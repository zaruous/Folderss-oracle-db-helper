using System;
using System.Collections.Generic;
using System.Linq;
using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    /// <summary>전체 DB 상태(OverviewLogic): 긴 형식 지표 읽기, 기본 임계값 판정·이유 줄, 타일 글, 순서, 고정·숨김 설정, 미니 추이.</summary>
    public class OverviewLogicTests
    {
        private static QueryTable Table(params string[][] rows)
        {
            var table = new QueryTable();
            table.Columns.AddRange(new[] { "METRIC", "ITEM", "VALUE", "TEXT" });
            foreach (var row in rows)
                table.Rows.Add(row);
            return table;
        }

        private static string[] M(string metric, string value, string item = null, string text = null)
        {
            return new[] { metric, item, value, text };
        }

        // 11.2 XE 실측 값(2026-10-19)과 같은 모양
        private static QueryTable Xe()
        {
            return Table(
                M("active_sessions", "1"), M("aas", "0"), M("cpu_count", "22"), M("blocked_sessions", "0"), M("longest_block_s", null),
                M("ts_used_pct", "61.7", "SYSTEM"), M("ts_used_pct", "2", "SYSAUX"), M("ts_used_pct", ".1", "MESPLUS_DATA_TS"),
                M("fra_used_pct", "1"), M("archive_dest_errors", "0"), M("last_backup_h", null), M("failed_jobs_24h", "0"),
                M("uptime_h", "117.8"), M("log_mode", null, null, "NOARCHIVELOG"), M("db_role", null, null, "PRIMARY"));
        }

        [Fact]
        public void Parse_ReadsLongFormat_CaseInsensitive_IgnoresUnknownAndBlank()
        {
            var table = new QueryTable();
            table.Columns.AddRange(new[] { "metric", "item", "value", "text" });
            table.Rows.Add(new[] { "ts_used_pct", "USERS", "12.5", null });
            table.Rows.Add(new[] { "made_up", null, "1", null });
            table.Rows.Add(new[] { null, null, "1", null });
            table.Rows.Add(new[] { "last_backup_h", null, null, null });
            var snapshot = OverviewLogic.Parse(table);
            Assert.Equal(12.5, snapshot.Number("ts_used_pct"));
            Assert.Equal("USERS", snapshot.Values("ts_used_pct")[0].Item);
            Assert.False(snapshot.Has("made_up"));
            Assert.True(snapshot.Has("last_backup_h"));
            Assert.Null(snapshot.Number("last_backup_h"));
            Assert.False(snapshot.Has("fra_used_pct"));
            Assert.Empty(snapshot.Values("fra_used_pct"));
            var bad = new QueryTable();
            bad.Columns.Add("NAME");
            Assert.Throws<InvalidOperationException>(() => OverviewLogic.Parse(bad));
            Assert.Throws<ArgumentNullException>(() => OverviewLogic.Parse(null));
        }

        [Fact]
        public void Xe_OnlyNoBackupWarning_AndTileTexts()
        {
            var snapshot = OverviewLogic.Parse(Xe());
            var findings = OverviewLogic.Judge(snapshot);
            var only = Assert.Single(findings);
            Assert.Equal(MonitorMetrics.LastBackupHours, only.Metric);
            Assert.Equal(OverviewLevel.Warning, only.Level);
            Assert.Equal(OverviewTileState.Warning, OverviewLogic.StateOf(findings));
            Assert.Equal("백업 기록 없음", OverviewLogic.ReasonText(findings));
            Assert.Equal("활성 0.0 / CPU 22", OverviewLogic.ActivityText(snapshot));
            Assert.Equal("공간 61.7% SYSTEM · FRA 1%", OverviewLogic.SpaceText(snapshot));
            Assert.Equal("백업 기록 없음 · NOARCHIVELOG", OverviewLogic.BackupText(snapshot));
            Assert.Equal(0, OverviewLogic.TrendValue(snapshot));
        }

        [Fact]
        public void Judge_LevelsAndOrder_CriticalFirst_ThenEvaluationOrder()
        {
            var snapshot = OverviewLogic.Parse(Table(
                M("blocked_sessions", "2"), M("longest_block_s", "400"),
                M("ts_used_pct", "88", "MES_DATA"), M("ts_used_pct", "70", "USERS"),
                M("fra_used_pct", "92"), M("archive_dest_errors", "1"), M("last_backup_h", "30"),
                M("aas", "25"), M("cpu_count", "22"), M("failed_jobs_24h", "2")));
            var findings = OverviewLogic.Judge(snapshot);
            Assert.Equal(new[]
            {
                "잠금 대기 2 (최장 6분 40초)", "아카이브 대상 오류 1", "FRA 92%",
                "공간 MES_DATA 88% (최대 기준)", "마지막 백업 30시간 전", "활성 25.0 / CPU 22", "실패한 예약 작업 2 (24시간)"
            }, findings.Select(f => f.Text));
            Assert.Equal(new[] { OverviewLevel.Critical, OverviewLevel.Critical, OverviewLevel.Critical,
                OverviewLevel.Warning, OverviewLevel.Warning, OverviewLevel.Warning, OverviewLevel.Warning }, findings.Select(f => f.Level));
            Assert.Equal(OverviewTileState.Critical, OverviewLogic.StateOf(findings));
            Assert.Equal("잠금 대기 2 (최장 6분 40초)", OverviewLogic.ReasonText(findings));
        }

        [Fact]
        public void Judge_Boundaries()
        {
            OverviewLevel? LevelOf(params string[][] rows)
            {
                var found = OverviewLogic.Judge(OverviewLogic.Parse(Table(rows)));
                return found.Count == 0 ? (OverviewLevel?)null : found[0].Level;
            }
            // 잠금: 막힌 세션이 없으면 최장 대기 값이 있어도 판정하지 않는다(3초 미만 대기)
            Assert.Null(LevelOf(M("blocked_sessions", "0"), M("longest_block_s", "2")));
            Assert.Equal(OverviewLevel.Warning, LevelOf(M("blocked_sessions", "1"), M("longest_block_s", "5")));
            Assert.Equal(OverviewLevel.Critical, LevelOf(M("blocked_sessions", "5")));
            Assert.Equal(OverviewLevel.Critical, LevelOf(M("blocked_sessions", "1"), M("longest_block_s", "300")));
            Assert.Null(LevelOf(M("ts_used_pct", "84.9", "A")));
            Assert.Equal(OverviewLevel.Warning, LevelOf(M("ts_used_pct", "85", "A")));
            Assert.Equal(OverviewLevel.Critical, LevelOf(M("ts_used_pct", "95", "A")));
            Assert.Null(LevelOf(M("fra_used_pct", "79")));
            Assert.Null(LevelOf(M("archive_dest_errors", "0")));
            Assert.Null(LevelOf(M("last_backup_h", "25.9")));
            Assert.Equal(OverviewLevel.Critical, LevelOf(M("last_backup_h", "50")));
            // 백업 지표 행 자체가 없으면(DBMS가 주지 않음) 판정하지 않는다
            Assert.Null(LevelOf(M("fra_used_pct", "1")));
            // 평균 활성 세션은 CPU 수 대비 — CPU 수가 없으면 판정하지 않는다
            Assert.Null(LevelOf(M("aas", "50")));
            Assert.Null(LevelOf(M("aas", "21.9"), M("cpu_count", "22")));
            Assert.Equal(OverviewLevel.Critical, LevelOf(M("aas", "44"), M("cpu_count", "22")));
            // 예약 작업은 주의까지만
            Assert.Equal(OverviewLevel.Warning, LevelOf(M("failed_jobs_24h", "100")));
        }

        [Fact]
        public void Judge_CustomThresholds()
        {
            var limits = new Dictionary<string, OverviewThreshold>(OverviewLogic.DefaultThresholds.ToDictionary(p => p.Key, p => p.Value))
            {
                [MonitorMetrics.TablespaceUsedPercent] = new OverviewThreshold(70, 80)
            };
            var findings = OverviewLogic.Judge(OverviewLogic.Parse(Table(M("ts_used_pct", "75", "A"))), limits);
            Assert.Equal(OverviewLevel.Warning, Assert.Single(findings).Level);
        }

        [Fact]
        public void Texts_ActivityFallback_Backup_Role_Ago_Updated()
        {
            var noAas = OverviewLogic.Parse(Table(M("active_sessions", "4")));
            Assert.Equal("활성 세션 4", OverviewLogic.ActivityText(noAas));
            Assert.Equal(4, OverviewLogic.TrendValue(noAas));
            Assert.Equal("활성 -", OverviewLogic.ActivityText(OverviewLogic.Parse(Table())));
            Assert.Equal("공간 -", OverviewLogic.SpaceText(OverviewLogic.Parse(Table())));
            Assert.Equal("", OverviewLogic.BackupText(OverviewLogic.Parse(Table())));
            var standby = OverviewLogic.Parse(Table(M("last_backup_h", "9.4"), M("log_mode", null, null, "ARCHIVELOG"), M("db_role", null, null, "PHYSICAL STANDBY")));
            Assert.Equal("백업 9시간 전 · ARCHIVELOG · PHYSICAL STANDBY", OverviewLogic.BackupText(standby));
            Assert.Equal("47시간 전", OverviewLogic.AgoText(47.4));
            Assert.Equal("2일 전", OverviewLogic.AgoText(60));
            Assert.Equal("0시간 전", OverviewLogic.AgoText(-1));
            var now = new DateTime(2026, 10, 19, 12, 0, 0);
            Assert.Equal("", OverviewLogic.UpdatedText(null, now));
            Assert.Equal("갱신 5초 전", OverviewLogic.UpdatedText(now.AddSeconds(-5.7), now));
            Assert.Equal("갱신 3분 전", OverviewLogic.UpdatedText(now.AddSeconds(-200), now));
        }

        [Fact]
        public void States_TextOrderSummary()
        {
            foreach (OverviewTileState state in Enum.GetValues(typeof(OverviewTileState)))
                Assert.False(string.IsNullOrWhiteSpace(OverviewLogic.StateText(state)));
            Assert.True(OverviewLogic.SeverityOrder(OverviewTileState.Critical) < OverviewLogic.SeverityOrder(OverviewTileState.ConnectFailed));
            Assert.Equal(OverviewLogic.SeverityOrder(OverviewTileState.ConnectFailed), OverviewLogic.SeverityOrder(OverviewTileState.QueryFailed));
            Assert.True(OverviewLogic.SeverityOrder(OverviewTileState.QueryFailed) < OverviewLogic.SeverityOrder(OverviewTileState.Warning));
            Assert.True(OverviewLogic.SeverityOrder(OverviewTileState.Ok) < OverviewLogic.SeverityOrder(OverviewTileState.NotSupported));
            var states = new[] { OverviewTileState.Ok, OverviewTileState.Warning, OverviewTileState.Critical, OverviewTileState.ConnectFailed,
                OverviewTileState.QueryFailed, OverviewTileState.NotSupported, OverviewTileState.Warning };
            Assert.Equal("7개 · 위험 1 · 실패 2 · 주의 2 · 숨김 3", OverviewLogic.SummaryText(states, 3));
            Assert.Equal("1개", OverviewLogic.SummaryText(new[] { OverviewTileState.Ok }, 0));
            Assert.Equal("끔", OverviewLogic.RefreshText(0));
            Assert.Equal("30초", OverviewLogic.RefreshText(30));
            Assert.Equal("5분", OverviewLogic.RefreshText(300));
            Assert.Contains(OverviewLogic.DefaultRefreshSeconds, OverviewLogic.RefreshSeconds);
        }

        [Fact]
        public void Order_PinnedFirst_ThenSeverityOrName()
        {
            var tiles = new[]
            {
                (Name: "가", State: OverviewTileState.Ok, Pinned: false),
                (Name: "나", State: OverviewTileState.Critical, Pinned: false),
                (Name: "다", State: OverviewTileState.Warning, Pinned: true),
                (Name: "라", State: OverviewTileState.NotSupported, Pinned: false),
                (Name: "마", State: OverviewTileState.ConnectFailed, Pinned: false)
            };
            Assert.Equal(new[] { "다", "나", "마", "가", "라" }, OverviewLogic.Order(tiles, t => t.State, t => t.Name, t => t.Pinned, true).Select(t => t.Name));
            Assert.Equal(new[] { "다", "가", "나", "라", "마" }, OverviewLogic.Order(tiles, t => t.State, t => t.Name, t => t.Pinned, false).Select(t => t.Name));
        }

        [Fact]
        public void Ids_RoundTrip_SortedDistinct()
        {
            Assert.Empty(OverviewLogic.ParseIds(null));
            var ids = OverviewLogic.ParseIds("b\r\n a \n\nb\n");
            Assert.Equal(new[] { "a", "b" }, ids.OrderBy(s => s));
            Assert.Equal("a\nb", OverviewLogic.SerializeIds(new[] { "b", "a", "b", " " }));
            Assert.Equal("", OverviewLogic.SerializeIds(null));
        }

        [Fact]
        public void SparkPoints_ScaleToBox_ZeroAtBottom()
        {
            Assert.Empty(OverviewLogic.SparkPoints(new double[0], 90, 18));
            var single = OverviewLogic.SparkPoints(new[] { 0.5 }, 90, 18);
            Assert.Equal(new[] { 0.0, 90.0 }, single.Select(p => p.Key));
            Assert.All(single, p => Assert.Equal(9, p.Value, 6));
            var points = OverviewLogic.SparkPoints(new[] { 0.0, 2.0, 4.0 }, 90, 18);
            Assert.Equal(new[] { 0.0, 45.0, 90.0 }, points.Select(p => p.Key));
            Assert.Equal(new[] { 18.0, 9.0, 0.0 }, points.Select(p => p.Value));
            // 값이 모두 1보다 작으면 위 끝은 1(작은 변동이 꽉 차 보이지 않게)
            Assert.Equal(18 - 0.5 * 18, OverviewLogic.SparkPoints(new[] { 0.0, 0.5 }, 10, 18)[1].Value, 6);
        }
    }
}

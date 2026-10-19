using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MyPlugin
{
    /// <summary>지표 하나의 판정 단계.</summary>
    internal enum OverviewLevel
    {
        Ok = 0,
        Warning = 1,
        Critical = 2
    }

    /// <summary>타일 하나의 상태(지표 판정 + 접속·조회 결과).</summary>
    internal enum OverviewTileState
    {
        Loading,
        Ok,
        Warning,
        Critical,
        ConnectFailed,
        QueryFailed,
        PermissionDenied,
        NeedsPassword,
        NotSupported
    }

    /// <summary>지표 값 하나(테이블스페이스처럼 같은 지표가 여러 행이면 행마다 하나).</summary>
    internal sealed class OverviewValue
    {
        public string Item { get; set; }
        public double? Value { get; set; }
        public string Text { get; set; }
    }

    /// <summary>한 번 가져온 지표(지표 이름 → 값들, 결과 순서).</summary>
    internal sealed class OverviewSnapshot
    {
        private readonly Dictionary<string, List<OverviewValue>> _values = new Dictionary<string, List<OverviewValue>>(StringComparer.Ordinal);

        public void Add(string metric, OverviewValue value)
        {
            List<OverviewValue> list;
            if (!_values.TryGetValue(metric, out list))
            {
                list = new List<OverviewValue>();
                _values[metric] = list;
            }
            list.Add(value);
        }

        /// <summary>그 지표의 행이 있었는지(값이 NULL이어도 행이 있으면 true — "백업 기록 없음"처럼 NULL이 뜻을 가진다).</summary>
        public bool Has(string metric)
        {
            return _values.ContainsKey(metric);
        }

        public IReadOnlyList<OverviewValue> Values(string metric)
        {
            List<OverviewValue> list;
            return _values.TryGetValue(metric, out list) ? list : (IReadOnlyList<OverviewValue>)Array.Empty<OverviewValue>();
        }

        /// <summary>첫 행의 수(없거나 NULL이면 null).</summary>
        public double? Number(string metric)
        {
            var list = Values(metric);
            return list.Count == 0 ? null : list[0].Value;
        }

        /// <summary>첫 행의 글(없으면 null).</summary>
        public string Text(string metric)
        {
            var list = Values(metric);
            return list.Count == 0 ? null : list[0].Text;
        }
    }

    /// <summary>주의·위험 판정 하나("잠금 대기 3 (최장 4분 12초)").</summary>
    internal sealed class OverviewFinding
    {
        public OverviewFinding(string metric, OverviewLevel level, string text)
        {
            Metric = metric;
            Level = level;
            Text = text;
        }

        public string Metric { get; }
        public OverviewLevel Level { get; }
        public string Text { get; }
    }

    /// <summary>지표의 주의·위험 문턱(값 ≥ 문턱이면 그 단계). 위험이 없으면 null.</summary>
    internal sealed class OverviewThreshold
    {
        public OverviewThreshold(double warning, double? critical)
        {
            Warning = warning;
            Critical = critical;
        }

        public double Warning { get; }
        public double? Critical { get; }

        public OverviewLevel LevelOf(double value)
        {
            if (Critical.HasValue && value >= Critical.Value)
                return OverviewLevel.Critical;
            return value >= Warning ? OverviewLevel.Warning : OverviewLevel.Ok;
        }
    }

    /// <summary>
    /// 전체 DB 한 화면(순수 로직 — DBMS를 모른다). 공급자 지표(긴 형식)를 읽고, 기본 임계값으로 판정해 타일의 상태·이유 줄·표시 글을 만든다.
    /// 판정 순서(같은 단계면 이 순서로 이유 줄에 먼저): 잠금 → 아카이브 → 공간 → FRA → 백업 → 활성 세션 → 예약 작업.
    /// 임계값을 접속별로 덮어쓰는 것은 일일 점검(8-3)의 SQLite 저장소와 함께 한다 — 지금은 기본값.
    /// </summary>
    internal static class OverviewLogic
    {
        public const string Title = "전체 DB 상태";
        public const string PinnedSettingKey = "monitor.overview.pinned";
        public const string HiddenSettingKey = "monitor.overview.hidden";

        /// <summary>동시에 조회하는 DB 수와 DB 하나의 시간 제한(초).</summary>
        public const int MaxConcurrent = 4;
        public const int TimeoutSeconds = 10;

        /// <summary>미니 추이에 남기는 점 수(창이 열린 동안).</summary>
        public const int TrendPoints = 30;

        /// <summary>자동 새로 고침(초). 0 = 끔. 기본 60초.</summary>
        public static readonly IReadOnlyList<int> RefreshSeconds = new[] { 0, 30, 60, 300 };
        public const int DefaultRefreshSeconds = 60;

        /// <summary>기본 임계값(설계 5장 표). 백업은 시간, 공간·FRA는 %, 잠금은 세션 수·초.</summary>
        public static readonly IReadOnlyDictionary<string, OverviewThreshold> DefaultThresholds = new Dictionary<string, OverviewThreshold>(StringComparer.Ordinal)
        {
            { MonitorMetrics.BlockedSessions, new OverviewThreshold(1, 5) },
            { MonitorMetrics.LongestBlockSeconds, new OverviewThreshold(60, 300) },
            { MonitorMetrics.TablespaceUsedPercent, new OverviewThreshold(85, 95) },
            { MonitorMetrics.RecoveryAreaUsedPercent, new OverviewThreshold(80, 90) },
            { MonitorMetrics.ArchiveDestErrors, new OverviewThreshold(1, 1) },
            { MonitorMetrics.LastBackupHours, new OverviewThreshold(26, 50) },
            { MonitorMetrics.FailedJobs24h, new OverviewThreshold(1, null) },
            // 평균 활성 세션 ÷ CPU 수(1배 이상이면 CPU·자원을 기다리는 세션이 있다)
            { MonitorMetrics.AverageActiveSessions, new OverviewThreshold(1, 2) }
        };

        // ---------- 읽기 ----------

        /// <summary>긴 형식 결과 → 지표. 열 이름은 대소문자를 가리지 않고(PostgreSQL은 소문자), 목록 밖 지표·이름 없는 행은 버린다.</summary>
        public static OverviewSnapshot Parse(QueryTable table)
        {
            if (table == null)
                throw new ArgumentNullException(nameof(table));
            var metric = IndexOf(table.Columns, MonitorOverview.Metric);
            if (metric < 0)
                throw new InvalidOperationException("전체 DB 지표 조회 결과에 " + MonitorOverview.Metric + " 열이 없습니다.");
            var item = IndexOf(table.Columns, MonitorOverview.Item);
            var value = IndexOf(table.Columns, MonitorOverview.Value);
            var text = IndexOf(table.Columns, MonitorOverview.Text);
            var snapshot = new OverviewSnapshot();
            foreach (var row in table.Rows)
            {
                var name = (Cell(row, metric) ?? "").Trim();
                if (!MonitorMetrics.All.Contains(name))
                    continue;
                snapshot.Add(name, new OverviewValue { Item = Cell(row, item), Value = Number(Cell(row, value)), Text = Cell(row, text) });
            }
            return snapshot;
        }

        // ---------- 판정 ----------

        /// <summary>주의·위험만, 단계가 높은 것부터(같으면 판정 순서). 정상이면 빈 목록.</summary>
        public static List<OverviewFinding> Judge(OverviewSnapshot snapshot, IReadOnlyDictionary<string, OverviewThreshold> thresholds = null)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));
            var limits = thresholds ?? DefaultThresholds;
            var findings = new List<OverviewFinding>();

            var blocked = snapshot.Number(MonitorMetrics.BlockedSessions);
            if (blocked.HasValue && blocked.Value > 0)
            {
                var longest = snapshot.Number(MonitorMetrics.LongestBlockSeconds);
                var level = Max(Level(limits, MonitorMetrics.BlockedSessions, blocked.Value),
                    longest.HasValue ? Level(limits, MonitorMetrics.LongestBlockSeconds, longest.Value) : OverviewLevel.Ok);
                if (level > OverviewLevel.Ok)
                    findings.Add(new OverviewFinding(MonitorMetrics.BlockedSessions, level,
                        "잠금 대기 " + Count(blocked.Value) + (longest.HasValue ? " (최장 " + LockTreeLogic.DurationText(longest.Value) + ")" : "")));
            }

            var archive = snapshot.Number(MonitorMetrics.ArchiveDestErrors);
            if (archive.HasValue && Level(limits, MonitorMetrics.ArchiveDestErrors, archive.Value) > OverviewLevel.Ok)
                findings.Add(new OverviewFinding(MonitorMetrics.ArchiveDestErrors, Level(limits, MonitorMetrics.ArchiveDestErrors, archive.Value),
                    "아카이브 대상 오류 " + Count(archive.Value)));

            var worstSpace = snapshot.Values(MonitorMetrics.TablespaceUsedPercent).Where(v => v.Value.HasValue).OrderByDescending(v => v.Value.Value).FirstOrDefault();
            if (worstSpace != null && Level(limits, MonitorMetrics.TablespaceUsedPercent, worstSpace.Value.Value) > OverviewLevel.Ok)
                findings.Add(new OverviewFinding(MonitorMetrics.TablespaceUsedPercent, Level(limits, MonitorMetrics.TablespaceUsedPercent, worstSpace.Value.Value),
                    "공간 " + (worstSpace.Item ?? "") + " " + Percent(worstSpace.Value.Value) + " (최대 기준)"));

            var fra = snapshot.Number(MonitorMetrics.RecoveryAreaUsedPercent);
            if (fra.HasValue && Level(limits, MonitorMetrics.RecoveryAreaUsedPercent, fra.Value) > OverviewLevel.Ok)
                findings.Add(new OverviewFinding(MonitorMetrics.RecoveryAreaUsedPercent, Level(limits, MonitorMetrics.RecoveryAreaUsedPercent, fra.Value), "FRA " + Percent(fra.Value)));

            if (snapshot.Has(MonitorMetrics.LastBackupHours))
            {
                var hours = snapshot.Number(MonitorMetrics.LastBackupHours);
                if (!hours.HasValue)
                    findings.Add(new OverviewFinding(MonitorMetrics.LastBackupHours, OverviewLevel.Warning, NoBackupText));
                else if (Level(limits, MonitorMetrics.LastBackupHours, hours.Value) > OverviewLevel.Ok)
                    findings.Add(new OverviewFinding(MonitorMetrics.LastBackupHours, Level(limits, MonitorMetrics.LastBackupHours, hours.Value), "마지막 백업 " + AgoText(hours.Value)));
            }

            var aas = snapshot.Number(MonitorMetrics.AverageActiveSessions);
            var cpu = snapshot.Number(MonitorMetrics.CpuCount);
            if (aas.HasValue && cpu.HasValue && cpu.Value > 0 && Level(limits, MonitorMetrics.AverageActiveSessions, aas.Value / cpu.Value) > OverviewLevel.Ok)
                findings.Add(new OverviewFinding(MonitorMetrics.AverageActiveSessions, Level(limits, MonitorMetrics.AverageActiveSessions, aas.Value / cpu.Value),
                    "활성 " + Decimal(aas.Value) + " / CPU " + Count(cpu.Value)));

            var jobs = snapshot.Number(MonitorMetrics.FailedJobs24h);
            if (jobs.HasValue && Level(limits, MonitorMetrics.FailedJobs24h, jobs.Value) > OverviewLevel.Ok)
                findings.Add(new OverviewFinding(MonitorMetrics.FailedJobs24h, Level(limits, MonitorMetrics.FailedJobs24h, jobs.Value), "실패한 예약 작업 " + Count(jobs.Value) + " (24시간)"));

            // 단계 내림차순, 같으면 위에서 더한 순서(판정 순서) — List.Sort는 안정 정렬이 아니므로 순서 번호로
            return findings.Select((f, i) => new { f, i }).OrderByDescending(x => x.f.Level).ThenBy(x => x.i).Select(x => x.f).ToList();
        }

        public static OverviewTileState StateOf(IReadOnlyList<OverviewFinding> findings)
        {
            var worst = findings == null || findings.Count == 0 ? OverviewLevel.Ok : findings.Max(f => f.Level);
            return worst == OverviewLevel.Critical ? OverviewTileState.Critical : worst == OverviewLevel.Warning ? OverviewTileState.Warning : OverviewTileState.Ok;
        }

        // ---------- 글 ----------

        public const string NoBackupText = "백업 기록 없음";
        public const string OkText = "정상";
        public const string NeedsPasswordText = "저장된 비밀번호가 없습니다 — 눌러서 입력하면 이 창이 열린 동안 연결을 유지합니다.";
        public const string NotSupportedText = "아직 Oracle만 지원합니다(PostgreSQL·SQL Server는 다음 단계).";
        public const string TimeoutText = "시간 초과";

        /// <summary>이유 줄: 가장 나쁜 판정 하나, 없으면 "정상".</summary>
        public static string ReasonText(IReadOnlyList<OverviewFinding> findings)
        {
            return findings == null || findings.Count == 0 ? OkText : findings[0].Text;
        }

        /// <summary>활성 줄: "활성 6.3 / CPU 22" — 평균 활성 세션이 없으면 지금 활성 세션 수 "활성 세션 4".</summary>
        public static string ActivityText(OverviewSnapshot snapshot)
        {
            var aas = snapshot.Number(MonitorMetrics.AverageActiveSessions);
            var cpu = snapshot.Number(MonitorMetrics.CpuCount);
            if (aas.HasValue)
                return "활성 " + Decimal(aas.Value) + (cpu.HasValue ? " / CPU " + Count(cpu.Value) : "");
            var active = snapshot.Number(MonitorMetrics.ActiveSessions);
            return active.HasValue ? "활성 세션 " + Count(active.Value) : "활성 -";
        }

        /// <summary>미니 추이에 쓸 값: 평균 활성 세션, 없으면 지금 활성 세션 수.</summary>
        public static double? TrendValue(OverviewSnapshot snapshot)
        {
            return snapshot.Number(MonitorMetrics.AverageActiveSessions) ?? snapshot.Number(MonitorMetrics.ActiveSessions);
        }

        /// <summary>공간 줄: "공간 61.7% SYSTEM · FRA 1%" (공간은 최대 기준 — 머리 도움말에 적는다).</summary>
        public static string SpaceText(OverviewSnapshot snapshot)
        {
            var worst = snapshot.Values(MonitorMetrics.TablespaceUsedPercent).Where(v => v.Value.HasValue).OrderByDescending(v => v.Value.Value).FirstOrDefault();
            var text = worst == null ? "공간 -" : "공간 " + Percent(worst.Value.Value) + " " + (worst.Item ?? "");
            var fra = snapshot.Number(MonitorMetrics.RecoveryAreaUsedPercent);
            return text.TrimEnd() + (fra.HasValue ? " · FRA " + Percent(fra.Value) : "");
        }

        /// <summary>백업 줄: "백업 9시간 전 · NOARCHIVELOG · STANDBY"(역할은 PRIMARY가 아닐 때만).</summary>
        public static string BackupText(OverviewSnapshot snapshot)
        {
            var parts = new List<string>();
            if (snapshot.Has(MonitorMetrics.LastBackupHours))
            {
                var hours = snapshot.Number(MonitorMetrics.LastBackupHours);
                parts.Add(hours.HasValue ? "백업 " + AgoText(hours.Value) : NoBackupText);
            }
            var logMode = snapshot.Text(MonitorMetrics.LogMode);
            if (!string.IsNullOrEmpty(logMode))
                parts.Add(logMode);
            var role = snapshot.Text(MonitorMetrics.DbRole);
            if (!string.IsNullOrEmpty(role) && !string.Equals(role, "PRIMARY", StringComparison.OrdinalIgnoreCase))
                parts.Add(role);
            return parts.Count == 0 ? "" : string.Join(" · ", parts);
        }

        /// <summary>"9시간 전" · "3일 전"(48시간부터 일).</summary>
        public static string AgoText(double hours)
        {
            var h = Math.Max(0, hours);
            return h < 48 ? Math.Round(h).ToString("0", Inv) + "시간 전" : Math.Floor(h / 24).ToString("0", Inv) + "일 전";
        }

        /// <summary>갱신 글: "갱신 5초 전" · "갱신 3분 전". 아직이면 "".</summary>
        public static string UpdatedText(DateTime? updatedAt, DateTime now)
        {
            if (!updatedAt.HasValue)
                return "";
            var seconds = Math.Max(0, (now - updatedAt.Value).TotalSeconds);
            return seconds < 60 ? "갱신 " + Math.Floor(seconds).ToString("0", Inv) + "초 전" : "갱신 " + Math.Floor(seconds / 60).ToString("0", Inv) + "분 전";
        }

        public static string StateText(OverviewTileState state)
        {
            switch (state)
            {
                case OverviewTileState.Loading: return "가져오는 중";
                case OverviewTileState.Ok: return "정상";
                case OverviewTileState.Warning: return "주의";
                case OverviewTileState.Critical: return "위험";
                case OverviewTileState.ConnectFailed: return "접속 실패";
                case OverviewTileState.QueryFailed: return "조회 실패";
                case OverviewTileState.PermissionDenied: return "권한 필요";
                case OverviewTileState.NeedsPassword: return "비밀번호 필요";
                default: return "지원 예정";
            }
        }

        /// <summary>심각도 순서(작을수록 앞): 위험 → 접속·조회 실패 → 주의 → 권한 필요 → 비밀번호 필요 → 정상 → 가져오는 중 → 지원 예정.</summary>
        public static int SeverityOrder(OverviewTileState state)
        {
            switch (state)
            {
                case OverviewTileState.Critical: return 0;
                case OverviewTileState.ConnectFailed:
                case OverviewTileState.QueryFailed: return 1;
                case OverviewTileState.Warning: return 2;
                case OverviewTileState.PermissionDenied: return 3;
                case OverviewTileState.NeedsPassword: return 4;
                case OverviewTileState.Ok: return 5;
                case OverviewTileState.Loading: return 6;
                default: return 7;
            }
        }

        /// <summary>타일 순서: 고정한 것이 먼저, 그다음 심각도(bySeverity) 또는 이름, 같으면 이름.</summary>
        public static List<T> Order<T>(IEnumerable<T> tiles, Func<T, OverviewTileState> state, Func<T, string> name, Func<T, bool> pinned, bool bySeverity)
        {
            return tiles.OrderBy(t => pinned(t) ? 0 : 1)
                .ThenBy(t => bySeverity ? SeverityOrder(state(t)) : 0)
                .ThenBy(t => name(t) ?? "", StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>머리 요약: "12개 · 위험 1 · 접속 실패 1 · 주의 2"(0인 상태는 빼고, 정상·지원 예정은 세지 않는다).</summary>
        public static string SummaryText(IEnumerable<OverviewTileState> states, int hidden)
        {
            var list = states.ToList();
            var text = list.Count.ToString(Inv) + "개";
            foreach (var group in list.Where(s => s != OverviewTileState.Ok && s != OverviewTileState.NotSupported && s != OverviewTileState.Loading)
                .GroupBy(s => s == OverviewTileState.QueryFailed ? OverviewTileState.ConnectFailed : s).OrderBy(g => SeverityOrder(g.Key)))
                text += " · " + (group.Key == OverviewTileState.ConnectFailed ? "실패" : StateText(group.Key)) + " " + group.Count().ToString(Inv);
            if (hidden > 0)
                text += " · 숨김 " + hidden.ToString(Inv);
            return text;
        }

        public static string RefreshText(int seconds)
        {
            if (seconds <= 0)
                return "끔";
            return seconds < 60 ? seconds.ToString(Inv) + "초" : (seconds / 60).ToString(Inv) + "분";
        }

        // ---------- 설정(고정·숨김) ----------

        /// <summary>접속 Id 목록 설정 값: 줄바꿈으로 잇는다(Id는 GUID 글이라 줄바꿈이 없다).</summary>
        public static HashSet<string> ParseIds(string value)
        {
            return new HashSet<string>((value ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0), StringComparer.Ordinal);
        }

        public static string SerializeIds(IEnumerable<string> ids)
        {
            return string.Join("\n", (ids ?? Enumerable.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal));
        }

        // ---------- 미니 추이 ----------

        /// <summary>점들을 width×height 상자의 좌표로(왼쪽이 오래된 값, 아래가 0, 위가 max(최댓값, 1)). 점이 하나면 가운데 짧은 선.</summary>
        public static List<KeyValuePair<double, double>> SparkPoints(IReadOnlyList<double> values, double width, double height)
        {
            var points = new List<KeyValuePair<double, double>>();
            if (values == null || values.Count == 0 || width <= 0 || height <= 0)
                return points;
            var top = Math.Max(1, values.Max());
            if (values.Count == 1)
            {
                var y = height - values[0] / top * height;
                points.Add(new KeyValuePair<double, double>(0, y));
                points.Add(new KeyValuePair<double, double>(width, y));
                return points;
            }
            for (var i = 0; i < values.Count; i++)
                points.Add(new KeyValuePair<double, double>(width * i / (values.Count - 1), height - Math.Max(0, values[i]) / top * height));
            return points;
        }

        // ---------- 도움 ----------

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static OverviewLevel Level(IReadOnlyDictionary<string, OverviewThreshold> limits, string metric, double value)
        {
            OverviewThreshold threshold;
            return limits.TryGetValue(metric, out threshold) ? threshold.LevelOf(value) : OverviewLevel.Ok;
        }

        private static OverviewLevel Max(OverviewLevel a, OverviewLevel b)
        {
            return a > b ? a : b;
        }

        private static string Count(double value)
        {
            return Math.Round(value).ToString("0", Inv);
        }

        private static string Decimal(double value)
        {
            return value.ToString("0.0", Inv);
        }

        private static string Percent(double value)
        {
            return value.ToString("0.#", Inv) + "%";
        }

        private static int IndexOf(IReadOnlyList<string> columns, string name)
        {
            for (var i = 0; i < columns.Count; i++)
                if (string.Equals(columns[i], name, StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }

        private static string Cell(string[] row, int index)
        {
            if (index < 0 || index >= row.Length)
                return null;
            return string.IsNullOrWhiteSpace(row[index]) ? null : row[index];
        }

        private static double? Number(string text)
        {
            double value;
            return text != null && double.TryParse(text, NumberStyles.Float, Inv, out value) ? value : (double?)null;
        }
    }
}

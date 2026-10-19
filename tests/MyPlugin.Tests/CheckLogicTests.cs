using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    /// <summary>
    /// 일일 점검(순수 로직): 규칙 판정, 직전 대비, 기본 + 사용자 항목 합치기와 덮어쓰기, 사용자 항목 검사, 규칙 JSON, 내보내기·가져오기, 설정, 글.
    /// Oracle 기본 묶음 SQL의 모양(바인드·DML 없음, 별칭 30바이트, 규칙이 가리키는 열이 SQL에 있음)도 여기서 본다.
    /// </summary>
    public class CheckLogicTests
    {
        private static QueryTable Table(string[] columns, params string[][] rows)
        {
            var table = new QueryTable();
            table.Columns.AddRange(columns);
            table.Rows.AddRange(rows);
            return table;
        }

        private static MonitorCheck Check(CheckRule rule, string id = "t.check", string advice = "조치")
        {
            return new MonitorCheck(id, "공간", "시험 항목", "SELECT 1 FROM DUAL", rule, "SELECT_CATALOG_ROLE", advice);
        }

        // ---------- 판정 ----------

        [Fact]
        public void Evaluate_AnyRow_LevelWhenRows_OkWhenNone()
        {
            var check = Check(CheckRule.AnyRow(CheckLevel.Critical));
            var found = CheckLogic.Evaluate(check, Table(new[] { "OWNER", "NAME" }, new[] { "SCOTT", "P_X" }, new[] { "HR", "V_Y" }));
            Assert.Equal(CheckStatus.Critical, found.Status);
            Assert.Equal(2, found.Value);
            Assert.Equal("2건", found.ValueText);
            Assert.Equal(2, found.RowCount);
            Assert.Equal(new[] { "OWNER", "NAME" }, found.Columns);

            var none = CheckLogic.Evaluate(check, Table(new[] { "OWNER" }));
            Assert.Equal(CheckStatus.Ok, none.Status);
            Assert.Equal("없음", none.ValueText);
            // AnyRow(Ok)은 주의로 본다(행이 있는데 정상인 규칙은 뜻이 없음)
            Assert.Equal(CheckLevel.Warning, CheckRule.AnyRow(CheckLevel.Ok).Level);
        }

        [Fact]
        public void Evaluate_Threshold_MaxOfColumn_BoundariesInclusive_LabelFromFirstColumn()
        {
            var check = Check(CheckRule.Threshold("사용률(%)", 85, 95));
            Func<string, CheckOutcome> one = v => CheckLogic.Evaluate(check, Table(new[] { "테이블스페이스", "사용률(%)" }, new[] { "USERS", "10" }, new[] { "MES", v }));
            Assert.Equal(CheckStatus.Ok, one("84.9").Status);
            Assert.Equal(CheckStatus.Warning, one("85").Status);
            Assert.Equal(CheckStatus.Warning, one("94.99").Status);
            Assert.Equal(CheckStatus.Critical, one("95").Status);
            var worst = one("96.5");
            Assert.Equal(96.5, worst.Value);
            Assert.Equal("96.5 — MES", worst.ValueText);
            // 열 이름은 대소문자를 가리지 않고, 수가 아닌 칸·NULL은 건너뛴다
            var mixed = CheckLogic.Evaluate(Check(CheckRule.Threshold("value", 1, 2)), Table(new[] { "VALUE" }, new[] { "x" }, new string[] { null }, new[] { "1.5" }));
            Assert.Equal(CheckStatus.Warning, mixed.Status);
            Assert.Equal("1.5", mixed.ValueText);
        }

        [Fact]
        public void Evaluate_Threshold_NoValue_UsesMissingLevel()
        {
            // 마지막 백업: 기록이 없으면 MAX가 NULL 한 행 — 값 없음은 주의
            var backup = Check(CheckRule.Threshold("경과(시간)", 26, 50, CheckLevel.Warning));
            var none = CheckLogic.Evaluate(backup, Table(new[] { "마지막 성공", "경과(시간)" }, new[] { null, (string)null }));
            Assert.Equal(CheckStatus.Warning, none.Status);
            Assert.Equal("값 없음", none.ValueText);
            Assert.Null(none.Value);
            var empty = CheckLogic.Evaluate(Check(CheckRule.Threshold("V", 1, 2)), Table(new[] { "V" }));
            Assert.Equal(CheckStatus.Ok, empty.Status);
        }

        [Fact]
        public void Evaluate_ByLevel_CountsWarnAndCrit()
        {
            var check = Check(CheckRule.ByLevel("판정"));
            var table = Table(new[] { "이름", "판정" }, new[] { "a", "OK" }, new[] { "b", "warn" }, new[] { "c", "CRIT" }, new[] { "d", "CRITICAL" }, new[] { "e", " " });
            var outcome = CheckLogic.Evaluate(check, table);
            Assert.Equal(CheckStatus.Critical, outcome.Status);
            Assert.Equal(3, outcome.Value);
            Assert.Equal("위험 2 · 주의 1", outcome.ValueText);
            Assert.Equal(CheckStatus.Ok, CheckLogic.Evaluate(check, Table(new[] { "판정" }, new[] { "OK" })).Status);
            Assert.Equal("정상", CheckLogic.Evaluate(check, Table(new[] { "판정" }, new[] { "OK" })).ValueText);
            Assert.Equal("없음", CheckLogic.Evaluate(check, Table(new[] { "판정" })).ValueText);
            Assert.Equal(CheckStatus.Warning, CheckLogic.Evaluate(check, Table(new[] { "판정" }, new[] { "WARN" })).Status);
        }

        [Fact]
        public void Evaluate_MissingRuleColumn_IsInvalidError()
        {
            var outcome = CheckLogic.Evaluate(Check(CheckRule.Threshold("PCT", 1, 2)), Table(new[] { "NAME", "USED" }, new[] { "a", "1" }));
            Assert.Equal(CheckStatus.Error, outcome.Status);
            Assert.Equal("invalid", outcome.Reason);
            Assert.Contains("'PCT'", outcome.Message);
            Assert.Contains("NAME, USED", outcome.Message);
        }

        [Fact]
        public void Evaluate_Info_FirstRowSummary_RowsCappedAtHundred()
        {
            var rows = Enumerable.Range(1, 150).Select(i => new[] { i.ToString(), "x" }).ToArray();
            var outcome = CheckLogic.Evaluate(Check(CheckRule.Info()), Table(new[] { "N", "T" }, rows));
            Assert.Equal(CheckStatus.Info, outcome.Status);
            Assert.Equal("N 1 · T x", outcome.ValueText);
            Assert.Equal(150, outcome.RowCount);
            Assert.Equal(CheckLogic.MaxShownRows, outcome.Rows.Count);
            Assert.Equal("1,000건 넘음", CheckLogic.CountText(CheckLogic.MaxRows));
        }

        // ---------- 직전 대비 ----------

        [Fact]
        public void Compare_ByHealthRank_SkipAndErrorNotCompared()
        {
            Func<CheckStatus, string, CheckTrend> trend = (now, before) =>
            {
                var outcome = new CheckOutcome { Check = Check(CheckRule.Info()), Status = now };
                CheckLogic.Compare(outcome, before == null ? null : new StoredResult { ItemId = "t.check", Status = before });
                return outcome.Trend;
            };
            Assert.Equal(CheckTrend.New, trend(CheckStatus.Ok, null));
            Assert.Equal(CheckTrend.Worse, trend(CheckStatus.Warning, "ok"));
            Assert.Equal(CheckTrend.Worse, trend(CheckStatus.Critical, "warn"));
            Assert.Equal(CheckTrend.Better, trend(CheckStatus.Ok, "crit"));
            Assert.Equal(CheckTrend.Same, trend(CheckStatus.Info, "ok"));
            Assert.Equal(CheckTrend.Same, trend(CheckStatus.Warning, "warn"));
            Assert.Equal(CheckTrend.None, trend(CheckStatus.Skipped, "ok"));
            Assert.Equal(CheckTrend.None, trend(CheckStatus.Ok, "error"));
            Assert.Equal("↑ 나빠짐", CheckLogic.TrendText(CheckTrend.Worse));
            Assert.Equal("", CheckLogic.TrendText(CheckTrend.None));
        }

        // ---------- 상태 글·코드 ----------

        [Fact]
        public void StatusCodes_RoundTrip_SeverityOrder_Summary()
        {
            foreach (CheckStatus status in Enum.GetValues(typeof(CheckStatus)))
                Assert.Equal(status, CheckLogic.ParseStatus(CheckLogic.StatusCode(status)));
            Assert.Equal(new[] { "ok", "info", "warn", "crit", "skip", "error" }, Enum.GetValues(typeof(CheckStatus)).Cast<CheckStatus>().Select(CheckLogic.StatusCode));
            Assert.Equal(CheckStatus.Error, CheckLogic.ParseStatus("뭔가"));
            var ordered = Enum.GetValues(typeof(CheckStatus)).Cast<CheckStatus>().OrderByDescending(CheckLogic.Severity).ToList();
            Assert.Equal(new[] { CheckStatus.Critical, CheckStatus.Error, CheckStatus.Warning, CheckStatus.Skipped, CheckStatus.Info, CheckStatus.Ok }, ordered);

            var outcomes = new[] { CheckStatus.Ok, CheckStatus.Info, CheckStatus.Warning, CheckStatus.Warning, CheckStatus.Critical, CheckStatus.Skipped }
                .Select(s => new CheckOutcome { Check = Check(CheckRule.Info()), Status = s }).ToList();
            Assert.Equal("위험 1 · 주의 2 · 건너뜀 1 · 정상 2", CheckLogic.SummaryText(outcomes));
            Assert.Equal(CheckStatus.Critical, CheckLogic.Worst(outcomes).Status);
            Assert.Null(CheckLogic.Worst(new CheckOutcome[0]));
            Assert.Equal("항목 없음", CheckLogic.SummaryText(new CheckOutcome[0]));
            Assert.Equal("DB 2개 · 접속 실패 1 · 위험 1 · 주의 2 · 건너뜀 1 · 정상 2", CheckLogic.RunSummaryText(2, 1, outcomes));
            Assert.Equal("DB 1개", CheckLogic.RunSummaryText(1, 0, new CheckOutcome[0]));
        }

        [Fact]
        public void Ordered_ByKnownCategory_ThenOthers_StableWithin()
        {
            var a = new MonitorCheck("a", "보안", "A", "SELECT 1", CheckRule.Info());
            var b = new MonitorCheck("b", "가용성", "B", "SELECT 1", CheckRule.Info());
            var c = new MonitorCheck("c", "사용자", "C", "SELECT 1", CheckRule.Info());
            var d = new MonitorCheck("d", "가용성", "D", "SELECT 1", CheckRule.Info());
            var ordered = CheckLogic.Ordered(new[] { a, b, c, d }.Select(x => new CheckOutcome { Check = x }));
            Assert.Equal(new[] { "b", "d", "a", "c" }, ordered.Select(o => o.Check.Id));
        }

        [Fact]
        public void ToStored_KeepsNoRows_SkippedHasNoRowCount()
        {
            var found = CheckLogic.Evaluate(Check(CheckRule.AnyRow(CheckLevel.Warning)), Table(new[] { "OWNER" }, new[] { "SCOTT" }));
            var stored = CheckLogic.ToStored(found);
            Assert.Equal("t.check", stored.ItemId);
            Assert.Equal("warn", stored.Status);
            Assert.Equal(1, stored.Value);
            Assert.Equal(1, stored.RowCount);
            Assert.Null(stored.Reason);
            // 저장 형식에는 발견 행(DB 내용)을 담을 곳이 없다
            Assert.Equal(new[] { "ItemId", "Reason", "RowCount", "Status", "Value" }, typeof(StoredResult).GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
            var skipped = CheckLogic.ToStored(CheckLogic.Skipped(found.Check, "privilege", "권한 없음 ORA-00942"));
            Assert.Equal("skip", skipped.Status);
            Assert.Null(skipped.RowCount);
            Assert.Equal("privilege", skipped.Reason);
        }

        // ---------- 기본 + 사용자, 덮어쓰기 ----------

        private static UserCheckItem User(string id, string kind = "oracle", string rule = "{\"type\":\"anyRow\",\"level\":\"crit\"}", string title = "내 항목")
        {
            return new UserCheckItem { Id = id, Kind = kind, Category = "사용자", Title = title, Sql = "SELECT 1 FROM DUAL", Rule = rule };
        }

        [Fact]
        public void Plan_AddsUserItemsOfKind_AppliesOverrides_ReportsBrokenItems()
        {
            var set = DbProviders.Oracle.Monitor.CheckSet;
            var overrides = new Dictionary<string, CheckOverride>
            {
                { "ora.log_mode", new CheckOverride { ItemId = "ora.log_mode", Enabled = false } },
                { "ora.tablespace", new CheckOverride { ItemId = "ora.tablespace", Warning = 70 } },
                { "user.b", new CheckOverride { ItemId = "user.b", Enabled = false } }
            };
            var plan = CheckLogic.Plan(set, "oracle", new[] { User("user.a"), User("user.b"), User("user.pg", "postgres"), User("user.bad", rule: "{\"type\":\"nope\"}"), User("user.blank", title: " ") }, overrides);
            Assert.Equal(set.Items.Count - 1 + 1, plan.Active.Count);
            Assert.Contains(plan.Active, c => c.Id == "user.a" && c.IsUser && c.Rule.Kind == CheckRuleKind.AnyRow && c.Rule.Level == CheckLevel.Critical);
            Assert.DoesNotContain(plan.Active, c => c.Id == "user.pg" || c.Id == "user.b" || c.Id == "ora.log_mode");
            Assert.Equal(new[] { "ora.log_mode", "user.b" }, plan.Disabled.Select(c => c.Id).OrderBy(x => x));
            var tablespace = plan.Active.Single(c => c.Id == "ora.tablespace");
            Assert.Equal(70, tablespace.Rule.Warning);
            Assert.Equal(95, tablespace.Rule.Critical);
            Assert.Equal(set.Items.Single(c => c.Id == "ora.tablespace").Sql, tablespace.Sql);
            Assert.Equal(new[] { "user.bad", "user.blank" }, plan.Invalid.Select(o => o.Check.Id));
            Assert.All(plan.Invalid, o => Assert.Equal(CheckStatus.Error, o.Status));
            Assert.Contains("nope", plan.Invalid[0].Message);
            // 덮어쓰기가 없으면 기본 묶음 그대로(같은 순서)
            Assert.Equal(set.Items.Select(c => c.Id), CheckLogic.Plan(set, "oracle", null, null).Active.Select(c => c.Id));
            // 임계값 덮어쓰기는 임계값 규칙에만
            Assert.Null(CheckRule.AnyRow(CheckLevel.Warning).WithThresholds(1, 2).Warning);
        }

        [Fact]
        public void Validate_SingleSelectOnly_RuleRequired()
        {
            Assert.Empty(CheckLogic.Validate(User("user.a")));
            Assert.Empty(CheckLogic.Validate(new UserCheckItem { Id = "user.w", Title = "t", Sql = "WITH x AS (SELECT 1 a FROM DUAL) SELECT * FROM x", Rule = "{\"type\":\"info\"}" }));
            Func<string, List<string>> sql = text => CheckLogic.Validate(new UserCheckItem { Id = "user.x", Title = "t", Sql = text, Rule = "{\"type\":\"info\"}" });
            Assert.Contains(sql("DELETE FROM T"), e => e.Contains("SELECT"));
            Assert.Contains(sql("SELECT * FROM T FOR UPDATE"), e => e.Contains("SELECT"));
            Assert.Contains(sql("SELECT 1 FROM DUAL; SELECT 2 FROM DUAL"), e => e.Contains("한 문장"));
            Assert.Contains(sql(" "), e => e.Contains("SQL을"));
            Assert.Contains(CheckLogic.Validate(new UserCheckItem { Id = "user.y", Title = "", Sql = "SELECT 1 FROM DUAL", Rule = "{\"type\":\"info\"}" }), e => e.Contains("제목"));
            Assert.Contains(CheckLogic.Validate(new UserCheckItem { Id = "user.z", Title = "t", Sql = "SELECT 1 FROM DUAL", Rule = null }), e => e == CheckLogic.EmptyRuleText);
            Assert.Contains(CheckLogic.Validate(new UserCheckItem { Id = "user.z", Title = "t", Sql = "SELECT 1 FROM DUAL", Rule = "{\"type\":\"threshold\",\"column\":\"V\"}" }), e => e.Contains("주의나 위험"));
            Assert.StartsWith("user.", CheckLogic.NewUserId());
            Assert.NotEqual(CheckLogic.NewUserId(), CheckLogic.NewUserId());
        }

        // ---------- 규칙 JSON ----------

        [Fact]
        public void RuleJson_RoundTrip_EveryKind()
        {
            var rules = new[]
            {
                CheckRule.AnyRow(CheckLevel.Warning), CheckRule.AnyRow(CheckLevel.Critical),
                CheckRule.Threshold("사용률(%)", 85, 95), CheckRule.Threshold("V", null, 3, CheckLevel.Critical),
                CheckRule.ByLevel("판정"), CheckRule.Info()
            };
            foreach (var rule in rules)
            {
                var json = CheckLogic.RuleToJson(rule);
                var back = CheckLogic.RuleFromJson(json);
                Assert.Equal(rule.Kind, back.Kind);
                Assert.Equal(rule.Level, back.Level);
                Assert.Equal(rule.Column, back.Column);
                Assert.Equal(rule.Warning, back.Warning);
                Assert.Equal(rule.Critical, back.Critical);
                Assert.Equal(rule.MissingLevel, back.MissingLevel);
            }
            Assert.Equal("{\"type\":\"anyRow\",\"level\":\"warn\"}", CheckLogic.RuleToJson(CheckRule.AnyRow(CheckLevel.Warning)));
            // 문턱은 글자 숫자도 받는다(손으로 쓴 JSON)
            Assert.Equal(80, CheckLogic.RuleFromJson("{\"type\":\"threshold\",\"column\":\"V\",\"warn\":\"80\"}").Warning);
            Assert.Throws<FormatException>(() => CheckLogic.RuleFromJson("{"));
            Assert.Throws<FormatException>(() => CheckLogic.RuleFromJson("{\"type\":\"byLevel\"}"));
            Assert.Throws<FormatException>(() => CheckLogic.RuleFromJson(""));
            Assert.Equal("사용률(%) ≥ 85 주의 · ≥ 95 위험", CheckLogic.RuleText(CheckRule.Threshold("사용률(%)", 85, 95)));
            Assert.Equal("경과(시간) ≥ 26 주의 · ≥ 50 위험(값 없으면 주의)", CheckLogic.RuleText(CheckRule.Threshold("경과(시간)", 26, 50, CheckLevel.Warning)));
            Assert.Equal("행이 있으면 위험", CheckLogic.RuleText(CheckRule.AnyRow(CheckLevel.Critical)));
        }

        // ---------- 내보내기·가져오기 ----------

        [Fact]
        public void ExportImport_RoundTrip_KeepsIds_ForeignIdsRenamed_BadItemsNamed()
        {
            var items = new[]
            {
                User("user.a", title: "세션 많음"),
                new UserCheckItem { Id = "user.b", Kind = "oracle", Category = "공간", Title = "큰 세그먼트", Sql = "SELECT SEGMENT_NAME, BYTES/1048576 MB FROM DBA_SEGMENTS",
                    Rule = CheckLogic.RuleToJson(CheckRule.Threshold("MB", 1000, 5000)), Advice = "줄 \"인용\" 포함" }
            };
            var json = CheckLogic.ExportItems(items);
            Assert.Contains("\"format\": \"folderss-db-helper-checks\"", json);
            Assert.Contains("세션 많음", json);
            var back = CheckLogic.ImportItems(json);
            Assert.Equal(new[] { "user.a", "user.b" }, back.Select(i => i.Id));
            Assert.Equal(items[1].Advice, back[1].Advice);
            Assert.Equal(1000, CheckLogic.RuleFromJson(back[1].Rule).Warning);

            var foreign = json.Replace("\"user.a\"", "\"ora.tablespace\"");
            Assert.StartsWith("user.", CheckLogic.ImportItems(foreign)[0].Id);
            Assert.NotEqual("user.a", CheckLogic.ImportItems(foreign)[0].Id);

            var bad = Assert.Throws<FormatException>(() => CheckLogic.ImportItems(json.Replace("SELECT SEGMENT_NAME", "DELETE SEGMENT_NAME")));
            Assert.Contains("2번째 항목(큰 세그먼트)", bad.Message);
            Assert.Throws<FormatException>(() => CheckLogic.ImportItems("{\"items\":[]}"));
            Assert.Throws<FormatException>(() => CheckLogic.ImportItems("not json"));
        }

        // ---------- 설정·글 ----------

        [Fact]
        public void Settings_RecordDefaultsOn_RetentionRange()
        {
            Assert.True(CheckLogic.IsRecording(null));
            Assert.True(CheckLogic.IsRecording("true"));
            Assert.False(CheckLogic.IsRecording(" FALSE "));
            bool valid;
            Assert.Equal(90, CheckLogic.RetentionDays(null, out valid));
            Assert.True(valid);
            Assert.Equal(30, CheckLogic.RetentionDays("30", out valid));
            Assert.True(valid);
            Assert.Equal(90, CheckLogic.RetentionDays("6", out valid));
            Assert.False(valid);
            Assert.Equal(3650, CheckLogic.RetentionDays("3650", out valid));
            Assert.Equal(90, CheckLogic.RetentionDays("abc", out valid));
            Assert.False(valid);
            Assert.Equal("일일 점검 2026-10-20 0905.xlsx", CheckLogic.ReportFileName(new DateTime(2026, 10, 20, 9, 5, 0)));
            Assert.Equal("OWNER: SCOTT · NAME: ", CheckLogic.RowText(new[] { "OWNER", "NAME" }, new[] { "SCOTT", null }));
        }

        [Fact]
        public void PluginVersion_FromPluginJson_EmptyWhenMissing()
        {
            var folder = Path.Combine(Path.GetTempPath(), "fmon-ver-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                Assert.Equal("", CheckLogic.PluginVersion(folder));
                File.WriteAllText(Path.Combine(folder, "plugin.json"), "{\"id\":\"x\",\"version\":\"1.9.0\"}");
                Assert.Equal("1.9.0", CheckLogic.PluginVersion(folder));
                File.WriteAllText(Path.Combine(folder, "plugin.json"), "{broken");
                Assert.Equal("", CheckLogic.PluginVersion(folder));
                Assert.Equal("", CheckLogic.PluginVersion(null));
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        // ---------- Oracle 기본 묶음 모양 ----------

        [Fact]
        public void CheckSet_OnlyOracleForNow()
        {
            Assert.NotNull(DbProviders.Oracle.Monitor.CheckSet);
            Assert.Null(DbProviders.Postgres.Monitor.CheckSet);
            Assert.Null(DbProviders.SqlServer.Monitor.CheckSet);
            Assert.Null(DbProviders.Generic.Monitor.CheckSet);
        }

        [Fact]
        public void Oracle_CheckSet_IdsUnique_KnownCategories_ReadOnlySql_AliasesFit11g_RuleColumnsInSql()
        {
            var set = DbProviders.Oracle.Monitor.CheckSet;
            Assert.Contains("AS IDENTITY", set.IdentitySql);
            Assert.Contains("AS LABEL", set.IdentitySql);
            Assert.Contains("V$DATABASE", set.IdentitySql);
            Assert.Equal(set.Items.Count, set.Items.Select(c => c.Id).Distinct().Count());
            foreach (var check in set.Items)
            {
                Assert.StartsWith("ora.", check.Id);
                Assert.False(check.IsUser);
                Assert.Contains(check.Category, MonitorCheckSet.Categories);
                Assert.False(string.IsNullOrWhiteSpace(check.Advice), check.Id);
                Assert.False(string.IsNullOrWhiteSpace(check.Privilege), check.Id);
                foreach (var sql in new[] { check.Sql }.Concat(check.Requirements.Select(r => r.Sql)))
                {
                    var statements = SqlScript.Split(sql);
                    Assert.Single(statements);
                    Assert.Equal(SqlKind.Query, statements[0].Kind);
                    var bare = Regex.Replace(sql, "'[^']*'", "''");
                    Assert.DoesNotMatch(@":[A-Za-z_]", bare);
                    foreach (var word in new[] { "INSERT ", "UPDATE ", "DELETE ", "DROP ", "ALTER ", "TRUNCATE ", "MERGE " })
                        Assert.False(Regex.IsMatch(bare, @"\b" + word, RegexOptions.IgnoreCase), check.Id + ": " + word);
                    Assert.DoesNotContain(";", sql);
                }
                foreach (Match alias in Regex.Matches(check.Sql, "AS \"([^\"]+)\""))
                    Assert.True(Encoding.UTF8.GetByteCount(alias.Groups[1].Value) <= 30, check.Id + " 별칭 " + alias.Groups[1].Value);
                if (check.Rule.Column != null)
                    Assert.Contains("AS \"" + check.Rule.Column + "\"", check.Sql);
            }
            // 함정(실측): 휴지통 객체는 먼저 거르고, 경고 로그는 권한 안내, FRA는 회수 가능 공간을 뺀다, 내장 스키마는 뺀다
            var stale = set.Items.Single(c => c.Id == "ora.stale_stats").Sql;
            Assert.Contains("NO_MERGE", stale);
            Assert.Contains("NOT LIKE 'BIN$%'", stale);
            Assert.Contains("11g", set.Items.Single(c => c.Id == "ora.alert_log").Privilege);
            Assert.Contains("SPACE_USED - SPACE_RECLAIMABLE", set.Items.Single(c => c.Id == "ora.fra").Sql);
            Assert.Single(set.Items.Single(c => c.Id == "ora.fra").Requirements);
            Assert.Contains("NOT LIKE 'APEX\\_%' ESCAPE '\\'", set.Items.Single(c => c.Id == "ora.invalid_objects").Sql);
            Assert.Contains("'SYS'", set.Items.Single(c => c.Id == "ora.password_expiry").Sql);
            Assert.Equal(CheckLevel.Warning, set.Items.Single(c => c.Id == "ora.last_backup").Rule.MissingLevel);
        }
    }
}

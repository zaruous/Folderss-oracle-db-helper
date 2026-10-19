using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MyPlugin
{
    /// <summary>점검 결과 상태.</summary>
    internal enum CheckStatus
    {
        Ok,
        Info,
        Warning,
        Critical,
        /// <summary>권한·필요 조건·꺼 둠으로 돌리지 않음.</summary>
        Skipped,
        /// <summary>실패·시간 초과·잘못된 사용자 항목.</summary>
        Error
    }

    /// <summary>직전 실행과 비교.</summary>
    internal enum CheckTrend
    {
        None,
        New,
        Same,
        Worse,
        Better
    }

    /// <summary>점검 항목 하나의 결과.</summary>
    internal sealed class CheckOutcome
    {
        public MonitorCheck Check { get; set; }
        public CheckStatus Status { get; set; }
        public double? Value { get; set; }
        /// <summary>값 요약("3건" · "88.0 — MES_DATA" · "값 없음").</summary>
        public string ValueText { get; set; }
        /// <summary>건너뜀·오류의 설명(사람이 읽을 문장). 정상·주의·위험이면 null.</summary>
        public string Message { get; set; }
        /// <summary>건너뜀·오류의 이유 분류(privilege · requirement · disabled · timeout · error · invalid).</summary>
        public string Reason { get; set; }
        public List<string> Columns { get; set; } = new List<string>();
        /// <summary>보이는 발견 행(최대 <see cref="CheckLogic.MaxShownRows"/>).</summary>
        public List<string[]> Rows { get; set; } = new List<string[]>();
        /// <summary>가져온 행 수(<see cref="CheckLogic.MaxRows"/>에서 끊음).</summary>
        public int RowCount { get; set; }
        public TimeSpan Elapsed { get; set; }
        public StoredResult Previous { get; set; }
        public CheckTrend Trend { get; set; }
    }

    /// <summary>
    /// 일일 점검(순수 로직 — DBMS를 모른다): 규칙 판정, 직전 대비, 기본 + 사용자 항목 합치기·덮어쓰기, 사용자 항목 검증, JSON 내보내기·가져오기, 설정 해석.
    /// 설계: docs/monitor-dba-design.md 6장.
    /// </summary>
    internal static class CheckLogic
    {
        public const string Title = "일일 점검";
        /// <summary>항목 하나에서 가져오는 최대 행(값·행 수 판정용)과 화면·엑셀에 보이는 최대 행.</summary>
        public const int MaxRows = 1000;
        public const int MaxShownRows = 100;
        /// <summary>항목 하나의 시간 제한(초).</summary>
        public const int TimeoutSeconds = 60;
        public const string EmptyRuleText = "판정 규칙이 비었습니다.";

        public const string RecordSettingKey = "monitor.check.record";
        public const string RetentionSettingKey = "monitor.history.retentionDays";
        /// <summary>마지막에 고른 대상 DB(접속 Id 목록).</summary>
        public const string DbsSettingKey = "monitor.check.dbs";
        /// <summary>내 양식(.xlsx) 경로. 비었으면 기본 양식.</summary>
        public const string TemplateSettingKey = "monitor.check.template";
        /// <summary>보고서를 마지막에 저장한 폴더.</summary>
        public const string FolderSettingKey = "monitor.check.folder";
        public const int DefaultRetentionDays = 90;
        public const int MinRetentionDays = 7;
        public const int MaxRetentionDays = 3650;

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ---------- 판정 ----------

        /// <summary>조회 결과를 규칙으로 판정한다. 규칙이 말하는 열이 없으면 오류(사용자 항목의 열 이름 오타).</summary>
        public static CheckOutcome Evaluate(MonitorCheck check, QueryTable table)
        {
            if (check == null)
                throw new ArgumentNullException(nameof(check));
            if (table == null)
                throw new ArgumentNullException(nameof(table));
            var outcome = new CheckOutcome
            {
                Check = check,
                Columns = new List<string>(table.Columns),
                Rows = table.Rows.Take(MaxShownRows).ToList(),
                RowCount = table.Rows.Count
            };
            var rule = check.Rule;
            switch (rule.Kind)
            {
                case CheckRuleKind.AnyRow:
                    outcome.Value = table.Rows.Count;
                    outcome.Status = table.Rows.Count > 0 ? StatusOf(rule.Level) : CheckStatus.Ok;
                    outcome.ValueText = table.Rows.Count > 0 ? CountText(table.Rows.Count) : "없음";
                    break;
                case CheckRuleKind.Threshold:
                {
                    var index = ColumnIndex(table, rule.Column);
                    if (index < 0)
                        return Failed(check, "invalid", "결과에 판정할 열 '" + rule.Column + "'이 없습니다. 있는 열: " + string.Join(", ", table.Columns));
                    string[] worst = null;
                    double? max = null;
                    foreach (var row in table.Rows)
                    {
                        var value = Number(index < row.Length ? row[index] : null);
                        if (value.HasValue && (!max.HasValue || value.Value > max.Value))
                        {
                            max = value;
                            worst = row;
                        }
                    }
                    outcome.Value = max;
                    if (!max.HasValue)
                    {
                        outcome.Status = StatusOf(rule.MissingLevel);
                        outcome.ValueText = "값 없음";
                        break;
                    }
                    var level = rule.Critical.HasValue && max.Value >= rule.Critical.Value ? CheckLevel.Critical
                        : rule.Warning.HasValue && max.Value >= rule.Warning.Value ? CheckLevel.Warning : CheckLevel.Ok;
                    outcome.Status = StatusOf(level);
                    var label = index > 0 && worst != null && worst.Length > 0 && !string.IsNullOrEmpty(worst[0]) ? " — " + worst[0] : "";
                    outcome.ValueText = NumberText(max.Value) + label;
                    break;
                }
                case CheckRuleKind.ByLevel:
                {
                    var index = ColumnIndex(table, rule.Column);
                    if (index < 0)
                        return Failed(check, "invalid", "결과에 판정 열 '" + rule.Column + "'이 없습니다. 있는 열: " + string.Join(", ", table.Columns));
                    var critical = 0;
                    var warning = 0;
                    foreach (var row in table.Rows)
                    {
                        var level = ((index < row.Length ? row[index] : null) ?? "").Trim().ToUpperInvariant();
                        if (level == "CRIT" || level == "CRITICAL")
                            critical++;
                        else if (level != "OK" && level.Length > 0)
                            warning++;
                    }
                    outcome.Value = critical + warning;
                    outcome.Status = critical > 0 ? CheckStatus.Critical : warning > 0 ? CheckStatus.Warning : CheckStatus.Ok;
                    outcome.ValueText = critical + warning == 0 ? (table.Rows.Count == 0 ? "없음" : "정상")
                        : string.Join(" · ", new[] { critical > 0 ? "위험 " + critical.ToString(Inv) : null, warning > 0 ? "주의 " + warning.ToString(Inv) : null }.Where(t => t != null));
                    break;
                }
                default:
                    outcome.Status = CheckStatus.Info;
                    outcome.ValueText = table.Rows.Count == 0 ? "없음"
                        : string.Join(" · ", table.Columns.Take(3).Select((c, i) => c + " " + (i < table.Rows[0].Length ? table.Rows[0][i] ?? "" : "")));
                    break;
            }
            return outcome;
        }

        public static CheckOutcome Skipped(MonitorCheck check, string reason, string message)
        {
            return new CheckOutcome { Check = check, Status = CheckStatus.Skipped, Reason = reason, Message = message, ValueText = "건너뜀" };
        }

        public static CheckOutcome Failed(MonitorCheck check, string reason, string message)
        {
            return new CheckOutcome { Check = check, Status = CheckStatus.Error, Reason = reason, Message = message, ValueText = "오류" };
        }

        private static CheckStatus StatusOf(CheckLevel level)
        {
            return level == CheckLevel.Critical ? CheckStatus.Critical : level == CheckLevel.Warning ? CheckStatus.Warning : CheckStatus.Ok;
        }

        // ---------- 직전 대비 ----------

        /// <summary>
        /// 직전 실행의 같은 항목과 비교: 건강 단계(정상·정보 &lt; 주의 &lt; 위험)가 오르면 나빠짐, 내리면 좋아짐, 같으면 같음. 직전에 없으면 처음.
        /// 이번이나 직전이 건너뜀·오류면 비교하지 않는다.
        /// </summary>
        public static void Compare(CheckOutcome outcome, StoredResult previous)
        {
            outcome.Previous = previous;
            if (previous == null)
            {
                outcome.Trend = CheckTrend.New;
                return;
            }
            var now = HealthRank(outcome.Status);
            var before = HealthRank(ParseStatus(previous.Status));
            if (now < 0 || before < 0)
                outcome.Trend = CheckTrend.None;
            else
                outcome.Trend = now > before ? CheckTrend.Worse : now < before ? CheckTrend.Better : CheckTrend.Same;
        }

        private static int HealthRank(CheckStatus status)
        {
            switch (status)
            {
                case CheckStatus.Ok:
                case CheckStatus.Info:
                    return 0;
                case CheckStatus.Warning:
                    return 1;
                case CheckStatus.Critical:
                    return 2;
                default:
                    return -1;
            }
        }

        public static string TrendText(CheckTrend trend)
        {
            switch (trend)
            {
                case CheckTrend.New: return "처음";
                case CheckTrend.Worse: return "↑ 나빠짐";
                case CheckTrend.Better: return "↓ 좋아짐";
                case CheckTrend.Same: return "같음";
                default: return "";
            }
        }

        // ---------- 상태 글·코드·순서 ----------

        public static string StatusText(CheckStatus status)
        {
            switch (status)
            {
                case CheckStatus.Ok: return "정상";
                case CheckStatus.Info: return "정보";
                case CheckStatus.Warning: return "주의";
                case CheckStatus.Critical: return "위험";
                case CheckStatus.Skipped: return "건너뜀";
                default: return "오류";
            }
        }

        /// <summary>저장·엑셀용 코드: ok · info · warn · crit · skip · error.</summary>
        public static string StatusCode(CheckStatus status)
        {
            switch (status)
            {
                case CheckStatus.Ok: return "ok";
                case CheckStatus.Info: return "info";
                case CheckStatus.Warning: return "warn";
                case CheckStatus.Critical: return "crit";
                case CheckStatus.Skipped: return "skip";
                default: return "error";
            }
        }

        public static CheckStatus ParseStatus(string code)
        {
            switch ((code ?? "").Trim().ToLowerInvariant())
            {
                case "ok": return CheckStatus.Ok;
                case "info": return CheckStatus.Info;
                case "warn": return CheckStatus.Warning;
                case "crit": return CheckStatus.Critical;
                case "skip": return CheckStatus.Skipped;
                default: return CheckStatus.Error;
            }
        }

        /// <summary>심각도(클수록 나쁨): 위험 5 · 오류 4 · 주의 3 · 건너뜀 2 · 정보 1 · 정상 0.</summary>
        public static int Severity(CheckStatus status)
        {
            switch (status)
            {
                case CheckStatus.Critical: return 5;
                case CheckStatus.Error: return 4;
                case CheckStatus.Warning: return 3;
                case CheckStatus.Skipped: return 2;
                case CheckStatus.Info: return 1;
                default: return 0;
            }
        }

        /// <summary>분류 순서(기본 분류 → 그 밖 분류 이름순), 같은 분류면 들어온 순서.</summary>
        public static List<CheckOutcome> Ordered(IEnumerable<CheckOutcome> outcomes)
        {
            return outcomes.Select((o, i) => new { o, i })
                .OrderBy(x => CategoryOrder(x.o.Check.Category)).ThenBy(x => x.o.Check.Category, StringComparer.CurrentCulture).ThenBy(x => x.i)
                .Select(x => x.o).ToList();
        }

        public static int CategoryOrder(string category)
        {
            var index = MonitorCheckSet.Categories.ToList().IndexOf(category ?? "");
            return index < 0 ? MonitorCheckSet.Categories.Count : index;
        }

        /// <summary>요약: "위험 1 · 오류 1 · 주의 4 · 건너뜀 2 · 정상 10"(0인 것은 뺀다, 정보는 정상에 넣는다).</summary>
        public static string SummaryText(IEnumerable<CheckOutcome> outcomes)
        {
            var list = outcomes.ToList();
            if (list.Count == 0)
                return "항목 없음";
            var parts = new List<string>();
            foreach (var status in new[] { CheckStatus.Critical, CheckStatus.Error, CheckStatus.Warning, CheckStatus.Skipped })
            {
                var count = list.Count(o => o.Status == status);
                if (count > 0)
                    parts.Add(StatusText(status) + " " + count.ToString(Inv));
            }
            var ok = list.Count(o => o.Status == CheckStatus.Ok || o.Status == CheckStatus.Info);
            if (ok > 0)
                parts.Add("정상 " + ok.ToString(Inv));
            return string.Join(" · ", parts);
        }

        /// <summary>가장 심각한 결과(같으면 앞의 것). 없으면 null.</summary>
        public static CheckOutcome Worst(IEnumerable<CheckOutcome> outcomes)
        {
            CheckOutcome worst = null;
            foreach (var o in outcomes)
                if (worst == null || Severity(o.Status) > Severity(worst.Status))
                    worst = o;
            return worst;
        }

        public static StoredResult ToStored(CheckOutcome outcome)
        {
            return new StoredResult
            {
                ItemId = outcome.Check.Id,
                Status = StatusCode(outcome.Status),
                Value = outcome.Value,
                RowCount = outcome.Status == CheckStatus.Skipped || outcome.Status == CheckStatus.Error ? (int?)null : outcome.RowCount,
                Reason = outcome.Reason
            };
        }

        // ---------- 기본 + 사용자 항목, 덮어쓰기 ----------

        /// <summary>이번에 돌릴 항목·꺼 둔 항목·잘못된 사용자 항목.</summary>
        internal sealed class CheckPlan
        {
            public List<MonitorCheck> Active { get; } = new List<MonitorCheck>();
            public List<MonitorCheck> Disabled { get; } = new List<MonitorCheck>();
            public List<CheckOutcome> Invalid { get; } = new List<CheckOutcome>();
        }

        /// <summary>
        /// 기본 묶음 + 이 DBMS의 사용자 항목에 덮어쓰기(켬·끔, 임계값 규칙의 문턱)를 적용한다.
        /// 규칙 JSON이 잘못된 사용자 항목은 돌리지 않고 오류 결과로 돌려준다.
        /// </summary>
        public static CheckPlan Plan(MonitorCheckSet set, string kind, IEnumerable<UserCheckItem> userItems, IReadOnlyDictionary<string, CheckOverride> overrides)
        {
            var plan = new CheckPlan();
            var all = new List<MonitorCheck>(set == null ? Enumerable.Empty<MonitorCheck>() : set.Items);
            foreach (var item in (userItems ?? Enumerable.Empty<UserCheckItem>()).Where(i => string.Equals(i.Kind, kind, StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    all.Add(ToCheck(item));
                }
                catch (Exception ex) when (ex is FormatException || ex is ArgumentException)
                {
                    var id = string.IsNullOrWhiteSpace(item.Id) ? NewUserId() : item.Id;
                    var broken = new MonitorCheck(id, string.IsNullOrWhiteSpace(item.Category) ? "사용자" : item.Category,
                        string.IsNullOrWhiteSpace(item.Title) ? id : item.Title, string.IsNullOrWhiteSpace(item.Sql) ? "SELECT 1" : item.Sql, CheckRule.Info());
                    plan.Invalid.Add(Failed(broken, "invalid", "사용자 항목이 잘못돼 돌리지 않았습니다: " + ex.Message));
                }
            }
            foreach (var check in all)
            {
                CheckOverride value = null;
                if (overrides != null)
                    overrides.TryGetValue(check.Id, out value);
                if (value != null && value.Enabled == false)
                {
                    plan.Disabled.Add(check);
                    continue;
                }
                plan.Active.Add(value == null ? check : new MonitorCheck(check.Id, check.Category, check.Title, check.Sql,
                    check.Rule.WithThresholds(value.Warning, value.Critical), check.Privilege, check.Advice, check.Requirements));
            }
            return plan;
        }

        /// <summary>사용자 항목 → 점검 항목. 규칙 JSON이 잘못되면 FormatException, 제목·SQL이 비면 ArgumentException.</summary>
        public static MonitorCheck ToCheck(UserCheckItem item)
        {
            return new MonitorCheck(item.Id, string.IsNullOrWhiteSpace(item.Category) ? "사용자" : item.Category.Trim(), (item.Title ?? "").Trim(), (item.Sql ?? "").Trim(),
                RuleFromJson(item.Rule), "", item.Advice);
        }

        /// <summary>사용자 항목의 문제(빈 목록이면 저장할 수 있다): 제목·SQL(조회 한 문장, FOR UPDATE 아님)·규칙.</summary>
        public static List<string> Validate(UserCheckItem item)
        {
            var errors = new List<string>();
            if (item == null)
            {
                errors.Add("항목이 없습니다.");
                return errors;
            }
            if (string.IsNullOrWhiteSpace(item.Title))
                errors.Add("제목을 입력하세요.");
            if (string.IsNullOrWhiteSpace(item.Sql))
                errors.Add("SQL을 입력하세요.");
            else
            {
                var statements = SqlScript.Split(item.Sql);
                if (statements.Count != 1)
                    errors.Add("SQL은 한 문장이어야 합니다(지금 " + statements.Count.ToString(Inv) + "개).");
                else if (statements[0].Kind != SqlKind.Query || statements[0].ForUpdate)
                    errors.Add("점검 SQL은 자료를 바꾸지 않는 SELECT(WITH) 문이어야 합니다.");
            }
            try
            {
                RuleFromJson(item.Rule);
            }
            catch (FormatException ex)
            {
                errors.Add(ex.Message);
            }
            return errors;
        }

        /// <summary>새 사용자 항목 id: "user." + 12자리.</summary>
        public static string NewUserId()
        {
            return "user." + Guid.NewGuid().ToString("N").Substring(0, 12);
        }

        // ---------- 규칙 JSON ----------

        /// <summary>{"type":"anyRow","level":"warn"} · {"type":"threshold","column":"…","warn":80,"crit":90,"missing":"warn"} · {"type":"byLevel","column":"…"} · {"type":"info"}</summary>
        public static string RuleToJson(CheckRule rule)
        {
            var values = new Dictionary<string, object>();
            switch (rule.Kind)
            {
                case CheckRuleKind.AnyRow:
                    values["type"] = "anyRow";
                    values["level"] = LevelCode(rule.Level);
                    break;
                case CheckRuleKind.Threshold:
                    values["type"] = "threshold";
                    values["column"] = rule.Column;
                    if (rule.Warning.HasValue)
                        values["warn"] = rule.Warning.Value;
                    if (rule.Critical.HasValue)
                        values["crit"] = rule.Critical.Value;
                    if (rule.MissingLevel != CheckLevel.Ok)
                        values["missing"] = LevelCode(rule.MissingLevel);
                    break;
                case CheckRuleKind.ByLevel:
                    values["type"] = "byLevel";
                    values["column"] = rule.Column;
                    break;
                default:
                    values["type"] = "info";
                    break;
            }
            return JsonSerializer.Serialize(values);
        }

        public static CheckRule RuleFromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new FormatException(EmptyRuleText);
            try
            {
                using (var document = JsonDocument.Parse(json))
                {
                    var root = document.RootElement;
                    var type = Str(root, "type");
                    switch ((type ?? "").ToLowerInvariant())
                    {
                        case "anyrow":
                            return CheckRule.AnyRow(ParseLevel(Str(root, "level"), CheckLevel.Warning));
                        case "threshold":
                            return CheckRule.Threshold(Str(root, "column"), Num(root, "warn"), Num(root, "crit"), ParseLevel(Str(root, "missing"), CheckLevel.Ok));
                        case "bylevel":
                            return CheckRule.ByLevel(Str(root, "column"));
                        case "info":
                            return CheckRule.Info();
                        default:
                            throw new FormatException("판정 규칙 종류를 모릅니다: " + (type ?? "(없음)"));
                    }
                }
            }
            catch (JsonException ex)
            {
                throw new FormatException("판정 규칙 JSON이 잘못됐습니다: " + ex.Message, ex);
            }
            catch (ArgumentException ex)
            {
                throw new FormatException(ex.Message, ex);
            }
        }

        public static string LevelCode(CheckLevel level)
        {
            return level == CheckLevel.Critical ? "crit" : level == CheckLevel.Warning ? "warn" : "ok";
        }

        private static CheckLevel ParseLevel(string code, CheckLevel fallback)
        {
            switch ((code ?? "").Trim().ToLowerInvariant())
            {
                case "crit": return CheckLevel.Critical;
                case "warn": return CheckLevel.Warning;
                case "ok": return CheckLevel.Ok;
                default: return fallback;
            }
        }

        /// <summary>사람이 읽을 규칙 설명: "행이 있으면 위험" · "사용률(%) ≥ 85 주의 · ≥ 95 위험(값 없으면 주의)".</summary>
        public static string RuleText(CheckRule rule)
        {
            switch (rule.Kind)
            {
                case CheckRuleKind.AnyRow:
                    return "행이 있으면 " + (rule.Level == CheckLevel.Critical ? "위험" : "주의");
                case CheckRuleKind.Threshold:
                    var parts = new List<string>();
                    if (rule.Warning.HasValue)
                        parts.Add("≥ " + NumberText(rule.Warning.Value) + " 주의");
                    if (rule.Critical.HasValue)
                        parts.Add("≥ " + NumberText(rule.Critical.Value) + " 위험");
                    return rule.Column + " " + string.Join(" · ", parts) + (rule.MissingLevel != CheckLevel.Ok ? "(값 없으면 " + (rule.MissingLevel == CheckLevel.Critical ? "위험" : "주의") + ")" : "");
                case CheckRuleKind.ByLevel:
                    return rule.Column + " 열의 OK·WARN·CRIT";
                default:
                    return "정보(판정 없음)";
            }
        }

        // ---------- 내보내기·가져오기(JSON) ----------

        /// <summary>사용자 항목을 팀에 나눌 JSON(형식 1): {"format":"folderss-db-helper-checks","version":1,"items":[…]}.</summary>
        public static string ExportItems(IEnumerable<UserCheckItem> items)
        {
            var list = items.Select(i => new Dictionary<string, object>
            {
                { "id", i.Id }, { "kind", i.Kind }, { "category", i.Category }, { "title", i.Title }, { "sql", i.Sql },
                { "rule", JsonDocument.Parse(i.Rule).RootElement.Clone() }, { "advice", i.Advice ?? "" }
            }).ToList();
            return JsonSerializer.Serialize(new Dictionary<string, object> { { "format", "folderss-db-helper-checks" }, { "version", 1 }, { "items", list } },
                new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }

        /// <summary>가져오기: 형식이 다르거나 항목이 잘못되면 FormatException(어느 항목인지). id가 user.로 시작하지 않으면 새로 붙인다.</summary>
        public static List<UserCheckItem> ImportItems(string json)
        {
            try
            {
                using (var document = JsonDocument.Parse(json ?? ""))
                {
                    var root = document.RootElement;
                    if (Str(root, "format") != "folderss-db-helper-checks")
                        throw new FormatException("DB Helper 점검 항목 파일이 아닙니다(format이 folderss-db-helper-checks가 아님).");
                    JsonElement items;
                    if (!root.TryGetProperty("items", out items) || items.ValueKind != JsonValueKind.Array)
                        throw new FormatException("items 목록이 없습니다.");
                    var result = new List<UserCheckItem>();
                    var index = 0;
                    foreach (var element in items.EnumerateArray())
                    {
                        index++;
                        JsonElement rule;
                        var item = new UserCheckItem
                        {
                            Id = Str(element, "id"),
                            Kind = Str(element, "kind") ?? "oracle",
                            Category = Str(element, "category") ?? "사용자",
                            Title = Str(element, "title"),
                            Sql = Str(element, "sql"),
                            Rule = element.TryGetProperty("rule", out rule) ? rule.GetRawText() : null,
                            Advice = Str(element, "advice") ?? ""
                        };
                        if (string.IsNullOrWhiteSpace(item.Id) || !item.Id.StartsWith("user.", StringComparison.Ordinal))
                            item.Id = NewUserId();
                        var errors = Validate(item);
                        if (errors.Count > 0)
                            throw new FormatException(index.ToString(Inv) + "번째 항목(" + (item.Title ?? "") + "): " + string.Join(" ", errors));
                        result.Add(item);
                    }
                    return result;
                }
            }
            catch (JsonException ex)
            {
                throw new FormatException("JSON이 잘못됐습니다: " + ex.Message, ex);
            }
        }

        // ---------- 설정 ----------

        /// <summary>결과 기록 설정: "false"만 끔(없으면 켬).</summary>
        public static bool IsRecording(string value)
        {
            return !string.Equals((value ?? "").Trim(), "false", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>보관 기간 설정: 비거나 숫자가 아니거나 범위(7~3650) 밖이면 기본 90일. valid는 그대로 쓸 수 있는 값이었는지.</summary>
        public static int RetentionDays(string value, out bool valid)
        {
            int days;
            valid = string.IsNullOrWhiteSpace(value) || (int.TryParse(value.Trim(), NumberStyles.Integer, Inv, out days) && days >= MinRetentionDays && days <= MaxRetentionDays);
            return !string.IsNullOrWhiteSpace(value) && int.TryParse(value.Trim(), NumberStyles.Integer, Inv, out days) && days >= MinRetentionDays && days <= MaxRetentionDays
                ? days : DefaultRetentionDays;
        }

        /// <summary>보고서 기본 파일 이름: "일일 점검 2026-10-20 0905.xlsx".</summary>
        public static string ReportFileName(DateTime now)
        {
            return Title + " " + now.ToString("yyyy-MM-dd HHmm", Inv) + ".xlsx";
        }

        /// <summary>플러그인 폴더 plugin.json의 version(결과에 남겨 기본 묶음이 바뀐 실행을 구분). 읽지 못하면 빈 글.</summary>
        public static string PluginVersion(string pluginDirectory)
        {
            try
            {
                if (string.IsNullOrEmpty(pluginDirectory))
                    return "";
                var path = System.IO.Path.Combine(pluginDirectory, "plugin.json");
                if (!System.IO.File.Exists(path))
                    return "";
                using (var document = JsonDocument.Parse(System.IO.File.ReadAllText(path)))
                    return Str(document.RootElement, "version") ?? "";
            }
            catch (Exception)
            {
                return "";
            }
        }

        /// <summary>
        /// 점검 창 머리 요약: 돌린 DB 수 · 접속 실패 수 · 상태별 항목 수. 아직 돌리지 않았으면 안내.
        /// </summary>
        public static string RunSummaryText(int dbCount, int failedDbs, IEnumerable<CheckOutcome> outcomes)
        {
            if (dbCount == 0)
                return "대상 DB를 고르고 [점검 실행]을 누르세요.";
            var text = "DB " + dbCount.ToString(Inv) + "개";
            if (failedDbs > 0)
                text += " · 접속 실패 " + failedDbs.ToString(Inv);
            var list = outcomes.ToList();
            return list.Count == 0 ? text : text + " · " + SummaryText(list);
        }

        // ---------- 글 ----------

        public static string CountText(int count)
        {
            return count >= MaxRows ? MaxRows.ToString("N0", Inv) + "건 넘음" : count.ToString("N0", Inv) + "건";
        }

        public static string NumberText(double value)
        {
            return value.ToString("0.#", Inv);
        }

        /// <summary>발견 행 한 줄 글: "소유자: SCOTT · 객체: P_X".</summary>
        public static string RowText(IReadOnlyList<string> columns, string[] row)
        {
            return string.Join(" · ", columns.Select((c, i) => c + ": " + (i < row.Length ? row[i] ?? "" : "")));
        }

        // ---------- 도움 ----------

        private static int ColumnIndex(QueryTable table, string name)
        {
            for (var i = 0; i < table.Columns.Count; i++)
                if (string.Equals(table.Columns[i], name, StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }

        private static double? Number(string text)
        {
            double value;
            return !string.IsNullOrWhiteSpace(text) && double.TryParse(text.Trim(), NumberStyles.Float, Inv, out value) ? value : (double?)null;
        }

        private static string Str(JsonElement element, string name)
        {
            JsonElement value;
            return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }

        private static double? Num(JsonElement element, string name)
        {
            JsonElement value;
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out value))
                return null;
            if (value.ValueKind == JsonValueKind.Number)
                return value.GetDouble();
            double parsed;
            return value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, Inv, out parsed) ? parsed : (double?)null;
        }
    }

    /// <summary>
    /// 점검 실행기: 한 세션에서 항목을 차례로 — 필요 조건 점검 → 조회(항목마다 시간 제한) → 판정. 하나가 실패해도 계속한다.
    /// 권한 오류는 "건너뜀(권한)", 조건 불충족은 "건너뜀(조건)", 시간 초과·그 밖은 "오류". 세션이 끊기면 남은 항목은 오류로 끝낸다.
    /// </summary>
    internal static class CheckRunner
    {
        /// <param name="progress">항목 하나가 끝날 때마다(부른 스레드의 동기화 문맥에서). null이면 부르지 않는다.</param>
        /// <param name="perCheck">항목 하나의 시간 제한(기본 <see cref="CheckLogic.TimeoutSeconds"/>초).</param>
        /// <param name="permissionDenied">권한 오류 판정(기본은 세션 공급자의 <see cref="IDbProvider.IsPermissionDenied"/>).</param>
        public static async Task<List<CheckOutcome>> RunAsync(DbSession session, IReadOnlyList<MonitorCheck> checks, Action<CheckOutcome> progress,
            CancellationToken cancellation, TimeSpan? perCheck = null, Func<Exception, bool> permissionDenied = null)
        {
            var limit = perCheck ?? TimeSpan.FromSeconds(CheckLogic.TimeoutSeconds);
            var denied = permissionDenied ?? session.Provider.IsPermissionDenied;
            var outcomes = new List<CheckOutcome>();
            foreach (var check in checks)
            {
                cancellation.ThrowIfCancellationRequested();
                CheckOutcome outcome;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                if (session.IsBroken)
                {
                    outcome = CheckLogic.Failed(check, "error", "DB 연결이 끊겨 돌리지 못했습니다.");
                }
                else
                {
                    outcome = await RunOne(session, denied, check, cancellation, limit);
                }
                outcome.Elapsed = watch.Elapsed;
                outcomes.Add(outcome);
                progress?.Invoke(outcome);
            }
            return outcomes;
        }

        private static async Task<CheckOutcome> RunOne(DbSession session, Func<Exception, bool> denied, MonitorCheck check, CancellationToken cancellation, TimeSpan limit)
        {
            try
            {
                foreach (var requirement in check.Requirements)
                {
                    var met = await session.QueryTableAsync(requirement.Sql, 1, cancellation);
                    if (!MonitorLogic.RequirementMet(met))
                        return CheckLogic.Skipped(check, "requirement", requirement.Message);
                }
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                {
                    timeout.CancelAfter(limit);
                    try
                    {
                        var table = await session.QueryTableAsync(check.Sql, CheckLogic.MaxRows, timeout.Token);
                        return CheckLogic.Evaluate(check, table);
                    }
                    catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                    {
                        return CheckLogic.Failed(check, "timeout", "시간 초과(" + limit.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + "초)");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var reason = DbSession.DescribeError(ex);
                if (denied(ex))
                    return CheckLogic.Skipped(check, "privilege", "권한이 없어 건너뜀 — 필요 권한: " + (string.IsNullOrWhiteSpace(check.Privilege) ? "(알 수 없음)" : check.Privilege) + " (" + reason + ")");
                return CheckLogic.Failed(check, "error", reason);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MyPlugin
{
    /// <summary>점검 보고서의 DB 하나(접속에 실패했으면 Error만 있고 Outcomes는 빈 목록).</summary>
    internal sealed class CheckReportDb
    {
        /// <summary>접속 이름.</summary>
        public string Name { get; set; }
        /// <summary>user@host:port/service.</summary>
        public string Address { get; set; }
        /// <summary>서버가 알려 준 DB 이름(XE (DBID 2963…)). 모르면 빈 글.</summary>
        public string Label { get; set; }
        public string Version { get; set; }
        public DateTime StartedAt { get; set; }
        public TimeSpan Elapsed { get; set; }
        public string Error { get; set; }
        public List<CheckOutcome> Outcomes { get; set; } = new List<CheckOutcome>();
        /// <summary>직전 실행 시각(현지). 처음이면 null.</summary>
        public DateTime? PreviousAt { get; set; }
    }

    /// <summary>
    /// 일일 점검 보고서 템플릿 변수(순수 로직, 테스트 대상). 정의서와 같은 규칙 — 값이 없으면 빈 글자, 수는 수 그대로.
    /// 발견 행의 열은 c1~c8(제목)·v1~v8(값)로 칸마다 넣는다. 열이 8개를 넘으면 8번째 칸에 나머지를 " · "로 이어 붙인다.
    /// </summary>
    internal static class CheckReportModel
    {
        public const string DefaultTitle = "DB 일일 점검 보고서";
        /// <summary>발견 행을 칸으로 펼치는 열 수.</summary>
        public const int CellColumns = 8;

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static readonly IReadOnlyList<TemplateVariable> Variables = new[]
        {
            new TemplateVariable("$report.title", "보고서 제목", DefaultTitle),
            new TemplateVariable("$report.date", "점검일(yyyy-MM-dd)", "2026-10-20"),
            new TemplateVariable("$report.author", "점검자(Windows 사용자)", "dba01"),
            new TemplateVariable("$report.generatedAt", "만든 시각(yyyy-MM-dd HH:mm)", "2026-10-20 09:05"),
            new TemplateVariable("$report.dbCount", "대상 DB 수(숫자)", "3"),
            new TemplateVariable("$report.summary", "전체 요약", "위험 1 · 주의 4 · 정상 40"),
            new TemplateVariable("$report.issueCount", "주의·위험·오류 항목 수(모든 DB, 숫자)", "5"),
            new TemplateVariable("$dbs", "대상 DB 목록(#foreach·#sheet로 돈다)", ""),
            new TemplateVariable("$db.no / $db.name / $db.address", "순번 / 접속 이름 / user@host:port/service", "1 / 운영 / scott@db1:1521/prod"),
            new TemplateVariable("$db.label / $db.version", "서버가 알려 준 DB 이름 / 제품·버전", "PROD (DBID 1234) / Oracle Database 19c …"),
            new TemplateVariable("$db.sheetName", "이 DB의 시트 이름(31자 안, 겹치지 않음)", "운영"),
            new TemplateVariable("$db.startedAt / $db.elapsed", "점검 시작 시각 / 걸린 시간(초, 숫자)", "2026-10-20 09:00 / 4.2"),
            new TemplateVariable("$db.previousAt", "직전 점검 시각(처음이면 빈 칸)", "2026-10-19 09:00"),
            new TemplateVariable("$db.error", "접속 실패 이유(성공이면 빈 칸)", "ORA-12541: TNS:no listener"),
            new TemplateVariable("$db.summary", "요약 글", "위험 1 · 주의 2 · 정상 14"),
            new TemplateVariable("$db.critCount / warnCount / errorCount / skipCount / okCount", "상태별 항목 수(숫자, okCount는 정보 포함)", "1 / 2 / 0 / 1 / 13"),
            new TemplateVariable("$db.worst / $db.worstCode", "가장 나쁜 상태 글 / 코드", "위험 / crit"),
            new TemplateVariable("$db.checks", "점검 항목 결과(분류 순, #foreach($check in $db.checks))", ""),
            new TemplateVariable("$db.issues", "주의·위험·오류인 항목만", ""),
            new TemplateVariable("$db.findings", "발견 행이 있는 주의·위험·정보 항목만(발견 사항 표)", ""),
            new TemplateVariable("$check.no / id / category / title", "순번 / 항목 id / 분류 / 제목", "3 / ora.tablespace / 공간 / 테이블스페이스 사용률"),
            new TemplateVariable("$check.status / statusText", "상태 코드 ok·info·warn·crit·skip·error / 정상·정보·주의·위험·건너뜀·오류", "warn / 주의"),
            new TemplateVariable("$check.value / valueText", "판정에 쓴 수(없으면 빈 칸) / 값 요약", "88 / 88 — USERS"),
            new TemplateVariable("$check.trend / previous", "직전 대비(처음·같음·↑ 나빠짐·↓ 좋아짐) / 직전 상태 글", "↑ 나빠짐 / 정상"),
            new TemplateVariable("$check.rule", "판정 기준", "사용률(%) ≥ 85 주의 · ≥ 95 위험"),
            new TemplateVariable("$check.message / advice", "건너뜀·오류 이유 / 조치 안내", ""),
            new TemplateVariable("$check.note", "상태에 맞는 설명(정상 빈 칸 · 주의·위험은 조치 · 건너뜀·오류는 이유)", ""),
            new TemplateVariable("$check.rowCount / shownRows", "발견 행 수 / 보이는 행 수(최대 100, 숫자)", "12 / 12"),
            new TemplateVariable("$check.elapsed", "걸린 시간(초, 숫자)", "0.3"),
            new TemplateVariable("$check.c1 … c8", "발견 행의 열 제목(8칸, 넘치면 8번째에 이어서)", "테이블스페이스"),
            new TemplateVariable("$check.rows", "발견 행(#foreach($row in $check.rows))", ""),
            new TemplateVariable("$row.no / $row.v1 … v8 / $row.text", "순번 / 열 값(8칸) / 한 줄 글 '열: 값 · …'", "1 / USERS / …")
        };

        public static VelocityContext Context(IReadOnlyList<CheckReportDb> dbs, string title = null, string author = null, DateTime? now = null)
        {
            var at = now ?? DateTime.Now;
            var list = dbs ?? new List<CheckReportDb>();
            var sheetNames = new List<string>();
            var dbValues = new List<object>();
            for (var i = 0; i < list.Count; i++)
            {
                var sheetName = ExcelTemplate.UniqueSheetName(string.IsNullOrWhiteSpace(list[i].Name) ? "DB " + (i + 1).ToString(Inv) : list[i].Name, sheetNames);
                sheetNames.Add(sheetName);
                dbValues.Add(Db(list[i], i + 1, sheetName));
            }
            var all = list.SelectMany(d => d.Outcomes).ToList();
            var failed = list.Count(d => !string.IsNullOrEmpty(d.Error));
            var summary = CheckLogic.SummaryText(all);
            if (failed > 0)
                summary = "접속 실패 " + failed.ToString(Inv) + (all.Count > 0 ? " · " + summary : "");
            var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                { "report", Map(
                    "title", string.IsNullOrWhiteSpace(title) ? DefaultTitle : title.Trim(),
                    "date", at.ToString("yyyy-MM-dd", Inv),
                    "author", author ?? "",
                    "generatedAt", at.ToString("yyyy-MM-dd HH:mm", Inv),
                    "dbCount", list.Count,
                    "issueCount", all.Count(o => o.Status == CheckStatus.Critical || o.Status == CheckStatus.Warning || o.Status == CheckStatus.Error),
                    "summary", summary) },
                { "dbs", dbValues }
            };
            return new VelocityContext(values);
        }

        private static Dictionary<string, object> Db(CheckReportDb db, int no, string sheetName)
        {
            var ordered = CheckLogic.Ordered(db.Outcomes);
            var checks = new List<object>();
            var issues = new List<object>();
            var findings = new List<object>();
            for (var i = 0; i < ordered.Count; i++)
            {
                var value = Check(ordered[i], i + 1);
                checks.Add(value);
                var status = ordered[i].Status;
                if (status == CheckStatus.Critical || status == CheckStatus.Warning || status == CheckStatus.Error)
                    issues.Add(value);
                if (ordered[i].RowCount > 0 && (status == CheckStatus.Critical || status == CheckStatus.Warning || status == CheckStatus.Info))
                    findings.Add(value);
            }
            var worst = CheckLogic.Worst(ordered);
            var hasError = !string.IsNullOrEmpty(db.Error);
            return Map(
                "no", no,
                "name", db.Name ?? "",
                "address", db.Address ?? "",
                "label", db.Label ?? "",
                "version", db.Version ?? "",
                "sheetName", sheetName,
                "startedAt", db.StartedAt == default(DateTime) ? "" : db.StartedAt.ToString("yyyy-MM-dd HH:mm", Inv),
                "elapsed", Math.Round(db.Elapsed.TotalSeconds, 1),
                "previousAt", db.PreviousAt.HasValue ? db.PreviousAt.Value.ToString("yyyy-MM-dd HH:mm", Inv) : "",
                "error", db.Error ?? "",
                "summary", hasError ? "접속 실패" : CheckLogic.SummaryText(ordered),
                "critCount", ordered.Count(o => o.Status == CheckStatus.Critical),
                "warnCount", ordered.Count(o => o.Status == CheckStatus.Warning),
                "errorCount", ordered.Count(o => o.Status == CheckStatus.Error),
                "skipCount", ordered.Count(o => o.Status == CheckStatus.Skipped),
                "okCount", ordered.Count(o => o.Status == CheckStatus.Ok || o.Status == CheckStatus.Info),
                "worst", hasError ? "접속 실패" : worst == null ? "" : CheckLogic.StatusText(worst.Status),
                "worstCode", hasError ? "error" : worst == null ? "" : CheckLogic.StatusCode(worst.Status),
                "checks", checks,
                "issues", issues,
                "findings", findings);
        }

        private static Dictionary<string, object> Check(CheckOutcome outcome, int no)
        {
            var check = outcome.Check;
            var map = Map(
                "no", no,
                "id", check.Id,
                "category", check.Category ?? "",
                "title", check.Title ?? "",
                "status", CheckLogic.StatusCode(outcome.Status),
                "statusText", CheckLogic.StatusText(outcome.Status),
                "value", outcome.Value.HasValue ? (object)outcome.Value.Value : "",
                "valueText", outcome.ValueText ?? "",
                "trend", CheckLogic.TrendText(outcome.Trend),
                "previous", outcome.Previous == null ? "" : CheckLogic.StatusText(CheckLogic.ParseStatus(outcome.Previous.Status)),
                "rule", CheckLogic.RuleText(check.Rule),
                "message", outcome.Message ?? "",
                "advice", check.Advice ?? "",
                "note", Note(outcome),
                "rowCount", outcome.RowCount,
                "shownRows", outcome.Rows.Count,
                "elapsed", Math.Round(outcome.Elapsed.TotalSeconds, 1));
            var titles = Cells(outcome.Columns, outcome.Columns, true);
            for (var i = 0; i < CellColumns; i++)
                map["c" + (i + 1).ToString(Inv)] = titles[i];
            var rows = new List<object>();
            for (var r = 0; r < outcome.Rows.Count; r++)
            {
                var row = outcome.Rows[r];
                var values = Cells(outcome.Columns, row, false);
                var rowMap = Map("no", r + 1, "text", CheckLogic.RowText(outcome.Columns, row));
                for (var i = 0; i < CellColumns; i++)
                    rowMap["v" + (i + 1).ToString(Inv)] = values[i];
                rows.Add(rowMap);
            }
            map["rows"] = rows;
            return map;
        }

        /// <summary>상태에 맞는 설명: 주의·위험은 조치 안내, 건너뜀·오류는 이유, 그 밖은 빈 글.</summary>
        public static string Note(CheckOutcome outcome)
        {
            switch (outcome.Status)
            {
                case CheckStatus.Warning:
                case CheckStatus.Critical:
                    return outcome.Check.Advice ?? "";
                case CheckStatus.Skipped:
                case CheckStatus.Error:
                    return outcome.Message ?? "";
                default:
                    return "";
            }
        }

        /// <summary>값을 8칸으로: 모자라면 빈 글, 넘치면 8번째 칸에 "열: 값" 꼴로 이어 붙인다(제목 줄 header는 열 이름만 이어 붙임).</summary>
        public static string[] Cells(IReadOnlyList<string> columns, IReadOnlyList<string> values, bool header)
        {
            var cells = new string[CellColumns];
            for (var i = 0; i < CellColumns; i++)
                cells[i] = i < values.Count ? values[i] ?? "" : "";
            if (values.Count > CellColumns)
            {
                var rest = new List<string>();
                for (var i = CellColumns - 1; i < values.Count; i++)
                    rest.Add(header ? values[i] ?? "" : (i < columns.Count ? columns[i] : "") + ": " + (values[i] ?? ""));
                cells[CellColumns - 1] = string.Join(" · ", rest);
            }
            return cells;
        }

        private static Dictionary<string, object> Map(params object[] pairs)
        {
            var map = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i + 1 < pairs.Length; i += 2)
                map[(string)pairs[i]] = pairs[i + 1];
            return map;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MyPlugin
{
    /// <summary>테이블 정의서 대상 선택 창을 여는 요청(트리에서 고른 행으로 만든다).</summary>
    internal sealed class TableSpecRequest
    {
        public string DbId { get; set; }
        /// <summary>접속 사용자의 스키마(스키마 목록 맨 위). 모르면 null.</summary>
        public string MySchema { get; set; }
        /// <summary>처음 보일 스키마.</summary>
        public string InitialOwner { get; set; }
        /// <summary>처음부터 체크할 테이블·뷰(트리에서 고른 객체).</summary>
        public List<TableKey> Checked { get; } = new List<TableKey>();
        /// <summary>테이블을 모두 체크할 스키마(트리에서 스키마·테이블 묶음을 고름).</summary>
        public List<string> CheckAllTables { get; } = new List<string>();
        /// <summary>뷰를 모두 체크할 스키마(트리에서 뷰 묶음을 고름).</summary>
        public List<string> CheckAllViews { get; } = new List<string>();
        /// <summary>뷰를 고른 것이 있으면 [뷰 포함]을 켜고 연다.</summary>
        public bool IncludeViews { get; set; }
        /// <summary>스키마 목록(트리가 이미 불러온 것, 내장 스키마는 뺌). null이면 창이 불러온다.</summary>
        public List<string> Schemas { get; set; }

        /// <summary>창을 열 때 후보를 불러올 스키마(처음 스키마 먼저).</summary>
        public IEnumerable<string> OwnersToLoad
        {
            get
            {
                return new[] { InitialOwner }.Concat(Checked.Select(k => k.Owner)).Concat(CheckAllTables).Concat(CheckAllViews)
                    .Where(o => !string.IsNullOrEmpty(o)).Distinct(StringComparer.Ordinal);
            }
        }
    }

    /// <summary>
    /// [만들기…]로 마지막에 쓴 입력값(플러그인 설정 <see cref="TableSpecLogic.SettingKey"/>에 JSON). 창을 다시 열면 이 값으로 채운다.
    /// 문서 정보(표지)와 고른 양식·템플릿 파일·저장 폴더. 작성일은 "yyyy-MM-dd" 글자.
    /// </summary>
    internal sealed class TableSpecSettings
    {
        public string Title { get; set; }
        public string Project { get; set; }
        public string System { get; set; }
        public string Author { get; set; }
        public string Version { get; set; }
        public string Date { get; set; }
        public string Description { get; set; }
        /// <summary>양식 목록에서 고른 양식 id(기본 양식 "default"·"spec-sheet", 사용자 양식 "user:파일.xlsx"). 템플릿 파일을 썼어도 남긴다.</summary>
        public string TemplateId { get; set; }
        /// <summary>템플릿 파일 경로. 기본 양식을 썼으면 null.</summary>
        public string TemplatePath { get; set; }
        /// <summary>마지막으로 저장한 폴더.</summary>
        public string Folder { get; set; }
    }

    /// <summary>테이블 정의서 대상 선택 창의 글·규칙(순수 로직, 테스트 대상).</summary>
    internal static class TableSpecLogic
    {
        public const string Title = "테이블 정의서 만들기";

        /// <summary>마지막 입력값(TableSpecSettings)을 담는 플러그인 설정 키.</summary>
        public const string SettingKey = "tableSpecLast";

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        /// <summary>[만들기…] 때의 문서 정보·양식·폴더로 저장할 값을 만든다. 빈 칸은 빈 글자로 둔다(다시 열 때 빈 칸 그대로).</summary>
        public static TableSpecSettings SettingsFrom(TableSpecDocument document, string templateId, string templatePath, string folder)
        {
            document = document ?? new TableSpecDocument();
            return new TableSpecSettings
            {
                Title = document.Title ?? "",
                Project = document.Project ?? "",
                System = document.System ?? "",
                Author = document.Author ?? "",
                Version = document.Version ?? "",
                Date = document.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Description = document.Description ?? "",
                TemplateId = templateId,
                TemplatePath = string.IsNullOrWhiteSpace(templatePath) ? null : templatePath,
                Folder = string.IsNullOrWhiteSpace(folder) ? null : folder
            };
        }

        public static string SerializeSettings(TableSpecSettings settings)
        {
            return settings == null ? null : JsonSerializer.Serialize(settings, JsonOptions);
        }

        /// <summary>설정 글을 읽는다. 없거나 깨졌으면 null(창은 기본값으로 연다).</summary>
        public static TableSpecSettings ParseSettings(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;
            try
            {
                return JsonSerializer.Deserialize<TableSpecSettings>(json, JsonOptions);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// 창에 채울 문서 정보: 저장값이 있으면 그 값(빈 칸도 그대로), 없으면 기본값(제목 "테이블 정의서", 작성자 defaultAuthor, 버전 1.0).
        /// 작성일은 저장값이 날짜로 읽히면 그 날, 아니면 today.
        /// </summary>
        public static TableSpecDocument DocumentFrom(TableSpecSettings settings, string defaultAuthor, DateTime today)
        {
            if (settings == null)
                return new TableSpecDocument { Author = defaultAuthor, Date = today };
            return new TableSpecDocument
            {
                Title = settings.Title ?? TableSpecDocument.DefaultTitle,
                Project = settings.Project,
                System = settings.System,
                Author = settings.Author ?? defaultAuthor,
                Version = settings.Version ?? "1.0",
                Date = ParseDate(settings.Date) ?? today,
                Description = settings.Description
            };
        }

        // 사용자가 쓴 정규식이 너무 오래 걸리면(역추적 폭발) 멈춘다
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

        /// <summary>
        /// 트리 오른쪽 메뉴 글. 대상이 아니면 null.
        /// 누른 행이 고른 행들 안에 있고 고른 테이블·뷰가 둘 이상이면 "(선택한 N개)", 스키마는 "(스키마 전체)", 테이블·뷰 묶음은 "(테이블 전체)"·"(뷰 전체)".
        /// </summary>
        public static string MenuText(TreeRow clicked, IReadOnlyList<TreeRow> targets)
        {
            if (clicked == null)
                return null;
            var objects = (targets ?? new TreeRow[0]).Count(IsTableOrView);
            if (objects >= 2)
                return Title + " (선택한 " + objects.ToString(CultureInfo.InvariantCulture) + "개)…";
            switch (clicked.Kind)
            {
                case TreeRowKind.Database:
                    return Title + "…";
                case TreeRowKind.Schema:
                    return Title + " (스키마 전체)…";
                case TreeRowKind.Group:
                    if (clicked.ObjectType == TreeGroups.Table)
                        return Title + " (테이블 전체)…";
                    if (clicked.ObjectType == TreeGroups.View)
                        return Title + " (뷰 전체)…";
                    return null;
                case TreeRowKind.Object:
                case TreeRowKind.Column:
                    return TreeGroups.HasColumns(clicked.ObjectType) ? Title + "…" : null;
                default:
                    return null;
            }
        }

        /// <summary>
        /// 고른 행들(같은 DB)로 요청을 만든다: 테이블·뷰(열이면 그 테이블·뷰)는 체크, 스키마·테이블 묶음은 그 스키마 테이블 전부, 뷰 묶음은 뷰 전부.
        /// DB 행만 골랐으면 체크 없이 내 스키마를 보인다. 처음 스키마는 고른 것 중 첫 스키마, 없으면 내 스키마.
        /// </summary>
        public static TableSpecRequest Request(string dbId, IEnumerable<TreeRow> rows, string mySchema, IEnumerable<SchemaInfo> schemas, bool showSystem)
        {
            var request = new TableSpecRequest { DbId = dbId, MySchema = mySchema };
            foreach (var row in (rows ?? Enumerable.Empty<TreeRow>()).Where(r => r != null && r.DbId == dbId))
            {
                switch (row.Kind)
                {
                    case TreeRowKind.Schema:
                        AddOnce(request.CheckAllTables, row.Owner);
                        break;
                    case TreeRowKind.Group:
                        if (row.ObjectType == TreeGroups.Table)
                            AddOnce(request.CheckAllTables, row.Owner);
                        else if (row.ObjectType == TreeGroups.View)
                            AddOnce(request.CheckAllViews, row.Owner);
                        break;
                    case TreeRowKind.Object:
                    case TreeRowKind.Column:
                        if (!TreeGroups.HasColumns(row.ObjectType) || string.IsNullOrEmpty(row.ObjectName))
                            break;
                        var key = new TableKey(row.Owner, row.ObjectName);
                        if (!request.Checked.Contains(key))
                            request.Checked.Add(key);
                        if (row.ObjectType == "VIEW")
                            request.IncludeViews = true;
                        break;
                }
            }
            if (request.CheckAllViews.Count > 0)
                request.IncludeViews = true;
            request.InitialOwner = request.Checked.Select(k => k.Owner).Concat(request.CheckAllTables).Concat(request.CheckAllViews)
                .FirstOrDefault(o => !string.IsNullOrEmpty(o)) ?? mySchema;
            if (schemas != null)
            {
                request.Schemas = schemas.Where(s => showSystem || !s.OracleMaintained || s.Name == mySchema).Select(s => s.Name).ToList();
                foreach (var owner in request.OwnersToLoad)
                    AddOnce(request.Schemas, owner);
            }
            return request;
        }

        private static bool IsTableOrView(TreeRow row)
        {
            return row != null && row.Kind == TreeRowKind.Object && TreeGroups.HasColumns(row.ObjectType);
        }

        private static void AddOnce(List<string> list, string value)
        {
            if (!string.IsNullOrEmpty(value) && !list.Contains(value))
                list.Add(value);
        }

        /// <summary>
        /// [LLM 지침 복사]로 클립보드에 넣는 요청 글. LLM(Claude Code 등)에게 붙여 넣으면 지침을 읽고 사용자 양식 폴더에 새 양식을 만든다.
        /// baseTemplate: 바탕으로 삼을 양식 파일(사용자 양식을 골랐을 때). 없으면 기본 양식을 꺼내는 방법을 적는다.
        /// </summary>
        public static string LlmPrompt(string guideUrl, string guideMarkdownUrl, string folder, string baseTemplate)
        {
            var lines = new List<string>
            {
                "DB Helper(Folderss 플러그인)의 테이블 정의서 엑셀 양식을 만들어 줘.",
                "",
                "- 먼저 양식 작성 지침을 읽어: " + guideMarkdownUrl + " (사람용 페이지: " + guideUrl + ")",
                "- 만든 .xlsx는 이 폴더에 저장해: " + (string.IsNullOrEmpty(folder) ? "(DB Helper의 양식 폴더 — 팝업의 [양식 폴더]로 열 수 있음)" : folder),
                "  이 폴더의 .xlsx는 DB Helper의 [양식] 목록에 '내 양식 — 파일 이름'으로 보인다.",
                string.IsNullOrEmpty(baseTemplate)
                    ? "- 바탕 양식이 필요하면 지침의 예제 스크립트를 쓰거나, DB Helper에서 기본 양식을 [복사해서 고치기]로 이 폴더에 꺼낸 파일을 고쳐."
                    : "- 이 양식을 바탕으로 고쳐 새 파일로 저장해(원본은 두고): " + baseTemplate,
                "- 지침의 검증 체크리스트를 모두 지켜(지시문 짝, 지시문 셀은 줄에 혼자, 변수 이름 철자, 시트 이름 31자).",
                "",
                "원하는 양식:",
                "(여기에 원하는 모양을 적으세요 — 예: 표지에 회사 로고 자리와 결재란, 테이블마다 시트, 열 목록에 '업무 규칙' 칸 추가)"
            };
            return string.Join(Environment.NewLine, lines);
        }

        /// <summary>내 스키마를 맨 위에, 나머지는 이름 순.</summary>
        public static List<string> OrderSchemas(IEnumerable<string> schemas, string mySchema)
        {
            return (schemas ?? Enumerable.Empty<string>()).Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.Ordinal)
                .OrderBy(s => s == mySchema ? 0 : 1).ThenBy(s => s, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// 정규식 필터. 빈 글자면 null(모두 일치). 잘못된 정규식이면 null과 error("정규식 오류: …").
        /// </summary>
        public static Regex Pattern(string text, bool ignoreCase, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(text))
                return null;
            try
            {
                var options = RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
                return new Regex(text.Trim(), options, RegexTimeout);
            }
            catch (ArgumentException ex)
            {
                error = "정규식 오류: " + ex.Message;
                return null;
            }
        }

        /// <summary>이름(searchComment면 주석도)이 정규식과 일치하면 true. 정규식이 null이면 true. 시간 초과면 false.</summary>
        public static bool Matches(Regex pattern, TableSpecCandidate candidate, bool searchComment)
        {
            if (pattern == null)
                return true;
            try
            {
                return pattern.IsMatch(candidate.Name ?? "") || (searchComment && pattern.IsMatch(candidate.Comment ?? ""));
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        /// <summary>정의서 순서: 스키마 이름 순, 그 안에서 테이블 이름 순. 같은 대상은 한 번.</summary>
        public static List<TableKey> Order(IEnumerable<TableKey> keys)
        {
            return (keys ?? Enumerable.Empty<TableKey>()).Distinct()
                .OrderBy(k => k.Owner, StringComparer.Ordinal).ThenBy(k => k.Name, StringComparer.Ordinal).ToList();
        }

        /// <summary>"체크 12개 · 스키마 2개"(체크가 없으면 "체크한 테이블 없음").</summary>
        public static string SelectionText(ICollection<TableKey> keys)
        {
            if (keys == null || keys.Count == 0)
                return "체크한 테이블 없음";
            var owners = keys.Select(k => k.Owner).Distinct(StringComparer.Ordinal).Count();
            return "체크 " + keys.Count.ToString("N0", CultureInfo.InvariantCulture) + "개" + (owners > 1 ? " · 스키마 " + owners.ToString(CultureInfo.InvariantCulture) + "개" : "");
        }

        /// <summary>목록 위 글: "일치 37 / 전체 120"(필터가 없으면 "전체 120").</summary>
        public static string MatchText(int shown, int total, bool filtered)
        {
            return filtered
                ? "일치 " + shown.ToString("N0", CultureInfo.InvariantCulture) + " / 전체 " + total.ToString("N0", CultureInfo.InvariantCulture)
                : "전체 " + total.ToString("N0", CultureInfo.InvariantCulture);
        }

        /// <summary>저장 파일 기본 이름: "{문서 제목}_{DB 이름}_{스키마}_{yyyyMMdd}.xlsx"(스키마가 여럿이면 첫 스키마 외 N). 파일 이름에 못 쓰는 글자는 _.</summary>
        public static string DefaultFileName(string title, string dbName, IEnumerable<string> owners, DateTime date)
        {
            var list = (owners ?? Enumerable.Empty<string>()).Where(o => !string.IsNullOrEmpty(o)).Distinct(StringComparer.Ordinal).ToList();
            var parts = new List<string> { string.IsNullOrWhiteSpace(title) ? TableSpecDocument.DefaultTitle : title.Trim() };
            if (!string.IsNullOrWhiteSpace(dbName))
                parts.Add(dbName.Trim());
            if (list.Count == 1)
                parts.Add(list[0]);
            else if (list.Count > 1)
                parts.Add(list[0] + " 외 " + (list.Count - 1).ToString(CultureInfo.InvariantCulture));
            parts.Add(date.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }).ToArray();
            var name = new string(string.Join("_", parts).Select(c => Array.IndexOf(invalid, c) >= 0 || char.IsControl(c) ? '_' : c).ToArray());
            return name.Replace(' ', '_') + ".xlsx";
        }

        /// <summary>작성일 입력 "2026-10-10"(또는 2026.10.10, 20261010). 못 읽으면 null.</summary>
        public static DateTime? ParseDate(string text)
        {
            DateTime value;
            var formats = new[] { "yyyy-MM-dd", "yyyy.MM.dd", "yyyy/MM/dd", "yyyyMMdd", "yyyy-M-d", "yyyy.M.d" };
            return DateTime.TryParseExact((text ?? "").Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out value) ? value : (DateTime?)null;
        }

        /// <summary>다 만든 뒤 상태 글. missing은 체크했지만 찾지 못한(그 사이 지웠거나 권한 없는) 테이블 수.</summary>
        public static string DoneText(int tables, int missing, string path)
        {
            var text = "테이블 " + tables.ToString("N0", CultureInfo.InvariantCulture) + "개로 정의서를 만들었습니다: " + path;
            if (missing > 0)
                text += " (찾지 못한 " + missing.ToString(CultureInfo.InvariantCulture) + "개는 뺐습니다)";
            return text;
        }

        public static string BusyText(string dbName)
        {
            return "'" + dbName + "'에서 실행 중입니다. 끝난 뒤 다시 만드세요(DB마다 세션이 하나라 함께 쓸 수 없습니다).";
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MyPlugin
{
    /// <summary>별칭 계약 한 줄: 사용자 SQL의 결과 열 이름(별칭)과 그 뜻.</summary>
    internal sealed class CodeAlias
    {
        public CodeAlias(string name, bool required, string description, string example)
        {
            Name = name;
            Required = required;
            Description = description;
            Example = example;
        }

        public string Name { get; }
        public bool Required { get; }
        public string Description { get; }
        public string Example { get; }
    }

    /// <summary>
    /// GCM(공통 코드) 정의서의 별칭 계약. 플러그인은 사용자 SQL을 해석하지 않고, 결과 열 이름이 이 별칭과 맞는지만 본다(대소문자 무시).
    /// 필수: TABLE_NAME · CODE · CODE_NAME. 선택: GROUP_NAME · GROUP_DESC · CODE_DESC · SORT_NO · USE_YN. 그 밖의 열은 모두 추가 속성($code.attrs)으로 남긴다.
    /// 환경(테이블·열 이름)이 달라도 SQL의 AS 절만 바꾸면 되므로 코드에 GCM 테이블 이름을 두지 않는다.
    /// </summary>
    internal static class CodeSpecContract
    {
        public const string GroupId = "TABLE_NAME";
        /// <summary>옛 이름(1.3까지). 저장해 둔 SQL이 그대로 돌도록 TABLE_NAME으로 읽는다.</summary>
        public const string OldGroupId = "GROUP_ID";
        public const string Code = "CODE";
        public const string CodeName = "CODE_NAME";
        public const string GroupName = "GROUP_NAME";
        public const string GroupDesc = "GROUP_DESC";
        public const string CodeDesc = "CODE_DESC";
        public const string SortNo = "SORT_NO";
        public const string UseYn = "USE_YN";

        public static readonly IReadOnlyList<CodeAlias> Aliases = new[]
        {
            new CodeAlias(GroupId, true, "코드 그룹(코드 테이블) 식별자 — 그룹을 나누는 기준. 옛 별칭 GROUP_ID도 받음", "ORDER_STATUS"),
            new CodeAlias(Code, true, "코드 값", "A"),
            new CodeAlias(CodeName, true, "코드 명칭", "접수"),
            new CodeAlias(GroupName, false, "그룹 명칭(행마다 반복되면 첫 값)", "주문 상태"),
            new CodeAlias(GroupDesc, false, "그룹 설명(첫 값)", "주문 진행 단계"),
            new CodeAlias(CodeDesc, false, "코드 설명", "고객이 주문을 넣은 상태"),
            new CodeAlias(SortNo, false, "정렬 순서(있으면 그룹 안에서 이 값으로 정렬, 없으면 SQL 순서)", "1"),
            new CodeAlias(UseYn, false, "사용 여부(Y/N, 1/0, true/false — 비우면 사용)", "Y")
        };

        public static IEnumerable<string> RequiredNames
        {
            get { return Aliases.Where(a => a.Required).Select(a => a.Name); }
        }

        public static bool IsKnown(string column)
        {
            return Aliases.Any(a => string.Equals(a.Name, Canonical(column), StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>옛 별칭을 지금 이름으로(GROUP_ID → TABLE_NAME). 그 밖은 그대로.</summary>
        public static string Canonical(string column)
        {
            return string.Equals(column, OldGroupId, StringComparison.OrdinalIgnoreCase) ? GroupId : column;
        }

        /// <summary>결과 열 이름이 계약과 맞는지. 별칭은 대소문자 무시, 앞뒤 공백 무시. 겹치는 열 이름은 처음 것만 센다.</summary>
        public static CodeSpecCheck Check(IEnumerable<string> columns)
        {
            var check = new CodeSpecCheck();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in (columns ?? Enumerable.Empty<string>()).Select(c => (c ?? "").Trim()).Where(c => c.Length > 0))
            {
                if (!seen.Add(column))
                    continue;
                if (IsKnown(column))
                    check.Found.Add(Canonical(column).ToUpperInvariant());
                else
                    check.Extra.Add(column);
            }
            foreach (var alias in Aliases)
            {
                if (check.Found.Contains(alias.Name))
                    continue;
                if (alias.Required)
                    check.Missing.Add(alias.Name);
                else
                    check.MissingOptional.Add(alias.Name);
            }
            return check;
        }
    }

    /// <summary>별칭 검사 결과.</summary>
    internal sealed class CodeSpecCheck
    {
        /// <summary>계약 별칭 중 결과에 있는 것(대문자).</summary>
        public List<string> Found { get; } = new List<string>();
        /// <summary>빠진 필수 별칭.</summary>
        public List<string> Missing { get; } = new List<string>();
        /// <summary>빠진 선택 별칭.</summary>
        public List<string> MissingOptional { get; } = new List<string>();
        /// <summary>계약에 없는 열 — 추가 속성이 된다(결과 순서, 원래 이름).</summary>
        public List<string> Extra { get; } = new List<string>();

        public bool Ok
        {
            get { return Missing.Count == 0; }
        }

        public bool Has(string alias)
        {
            return Found.Contains(alias, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>"필수 별칭이 모두 있습니다 · 선택 3개 · 추가 속성 2개(PLANT_CD, LANG)" 또는 "빠진 필수 별칭: CODE_NAME".</summary>
        public string Summary
        {
            get
            {
                if (!Ok)
                    return "빠진 필수 별칭: " + string.Join(", ", Missing);
                var text = "필수 별칭이 모두 있습니다";
                var optional = Found.Count - CodeSpecContract.RequiredNames.Count();
                if (optional > 0)
                    text += " · 선택 " + optional.ToString(CultureInfo.InvariantCulture) + "개";
                if (Extra.Count > 0)
                    text += " · 추가 속성 " + Extra.Count.ToString(CultureInfo.InvariantCulture) + "개(" + string.Join(", ", Extra) + ")";
                return text;
            }
        }
    }

    /// <summary>코드 한 줄(사용자 SQL 결과 한 행).</summary>
    internal sealed class CodeEntry
    {
        public string GroupId { get; set; }
        public string Code { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string SortNo { get; set; }
        public string UseYn { get; set; }
        /// <summary>계약에 없는 열의 값(열 이름 → 값, 이름 대소문자 무시, 결과 열 순서).</summary>
        public Dictionary<string, string> Attrs { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>USE_YN이 비었거나 Y·1·TRUE·T·사용이면 사용.</summary>
        public bool IsUsed
        {
            get
            {
                var value = (UseYn ?? "").Trim();
                if (value.Length == 0)
                    return true;
                switch (value.ToUpperInvariant())
                {
                    case "Y":
                    case "YES":
                    case "1":
                    case "TRUE":
                    case "T":
                    case "사용":
                        return true;
                    default:
                        return false;
                }
            }
        }
    }

    /// <summary>코드 그룹(TABLE_NAME이 같은 행들, 처음 나온 순서).</summary>
    internal sealed class CodeGroup
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public List<CodeEntry> Codes { get; } = new List<CodeEntry>();
        /// <summary>이 그룹의 코드가 가진 추가 속성 이름(<see cref="CodeSpecSource.AttrNames"/> 순서). JSON 열을 펼치면 그룹마다 다르다.</summary>
        public List<string> AttrNames { get; } = new List<string>();
    }

    /// <summary>정의서에 남기는 출처: SQL 이름(파일)·본문·조회 시각·행 수·열 이름.</summary>
    internal sealed class CodeSpecSource
    {
        public string Name { get; set; }
        public string Sql { get; set; }
        public DateTime FetchedAt { get; set; } = DateTime.Now;
        public int RowCount { get; set; }
        public List<string> Columns { get; } = new List<string>();
        /// <summary>추가 속성 이름 전체: 계약에 없는 열(결과 순서) + JSON 객체 열을 펼친 키(처음 나온 순서).</summary>
        public List<string> AttrNames { get; } = new List<string>();
        /// <summary>열 대신 "이름→값"으로 펼친 열(결과 순서): 이름/값 짝 열은 "KEY_1_NAME→KEY_1", JSON 객체 열은 열 이름 그대로.</summary>
        public List<string> ExpandedColumns { get; } = new List<string>();
    }

    /// <summary>GCM 정의서 데이터(조회 결과를 그룹으로 묶은 것)와 그 출처.</summary>
    internal sealed class CodeSpecData
    {
        public CodeSpecSource Source { get; set; } = new CodeSpecSource();
        public List<CodeGroup> Groups { get; } = new List<CodeGroup>();

        public int CodeCount
        {
            get { return Groups.Sum(g => g.Codes.Count); }
        }
    }

    /// <summary>
    /// GCM(공통 코드) 정의서 모델(순수 로직, 테스트 대상): 조회 결과 → 그룹, 템플릿 변수($doc · $db · $source · $groups · $codes), JSON·CSV 출력.
    /// 값은 테이블 정의서와 같은 규칙 — 없으면 빈 글자, 숫자는 숫자, 이름은 대소문자 무시.
    /// </summary>
    internal static class CodeSpecModel
    {
        public const string DefaultTitle = "공통 코드 정의서";

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>변수 목록(이름 · 설명 · 예). 기본 양식의 안내 시트와 지침에 그대로 쓴다.</summary>
        public static readonly IReadOnlyList<TemplateVariable> Variables = new[]
        {
            new TemplateVariable("$doc.title", "문서 제목", DefaultTitle),
            new TemplateVariable("$doc.project", "프로젝트(시스템) 이름", "MES"),
            new TemplateVariable("$doc.system", "시스템/모듈", "공통"),
            new TemplateVariable("$doc.author", "작성자", "홍길동"),
            new TemplateVariable("$doc.version", "문서 버전", "1.0"),
            new TemplateVariable("$doc.date", "작성일(yyyy-MM-dd)", "2026-10-11"),
            new TemplateVariable("$doc.description", "문서 설명", ""),
            new TemplateVariable("$doc.groupCount / $doc.codeCount", "그룹 수 / 코드 수(숫자)", "12 / 180"),
            new TemplateVariable("$doc.generatedAt", "만든 시각(yyyy-MM-dd HH:mm)", "2026-10-11 14:30"),
            new TemplateVariable("$db.name", "접속 이름", "개발 DB"),
            new TemplateVariable("$db.user", "접속 사용자", "MES"),
            new TemplateVariable("$db.host / $db.port / $db.service", "호스트 · 포트 · 서비스명", "localhost / 1521 / xe"),
            new TemplateVariable("$db.address", "user@host:port/service", "mes@localhost:1521/xe"),
            new TemplateVariable("$db.version", "DBMS 제품·버전(읽지 못하면 빈 칸)", "Oracle Database 19c"),
            new TemplateVariable("$source.name", "SQL 이름(저장한 SQL 파일 이름, 없으면 빈 칸)", "GCM_운영.sql"),
            new TemplateVariable("$source.sql", "실행한 SQL 본문", "SELECT …"),
            new TemplateVariable("$source.fetchedAt", "조회 시각(yyyy-MM-dd HH:mm)", "2026-10-11 14:29"),
            new TemplateVariable("$source.rowCount", "조회한 행 수(숫자)", "180"),
            new TemplateVariable("$source.columns", "결과 열 이름(쉼표로 이음)", "TABLE_NAME, CODE, CODE_NAME, PLANT_CD"),
            new TemplateVariable("$source.attrNames", "추가 속성 열 이름 목록(#foreach($a in $source.attrNames))", ""),
            new TemplateVariable("$source.attrNamesText", "추가 속성 열 이름(쉼표로 이음)", "PLANT_CD, LANG"),
            new TemplateVariable("$source.expandedColumnsText", "열 대신 이름→값으로 펼친 열(이름/값 짝 열 'X_NAME→X', JSON 객체 열, 쉼표로 이음, 없으면 빈 칸)", "KEY_1_NAME→KEY_1, DATA_1_NAME→DATA_1"),
            new TemplateVariable("$groups", "그룹 목록(#foreach·#sheet로 돈다). $groups.size()는 개수", ""),
            new TemplateVariable("$codes", "모든 코드를 그룹 순서대로 편 목록(#foreach($code in $codes))", ""),
            new TemplateVariable("$group.no", "그룹 순번(1부터, 숫자)", "1"),
            new TemplateVariable("$group.id", "그룹 식별자(TABLE_NAME)", "ORDER_STATUS"),
            new TemplateVariable("$group.name / $group.description", "그룹 명칭 / 설명(GROUP_NAME · GROUP_DESC의 첫 값)", "주문 상태 / 주문 진행 단계"),
            new TemplateVariable("$group.sheetName", "이 그룹의 시트 이름(31자 안, 겹치지 않음) — #sheet 탭 이름·목록의 링크에 쓴다", "ORDER_STATUS"),
            new TemplateVariable("$group.codeCount", "그룹의 코드 수(숫자)", "5"),
            new TemplateVariable("$group.codes", "그룹의 코드 목록(#foreach($code in $group.codes))", ""),
            new TemplateVariable("$group.attrNames / $group.attrNamesText", "이 그룹에 있는 추가 속성 이름 목록 / 쉼표로 이은 글자(JSON 열을 펼치면 그룹마다 다름)", "AREA_CODE, COLOR_HEX_CODE"),
            new TemplateVariable("$code.no", "코드 순번(그룹 안 1부터, $codes에서는 전체 순번, 숫자)", "1"),
            new TemplateVariable("$code.group / $code.groupName", "그룹 식별자 / 그룹 명칭", "ORDER_STATUS / 주문 상태"),
            new TemplateVariable("$code.code / $code.name", "코드 값 / 코드 명칭", "A / 접수"),
            new TemplateVariable("$code.description", "코드 설명(CODE_DESC)", "고객이 주문을 넣은 상태"),
            new TemplateVariable("$code.sortNo", "정렬 순서(SORT_NO, 글자 그대로)", "1"),
            new TemplateVariable("$code.useYn / $code.isUsed", "사용 여부(USE_YN 글자 그대로) / #if에 쓰는 참·거짓(비우면 참)", "Y / true"),
            new TemplateVariable("$code.attrs.열이름", "추가 속성 값(계약에 없는 열, 이름 대소문자 무시)", "${code.attrs.PLANT_CD}"),
            new TemplateVariable("$code.attrValues", "추가 속성 값 목록($source.attrNames와 같은 순서)", ""),
            new TemplateVariable("$code.groupAttrValues", "추가 속성 값 목록($group.attrNames와 같은 순서 — 그룹 시트의 동적 열)", ""),
            new TemplateVariable("$code.attrText", "추가 속성을 '이름=값; …'으로 이은 글자", "PLANT_CD=P1; LANG=ko"),
            new TemplateVariable("$foreach.count / $foreach.index", "#foreach 안 순번(1부터) / (0부터)", "1 / 0"),
            new TemplateVariable("$foreach.hasNext / first / last", "다음 항목이 있음 / 첫 항목 / 끝 항목", "true")
        };

        // ================= 조회 결과 → 그룹 =================

        /// <summary>
        /// 조회 결과를 그룹으로 묶는다(TABLE_NAME이 같은 행, 처음 나온 순서). 그룹 명칭·설명은 그 그룹에서 처음 비어 있지 않은 값.
        /// SORT_NO 열이 있으면 그룹 안에서 그 값으로 정렬(숫자로 읽히면 수로, 아니면 글자로 — 안정 정렬), 없으면 SQL 순서.
        /// 계약에 없는 열은 추가 속성. 두 가지 펼치기 규약으로 "코드 테이블마다 다른 열 이름"을 담을 수 있다(그룹마다 속성 이름이 달라진다):
        /// - 이름/값 짝 열(모든 DBMS): 추가 열 X와 X_NAME이 함께 있으면 행마다 X_NAME의 값을 속성 이름으로, X의 값을 속성 값으로.
        ///   이름이 비어 있는데 값이 있으면 값 열 이름(대문자, 예: DATA_2)을 속성 이름으로 쓴다(DEF에 이름을 안 적은 칸의 값이 사라지지 않게). 둘 다 비면 뺀다.
        ///   예: KEY_1_NAME/KEY_1, DATA_1_NAME/DATA_1 … → 그룹마다 그 그룹의 열 이름이 속성이 된다($group.attrNames, $code.groupAttrValues).
        /// - JSON 객체 열(PostgreSQL jsonb 등): 비어 있지 않은 값이 모두 {"이름": 값, …}이면 키를 속성으로(값이 객체·배열이면 JSON 글자 그대로, null은 뺀다).
        /// 필수 별칭이 없으면 <see cref="ArgumentException"/>(먼저 <see cref="CodeSpecContract.Check"/>로 확인한다).
        /// </summary>
        public static CodeSpecData Build(QueryTable table, string sourceName, string sql)
        {
            if (table == null)
                throw new ArgumentNullException(nameof(table));
            var check = CodeSpecContract.Check(table.Columns);
            if (!check.Ok)
                throw new ArgumentException(check.Summary);
            var data = new CodeSpecData();
            data.Source.Name = sourceName ?? "";
            data.Source.Sql = sql ?? "";
            data.Source.RowCount = table.Rows.Count;
            data.Source.Columns.AddRange(table.Columns);
            data.Source.AttrNames.AddRange(check.Extra);

            var index = Index(table.Columns);
            // 이름/값 짝 열: X_NAME이 있는 X는 짝(값 열 → 이름 열). 이름 열 자체는 보통 속성이 되지 않는다
            var pairNameOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var nameColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var attr in check.Extra)
            {
                var nameColumn = check.Extra.FirstOrDefault(c => string.Equals(c, attr + "_NAME", StringComparison.OrdinalIgnoreCase));
                if (nameColumn != null)
                {
                    pairNameOf[attr] = nameColumn;
                    nameColumns.Add(nameColumn);
                }
            }
            // 추가 속성 열 중 JSON 객체 열(비어 있지 않은 값이 모두 객체이고 하나는 있음)은 키를 펼친다
            var jsonColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var attr in check.Extra)
            {
                if (pairNameOf.ContainsKey(attr) || nameColumns.Contains(attr))
                    continue;
                var any = false;
                var all = true;
                foreach (var row in table.Rows)
                {
                    var value = Value(row, index, attr);
                    if (string.IsNullOrWhiteSpace(value))
                        continue;
                    any = true;
                    List<KeyValuePair<string, string>> ignored;
                    if (!TryParseJsonObject(value, out ignored))
                    {
                        all = false;
                        break;
                    }
                }
                if (any && all)
                    jsonColumns.Add(attr);
            }
            data.Source.AttrNames.Clear();
            var attrNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var attr in check.Extra)
            {
                if (pairNameOf.ContainsKey(attr))
                    data.Source.ExpandedColumns.Add(pairNameOf[attr] + "→" + attr);
                else if (nameColumns.Contains(attr))
                    continue;
                else if (jsonColumns.Contains(attr))
                    data.Source.ExpandedColumns.Add(attr);
                else if (attrNames.Add(attr))
                    data.Source.AttrNames.Add(attr);
            }

            var groups = new Dictionary<string, CodeGroup>(StringComparer.Ordinal);
            foreach (var row in table.Rows)
            {
                var entry = new CodeEntry
                {
                    GroupId = Value(row, index, CodeSpecContract.GroupId),
                    Code = Value(row, index, CodeSpecContract.Code),
                    Name = Value(row, index, CodeSpecContract.CodeName),
                    Description = Value(row, index, CodeSpecContract.CodeDesc),
                    SortNo = Value(row, index, CodeSpecContract.SortNo),
                    UseYn = Value(row, index, CodeSpecContract.UseYn)
                };
                foreach (var attr in check.Extra)
                {
                    var value = Value(row, index, attr);
                    if (nameColumns.Contains(attr))
                        continue;
                    string nameColumn;
                    if (pairNameOf.TryGetValue(attr, out nameColumn))
                    {
                        // 이름/값 짝: 이 행의 이름 열 값이 속성 이름. 이름이 비면 값이 있을 때만 값 열 이름(DATA_2)으로 남긴다
                        var name = Value(row, index, nameColumn).Trim();
                        if (name.Length == 0)
                        {
                            if (string.IsNullOrWhiteSpace(value))
                                continue;
                            name = attr.ToUpperInvariant();
                        }
                        AddDynamicAttr(entry, name, value, attrNames, data.Source.AttrNames);
                        continue;
                    }
                    if (!jsonColumns.Contains(attr))
                    {
                        entry.Attrs[attr] = value;
                        continue;
                    }
                    List<KeyValuePair<string, string>> pairs;
                    if (string.IsNullOrWhiteSpace(value) || !TryParseJsonObject(value, out pairs))
                        continue;
                    foreach (var pair in pairs)
                        AddDynamicAttr(entry, pair.Key, pair.Value, attrNames, data.Source.AttrNames);
                }
                CodeGroup group;
                if (!groups.TryGetValue(entry.GroupId, out group))
                {
                    group = new CodeGroup { Id = entry.GroupId, Name = "", Description = "" };
                    groups.Add(entry.GroupId, group);
                    data.Groups.Add(group);
                }
                if (group.Name.Length == 0)
                    group.Name = Value(row, index, CodeSpecContract.GroupName);
                if (group.Description.Length == 0)
                    group.Description = Value(row, index, CodeSpecContract.GroupDesc);
                group.Codes.Add(entry);
            }
            if (check.Has(CodeSpecContract.SortNo))
            {
                foreach (var group in data.Groups)
                {
                    var sorted = group.Codes.Select((c, i) => new { Code = c, At = i })
                        .OrderBy(x => SortKey(x.Code.SortNo).Item1).ThenBy(x => SortKey(x.Code.SortNo).Item2).ThenBy(x => SortKey(x.Code.SortNo).Item3, StringComparer.Ordinal).ThenBy(x => x.At)
                        .Select(x => x.Code).ToList();
                    group.Codes.Clear();
                    group.Codes.AddRange(sorted);
                }
            }
            // 그룹마다 실제로 있는 속성 이름(전체 순서 유지)
            foreach (var group in data.Groups)
                group.AttrNames.AddRange(data.Source.AttrNames.Where(n => group.Codes.Any(c => c.Attrs.ContainsKey(n))));
            return data;
        }

        // 펼친 속성 하나를 넣는다. 먼저 넣은 값(보통 열 포함)이 우선하고, 이름은 전체 목록에 처음 나온 순서로 쌓인다
        private static void AddDynamicAttr(CodeEntry entry, string name, string value, HashSet<string> attrNames, List<string> allNames)
        {
            if (!entry.Attrs.ContainsKey(name))
                entry.Attrs[name] = value ?? "";
            if (attrNames.Add(name))
                allNames.Add(name);
        }

        /// <summary>
        /// "{…}" 글자가 JSON 객체면 (키, 값) 목록으로. 값: 글자는 그대로, 숫자·참거짓은 JSON 표기, null은 뺌, 객체·배열은 JSON 글자 그대로. 아니면 false.
        /// </summary>
        public static bool TryParseJsonObject(string text, out List<KeyValuePair<string, string>> pairs)
        {
            pairs = null;
            var trimmed = (text ?? "").Trim();
            if (trimmed.Length < 2 || trimmed[0] != '{' || trimmed[trimmed.Length - 1] != '}')
                return false;
            try
            {
                using (var document = JsonDocument.Parse(trimmed))
                {
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                        return false;
                    var list = new List<KeyValuePair<string, string>>();
                    foreach (var property in document.RootElement.EnumerateObject())
                    {
                        string value;
                        switch (property.Value.ValueKind)
                        {
                            case JsonValueKind.Null:
                                continue;
                            case JsonValueKind.String:
                                value = property.Value.GetString();
                                break;
                            case JsonValueKind.True:
                                value = "true";
                                break;
                            case JsonValueKind.False:
                                value = "false";
                                break;
                            default:
                                value = property.Value.GetRawText();
                                break;
                        }
                        list.Add(new KeyValuePair<string, string>(property.Name, value));
                    }
                    pairs = list;
                    return true;
                }
            }
            catch (JsonException)
            {
                return false;
            }
        }

        // 숫자로 읽히는 값이 먼저(수로), 그다음 글자(사전순), 빈 값은 맨 뒤
        private static Tuple<int, decimal, string> SortKey(string sortNo)
        {
            var text = (sortNo ?? "").Trim();
            decimal number;
            if (text.Length == 0)
                return Tuple.Create(2, 0m, "");
            if (decimal.TryParse(text, NumberStyles.Number, Inv, out number))
                return Tuple.Create(0, number, "");
            return Tuple.Create(1, 0m, text);
        }

        private static Dictionary<string, int> Index(IReadOnlyList<string> columns)
        {
            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < columns.Count; i++)
            {
                var name = CodeSpecContract.Canonical((columns[i] ?? "").Trim());
                if (name.Length > 0 && !index.ContainsKey(name))
                    index[name] = i;
            }
            return index;
        }

        private static string Value(string[] row, Dictionary<string, int> index, string column)
        {
            int at;
            if (!index.TryGetValue(column, out at) || at >= row.Length)
                return "";
            return row[at] ?? "";
        }

        /// <summary>그룹 필터: 정규식이 그룹 식별자(searchName이면 명칭·설명도)와 맞는 그룹만. 정규식이 null이면 모두.</summary>
        public static List<CodeGroup> Filter(IEnumerable<CodeGroup> groups, Regex pattern, bool searchName)
        {
            var list = (groups ?? Enumerable.Empty<CodeGroup>()).ToList();
            if (pattern == null)
                return list;
            return list.Where(g =>
            {
                try
                {
                    return pattern.IsMatch(g.Id ?? "") || (searchName && (pattern.IsMatch(g.Name ?? "") || pattern.IsMatch(g.Description ?? "")));
                }
                catch (RegexMatchTimeoutException)
                {
                    return false;
                }
            }).ToList();
        }

        // ================= 템플릿 변수 =================

        /// <summary>템플릿 변수 전체: doc · db · source · groups · codes. dbVersion은 표지의 DBMS / 버전(모르면 null).</summary>
        public static VelocityContext Context(TableSpecDocument doc, OracleConnectionProfile profile, CodeSpecData data, string dbVersion = null)
        {
            doc = doc ?? new TableSpecDocument();
            data = data ?? new CodeSpecData();
            var source = data.Source ?? new CodeSpecSource();
            var sheetNames = new List<string>();
            var groupValues = new List<object>();
            var allCodes = new List<object>();
            for (var i = 0; i < data.Groups.Count; i++)
            {
                var group = data.Groups[i];
                var sheetName = ExcelTemplate.UniqueSheetName(group.Id, sheetNames);
                sheetNames.Add(sheetName);
                var codes = new List<object>();
                foreach (var code in group.Codes)
                {
                    codes.Add(Code(code, codes.Count + 1, group, source.AttrNames));
                    allCodes.Add(Code(code, allCodes.Count + 1, group, source.AttrNames));
                }
                groupValues.Add(Map(
                    "no", i + 1,
                    "id", Text(group.Id),
                    "name", Text(group.Name),
                    "description", Text(group.Description),
                    "sheetName", sheetName,
                    "codeCount", group.Codes.Count,
                    "codes", codes,
                    "attrNames", group.AttrNames.Cast<object>().ToList(),
                    "attrNamesText", string.Join(", ", group.AttrNames)));
            }
            var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                { "doc", Map(
                    "title", Text(string.IsNullOrWhiteSpace(doc.Title) ? DefaultTitle : doc.Title.Trim()),
                    "project", Text(doc.Project),
                    "system", Text(doc.System),
                    "author", Text(doc.Author),
                    "version", Text(doc.Version),
                    "date", doc.Date.ToString("yyyy-MM-dd", Inv),
                    "description", Text(doc.Description),
                    "groupCount", data.Groups.Count,
                    "codeCount", data.CodeCount,
                    "generatedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm", Inv)) },
                { "db", Db(profile, dbVersion) },
                { "source", Map(
                    "name", Text(source.Name),
                    "sql", Text(source.Sql),
                    "fetchedAt", source.FetchedAt.ToString("yyyy-MM-dd HH:mm", Inv),
                    "rowCount", source.RowCount,
                    "columns", string.Join(", ", source.Columns),
                    "attrNames", source.AttrNames.Cast<object>().ToList(),
                    "attrNamesText", string.Join(", ", source.AttrNames),
                    "expandedColumnsText", string.Join(", ", source.ExpandedColumns)) },
                { "groups", groupValues },
                { "codes", allCodes }
            };
            return new VelocityContext(values);
        }

        private static Dictionary<string, object> Db(OracleConnectionProfile profile, string dbVersion)
        {
            return Map(
                "name", Text(profile == null ? null : profile.Name),
                "user", Text(profile == null ? null : profile.UserId),
                "host", Text(profile == null ? null : profile.Host),
                "port", profile == null ? (object)"" : profile.Port,
                "service", Text(profile == null ? null : profile.ServiceName),
                "address", profile == null ? "" : ConnectionManagerLogic.Address(profile),
                "version", Text(dbVersion));
        }

        private static Dictionary<string, object> Code(CodeEntry code, int no, CodeGroup group, IReadOnlyList<string> attrNames)
        {
            var attrs = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var attrValues = new List<object>();
            foreach (var name in attrNames)
            {
                string value;
                var text = code.Attrs.TryGetValue(name, out value) ? Text(value) : "";
                attrs[name] = text;
                attrValues.Add(text);
            }
            var groupAttrValues = new List<object>();
            foreach (var name in group.AttrNames)
            {
                string value;
                groupAttrValues.Add(code.Attrs.TryGetValue(name, out value) ? Text(value) : "");
            }
            return Map(
                "no", no,
                "group", Text(group.Id),
                "groupName", Text(group.Name),
                "code", Text(code.Code),
                "name", Text(code.Name),
                "description", Text(code.Description),
                "sortNo", Text(code.SortNo),
                "useYn", Text(code.UseYn),
                "isUsed", code.IsUsed,
                "attrs", attrs,
                "attrValues", attrValues,
                "groupAttrValues", groupAttrValues,
                "attrText", AttrText(code, attrNames));
        }

        /// <summary>"PLANT_CD=P1; LANG=ko"(값이 빈 속성은 뺀다).</summary>
        public static string AttrText(CodeEntry code, IEnumerable<string> attrNames)
        {
            var parts = new List<string>();
            foreach (var name in attrNames ?? Enumerable.Empty<string>())
            {
                string value;
                if (code.Attrs.TryGetValue(name, out value) && !string.IsNullOrEmpty(value))
                    parts.Add(name + "=" + value);
            }
            return string.Join("; ", parts);
        }

        // ================= JSON · CSV =================

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>
        /// JSON 문서: { doc, db, source, groups: [ { id, name, description, codes: [ { code, name, description, sortNo, useYn, isUsed, attrs } ] } ] }.
        /// 한글은 그대로(이스케이프 안 함), 들여쓰기. 값이 없으면 빈 글자.
        /// </summary>
        public static string Json(TableSpecDocument doc, OracleConnectionProfile profile, CodeSpecData data, string dbVersion = null)
        {
            doc = doc ?? new TableSpecDocument();
            data = data ?? new CodeSpecData();
            var source = data.Source ?? new CodeSpecSource();
            var root = new Dictionary<string, object>
            {
                { "doc", new Dictionary<string, object>
                    {
                        { "title", Text(string.IsNullOrWhiteSpace(doc.Title) ? DefaultTitle : doc.Title.Trim()) },
                        { "project", Text(doc.Project) },
                        { "system", Text(doc.System) },
                        { "author", Text(doc.Author) },
                        { "version", Text(doc.Version) },
                        { "date", doc.Date.ToString("yyyy-MM-dd", Inv) },
                        { "description", Text(doc.Description) },
                        { "groupCount", data.Groups.Count },
                        { "codeCount", data.CodeCount },
                        { "generatedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm", Inv) }
                    } },
                { "db", new Dictionary<string, object>
                    {
                        { "name", Text(profile == null ? null : profile.Name) },
                        { "user", Text(profile == null ? null : profile.UserId) },
                        { "address", profile == null ? "" : ConnectionManagerLogic.Address(profile) },
                        { "version", Text(dbVersion) }
                    } },
                { "source", new Dictionary<string, object>
                    {
                        { "name", Text(source.Name) },
                        { "sql", Text(source.Sql) },
                        { "fetchedAt", source.FetchedAt.ToString("yyyy-MM-dd HH:mm", Inv) },
                        { "rowCount", source.RowCount },
                        { "columns", source.Columns },
                        { "attrNames", source.AttrNames },
                        { "expandedColumns", source.ExpandedColumns }
                    } },
                { "groups", data.Groups.Select(g => new Dictionary<string, object>
                    {
                        { "id", Text(g.Id) },
                        { "name", Text(g.Name) },
                        { "description", Text(g.Description) },
                        { "attrNames", g.AttrNames },
                        { "codes", g.Codes.Select(c => new Dictionary<string, object>
                            {
                                { "code", Text(c.Code) },
                                { "name", Text(c.Name) },
                                { "description", Text(c.Description) },
                                { "sortNo", Text(c.SortNo) },
                                { "useYn", Text(c.UseYn) },
                                { "isUsed", c.IsUsed },
                                // 그룹에 있는 속성만(JSON 열을 펼치면 그룹마다 다르다)
                                { "attrs", g.AttrNames.ToDictionary(a => a, a => { string v; return c.Attrs.TryGetValue(a, out v) ? Text(v) : ""; }, StringComparer.Ordinal) }
                            }).ToList() }
                    }).ToList() }
            };
            return JsonSerializer.Serialize(root, JsonOptions);
        }

        /// <summary>CSV 머리글: 계약 별칭(고정 순서) + 추가 속성 열(결과 순서).</summary>
        public static List<string> CsvHeader(CodeSpecData data)
        {
            var header = new List<string> { CodeSpecContract.GroupId, CodeSpecContract.GroupName, CodeSpecContract.GroupDesc, CodeSpecContract.Code,
                CodeSpecContract.CodeName, CodeSpecContract.CodeDesc, CodeSpecContract.SortNo, CodeSpecContract.UseYn };
            if (data != null && data.Source != null)
                header.AddRange(data.Source.AttrNames);
            return header;
        }

        /// <summary>
        /// CSV 글(머리글 + 코드마다 한 줄, 그룹 순서). 줄 끝 CRLF. 쉼표·따옴표·줄바꿈이 든 값은 따옴표로 감싼다(따옴표는 두 번).
        /// 파일로 쓸 때는 UTF-8 BOM을 붙여 엑셀이 한글을 바로 읽게 한다(<see cref="CsvBytes"/>).
        /// </summary>
        public static string Csv(CodeSpecData data)
        {
            data = data ?? new CodeSpecData();
            var attrNames = data.Source == null ? new List<string>() : data.Source.AttrNames;
            var text = new StringBuilder();
            text.Append(string.Join(",", CsvHeader(data).Select(CsvField))).Append("\r\n");
            foreach (var group in data.Groups)
            {
                foreach (var code in group.Codes)
                {
                    var fields = new List<string> { group.Id, group.Name, group.Description, code.Code, code.Name, code.Description, code.SortNo, code.UseYn };
                    foreach (var name in attrNames)
                    {
                        string value;
                        fields.Add(code.Attrs.TryGetValue(name, out value) ? value : "");
                    }
                    text.Append(string.Join(",", fields.Select(CsvField))).Append("\r\n");
                }
            }
            return text.ToString();
        }

        public static byte[] CsvBytes(CodeSpecData data)
        {
            return new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(Csv(data))).ToArray();
        }

        public static string CsvField(string value)
        {
            var text = value ?? "";
            if (text.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
                return text;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }

        // ================= 도우미 =================

        private static string Text(string value)
        {
            return value ?? "";
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

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MyPlugin
{
    /// <summary>정의서 표지에 쓰는 문서 정보(대상 선택 창에서 입력).</summary>
    internal sealed class TableSpecDocument
    {
        public const string DefaultTitle = "테이블 정의서";

        public string Title { get; set; } = DefaultTitle;
        public string Project { get; set; }
        /// <summary>시스템/모듈(명세서형 표지).</summary>
        public string System { get; set; }
        public string Author { get; set; }
        public string Version { get; set; } = "1.0";
        public DateTime Date { get; set; } = DateTime.Today;
        public string Description { get; set; }
    }

    /// <summary>템플릿 변수 하나의 설명(기본 템플릿의 안내 시트·README).</summary>
    internal sealed class TemplateVariable
    {
        public TemplateVariable(string name, string description, string example)
        {
            Name = name;
            Description = description;
            Example = example;
        }

        public string Name { get; }
        public string Description { get; }
        public string Example { get; }
    }

    /// <summary>
    /// 테이블 정의서 템플릿 변수(순수 로직, 테스트 대상). 모든 값은 사전(이름 대소문자 무시)·목록·글자·숫자다.
    /// 값이 없으면 null 대신 빈 글자를 넣는다 — 템플릿에 $!를 쓰지 않아도 빈 칸이 된다(값이 없는 참조를 그대로 남기는 것은 오타일 때만).
    /// 숫자(no, numRows, columnCount 등)는 숫자 그대로라 엑셀에서도 숫자 셀이 된다. 통계가 없는 numRows는 빈 글자.
    /// Oracle에는 논리명이 따로 없어 주석을 나눠 쓴다(SplitComment): 첫 줄(또는 ':'·'|'·' - ' 앞, "이름(설명)"의 이름)이 논리명, 나머지가 설명.
    /// </summary>
    internal static class TableSpecModel
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // 검사 조건 "STATUS IN ('A','B')" — 공통 코드 후보
        private static readonly Regex InList = new Regex(@"^\s*""?([A-Za-z_][A-Za-z0-9_$#]*)""?\s+IN\s*\((.*)\)\s*$", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

        /// <summary>변수 목록(이름 · 설명 · 예). 기본 템플릿의 안내 시트와 문서에 그대로 쓴다.</summary>
        public static readonly IReadOnlyList<TemplateVariable> Variables = new[]
        {
            new TemplateVariable("$doc.title", "문서 제목", "테이블 정의서"),
            new TemplateVariable("$doc.project", "프로젝트(시스템) 이름", "주문 관리 시스템"),
            new TemplateVariable("$doc.system", "시스템/모듈", "주문 · 결제"),
            new TemplateVariable("$doc.author", "작성자", "홍길동"),
            new TemplateVariable("$doc.version", "문서 버전", "1.0"),
            new TemplateVariable("$doc.date", "작성일 = 스키마 기준일(yyyy-MM-dd)", "2026-10-10"),
            new TemplateVariable("$doc.description", "문서 설명", ""),
            new TemplateVariable("$doc.schemas", "대상 스키마(쉼표로 이음)", "SCOTT, HR"),
            new TemplateVariable("$doc.tableCount", "대상 테이블 수(숫자)", "12"),
            new TemplateVariable("$doc.generatedAt", "만든 시각(yyyy-MM-dd HH:mm)", "2026-10-10 14:30"),
            new TemplateVariable("$db.name", "접속 이름", "개발 DB"),
            new TemplateVariable("$db.user", "접속 사용자", "SCOTT"),
            new TemplateVariable("$db.host / $db.port / $db.service", "호스트 · 포트 · 서비스명", "localhost / 1521 / xe"),
            new TemplateVariable("$db.address", "user@host:port/service", "scott@localhost:1521/xe"),
            new TemplateVariable("$db.version", "DBMS 제품·버전(읽지 못하면 빈 칸)", "Oracle Database 12c Enterprise Edition 12.1.0.2.0"),
            new TemplateVariable("$tables", "대상 테이블 목록(#foreach·#sheet로 돈다). $tables.size()는 개수", ""),
            new TemplateVariable("$codes", "검사 조건 'COL IN (…)'에서 찾은 공통 코드 후보(#foreach($code in $codes))", ""),
            new TemplateVariable("$table.no", "순번(1부터, 숫자)", "1"),
            new TemplateVariable("$table.owner", "스키마", "SCOTT"),
            new TemplateVariable("$table.name", "테이블 이름(물리명)", "EMP"),
            new TemplateVariable("$table.qualifiedName", "스키마.이름", "SCOTT.EMP"),
            new TemplateVariable("$table.sheetName", "이 테이블의 시트 이름(31자 안, 겹치지 않음) — #sheet 탭 이름·목록의 링크에 쓴다", "EMP"),
            new TemplateVariable("$table.comment", "테이블 주석 전체", "사원: 재직 중인 직원"),
            new TemplateVariable("$table.logicalName / $table.description", "주석을 나눈 논리명 / 설명", "사원 / 재직 중인 직원"),
            new TemplateVariable("$table.type / $table.typeLabel", "TABLE·VIEW / 테이블·뷰", "TABLE / 테이블"),
            new TemplateVariable("$table.status", "상태(VALID·INVALID)", "VALID"),
            new TemplateVariable("$table.numRows / $table.numRowsText", "통계의 행 수(숫자, 없으면 빈 칸) / 1,000 꼴 글자", "14 / 14"),
            new TemplateVariable("$table.lastAnalyzed", "통계 날짜", "2026-10-01 09:00"),
            new TemplateVariable("$table.created / $table.lastDdl", "만든 날 / 마지막 DDL", "2026-01-02 10:00"),
            new TemplateVariable("$table.tablespace", "테이블스페이스", "USERS"),
            new TemplateVariable("$table.pkName / $table.pkColumns", "기본 키 이름 / 열(쉼표로 이음)", "PK_EMP / EMPNO"),
            new TemplateVariable("$table.columnCount", "열 수(숫자)", "8"),
            new TemplateVariable("$table.columns", "열 목록(#foreach($col in $table.columns))", ""),
            new TemplateVariable("$table.indexes", "인덱스 목록(#foreach($idx in $table.indexes))", ""),
            new TemplateVariable("$table.constraints", "제약 조건 목록(#foreach($con in $table.constraints))", ""),
            new TemplateVariable("$table.foreignKeys / $table.checks", "외래 키만(R) / 검사 조건만(C)", ""),
            new TemplateVariable("$col.no", "열 순서(COLUMN_ID, 숫자)", "1"),
            new TemplateVariable("$col.name", "열 이름(물리명)", "EMPNO"),
            new TemplateVariable("$col.comment", "열 주석 전체", "사원 번호"),
            new TemplateVariable("$col.logicalName / $col.description", "주석을 나눈 논리명 / 설명", "상태 / 1=재직, 2=퇴직"),
            new TemplateVariable("$col.typeLabel", "형식 전체", "VARCHAR2(10 CHAR)"),
            new TemplateVariable("$col.dataType / $col.length", "형식 이름 / 길이(괄호 안)", "VARCHAR2 / 10 CHAR"),
            new TemplateVariable("$col.nullable / $col.notNull", "NULL 허용 Y·N / NOT NULL이면 Y", "N / Y"),
            new TemplateVariable("$col.pk / $col.pkOrder", "기본 키면 Y / 기본 키 안 순서(숫자)", "Y / 1"),
            new TemplateVariable("$col.fk / $col.fkRef", "외래 키면 Y / 가리키는 열", "Y / SCOTT.DEPT.DEPTNO"),
            new TemplateVariable("$col.key", "키 표시 PK·FK·UK·IX(겹치면 쉼표)", "PK,FK"),
            new TemplateVariable("$col.isPk / $col.isFk / $col.isNullable", "#if에 쓰는 참·거짓", "true"),
            new TemplateVariable("$col.default", "기본값", "SYSDATE"),
            new TemplateVariable("$idx.no / $idx.name", "순번 / 인덱스 이름", "1 / PK_EMP"),
            new TemplateVariable("$idx.columns", "열(순서대로, DESC 포함)", "EMPNO"),
            new TemplateVariable("$idx.unique / $idx.uniqueness", "고유면 Y / UNIQUE·NONUNIQUE", "Y / UNIQUE"),
            new TemplateVariable("$idx.kind", "유형 PK·UNIQUE·INDEX", "PK"),
            new TemplateVariable("$idx.type / $idx.status", "종류 / 상태", "NORMAL / VALID"),
            new TemplateVariable("$con.no / $con.name", "순번 / 제약 조건 이름", "1 / FK_DEPTNO"),
            new TemplateVariable("$con.type / $con.typeLabel", "P·U·R·C / 기본 키·고유·외래 키·검사", "R / 외래 키"),
            new TemplateVariable("$con.columns", "열(쉼표로 이음)", "DEPTNO"),
            new TemplateVariable("$con.reference / $con.referenceColumns", "외래 키가 가리키는 테이블 / 열", "SCOTT.DEPT / DEPTNO"),
            new TemplateVariable("$con.target", "외래 키 참조 대상(테이블.열, 열이 여럿이면 테이블(열, 열))", "SCOTT.DEPT.DEPTNO"),
            new TemplateVariable("$con.deleteRule / $con.updateRule", "외래 키 삭제 시 / 갱신 시(Oracle은 늘 NO ACTION)", "CASCADE / NO ACTION"),
            new TemplateVariable("$con.condition", "검사 조건", "SAL > 0"),
            new TemplateVariable("$con.status", "상태(ENABLED·DISABLED)", "ENABLED"),
            new TemplateVariable("$code.group / $code.code / $code.meaning", "코드 그룹(열 이름) / 코드 값 / 의미(빈 칸 — 직접 적는다)", "STATUS / A / "),
            new TemplateVariable("$code.usage / $code.constraint", "사용 테이블.열 / 검사 조건 이름", "EMP.STATUS / CK_EMP_STATUS"),
            new TemplateVariable("$foreach.count / $foreach.index", "#foreach 안 순번(1부터) / (0부터)", "1 / 0"),
            new TemplateVariable("$foreach.hasNext / first / last", "다음 항목이 있음 / 첫 항목 / 끝 항목", "true")
        };

        /// <summary>템플릿 변수 전체: doc · db · tables · codes. dbVersion은 표지의 DBMS / 버전(모르면 null).</summary>
        public static VelocityContext Context(TableSpecDocument doc, OracleConnectionProfile profile, IReadOnlyList<TableDescription> tables, string dbVersion = null)
        {
            doc = doc ?? new TableSpecDocument();
            var list = tables ?? new List<TableDescription>();
            var sheetNames = new List<string>();
            var tableValues = new List<object>();
            for (var i = 0; i < list.Count; i++)
            {
                var sheetName = ExcelTemplate.UniqueSheetName(list[i].Name, sheetNames);
                sheetNames.Add(sheetName);
                tableValues.Add(Table(list[i], i + 1, sheetName));
            }
            var schemas = list.Select(t => t.Owner).Where(o => !string.IsNullOrEmpty(o)).Distinct(StringComparer.Ordinal).ToList();
            var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                { "doc", Map(
                    "title", Text(string.IsNullOrWhiteSpace(doc.Title) ? TableSpecDocument.DefaultTitle : doc.Title.Trim()),
                    "project", Text(doc.Project),
                    "system", Text(doc.System),
                    "author", Text(doc.Author),
                    "version", Text(doc.Version),
                    "date", doc.Date.ToString("yyyy-MM-dd", Inv),
                    "description", Text(doc.Description),
                    "schemas", string.Join(", ", schemas),
                    "tableCount", list.Count,
                    "generatedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm", Inv)) },
                { "db", Map(
                    "name", Text(profile == null ? null : profile.Name),
                    "user", Text(profile == null ? null : profile.UserId),
                    "host", Text(profile == null ? null : profile.Host),
                    "port", profile == null ? (object)"" : profile.Port,
                    "service", Text(profile == null ? null : profile.ServiceName),
                    "address", profile == null ? "" : ConnectionManagerLogic.Address(profile),
                    "version", Text(dbVersion)) },
                { "tables", tableValues },
                { "codes", Codes(list) }
            };
            return new VelocityContext(values);
        }

        private static Dictionary<string, object> Table(TableDescription info, int no, string sheetName)
        {
            var primary = info.Constraints.FirstOrDefault(c => c.Type == "P");
            var foreignKeys = info.Constraints.Where(c => c.Type == "R").ToList();
            var keys = new KeyColumns(info, primary);
            var columns = new List<object>();
            foreach (var column in info.Columns)
                columns.Add(Column(column, primary, foreignKeys, keys));
            var indexes = new List<object>();
            foreach (var index in info.Indexes)
            {
                indexes.Add(Map(
                    "no", indexes.Count + 1,
                    "name", Text(index.Name),
                    "columns", Text(index.ColumnNames.Count > 0 ? string.Join(", ", index.ColumnNames) : index.Columns),
                    "unique", index.Unique ? "Y" : "",
                    "uniqueness", index.Unique ? "UNIQUE" : "NONUNIQUE",
                    "kind", IndexKind(info, primary, index),
                    "type", Text(index.Type),
                    "status", Text(index.Status)));
            }
            var constraints = new List<object>();
            foreach (var constraint in info.Constraints)
                constraints.Add(Constraint(constraint, constraints.Count + 1));
            var isView = string.Equals(info.Type, "VIEW", StringComparison.Ordinal);
            string logicalName;
            string description;
            SplitComment(info.Comment, out logicalName, out description);
            return Map(
                "no", no,
                "owner", Text(info.Owner),
                "name", Text(info.Name),
                "qualifiedName", string.IsNullOrEmpty(info.Owner) ? Text(info.Name) : info.Owner + "." + info.Name,
                "sheetName", sheetName,
                "comment", Text(info.Comment),
                "logicalName", logicalName,
                "description", description,
                "type", Text(info.Type),
                "typeLabel", isView ? "뷰" : "테이블",
                "status", Text(info.Status),
                "numRows", info.NumRows.HasValue ? (object)info.NumRows.Value : "",
                "numRowsText", info.NumRows.HasValue ? info.NumRows.Value.ToString("N0", Inv) : "",
                "lastAnalyzed", Time(info.LastAnalyzed),
                "created", Time(info.Created),
                "lastDdl", Time(info.LastDdl),
                "tablespace", Text(info.Tablespace),
                "pkName", primary == null ? "" : Text(primary.Name),
                "pkColumns", primary == null ? "" : string.Join(", ", Names(primary)),
                "columnCount", info.Columns.Count,
                "columns", columns,
                "indexes", indexes,
                "constraints", constraints,
                "foreignKeys", constraints.Where(c => (string)((Dictionary<string, object>)c)["type"] == "R").ToList(),
                "checks", constraints.Where(c => (string)((Dictionary<string, object>)c)["type"] == "C").ToList());
        }

        private static Dictionary<string, object> Column(DescribedColumn described, ConstraintDescription primary, List<ConstraintDescription> foreignKeys, KeyColumns keys)
        {
            var column = described.Column ?? new ColumnInfo();
            string dataType;
            string length;
            SplitType(column.TypeLabel, out dataType, out length);
            string logicalName;
            string description;
            SplitComment(described.Comment, out logicalName, out description);
            var pkOrder = primary == null ? 0 : Names(primary).IndexOf(column.Name) + 1;
            var isPk = pkOrder > 0 || (primary == null && column.PrimaryKey);
            var references = new List<string>();
            foreach (var fk in foreignKeys)
            {
                var at = Names(fk).IndexOf(column.Name);
                if (at < 0)
                    continue;
                var target = fk.Reference ?? "";
                if (at < fk.ReferenceColumns.Count)
                    target += (target.Length > 0 ? "." : "") + fk.ReferenceColumns[at];
                references.Add(target);
            }
            return Map(
                "no", column.Position,
                "name", Text(column.Name),
                "comment", Text(described.Comment),
                "logicalName", logicalName,
                "description", description,
                "typeLabel", Text(column.TypeLabel),
                "dataType", dataType,
                "length", length,
                "nullable", column.Nullable ? "Y" : "N",
                "notNull", column.Nullable ? "" : "Y",
                "isNullable", column.Nullable,
                "pk", isPk ? "Y" : "",
                "pkOrder", pkOrder > 0 ? (object)pkOrder : "",
                "isPk", isPk,
                "fk", references.Count > 0 ? "Y" : "",
                "fkRef", string.Join(", ", references),
                "isFk", references.Count > 0,
                "key", keys.Label(column.Name, isPk, references.Count > 0),
                "default", Text(described.Default));
        }

        private static Dictionary<string, object> Constraint(ConstraintDescription constraint, int no)
        {
            var isForeign = constraint.Type == "R";
            return Map(
                "no", no,
                "name", Text(constraint.Name),
                "type", Text(constraint.Type),
                "typeLabel", TableInfo.ConstraintTypeLabel(constraint.Type),
                "columns", string.Join(", ", Names(constraint)),
                "reference", Text(constraint.Reference),
                "referenceColumns", string.Join(", ", constraint.ReferenceColumns),
                "target", isForeign ? Target(constraint) : "",
                "deleteRule", isForeign ? Text(constraint.DeleteRule ?? "NO ACTION") : "",
                // Oracle 외래 키에는 ON UPDATE가 없다 — 부모 키를 바꾸면 늘 막힌다(NO ACTION)
                "updateRule", isForeign ? "NO ACTION" : "",
                "condition", Text(constraint.Condition),
                "indexName", Text(constraint.IndexName),
                "status", Text(constraint.Status));
        }

        // 외래 키 참조 대상: 열이 하나면 "SCOTT.DEPT.DEPTNO", 여럿이면 "SCOTT.DEPT(A, B)"
        private static string Target(ConstraintDescription constraint)
        {
            var table = constraint.Reference ?? "";
            if (constraint.ReferenceColumns.Count == 1)
                return table + (table.Length > 0 ? "." : "") + constraint.ReferenceColumns[0];
            return constraint.ReferenceColumns.Count == 0 ? table : table + "(" + string.Join(", ", constraint.ReferenceColumns) + ")";
        }

        // 인덱스 유형: 기본 키가 쓰는 인덱스면 PK, 고유면 UNIQUE, 아니면 INDEX
        private static string IndexKind(TableDescription info, ConstraintDescription primary, IndexDescription index)
        {
            if (primary != null && (string.Equals(primary.IndexName, index.Name, StringComparison.Ordinal)
                || (primary.IndexName == null && index.Unique && Names(primary).SequenceEqual(IndexColumns(index)))))
                return "PK";
            return index.Unique || info.Constraints.Any(c => c.Type == "U" && string.Equals(c.IndexName, index.Name, StringComparison.Ordinal)) ? "UNIQUE" : "INDEX";
        }

        /// <summary>열마다 PK·FK·UK·IX 표시를 정하는 데 쓰는 고유·인덱스 열 모음.</summary>
        private sealed class KeyColumns
        {
            private readonly HashSet<string> _unique = new HashSet<string>(StringComparer.Ordinal);
            private readonly HashSet<string> _indexed = new HashSet<string>(StringComparer.Ordinal);

            public KeyColumns(TableDescription info, ConstraintDescription primary)
            {
                foreach (var constraint in info.Constraints.Where(c => c.Type == "U"))
                    _unique.UnionWith(Names(constraint));
                foreach (var index in info.Indexes)
                {
                    if (IndexKind(info, primary, index) == "PK")
                        continue;
                    if (index.Unique)
                        _unique.UnionWith(IndexColumns(index));
                    else
                        _indexed.UnionWith(IndexColumns(index));
                }
            }

            /// <summary>"PK", "PK,FK", "FK,IX", "UK" 등(없으면 "").</summary>
            public string Label(string column, bool isPk, bool isFk)
            {
                var parts = new List<string>();
                if (isPk)
                    parts.Add("PK");
                if (isFk)
                    parts.Add("FK");
                if (!isPk && _unique.Contains(column ?? ""))
                    parts.Add("UK");
                if (!isPk && _indexed.Contains(column ?? ""))
                    parts.Add("IX");
                return string.Join(",", parts);
            }
        }

        private static List<string> IndexColumns(IndexDescription index)
        {
            var names = index.ColumnNames.Count > 0 ? index.ColumnNames : (index.Columns ?? "").Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries).ToList();
            return names.Select(n => n.EndsWith(" DESC", StringComparison.Ordinal) ? n.Substring(0, n.Length - 5) : n).ToList();
        }

        /// <summary>
        /// 공통 코드 후보: 검사 조건이 "열 IN ('A','B')" 꼴이면 값마다 한 줄(코드 그룹 = 열 이름, 의미는 빈 칸 — 사람이 채운다). 테이블 순서대로.
        /// </summary>
        public static List<object> Codes(IEnumerable<TableDescription> tables)
        {
            var codes = new List<object>();
            foreach (var table in tables ?? Enumerable.Empty<TableDescription>())
            {
                foreach (var constraint in table.Constraints.Where(c => c.Type == "C" && !string.IsNullOrEmpty(c.Condition)))
                {
                    string column;
                    List<string> values;
                    if (!ParseInList(constraint.Condition, out column, out values))
                        continue;
                    foreach (var value in values)
                    {
                        codes.Add(Map(
                            "no", codes.Count + 1,
                            "group", column,
                            "code", value,
                            "meaning", "",
                            "usage", table.Name + "." + column,
                            "table", Text(table.Name),
                            "column", column,
                            "constraint", Text(constraint.Name)));
                    }
                }
            }
            return codes;
        }

        /// <summary>"STATUS IN ('A','B')"·"\"KIND\" in (1, 2)" → STATUS, [A, B]. 값의 따옴표는 뺀다('' → '). 꼴이 다르면 false.</summary>
        public static bool ParseInList(string condition, out string column, out List<string> values)
        {
            column = null;
            values = new List<string>();
            var match = InList.Match(condition ?? "");
            if (!match.Success)
                return false;
            column = match.Groups[1].Value.ToUpperInvariant();
            // 따옴표 밖의 쉼표로 나눈다
            var body = match.Groups[2].Value;
            var tokens = new List<string>();
            var current = new StringBuilder();
            var quoted = false;
            foreach (var c in body)
            {
                if (c == '\'')
                    quoted = !quoted;
                if (c == ',' && !quoted)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }
            if (quoted)
                return false;
            tokens.Add(current.ToString());
            foreach (var token in tokens.Select(t => t.Trim()))
            {
                if (token.Length >= 2 && token[0] == '\'' && token[token.Length - 1] == '\'')
                    values.Add(token.Substring(1, token.Length - 2).Replace("''", "'"));
                else if (token.Length > 0)
                    values.Add(token);
            }
            return values.Count > 0;
        }

        /// <summary>
        /// 주석을 논리명과 설명으로 나눈다: 여러 줄이면 첫 줄 / 나머지, 한 줄이면 첫 ':'·'|'·' - ' 앞 / 뒤, "이름(설명)"이면 이름 / 괄호 안,
        /// 나눌 곳이 없으면 주석 전체 / "". 예: "상태: 1=활성, 2=휴면" → 상태 / 1=활성, 2=휴면 · "사용 여부(Y/N)" → 사용 여부 / Y/N.
        /// </summary>
        public static void SplitComment(string comment, out string logicalName, out string description)
        {
            var text = (comment ?? "").Trim();
            logicalName = text;
            description = "";
            if (text.Length == 0)
                return;
            var newline = text.IndexOfAny(new[] { '\r', '\n' });
            if (newline > 0)
            {
                logicalName = text.Substring(0, newline).Trim();
                description = text.Substring(newline).Trim();
                return;
            }
            foreach (var separator in new[] { ":", "|", " - " })
            {
                var at = text.IndexOf(separator, StringComparison.Ordinal);
                if (at > 0)
                {
                    logicalName = text.Substring(0, at).Trim();
                    description = text.Substring(at + separator.Length).Trim();
                    return;
                }
            }
            var open = text.IndexOf('(');
            if (open > 0 && text.EndsWith(")", StringComparison.Ordinal))
            {
                logicalName = text.Substring(0, open).Trim();
                description = text.Substring(open + 1, text.Length - open - 2).Trim();
            }
        }

        /// <summary>
        /// 형식 전체를 이름과 괄호 안으로 나눈다: "VARCHAR2(10 CHAR)" → "VARCHAR2", "10 CHAR" · "NUMBER(7,2)" → "NUMBER", "7,2" ·
        /// "TIMESTAMP(6) WITH TIME ZONE" → "TIMESTAMP WITH TIME ZONE", "6" · "DATE" → "DATE", "". 첫 괄호만 본다.
        /// </summary>
        public static void SplitType(string typeLabel, out string dataType, out string length)
        {
            var text = (typeLabel ?? "").Trim();
            var open = text.IndexOf('(');
            var close = open < 0 ? -1 : text.IndexOf(')', open);
            if (close < 0)
            {
                dataType = text;
                length = "";
                return;
            }
            length = text.Substring(open + 1, close - open - 1).Trim();
            dataType = (text.Substring(0, open).TrimEnd() + " " + text.Substring(close + 1).TrimStart()).Trim();
        }

        // 제약 조건 열: 정의서 조회는 ColumnNames를 채운다. F4 조회 결과처럼 없으면 "A, B"를 나눈다.
        private static List<string> Names(ConstraintDescription constraint)
        {
            if (constraint.ColumnNames.Count > 0)
                return constraint.ColumnNames;
            return (constraint.Columns ?? "").Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        private static string Text(string value)
        {
            return value ?? "";
        }

        private static string Time(DateTime? value)
        {
            return value.HasValue ? value.Value.ToString("yyyy-MM-dd HH:mm", Inv) : "";
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

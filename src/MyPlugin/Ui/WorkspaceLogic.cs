using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MyPlugin
{
    /// <summary>탭 닫기 메뉴의 범위: 이 탭 · 다른 탭 모두 · 왼쪽 탭 모두 · 오른쪽 탭 모두.</summary>
    internal enum TabCloseScope
    {
        This,
        Others,
        Left,
        Right
    }

    /// <summary>
    /// SQL 작업 영역(SqlWorkspace)의 화면과 무관한 규칙(순수 로직, 테스트 대상):
    /// 탭 이름, 줄·열 표시, 식별자 따옴표·SELECT 문장, 상태줄·메시지 문장, 기록에서 넣을 문장, TSV, 결과 칸 표시, 열 너비 어림.
    /// </summary>
    internal static class WorkspaceLogic
    {
        public const string TabTitlePrefix = "SQL ";

        /// <summary>실행 기록 최대 개수(메모리에만).</summary>
        public const int HistoryLimit = 100;

        /// <summary>메시지 탭에 남기는 최대 줄 수(오래된 것부터 버림).</summary>
        public const int MessageLimit = 1000;

        public const double MinColumnWidth = 48;
        public const double MaxColumnWidth = 320;

        private const double CharWidth = 7.2;
        private const double CellPadding = 18;
        private const int TooltipLineWidth = 100;
        private const int TooltipMaxChars = 2000;

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly char[] LineBreaks = { '\r', '\n' };
        private static readonly char[] TsvBreakers = { '\t', '\r', '\n' };

        // Oracle SQL 예약어(SQL Language Reference 부록). 따옴표 없이 쓰면 식별자로 읽히지 않는다.
        private static readonly HashSet<string> ReservedWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "ACCESS", "ADD", "ALL", "ALTER", "AND", "ANY", "AS", "ASC", "AUDIT", "BETWEEN", "BY", "CHAR", "CHECK", "CLUSTER",
            "COLUMN", "COLUMN_VALUE", "COMMENT", "COMPRESS", "CONNECT", "CREATE", "CURRENT", "DATE", "DECIMAL", "DEFAULT", "DELETE",
            "DESC", "DISTINCT", "DROP", "ELSE", "EXCLUSIVE", "EXISTS", "FILE", "FLOAT", "FOR", "FROM", "GRANT", "GROUP", "HAVING",
            "IDENTIFIED", "IMMEDIATE", "IN", "INCREMENT", "INDEX", "INITIAL", "INSERT", "INTEGER", "INTERSECT", "INTO", "IS",
            "LEVEL", "LIKE", "LOCK", "LONG", "MAXEXTENTS", "MINUS", "MLSLABEL", "MODE", "MODIFY", "NESTED_TABLE_ID", "NOAUDIT",
            "NOCOMPRESS", "NOT", "NOWAIT", "NULL", "NUMBER", "OF", "OFFLINE", "ON", "ONLINE", "OPTION", "OR", "ORDER", "PCTFREE",
            "PRIOR", "PUBLIC", "RAW", "RENAME", "RESOURCE", "REVOKE", "ROW", "ROWID", "ROWNUM", "ROWS", "SELECT", "SESSION", "SET",
            "SHARE", "SIZE", "SMALLINT", "START", "SUCCESSFUL", "SYNONYM", "SYSDATE", "TABLE", "THEN", "TO", "TRIGGER", "UID",
            "UNION", "UNIQUE", "UPDATE", "USER", "VALIDATE", "VALUES", "VARCHAR", "VARCHAR2", "VIEW", "WHENEVER", "WHERE", "WITH"
        };

        // ---------- 탭 ----------

        public static string TabTitle(int number)
        {
            return TabTitlePrefix + number.ToString(Inv);
        }

        /// <summary>
        /// 새 탭 번호: start 이상에서 "SQL n" 제목이 아직 없는 가장 작은 n.
        /// 호출자는 돌려받은 번호 + 1을 다음 start로 기억한다(닫은 탭 번호를 다시 쓰면 메시지·기록의 탭 이름이 헷갈린다).
        /// </summary>
        public static int NextTabNumber(IEnumerable<string> existingTitles, int start)
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            if (existingTitles != null)
            {
                foreach (var title in existingTitles)
                {
                    if (title != null)
                        used.Add(title);
                }
            }
            var n = Math.Max(1, start);
            while (used.Contains(TabTitle(n)))
                n++;
            return n;
        }

        /// <summary>
        /// 탭 닫기 메뉴로 닫을 탭 위치(앞에서 뒤로). 탭이 하나뿐이거나 index가 범위 밖이면 빈 목록(작업 영역은 탭을 하나 이상 둔다).
        /// </summary>
        public static List<int> TabsToClose(int count, int index, TabCloseScope scope)
        {
            var result = new List<int>();
            if (count <= 1 || index < 0 || index >= count)
                return result;
            for (var i = 0; i < count; i++)
            {
                var hit = scope == TabCloseScope.This ? i == index
                    : scope == TabCloseScope.Others ? i != index
                    : scope == TabCloseScope.Left ? i < index
                    : i > index;
                if (hit)
                    result.Add(i);
            }
            return result;
        }

        /// <summary>＋로 만든 탭의 첫 줄.</summary>
        public static string NewTabText(string profileName)
        {
            return "-- 대상: " + (string.IsNullOrEmpty(profileName) ? "(없음)" : profileName) + "\n";
        }

        /// <summary>"대상" 목록의 항목 글자.</summary>
        public static string TargetItemText(string name, bool connected, bool readOnly)
        {
            return (name ?? "") + (connected ? "" : " (연결 안 됨)") + (readOnly ? " · 읽기 전용" : "");
        }

        // ---------- 편집기 ----------

        /// <summary>줄 수. CRLF·LF·CR을 각각 줄바꿈 하나로 센다. 빈 텍스트도 1줄.</summary>
        public static int CountLines(string text)
        {
            if (string.IsNullOrEmpty(text))
                return 1;
            var lines = 1;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '\n')
                {
                    lines++;
                }
                else if (c == '\r')
                {
                    lines++;
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                        i++;
                }
            }
            return lines;
        }

        /// <summary>줄 번호 칸의 텍스트 "1\n2\n…\ncount".</summary>
        public static string LineNumbers(int count)
        {
            count = Math.Max(1, count);
            var sb = new StringBuilder(count * 4);
            for (var i = 1; i <= count; i++)
            {
                if (i > 1)
                    sb.Append('\n');
                sb.Append(i.ToString(Inv));
            }
            return sb.ToString();
        }

        /// <summary>캐럿 위치의 줄·열(1부터). 열은 줄 시작부터 센 글자 수 + 1(탭도 한 글자).</summary>
        public static void CaretLineColumn(string text, int caret, out int line, out int column)
        {
            text = text ?? "";
            caret = Math.Max(0, Math.Min(caret, text.Length));
            line = 1;
            var lineStart = 0;
            for (var i = 0; i < caret; i++)
            {
                var c = text[i];
                if (c == '\n')
                {
                    line++;
                    lineStart = i + 1;
                }
                else if (c == '\r')
                {
                    var crlf = i + 1 < text.Length && text[i + 1] == '\n';
                    if (!crlf)
                    {
                        line++;
                        lineStart = i + 1;
                    }
                    else if (i + 1 < caret)
                    {
                        line++;
                        lineStart = i + 2;
                        i++;
                    }
                }
            }
            column = caret - lineStart + 1;
        }

        /// <summary>선택한 글자 수. CRLF는 사용자에게 줄바꿈 하나이므로 한 글자로 센다.</summary>
        public static int SelectedCharCount(string text, int start, int length)
        {
            text = text ?? "";
            start = Math.Max(0, Math.Min(start, text.Length));
            var end = Math.Max(start, Math.Min(text.Length, start + Math.Max(0, length)));
            var count = end - start;
            for (var i = start; i + 1 < end; i++)
            {
                if (text[i] == '\r' && text[i + 1] == '\n')
                    count--;
            }
            return count;
        }

        /// <summary>편집기 바의 위치 표시: "줄 n, 열 m" + 선택이 있으면 " · 선택 k자".</summary>
        public static string CursorInfo(string text, int caret, int selectionStart, int selectionLength)
        {
            int line, column;
            CaretLineColumn(text, caret, out line, out column);
            return CursorInfo(line, column, SelectedCharCount(text, selectionStart, selectionLength));
        }

        /// <summary>편집기가 아는 줄·열(1부터)과 선택 글자 수(CRLF는 한 글자)로 — 문서 전체를 훑지 않는다.</summary>
        public static string CursorInfo(int line, int column, int selected)
        {
            // 줄·열에는 천 단위 쉼표를 넣지 않는다("줄 1,234, 열 5"는 읽기 어렵다)
            var info = "줄 " + line.ToString(Inv) + ", 열 " + column.ToString(Inv);
            if (selected > 0)
                info += " · 선택 " + selected.ToString("N0", Inv) + "자";
            return info;
        }

        /// <summary>줄바꿈을 newline 하나로 맞춘다(WPF TextBox는 Enter로 Environment.NewLine을 넣는다).</summary>
        public static string NormalizeNewlines(string text, string newline)
        {
            if (string.IsNullOrEmpty(text))
                return text ?? "";
            var sb = new StringBuilder(text.Length + 16);
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '\r')
                {
                    sb.Append(newline);
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                        i++;
                }
                else if (c == '\n')
                {
                    sb.Append(newline);
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        // ---------- 문장 ----------

        /// <summary>메시지·기록에 보일 첫 줄: 비어 있지 않은 첫 줄(앞뒤 공백 제거, 탭은 공백). maxLength를 넘으면 잘라 "…".</summary>
        public static string FirstLine(string sql, int maxLength = 80)
        {
            if (string.IsNullOrEmpty(sql))
                return "";
            foreach (var raw in sql.Split(LineBreaks))
            {
                var line = raw.Replace('\t', ' ').Trim();
                if (line.Length == 0)
                    continue;
                return Truncate(line, maxLength);
            }
            return "";
        }

        /// <summary>
        /// 읽기 전용 접속에서 실행해도 되는 문장: SELECT·WITH와 COMMIT·ROLLBACK.
        /// SELECT … FOR UPDATE는 행 잠금을 잡아 다른 세션을 막으므로 뺀다.
        /// </summary>
        public static bool AllowedOnReadOnly(SqlStatement statement)
        {
            if (statement == null)
                return false;
            if (statement.Kind == SqlKind.Query)
                return !statement.ForUpdate;
            return statement.Kind == SqlKind.Transaction
                && (statement.TransactionAction == SqlTransactionAction.Commit || statement.TransactionAction == SqlTransactionAction.Rollback);
        }

        /// <summary>
        /// SELECT에 넣을 식별자. 대문자로 시작하는 단순 이름(^[A-Z][A-Z0-9_$#]*$)이고 예약어가 아니면 그대로,
        /// 아니면 큰따옴표로 감싸고 안의 "는 두 번 쓴다(소문자·공백·한글 이름도 데이터 사전 그대로 찾는다).
        /// </summary>
        public static string QuoteIdentifier(string name)
        {
            name = name ?? "";
            if (IsSimpleIdentifier(name) && !ReservedWords.Contains(name))
                return name;
            return "\"" + name.Replace("\"", "\"\"") + "\"";
        }

        public static string QualifiedName(string owner, string name)
        {
            return string.IsNullOrEmpty(owner) ? QuoteIdentifier(name) : QuoteIdentifier(owner) + "." + QuoteIdentifier(name);
        }

        /// <summary>트리 [SELECT 문 넣기]·Enter로 편집기 맨 위에 넣는 문장(빠른 조회 F1도 이 문장). FETCH FIRST는 넣지 않는다(넣으면 다음 행을 가져올 수 없음).</summary>
        public static string SelectStatement(string owner, string name)
        {
            return "SELECT *\n  FROM " + QualifiedName(owner, name) + ";\n\n";
        }

        /// <summary>
        /// 기록을 눌러 편집기 맨 위에 넣을 텍스트: 실행한 문장 + 끝 구분자 + 빈 줄.
        /// 블록(PL/SQL — EXEC를 바꾼 BEGIN … END; 포함, SQL Server CREATE PROCEDURE 등)은 구분 줄(Oracle '/', SQL Server GO)로 끝내야 한다
        /// — ';'로 끝내면 아래 문장까지 블록으로 읽힌다. rules는 그 탭 대상 DB의 규칙(null이면 Oracle).
        /// </summary>
        public static string HistoryInsertText(string sql, SqlLexRules rules = null)
        {
            rules = rules ?? SqlLexRules.Oracle;
            sql = (sql ?? "").Trim();
            var parsed = SqlScript.Parse(sql, rules);
            var block = parsed != null && parsed.IsPlSqlBlock && rules.BlockEndLine != null;
            return sql + (block ? "\n" + rules.BlockEndLine + "\n\n" : ";\n\n");
        }

        // ---------- 시간 ----------

        /// <summary>결과·기록의 실행 시간: "0.35".</summary>
        public static string Seconds(TimeSpan elapsed)
        {
            return Math.Max(0, elapsed.TotalSeconds).ToString("0.00", Inv);
        }

        /// <summary>실행 중 표시: "1.2".</summary>
        public static string SecondsShort(TimeSpan elapsed)
        {
            return Math.Max(0, elapsed.TotalSeconds).ToString("0.0", Inv);
        }

        public static string Count(int value)
        {
            return value.ToString("N0", Inv);
        }

        // ---------- 메시지 ----------

        public static string ExecutedMessage(string summary, TimeSpan elapsed, string sql)
        {
            return (summary ?? "실행함") + " · " + Seconds(elapsed) + "초 · " + FirstLine(sql);
        }

        public static string ErrorMessage(string described, string sql)
        {
            var line = FirstLine(sql);
            return described + ErrorHint(described) + (line.Length > 0 ? " · " + line : "");
        }

        public static string FetchErrorMessage(string described)
        {
            return "다음 행을 가져오지 못했습니다: " + described + ErrorHint(described);
        }

        /// <summary>오류 문장에 덧붙일 안내. ORA-01555는 커서를 오래 열어 두었을 때 난다.</summary>
        public static string ErrorHint(string described)
        {
            if (described != null && described.IndexOf("ORA-01555", StringComparison.Ordinal) >= 0)
                return " — 커서를 연 뒤 시간이 지나 그 사이 바뀐 데이터를 읽을 수 없습니다. 다시 실행하세요.";
            return "";
        }

        public static string FetchedMessage(int count, int from, int to, TimeSpan elapsed, bool last)
        {
            if (count <= 0)
                return "더 가져올 행이 없습니다 (커서 닫힘).";
            return "다음 " + Count(count) + "행 가져옴 (" + Count(from) + "–" + Count(to) + ") · " + Seconds(elapsed) + "초"
                + (last ? " · 마지막 행" : "");
        }

        public static string ReadOnlyBlockedMessage(string sql)
        {
            return "읽기 전용 접속이라 실행하지 않았습니다: " + FirstLine(sql);
        }

        public static string TargetChangedMessage(string tabTitle, string name)
        {
            return "'" + tabTitle + "' 탭의 대상을 " + (string.IsNullOrEmpty(name) ? "(없음)" : name) + "(으)로 바꿨습니다.";
        }

        public static string TargetRemovedMessage(string tabTitle)
        {
            return "'" + tabTitle + "' 탭의 대상 접속이 삭제되어 대상을 비웠습니다.";
        }

        /// <summary>"SELECT 만듦: SCOTT.EMP" + 다른 탭으로 옮겼으면 " → 'SQL 2' 탭(대상 개발)".</summary>
        public static string SelectMadeMessage(string qualifiedName, bool moved, string tabTitle, string name)
        {
            return "SELECT 만듦: " + qualifiedName + (moved ? MovedSuffix(tabTitle, name) : "");
        }

        public static string HistoryInsertedMessage(string sql, string tabTitle, string name)
        {
            return "기록에서 넣음: " + FirstLine(sql, 60) + MovedSuffix(tabTitle, name);
        }

        public static string CopiedMessage(int rows)
        {
            return Count(rows) + "행을 TSV로 복사했습니다(머리글 포함).";
        }

        // ---- 빠른 조회(트리 F1) ----

        /// <summary>트리 F1 빠른 조회로 처음 가져올 행 수.</summary>
        public const int QuickQueryRows = 100;

        public static string QuickQueryMessage(string qualifiedName, string tabTitle, string where = null)
        {
            return "빠른 조회: " + qualifiedName + (string.IsNullOrEmpty(where) ? "" : " WHERE " + where) + " 앞 " + QuickQueryRows.ToString(Inv)
                + "행 → '" + tabTitle + "' 탭 (편집기는 그대로, 실행 기록에 남음)";
        }

        /// <summary>
        /// 빠른 조회 SELECT. 조건이 있으면 "WHERE (조건)"으로 괄호에 가둔다 — 조건은 <see cref="QuickQueryWhere"/>로 검사한 것이어야 한다.
        /// rowId면(Oracle, 기본 키·고유 키가 없는 테이블) 끝에 ROWID를 글로 붙인다: "SELECT 스키마.테이블.*, ROWIDTOCHAR(ROWID) AS "DBH$ROWID"".
        /// 테이블 별칭을 두지 않아 WHERE 조건의 "테이블.열"도 그대로 쓸 수 있다. 붙인 열은 결과 편집이 그리드에 보이기 전에 뗀다.
        /// </summary>
        public static string QuickQueryStatement(string owner, string name, string where, bool rowId = false)
        {
            if (string.IsNullOrEmpty(where) && !rowId)
                return SelectStatement(owner, name);
            var table = QualifiedName(owner, name);
            var select = rowId ? "SELECT " + table + ".*, ROWIDTOCHAR(ROWID) AS \"" + RowEditLogic.RowIdAlias + "\"" : "SELECT *";
            return select + "\n  FROM " + table + (string.IsNullOrEmpty(where) ? "" : "\n WHERE (" + where + ")");
        }

        /// <summary>
        /// 편집 막대의 WHERE 칸 검사. 앞의 "WHERE"는 떼고 앞뒤 공백을 지운 조건을 돌려준다(비었으면 "" — 조건 없이 조회).
        /// 조건은 괄호 안에 넣어 실행하므로, 괄호를 벗어나 문장을 바꿀 수 있는 것을 막는다:
        /// 문자열·따옴표 이름 밖의 ';'·주석(--, /*)·짝이 안 맞는 괄호·닫히지 않은 문자열과 따옴표 이름,
        /// DB마다 해석이 달라 검사를 속일 수 있는 것(대괄호, 접두가 붙은 문자열 q'·E'·U&amp;' 등 — N'만 허용, '$'로 시작하는 달러 인용).
        /// 막으면 null(error에 이유).
        /// </summary>
        public static string QuickQueryWhere(string input, out string error)
        {
            error = null;
            var text = (input ?? "").Trim();
            if (text.Length >= 5 && string.Compare(text, 0, "WHERE", 0, 5, StringComparison.OrdinalIgnoreCase) == 0
                && (text.Length == 5 || char.IsWhiteSpace(text[5])))
                text = text.Substring(5).Trim();
            var depth = 0;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '\'' || c == '"')
                {
                    var prefix = WordBefore(text, i);
                    if (c == '\'' && ((i > 0 && text[i - 1] == '&') || (prefix.Length > 0 && !string.Equals(prefix, "N", StringComparison.OrdinalIgnoreCase))))
                    {
                        error = "접두가 붙은 문자열(q'·E'·U&' 등)은 쓸 수 없습니다. 보통 문자열 '…'을 쓰세요.";
                        return null;
                    }
                    var end = i + 1;
                    while (true)
                    {
                        end = text.IndexOf(c, end);
                        if (end < 0)
                        {
                            error = c == '\'' ? "닫히지 않은 문자열(')이 있습니다." : "닫히지 않은 따옴표 이름(\")이 있습니다.";
                            return null;
                        }
                        if (end + 1 < text.Length && text[end + 1] == c)
                        {
                            end += 2;
                            continue;
                        }
                        break;
                    }
                    i = end;
                    continue;
                }
                if (c == ';')
                    error = "';'는 쓸 수 없습니다(조건 하나만 넣으세요).";
                else if ((c == '-' && i + 1 < text.Length && text[i + 1] == '-') || (c == '/' && i + 1 < text.Length && text[i + 1] == '*'))
                    error = "주석(--, /*)은 쓸 수 없습니다.";
                else if (c == '[' || c == ']')
                    error = "대괄호([ ])는 쓸 수 없습니다. 이름은 큰따옴표(\"…\")로 감싸세요.";
                else if (c == '$' && !IsIdentifierStart(WordBefore(text, i)))
                    error = "'$'로 시작하는 인용·변수는 쓸 수 없습니다.";
                else if (c == '(')
                    depth++;
                else if (c == ')' && --depth < 0)
                    error = "짝이 없는 ')'가 있습니다.";
                if (error != null)
                    return null;
            }
            if (depth != 0)
            {
                error = "닫히지 않은 '('가 있습니다.";
                return null;
            }
            return text;
        }

        // text[i] 바로 앞에 붙은 낱말(글자·숫자·_·$·#). 없으면 "".
        private static string WordBefore(string text, int i)
        {
            var start = i;
            while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_' || text[start - 1] == '$' || text[start - 1] == '#'))
                start--;
            return text.Substring(start, i - start);
        }

        // 이름 안의 '$'(V$SESSION)만 허용: 글자나 _로 시작하는 낱말 뒤여야 한다(숫자 뒤·단독 '$'는 PostgreSQL 달러 인용·변수).
        private static bool IsIdentifierStart(string word)
        {
            return word.Length > 0 && (char.IsLetter(word[0]) || word[0] == '_');
        }

        // ---- 여러 문장 실행(결과 하위 탭) ----

        /// <summary>한 번 실행에서 그리드로 남기는 최대 조회 결과 수. 넘는 조회는 커서를 바로 닫는다(서버의 열린 커서 수를 아끼려고).</summary>
        public const int MaxResultSets = 20;

        private static readonly System.Text.RegularExpressions.Regex FromTarget = new System.Text.RegularExpressions.Regex(
            @"\bFROM\s+((?:""[^""]+""|[A-Za-z_][\w$#]*)(?:\s*\.\s*(?:""[^""]+""|[A-Za-z_][\w$#]*))?)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>조회의 첫 FROM 대상(예: "SAMPLE1.AUDIT_LOG"). 주석·문자열 안은 보지 않는다. 없으면(DUAL도 포함해 그대로) null.</summary>
        public static string ResultSource(string sql)
        {
            if (string.IsNullOrEmpty(sql))
                return null;
            var match = FromTarget.Match(StripCommentsAndStrings(sql));
            // "sample1 . dept" → "sample1.dept"(따옴표 이름 안의 공백은 그대로)
            return match.Success ? System.Text.RegularExpressions.Regex.Replace(match.Groups[1].Value, @"\s*\.\s*", ".") : null;
        }

        /// <summary>결과 하위 탭 이름: "결과 2 · SAMPLE1.ATTACHMENT (5행)", 더 있으면 "(200행+)".</summary>
        public static string ResultTabTitle(int number, string sql, int rows, bool hasMore)
        {
            var source = ResultSource(sql);
            return "결과 " + number.ToString(Inv) + (source != null ? " · " + source : "") + " (" + Count(rows) + "행" + (hasMore ? "+" : "") + ")";
        }

        /// <summary>여러 문장 실행의 메시지 머리: "[2/5] ".</summary>
        public static string ScriptStep(int index, int total)
        {
            return "[" + index.ToString(Inv) + "/" + total.ToString(Inv) + "] ";
        }

        /// <summary>여러 문장 실행이 끝났을 때 메시지 한 줄.</summary>
        public static string ScriptSummary(int executed, int total, int queries, int failedAt, bool cancelled, TimeSpan elapsed)
        {
            var head = failedAt > 0 ? failedAt.ToString(Inv) + "번째 문장에서 오류로 멈췄습니다"
                : cancelled ? "취소해 멈췄습니다"
                : "스크립트 실행을 마쳤습니다";
            return head + " — 문장 " + total.ToString(Inv) + "개 중 " + executed.ToString(Inv) + "개 실행, 조회 결과 "
                + queries.ToString(Inv) + "개 · " + Seconds(elapsed) + "초";
        }

        /// <summary>여러 문장 실행 뒤 상태줄 앞머리: "문장 5/5 · 결과 2개 —" (오류·취소면 그 표시).</summary>
        public static string ScriptLead(int executed, int total, int results, int failedAt, bool cancelled)
        {
            var lead = "문장 " + executed.ToString(Inv) + "/" + total.ToString(Inv) + " · 결과 " + results.ToString(Inv) + "개";
            if (failedAt > 0)
                lead += " (" + failedAt.ToString(Inv) + "번째에서 오류)";
            else if (cancelled)
                lead += " (취소됨)";
            return lead + " —";
        }

        public static string TooManyResultsMessage(int index)
        {
            return index.ToString(Inv) + "번째 문장의 결과는 보이지 않습니다(한 번에 결과 " + MaxResultSets.ToString(Inv) + "개까지). 커서를 닫았습니다.";
        }

        // 주석과 문자열 안의 글자를 공백으로 바꾼다(FROM 찾기용)
        private static string StripCommentsAndStrings(string sql)
        {
            var chars = sql.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (chars[i] == '-' && i + 1 < chars.Length && chars[i + 1] == '-')
                {
                    while (i < chars.Length && chars[i] != '\n')
                        chars[i++] = ' ';
                }
                else if (chars[i] == '/' && i + 1 < chars.Length && chars[i + 1] == '*')
                {
                    var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    end = end < 0 ? chars.Length : end + 2;
                    for (; i < end; i++)
                        chars[i] = ' ';
                    i--;
                }
                else if (chars[i] == '\'')
                {
                    chars[i++] = ' ';
                    while (i < chars.Length && chars[i] != '\'')
                        chars[i++] = ' ';
                    if (i < chars.Length)
                        chars[i] = ' ';
                }
            }
            return new string(chars);
        }

        // ---- 결과 정렬(열 머리 누르기) ----

        /// <summary>
        /// 결과 칸 값 비교(오름차순 기준). NULL(null)은 맨 뒤 — Oracle ORDER BY 기본(ASC NULLS LAST)과 같다.
        /// 숫자 열은 수로 비교하고(수로 읽히지 않는 값은 수 뒤), 그 밖은 서수 비교(Oracle 기본 BINARY 정렬과 같은 순서).
        /// </summary>
        public static int CompareCells(string a, string b, bool numeric)
        {
            if (a == null || b == null)
                return a == null ? (b == null ? 0 : 1) : -1;
            if (numeric)
            {
                decimal da, db;
                var isDecA = decimal.TryParse(a, NumberStyles.Float, Inv, out da);
                var isDecB = decimal.TryParse(b, NumberStyles.Float, Inv, out db);
                if (isDecA && isDecB)
                    return da.CompareTo(db);
                // decimal 범위를 넘는 수(1E+40 등)·BINARY_DOUBLE의 Infinity·NaN
                double xa, xb;
                var isNumA = double.TryParse(a, NumberStyles.Float, Inv, out xa);
                var isNumB = double.TryParse(b, NumberStyles.Float, Inv, out xb);
                if (isNumA && isNumB)
                    return xa.CompareTo(xb);
                if (isNumA != isNumB)
                    return isNumA ? -1 : 1;
            }
            return string.CompareOrdinal(a, b);
        }

        /// <summary>
        /// 결과 두 행 비교: column 열 값으로(descending이면 뒤집음 — NULL이 맨 앞, Oracle DESC NULLS FIRST와 같다).
        /// 같으면 원래 순서(행 번호)를 지킨다(안정 정렬).
        /// </summary>
        public static int CompareRows(string[] a, int numberA, string[] b, int numberB, int column, bool numeric, bool descending)
        {
            var va = a != null && column >= 0 && column < a.Length ? a[column] : null;
            var vb = b != null && column >= 0 && column < b.Length ? b[column] : null;
            var result = CompareCells(va, vb, numeric);
            if (descending)
                result = -result;
            return result != 0 ? result : numberA.CompareTo(numberB);
        }

        /// <summary>열 머리 누르기 순환: 정렬 안 함 → 오름차순 → 내림차순 → 정렬 안 함. 다른 열을 누르면 그 열 오름차순.</summary>
        public static void NextSort(int currentColumn, bool currentDescending, int clickedColumn, out int column, out bool descending)
        {
            if (clickedColumn != currentColumn || currentColumn < 0)
            {
                column = clickedColumn;
                descending = false;
            }
            else if (!currentDescending)
            {
                column = currentColumn;
                descending = true;
            }
            else
            {
                column = -1;
                descending = false;
            }
        }

        // 정렬 표시(" ▲")가 차지하는 칸(열 너비 어림용)
        private const double SortMarkUnits = 2;

        /// <summary>정렬한 열 머리에 붙이는 표시.</summary>
        public static string SortMark(bool descending)
        {
            return descending ? " ▼" : " ▲";
        }

        /// <summary>더 가져올 행이 남은 결과를 정렬했을 때(결과마다 한 번).</summary>
        public static string SortPartialMessage(int fetched)
        {
            return "가져온 " + Count(fetched) + "행 안에서만 정렬했습니다. 더 가져온 행도 정렬 위치에 들어가지만, 전체 결과의 순서는 ORDER BY로 정하세요.";
        }

        private static string MovedSuffix(string tabTitle, string name)
        {
            return " → '" + tabTitle + "' 탭(대상 " + (string.IsNullOrEmpty(name) ? "없음" : name) + ")";
        }

        // ---------- 상태줄 ----------

        public enum StatusAction
        {
            None,
            /// <summary>[연결]: 대상 DB에 연결</summary>
            Connect,
            /// <summary>[다시 연결]: 끊긴 세션을 버리고 다시 연결</summary>
            Reconnect,
            /// <summary>[다음 n행 가져오기]: 열린 커서에서 이어 읽기</summary>
            FetchNext
        }

        /// <summary>상태줄 글자 한 조각. NewPart면 앞 조각과 간격을 두고 새 덩어리로 시작한다.</summary>
        public sealed class StatusSegment
        {
            public StatusSegment(string text, bool strong, bool newPart)
            {
                Text = text ?? "";
                Strong = strong;
                NewPart = newPart;
            }

            public string Text { get; }
            public bool Strong { get; }
            public bool NewPart { get; }
        }

        /// <summary>탭마다 기억하는 상태줄 내용(왼쪽 글자 + 오른쪽 동작 버튼). 다음 행 버튼의 글자는 그릴 때 정한다(가져올 행 수가 바뀔 수 있음).</summary>
        public sealed class StatusInfo
        {
            public List<StatusSegment> Segments { get; } = new List<StatusSegment>();

            public StatusAction Action { get; set; }

            /// <summary>Connect·Reconnect 버튼 글자.</summary>
            public string ActionText { get; set; }

            /// <summary>열린 커서·가져온 행을 보여 주는 상태(커서가 닫히면 다시 그려야 함).</summary>
            public bool ShowsCursor { get; set; }

            /// <summary>새 덩어리를 덧붙인다.</summary>
            public StatusInfo Part(string text, bool strong = false)
            {
                Segments.Add(new StatusSegment(text, strong, Segments.Count > 0));
                return this;
            }

            /// <summary>앞 덩어리에 이어 붙인다(간격 없이).</summary>
            public StatusInfo Then(string text, bool strong = false)
            {
                Segments.Add(new StatusSegment(text, strong, false));
                return this;
            }

            /// <summary>평문(덩어리 사이는 공백 두 칸). 툴팁·시험용.</summary>
            public string PlainText
            {
                get
                {
                    var sb = new StringBuilder();
                    foreach (var segment in Segments)
                    {
                        if (segment.NewPart)
                            sb.Append("  ");
                        sb.Append(segment.Text);
                    }
                    return sb.ToString();
                }
            }
        }

        public static StatusInfo ReadyStatus()
        {
            return TextStatus("준비");
        }

        public static StatusInfo TextStatus(string text)
        {
            return new StatusInfo().Part(text);
        }

        public static StatusInfo NoTargetStatus()
        {
            return TextStatus("이 탭의 대상 DB가 없습니다. 편집기 위 [대상]에서 고르세요.");
        }

        public static StatusInfo NoStatementStatus()
        {
            return TextStatus("실행할 문장이 없습니다. 커서를 문장 안에 두거나 실행할 부분을 선택하세요.");
        }

        public static StatusInfo NotConnectedStatus(string name)
        {
            var status = TextStatus("대상 DB(" + name + ")에 연결되어 있지 않습니다.");
            status.Action = StatusAction.Connect;
            status.ActionText = "연결";
            return status;
        }

        public static StatusInfo BrokenStatus(string name)
        {
            var status = TextStatus("대상 DB(" + name + ")의 연결이 끊겼습니다. 다시 연결하세요.");
            status.Action = StatusAction.Reconnect;
            status.ActionText = "다시 연결";
            return status;
        }

        /// <summary>끊긴 세션에서 실행할 때 다시 연결할지 묻는 글. 커밋 대기가 있었으면 이미 서버에서 사라졌다고 덧붙인다.</summary>
        public static string ReconnectQuestion(string name, string pendingText)
        {
            var question = "대상 DB(" + name + ")의 연결이 끊겼습니다. 다시 연결할까요?";
            return string.IsNullOrEmpty(pendingText)
                ? question
                : question + Environment.NewLine + "커밋하지 않은 변경(" + pendingText + ")은 서버에서 이미 사라졌습니다.";
        }

        public static StatusInfo ConnectedStatus(bool reconnected)
        {
            return TextStatus(reconnected ? "다시 연결됨 — 다시 실행하세요." : "연결됨 — 다시 실행하세요.");
        }

        public static StatusInfo ReadOnlyStatus()
        {
            return TextStatus("읽기 전용 접속에서는 SELECT·WITH만 실행합니다(FOR UPDATE 제외).");
        }

        /// <summary>같은 DB의 다른 탭이 실행 중(DB마다 세션 1개).</summary>
        public static StatusInfo BusyStatus(string name)
        {
            return TextStatus("같은 DB(" + name + ")에서 다른 탭이 실행 중입니다. 끝난 뒤 실행하세요 (DB마다 세션 1개).");
        }

        /// <summary>탭은 아니지만 같은 세션을 쓰는 다른 작업(트리 불러오기·커밋 등)이 진행 중.</summary>
        public static StatusInfo BusyOtherStatus(string name)
        {
            return TextStatus("같은 DB(" + name + ")에서 다른 작업이 진행 중입니다. 잠시 뒤 다시 실행하세요.");
        }

        /// <summary>다음 행을 가져오려는데 세션이 바쁨. 버튼은 남겨 끝난 뒤 다시 누를 수 있게 한다.</summary>
        public static StatusInfo BusyFetchStatus(string name, bool otherTab)
        {
            var status = TextStatus(otherTab
                ? "같은 DB(" + name + ")에서 다른 탭이 실행 중입니다. 끝난 뒤 가져오세요."
                : "같은 DB(" + name + ")에서 다른 작업이 진행 중입니다. 잠시 뒤 가져오세요.");
            status.Action = StatusAction.FetchNext;
            status.ShowsCursor = true;
            return status;
        }

        /// <param name="progress">여러 문장 실행이면 "2/5"(몇 번째 문장 / 모두). 아니면 null.</param>
        public static StatusInfo RunningStatus(TimeSpan elapsed, bool cancelling, string progress = null)
        {
            var status = new StatusInfo().Part(cancelling ? "취소하는 중… " : "실행 중… ");
            if (!string.IsNullOrEmpty(progress))
                status.Then("(" + progress + ") ");
            status.Then(SecondsShort(elapsed) + "초", true);
            if (!cancelling)
                status.Part("[취소]로 멈출 수 있습니다.");
            return status;
        }

        public static StatusInfo FetchingStatus(int count, TimeSpan elapsed, bool cancelling)
        {
            var status = new StatusInfo()
                .Part(cancelling ? "취소하는 중… " : "다음 " + Count(count) + "행 가져오는 중… ")
                .Then(SecondsShort(elapsed) + "초", true);
            if (!cancelling)
                status.Part("열어 둔 커서에서 이어서 읽습니다(다시 조회하지 않음).");
            return status;
        }

        /// <summary>
        /// 조회 결과 상태: "{n}행 표시 중" · "{초}초" · ("아직 더 있음 · 커서 열림" | "마지막 행까지 가져옴 · 커서 닫힘" | "커서 닫힘") · "끝의 ; 는 빼고 실행함".
        /// closedEarly: 더 있었는데 대상 변경·끊기·취소 등으로 닫혔음. lead: 앞에 붙일 알림(예: "취소됨").
        /// </summary>
        public static StatusInfo FetchStatus(int fetched, TimeSpan? elapsed, bool hasMore, bool closedEarly, bool strippedSemicolon, string lead = null)
        {
            var status = new StatusInfo { Action = StatusAction.FetchNext, ShowsCursor = true };
            if (!string.IsNullOrEmpty(lead))
                status.Part(lead);
            status.Part(Count(fetched) + "행", true).Then(" 표시 중");
            if (elapsed.HasValue)
                status.Part(Seconds(elapsed.Value) + "초");
            if (hasMore)
                status.Part("아직 더 있음 · 커서 열림");
            else
                status.Part((fetched > 0 && !closedEarly ? "마지막 행까지 가져옴 · " : "") + "커서 닫힘");
            if (strippedSemicolon)
                status.Part("끝의 ; 는 빼고 실행함");
            return status;
        }

        /// <summary>상태줄 오른쪽 버튼 글자.</summary>
        public static string FetchButtonText(bool hasMore, int fetchCount)
        {
            return hasMore ? "다음 " + Count(fetchCount) + "행 가져오기" : "더 가져올 행 없음";
        }

        /// <summary>조회가 아닌 문장의 결과 상태: 요약(굵게) · 초 · 종류별 안내. 경고(컴파일 오류 등)가 있으면 안내 대신 오류 상태.</summary>
        public static StatusInfo ExecutedStatus(SqlStatement statement, ExecuteResult result, TimeSpan elapsed)
        {
            var status = new StatusInfo().Part(result != null && !string.IsNullOrEmpty(result.Summary) ? result.Summary : "실행함", true)
                .Part(Seconds(elapsed) + "초");
            // 실행은 됐어도 결과가 쓸 수 없는 상태(INVALID 객체 등)면 성공처럼 보이지 않게
            if (result != null && !string.IsNullOrEmpty(result.Warning))
                return status.Part("오류 — 메시지 탭을 보세요.");
            var note = ExecutedNote(statement, result);
            if (note != null)
                status.Part(note);
            return status;
        }

        private static string ExecutedNote(SqlStatement statement, ExecuteResult result)
        {
            if (statement == null)
                return null;
            if (statement.TransactionAction == SqlTransactionAction.Commit || statement.TransactionAction == SqlTransactionAction.Rollback)
                return "같은 DB의 모든 탭에 적용됩니다.";
            // PL/SQL은 블록이 커밋·롤백했거나 아무것도 바꾸지 않았으면 TransactionEnded(남은 트랜잭션 없음)
            var ended = result != null && result.TransactionEnded;
            if (!ended && (statement.Kind == SqlKind.Dml || statement.Kind == SqlKind.PlSql))
                return "커밋 전에는 다른 세션에 보이지 않습니다. 같은 DB의 다른 탭에는 보입니다(세션 공유).";
            return null;
        }

        // ---------- 결과 ----------

        /// <summary>TSV 한 칸: NULL은 빈 칸, 값 안의 탭·CR·LF는 공백(CRLF는 공백 하나).</summary>
        public static string TsvCell(string value)
        {
            if (value == null)
                return "";
            if (value.IndexOfAny(TsvBreakers) < 0)
                return value;
            var sb = new StringBuilder(value.Length);
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (c == '\r' && i + 1 < value.Length && value[i + 1] == '\n')
                {
                    sb.Append(' ');
                    i++;
                }
                else
                {
                    sb.Append(c == '\t' || c == '\r' || c == '\n' ? ' ' : c);
                }
            }
            return sb.ToString();
        }

        /// <summary>TSV 텍스트. header가 null이 아니면 첫 줄. 줄은 CRLF로 나누고 끝에는 줄바꿈을 붙이지 않는다.</summary>
        public static string ToTsv(IEnumerable<string> header, IEnumerable<string[]> rows)
        {
            var sb = new StringBuilder();
            var first = true;
            if (header != null)
            {
                AppendTsvLine(sb, header);
                first = false;
            }
            if (rows != null)
            {
                foreach (var row in rows)
                {
                    if (!first)
                        sb.Append("\r\n");
                    AppendTsvLine(sb, row ?? Array.Empty<string>());
                    first = false;
                }
            }
            return sb.ToString();
        }

        private static void AppendTsvLine(StringBuilder sb, IEnumerable<string> cells)
        {
            var firstCell = true;
            foreach (var cell in cells)
            {
                if (!firstCell)
                    sb.Append('\t');
                sb.Append(TsvCell(cell));
                firstCell = false;
            }
        }

        public static bool HasLineBreak(string value)
        {
            return value != null && value.IndexOfAny(LineBreaks) >= 0;
        }

        /// <summary>그리드 칸 표시: NULL은 "NULL", 줄바꿈은 "↵", 탭은 공백(행 높이가 한 줄로 유지되게).</summary>
        /// <summary>[셀 복사]로 클립보드에 넣을 값: 원래 값 그대로(줄바꿈·탭 유지), NULL은 빈 글. 열이 없으면 빈 글.</summary>
        public static string CellCopyText(string[] values, int column)
        {
            return values != null && column >= 0 && column < values.Length ? values[column] ?? "" : "";
        }

        public static string CellText(string value)
        {
            if (value == null)
                return "NULL";
            if (value.IndexOfAny(TsvBreakers) < 0)
                return value;
            var sb = new StringBuilder(value.Length);
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (c == '\r')
                {
                    sb.Append('↵');
                    if (i + 1 < value.Length && value[i + 1] == '\n')
                        i++;
                }
                else if (c == '\n')
                {
                    sb.Append('↵');
                }
                else
                {
                    sb.Append(c == '\t' ? ' ' : c);
                }
            }
            return sb.ToString();
        }

        /// <summary>칸 툴팁: 전체 값을 줄마다 width자로 접고, 전체가 maxChars자를 넘으면 잘라 "…". NULL이면 null.</summary>
        public static string TooltipText(string value, int width = TooltipLineWidth, int maxChars = TooltipMaxChars)
        {
            if (value == null)
                return null;
            width = Math.Max(1, width);
            var truncated = value.Length > maxChars;
            var text = truncated ? Truncate(value, maxChars, "") : value;
            var sb = new StringBuilder(text.Length + text.Length / width + 4);
            var lines = text.Replace("\r\n", "\n").Split(LineBreaks);
            for (var l = 0; l < lines.Length; l++)
            {
                if (l > 0)
                    sb.Append('\n');
                var line = lines[l];
                var pos = 0;
                while (line.Length - pos > width)
                {
                    var take = width;
                    // 서로게이트 쌍 가운데에서 접지 않는다
                    if (char.IsHighSurrogate(line[pos + take - 1]))
                        take--;
                    sb.Append(line, pos, take).Append('\n');
                    pos += take;
                }
                sb.Append(line, pos, line.Length - pos);
            }
            if (truncated)
                sb.Append('…');
            return sb.ToString();
        }

        /// <summary>
        /// 결과 열의 처음 너비(픽셀) 어림: 열 이름(굵게)·형식(작은 고정폭)·앞쪽 행 값 중 가장 긴 것. MinColumnWidth~MaxColumnWidth.
        /// 한글 등 넓은 글자는 1.8칸으로 센다. 사용자가 머리 경계를 끌어 바꿀 수 있으므로 어림이면 충분하다.
        /// 열 이름 뒤에는 정렬 표시(SortMark " ▲") 자리를 둔다 — 없으면 좁은 열에서 표시가 잘려 보이지 않는다.
        /// </summary>
        public static double ColumnWidth(string name, string typeLabel, IEnumerable<string> sample)
        {
            var units = Math.Max((TextUnits(name) + SortMarkUnits) * 1.1, TextUnits(typeLabel) * 0.85);
            if (sample != null)
            {
                foreach (var value in sample)
                    units = Math.Max(units, TextUnits(CellText(value)));
            }
            return Math.Max(MinColumnWidth, Math.Min(MaxColumnWidth, Math.Ceiling(units * CharWidth + CellPadding)));
        }

        /// <summary>행 번호 열 너비: 자릿수(쉼표 포함)에 맞춘다.</summary>
        public static double RowNumberWidth(int rowCount)
        {
            var digits = Count(Math.Max(1, rowCount)).Length;
            return Math.Max(40, Math.Ceiling(digits * CharWidth + CellPadding));
        }

        private static double TextUnits(string text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;
            double units = 0;
            // 너비 상한(MaxColumnWidth)을 넘는 길이는 셀 필요가 없다
            var limit = Math.Min(text.Length, 80);
            for (var i = 0; i < limit; i++)
                units += IsWide(text[i]) ? 1.8 : 1;
            return units;
        }

        private static bool IsWide(char c)
        {
            return (c >= 'ᄀ' && c <= 'ᅟ') || (c >= '⺀' && c <= '꓏') || (c >= '가' && c <= '힣')
                || (c >= '豈' && c <= '﫿') || (c >= '︰' && c <= '﹏') || (c >= '＀' && c <= '｠')
                || (c >= '￠' && c <= '￦');
        }

        private static bool IsSimpleIdentifier(string name)
        {
            if (name.Length == 0 || name[0] < 'A' || name[0] > 'Z')
                return false;
            foreach (var c in name)
            {
                var ok = (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '$' || c == '#';
                if (!ok)
                    return false;
            }
            return true;
        }

        private static string Truncate(string text, int maxLength, string suffix = "…")
        {
            if (text.Length <= maxLength)
                return text;
            var length = Math.Max(0, maxLength);
            // 서로게이트 쌍의 앞 절반에서 자르면 깨진 글자가 남는다
            if (length > 0 && char.IsHighSurrogate(text[length - 1]))
                length--;
            return text.Substring(0, length).TrimEnd() + suffix;
        }
    }
}

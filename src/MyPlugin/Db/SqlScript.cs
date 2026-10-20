using System;
using System.Collections.Generic;
using System.Text;

namespace MyPlugin
{
    public enum SqlKind
    {
        /// <summary>SELECT, WITH</summary>
        Query,
        /// <summary>INSERT, UPDATE, DELETE, MERGE</summary>
        Dml,
        /// <summary>CREATE, ALTER(ALTER SESSION·SYSTEM 제외), DROP, TRUNCATE, RENAME, GRANT, REVOKE, COMMENT, PURGE, ANALYZE, AUDIT, NOAUDIT, FLASHBACK. Oracle은 DDL 전후로 자동 커밋한다.</summary>
        Ddl,
        /// <summary>BEGIN, DECLARE, CALL, EXEC/EXECUTE(BEGIN … END;로 바꿔 실행)</summary>
        PlSql,
        /// <summary>COMMIT, ROLLBACK, SAVEPOINT, SET TRANSACTION, SET CONSTRAINT(S)</summary>
        Transaction,
        /// <summary>그 밖(ALTER SESSION, ALTER SYSTEM(자동 커밋 안 함), LOCK TABLE, EXPLAIN PLAN 등)</summary>
        Other
    }

    public enum SqlTransactionAction
    {
        None,
        /// <summary>COMMIT [WORK] (뒤에 COMMENT·WRITE 옵션이 있어도 커밋. FORCE는 Other)</summary>
        Commit,
        /// <summary>ROLLBACK [WORK] (TO SAVEPOINT·FORCE 아님)</summary>
        Rollback,
        /// <summary>SAVEPOINT, ROLLBACK TO SAVEPOINT, SET TRANSACTION, SET CONSTRAINT(S), COMMIT·ROLLBACK FORCE — 트랜잭션 안에서 SQL로 그대로 실행</summary>
        Other
    }

    /// <summary>실행할 문장 하나.</summary>
    public sealed class SqlStatement
    {
        /// <summary>실행할 텍스트. 앞뒤 공백 제거(앞의 주석도 뺀다 — Start부터). PL/SQL 블록이 아니면 끝의 ';'를 뺀다(ODP.NET은 ';'가 있으면 ORA-00911).
        /// PL/SQL 블록은 END …; 의 ';'를 유지하고 끝의 '/' 줄은 뺀다. 문장 안의 주석은 그대로 둔다.
        /// EXEC/EXECUTE x는 "BEGIN x; END;"(x 끝의 ';'와 뒤 주석은 뺌).</summary>
        public string Text { get; set; }

        /// <summary>원문에서 문장이 시작하는 위치(앞 공백·주석 다음, 첫 토큰). <see cref="SqlScript.Parse"/>로 만든 문장은 0.</summary>
        public int Start { get; set; }

        /// <summary>원문에서 문장이 차지하는 길이(끝 구분자 ';' 또는 '/' 줄 포함). 빈 줄·스크립트 끝에서 끝나면 마지막 토큰(주석 포함)까지.</summary>
        public int Length { get; set; }

        public SqlKind Kind { get; set; }

        /// <summary>주석을 건너뛴 첫 키워드(대문자). 예: SELECT, UPDATE, CREATE, EXEC. 여는 괄호로 시작하면 괄호 안 첫 키워드. 키워드로 시작하지 않으면 "".</summary>
        public string Verb { get; set; }

        public SqlTransactionAction TransactionAction { get; set; }

        /// <summary>끝의 ';'를 빼고 실행 텍스트를 만들었으면 true.</summary>
        public bool StrippedSemicolon { get; set; }

        /// <summary>문자열·주석 밖, 괄호 깊이 0에서 FOR UPDATE가 있는 SELECT. 잠금을 잡으므로 트랜잭션 안에서 실행해야 한다.</summary>
        public bool ForUpdate { get; set; }

        /// <summary>'/' 줄(또는 스크립트 끝, 끝을 짐작한 빈 줄 — <see cref="SqlScript"/> 참고)로 끝나는 PL/SQL 블록(DECLARE·BEGIN, CREATE [OR REPLACE] [EDITIONABLE|NONEDITIONABLE]
        /// PROCEDURE·FUNCTION·PACKAGE [BODY]·TRIGGER·TYPE [BODY]·LIBRARY·JAVA).</summary>
        public bool IsPlSqlBlock { get; set; }

        /// <summary>실행 전에 확인을 받아야 하는 이유(사람이 읽을 한국어 문장). 없으면 null.
        /// WHERE 없는 UPDATE·DELETE(괄호 깊이 0 기준), DROP·TRUNCATE·ALTER(ALTER SESSION·SYSTEM 제외)·PURGE·FLASHBACK(TO BEFORE DROP 제외),
        /// '/' 줄이 없어 빈 줄에서 끝을 짐작한 CREATE 블록.</summary>
        public string Danger { get; set; }
    }

    /// <summary>컴파일하는 PL/SQL 객체·뷰(ALL_ERRORS에서 컴파일 오류를 찾을 때).</summary>
    public sealed class CompileTarget
    {
        /// <summary>스키마. 문장에 없으면 null(현재 스키마).</summary>
        public string Owner { get; set; }

        public string Name { get; set; }

        /// <summary>ALL_ERRORS.TYPE 값. 예: PROCEDURE, PACKAGE BODY, VIEW. ALTER PACKAGE … COMPILE은 PACKAGE와 PACKAGE BODY.</summary>
        public List<string> Types { get; } = new List<string>();
    }

    /// <summary>구문 강조 구간의 종류.</summary>
    public enum SqlSpanKind
    {
        /// <summary>줄 주석(--)·블록 주석(/* */, 힌트 /*+ */ 포함)</summary>
        Comment,
        /// <summary>문자열 '…'·N'…'·q'[…]'·nq'[…]'</summary>
        String
    }

    /// <summary>구문 강조 구간: 글에서의 위치(UTF-16 인덱스 — string.Substring과 같다)와 길이.</summary>
    public readonly struct SqlSpan
    {
        public SqlSpan(SqlSpanKind kind, int start, int length)
        {
            Kind = kind;
            Start = start;
            Length = length;
        }

        public SqlSpanKind Kind { get; }
        public int Start { get; }
        public int Length { get; }
        public int End { get { return Start + Length; } }
    }

    /// <summary>
    /// SQL 편집기 텍스트를 문장으로 나누고 종류를 판정한다(순수 로직, 테스트 대상).
    /// 구분 규칙(SQL Developer와 비슷하게):
    /// - 문자열('…', '' 이스케이프), Oracle 대체 따옴표(q'[…]', q'{…}', q'(…)', q'&lt;…&gt;', 그 밖 같은 문자 짝), 따옴표 식별자("…"),
    ///   줄 주석(--), 블록 주석(/* */) 안의 구분자는 무시한다. 닫히지 않은 문자열·주석은 끝까지 이어진 것으로 본다(예외 없음).
    /// - PL/SQL 블록이 아닌 문장은 ';', 공백만 있는 빈 줄, 또는 '/'만 있는 줄에서 끝난다.
    /// - PL/SQL 블록은 '/'만 있는 줄에서 끝난다(안의 ';'·빈 줄로는 안 끝남). '/' 줄 없이 스크립트 끝까지 가면 SQL Developer처럼
    ///   들여쓰지 않은 줄이 END [이름];으로 끝나고 바로 빈 줄이 오며 그 뒤에 문장이 더 있을 때 그 빈 줄에서 끝낸다
    ///   (패키지 본문 안의 "  END p;" 뒤 빈 줄처럼 들여쓴 END에서는 끝내지 않는다). 이름은 블록 자신의 이름이어야 하고(익명 블록은 이름 없는 END;),
    ///   빈 줄 뒤가 PROCEDURE·FUNCTION·END 등 블록 안에서만 이어지는 낱말이면 끝내지 않는다. 그런 곳이 없으면 스크립트 끝까지.
    /// - 주석·공백만 있는 조각은 문장으로 내지 않는다.
    /// </summary>
    public static class SqlScript
    {
        private const string UpdateWithoutWhere = "WHERE 절이 없습니다. 테이블의 모든 행이 바뀝니다.";
        private const string DeleteWithoutWhere = "WHERE 절이 없습니다. 테이블의 모든 행이 삭제됩니다.";
        private const string Irreversible = "되돌릴 수 없는 문장입니다. DDL은 바로 커밋되어 롤백할 수 없습니다.";
        private const string GuessedBlockEnd = "블록 끝을 알리는 '/' 줄이 없어 빈 줄에서 블록이 끝난 것으로 봤습니다. "
            + "블록이 잘리거나 아래 문장이 섞이지 않았는지 확인하세요(블록 끝에 '/'만 있는 줄을 두면 묻지 않습니다).";

        /// <summary>스크립트 전체를 문장 목록으로. 순서는 원문 순서.</summary>
        public static List<SqlStatement> Split(string script)
        {
            var result = new List<SqlStatement>();
            if (string.IsNullOrEmpty(script))
                return result;
            var tokens = Tokenize(script);
            var i = SkipSeparators(script, tokens, 0);
            while (i < tokens.Count)
            {
                var block = IsBlockHeader(script, tokens, i);
                var end = i;
                while (end < tokens.Count && !EndsStatement(script, tokens[end], block))
                    end++;
                var guessedEnd = false;
                if (block && end >= tokens.Count)
                {
                    // '/' 줄 없이 스크립트 끝까지 가는 블록이 아래 문장을 삼키지 않게 한다
                    var blank = UnterminatedBlockEnd(script, tokens, i);
                    if (blank >= 0)
                    {
                        end = blank;
                        guessedEnd = true;
                    }
                }
                // ';'와 '/' 줄은 문장 범위에 넣는다. 빈 줄·스크립트 끝에서 끝나면 마지막 토큰까지.
                var terminatorEnd = -1;
                var semicolon = false;
                if (end < tokens.Count && tokens[end].Type != TokenType.BlankLine)
                {
                    terminatorEnd = tokens[end].End;
                    semicolon = tokens[end].Type == TokenType.Symbol;
                }
                var statement = Build(script, tokens, i, end, terminatorEnd, block, semicolon);
                // CREATE 블록을 잘못 자르거나 합치면 멀쩡한 객체가 INVALID로 바뀐다 — 짐작한 끝은 실행 전에 확인받는다
                if (guessedEnd && statement.Kind == SqlKind.Ddl && statement.Danger == null)
                    statement.Danger = GuessedBlockEnd;
                result.Add(statement);
                i = SkipSeparators(script, tokens, end + 1);
            }
            return result;
        }

        /// <summary>
        /// 구문 강조용 주석·문자열 구간(앞에서 뒤로, 겹치지 않음). 문장 나누기(<see cref="Split"/>)와 같은 토큰 규칙이라
        /// 실행할 때 문장을 나누는 기준과 화면에 칠하는 기준이 같다. 따옴표 식별자("…")·숫자·키워드는 구간이 아니다.
        /// 줄 주석은 줄 끝 공백을 넣지 않고, 닫히지 않은 문자열·주석은 글 끝까지다. null·빈 글은 빈 목록.
        /// </summary>
        public static List<SqlSpan> HighlightSpans(string script)
        {
            var spans = new List<SqlSpan>();
            if (string.IsNullOrEmpty(script))
                return spans;
            foreach (var t in Tokenize(script))
            {
                if (t.IsComment)
                    spans.Add(new SqlSpan(SqlSpanKind.Comment, t.Start, t.End - t.Start));
                else if (t.Type == TokenType.Literal)
                    spans.Add(new SqlSpan(SqlSpanKind.String, t.Start, t.End - t.Start));
            }
            return spans;
        }

        /// <summary>
        /// 커서 위치의 문장. 커서가 어떤 문장 범위(Start ≤ caret ≤ Start+Length) 안이면 그 문장,
        /// 문장 사이(공백·주석만 있는 곳)면 커서 뒤 가장 가까운 문장, 뒤에 없으면 앞 문장. 문장이 없으면 null.
        /// </summary>
        public static SqlStatement AtCaret(string script, int caret)
        {
            var statements = Split(script);
            if (statements.Count == 0)
                return null;
            foreach (var statement in statements)
            {
                if (statement.Start <= caret && caret <= statement.Start + statement.Length)
                    return statement;
            }
            foreach (var statement in statements)
            {
                if (statement.Start > caret)
                    return statement;
            }
            return statements[statements.Count - 1];
        }

        /// <summary>
        /// 여러 문장 실행(선택 영역 Ctrl+Enter·스크립트 실행 F5)용 나누기. <see cref="Split"/>과 같되,
        /// 빈 줄에서 끊긴 조각 뒤의 조각이 새 문장으로 시작하지 않으면(FROM·WHERE·SET·AND·UNION 등으로 이어지면) 한 문장으로 합친다.
        /// 그래서 문장 안에 빈 줄을 둔 스크립트도 ';'로 끝내면 그대로 실행된다. ';'·'/' 줄은 언제나 문장을 끝낸다.
        /// 합친 문장은 <see cref="Parse"/>로 다시 분석하고 Start·Length는 원문 기준이다.
        /// </summary>
        public static List<SqlStatement> SplitForRun(string script)
        {
            var result = new List<SqlStatement>();
            foreach (var part in Split(script))
            {
                var previous = result.Count > 0 ? result[result.Count - 1] : null;
                if (previous != null && EndedAtBlankLine(script, previous) && !StartsStatement(part))
                {
                    var start = previous.Start;
                    var end = part.Start + part.Length;
                    var merged = Parse(script.Substring(start, end - start));
                    if (merged != null)
                    {
                        merged.Start += start;
                        merged.Length = end - merged.Start;
                        result[result.Count - 1] = merged;
                        continue;
                    }
                }
                result.Add(part);
            }
            return result;
        }

        // 빈 줄(또는 스크립트 끝)에서 끝난 일반 문장인지: ';'·'/' 줄로 끝났거나 PL/SQL 블록이면 아니다
        private static bool EndedAtBlankLine(string script, SqlStatement statement)
        {
            if (statement.IsPlSqlBlock)
                return false;
            var raw = script.Substring(statement.Start, statement.Length).TrimEnd();
            if (raw.EndsWith(";", StringComparison.Ordinal))
                return false;
            var lastBreak = raw.LastIndexOf('\n');
            return raw.Substring(lastBreak + 1).Trim() != "/";
        }

        // 새 문장으로 시작하는 조각인지. ALTER SESSION·LOCK·EXPLAIN 말고 종류가 Other인 조각(FROM·WHERE·SET·AND …)은 앞 문장에 이어진다.
        private static bool StartsStatement(SqlStatement statement)
        {
            if (statement.Kind != SqlKind.Other)
                return true;
            switch (statement.Verb)
            {
                case "ALTER":
                case "LOCK":
                case "EXPLAIN":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// 선택 영역처럼 이미 문장 하나로 정해진 텍스트를 분석한다. 주석·공백(과 ';'·'/' 줄 같은 빈 구분자)뿐이면 null.
        /// 가운데의 ';'·빈 줄로는 나누지 않고 끝의 '/' 줄과(PL/SQL 블록이 아니면) 끝의 ';' 하나만 뗀다.
        /// Length는 텍스트 처음부터 문장 끝(뗀 구분자 포함)까지.
        /// </summary>
        public static SqlStatement Parse(string text)
        {
            if (string.IsNullOrEmpty(text))
                return null;
            var tokens = Tokenize(text);
            var first = SkipSeparators(text, tokens, 0);
            if (first >= tokens.Count)
                return null;
            var block = IsBlockHeader(text, tokens, first);
            var limit = tokens.Count;
            var terminatorEnd = -1;
            var last = LastSignificant(tokens, first, limit);
            if (tokens[last].Type == TokenType.SlashLine)
            {
                terminatorEnd = tokens[last].End;
                limit = last;
                last = LastSignificant(tokens, first, limit);
            }
            var semicolon = !block && IsSymbol(text, tokens[last], ';');
            if (semicolon)
            {
                if (terminatorEnd < 0)
                    terminatorEnd = tokens[last].End;
                limit = last;
            }
            var statement = Build(text, tokens, first, limit, terminatorEnd, block, semicolon);
            statement.Length += statement.Start;
            statement.Start = 0;
            return statement;
        }

        /// <summary>
        /// 컴파일 오류가 남는 객체: CREATE [OR REPLACE] [EDITIONABLE·NONEDITIONABLE·EDITIONING·[NO] FORCE]
        /// {PROCEDURE|FUNCTION|TRIGGER|VIEW|PACKAGE [BODY]|TYPE [BODY]} [스키마.]이름,
        /// ALTER {PROCEDURE|FUNCTION|TRIGGER|VIEW|PACKAGE|TYPE} [스키마.]이름 … COMPILE [BODY|SPECIFICATION]. 아니면 null.
        /// 따옴표 없는 이름은 대문자로, 따옴표 이름은 그대로.
        /// </summary>
        public static CompileTarget CompileTargetOf(string sql)
        {
            if (string.IsNullOrEmpty(sql))
                return null;
            return CompileTargetOf(sql, Tokenize(sql).FindAll(t => !t.IsComment && !t.IsMarker));
        }

        // sig: 주석·표시 토큰을 뺀 문장 토큰(sig[0]이 첫 낱말)
        private static CompileTarget CompileTargetOf(string sql, List<Token> sig)
        {
            var verb = WordText(sql, sig, 0);
            var k = 1;
            if (verb == "CREATE")
            {
                if (WordText(sql, sig, k) == "OR" && WordText(sql, sig, k + 1) == "REPLACE")
                    k += 2;
                while (true)
                {
                    var w = WordText(sql, sig, k);
                    if (w == "EDITIONABLE" || w == "NONEDITIONABLE" || w == "EDITIONING" || w == "FORCE" || w == "NOFORCE")
                        k++;
                    else if (w == "NO" && WordText(sql, sig, k + 1) == "FORCE")
                        k += 2;
                    else
                        break;
                }
            }
            else if (verb != "ALTER")
            {
                return null;
            }
            var kind = WordText(sql, sig, k++);
            if (kind != "PROCEDURE" && kind != "FUNCTION" && kind != "TRIGGER" && kind != "VIEW" && kind != "PACKAGE" && kind != "TYPE")
                return null;
            var body = false;
            if (verb == "CREATE" && (kind == "PACKAGE" || kind == "TYPE") && WordText(sql, sig, k) == "BODY")
            {
                body = true;
                k++;
            }
            var target = new CompileTarget { Name = NameText(sql, sig, k) };
            if (target.Name == null)
                return null;
            if (k + 2 < sig.Count && IsSymbol(sql, sig[k + 1], '.'))
            {
                target.Owner = target.Name;
                target.Name = NameText(sql, sig, k + 2);
                if (target.Name == null)
                    return null;
            }
            if (verb == "CREATE")
            {
                target.Types.Add(body ? kind + " BODY" : kind);
                return target;
            }
            // ALTER는 COMPILE일 때만 컴파일 오류가 생긴다. 패키지·형식은 무엇을 컴파일했는지에 따라 명세·본문
            var compile = sig.FindIndex(k, t => IsWord(sql, t, "COMPILE"));
            if (compile < 0)
                return null;
            var part = WordText(sql, sig, compile + 1);
            if (kind != "PACKAGE" && kind != "TYPE")
            {
                target.Types.Add(kind);
                return target;
            }
            if (part != "BODY")
                target.Types.Add(kind);
            if (part != "SPECIFICATION")
                target.Types.Add(kind + " BODY");
            return target;
        }

        /// <summary>LIKE 패턴용 이스케이프: '\' → '\\', '%' → '\%', '_' → '\_'. SQL에는 ESCAPE '\'를 함께 쓴다.</summary>
        public static string EscapeLike(string term)
        {
            if (string.IsNullOrEmpty(term))
                return "";
            var sb = new StringBuilder(term.Length + 8);
            foreach (var c in term)
            {
                if (c == '\\' || c == '%' || c == '_')
                    sb.Append('\\');
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>"포함" 검색 패턴: "%" + EscapeLike(앞뒤 공백 뺀 term의 대문자) + "%".</summary>
        public static string ContainsPattern(string term)
        {
            return "%" + EscapeLike((term ?? "").Trim().ToUpperInvariant()) + "%";
        }

        // ---- 문장 만들기 ----

        // tokens[first, limit)가 문장 내용(끝 구분자 제외). terminatorEnd는 문장 범위에 넣을 구분자의 끝(없으면 -1).
        private static SqlStatement Build(string s, List<Token> tokens, int first, int limit, int terminatorEnd, bool block, bool strippedSemicolon)
        {
            var significant = new List<Token>();
            var contentEnd = tokens[first].End;
            for (var i = first; i < limit; i++)
            {
                var t = tokens[i];
                if (t.IsMarker)
                    continue;
                contentEnd = t.End;
                if (!t.IsComment)
                    significant.Add(t);
            }
            var start = tokens[first].Start;
            var statement = new SqlStatement
            {
                Start = start,
                Length = (terminatorEnd >= 0 ? terminatorEnd : contentEnd) - start,
                // 닫히지 않은 문자열·주석이 스크립트 끝까지 이어지면 끝 공백이 들어 있다
                Text = s.Substring(start, contentEnd - start).TrimEnd(),
                IsPlSqlBlock = block,
                StrippedSemicolon = strippedSemicolon
            };
            Analyze(statement, s, significant);
            return statement;
        }

        // sig: 문장 내용에서 주석을 뺀 토큰
        private static void Analyze(SqlStatement statement, string s, List<Token> sig)
        {
            var v = 0;
            while (v < sig.Count && IsSymbol(s, sig[v], '('))
                v++;
            var verb = WordText(s, sig, v) ?? "";
            var next = WordText(s, sig, v + 1);
            statement.Verb = verb;
            statement.Kind = KindOf(verb, next);
            statement.TransactionAction = TransactionActionOf(verb, next, s, sig, v);
            statement.ForUpdate = statement.Kind == SqlKind.Query && HasTopLevel(s, sig, "FOR", "UPDATE");
            statement.Danger = DangerOf(verb, next, s, sig);
            if (v == 0 && (verb == "EXEC" || verb == "EXECUTE"))
                statement.Text = ExecAsBlock(s, sig, statement.Text);
        }

        private static SqlKind KindOf(string verb, string next)
        {
            switch (verb)
            {
                case "SELECT":
                case "WITH":
                    return SqlKind.Query;
                case "INSERT":
                case "UPDATE":
                case "DELETE":
                case "MERGE":
                    return SqlKind.Dml;
                case "ALTER":
                    // ALTER SYSTEM은 DDL과 달리 트랜잭션을 커밋하지 않는다
                    return next == "SESSION" || next == "SYSTEM" ? SqlKind.Other : SqlKind.Ddl;
                case "CREATE":
                case "DROP":
                case "TRUNCATE":
                case "RENAME":
                case "GRANT":
                case "REVOKE":
                case "COMMENT":
                case "PURGE":
                case "ANALYZE":
                case "AUDIT":
                case "NOAUDIT":
                case "FLASHBACK":
                    return SqlKind.Ddl;
                case "BEGIN":
                case "DECLARE":
                case "CALL":
                case "EXEC":
                case "EXECUTE":
                    return SqlKind.PlSql;
                case "COMMIT":
                case "ROLLBACK":
                case "SAVEPOINT":
                    return SqlKind.Transaction;
                case "SET":
                    return IsTransactionSet(next) ? SqlKind.Transaction : SqlKind.Other;
                default:
                    return SqlKind.Other;
            }
        }

        // SET TRANSACTION, SET CONSTRAINT(S): 지금 트랜잭션에만 뜻이 있다(트랜잭션 밖에서 자동 커밋되면 바로 사라진다)
        private static bool IsTransactionSet(string next)
        {
            return next == "TRANSACTION" || next == "CONSTRAINT" || next == "CONSTRAINTS";
        }

        private static SqlTransactionAction TransactionActionOf(string verb, string next, string s, List<Token> sig, int v)
        {
            if (verb == "COMMIT" || verb == "ROLLBACK")
            {
                var k = v + 1;
                if (k < sig.Count && IsWord(s, sig[k], "WORK"))
                    k++;
                if (k >= sig.Count)
                    return verb == "COMMIT" ? SqlTransactionAction.Commit : SqlTransactionAction.Rollback;
                // COMMENT·WRITE는 커밋 방식 옵션일 뿐이다. ROLLBACK TO [SAVEPOINT]와 FORCE(다른 분산 트랜잭션 정리)는
                // 현재 트랜잭션을 끝내지 않으므로 SQL로 보낸다.
                if (verb == "COMMIT" && (IsWord(s, sig[k], "COMMENT") || IsWord(s, sig[k], "WRITE")))
                    return SqlTransactionAction.Commit;
                return SqlTransactionAction.Other;
            }
            if (verb == "SAVEPOINT" || (verb == "SET" && IsTransactionSet(next)))
                return SqlTransactionAction.Other;
            return SqlTransactionAction.None;
        }

        private static string DangerOf(string verb, string next, string s, List<Token> sig)
        {
            switch (verb)
            {
                case "UPDATE":
                    return HasTopLevel(s, sig, "WHERE", null) ? null : UpdateWithoutWhere;
                case "DELETE":
                    return HasTopLevel(s, sig, "WHERE", null) ? null : DeleteWithoutWhere;
                case "DROP":
                case "TRUNCATE":
                case "PURGE":
                    return Irreversible;
                case "ALTER":
                    // ALTER TABLE … DROP COLUMN·TRUNCATE PARTITION 등. 세션·시스템 설정은 데이터를 지우지 않는다
                    return next == "SESSION" || next == "SYSTEM" ? null : Irreversible;
                case "FLASHBACK":
                    // TO BEFORE DROP은 지운 테이블을 되살린다. TO SCN·TIMESTAMP는 그 뒤의 변경을 버린다
                    return HasTopLevel(s, sig, "BEFORE", "DROP") ? null : Irreversible;
                default:
                    return null;
            }
        }

        // 괄호 깊이 0에서 keyword(then이 있으면 바로 뒤에 then)가 나오는지. 부질의 안의 WHERE·FOR UPDATE는 세지 않는다.
        private static bool HasTopLevel(string s, List<Token> sig, string keyword, string then)
        {
            var depth = 0;
            for (var i = 0; i < sig.Count; i++)
            {
                var t = sig[i];
                if (IsSymbol(s, t, '('))
                    depth++;
                else if (IsSymbol(s, t, ')'))
                    depth--;
                else if (depth == 0 && IsWord(s, t, keyword) && (then == null || (i + 1 < sig.Count && IsWord(s, sig[i + 1], then))))
                    return true;
            }
            return false;
        }

        // EXEC x → BEGIN x; END;  x 끝의 ';'와 뒤 주석은 뺀다(끝 줄 주석이 "; END;"까지 주석으로 만들지 않게).
        // 줄 끝의 '-'는 SQL*Plus의 줄 이음표다 — 그대로 두면 다음 줄 값의 빼기 부호가 된다(p(1, -⏎2) → p(1, -2)).
        private static string ExecAsBlock(string s, List<Token> sig, string text)
        {
            var last = sig.Count - 1;
            while (last > 0 && IsSymbol(s, sig[last], ';'))
                last--;
            if (last < 1)
                return text;  // EXEC만 있으면 그대로 보내 DB 오류로 알린다
            var body = new StringBuilder();
            var pos = sig[1].Start;
            for (var i = 1; i < last; i++)
            {
                var t = sig[i];
                if (!IsSymbol(s, t, '-') || !IsBlankUntilLineEnd(s, t.End))
                    continue;
                var lineEnd = LineEnd(s, t.End);
                body.Append(s, pos, t.Start - pos).Append(' ');
                pos = lineEnd + (s[lineEnd] == '\r' && lineEnd + 1 < s.Length && s[lineEnd + 1] == '\n' ? 2 : 1);
            }
            body.Append(s, pos, sig[last].End - pos);
            return "BEGIN " + body + "; END;";
        }

        // ---- 문장 경계 ----

        // 첫 키워드(주석·빈 줄은 건너뜀)로 '/' 줄까지 이어지는 PL/SQL 블록인지 판단한다.
        private static bool IsBlockHeader(string s, List<Token> tokens, int first)
        {
            var words = new List<string>();
            for (var i = first; i < tokens.Count && words.Count < 7; i++)
            {
                var t = tokens[i];
                if (t.IsComment || t.Type == TokenType.BlankLine)
                    continue;
                if (t.Type != TokenType.Word)
                    break;
                words.Add(Upper(s, t));
            }
            var w0 = WordAt(words, 0);
            if (w0 == "DECLARE" || w0 == "BEGIN")
                return true;
            if (w0 != "CREATE")
                return false;
            var k = 1;
            if (WordAt(words, k) == "OR" && WordAt(words, k + 1) == "REPLACE")
                k += 2;
            if (WordAt(words, k) == "EDITIONABLE" || WordAt(words, k) == "NONEDITIONABLE")
                k++;
            // CREATE [OR REPLACE] [AND {RESOLVE|COMPILE}] [NOFORCE] JAVA …
            if (WordAt(words, k) == "AND" && (WordAt(words, k + 1) == "RESOLVE" || WordAt(words, k + 1) == "COMPILE"))
                k += 2;
            if (WordAt(words, k) == "NOFORCE")
                k++;
            switch (WordAt(words, k))
            {
                case "PROCEDURE":
                case "FUNCTION":
                case "PACKAGE":
                case "TRIGGER":
                // TYPE은 BODY가 없으면 SQL 형식 명세일 수도 있지만 SQL*Plus처럼 '/' 줄까지 한 문장으로 본다
                case "TYPE":
                case "LIBRARY":
                case "JAVA":
                    return true;
                default:
                    return false;
            }
        }

        private static bool EndsStatement(string s, Token t, bool block)
        {
            if (t.Type == TokenType.SlashLine)
                return true;
            return !block && (t.Type == TokenType.BlankLine || IsSymbol(s, t, ';'));
        }

        /// <summary>
        /// '/' 줄 없이 스크립트 끝까지 가는 블록(tokens[first]부터)의 끝으로 볼 빈 줄: 들여쓰지 않은 줄이 END [이름];로 끝나고(뒤 주석은 무시)
        /// 바로 다음이 빈 줄이며 그 뒤에 문장이 더 있는 첫 곳. 없으면 -1. 들여쓴 END(패키지 본문 안의 서브프로그램 등)에서는 끝내지 않는다.
        /// 들여쓰지 않은 안쪽 END도 끝으로 보지 않는다: END 뒤 이름은 블록 자신의 이름이어야 하고(CREATE … 이름, 익명 블록은 이름 없는 END;만),
        /// 빈 줄 뒤가 PROCEDURE·FUNCTION·END처럼 블록 안에서만 이어지는 낱말이면 끝이 아니다(패키지·형식 본문, 로컬 서브프로그램).
        /// </summary>
        private static int UnterminatedBlockEnd(string s, List<Token> tokens, int first)
        {
            var own = BlockName(s, tokens, first);
            for (var k = first + 1; k < tokens.Count; k++)
            {
                if (tokens[k].Type != TokenType.BlankLine || !EndsWithBlockEnd(s, tokens, first, k, own))
                    continue;
                var next = NextStatementToken(s, tokens, k + 1);
                if (next >= 0 && !ContinuesBlock(s, tokens[next]))
                    return k;
            }
            return -1;
        }

        // tokens[blank] 바로 앞 줄이 들여쓰지 않은 줄이고 END [own] ; 로 끝나는지(END IF·LOOP·CASE는 블록 끝이 아니다).
        // 이름이 다른 END(패키지 본문 안의 "END p1;")는 안쪽 서브프로그램의 끝이다. own이 null(익명 블록·이름 모름)이면 이름 없는 END만.
        private static bool EndsWithBlockEnd(string s, List<Token> tokens, int first, int blank, string own)
        {
            var m = blank - 1;
            while (m > first && tokens[m].IsComment)
                m--;
            if (m <= first || !IsSymbol(s, tokens[m], ';'))
                return false;
            var e = m - 1;
            var name = tokens[e];
            var named = false;
            if (e > first && (name.Type == TokenType.QuotedIdentifier
                || (name.Type == TokenType.Word && !IsWord(s, name, "END") && !IsWord(s, name, "IF") && !IsWord(s, name, "LOOP") && !IsWord(s, name, "CASE"))))
            {
                named = true;
                e--;
            }
            if (e <= first || !IsWord(s, tokens[e], "END"))
                return false;
            if (named && (own == null || !string.Equals(NameText(s, tokens, e + 1), own, StringComparison.Ordinal)))
                return false;
            var lineStart = tokens[e].Start;
            while (lineStart > 0 && s[lineStart - 1] != '\n' && s[lineStart - 1] != '\r')
                lineStart--;
            return !char.IsWhiteSpace(s[lineStart]);
        }

        // CREATE 블록이 만드는 객체 이름(CompileTargetOf와 같은 규칙). 익명 블록이거나 알 수 없으면 null.
        private static string BlockName(string s, List<Token> tokens, int first)
        {
            if (!IsWord(s, tokens[first], "CREATE"))
                return null;
            // CREATE [OR REPLACE] [EDITIONABLE …] 종류 [BODY] [스키마.]이름 — 머리 토큰 몇 개면 된다
            var header = new List<Token>();
            for (var i = first; i < tokens.Count && header.Count < 16; i++)
            {
                if (!tokens[i].IsComment && !tokens[i].IsMarker)
                    header.Add(tokens[i]);
            }
            var target = CompileTargetOf(s, header);
            return target == null ? null : target.Name;
        }

        // from부터 처음 나오는 문장 토큰(주석·표시·';'가 아닌 것). 없으면 -1.
        private static int NextStatementToken(string s, List<Token> tokens, int from)
        {
            for (var i = from; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (!t.IsComment && !t.IsMarker && !IsSymbol(s, t, ';'))
                    return i;
            }
            return -1;
        }

        // 블록 안에서만 이어지는 낱말(다음 서브프로그램·선언·형식 본문의 멤버, 마지막 END). 이런 낱말로 시작하는 SQL 문장은 없다.
        private static bool ContinuesBlock(string s, Token t)
        {
            if (t.Type != TokenType.Word)
                return false;
            switch (Upper(s, t))
            {
                case "PROCEDURE":
                case "FUNCTION":
                case "END":
                case "CURSOR":
                case "TYPE":
                case "SUBTYPE":
                case "PRAGMA":
                case "MEMBER":
                case "STATIC":
                case "CONSTRUCTOR":
                case "MAP":
                case "ORDER":
                case "OVERRIDING":
                    return true;
                default:
                    return false;
            }
        }

        // 문장 앞의 주석·빈 줄과 빈 조각의 구분자(';', '/' 줄)를 건너뛴다
        private static int SkipSeparators(string s, List<Token> tokens, int i)
        {
            while (i < tokens.Count && (tokens[i].IsComment || tokens[i].IsMarker || IsSymbol(s, tokens[i], ';')))
                i++;
            return i;
        }

        // [first, limit)에서 주석·빈 줄이 아닌 마지막 토큰. tokens[first]는 그런 토큰이어야 한다.
        private static int LastSignificant(List<Token> tokens, int first, int limit)
        {
            var i = limit - 1;
            while (i > first && (tokens[i].IsComment || tokens[i].Type == TokenType.BlankLine))
                i--;
            return i;
        }

        private static string WordAt(List<string> words, int index)
        {
            return index < words.Count ? words[index] : null;
        }

        private static string WordText(string s, List<Token> sig, int index)
        {
            return index < sig.Count && sig[index].Type == TokenType.Word ? Upper(s, sig[index]) : null;
        }

        // 식별자 하나: 따옴표 없으면 대문자, 따옴표 식별자는 안의 글자 그대로. 식별자가 아니면 null.
        private static string NameText(string s, List<Token> sig, int index)
        {
            if (index >= sig.Count)
                return null;
            var t = sig[index];
            if (t.Type == TokenType.Word)
                return Upper(s, t);
            if (t.Type != TokenType.QuotedIdentifier || t.End - t.Start < 3 || s[t.End - 1] != '"')
                return null;
            return s.Substring(t.Start + 1, t.End - t.Start - 2);
        }

        private static bool IsSymbol(string s, Token t, char c)
        {
            return t.Type == TokenType.Symbol && s[t.Start] == c;
        }

        private static bool IsWord(string s, Token t, string keyword)
        {
            return t.Type == TokenType.Word && t.End - t.Start == keyword.Length
                && string.Compare(s, t.Start, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) == 0;
        }

        private static string Upper(string s, Token t)
        {
            return s.Substring(t.Start, t.End - t.Start).ToUpperInvariant();
        }

        // ---- 토큰 나누기 ----

        private enum TokenType
        {
            /// <summary>키워드·식별자(글자로 시작, 글자·숫자·_·$·#)</summary>
            Word,
            Number,
            /// <summary>'…', N'…', q'[…]', nq'[…]'</summary>
            Literal,
            QuotedIdentifier,
            LineComment,
            BlockComment,
            /// <summary>그 밖의 한 글자: ( ) ; , / 등</summary>
            Symbol,
            /// <summary>문자열·주석 밖의 공백만 있는 줄(길이 0 표시)</summary>
            BlankLine,
            /// <summary>문자열·주석 밖의 '/'만 있는 줄의 '/'</summary>
            SlashLine
        }

        private readonly struct Token
        {
            public Token(TokenType type, int start, int end)
            {
                Type = type;
                Start = start;
                End = end;
            }

            public TokenType Type { get; }
            public int Start { get; }
            public int End { get; }
            public bool IsComment { get { return Type == TokenType.LineComment || Type == TokenType.BlockComment; } }
            public bool IsMarker { get { return Type == TokenType.BlankLine || Type == TokenType.SlashLine; } }
        }

        /// <summary>
        /// 한 번 훑어 토큰으로 나눈다. 공백은 버리고, 문자열·주석 밖의 빈 줄과 '/'만 있는 줄은 표시 토큰으로 남긴다.
        /// 줄바꿈은 CRLF·LF·CR. 닫히지 않은 문자열·주석·따옴표 식별자는 끝까지로 본다.
        /// </summary>
        private static List<Token> Tokenize(string s)
        {
            var tokens = new List<Token>();
            var n = s.Length;
            var pos = 0;
            // 지금 줄에 공백 말고 무엇이 있었는지. 여러 줄 문자열·주석이 끝난 줄도 내용이 있는 줄이다.
            var lineHasContent = false;
            while (pos < n)
            {
                var c = s[pos];
                if (c == '\n' || c == '\r')
                {
                    if (!lineHasContent)
                        tokens.Add(new Token(TokenType.BlankLine, pos, pos));
                    pos += c == '\r' && pos + 1 < n && s[pos + 1] == '\n' ? 2 : 1;
                    lineHasContent = false;
                    continue;
                }
                if (char.IsWhiteSpace(c))
                {
                    pos++;
                    continue;
                }

                var start = pos;
                var next = pos + 1 < n ? s[pos + 1] : '\0';
                TokenType type;
                if (c == '-' && next == '-')
                {
                    pos = LineEnd(s, pos);
                    // 줄 끝 공백은 토큰에 넣지 않는다(다음 바퀴에서 공백으로 건너뜀)
                    while (pos > start + 2 && char.IsWhiteSpace(s[pos - 1]))
                        pos--;
                    type = TokenType.LineComment;
                }
                else if (c == '/' && next == '*')
                {
                    var close = s.IndexOf("*/", pos + 2, StringComparison.Ordinal);
                    pos = close < 0 ? n : close + 2;
                    type = TokenType.BlockComment;
                }
                else if (c == '/' && !lineHasContent && IsBlankUntilLineEnd(s, pos + 1))
                {
                    pos++;
                    type = TokenType.SlashLine;
                }
                else if (c == '\'')
                {
                    pos = StringEnd(s, pos + 1);
                    type = TokenType.Literal;
                }
                else if (c == '"')
                {
                    var close = s.IndexOf('"', pos + 1);
                    pos = close < 0 ? n : close + 1;
                    type = TokenType.QuotedIdentifier;
                }
                else if (char.IsLetter(c))
                {
                    pos++;
                    while (pos < n && IsWordPart(s[pos]))
                        pos++;
                    type = TokenType.Word;
                    // 접두어가 낱말 전체일 때만 접두 문자열이다: q'[…]', nq'[…]', N'…' (xq'…'는 xq 다음 보통 문자열)
                    if (pos < n && s[pos] == '\'')
                    {
                        var length = pos - start;
                        if (IsQPrefix(s, start, length) && pos + 1 < n && !char.IsWhiteSpace(s[pos + 1]))
                        {
                            pos = QuoteEnd(s, pos + 1);
                            type = TokenType.Literal;
                        }
                        else if (length == 1 && (c == 'n' || c == 'N'))
                        {
                            pos = StringEnd(s, pos + 1);
                            type = TokenType.Literal;
                        }
                    }
                }
                else if (IsDigit(c) || (c == '.' && IsDigit(next)))
                {
                    pos = NumberEnd(s, pos);
                    type = TokenType.Number;
                }
                else
                {
                    pos++;
                    type = TokenType.Symbol;
                }
                tokens.Add(new Token(type, start, pos));
                lineHasContent = true;
            }
            return tokens;
        }

        private static int LineEnd(string s, int pos)
        {
            while (pos < s.Length && s[pos] != '\n' && s[pos] != '\r')
                pos++;
            return pos;
        }

        private static bool IsBlankUntilLineEnd(string s, int pos)
        {
            for (; pos < s.Length && s[pos] != '\n' && s[pos] != '\r'; pos++)
            {
                if (!char.IsWhiteSpace(s[pos]))
                    return false;
            }
            return true;
        }

        // pos: 여는 따옴표 다음. '' 는 따옴표 한 글자다.
        private static int StringEnd(string s, int pos)
        {
            while (pos < s.Length)
            {
                if (s[pos] == '\'')
                {
                    if (pos + 1 < s.Length && s[pos + 1] == '\'')
                    {
                        pos += 2;
                        continue;
                    }
                    return pos + 1;
                }
                pos++;
            }
            return s.Length;
        }

        // pos: q' 다음의 여는 구분자. [ { ( < 는 짝 문자로, 그 밖은 같은 문자로 닫고 바로 뒤에 '가 와야 끝난다.
        private static int QuoteEnd(string s, int pos)
        {
            var open = s[pos];
            var close = open == '[' ? ']' : open == '{' ? '}' : open == '(' ? ')' : open == '<' ? '>' : open;
            for (var i = pos + 1; i + 1 < s.Length; i++)
            {
                if (s[i] == close && s[i + 1] == '\'')
                    return i + 2;
            }
            return s.Length;
        }

        // Oracle 숫자: 123, 1.5, .5, 1e-3, 2f, 3D. 1FROM·1WHERE처럼 붙여 쓴 키워드를 숫자에 삼키지 않는다.
        private static int NumberEnd(string s, int pos)
        {
            var n = s.Length;
            while (pos < n && IsDigit(s[pos]))
                pos++;
            // '..'(PL/SQL 범위 1..10)의 첫 '.'은 소수점이 아니다
            if (pos < n && s[pos] == '.' && !(pos + 1 < n && s[pos + 1] == '.'))
            {
                pos++;
                while (pos < n && IsDigit(s[pos]))
                    pos++;
            }
            if (pos < n && (s[pos] == 'e' || s[pos] == 'E'))
            {
                var j = pos + 1;
                if (j < n && (s[j] == '+' || s[j] == '-'))
                    j++;
                if (j < n && IsDigit(s[j]))
                {
                    pos = j;
                    while (pos < n && IsDigit(s[pos]))
                        pos++;
                }
            }
            if (pos < n && (s[pos] == 'f' || s[pos] == 'F' || s[pos] == 'd' || s[pos] == 'D') && !(pos + 1 < n && IsWordPart(s[pos + 1])))
                pos++;
            return pos;
        }

        private static bool IsQPrefix(string s, int start, int length)
        {
            if (length == 1)
                return s[start] == 'q' || s[start] == 'Q';
            return length == 2 && (s[start] == 'n' || s[start] == 'N') && (s[start + 1] == 'q' || s[start + 1] == 'Q');
        }

        private static bool IsWordPart(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_' || c == '$' || c == '#';
        }

        private static bool IsDigit(char c)
        {
            return c >= '0' && c <= '9';
        }
    }
}

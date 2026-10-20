using System.Linq;
using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    /// <summary>
    /// DBMS별 구문 규칙(SqlLexRules): 문장 나누기와 구문 강조가 같은 규칙을 따른다.
    /// Oracle은 지금까지와 같고(SqlScriptTests가 기본 규칙으로 그대로 확인), PostgreSQL·SQL Server는 자기 문법을 안다.
    /// </summary>
    public class SqlLexRulesTests
    {
        private static readonly SqlLexRules Pg = SqlLexRules.Postgres;
        private static readonly SqlLexRules Ms = SqlLexRules.SqlServer;

        private static string[] Texts(string script, SqlLexRules rules)
        {
            return SqlScript.Split(script, rules).Select(s => s.Text).ToArray();
        }

        private static (SqlSpanKind, string)[] Spans(string script, SqlLexRules rules)
        {
            return SqlScript.HighlightSpans(script, rules).Select(s => (s.Kind, script.Substring(s.Start, s.Length))).ToArray();
        }

        // ---------- 공급자 ----------

        [Fact]
        public void Providers_ExposeTheirRules()
        {
            Assert.Same(SqlLexRules.Oracle, DbProviders.Oracle.Lex);
            Assert.Same(SqlLexRules.Postgres, DbProviders.Postgres.Lex);
            Assert.Same(SqlLexRules.SqlServer, DbProviders.SqlServer.Lex);
            // 시험(SQLite)·모르는 연결은 지금까지처럼 Oracle 규칙
            Assert.Same(SqlLexRules.Oracle, DbProviders.Generic.Lex);
        }

        [Fact]
        public void NullRules_AreOracle()
        {
            const string script = "SELECT q'[a;b]' FROM dual;\nBEGIN\n  NULL;\nEND;\n/\nSELECT 2 FROM dual;";
            Assert.Equal(Texts(script, SqlLexRules.Oracle), SqlScript.Split(script).Select(s => s.Text).ToArray());
            Assert.Equal(3, SqlScript.Split(script).Count);
        }

        // ---------- PostgreSQL ----------

        [Fact]
        public void Postgres_DollarQuotedBody_IsOneStatement()
        {
            const string script = "CREATE FUNCTION f() RETURNS int LANGUAGE plpgsql AS $$\nBEGIN\n  PERFORM 1;\n  RETURN 1;\nEND;\n$$;\nSELECT f();";
            var parts = SqlScript.Split(script, Pg);
            Assert.Equal(2, parts.Count);
            Assert.EndsWith("$$", parts[0].Text);
            Assert.False(parts[0].IsPlSqlBlock);
            Assert.Equal("SELECT f()", parts[1].Text);
            Assert.Equal(new[] { (SqlSpanKind.String, "$$\nBEGIN\n  PERFORM 1;\n  RETURN 1;\nEND;\n$$") }, Spans(script, Pg));
        }

        [Fact]
        public void Postgres_TaggedDollarQuote_AndDoBlock()
        {
            const string script = "DO $body$ BEGIN RAISE NOTICE '$$;'; END $body$;\nSELECT 1;";
            Assert.Equal(new[] { "DO $body$ BEGIN RAISE NOTICE '$$;'; END $body$", "SELECT 1" }, Texts(script, Pg));
            Assert.Equal(new[] { (SqlSpanKind.String, "$body$ BEGIN RAISE NOTICE '$$;'; END $body$") }, Spans(script, Pg));
        }

        [Fact]
        public void Postgres_PositionalParameterAndWordDollar_AreNotQuotes()
        {
            // $1(위치 매개변수)·a$b(식별자 안의 $)는 따옴표가 아니다
            const string script = "SELECT $1, a$b FROM t; SELECT 2;";
            Assert.Equal(2, SqlScript.Split(script, Pg).Count);
            Assert.Empty(SqlScript.HighlightSpans(script, Pg));
        }

        [Fact]
        public void Postgres_EscapeString_BackslashQuote()
        {
            const string script = "SELECT E'it\\'s; -- x', 'plain\\' FROM t;\nSELECT 2;";
            Assert.Equal(2, SqlScript.Split(script, Pg).Count);
            Assert.Equal(new[] { (SqlSpanKind.String, "E'it\\'s; -- x'"), (SqlSpanKind.String, "'plain\\'") }, Spans(script, Pg));
        }

        [Fact]
        public void Postgres_NestedBlockComment()
        {
            const string script = "/* a /* b */ still; comment */ SELECT 1;";
            Assert.Equal(new[] { (SqlSpanKind.Comment, "/* a /* b */ still; comment */") }, Spans(script, Pg));
            Assert.Equal(new[] { "SELECT 1" }, Texts(script, Pg).Select(t => t.Substring(t.IndexOf("SELECT"))).ToArray());
            // Oracle은 중첩하지 않는다(첫 */에서 끝남)
            Assert.Equal((SqlSpanKind.Comment, "/* a /* b */"), Spans(script, SqlLexRules.Oracle)[0]);
        }

        [Fact]
        public void Postgres_BeginIsTransaction_NotBlock_AndSlashIsNotSeparator()
        {
            Assert.Equal(new[] { "BEGIN", "UPDATE t SET a = 1", "COMMIT" }, Texts("BEGIN;\nUPDATE t SET a = 1;\nCOMMIT;", Pg));
            // '/'만 있는 줄은 PostgreSQL에서 구분자가 아니다
            Assert.Single(SqlScript.Split("SELECT 4\n/\n2;", Pg));
            // q'…'는 Oracle 문법 — PostgreSQL에서는 q 다음 보통 문자열
            Assert.Equal(new[] { (SqlSpanKind.String, "'[a'") }, Spans("SELECT q'[a';", Pg));
        }

        // ---------- SQL Server ----------

        [Fact]
        public void SqlServer_GoSeparatesBatches()
        {
            const string script = "CREATE PROCEDURE dbo.p AS\nBEGIN\n  SELECT 1;\n  SELECT 2;\nEND\nGO\nexec dbo.p;\ngo\nSELECT 3";
            var parts = SqlScript.Split(script, Ms);
            Assert.Equal(3, parts.Count);
            Assert.True(parts[0].IsPlSqlBlock);
            Assert.EndsWith("END", parts[0].Text);
            Assert.DoesNotContain("GO", parts[0].Text);
            Assert.Equal("exec dbo.p", parts[1].Text);
            Assert.Equal("SELECT 3", parts[2].Text);
        }

        [Fact]
        public void SqlServer_GoOnlyAsWholeLine()
        {
            // 문자열·주석 안의 GO, GOTO, 줄 가운데 GO는 구분자가 아니다
            const string script = "SELECT 'x\nGO\ny' AS a;\n/* \nGO\n */ SELECT 1 GO_ON\nGOTO lbl;\n";
            // 문장은 둘: 문자열 문장 하나, 주석 뒤 "SELECT 1 GO_ON\nGOTO lbl"(사이에 ';'·빈 줄이 없어 한 문장)
            var parts = SqlScript.Split(script, Ms);
            Assert.Equal(2, parts.Count);
            Assert.Contains("\nGO\n", parts[0].Text);
            Assert.EndsWith("SELECT 1 GO_ON\nGOTO lbl", parts[1].Text);
            // 앞뒤 공백·탭은 괜찮다
            Assert.Equal(2, SqlScript.Split("SELECT 1\n  \tGo  \nSELECT 2", Ms).Count);
        }

        [Fact]
        public void SqlServer_BracketIdentifier_IsNotCommentOrString()
        {
            const string script = "SELECT [a -- b], [x]]y;z] FROM [t];\nSELECT 2;";
            Assert.Equal(2, SqlScript.Split(script, Ms).Count);
            Assert.Empty(SqlScript.HighlightSpans(script, Ms));
            // Oracle·PostgreSQL에서는 [가 기호일 뿐이다(-- 뒤가 주석)
            Assert.Contains(SqlScript.HighlightSpans(script, SqlLexRules.Oracle), s => s.Kind == SqlSpanKind.Comment);
        }

        [Fact]
        public void SqlServer_BeginTran_IsStatement_AndCreateOrAlterIsBlock()
        {
            Assert.Equal(new[] { "BEGIN TRAN", "UPDATE t SET a = 1", "COMMIT" }, Texts("BEGIN TRAN;\nUPDATE t SET a = 1;\nCOMMIT;", Ms));
            Assert.Equal(3, SqlScript.Split("BEGIN TRANSACTION;\nSELECT 1;\nCOMMIT TRANSACTION;", Ms).Count);
            var proc = SqlScript.Split("CREATE OR ALTER PROCEDURE p AS\nBEGIN\n  SELECT 1;\n  SELECT 2;\nEND\nGO\nSELECT 3;", Ms);
            Assert.Equal(2, proc.Count);
            Assert.True(proc[0].IsPlSqlBlock);
            // '/' 줄은 SQL Server에서 구분자가 아니다
            Assert.Single(SqlScript.Split("SELECT 4\n/\n2;", Ms));
        }

        [Fact]
        public void SplitForRunAndAtCaretAndParse_FollowRules()
        {
            const string script = "SELECT 1;\nDO $$ BEGIN PERFORM 1; END $$;\nSELECT 2;";
            Assert.Equal(3, SqlScript.SplitForRun(script, Pg).Count);
            var caret = script.IndexOf("PERFORM");
            Assert.StartsWith("DO $$", SqlScript.AtCaret(script, caret, Pg).Text);
            Assert.Equal("DO $$ BEGIN PERFORM 1; END $$", SqlScript.Parse("DO $$ BEGIN PERFORM 1; END $$;", Pg).Text);
            Assert.Equal("SELECT 1", SqlScript.Parse("SELECT 1\nGO", Ms).Text);
        }

        [Fact]
        public void HistoryInsertText_UsesBlockEndOfRules()
        {
            Assert.Equal("BEGIN NULL; END;\n/\n\n", WorkspaceLogic.HistoryInsertText("BEGIN NULL; END;"));
            Assert.Equal("CREATE PROCEDURE p AS SELECT 1\nGO\n\n", WorkspaceLogic.HistoryInsertText("CREATE PROCEDURE p AS SELECT 1", Ms));
            Assert.Equal("DO $$ BEGIN END $$;\n\n", WorkspaceLogic.HistoryInsertText("DO $$ BEGIN END $$", Pg));
        }
    }
}

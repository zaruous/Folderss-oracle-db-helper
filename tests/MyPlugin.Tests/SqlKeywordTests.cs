using System.Collections.Generic;
using System.Linq;
using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    /// <summary>
    /// SqlScript.EditorSpans의 키워드: 문장·절·조건·테이블 정의(DDL)·자료형을 DBMS별 목록(SqlLexRules.Keywords)으로 찾는다.
    /// 문자열·주석·따옴표 식별자 안과 . : @ &amp; 바로 뒤 낱말(t.date, :from, @from)은 키워드가 아니다.
    /// </summary>
    public class SqlKeywordTests
    {
        // 강조 낱말(키워드와 자료형) — 아래 기대값은 칠하는 낱말 전부
        private static List<string> Keywords(string script, SqlLexRules rules)
        {
            return SqlScript.EditorSpans(script, rules).Where(s => s.Kind == SqlSpanKind.Keyword || s.Kind == SqlSpanKind.DataType)
                .Select(s => script.Substring(s.Start, s.Length)).ToList();
        }

        private static List<(SqlSpanKind, string)> Kinds(string script, SqlLexRules rules)
        {
            return SqlScript.EditorSpans(script, rules).Where(s => s.Kind == SqlSpanKind.Keyword || s.Kind == SqlSpanKind.DataType)
                .Select(s => (s.Kind, script.Substring(s.Start, s.Length))).ToList();
        }

        [Fact]
        public void DataTypes_AreSeparateKind_PerDbms()
        {
            const SqlSpanKind K = SqlSpanKind.Keyword;
            const SqlSpanKind T = SqlSpanKind.DataType;
            Assert.Equal(new[] { (K, "CREATE"), (K, "TABLE"), (T, "NUMBER"), (K, "PRIMARY"), (K, "KEY"), (T, "VARCHAR2"), (T, "DATE"), (K, "DEFAULT"), (K, "SYSDATE") },
                Kinds("CREATE TABLE t (id NUMBER(10) PRIMARY KEY, name VARCHAR2(10), at DATE DEFAULT SYSDATE);", SqlLexRules.Oracle));
            Assert.Equal(new[] { (T, "TEXT"), (T, "JSONB"), (T, "TIMESTAMPTZ") }, Kinds("x TEXT, y JSONB, z TIMESTAMPTZ", SqlLexRules.Postgres));
            Assert.Equal(new[] { (T, "NVARCHAR"), (K, "IDENTITY"), (T, "DATETIME2") }, Kinds("a NVARCHAR(10), b IDENTITY, c DATETIME2", SqlLexRules.SqlServer));
            // 다른 DBMS 자료형은 아무것도 아님
            Assert.Empty(Kinds("VARCHAR2 JSONB", SqlLexRules.SqlServer));
        }

        [Fact]
        public void WithKeywords_AddsKeywords_RemovesKeywordsAndTypes_KeepsSplitRules()
        {
            var rules = SqlLexRules.Oracle.WithKeywords(new[] { "NVL", "DECODE" }, new[] { "level", "DATE" });
            Assert.Equal("Oracle", rules.Name);
            Assert.Equal(SqlLexRules.Oracle.BlockEndLine, rules.BlockEndLine);
            Assert.Equal(new[] { (SqlSpanKind.Keyword, "SELECT"), (SqlSpanKind.Keyword, "NVL"), (SqlSpanKind.Keyword, "decode"), (SqlSpanKind.Keyword, "FROM") },
                Kinds("SELECT NVL(a, 1), decode(b), level, DATE FROM t", rules));
            // 원래 규칙은 그대로
            Assert.Contains("level", Keywords("SELECT level FROM t", SqlLexRules.Oracle));
            Assert.Same(SqlLexRules.Oracle, SqlLexRules.Oracle.WithKeywords(null, new string[0]));
        }

        private const string OracleQuery = "SELECT e.emp_id, e.name AS \"Select\", e.date, COUNT(*) cnt\n"
            + "  FROM emp e\n"
            + "  LEFT OUTER JOIN dept d ON d.id = e.dept_id\n"
            + " WHERE e.status = 'SELECT FROM' -- where 주석\n"
            + "   AND e.hired BETWEEN :from AND :to\n"
            + "   AND e.select_flag IS NOT NULL /* order by */\n"
            + "   AND e.code IN (SELECT code FROM codes WHERE use_yn = 'Y')\n"
            + " GROUP BY e.emp_id HAVING COUNT(*) > 1 ORDER BY cnt DESC;\n";

        [Fact]
        public void Oracle_Query_ClausesAndConditions()
        {
            Assert.Equal(new[]
            {
                "SELECT", "AS", "FROM", "LEFT", "OUTER", "JOIN", "ON", "WHERE", "AND", "BETWEEN", "AND", "AND", "IS", "NOT", "NULL",
                "AND", "IN", "SELECT", "FROM", "WHERE", "GROUP", "BY", "HAVING", "ORDER", "BY", "DESC"
            }, Keywords(OracleQuery, SqlLexRules.Oracle));
        }

        [Fact]
        public void Oracle_CreateTable_TypesAndConstraints()
        {
            const string ddl = "CREATE TABLE t_order (\n"
                + "  id      NUMBER(10)     CONSTRAINT pk_order PRIMARY KEY,\n"
                + "  name    VARCHAR2(100)  NOT NULL,\n"
                + "  amount  NUMBER(12,2)   DEFAULT 0 CHECK (amount >= 0),\n"
                + "  created DATE           DEFAULT SYSDATE,\n"
                + "  dept_id NUMBER REFERENCES dept(id)\n"
                + ");\n"
                + "COMMENT ON TABLE t_order IS '주문';\n"
                + "CREATE UNIQUE INDEX ix_order_name ON t_order(name);\n";
            Assert.Equal(new[]
            {
                "CREATE", "TABLE", "NUMBER", "CONSTRAINT", "PRIMARY", "KEY", "VARCHAR2", "NOT", "NULL", "NUMBER", "DEFAULT", "CHECK",
                "DATE", "DEFAULT", "SYSDATE", "NUMBER", "REFERENCES", "COMMENT", "ON", "TABLE", "IS", "CREATE", "UNIQUE", "INDEX", "ON"
            }, Keywords(ddl, SqlLexRules.Oracle));
        }

        [Fact]
        public void Oracle_Dml_PlSql_Hierarchy()
        {
            const string script = "INSERT INTO t (a, b) VALUES (1, 'x');\n"
                + "UPDATE t SET a = 2 WHERE b IS NULL;\n"
                + "DELETE FROM t WHERE a < 0;\n"
                + "MERGE INTO t USING s ON (t.id = s.id) WHEN MATCHED THEN UPDATE SET t.v = s.v;\n"
                + "SELECT id FROM a MINUS SELECT id FROM b START WITH pid IS NULL CONNECT BY PRIOR id = pid;\n"
                + "BEGIN\n  FOR r IN (SELECT 1 FROM dual) LOOP NULL; END LOOP;\nEXCEPTION WHEN OTHERS THEN RAISE;\nEND;\n/\n";
            var words = Keywords(script, SqlLexRules.Oracle);
            Assert.Equal(new[] { "INSERT", "INTO", "VALUES", "UPDATE", "SET", "WHERE", "IS", "NULL", "DELETE", "FROM", "WHERE" }, words.Take(11));
            Assert.Contains("MERGE", words);
            Assert.Contains("MATCHED", words);
            Assert.Contains("MINUS", words);
            Assert.Contains("CONNECT", words);
            Assert.Contains("PRIOR", words);
            Assert.Contains("EXCEPTION", words);
            Assert.Contains("OTHERS", words);
            Assert.Contains("LOOP", words);
            // dual은 테이블, '/' 줄은 키워드가 아니다
            Assert.DoesNotContain("dual", words);
            Assert.DoesNotContain("/", words);
        }

        [Fact]
        public void NotKeywords_StringsCommentsIdentifiersQualifiedAndBindNames()
        {
            const string script = "SELECT 'from where' AS x, \"order\", t.date, t.select, :from, &where, e.select_flag, fromage, selected\n"
                + "-- select from where\n"
                + "/* group by\n order by */ FROM t;\n";
            Assert.Equal(new[] { "SELECT", "AS", "FROM" }, Keywords(script, SqlLexRules.Oracle));
        }

        [Fact]
        public void CaseInsensitive_KeepsOriginalText()
        {
            Assert.Equal(new[] { "select", "From", "wHeRe", "and" }, Keywords("select a From t wHeRe x = 1 and y = 2", SqlLexRules.Oracle));
        }

        [Fact]
        public void Postgres_OwnWords_DollarBodyIsString()
        {
            const string script = "SELECT id FROM t WHERE name ILIKE 'a%' ORDER BY id LIMIT 10 OFFSET 5;\n"
                + "INSERT INTO t (id) VALUES (1) ON CONFLICT DO NOTHING RETURNING id;\n"
                + "CREATE TABLE p (id BIGSERIAL PRIMARY KEY, body TEXT, meta JSONB, at TIMESTAMPTZ);\n"
                + "CREATE FUNCTION f() RETURNS int LANGUAGE plpgsql AS $$ BEGIN RETURN 1; END $$;\n";
            var words = Keywords(script, SqlLexRules.Postgres);
            Assert.Equal(new[] { "SELECT", "FROM", "WHERE", "ILIKE", "ORDER", "BY", "LIMIT", "OFFSET" }, words.Take(8));
            Assert.Contains("RETURNING", words);
            Assert.Contains("CONFLICT", words);
            Assert.Contains("BIGSERIAL", words);
            Assert.Contains("JSONB", words);
            Assert.Contains("TIMESTAMPTZ", words);
            // $$ 본문은 문자열이라 안의 BEGIN·RETURN·END는 키워드로 칠하지 않는다(문장 나누기와 같은 기준)
            Assert.Equal(new[] { "CREATE", "FUNCTION", "RETURNS", "INT", "LANGUAGE", "AS" },
                Keywords("CREATE FUNCTION f() RETURNS int LANGUAGE plpgsql AS $$ BEGIN RETURN 1; END $$;", SqlLexRules.Postgres).Select(w => w.ToUpperInvariant()));
            // Oracle 것은 PostgreSQL 키워드가 아니다
            Assert.Empty(Keywords("x MINUS y VARCHAR2", SqlLexRules.Postgres));
        }

        [Fact]
        public void SqlServer_Top_Brackets_Variables_GoLine()
        {
            const string script = "SELECT TOP 10 [select], @from, name FROM dbo.t WITH (NOLOCK) WHERE id = @id;\n"
                + "GO\n"
                + "DECLARE @x NVARCHAR(10) = N'from';\n"
                + "CREATE TABLE s (id INT IDENTITY(1,1) PRIMARY KEY CLUSTERED, at DATETIME2, ok BIT);\n"
                + "go\n";
            Assert.Equal(new[]
            {
                "SELECT", "TOP", "FROM", "WITH", "WHERE", "GO", "DECLARE", "NVARCHAR",
                "CREATE", "TABLE", "INT", "IDENTITY", "PRIMARY", "KEY", "CLUSTERED", "DATETIME2", "BIT", "go"
            }, Keywords(script, SqlLexRules.SqlServer));
            // Oracle 규칙에서는 TOP·[…]가 다르다: TOP은 키워드가 아니고 [select]의 select는 낱말
            Assert.DoesNotContain("TOP", Keywords(script, SqlLexRules.Oracle));
        }

        [Fact]
        public void EditorSpans_CommentsAndStrings_SameAsHighlightSpans()
        {
            foreach (var rules in new[] { SqlLexRules.Oracle, SqlLexRules.Postgres, SqlLexRules.SqlServer })
            {
                foreach (var script in new[] { OracleQuery, SqlHighlighterTests.Mixed })
                {
                    var editor = SqlScript.EditorSpans(script, rules).Where(s => s.Kind == SqlSpanKind.Comment || s.Kind == SqlSpanKind.String)
                        .Select(s => (s.Kind, s.Start, s.Length));
                    Assert.Equal(SqlScript.HighlightSpans(script, rules).Select(s => (s.Kind, s.Start, s.Length)), editor);
                }
            }
        }

        [Fact]
        public void EditorSpans_QuotedIdentifiersAreSpans()
        {
            var spans = SqlScript.EditorSpans("SELECT \"a\nb\", [c] FROM t", SqlLexRules.SqlServer);
            Assert.Equal(new[] { "\"a\nb\"", "[c]" },
                spans.Where(s => s.Kind == SqlSpanKind.Identifier).Select(s => "SELECT \"a\nb\", [c] FROM t".Substring(s.Start, s.Length)));
        }

        [Fact]
        public void KeywordSet_IgnoresNonAscii()
        {
            var set = new SqlKeywords(new[] { "SELECT" });
            Assert.True(set.Contains("select"));
            Assert.True(set.Contains("xSELECTx", 1, 6));
            Assert.False(set.Contains("셀렉트"));
            Assert.False(set.Contains("SELECTS"));
            Assert.Equal(1, set.Count);
        }
    }
}

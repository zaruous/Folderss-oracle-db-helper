using System;
using System.Linq;
using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    /// <summary>TableInfo(F4): 대상 찾기 SQL·순위·모호함, 정보 SQL, 정보 글. 여러 문장 실행·빠른 조회의 문장(WorkspaceLogic).</summary>
    public class TableInfoTests
    {
        private static ResolvedObject Candidate(string owner, string name, int priority, string via = null)
        {
            return new ResolvedObject { Owner = owner, Name = name, Type = "TABLE", Priority = priority, Via = via };
        }

        // ---------- 대상 찾기 ----------

        [Fact]
        public void ResolveTable_WithoutOwner_SearchesSchemaSynonymsPublicAndOthers()
        {
            var query = TableInfo.ResolveTable(null, "EMP");

            Assert.Contains("SYS_CONTEXT('USERENV','CURRENT_SCHEMA')", query.Sql);
            Assert.Contains("s.OWNER = 'PUBLIC'", query.Sql);
            Assert.Contains(", 4 FROM ALL_OBJECTS o", query.Sql);
            Assert.Contains("s.DB_LINK IS NULL", query.Sql);
            Assert.EndsWith("ORDER BY PRI, OWNER", query.Sql);
            Assert.Equal(new[] { "name" }, query.Parameters.Select(p => p.Key).ToArray());
            Assert.Equal("EMP", query.Parameters[0].Value);
        }

        [Fact]
        public void ResolveTable_WithOwner_OnlyThatSchema()
        {
            var query = TableInfo.ResolveTable("SCOTT", "EMP");

            Assert.DoesNotContain("PUBLIC", query.Sql);
            Assert.DoesNotContain(", 4 FROM", query.Sql);
            Assert.Contains("o.OWNER = :owner", query.Sql);
            Assert.Contains("s.OWNER = :owner", query.Sql);
            Assert.Equal(new[] { "owner", "name" }, query.Parameters.Select(p => p.Key).ToArray());
        }

        [Fact]
        public void Resolve_PicksBestPriority()
        {
            var result = TableInfo.Resolve(new[]
            {
                Candidate("HR", "EMP", 4),
                Candidate("SCOTT", "EMP", 3, "PUBLIC.EMP"),
                Candidate("APP", "EMP", 4)
            });

            Assert.Equal("SCOTT", result.Object.Owner);
            Assert.Equal("PUBLIC.EMP", result.Object.Via);
            Assert.Empty(result.AmbiguousOwners);
        }

        [Fact]
        public void Resolve_OtherSchemasOnly_SingleOwnerIsFine_ManyIsAmbiguous()
        {
            Assert.Equal("SAMPLE1", TableInfo.Resolve(new[] { Candidate("SAMPLE1", "ORDERS", 4) }).Object.Owner);

            var ambiguous = TableInfo.Resolve(new[] { Candidate("SAMPLE1", "ORDERS", 4), Candidate("DBH_IT", "ORDERS", 4) });
            Assert.Null(ambiguous.Object);
            Assert.Equal(new[] { "DBH_IT", "SAMPLE1" }, ambiguous.AmbiguousOwners.ToArray());
            Assert.Contains("스키마를 붙여(예: DBH_IT.ORDERS)", TableInfo.NotUniqueMessage("ORDERS", ambiguous.AmbiguousOwners, "ORDERS"));
        }

        [Fact]
        public void Resolve_Nothing_IsNotFound()
        {
            var result = TableInfo.Resolve(new ResolvedObject[0]);
            Assert.Null(result.Object);
            Assert.Empty(result.AmbiguousOwners);
            Assert.Null(TableInfo.Resolve(null).Object);
        }

        // ---------- 정보 SQL ----------

        [Fact]
        public void Columns_AddsCommentsToTreeColumnQuery()
        {
            var query = TableInfo.Columns("SAMPLE1", "ORDERS");

            Assert.Contains("cc.COMMENTS FROM ALL_TAB_COLUMNS c LEFT JOIN ALL_COL_COMMENTS cc", query.Sql);
            Assert.Contains("ORDER BY c.COLUMN_ID", query.Sql);
            Assert.Equal(new[] { "owner", "name" }, query.Parameters.Select(p => p.Key).ToArray());
        }

        [Fact]
        public void IndexesAndConstraints_Sql()
        {
            Assert.Contains("LISTAGG(ic.COLUMN_NAME", TableInfo.Indexes("A", "B").Sql);
            var constraints = TableInfo.Constraints("A", "B").Sql;
            Assert.Contains("CONSTRAINT_TYPE IN ('P','U','R','C')", constraints);
            Assert.Contains("NOT (c.CONSTRAINT_TYPE = 'C' AND c.GENERATED = 'GENERATED NAME')", constraints);
            Assert.DoesNotContain("SEARCH_CONDITION", constraints); // LONG 열은 읽지 않는다
        }

        [Theory]
        [InlineData("P", "기본 키")]
        [InlineData("U", "고유")]
        [InlineData("R", "외래 키")]
        [InlineData("C", "검사")]
        [InlineData("X", "X")]
        public void ConstraintTypeLabel(string type, string expected)
        {
            Assert.Equal(expected, TableInfo.ConstraintTypeLabel(type));
        }

        // ---------- 정보 글 ----------

        [Fact]
        public void Facts_Table()
        {
            var info = new TableDescription
            {
                Type = "TABLE",
                NumRows = 1000,
                LastAnalyzed = new DateTime(2026, 10, 3, 11, 2, 0),
                Created = new DateTime(2026, 10, 1, 9, 0, 0),
                Via = "PUBLIC.ORD"
            };
            info.Columns.Add(new DescribedColumn());

            Assert.Equal("행 수 1,000 (통계 2026-10-03 11:02) · 만든 날 2026-10-01 09:00 · 동의어 PUBLIC.ORD로 찾음 · 열 1개", TableInfoText.Facts(info));
        }

        [Fact]
        public void Facts_ViewAndNoStats()
        {
            Assert.Equal("열 0개", TableInfoText.Facts(new TableDescription { Type = "VIEW" }));
            Assert.StartsWith("행 수 통계 없음", TableInfoText.Facts(new TableDescription { Type = "TABLE" }));
        }

        // ---------- 여러 문장 실행·빠른 조회 문장 ----------

        [Theory]
        [InlineData("SELECT * FROM SAMPLE1.AUDIT_LOG", "SAMPLE1.AUDIT_LOG")]
        [InlineData("select a from  emp e where 1=1", "emp")]
        [InlineData("SELECT * FROM \"My Tab\"", "\"My Tab\"")]
        [InlineData("SELECT 'FROM x' AS a, 1 /* FROM y */ FROM sample1 . dept", "sample1.dept")]
        [InlineData("-- FROM z\nSELECT SYSDATE FROM DUAL", "DUAL")]
        [InlineData("SELECT 1", null)]
        [InlineData(null, null)]
        public void ResultSource(string sql, string expected)
        {
            Assert.Equal(expected, WorkspaceLogic.ResultSource(sql));
        }

        [Fact]
        public void ResultTabTitle()
        {
            Assert.Equal("결과 2 · SAMPLE1.ATTACHMENT (5행)", WorkspaceLogic.ResultTabTitle(2, "SELECT * FROM SAMPLE1.ATTACHMENT", 5, false));
            Assert.Equal("결과 1 (1,200행+)", WorkspaceLogic.ResultTabTitle(1, "SELECT 1", 1200, true));
        }

        [Fact]
        public void ScriptMessages()
        {
            Assert.Equal("[2/5] ", WorkspaceLogic.ScriptStep(2, 5));
            Assert.StartsWith("스크립트 실행을 마쳤습니다 — 문장 3개 중 3개 실행, 조회 결과 2개", WorkspaceLogic.ScriptSummary(3, 3, 2, 0, false, TimeSpan.FromSeconds(1)));
            Assert.StartsWith("2번째 문장에서 오류로 멈췄습니다 — 문장 3개 중 1개 실행", WorkspaceLogic.ScriptSummary(1, 3, 1, 2, false, TimeSpan.Zero));
            Assert.StartsWith("취소해 멈췄습니다", WorkspaceLogic.ScriptSummary(1, 3, 0, 0, true, TimeSpan.Zero));
            Assert.Equal("문장 3/3 · 결과 2개 —", WorkspaceLogic.ScriptLead(3, 3, 2, 0, false));
            Assert.Equal("문장 1/3 · 결과 1개 (2번째에서 오류) —", WorkspaceLogic.ScriptLead(1, 3, 1, 2, false));
            Assert.Contains("한 번에 결과 20개까지", WorkspaceLogic.TooManyResultsMessage(21));
        }

        [Fact]
        public void RunningStatus_ShowsProgressForScripts()
        {
            Assert.Contains("(2/5)", WorkspaceLogic.RunningStatus(TimeSpan.FromSeconds(1), false, "2/5").PlainText);
            Assert.DoesNotContain("/", WorkspaceLogic.RunningStatus(TimeSpan.FromSeconds(1), false).PlainText.Replace("초", ""));
        }

        [Fact]
        public void QuickQueryMessage()
        {
            Assert.Equal("빠른 조회: SAMPLE1.ORDERS 앞 100행 → 'SQL 2' 탭 (편집기는 그대로, 실행 기록에 남음)", WorkspaceLogic.QuickQueryMessage("SAMPLE1.ORDERS", "SQL 2"));
            Assert.Equal("빠른 조회: SAMPLE1.ORDERS WHERE ID = 1 앞 100행 → 'SQL 2' 탭 (편집기는 그대로, 실행 기록에 남음)", WorkspaceLogic.QuickQueryMessage("SAMPLE1.ORDERS", "SQL 2", "ID = 1"));
        }

        [Theory]
        [InlineData("", "")]
        [InlineData("   ", "")]
        [InlineData("ID = 1", "ID = 1")]
        [InlineData("  where ID = 1 ", "ID = 1")]
        [InlineData("WHERE", "")]
        [InlineData("WHEREVER = 1", "WHEREVER = 1")]
        [InlineData("NAME = 'a;b -- /* ) ('", "NAME = 'a;b -- /* ) ('")]
        [InlineData("NAME = 'it''s'", "NAME = 'it''s'")]
        [InlineData("\"odd;name\" = 1", "\"odd;name\" = 1")]
        [InlineData("STATUS IN ('A', 'B') AND (QTY > 0 OR QTY IS NULL)", "STATUS IN ('A', 'B') AND (QTY > 0 OR QTY IS NULL)")]
        [InlineData("DEPT_ID IN (SELECT ID FROM DEPT WHERE NAME LIKE 'S%')", "DEPT_ID IN (SELECT ID FROM DEPT WHERE NAME LIKE 'S%')")]
        [InlineData("NAME = N'가나'", "NAME = N'가나'")]
        [InlineData("SID IN (SELECT SID FROM V$SESSION)", "SID IN (SELECT SID FROM V$SESSION)")]
        [InlineData("A / 2 > 1", "A / 2 > 1")]
        public void QuickQueryWhere_Accepts(string input, string expected)
        {
            string error;
            Assert.Equal(expected, WorkspaceLogic.QuickQueryWhere(input, out error));
            Assert.Null(error);
        }

        [Theory]
        [InlineData("1=1; DELETE FROM ORDERS")]
        [InlineData("1=1) ; DELETE FROM ORDERS; --")]
        [InlineData("1=1) UNION SELECT * FROM SECRET WHERE (1=1")]
        [InlineData("1=1) FOR UPDATE --")]
        [InlineData("ID = 1 -- 나머지")]
        [InlineData("ID = 1 /* x */")]
        [InlineData("(ID = 1")]
        [InlineData("NAME = 'abc")]
        [InlineData("\"NAME = 1")]
        [InlineData("NAME = q'[a' ) ; DELETE FROM T; --]'")]
        [InlineData("NAME = E'\\'' ) ; DELETE FROM T; SELECT '")]
        [InlineData("NAME = U&'x'")]
        [InlineData("[x'] = 1) ; DROP TABLE T; --'")]
        [InlineData("ARR[']'] = 1) ; DELETE FROM T; --'")]
        [InlineData("NAME = $$a$$")]
        [InlineData("NAME = $t$')$t$")]
        [InlineData("NAME = 1$$'$$ ) ; DELETE FROM T; SELECT '1")]
        public void QuickQueryWhere_RejectsEscapes(string input)
        {
            string error;
            Assert.Null(WorkspaceLogic.QuickQueryWhere(input, out error));
            Assert.False(string.IsNullOrEmpty(error));
        }

        [Fact]
        public void QuickQueryStatement_WrapsConditionAndStaysOneQuery()
        {
            Assert.Equal(WorkspaceLogic.SelectStatement("SAMPLE1", "ORDERS"), WorkspaceLogic.QuickQueryStatement("SAMPLE1", "ORDERS", ""));
            var sql = WorkspaceLogic.QuickQueryStatement("SAMPLE1", "ORDERS", "STATUS = 'A'");
            Assert.Equal("SELECT *\n  FROM SAMPLE1.ORDERS\n WHERE (STATUS = 'A')", sql);
            var statement = SqlScript.Parse(sql);
            Assert.Equal(SqlKind.Query, statement.Kind);
            Assert.False(statement.ForUpdate);
            Assert.True(WorkspaceLogic.AllowedOnReadOnly(statement));
        }
    }
}

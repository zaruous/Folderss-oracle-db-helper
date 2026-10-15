using System;
using System.Collections.Generic;
using System.Linq;
using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    public class RowEditLogicTests
    {
        [Theory]
        [InlineData("VARCHAR2(10)", ColumnValueKind.Text)]
        [InlineData("VARCHAR2(10 CHAR)", ColumnValueKind.Text)]
        [InlineData("NCHAR(3)", ColumnValueKind.Text)]
        [InlineData("character varying(20)", ColumnValueKind.Text)]
        [InlineData("nvarchar(50)", ColumnValueKind.Text)]
        [InlineData("text", ColumnValueKind.Text)]
        [InlineData("NUMBER", ColumnValueKind.Number)]
        [InlineData("NUMBER(7,2)", ColumnValueKind.Number)]
        [InlineData("integer", ColumnValueKind.Number)]
        [InlineData("decimal(10,2)", ColumnValueKind.Number)]
        [InlineData("BINARY_DOUBLE", ColumnValueKind.Number)]
        [InlineData("DATE", ColumnValueKind.DateTime)]
        [InlineData("TIMESTAMP(6)", ColumnValueKind.DateTime)]
        [InlineData("timestamp without time zone", ColumnValueKind.DateTime)]
        [InlineData("datetime2(7)", ColumnValueKind.DateTime)]
        [InlineData("TIMESTAMP(6) WITH TIME ZONE", ColumnValueKind.Unsupported)]
        [InlineData("TIMESTAMP(6) WITH LOCAL TIME ZONE", ColumnValueKind.Unsupported)]
        [InlineData("CLOB", ColumnValueKind.Unsupported)]
        [InlineData("BLOB", ColumnValueKind.Unsupported)]
        [InlineData("RAW(16)", ColumnValueKind.Unsupported)]
        [InlineData("INTERVAL DAY(2) TO SECOND(6)", ColumnValueKind.Unsupported)]
        [InlineData("boolean", ColumnValueKind.Unsupported)]
        [InlineData("uuid", ColumnValueKind.Unsupported)]
        [InlineData("", ColumnValueKind.Unsupported)]
        [InlineData(null, ColumnValueKind.Unsupported)]
        public void KindOf_ClassifiesTypeLabels(string label, ColumnValueKind expected)
        {
            Assert.Equal(expected, RowEditLogic.KindOf(label));
        }

        [Fact]
        public void IsTruncated_DetectsFormatterSuffix()
        {
            Assert.True(RowEditLogic.IsTruncated("abc…(전체 5000자)"));
            Assert.True(RowEditLogic.IsTruncated("0xAB…(전체 3000바이트)"));
            Assert.False(RowEditLogic.IsTruncated("abc"));
            Assert.False(RowEditLogic.IsTruncated(null));
            Assert.False(RowEditLogic.CanEditValue(ColumnValueKind.Text, "abc…(전체 5000자)"));
            Assert.True(RowEditLogic.CanEditValue(ColumnValueKind.Text, "abc"));
            Assert.False(RowEditLogic.CanEditValue(ColumnValueKind.Unsupported, "abc"));
        }

        [Fact]
        public void Validate_And_ToDbValue()
        {
            Assert.Null(RowEditLogic.Validate(ColumnValueKind.Number, "12.5"));
            Assert.Null(RowEditLogic.Validate(ColumnValueKind.Number, "-3"));
            Assert.Null(RowEditLogic.Validate(ColumnValueKind.Number, "1E5"));
            Assert.NotNull(RowEditLogic.Validate(ColumnValueKind.Number, "abc"));
            Assert.NotNull(RowEditLogic.Validate(ColumnValueKind.Number, "12,5"));
            Assert.Null(RowEditLogic.Validate(ColumnValueKind.DateTime, "2024-03-05 13:04:05"));
            Assert.Null(RowEditLogic.Validate(ColumnValueKind.DateTime, "2024-03-05 13:04:05.123"));
            Assert.Null(RowEditLogic.Validate(ColumnValueKind.DateTime, "2024-03-05"));
            Assert.NotNull(RowEditLogic.Validate(ColumnValueKind.DateTime, "05/03/2024"));
            Assert.NotNull(RowEditLogic.Validate(ColumnValueKind.DateTime, "2024-13-01"));
            Assert.Null(RowEditLogic.Validate(ColumnValueKind.Text, "anything"));
            // NULL은 언제나 통과
            Assert.Null(RowEditLogic.Validate(ColumnValueKind.Number, null));
            Assert.Null(RowEditLogic.Validate(ColumnValueKind.DateTime, null));
            Assert.NotNull(RowEditLogic.Validate(ColumnValueKind.Unsupported, "x"));

            Assert.Equal(DBNull.Value, RowEditLogic.ToDbValue(ColumnValueKind.Text, null));
            Assert.Equal(12.5m, RowEditLogic.ToDbValue(ColumnValueKind.Number, "12.5"));
            Assert.Equal(new DateTime(2024, 3, 5, 13, 4, 5), RowEditLogic.ToDbValue(ColumnValueKind.DateTime, "2024-03-05 13:04:05"));
            Assert.Equal(new DateTime(2024, 3, 5, 13, 4, 5).AddTicks(1230000), RowEditLogic.ToDbValue(ColumnValueKind.DateTime, "2024-03-05 13:04:05.123"));
            Assert.Equal("abc", RowEditLogic.ToDbValue(ColumnValueKind.Text, "abc"));
            // decimal에 안 들어가는 큰 수는 글자 그대로(DB가 변환)
            Assert.Equal("1234567890123456789012345678901234567", RowEditLogic.ToDbValue(ColumnValueKind.Number, "1234567890123456789012345678901234567"));
        }

        [Fact]
        public void Describe_MapsPrimaryKeysToResultColumns()
        {
            var columns = Columns("ID:NUMBER", "NAME:VARCHAR2(10)", "NOTE:CLOB");
            var table = RowEditLogic.Describe("HR", "EMP", columns, Meta("ID", true, "NAME", false, "NOTE", false), ":");
            Assert.True(table.CanEdit);
            Assert.Equal(new[] { 0 }, table.KeyIndexes);
            Assert.True(table.IsKey(0));
            Assert.False(table.IsKey(1));
            Assert.Equal(ColumnValueKind.Number, table.KindAt(0));
            Assert.Equal(ColumnValueKind.Unsupported, table.KindAt(2));
            Assert.Equal(ColumnValueKind.Unsupported, table.KindAt(9));

            // 기본 키 없음(뷰 등)
            var noKey = RowEditLogic.Describe("HR", "V_EMP", columns, Meta("ID", false), ":");
            Assert.False(noKey.CanEdit);
            Assert.Contains("기본 키", noKey.DisabledReason);

            // 데이터 사전의 기본 키 열이 결과에 없음
            var missing = RowEditLogic.Describe("HR", "EMP", columns, Meta("EMP_ID", true), ":");
            Assert.False(missing.CanEdit);
            Assert.Contains("EMP_ID", missing.DisabledReason);

            // 이름은 대소문자 무시로도 맞춘다(PostgreSQL 소문자)
            var lower = RowEditLogic.Describe("public", "emp", Columns("id:integer", "name:text"), Meta("ID", true), ":");
            Assert.True(lower.CanEdit);
            Assert.Equal(new[] { 0 }, lower.KeyIndexes);

            // 기본 키 형식을 바인드할 수 없으면 편집 불가
            var rawKey = RowEditLogic.Describe("HR", "T", Columns("ID:RAW(16)", "V:NUMBER"), Meta("ID", true), ":");
            Assert.False(rawKey.CanEdit);

            Assert.False(RowEditLogic.Unavailable("HR", "EMP", "사전을 읽지 못함").CanEdit);
            Assert.Equal("사전을 읽지 못함", RowEditLogic.Unavailable("HR", "EMP", "사전을 읽지 못함").DisabledReason);
        }

        [Fact]
        public void UpdateStatement_SetsOnlyChangedColumns_WhereByOriginalKey()
        {
            var table = RowEditLogic.Describe("HR", "EMP", Columns("ID:NUMBER", "NAME:VARCHAR2(10)", "HIRED:DATE", "lower:NUMBER"), Meta("ID", true), ":");
            var original = new[] { "7", "KIM", "2024-01-02 03:04:05", "1" };
            var current = new[] { "8", "KIM", null, "1" };

            var query = RowEditLogic.UpdateStatement(table, original, current);

            Assert.Equal("UPDATE HR.EMP SET ID = :s0, HIRED = :s1 WHERE ID = :k0", query.Sql);
            Assert.Equal(new[] { "s0", "s1", "k0" }, query.Parameters.Select(p => p.Key));
            Assert.Equal(8m, query.Parameters[0].Value);
            Assert.Equal(DBNull.Value, query.Parameters[1].Value);
            // WHERE는 원래 키 값
            Assert.Equal(7m, query.Parameters[2].Value);

            Assert.Null(RowEditLogic.UpdateStatement(table, original, (string[])original.Clone()));

            var lower = RowEditLogic.UpdateStatement(table, original, new[] { "7", "KIM", "2024-01-02 03:04:05", "2" });
            Assert.Equal("UPDATE HR.EMP SET \"lower\" = :s0 WHERE ID = :k0", lower.Sql);
        }

        [Fact]
        public void DeleteStatement_UsesCompositeKey_AndNullKeyIsNull()
        {
            var table = RowEditLogic.Describe("HR", "EMP HIST", Columns("A:NUMBER", "B:VARCHAR2(3)", "V:NUMBER"), Meta("A", true, "B", true), "@");

            var query = RowEditLogic.DeleteStatement(table, new[] { "1", "x", "9" });
            Assert.Equal("DELETE FROM HR.\"EMP HIST\" WHERE A = @k0 AND B = @k1", query.Sql);
            Assert.Equal(1m, query.Parameters[0].Value);
            Assert.Equal("x", query.Parameters[1].Value);

            var nullKey = RowEditLogic.DeleteStatement(table, new[] { "1", null, "9" });
            Assert.Equal("DELETE FROM HR.\"EMP HIST\" WHERE A = @k0 AND B IS NULL", nullKey.Sql);
            Assert.Single(nullKey.Parameters);

            Assert.Null(RowEditLogic.DeleteStatement(RowEditLogic.Unavailable("HR", "EMP", "x"), new[] { "1" }));
        }

        // DBMS마다 이름이 다른 큰 데이터·이진 형식: 모두 편집할 수 없는 형식이다
        [Theory]
        [InlineData("CLOB")]
        [InlineData("NCLOB")]
        [InlineData("BLOB")]
        [InlineData("RAW(2000)")]
        [InlineData("LONG")]
        [InlineData("LONG RAW")]
        [InlineData("XMLTYPE")]
        [InlineData("bytea")]
        [InlineData("xml")]
        [InlineData("jsonb")]
        [InlineData("varbinary(max)")]
        [InlineData("image")]
        public void LobTypes_AreUnsupported(string label)
        {
            Assert.Equal(ColumnValueKind.Unsupported, RowEditLogic.KindOf(label));
            Assert.NotNull(RowEditLogic.EditBlockReason(RowEditLogic.KindOf(label), "x"));
        }

        [Theory]
        [InlineData("NOTE:CLOB")]
        [InlineData("DOC:XMLTYPE")]
        [InlineData("data:bytea")]
        [InlineData("doc:xml")]
        [InlineData("BIN:varbinary(max)")]
        public void UpdateStatement_RejectsChangedLobColumn(string spec)
        {
            var table = RowEditLogic.Describe("HR", "T", Columns("ID:NUMBER", spec, "NAME:VARCHAR2(10)"), Meta("ID", true), ":");
            var original = new[] { "1", "abc…(전체 5000자)", "KIM" };
            var current = new[] { "1", "abc", "LEE" };

            var reason = RowEditLogic.UpdateBlockReason(table, original, current);
            Assert.NotNull(reason);
            Assert.Contains("'" + spec.Substring(0, spec.IndexOf(':')) + "'", reason);
            Assert.Contains("이 형식은 편집할 수 없습니다", reason);
            var ex = Assert.Throws<InvalidOperationException>(() => RowEditLogic.UpdateStatement(table, original, current));
            Assert.Equal(reason, ex.Message);

            // NULL로 바꾸거나 NULL에서 채워도 거부(원래 값을 모른다)
            Assert.NotNull(RowEditLogic.UpdateBlockReason(table, original, new[] { "1", null, "KIM" }));
            Assert.NotNull(RowEditLogic.UpdateBlockReason(table, new[] { "1", null, "KIM" }, new[] { "1", "x", "KIM" }));
        }

        [Fact]
        public void UpdateStatement_LeavesUnchangedLobOutOfSet()
        {
            var table = RowEditLogic.Describe("HR", "T", Columns("ID:NUMBER", "NOTE:CLOB", "PHOTO:BLOB", "NAME:VARCHAR2(10)"), Meta("ID", true), ":");
            var original = new[] { "1", "abc…(전체 5000자)", "(BLOB 10바이트)", "KIM" };
            var current = new[] { "1", "abc…(전체 5000자)", "(BLOB 10바이트)", "LEE" };

            Assert.Null(RowEditLogic.UpdateBlockReason(table, original, current));
            var query = RowEditLogic.UpdateStatement(table, original, current);
            Assert.Equal("UPDATE HR.T SET NAME = :s0 WHERE ID = :k0", query.Sql);
            Assert.Equal("LEE", query.Parameters[0].Value);
            // LOB만 그대로면 바꿀 것이 없다
            Assert.Null(RowEditLogic.UpdateStatement(table, original, (string[])original.Clone()));
        }

        [Fact]
        public void UpdateStatement_RejectsTruncatedTextValue()
        {
            var table = RowEditLogic.Describe("HR", "T", Columns("ID:NUMBER", "MEMO:VARCHAR2(4000)", "BODY:text"), Meta("ID", true), ":");

            // 원래 값이 잘려 보이던 칸을 바꿈
            var reason = RowEditLogic.UpdateBlockReason(table, new[] { "1", "abc…(전체 4000자)", "x" }, new[] { "1", "abc", "x" });
            Assert.Contains("'MEMO'", reason);
            Assert.Contains("값이 잘려 보여", reason);
            // 다른 행의 잘린 표시 글이 새 값으로 들어옴(다른 경로로 값을 바꾼 경우)
            Assert.Throws<InvalidOperationException>(() => RowEditLogic.UpdateStatement(table, new[] { "1", "a", "x" }, new[] { "1", "a", "y…(전체 9000자)" }));
            // 잘린 값이 그대로면 SET에서 빠진다
            var query = RowEditLogic.UpdateStatement(table, new[] { "1", "abc…(전체 4000자)", "x" }, new[] { "1", "abc…(전체 4000자)", "y" });
            Assert.Equal("UPDATE HR.T SET BODY = :s0 WHERE ID = :k0", query.Sql);
        }

        [Fact]
        public void InsertStatement_RejectsTruncatedValue_SkipsLob()
        {
            var table = RowEditLogic.Describe("HR", "T", Columns("ID:NUMBER", "MEMO:VARCHAR2(4000)", "NOTE:CLOB"), Meta("ID", true), ":");

            var reason = RowEditLogic.InsertBlockReason(table, new[] { "2", "abc…(전체 4000자)", null });
            Assert.Contains("'MEMO'", reason);
            Assert.Throws<InvalidOperationException>(() => RowEditLogic.InsertStatement(table, new[] { "2", "abc…(전체 4000자)", null }));
            // LOB 열은 값이 있어도 INSERT에서 빠진다(거부하지 않음)
            Assert.Null(RowEditLogic.InsertBlockReason(table, new[] { "2", "abc", "x…(전체 5000자)" }));
            Assert.Equal("INSERT INTO HR.T (ID, MEMO) VALUES (:i0, :i1)", RowEditLogic.InsertStatement(table, new[] { "2", "abc", "x…(전체 5000자)" }).Sql);
            // [복제]의 처음 값은 언제나 통과
            Assert.Null(RowEditLogic.InsertBlockReason(table, RowEditLogic.CopyForInsert(table, new[] { "1", "abc…(전체 4000자)", "x…(전체 5000자)" })));
        }

        [Fact]
        public void UpdateBlockReason_PassesOrdinaryColumns()
        {
            var table = RowEditLogic.Describe("HR", "EMP", Columns("ID:NUMBER", "NAME:VARCHAR2(10)", "HIRED:DATE", "SAL:NUMBER(7,2)"), Meta("ID", true), ":");
            var original = new[] { "1", "KIM", "2024-01-02 00:00:00", "100" };
            var current = new[] { "1", null, "2024-02-03 00:00:00", "200.5" };

            Assert.Null(RowEditLogic.UpdateBlockReason(table, original, current));
            Assert.Equal("UPDATE HR.EMP SET NAME = :s0, HIRED = :s1, SAL = :s2 WHERE ID = :k0", RowEditLogic.UpdateStatement(table, original, current).Sql);
            Assert.Null(RowEditLogic.InsertBlockReason(table, current));
        }

        [Fact]
        public void InsertStatement_SkipsNullAndUnsupportedColumns()
        {
            var table = RowEditLogic.Describe("HR", "EMP", Columns("ID:NUMBER", "NAME:VARCHAR2(10)", "PHOTO:BLOB", "HIRED:DATE"), Meta("ID", true), ":");

            var query = RowEditLogic.InsertStatement(table, new[] { "8", null, "(BLOB 10바이트)", "2024-01-02" });
            // NULL(NAME)·LOB(PHOTO)는 빼서 DB 기본값이 들어가게
            Assert.Equal("INSERT INTO HR.EMP (ID, HIRED) VALUES (:i0, :i1)", query.Sql);
            Assert.Equal(8m, query.Parameters[0].Value);
            Assert.Equal(new DateTime(2024, 1, 2), query.Parameters[1].Value);

            // 키를 비우면(자동 번호) 키 없이
            Assert.Equal("INSERT INTO HR.EMP (NAME) VALUES (:i0)", RowEditLogic.InsertStatement(table, new[] { null, "LEE", null, null }).Sql);
            // 넣을 값이 없으면 null(Oracle에는 DEFAULT VALUES가 없다)
            Assert.Null(RowEditLogic.InsertStatement(table, new string[4]));
            Assert.Null(RowEditLogic.InsertStatement(RowEditLogic.Unavailable("HR", "EMP", "x"), new[] { "1" }));
        }

        [Fact]
        public void CopyForInsert_ClearsUnsupportedAndTruncatedValues()
        {
            var table = RowEditLogic.Describe("HR", "EMP", Columns("ID:NUMBER", "NOTE:VARCHAR2(4000)", "PHOTO:BLOB", "NAME:VARCHAR2(10)"), Meta("ID", true), ":");
            var copy = RowEditLogic.CopyForInsert(table, new[] { "7", "abc…(전체 9000자)", "(BLOB 10바이트)", null });
            // 잘린 표시 글·LOB 표시를 그대로 INSERT하지 않게 NULL로
            Assert.Equal(new[] { "7", null, null, null }, copy);
        }

        [Fact]
        public void InsertScript_WritesLiteralsPerProvider()
        {
            var columns = Columns("ID:NUMBER", "NAME:VARCHAR2(10)", "HIRED:DATE", "AT:TIMESTAMP(6)", "MEMO:VARCHAR2(10)");
            var rows = new[]
            {
                new[] { "7", "O'Neil", "2024-01-02 03:04:05", "2024-01-02 03:04:05.123", null },
                new[] { "8", "줄\n바꿈", null, null, "x" }
            };

            var oracle = RowEditLogic.InsertScript("HR", "EMP", columns, rows, "oracle");
            Assert.Equal(
                "INSERT INTO HR.EMP (ID, NAME, HIRED, AT, MEMO) VALUES (7, 'O''Neil', TO_DATE('2024-01-02 03:04:05', 'YYYY-MM-DD HH24:MI:SS'), TO_TIMESTAMP('2024-01-02 03:04:05.123', 'YYYY-MM-DD HH24:MI:SS.FF'), NULL);\n"
                + "INSERT INTO HR.EMP (ID, NAME, HIRED, AT, MEMO) VALUES (8, '줄\n바꿈', NULL, NULL, 'x');\n", oracle);

            // SQL Server: 글은 N'…', 날짜는 DATETIME2로 바꿔 넣는다
            var mssql = RowEditLogic.InsertScript("dbo", "EMP", Columns("ID:int", "NAME:nvarchar(10)", "HIRED:datetime"), new[] { new[] { "1", "김", "2024-01-02 00:00:00.0033333" } }, "mssql");
            Assert.Equal("INSERT INTO \"dbo\".EMP (ID, NAME, HIRED) VALUES (1, N'김', CAST('2024-01-02 00:00:00.0033333' AS DATETIME2));\n", mssql);

            // PostgreSQL: 날짜도 '…'
            Assert.Equal("'2024-01-02 03:04:05'", RowEditLogic.SqlLiteral(ColumnValueKind.DateTime, "timestamp", "2024-01-02 03:04:05", "postgres"));
        }

        [Fact]
        public void InsertScript_SkipsLobAndTruncatedValuesWithNote()
        {
            var columns = Columns("ID:NUMBER", "PHOTO:BLOB", "NOTE:VARCHAR2(4000)");
            var script = RowEditLogic.InsertScript("HR", "EMP", columns, new[] { new[] { "7", "(BLOB 10바이트)", "abc…(전체 9000자)" } }, "oracle");
            Assert.Equal("INSERT INTO HR.EMP (ID) VALUES (7);  -- 뺀 열: PHOTO(BLOB), NOTE(잘린 값)\n", script);

            var none = RowEditLogic.InsertScript("HR", "T", Columns("PHOTO:BLOB"), new[] { new[] { "(BLOB)" } }, "oracle");
            Assert.Equal("-- 넣을 수 있는 열이 없는 행  -- 뺀 열: PHOTO(BLOB)\n", none);
        }

        [Fact]
        public void Describe_ShowsBindValues()
        {
            var table = RowEditLogic.Describe("HR", "EMP", Columns("ID:NUMBER", "NAME:VARCHAR2(10)", "HIRED:DATE"), Meta("ID", true), ":");
            var query = RowEditLogic.UpdateStatement(table, new[] { "1", "a", null }, new[] { "1", "o'k", "2024-01-02 03:04:05" });
            Assert.Equal("UPDATE HR.EMP SET NAME = :s0, HIRED = :s1 WHERE ID = :k0  -- s0='o''k', s1='2024-01-02 03:04:05', k0=1", RowEditLogic.Describe(query));
            Assert.Equal("", RowEditLogic.Describe(null));
        }

        [Fact]
        public void Messages()
        {
            Assert.Equal("", RowEditLogic.PendingSummary(0, 0));
            Assert.Equal("수정 2 · 삭제 1", RowEditLogic.PendingSummary(2, 1));
            // 실행 순서(DELETE → UPDATE → INSERT)대로
            Assert.Equal("저장: DELETE 1행 · UPDATE 2행 (커밋 전 — [커밋]으로 확정, [롤백]으로 취소)", RowEditLogic.SavedMessage(2, 1));
            Assert.Equal("저장: DELETE 1행 · UPDATE 2행 · INSERT 3행 (커밋 전 — [커밋]으로 확정, [롤백]으로 취소)", RowEditLogic.SavedMessage(2, 1, 3));
            Assert.Equal("추가 3 · 수정 2 · 삭제 1", RowEditLogic.PendingSummary(2, 1, 3));
            Assert.Equal("저장: 변경 없음", RowEditLogic.SavedMessage(0, 0));
            Assert.Contains("0행", RowEditLogic.AffectedMismatch(0));
            Assert.Contains("2행", RowEditLogic.AffectedMismatch(2));
            Assert.Equal("@", RowEditLogic.ParameterPrefix("mssql"));
            Assert.Equal(":", RowEditLogic.ParameterPrefix("oracle"));
            Assert.Equal(":", RowEditLogic.ParameterPrefix(null));
            Assert.True(RowEditLogic.SameValues(new[] { "a", null }, new[] { "a", null }));
            Assert.False(RowEditLogic.SameValues(new[] { "a", null }, new[] { "a", "" }));
        }

        [Fact]
        public void DateTimeCompose_KeepsTimeAndFraction()
        {
            var day = new DateTime(2025, 12, 31);
            Assert.Equal("2025-12-31 13:04:05", ReplaceDatePart("2024-03-05 13:04:05", day));
            Assert.Equal("2025-12-31 13:04:05.123", ReplaceDatePart("2024-03-05 13:04:05.123", day));
            Assert.Equal("2025-12-31 00:00:00", ReplaceDatePart("2024-03-05", day));
            Assert.Equal("2025-12-31 00:00:00", ReplaceDatePart("", day));
            Assert.Equal("2025-12-31 00:00:00", ReplaceDatePart(null, day));
            Assert.Equal("2025-12-31 00:00:00", ReplaceDatePart("not a date", day));
            // 넘긴 날짜의 시각은 무시한다(달력은 날짜만 고른다)
            Assert.Equal("2025-12-31 13:04:05", ReplaceDatePart("2024-03-05 13:04:05", day.AddHours(9)));

            // 2024년 3월: 1일이 금요일 → 2월 25일(일)부터 42일
            var grid = RowEditLogic.MonthGrid(new DateTime(2024, 3, 15));
            Assert.Equal(42, grid.Length);
            Assert.Equal(new DateTime(2024, 2, 25), grid[0]);
            Assert.Equal(DayOfWeek.Sunday, grid[0].DayOfWeek);
            Assert.Equal(new DateTime(2024, 3, 1), grid[5]);
            Assert.Equal(new DateTime(2024, 4, 6), grid[41]);
            // 1일이 일요일인 달은 그 날부터
            Assert.Equal(new DateTime(2024, 9, 1), RowEditLogic.MonthGrid(new DateTime(2024, 9, 1))[0]);

            TimeSpan time;
            Assert.True(RowEditLogic.TryParseTime("13:04", out time));
            Assert.Equal(new TimeSpan(13, 4, 0), time);
            Assert.True(RowEditLogic.TryParseTime("9:05:07", out time));
            Assert.Equal(new TimeSpan(9, 5, 7), time);
            Assert.True(RowEditLogic.TryParseTime("23:59:59.5", out time));
            Assert.Equal(new TimeSpan(23, 59, 59).Add(TimeSpan.FromMilliseconds(500)), time);
            Assert.False(RowEditLogic.TryParseTime("24:00", out time));
            Assert.False(RowEditLogic.TryParseTime("13", out time));
            Assert.False(RowEditLogic.TryParseTime("", out time));
            Assert.False(RowEditLogic.TryParseTime("1:2:3:4", out time));
            Assert.Equal("13:04:05", RowEditLogic.TimeText(new TimeSpan(13, 4, 5)));
            Assert.Equal("13:04:05.5", RowEditLogic.TimeText(new TimeSpan(13, 4, 5).Add(TimeSpan.FromMilliseconds(500))));
            Assert.Equal("00:00:00", RowEditLogic.TimeText(TimeSpan.Zero));
            Assert.Equal(new TimeSpan(13, 4, 5), RowEditLogic.TimePartOf("2024-03-05 13:04:05"));
            Assert.Equal(TimeSpan.Zero, RowEditLogic.TimePartOf("abc"));
            Assert.Equal("2025-12-31 09:30:00", RowEditLogic.Compose(new DateTime(2025, 12, 31, 5, 5, 5), new TimeSpan(9, 30, 0)));

            Assert.Equal(new DateTime(2024, 3, 5), RowEditLogic.DatePartOf("2024-03-05 13:04:05"));
            Assert.Null(RowEditLogic.DatePartOf("abc"));
            Assert.Null(RowEditLogic.DatePartOf(null));
        }

        // 달력에서 날짜만 고를 때 DateTimeBox가 하는 합성(시각은 칸의 것을 그대로)
        private static string ReplaceDatePart(string text, DateTime date)
        {
            return RowEditLogic.Compose(date, RowEditLogic.TimePartOf(text));
        }

        // ---- 기본 키가 없는 테이블: 고유 키 → Oracle ROWID ----

        [Fact]
        public void ChooseKey_PrefersPrimaryKeyOverUniqueKeys()
        {
            var meta = new List<ColumnInfo> { Col("ID", false, "NUMBER", pk: true), Col("CODE", false, "VARCHAR2(10)") };
            var key = RowEditLogic.ChooseKey(meta, Keys("UK_CODE:CODE"), "oracle", true);
            Assert.Equal(RowKeyKind.PrimaryKey, key.Kind);
            Assert.Equal(new[] { "ID" }, key.Columns);
            Assert.Null(key.Name);
        }

        [Fact]
        public void ChooseKey_UniqueKey_SkipsNullable_ThenFewestColumns_ThenName()
        {
            var meta = new List<ColumnInfo>
            {
                Col("A", false, "NUMBER"), Col("B", false, "VARCHAR2(10)"), Col("C", false, "DATE"),
                Col("EMAIL", true, "VARCHAR2(100)"), Col("PHOTO", false, "BLOB")
            };
            // UK_EMAIL은 열 하나지만 NULL 허용, UK_PHOTO는 바인드할 수 없는 형식, IX_EXPR는 사전에 없는 숨은 열(식 인덱스) → 뺀다
            // 남은 것: UK_ABC(3열), UK_Z_AB(2열), UK_A_BC(2열) → 열이 적은 둘 중 이름순으로 UK_A_BC
            var unique = Keys("UK_EMAIL:EMAIL", "UK_PHOTO:PHOTO", "IX_EXPR:SYS_NC00005$", "UK_ABC:A,B,C", "UK_Z_AB:A,B", "UK_A_BC:C,B");
            var key = RowEditLogic.ChooseKey(meta, unique, "postgres", false);
            Assert.Equal(RowKeyKind.UniqueKey, key.Kind);
            Assert.Equal("UK_A_BC", key.Name);
            // 열은 키 안의 순서(POSITION)대로
            Assert.Equal(new[] { "C", "B" }, key.Columns);

            // NULL 허용 고유 키만 있으면 고르지 않는다
            var onlyNullable = RowEditLogic.ChooseKey(meta, Keys("UK_EMAIL:EMAIL"), "mssql", false);
            Assert.Equal(RowKeyKind.None, onlyNullable.Kind);
            Assert.Equal(RowEditLogic.NoKeyReason, onlyNullable.Reason);
        }

        [Fact]
        public void ChooseKey_RowIdOnlyForOracleTables()
        {
            var meta = new List<ColumnInfo> { Col("NAME", true, "VARCHAR2(10)") };
            Assert.Equal(RowKeyKind.RowId, RowEditLogic.ChooseKey(meta, null, "oracle", true).Kind);
            Assert.Equal(RowKeyKind.RowId, RowEditLogic.ChooseKey(meta, null, "Oracle", true).Kind);
            // 고유 키가 있으면 ROWID보다 고유 키
            Assert.Equal(RowKeyKind.UniqueKey, RowEditLogic.ChooseKey(new List<ColumnInfo> { Col("NAME", false, "VARCHAR2(10)") }, Keys("UK:NAME"), "oracle", true).Kind);
            // Oracle이라도 뷰 등(ROWID를 쓸 수 있는 테이블이 아님)은 거부
            Assert.Equal(RowKeyKind.None, RowEditLogic.ChooseKey(meta, null, "oracle", false).Kind);
            // PostgreSQL(ctid)·SQL Server·모르는 DBMS는 행 주소를 쓰지 않는다
            foreach (var kind in new[] { "postgres", "mssql", "generic", null })
            {
                var key = RowEditLogic.ChooseKey(meta, null, kind, true);
                Assert.Equal(RowKeyKind.None, key.Kind);
                Assert.Equal("기본 키나 NOT NULL 고유 키가 없어 편집할 수 없습니다.", key.Reason);
            }
        }

        [Fact]
        public void Describe_NoKey_ShowsReadableReason()
        {
            var columns = Columns("NAME:text");
            var meta = new List<ColumnInfo> { Col("NAME", true, "text") };
            var table = RowEditLogic.Describe("public", "log", columns, meta, ":", RowEditLogic.ChooseKey(meta, null, "postgres", true));
            Assert.False(table.CanEdit);
            Assert.Equal(RowEditLogic.NoKeyReason, table.DisabledReason);
            Assert.Null(RowEditLogic.UpdateStatement(table, new[] { "a" }, new[] { "b" }));
        }

        [Fact]
        public void Describe_UniqueKey_UsesKeyColumnsInWhere()
        {
            var columns = Columns("NAME:VARCHAR2(10)", "CODE:VARCHAR2(10)", "SEQ:NUMBER");
            var meta = new List<ColumnInfo> { Col("NAME", true, "VARCHAR2(10)"), Col("CODE", false, "VARCHAR2(10)"), Col("SEQ", false, "NUMBER") };
            var key = RowEditLogic.ChooseKey(meta, Keys("UK_CODE_SEQ:CODE,SEQ"), "oracle", true);
            var table = RowEditLogic.Describe("HR", "T", columns, meta, ":", key);
            Assert.True(table.CanEdit);
            Assert.Equal(RowKeyKind.UniqueKey, table.KeyKind);
            Assert.Equal(new[] { 1, 2 }, table.KeyIndexes);
            Assert.True(table.IsKey(1));
            Assert.Equal("고유 키 UK_CODE_SEQ", table.KeyNote);

            var update = RowEditLogic.UpdateStatement(table, new[] { "a", "X", "1" }, new[] { "b", "X", "1" });
            Assert.Equal("UPDATE HR.T SET NAME = :s0 WHERE CODE = :k0 AND SEQ = :k1", update.Sql);
            Assert.Equal(1m, update.Parameters[2].Value);

            // 결과에 고유 키 열이 빠졌으면 편집 불가
            var missing = RowEditLogic.Describe("HR", "T", Columns("NAME:VARCHAR2(10)", "CODE:VARCHAR2(10)"), meta, ":", key);
            Assert.False(missing.CanEdit);
            Assert.Contains("고유 키 열 'SEQ'", missing.DisabledReason);
        }

        [Fact]
        public void RowIdKey_WhereByRowId_AndRowsWithoutRowIdAreBlocked()
        {
            var columns = Columns("NAME:VARCHAR2(10)", "QTY:NUMBER");
            var meta = new List<ColumnInfo> { Col("NAME", true, "VARCHAR2(10)"), Col("QTY", true, "NUMBER") };
            var table = RowEditLogic.Describe("HR", "LOG", columns, meta, ":", RowEditLogic.ChooseKey(meta, null, "oracle", true));
            Assert.True(table.CanEdit);
            Assert.True(table.UsesRowId);
            Assert.Empty(table.KeyIndexes);
            Assert.False(table.IsKey(0));
            Assert.Equal("ROWID", table.KeyNote);

            const string rid = "AAAR3sAAEAAAACXAAA";
            var update = RowEditLogic.UpdateStatement(table, new[] { "a", "1" }, new[] { "b", "1" }, rid);
            Assert.Equal("UPDATE HR.LOG SET NAME = :s0 WHERE ROWID = :rid", update.Sql);
            Assert.Equal(new[] { "s0", "rid" }, update.Parameters.Select(p => p.Key));
            Assert.Equal(rid, update.Parameters[1].Value);

            var delete = RowEditLogic.DeleteStatement(table, new[] { "a", "1" }, rid);
            Assert.Equal("DELETE FROM HR.LOG WHERE ROWID = :rid", delete.Sql);
            Assert.Equal(rid, Assert.Single(delete.Parameters).Value);

            // ROWID를 모르는 행(저장한 새 행)은 문장을 만들지 않고 막는다
            Assert.Null(RowEditLogic.UpdateStatement(table, new[] { "a", "1" }, new[] { "b", "1" }));
            Assert.Null(RowEditLogic.DeleteStatement(table, new[] { "a", "1" }));
            Assert.Contains("ROWID", RowEditLogic.RowBlockReason(table, null));
            Assert.Null(RowEditLogic.RowBlockReason(table, rid));
            // 다른 키는 ROWID가 없어도 막지 않는다
            Assert.Null(RowEditLogic.RowBlockReason(RowEditLogic.Describe("HR", "EMP", Columns("ID:NUMBER"), Meta("ID", true), ":"), null));

            // INSERT는 ROWID와 무관
            Assert.Equal("INSERT INTO HR.LOG (NAME, QTY) VALUES (:i0, :i1)", RowEditLogic.InsertStatement(table, new[] { "c", "2" }).Sql);
        }

        [Fact]
        public void QuickQueryStatement_RowId_AppendsRowIdAsLastColumn()
        {
            Assert.Equal("SELECT HR.LOG.*, ROWIDTOCHAR(ROWID) AS \"DBH$ROWID\"\n  FROM HR.LOG", WorkspaceLogic.QuickQueryStatement("HR", "LOG", "", true));
            var sql = WorkspaceLogic.QuickQueryStatement("HR", "LOG", "QTY > 1", true);
            Assert.Equal("SELECT HR.LOG.*, ROWIDTOCHAR(ROWID) AS \"DBH$ROWID\"\n  FROM HR.LOG\n WHERE (QTY > 1)", sql);
            var statement = SqlScript.Parse(sql);
            Assert.Equal(SqlKind.Query, statement.Kind);
            Assert.True(WorkspaceLogic.AllowedOnReadOnly(statement));
            // ROWID를 쓰지 않으면 지금 그대로
            Assert.Equal(WorkspaceLogic.SelectStatement("HR", "LOG"), WorkspaceLogic.QuickQueryStatement("HR", "LOG", "", false));
        }

        [Fact]
        public void UniqueKeyQueries_PerProvider()
        {
            var oracle = DbProviders.Oracle.Metadata;
            Assert.Contains("CONSTRAINT_TYPE = 'U'", oracle.UniqueKeys("HR", "T").Sql);
            Assert.Contains("UNIQUENESS = 'UNIQUE'", oracle.UniqueKeys("HR", "T").Sql);
            Assert.Contains("ALL_TABLES", oracle.RowIdTable("HR", "T").Sql);
            Assert.Contains("indisunique", DbProviders.Postgres.Metadata.UniqueKeys("public", "t").Sql);
            Assert.Contains("is_unique = 1", DbProviders.SqlServer.Metadata.UniqueKeys("dbo", "t").Sql);
            // 행 주소는 Oracle만
            Assert.Null(DbProviders.Postgres.Metadata.RowIdTable("public", "t"));
            Assert.Null(DbProviders.SqlServer.Metadata.RowIdTable("dbo", "t"));
        }

        private static ColumnInfo Col(string name, bool nullable, string type, bool pk = false)
        {
            return new ColumnInfo { Name = name, Nullable = nullable, TypeLabel = type, PrimaryKey = pk };
        }

        /// <summary>"키이름:열1,열2" → 고유 키 열 행(POSITION은 쓴 순서).</summary>
        private static List<UniqueKeyColumn> Keys(params string[] specs)
        {
            var list = new List<UniqueKeyColumn>();
            foreach (var spec in specs)
            {
                var colon = spec.IndexOf(':');
                var names = spec.Substring(colon + 1).Split(',');
                for (var i = 0; i < names.Length; i++)
                    list.Add(new UniqueKeyColumn { KeyName = spec.Substring(0, colon), ColumnName = names[i], Position = i + 1 });
            }
            // 데이터 사전이 돌려주는 순서와 무관하게 고르는지 보려고 섞는다
            list.Reverse();
            return list;
        }

        private static List<ResultColumn> Columns(params string[] specs)
        {
            return specs.Select(s =>
            {
                var colon = s.IndexOf(':');
                var label = s.Substring(colon + 1);
                return new ResultColumn { Name = s.Substring(0, colon), TypeLabel = label, IsNumeric = ValueFormatter.IsNumericTypeName(label) };
            }).ToList();
        }

        /// <summary>이름, 기본 키 여부 쌍.</summary>
        private static List<ColumnInfo> Meta(params object[] pairs)
        {
            var list = new List<ColumnInfo>();
            for (var i = 0; i + 1 < pairs.Length; i += 2)
                list.Add(new ColumnInfo { Name = (string)pairs[i], PrimaryKey = (bool)pairs[i + 1], Position = list.Count + 1 });
            return list;
        }
    }
}

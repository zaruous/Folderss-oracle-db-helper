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

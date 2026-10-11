using System;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    /// <summary>GCM 정의서 기본 양식 2종(기본·단일 시트형): 실제 데이터로 그려 시트 구성·값·빈 그룹 처리, 템플릿 파일 자체의 올바름.</summary>
    public class CodeSpecTemplateTests
    {
        // 31자를 넘는 그룹 ID — 시트 이름은 31자로 잘린다
        private const string LongGroupId = "VERY_LONG_GROUP_IDENTIFIER_FOR_SHEET_NAME_TEST";

        // ORDER_STATUS(코드 3개, PLANT_CD 추가 속성) + 긴 그룹(코드 1개)
        private static CodeSpecData Data()
        {
            var table = new QueryTable();
            table.Columns.AddRange(new[] { "TABLE_NAME", "GROUP_NAME", "GROUP_DESC", "CODE", "CODE_NAME", "CODE_DESC", "SORT_NO", "USE_YN", "PLANT_CD" });
            table.Rows.Add(new[] { "ORDER_STATUS", "주문 상태", "주문 진행 단계", "A", "접수", "고객이 주문을 넣은 상태", "1", "Y", "P1" });
            table.Rows.Add(new[] { "ORDER_STATUS", "주문 상태", "주문 진행 단계", "B", "배송", "배송 중", "2", "Y", "" });
            table.Rows.Add(new[] { "ORDER_STATUS", "주문 상태", "주문 진행 단계", "C", "완료", "", "3", "N", "P2" });
            table.Rows.Add(new[] { LongGroupId, "긴 그룹", "", "X", "엑스", "", "1", "", "" });
            return CodeSpecModel.Build(table, "GCM_운영.sql", "SELECT *\r\n  FROM gcm");
        }

        private static VelocityContext Context(CodeSpecData data)
        {
            var doc = new TableSpecDocument { Title = "공통 코드 정의서", Project = "MES", System = "공통", Author = "홍길동", Date = new DateTime(2026, 10, 11) };
            var profile = new OracleConnectionProfile { Name = "개발", Host = "db", Port = 1521, ServiceName = "orcl", UserId = "mes" };
            return CodeSpecModel.Context(doc, profile, data, "Oracle Database 19c");
        }

        private static XLWorkbook Render(byte[] template, CodeSpecData data)
        {
            var output = new MemoryStream();
            ExcelTemplate.Render(template, Context(data), output);
            output.Position = 0;
            return new XLWorkbook(output);
        }

        // 그려지지 않은 템플릿 글자가 없고 안내 시트가 빠졌는지
        private static void AssertClean(XLWorkbook book)
        {
            var leftovers = book.Worksheets.SelectMany(w => w.CellsUsed()).Where(c => !c.HasFormula && c.Value.IsText)
                .Select(c => c.GetText()).Where(t => t.Contains("${") || t.Contains("#foreach") || t.Contains("#end")).ToList();
            Assert.True(leftovers.Count == 0, "그려지지 않은 글자: " + string.Join(" | ", leftovers));
            Assert.DoesNotContain(book.Worksheets, w => w.Name.StartsWith("_", StringComparison.Ordinal));
        }

        [Fact]
        public void Default_CoverListAndGroupSheets()
        {
            using (var book = Render(CodeSpecTemplate.Create(), Data()))
            {
                var longSheet = LongGroupId.Substring(0, 31);
                Assert.Equal(new[] { "표지", "목록", "ORDER_STATUS", longSheet }, book.Worksheets.Select(w => w.Name).ToArray());
                AssertClean(book);

                var cover = book.Worksheet("표지");
                Assert.Equal("공통 코드 정의서", cover.Cell("B6").GetText());
                Assert.Equal("MES", cover.Cell("B9").GetText());
                Assert.Equal("개발 (mes@db:1521/orcl)", cover.Cell("E15").GetText());
                Assert.Equal("Oracle Database 19c", cover.Cell("E16").GetText());
                Assert.Equal(2, cover.Cell("E17").GetDouble());
                Assert.Equal(4, cover.Cell("E18").GetDouble());
                Assert.Equal("2026-10-11", cover.Cell("E21").GetText());
                Assert.Equal("GCM_운영.sql", cover.Cell("E25").GetText());
                Assert.Equal(4, cover.Cell("E27").GetDouble());
                Assert.Equal("TABLE_NAME, GROUP_NAME, GROUP_DESC, CODE, CODE_NAME, CODE_DESC, SORT_NO, USE_YN, PLANT_CD", cover.Cell("E28").GetText());
                // 엑셀 셀의 줄바꿈은 LF로 저장된다
                Assert.Equal("SELECT *\n  FROM gcm", cover.Cell("E29").GetText());
                Assert.True(cover.Cell("E29").MergedRange().RowCount() == 10);

                var list = book.Worksheet("목록");
                Assert.Equal(4, list.SheetView.SplitRow);
                Assert.Equal(1, list.Cell("B5").GetDouble());
                Assert.Equal("HYPERLINK(\"#'ORDER_STATUS'!A1\",\"ORDER_STATUS\")", list.Cell("C5").FormulaA1);
                Assert.Equal("주문 상태", list.Cell("D5").GetText());
                Assert.Equal("주문 진행 단계", list.Cell("E5").GetText());
                Assert.Equal(3, list.Cell("F5").GetDouble());
                Assert.Equal("HYPERLINK(\"#'" + longSheet + "'!A1\",\"" + LongGroupId + "\")", list.Cell("C6").FormulaA1);
                Assert.True(list.Cell("B7").IsEmpty());

                // #sheet 줄이 빠져 한 줄씩 위로: 머리 1~2, 개요 4~7, 코드 표 머리글 9, 코드 10~12
                var group = book.Worksheet("ORDER_STATUS");
                Assert.Equal("ORDER_STATUS", group.Cell("B1").GetText());
                Assert.Equal("주문 상태", group.Cell("B2").GetText());
                Assert.Equal("HYPERLINK(\"#'목록'!A1\",\"← 목록\")", group.Cell("H1").FormulaA1);
                Assert.Equal("주문 진행 단계", group.Cell("D6").GetText());
                Assert.Equal(3, group.Cell("D7").GetDouble());
                Assert.Equal("코드 (3)", group.Cell("B9").GetText());
                Assert.Equal("코드", group.Cell("C10").GetText());
                Assert.Equal(new[] { "A", "B", "C" }, Enumerable.Range(11, 3).Select(r => group.Cell(r, 3).GetText()).ToArray());
                Assert.Equal(new[] { "접수", "배송", "완료" }, Enumerable.Range(11, 3).Select(r => group.Cell(r, 4).GetText()).ToArray());
                Assert.Equal(1, group.Cell("B11").GetDouble());
                Assert.Equal("고객이 주문을 넣은 상태", group.Cell("E11").GetText());
                Assert.Equal("Y", group.Cell("G11").GetText());
                Assert.Equal("N", group.Cell("G13").GetText());
                Assert.Equal("PLANT_CD=P1", group.Cell("H11").GetText());
                Assert.True(group.Cell("H12").IsEmpty());
                Assert.Equal("PLANT_CD=P2", group.Cell("H13").GetText());
                Assert.True(group.Cell("B14").IsEmpty());
                Assert.True(group.Cell("E11").Style.Alignment.WrapText);

                var longGroup = book.Worksheet(longSheet);
                Assert.Equal(LongGroupId, longGroup.Cell("B1").GetText());
                Assert.Equal("X", longGroup.Cell("C11").GetText());
            }
        }

        [Fact]
        public void Flat_CoverAndOneCodeSheet()
        {
            using (var book = Render(CodeSpecTemplate.CreateFlat(), Data()))
            {
                Assert.Equal(new[] { "표지", "코드" }, book.Worksheets.Select(w => w.Name).ToArray());
                AssertClean(book);

                var codes = book.Worksheet("코드");
                Assert.Equal(4, codes.SheetView.SplitRow);
                Assert.Equal("추가 속성", codes.Cell("J4").GetText());
                // 지시문 줄이 빠져 코드는 5줄부터, 전체 순번
                Assert.Equal(new double[] { 1, 2, 3, 4 }, Enumerable.Range(5, 4).Select(r => codes.Cell(r, 2).GetDouble()).ToArray());
                Assert.Equal(new[] { "ORDER_STATUS", "ORDER_STATUS", "ORDER_STATUS", LongGroupId }, Enumerable.Range(5, 4).Select(r => codes.Cell(r, 3).GetText()).ToArray());
                Assert.Equal("주문 상태", codes.Cell("D5").GetText());
                Assert.Equal(new[] { "A", "B", "C", "X" }, Enumerable.Range(5, 4).Select(r => codes.Cell(r, 5).GetText()).ToArray());
                Assert.Equal(new[] { "접수", "배송", "완료", "엑스" }, Enumerable.Range(5, 4).Select(r => codes.Cell(r, 6).GetText()).ToArray());
                Assert.Equal("PLANT_CD=P1", codes.Cell("J5").GetText());
                Assert.Equal("PLANT_CD=P2", codes.Cell("J7").GetText());
                Assert.True(codes.Cell("B9").IsEmpty());
            }
        }

        [Fact]
        public void NoGroups_CoverAndListOnly()
        {
            using (var book = Render(CodeSpecTemplate.Create(), new CodeSpecData()))
            {
                Assert.Equal(new[] { "표지", "목록" }, book.Worksheets.Select(w => w.Name).ToArray());
                AssertClean(book);
                Assert.Equal("(그룹이 없습니다)", book.Worksheet("목록").Cell("B5").GetText());
                Assert.Equal(0, book.Worksheet("표지").Cell("E17").GetDouble());
            }
            using (var book = Render(CodeSpecTemplate.CreateFlat(), new CodeSpecData()))
            {
                Assert.Equal(new[] { "표지", "코드" }, book.Worksheets.Select(w => w.Name).ToArray());
                AssertClean(book);
                Assert.Equal("(코드가 없습니다)", book.Worksheet("코드").Cell("B5").GetText());
            }
        }

        // 템플릿 파일 자체가 올바른 엑셀인지(열리고, 탭 이름이 31자 이하)
        [Fact]
        public void Templates_OpenAndHaveValidSheetNames()
        {
            using (var template = new XLWorkbook(new MemoryStream(CodeSpecTemplate.Create())))
            {
                Assert.Equal(new[] { "표지", "목록", "${group.sheetName}", "_변수 설명" }, template.Worksheets.Select(w => w.Name).ToArray());
                Assert.All(template.Worksheets, w => Assert.True(w.Name.Length <= ExcelTemplate.MaxSheetNameLength));
                Assert.Equal("#sheet($group in $groups)", template.Worksheet("${group.sheetName}").Cell("A1").GetText());
                var help = template.Worksheet("_변수 설명");
                Assert.Contains(help.CellsUsed(), c => c.Value.IsText && c.GetText() == "$code.attrText");
            }
            using (var template = new XLWorkbook(new MemoryStream(CodeSpecTemplate.CreateFlat())))
            {
                Assert.Equal(new[] { "표지", "코드", "_변수 설명" }, template.Worksheets.Select(w => w.Name).ToArray());
                Assert.All(template.Worksheets, w => Assert.True(w.Name.Length <= ExcelTemplate.MaxSheetNameLength));
            }
            // 양식 목록의 기본 양식이 이 두 양식을 만든다
            Assert.Equal(new[] { CodeSpecTemplates.DefaultId, CodeSpecTemplates.FlatId }, CodeSpecTemplates.All.Select(t => t.Id).ToArray());
            Assert.All(CodeSpecTemplates.All, t => Assert.True(t.Create().Length > 0));
        }
    }
}

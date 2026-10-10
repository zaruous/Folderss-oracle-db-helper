using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    /// <summary>명세서형 기본 양식(워크스코브 양식 구성)과 그 양식이 쓰는 변수: 논리명·설명 나누기, 키 표시, 인덱스 유형, 공통 코드, 틀 고정 이동, 양식 목록.</summary>
    public class TableSpecSheetTemplateTests
    {
        // ORDERS: ID(PK) · CUSTOMER_NO(FK + 인덱스) · STATUS(검사 IN) · EMAIL(고유 제약)
        private static TableDescription Orders()
        {
            var t = new TableDescription { Owner = "SHOP", Name = "ORDERS", Type = "TABLE", Comment = "주문\n고객이 낸 주문 한 건", NumRows = 12345, Tablespace = "USERS" };
            t.Columns.Add(Col("ID", "NUMBER(10)", 1, false, "주문 번호"));
            t.Columns.Add(Col("CUSTOMER_NO", "NUMBER(10)", 2, false, "고객 번호"));
            t.Columns.Add(Col("STATUS", "CHAR(1 BYTE)", 3, false, "상태: R=접수, D=배송"));
            t.Columns.Add(Col("EMAIL", "VARCHAR2(100 CHAR)", 4, true, "알림 메일(선택)"));
            t.Columns[2].Default = "'R'";
            t.Indexes.Add(Index("PK_ORDERS", true, "ID"));
            t.Indexes.Add(Index("IX_ORDERS_CUST", false, "CUSTOMER_NO", "ID DESC"));
            t.Indexes.Add(Index("UQ_ORDERS_EMAIL", true, "EMAIL"));
            t.Constraints.Add(Con("PK_ORDERS", "P", new[] { "ID" }, indexName: "PK_ORDERS"));
            t.Constraints.Add(Con("UQ_ORDERS_EMAIL", "U", new[] { "EMAIL" }, indexName: "UQ_ORDERS_EMAIL"));
            var fk = Con("FK_ORDERS_CUST", "R", new[] { "CUSTOMER_NO" });
            fk.Reference = "SHOP.CUSTOMERS";
            fk.ReferenceColumns.Add("NO");
            fk.DeleteRule = "CASCADE";
            t.Constraints.Add(fk);
            var check = Con("CK_ORDERS_STATUS", "C", new[] { "STATUS" });
            check.Condition = "STATUS IN ('R','D')";
            t.Constraints.Add(check);
            return t;
        }

        private static TableDescription Customers()
        {
            var t = new TableDescription { Owner = "SHOP", Name = "CUSTOMERS", Type = "TABLE" };
            t.Columns.Add(Col("NO", "NUMBER(10)", 1, false, null));
            return t;
        }

        private static DescribedColumn Col(string name, string type, int position, bool nullable, string comment)
        {
            return new DescribedColumn { Column = new ColumnInfo { Name = name, TypeLabel = type, Position = position, Nullable = nullable }, Comment = comment };
        }

        private static IndexDescription Index(string name, bool unique, params string[] columns)
        {
            var index = new IndexDescription { Name = name, Unique = unique, Type = "NORMAL", Status = "VALID", Columns = string.Join(", ", columns) };
            index.ColumnNames.AddRange(columns);
            return index;
        }

        private static ConstraintDescription Con(string name, string type, string[] columns, string indexName = null)
        {
            var constraint = new ConstraintDescription { Name = name, Type = type, Status = "ENABLED", Columns = string.Join(", ", columns), IndexName = indexName };
            constraint.ColumnNames.AddRange(columns);
            return constraint;
        }

        private static VelocityContext Context(params TableDescription[] tables)
        {
            var doc = new TableSpecDocument { Title = "쇼핑몰 테이블 명세서", Project = "쇼핑몰", System = "주문", Author = "홍길동", Date = new DateTime(2026, 10, 10) };
            var profile = new OracleConnectionProfile { Name = "개발", Host = "db", Port = 1521, ServiceName = "orcl", UserId = "shop" };
            return TableSpecModel.Context(doc, profile, tables, "Oracle Database 19c Enterprise Edition 19.0.0.0.0");
        }

        private static string Render(string template, VelocityContext context)
        {
            return VelocityTemplate.Parse(template).Render(context);
        }

        // ---------- 주석 나누기 ----------

        [Theory]
        [InlineData("주문 번호", "주문 번호", "")]
        [InlineData("상태: R=접수, D=배송", "상태", "R=접수, D=배송")]
        [InlineData("상태 | 코드 그룹 ORDER_STATUS", "상태", "코드 그룹 ORDER_STATUS")]
        [InlineData("고객 - 주문한 사람", "고객", "주문한 사람")]
        [InlineData("사용 여부(Y/N)", "사용 여부", "Y/N")]
        [InlineData("주문\n고객이 낸 주문 한 건\n둘째 줄", "주문", "고객이 낸 주문 한 건\n둘째 줄")]
        [InlineData("가격(원) 합계", "가격(원) 합계", "")]
        [InlineData(null, "", "")]
        public void SplitComment(string comment, string logical, string description)
        {
            string l;
            string d;
            TableSpecModel.SplitComment(comment, out l, out d);
            Assert.Equal(logical, l);
            Assert.Equal(description, d);
        }

        // ---------- 공통 코드 ----------

        [Theory]
        [InlineData("STATUS IN ('R','D')", "STATUS", new[] { "R", "D" })]
        [InlineData("\"kind\" in (1, 2,3)", "KIND", new[] { "1", "2", "3" })]
        [InlineData("NOTE IN ('a,b', 'it''s')", "NOTE", new[] { "a,b", "it's" })]
        public void ParseInList_Values(string condition, string column, string[] values)
        {
            string c;
            List<string> v;
            Assert.True(TableSpecModel.ParseInList(condition, out c, out v));
            Assert.Equal(column, c);
            Assert.Equal(values, v.ToArray());
        }

        [Theory]
        [InlineData("SAL > 0")]
        [InlineData("STATUS IN ('A'")]
        [InlineData("STATUS NOT IN ('A')")]
        [InlineData(null)]
        public void ParseInList_OtherConditions(string condition)
        {
            string c;
            List<string> v;
            Assert.False(TableSpecModel.ParseInList(condition, out c, out v));
        }

        // ---------- 변수 ----------

        [Fact]
        public void Context_KeysLogicalNamesIndexKindsCodes()
        {
            var context = Context(Orders());

            Assert.Equal("ID=PK|CUSTOMER_NO=FK,IX|STATUS=|EMAIL=UK|",
                Render("#foreach($t in $tables)#foreach($c in $t.columns)$c.name=$c.key|#end#end", context));
            Assert.Equal("주문 번호/;상태/R=접수, D=배송;알림 메일/선택;",
                Render("#foreach($t in $tables)#foreach($c in $t.columns)#if($c.no != 2)$c.logicalName/$c.description;#end#end#end", context));
            Assert.Equal("주문|고객이 낸 주문 한 건|12,345", Render("#foreach($t in $tables)$t.logicalName|$t.description|$t.numRowsText#end", context));
            Assert.Equal("PK_ORDERS=PK IX_ORDERS_CUST=INDEX UQ_ORDERS_EMAIL=UNIQUE ",
                Render("#foreach($t in $tables)#foreach($i in $t.indexes)$i.name=$i.kind #end#end", context));
            Assert.Equal("SHOP.CUSTOMERS.NO|CASCADE|NO ACTION", Render("#foreach($t in $tables)#foreach($f in $t.foreignKeys)$f.target|$f.deleteRule|$f.updateRule#end#end", context));
            Assert.Equal("STATUS/R/ORDERS.STATUS/CK_ORDERS_STATUS;STATUS/D/ORDERS.STATUS/CK_ORDERS_STATUS;",
                Render("#foreach($c in $codes)$c.group/$c.code/$c.usage/$c.constraint;#end", context));
            Assert.Equal("CK_ORDERS_STATUS", Render("#foreach($t in $tables)#foreach($k in $t.checks)$k.name#end#end", context));
            Assert.Equal("Oracle Database 19c Enterprise Edition 19.0.0.0.0|주문", Render("$db.version|$doc.system", context));
        }

        [Fact]
        public void ServerVersion_DropsProductionSuffix()
        {
            var table = new System.Data.DataTable();
            table.Columns.Add("PRODUCT");
            table.Columns.Add("VERSION");
            table.Rows.Add("Oracle Database 11g Express Edition - Production", "11.2.0.2.0");
            var reader = table.CreateDataReader();
            reader.Read();
            Assert.Equal("Oracle Database 11g Express Edition 11.2.0.2.0", TableSpecQueries.ReadServerVersion(reader));
            Assert.Contains("PRODUCT_COMPONENT_VERSION", TableSpecQueries.ServerVersion().Sql);
        }

        // ---------- 양식 ----------

        [Fact]
        public void BuiltIns_HaveUniqueIdsAndRender()
        {
            Assert.Equal(TableSpecTemplates.All.Count, TableSpecTemplates.All.Select(t => t.Id).Distinct().Count());
            Assert.Same(TableSpecTemplates.All[0], TableSpecTemplates.Find("없는 양식"));
            Assert.Equal(TableSpecTemplates.SpecSheetId, TableSpecTemplates.Find(TableSpecTemplates.SpecSheetId).Id);
            foreach (var builtIn in TableSpecTemplates.All)
            {
                using (var output = new MemoryStream())
                {
                    ExcelTemplate.Render(builtIn.Create(), Context(Orders(), Customers()), output);
                    Assert.True(output.Length > 0, builtIn.Name);
                }
            }
        }

        // ---------- 사용자 양식 폴더 ----------

        [Fact]
        public void UserFolder_ListsXlsxAfterBuiltIns_SkipsLockAndOtherFiles()
        {
            var folder = Path.Combine(Path.GetTempPath(), "tablespec-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                File.WriteAllBytes(Path.Combine(folder, "우리팀_명세서.xlsx"), TableSpecSheetTemplate.Create());
                File.WriteAllBytes(Path.Combine(folder, "A_간단.xlsx"), TableSpecTemplate.Create());
                File.WriteAllText(Path.Combine(folder, "~$우리팀_명세서.xlsx"), "lock");
                File.WriteAllText(Path.Combine(folder, "메모.txt"), "x");

                var list = TableSpecTemplates.List(folder);

                Assert.Equal(new[] { TableSpecTemplates.DefaultId, TableSpecTemplates.SpecSheetId, "user:A_간단.xlsx", "user:우리팀_명세서.xlsx" }, list.Select(t => t.Id).ToArray());
                var mine = TableSpecTemplates.Find(list, "user:우리팀_명세서.xlsx");
                Assert.Equal("내 양식 — 우리팀_명세서", mine.Name);
                Assert.False(mine.IsBuiltIn);
                Assert.StartsWith("사용자 양식 · 우리팀_명세서.xlsx · 고친 날 ", mine.Description);
                // 지운 양식을 찾으면 맨 앞(기본)
                Assert.Equal(TableSpecTemplates.DefaultId, TableSpecTemplates.Find(list, "user:지운 양식.xlsx").Id);

                // 엑셀이 열어 잠근 동안에도 읽힌다
                using (new FileStream(mine.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                    var output = new MemoryStream();
                    ExcelTemplate.Render(mine.Create(), Context(Orders()), output);
                    Assert.True(output.Length > 0);
                }
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [Fact]
        public void UserFolder_MissingOrUnknown_OnlyBuiltIns()
        {
            Assert.Equal(TableSpecTemplates.All.Count, TableSpecTemplates.List(null).Count);
            Assert.Equal(TableSpecTemplates.All.Count, TableSpecTemplates.List(Path.Combine(Path.GetTempPath(), "없는-" + Guid.NewGuid().ToString("N"))).Count);
            Assert.Null(TableSpecTemplates.UserFolder(null));
            Assert.Equal(Path.Combine(@"C:\data", "templates"), TableSpecTemplates.UserFolder(@"C:\data"));
        }

        [Fact]
        public void CopyName_AvoidsExistingFiles()
        {
            var folder = Path.Combine(Path.GetTempPath(), "tablespec-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                Assert.Equal("테이블명세서.xlsx", TableSpecTemplates.CopyName(folder, "테이블명세서.xlsx"));
                File.WriteAllText(Path.Combine(folder, "테이블명세서.xlsx"), "x");
                File.WriteAllText(Path.Combine(folder, "테이블명세서 (2).xlsx"), "x");
                Assert.Equal("테이블명세서 (3).xlsx", TableSpecTemplates.CopyName(folder, "테이블명세서.xlsx"));
                Assert.Equal("양식.xlsx", TableSpecTemplates.CopyName(folder, "양식"));
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [Fact]
        public void LlmPrompt_HasGuideFolderAndBase()
        {
            var withBase = TableSpecLogic.LlmPrompt(TableSpecTemplates.GuideUrl, TableSpecTemplates.GuideMarkdownUrl, @"C:\t\templates", @"C:\t\templates\우리팀.xlsx");
            Assert.Contains(TableSpecTemplates.GuideMarkdownUrl, withBase);
            Assert.Contains(@"이 폴더에 저장해: C:\t\templates", withBase);
            Assert.Contains(@"바탕으로 고쳐 새 파일로 저장해(원본은 두고): C:\t\templates\우리팀.xlsx", withBase);
            var noBase = TableSpecLogic.LlmPrompt(TableSpecTemplates.GuideUrl, TableSpecTemplates.GuideMarkdownUrl, null, null);
            Assert.Contains("[양식 폴더]", noBase);
            Assert.Contains("[복사해서 고치기]", noBase);
        }

        [Fact]
        public void SpecSheet_CoverTableCodesHistory()
        {
            var output = new MemoryStream();
            ExcelTemplate.Render(TableSpecSheetTemplate.Create(), Context(Orders(), Customers()), output);
            SaveSample(output, "테이블명세서_샘플.xlsx");
            output.Position = 0;
            using (var book = new XLWorkbook(output))
            {
                Assert.Equal(new[] { "표지", "ORDERS", "CUSTOMERS", "공통 코드 정의", "개정 이력" }, book.Worksheets.Select(w => w.Name).ToArray());

                var cover = book.Worksheet("표지");
                Assert.Equal("쇼핑몰 테이블 명세서", cover.Cell("B2").GetText());
                Assert.Equal("주문", cover.Cell("C5").GetText());
                Assert.Equal("Oracle Database 19c Enterprise Edition 19.0.0.0.0", cover.Cell("C6").GetText());
                Assert.Equal("2026-10-10", cover.Cell("C8").GetText());
                Assert.Equal("테이블 목록 (2개)", cover.Cell("B12").GetText());
                Assert.Equal("ORDERS", cover.Cell("C14").GetText());
                Assert.Equal("주문", cover.Cell("D14").GetText());
                Assert.Equal("HYPERLINK(\"#'ORDERS'!A1\",\"이동\")", cover.Cell("F14").FormulaA1);
                Assert.Equal("CUSTOMERS", cover.Cell("C15").GetText());

                // #sheet 줄이 빠져 한 줄씩 위로, 틀 고정도 머리 줄(6)까지
                var orders = book.Worksheet("ORDERS");
                Assert.Equal("테이블: ORDERS (주문)", orders.Cell("B1").GetText());
                Assert.Equal("고객이 낸 주문 한 건", orders.Cell("D2").GetText());
                Assert.Equal("SHOP · 테이블 · 행 수(통계) 12,345 · USERS", orders.Cell("D3").GetText());
                Assert.Equal(6, orders.SheetView.SplitRow);
                Assert.Equal("컬럼명", orders.Cell("C6").GetText());
                Assert.Equal(new[] { "ID", "CUSTOMER_NO", "STATUS", "EMAIL" }, Enumerable.Range(7, 4).Select(r => orders.Cell(r, 3).GetText()).ToArray());
                Assert.Equal("FK,IX", orders.Cell("H8").GetText());
                Assert.Equal("SHOP.CUSTOMERS.NO 참조", orders.Cell("I8").GetText());
                Assert.Equal("'R'", orders.Cell("G9").GetText());
                Assert.Equal("R=접수, D=배송", orders.Cell("I9").GetText());
                Assert.Equal("외래키 (참조 관계)", orders.Cell("B12").GetText());
                Assert.Equal("FK_ORDERS_CUST", orders.Cell("B14").GetText());
                Assert.Equal("SHOP.CUSTOMERS.NO", orders.Cell("E14").GetText());
                Assert.Equal("CASCADE", orders.Cell("G14").GetText());
                Assert.Equal("인덱스", orders.Cell("B16").GetText());
                Assert.Equal("CUSTOMER_NO, ID DESC", orders.Cell("D19").GetText());
                Assert.Equal("검사 조건", orders.Cell("B22").GetText());
                Assert.Equal("STATUS IN ('R','D')", orders.Cell("E24").GetText());

                var customers = book.Worksheet("CUSTOMERS");
                Assert.Equal("테이블: CUSTOMERS", customers.Cell("B1").GetText());
                Assert.Equal("(없음)", customers.Cell("B11").GetText());
                Assert.Equal("(없음)", customers.Cell("B15").GetText());
                Assert.True(customers.Cell("B16").IsEmpty());

                var codes = book.Worksheet("공통 코드 정의");
                Assert.Equal(new[] { "R", "D" }, new[] { codes.Cell("C6").GetText(), codes.Cell("C7").GetText() });
                Assert.Equal("ORDERS.STATUS", codes.Cell("E6").GetText());
                Assert.Equal(5, codes.SheetView.SplitRow);

                var history = book.Worksheet("개정 이력");
                Assert.Equal(new[] { "1.0", "2026-10-10", "홍길동", "최초 작성" }, Enumerable.Range(2, 4).Select(c => history.Cell(5, c).GetText()).ToArray());
            }
        }

        [Fact]
        public void SpecSheet_TemplateKeepsGuideSheetsForEditing()
        {
            var bytes = TableSpecSheetTemplate.Create();
            SaveSample(new MemoryStream(bytes), "테이블명세서_템플릿.xlsx");
            using (var template = new XLWorkbook(new MemoryStream(bytes)))
            {
                Assert.Equal(new[] { "표지", "${table.sheetName}", "공통 코드 정의", "개정 이력", "_작성 가이드", "_변수 설명" }, template.Worksheets.Select(w => w.Name).ToArray());
                Assert.Equal(7, template.Worksheet("${table.sheetName}").SheetView.SplitRow);
            }
        }

        private static void SaveSample(MemoryStream stream, string name)
        {
            var dir = Environment.GetEnvironmentVariable("TABLESPEC_SAMPLE_DIR");
            if (string.IsNullOrEmpty(dir))
                return;
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, name), stream.ToArray());
        }
    }
}

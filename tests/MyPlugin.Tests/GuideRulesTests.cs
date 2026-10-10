using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    /// <summary>
    /// LLM용 양식 작성 지침(docs/table-spec-templates/guide.md)에 적힌 규칙이 실제 엔진 동작과 맞는지.
    /// 테스트 이름 앞의 S3_1 등은 지침의 장·절 번호다 — 지침을 고치면 같은 번호의 테스트도 고친다.
    /// </summary>
    public class GuideRulesTests
    {
        // ================= 3. 렌더링 규칙 =================

        [Fact]
        public void S3_1_UnderscoreSheets_AreDropped()
        {
            using (var book = Render(wb =>
            {
                wb.AddWorksheet("표지").Cell("A1").Value = "${doc.title}";
                wb.AddWorksheet("_안내").Cell("A1").Value = "메모";
            }))
                Assert.Equal(new[] { "표지" }, Sheets(book));
        }

        [Fact]
        public void S3_1_SheetDirective_ClonesPerItem_WithSheetNameTab_AndRemovesDirectiveRow()
        {
            using (var book = Render(wb =>
            {
                var s = wb.AddWorksheet("${table.sheetName}");
                s.Cell("A1").Value = "#sheet($table in $tables)";
                s.Cell("B2").Value = "${table.name}";
            }))
            {
                Assert.Equal(new[] { "ORDERS", "CUSTOMERS" }, Sheets(book));
                Assert.Equal("ORDERS", book.Worksheet("ORDERS").Cell("B1").GetText());
            }
        }

        [Fact]
        public void S3_1_SheetDirective_NoItems_RemovesSheet_AndEmptyBookGetsBlankSheet()
        {
            using (var book = Render(wb => wb.AddWorksheet("T").Cell("A1").Value = "#sheet($code in $codes)", GuideFixture.Context(GuideFixture.Customers())))
                Assert.Equal(new[] { "빈 문서" }, Sheets(book));
        }

        [Fact]
        public void S3_1_OtherTabPattern_IsCleanedAndTruncated_SoLinksBySheetNameMayNotMatch()
        {
            var a = new TableDescription { Owner = "VERY_LONG_SCHEMA", Name = "VERY_LONG_TABLE_NAME_X1" };
            var b = new TableDescription { Owner = "VERY_LONG_SCHEMA", Name = "VERY_LONG_TABLE_NAME_X2" };
            using (var book = Render(wb =>
            {
                var s = wb.AddWorksheet("${table.owner}_${table.name}");
                s.Cell("A1").Value = "#sheet($table in $tables)";
                s.Cell("B2").Value = "${table.sheetName}";
            }, GuideFixture.Context(a, b)))
            {
                var names = Sheets(book);
                Assert.Equal(new[] { "VERY_LONG_SCHEMA_VERY_LONG_TABL", "VERY_LONG_SCHEMA_VERY_LONG_ (2)" }, names);
                Assert.All(names, n => Assert.True(n.Length <= 31));
                // 링크에 쓰는 sheetName과 실제 탭 이름이 다르다 — 그래서 지침은 탭 이름을 ${table.sheetName}로 권한다
                Assert.Equal("VERY_LONG_TABLE_NAME_X1", book.Worksheet(names[0]).Cell("B1").GetText());
            }
            Assert.Equal("A_B_C_D_E_F_G_H", ExcelTemplate.SheetName("A[B]C:D*E?F/G\\H"));
            Assert.Equal("Sheet", ExcelTemplate.SheetName("History"));
        }

        [Fact]
        public void S3_1_SheetDirective_AfterContent_IsError()
        {
            var ex = Assert.Throws<TemplateException>(() => Render(wb =>
            {
                var s = wb.AddWorksheet("T");
                s.Cell("B1").Value = "머리";
                s.Cell("A2").Value = "#sheet($table in $tables)";
            }).Dispose());
            Assert.StartsWith("'T'!A2: #sheet는 시트 맨 위", ex.Message);
        }

        [Fact]
        public void S3_2_RowDirective_OnlyValueInRow_FillOnlyCellsDontCount_RowsRepeatAndDirectiveRowsVanish()
        {
            using (var book = Render(wb =>
            {
                var s = wb.AddWorksheet("S");
                s.Cell("A1").Value = "#foreach($col in $table.columns)";
                s.Range("A1:D1").Style.Fill.BackgroundColor = XLColor.LightYellow; // 칠하기만 한 칸은 세지 않는다
                s.Cell("B2").Value = "${col.name}";
                s.Cell("A3").Value = "#{end}";
                s.Cell("B4").Value = "끝";
            }, GuideFixture.Context(GuideFixture.Orders()), wrapInTable: true))
            {
                var s = book.Worksheet("S");
                Assert.Equal(new[] { "ID", "CUSTOMER_NO", "STATUS", "EMAIL", "끝" }, Enumerable.Range(1, 5).Select(r => s.Cell(r, 2).GetText()).ToArray());
            }
        }

        [Fact]
        public void S3_2_DirectiveWithOtherValueInRow_IsReadInsideCell_AndUnclosedIsTemplateError()
        {
            // 지침 9장: 같은 줄에 다른 값이 있으면 줄 지시문이 아니다 → 셀 안 지시문으로 읽혀 짝이 없으면 오류
            var ex = Assert.Throws<TemplateException>(() => Render(wb =>
            {
                var s = wb.AddWorksheet("목록");
                s.Cell("A5").Value = "#foreach($t in $tables)";
                s.Cell("C5").Value = "옆 칸 값";
            }).Dispose());
            Assert.Equal("'목록'!A5: #foreach에 맞는 #end가 없습니다.", ex.Message);
        }

        [Fact]
        public void S3_3_Cells_TextWithoutReferences_SingleReferenceKeepsNumber_EmptyIsBlank_LeadingQuoteKept_StyleCopied()
        {
            using (var book = Render(wb =>
            {
                var s = wb.AddWorksheet("S");
                s.Cell("A1").Value = "#foreach($t in $tables)";
                s.Cell("B2").Value = "가격 $5 #1";
                s.Cell("C2").Value = "${t.numRows}";
                s.Cell("D2").Value = "${t.numRows}행";
                s.Cell("E2").Value = "${t.tablespace}";
                s.Cell("F2").Value = "#foreach($c in $t.columns)#if($c.default)${c.default}#end#end";
                s.Cell("C2").Style.NumberFormat.Format = "#,##0";
                s.Cell("B2").Style.Font.Bold = true;
                s.Cell("A3").Value = "#end";
            }))
            {
                var s = book.Worksheet("S");
                Assert.Equal("가격 $5 #1", s.Cell("B1").GetText());
                Assert.True(s.Cell("B1").Style.Font.Bold);
                Assert.Equal(XLDataType.Number, s.Cell("C1").DataType);
                Assert.Equal(12345d, s.Cell("C1").GetDouble());
                Assert.Equal("#,##0", s.Cell("C1").Style.NumberFormat.Format);
                Assert.Equal("12345행", s.Cell("D1").GetText());
                Assert.Equal("'R'", s.Cell("F1").GetText());
                Assert.True(s.Cell("E2").Value.IsBlank); // CUSTOMERS: 테이블스페이스 없음 → 빈 셀
                Assert.True(s.Cell("C2").Value.IsBlank); // 통계 없음
            }
        }

        [Fact]
        public void S3_4_Formulas_OnlyBracedReferencesAreFilled_AbsoluteReferencesKept()
        {
            using (var book = Render(wb =>
            {
                var s = wb.AddWorksheet("S");
                s.Cell("A1").Value = "#foreach($t in $tables)";
                s.Cell("B2").FormulaA1 = "HYPERLINK(\"#'${t.sheetName}'!A1\",\"${t.name}\")";
                s.Cell("C2").FormulaA1 = "HYPERLINK(\"#'$t.sheetName'!A1\",\"x\")";
                s.Cell("D2").FormulaA1 = "SUM($B$1:$B$2)";
                s.Cell("A3").Value = "#end";
            }))
            {
                var s = book.Worksheet("S");
                Assert.Equal("HYPERLINK(\"#'ORDERS'!A1\",\"ORDERS\")", s.Cell("B1").FormulaA1);
                Assert.Contains("$t.sheetName", s.Cell("C1").FormulaA1); // 중괄호가 없으면 그리지 않는다(지침 9장)
                Assert.Equal("SUM($B$1:$B$2)", s.Cell("D1").FormulaA1);
            }
        }

        [Fact]
        public void S3_5_RowHeightOnlyWhenSet_MergesFollowRows_MergeOverDirectiveDropped_FreezeAndPrintTitlesMove_PrintAreaCleared()
        {
            using (var book = Render(wb =>
            {
                var s = wb.AddWorksheet("S");
                s.Cell("A1").Value = "#set($x = 1)";
                s.Cell("B2").Value = "머리";
                s.Cell("A3").Value = "#foreach($t in $tables)";
                s.Range("B4:C4").Merge();
                s.Cell("B4").Value = "${t.name}";
                s.Row(4).Height = 30;
                s.Cell("B5").Value = "높이 안 정함";
                s.Cell("A6").Value = "#end";
                s.Range("D6:D7").Merge(); // 지시문 줄을 걸친 병합 → 버려짐
                s.SheetView.FreezeRows(2);
                s.PageSetup.SetRowsToRepeatAtTop(2, 2);
                s.PageSetup.PrintAreas.Add("A1:D7");
            }))
            {
                var s = book.Worksheet("S");
                Assert.Equal("머리", s.Cell("B1").GetText());
                Assert.Equal(30, s.Row(2).Height, 1);
                Assert.Equal(30, s.Row(4).Height, 1);
                Assert.Equal(s.RowHeight, s.Row(3).Height, 1);
                Assert.Equal(new[] { "B2:C2", "B4:C4" }, s.MergedRanges.Select(m => m.RangeAddress.ToStringRelative()).OrderBy(a => a).ToArray());
                Assert.Equal(1, s.SheetView.SplitRow);
                Assert.Equal(1, s.PageSetup.FirstRowToRepeatAtTop);
                Assert.Empty(s.PageSetup.PrintAreas);
            }
        }

        // ================= 4. 문법 =================

        [Theory]
        [InlineData("#foreach($table in $tables)${table.name}_백업;#end", "ORDERS_백업;CUSTOMERS_백업;")]
        [InlineData("#foreach($table in $tables)$table.name님;#end", "ORDERS님;CUSTOMERS님;")]
        [InlineData("#foreach($t in $tables)$T.NAME;#end", "ORDERS;CUSTOMERS;")]
        [InlineData("[$!{nothing}][${nothing}][$표]", "[][${nothing}][$표]")]
        [InlineData("#foreach($t in $tables)$t.columns.size()/$t.columns.size/$t.columns.length/$t.columns.count/$t.indexes.isEmpty/$t.indexes.empty $t.name.length;#end",
            "4/4/4/4/false/false 6;1/1/1/1/true/true 9;")]
        [InlineData("\\$doc.title \\#if", "$doc.title #if")]
        [InlineData("#foreach($t in $tables)#foreach($col in $t.columns)${col.nmae}#break#end#end", "${col.nmae}#break${col.nmae}#break${col.nmae}#break${col.nmae}#break${col.nmae}#break")]
        public void S4_1_References(string template, string expected)
        {
            Assert.Equal(expected, Text(template));
        }

        [Theory]
        [InlineData("#foreach($t in $tables)#if($t.numRows > 100)큰#elseif($t.numRows)작은#{else}통계없음#end;#end", "큰;통계없음;")]
        [InlineData("#foreach($t in $tables)$foreach.count/$foreach.index/$foreach.hasNext/$foreach.first/$foreach.last/$velocityCount;#end", "1/0/true/true/false/1;2/1/false/false/true/2;")]
        [InlineData("#set($n = 0)#foreach($t in $tables)#set($n = $n + $t.columnCount)#end$n", "5")]
        [InlineData("#foreach($t in $tables)#foreach($c in $t.columns)#if($c.isPk)PK#{else}-#end#end;#end", "PK---;-;")]
        [InlineData("a## 줄 주석\nb#* 블록 주석 *#c", "abc")]
        [InlineData("#if(false)A#else(확인 불가)#end", "(확인 불가)")]
        [InlineData("#foreach($t in $tables)[#foreach($c in $t.columns)$foreach.count#end:$foreach.count]#end", "[1234:1][1:2]")]
        [InlineData("#foreach($t in $tables)#set($tableNo = $foreach.count)#foreach($c in $t.columns)$tableNo#end;#end", "1111;2;")]
        [InlineData("#foreach($t in $tables)#end[$t.name]", "[$t.name]")]
        public void S4_2_Directives(string template, string expected)
        {
            Assert.Equal(expected, Text(template));
        }

        [Theory]
        [InlineData("#foreach($t in $tables)#foreach($c in $t.columns)#if($c.no == \"1\")1#end#end#end", "11")]
        [InlineData("#foreach($t in $tables)#if($t.columnCount gt 3 and $t.status eq 'VALID')Y#{else}N#end#end", "YN")]
        [InlineData("#if('')A#end#if(0)B#end#if(null)C#end#if(false)D#end#if('x')E#end", "E")]
        [InlineData("#foreach($t in $tables)#if($t.indexes)있음#{else}없음#end;#if(!$t.indexes)빔#end;#end", "있음;;없음;빔;")]
        [InlineData("#foreach($t in $tables)#set($x = $t.owner + '.' + $t.name)$x;#end", "SHOP.ORDERS;SHOP.CUSTOMERS;")]
        [InlineData("#if(not false && (1 + 2) * 2 == 6 || false)Y#end", "Y")]
        public void S4_3_Expressions(string template, string expected)
        {
            Assert.Equal(expected, Text(template));
        }

        [Fact]
        public void S4_4_Unsupported_AreLeftAsTextOrRejected()
        {
            Assert.Equal("#macro(m) #include(\"a\") #parse(\"b\")", Text("#macro(m) #include(\"a\") #parse(\"b\")"));
            Assert.Contains("substring", Text("#foreach($t in $tables)$t.name.substring(0,3)#end"));
            Assert.Throws<TemplateException>(() => VelocityTemplate.Parse("#foreach($i in [1..3])$i#end"));
        }

        // ================= 6. 데이터 해석 규칙 =================

        [Theory]
        [InlineData("상태: R=접수, D=배송", "상태", "R=접수, D=배송")]
        [InlineData("사용 여부(Y/N)", "사용 여부", "Y/N")]
        [InlineData("주문\n고객이 낸 주문", "주문", "고객이 낸 주문")]
        [InlineData("상태 | 코드", "상태", "코드")]
        [InlineData("고객 - 주문한 사람", "고객", "주문한 사람")]
        [InlineData("주문 번호", "주문 번호", "")]
        public void S6_LogicalNameAndDescription_FromComment(string comment, string logical, string description)
        {
            string l;
            string d;
            TableSpecModel.SplitComment(comment, out l, out d);
            Assert.Equal(logical, l);
            Assert.Equal(description, d);
        }

        [Fact]
        public void S6_Keys_Codes_Stats_Views_Fallbacks()
        {
            var context = GuideFixture.Context(GuideFixture.Orders(), GuideFixture.Customers(), GuideFixture.View());
            // 키: PK 열은 UK·IX를 붙이지 않는다(ID는 IX_ORDERS_CUST에도 있음)
            Assert.Equal("ID=PK,CUSTOMER_NO=FK,IX,STATUS=,EMAIL=UK,", Render(context, "#foreach($t in $tables)#if($t.no == 1)#foreach($c in $t.columns)$c.name=$c.key,#end#end#end"));
            // 공통 코드: IN 목록만(SAL > 0 같은 검사 조건은 아님), 의미는 늘 빈 글자
            Assert.Equal("STATUS/R/[] STATUS/D/[] ", Render(context, "#foreach($code in $codes)$code.group/$code.code/[$code.meaning] #end"));
            // 통계 없음 → 빈 글자, 뷰 → 인덱스·제약 조건 없음, 외래 키 갱신 시 → 늘 NO ACTION
            Assert.Equal("12345|||", Render(context, "#foreach($t in $tables)$t.numRows|#end"));
            Assert.Equal("0/0", Render(context, "#foreach($t in $tables)#if($t.type == 'VIEW')$t.indexes.size()/$t.constraints.size()#end#end"));
            Assert.Equal("NO ACTION", Render(context, "#foreach($t in $tables)#foreach($fk in $t.foreignKeys)$fk.updateRule#end#end"));
            // DBMS 버전을 못 읽으면 빈 글자 → 지침의 대신할 글자 예
            var noVersion = TableSpecModel.Context(new TableSpecDocument(), new OracleConnectionProfile { Name = "n" }, new[] { GuideFixture.Orders() }, null);
            Assert.Equal("(확인 불가)", Render(noVersion, "#if($db.version)${db.version}#{else}(확인 불가)#end"));
        }

        // ================= 7.3 패턴 모음 =================

        [Fact]
        public void S7_3_Patterns()
        {
            using (var book = Render(wb =>
            {
                var list = wb.AddWorksheet("목록");
                list.Cell("A1").Value = "#foreach($t in $tables)";
                list.Cell("B2").FormulaA1 = "HYPERLINK(\"#'${t.sheetName}'!A1\",\"${t.name}\")";
                list.Cell("C2").Value = "#foreach($c in $t.columns)$c.name#if($foreach.hasNext), #end#end";
                list.Cell("D2").Value = "${t.numRows}";
                list.Cell("D2").Style.NumberFormat.Format = "#,##0";
                list.Cell("A3").Value = "#end";
                // 한 시트에 모든 테이블: 바깥 #foreach 줄 → 머리 줄 → 안쪽 #foreach 줄 → 열 줄 → #end → 빈 줄 → #end
                var all = wb.AddWorksheet("전체");
                all.Cell("A1").Value = "#foreach($t in $tables)";
                all.Cell("B2").Value = "${t.name}";
                all.Cell("A3").Value = "#foreach($c in $t.columns)";
                all.Cell("C4").Value = "${c.name}";
                all.Cell("D4").Value = "#if($c.isFk)→ ${c.fkRef}#end";
                all.Cell("A5").Value = "#end";
                all.Cell("A7").Value = "#end";
                var table = wb.AddWorksheet("${table.sheetName}");
                table.Cell("A1").Value = "#sheet($table in $tables)";
                table.Cell("B2").Value = "${table.name}";
                table.Cell("A3").Value = "#if(!$table.foreignKeys)";
                table.Cell("B4").Value = "(없음)";
                table.Cell("A5").Value = "#end";
            }))
            {
                Assert.Equal(new[] { "목록", "전체", "ORDERS", "CUSTOMERS" }, Sheets(book));
                var list = book.Worksheet("목록");
                Assert.Equal("HYPERLINK(\"#'CUSTOMERS'!A1\",\"CUSTOMERS\")", list.Cell("B2").FormulaA1);
                Assert.Equal("ID, CUSTOMER_NO, STATUS, EMAIL", list.Cell("C1").GetText());
                Assert.Equal(XLDataType.Number, list.Cell("D1").DataType);
                var all = book.Worksheet("전체");
                Assert.Equal(new[] { "ORDERS", "", "", "", "", "", "CUSTOMERS", "", "" },
                    Enumerable.Range(1, 9).Select(r => all.Cell(r, 2).Value.IsText ? all.Cell(r, 2).GetText() : "").ToArray());
                Assert.Equal("→ SHOP.CUSTOMERS.NO", all.Cell("D3").GetText());
                Assert.Equal("NO", all.Cell("C8").GetText());
                // #sheet 줄이 빠져 이름은 1행, "(없음)"은 외래 키가 없는 CUSTOMERS에만 2행
                Assert.True(book.Worksheet("ORDERS").Cell("B2").Value.IsBlank);
                Assert.Equal("(없음)", book.Worksheet("CUSTOMERS").Cell("B2").GetText());
            }
        }

        // ================= 9. 자주 하는 실수 =================

        [Fact]
        public void S9_Mistakes_ShowUpAsDocumented()
        {
            // 변수 오타 → 글자 그대로 남는다
            Assert.Contains("${col.nmae}", Text("#foreach($t in $tables)#foreach($col in $t.columns)${col.nmae}#end#end"));
            // 짝 안 맞음 → 셀 위치가 든 오류
            var ex = Assert.Throws<TemplateException>(() => Render(wb =>
            {
                var s = wb.AddWorksheet("시트");
                s.Cell("A5").Value = "#foreach($t in $tables)";
                s.Cell("B6").Value = "${t.name}";
            }).Dispose());
            Assert.Equal("'시트'!A5: #foreach에 맞는 #end가 없습니다.", ex.Message);
            // 숫자에 단위를 붙이면 글자 셀
            using (var book = Render(wb =>
            {
                var s = wb.AddWorksheet("S");
                s.Cell("A1").Value = "#foreach($t in $tables)";
                s.Cell("B2").Value = "${t.numRows}행";
                s.Cell("A3").Value = "#end";
                wb.AddWorksheet("안내").Cell("A1").Value = "'_'로 시작하지 않으면 남는다";
            }))
            {
                Assert.Equal(XLDataType.Text, book.Worksheet("S").Cell("B1").DataType);
                Assert.Contains("안내", Sheets(book));
            }
        }

        // ================= 도우미 =================

        private static string Text(string template)
        {
            return Render(GuideFixture.Rich(), template);
        }

        private static string Render(VelocityContext context, string template)
        {
            return VelocityTemplate.Parse(template).Render(context);
        }

        private static XLWorkbook Render(Action<XLWorkbook> build, VelocityContext context = null, bool wrapInTable = false)
        {
            byte[] bytes;
            using (var workbook = new XLWorkbook())
            using (var stream = new MemoryStream())
            {
                build(workbook);
                workbook.SaveAs(stream);
                bytes = stream.ToArray();
            }
            context = context ?? GuideFixture.Rich();
            if (wrapInTable)
            {
                // $table 하나를 쓰는 시험: 첫 테이블을 $table로
                object tables;
                context.TryGet("tables", out tables);
                context.Set("table", ((List<object>)tables)[0]);
            }
            var output = new MemoryStream();
            ExcelTemplate.Render(bytes, context, output);
            output.Position = 0;
            return new XLWorkbook(output);
        }

        private static string[] Sheets(XLWorkbook book)
        {
            return book.Worksheets.Select(w => w.Name).ToArray();
        }
    }

    /// <summary>지침 시험용 데이터: ORDERS(주석 두 줄·통계·PK·FK+인덱스·고유·검사 IN·기본값 'R'), CUSTOMERS(통계·제약 없음), V_ORDERS(뷰).</summary>
    internal static class GuideFixture
    {
        public static TableDescription Orders()
        {
            var t = new TableDescription { Owner = "SHOP", Name = "ORDERS", Type = "TABLE", Status = "VALID", Comment = "주문\n고객이 낸 주문 한 건", NumRows = 12345, Tablespace = "USERS" };
            t.Columns.Add(Col("ID", "NUMBER(10)", 1, false, "주문 번호"));
            t.Columns.Add(Col("CUSTOMER_NO", "NUMBER(10)", 2, false, "고객 번호"));
            t.Columns.Add(Col("STATUS", "CHAR(1 BYTE)", 3, false, "상태: R=접수, D=배송"));
            t.Columns.Add(Col("EMAIL", "VARCHAR2(100 CHAR)", 4, true, "알림 메일(선택)"));
            t.Columns[2].Default = "'R'";
            t.Indexes.Add(Index("PK_ORDERS", true, "ID"));
            t.Indexes.Add(Index("IX_ORDERS_CUST", false, "CUSTOMER_NO", "ID DESC"));
            t.Indexes.Add(Index("UQ_ORDERS_EMAIL", true, "EMAIL"));
            t.Constraints.Add(Con("PK_ORDERS", "P", "PK_ORDERS", "ID"));
            t.Constraints.Add(Con("UQ_ORDERS_EMAIL", "U", "UQ_ORDERS_EMAIL", "EMAIL"));
            var fk = Con("FK_ORDERS_CUST", "R", null, "CUSTOMER_NO");
            fk.Reference = "SHOP.CUSTOMERS";
            fk.ReferenceColumns.Add("NO");
            fk.DeleteRule = "CASCADE";
            t.Constraints.Add(fk);
            var inList = Con("CK_ORDERS_STATUS", "C", null, "STATUS");
            inList.Condition = "STATUS IN ('R','D')";
            t.Constraints.Add(inList);
            var positive = Con("CK_ORDERS_ID", "C", null, "ID");
            positive.Condition = "ID > 0";
            t.Constraints.Add(positive);
            return t;
        }

        public static TableDescription Customers()
        {
            var t = new TableDescription { Owner = "SHOP", Name = "CUSTOMERS", Type = "TABLE", Status = "VALID" };
            t.Columns.Add(Col("NO", "NUMBER(10)", 1, false, null));
            return t;
        }

        public static TableDescription View()
        {
            var t = new TableDescription { Owner = "SHOP", Name = "V_ORDERS", Type = "VIEW", Status = "VALID" };
            t.Columns.Add(Col("ID", "NUMBER(10)", 1, false, null));
            return t;
        }

        public static VelocityContext Context(params TableDescription[] tables)
        {
            var doc = new TableSpecDocument { Title = "지침 시험", Project = "쇼핑몰", System = "주문", Author = "홍길동", Date = new DateTime(2026, 10, 10) };
            var profile = new OracleConnectionProfile { Name = "개발", Host = "db", Port = 1521, ServiceName = "orcl", UserId = "shop" };
            return TableSpecModel.Context(doc, profile, tables, "Oracle Database 19c Enterprise Edition 19.0.0.0.0");
        }

        public static VelocityContext Rich()
        {
            return Context(Orders(), Customers());
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

        private static ConstraintDescription Con(string name, string type, string indexName, params string[] columns)
        {
            var constraint = new ConstraintDescription { Name = name, Type = type, Status = "ENABLED", Columns = string.Join(", ", columns), IndexName = indexName };
            constraint.ColumnNames.AddRange(columns);
            return constraint;
        }
    }
}

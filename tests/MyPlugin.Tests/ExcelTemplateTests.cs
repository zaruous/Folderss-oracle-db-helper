using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    /// <summary>ExcelTemplate: 셀 값·줄 지시문(#foreach·#if·#set)·시트 지시문(#sheet)·병합·수식·안내 시트·오류 위치. 기본 템플릿으로 산출물 만들기.</summary>
    public class ExcelTemplateTests
    {
        private static Dictionary<string, object> Map(params object[] pairs)
        {
            var map = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i + 1 < pairs.Length; i += 2)
                map[(string)pairs[i]] = pairs[i + 1];
            return map;
        }

        private static VelocityContext Context()
        {
            return new VelocityContext(new Dictionary<string, object>
            {
                { "title", "정의서" },
                { "quoted", "'N'" },
                { "cols", new List<object> { Map("no", 1, "name", "A", "pk", true), Map("no", 2, "name", "B", "pk", false), Map("no", 3, "name", "C", "pk", false) } },
                { "none", new List<object>() },
                { "items", new List<object> { Map("name", "EMP"), Map("name", "DEPT"), Map("name", "EMP") } }
            });
        }

        private static byte[] Template(Action<XLWorkbook> build)
        {
            using (var workbook = new XLWorkbook())
            using (var stream = new MemoryStream())
            {
                build(workbook);
                workbook.SaveAs(stream);
                return stream.ToArray();
            }
        }

        private static XLWorkbook Render(byte[] template, VelocityContext context = null)
        {
            var output = new MemoryStream();
            ExcelTemplate.Render(template, context ?? Context(), output);
            output.Position = 0;
            return new XLWorkbook(output);
        }

        [Fact]
        public void ForeachRows_RepeatAndDirectiveRowsDisappear()
        {
            var template = Template(wb =>
            {
                var s = wb.AddWorksheet("S");
                s.Cell("A1").Value = "제목: ${title}";
                s.Cell("A2").Value = "#foreach($c in $cols)";
                s.Cell("A3").Value = "${c.no}";
                s.Cell("B3").Value = "${c.name}#if($c.pk) (PK)#end";
                s.Cell("B3").Style.Font.Bold = true;
                s.Range("C3:D3").Merge();
                s.Cell("C3").Value = "$foreach.count/$cols.size()";
                s.Row(3).Height = 30;
                s.Cell("A4").Value = "#end";
                s.Cell("A5").Value = "끝";
            });

            using (var result = Render(template))
            {
                var s = result.Worksheet("S");
                Assert.Equal("제목: 정의서", s.Cell("A1").GetText());
                Assert.Equal(1d, s.Cell("A2").GetDouble());
                Assert.Equal(3d, s.Cell("A4").GetDouble());
                Assert.Equal("A (PK)", s.Cell("B2").GetText());
                Assert.Equal("B", s.Cell("B3").GetText());
                Assert.True(s.Cell("B4").Style.Font.Bold);
                Assert.Equal("3/3", s.Cell("C4").GetText());
                Assert.Equal(30, s.Row(4).Height, 1);
                Assert.Equal("끝", s.Cell("A5").GetText());
                Assert.True(s.Cell("A6").IsEmpty());
                var merges = s.MergedRanges.Select(m => m.RangeAddress.ToStringRelative()).OrderBy(a => a).ToArray();
                Assert.Equal(new[] { "C2:D2", "C3:D3", "C4:D4" }, merges);
            }
        }

        [Fact]
        public void IfRows_ElseAndNestedForeach()
        {
            var template = Template(wb =>
            {
                var s = wb.AddWorksheet("S");
                s.Cell("A1").Value = "#if($none)";
                s.Cell("A2").Value = "있음";
                s.Cell("A3").Value = "#else";
                s.Cell("A4").Value = "없음";
                s.Cell("A5").Value = "#end";
                s.Cell("A6").Value = "#foreach($c in $cols)";
                s.Cell("A7").Value = "#if($c.pk)";
                s.Cell("A8").Value = "PK ${c.name}";
                s.Cell("A9").Value = "#elseif($c.no == 3)";
                s.Cell("A10").Value = "마지막 ${c.name}";
                s.Cell("A11").Value = "#end";
                s.Cell("A12").Value = "#end";
                s.Cell("A13").Value = "#set($done = 'ok')";
                s.Cell("A14").Value = "$done";
            });

            using (var result = Render(template))
            {
                var s = result.Worksheet("S");
                Assert.Equal(new[] { "없음", "PK A", "마지막 C", "ok" }, Enumerable.Range(1, 4).Select(r => s.Cell(r, 1).GetText()).ToArray());
                Assert.True(s.Cell("A5").IsEmpty());
            }
        }

        [Fact]
        public void SheetDirective_CopiesSheetPerItem_UniqueNames_HelpSheetRemoved()
        {
            var template = Template(wb =>
            {
                wb.AddWorksheet("표지").Cell("A1").Value = "${title}";
                var t = wb.AddWorksheet("${it.name}");
                t.Column(2).Width = 33;
                t.Cell("A1").Value = "#sheet($it in $items)";
                t.Cell("A2").Value = "${it.name} ${foreach.count}";
                wb.AddWorksheet("_안내").Cell("A1").Value = "${이건 그리지 않음";
                wb.AddWorksheet("끝");
            });

            using (var result = Render(template))
            {
                Assert.Equal(new[] { "표지", "EMP", "DEPT", "EMP (2)", "끝" }, result.Worksheets.Select(w => w.Name).ToArray());
                Assert.Equal("DEPT 2", result.Worksheet("DEPT").Cell("A1").GetText());
                Assert.Equal("EMP 3", result.Worksheet("EMP (2)").Cell("A1").GetText());
                Assert.Equal(33, result.Worksheet("EMP (2)").Column(2).Width, 1);
            }
        }

        [Fact]
        public void SheetDirective_NoItems_RemovesSheet()
        {
            var template = Template(wb =>
            {
                wb.AddWorksheet("A").Cell("A1").Value = "x";
                wb.AddWorksheet("T").Cell("A1").Value = "#sheet($it in $none)";
            });

            using (var result = Render(template))
                Assert.Equal(new[] { "A" }, result.Worksheets.Select(w => w.Name).ToArray());
        }

        [Fact]
        public void Formula_RenderedOnlyWithBraces()
        {
            var template = Template(wb =>
            {
                var s = wb.AddWorksheet("S");
                s.Cell("A1").FormulaA1 = "HYPERLINK(\"#'${title}'!A1\",\"${title}\")";
                s.Cell("A2").FormulaA1 = "SUM($B$1:$B$2)";
                s.Cell("A3").Value = "${quoted}";
            });

            using (var result = Render(template))
            {
                var s = result.Worksheet("S");
                Assert.Equal("HYPERLINK(\"#'정의서'!A1\",\"정의서\")", s.Cell("A1").FormulaA1);
                Assert.Equal("SUM($B$1:$B$2)", s.Cell("A2").FormulaA1);
                // 맨 앞 '가 떨어지지 않는다(Oracle 기본값 'N' 등)
                Assert.Equal("'N'", s.Cell("A3").GetText());
            }
        }

        [Fact]
        public void InlineDirectiveWithOtherCells_IsNotRowDirective()
        {
            var template = Template(wb =>
            {
                var s = wb.AddWorksheet("S");
                s.Cell("A1").Value = "#if($title)Y#end";
                s.Cell("A2").Value = "#set($x = 1)";
                s.Cell("B2").Value = "옆 칸이 있으면 줄 지시문이 아니다 $x";
            });

            using (var result = Render(template))
            {
                var s = result.Worksheet("S");
                Assert.Equal("Y", s.Cell("A1").GetText());
                Assert.True(s.Cell("A2").IsEmpty());
                Assert.Equal("옆 칸이 있으면 줄 지시문이 아니다 1", s.Cell("B2").GetText());
            }
        }

        [Theory]
        [InlineData("#foreach($c in $cols)", "'S'!A2: #foreach에 맞는 #end가 없습니다.")]
        [InlineData("#end", "'S'!A2: #end에 맞는 #foreach·#if가 없습니다.")]
        [InlineData("#else", "'S'!A2: #else에 맞는 #if가 없습니다.")]
        [InlineData("#if($a ==)", "'S'!A2: 식이 비어 있거나")]
        public void Errors_NameTheCell(string directive, string message)
        {
            var template = Template(wb =>
            {
                var s = wb.AddWorksheet("S");
                s.Cell("A1").Value = "x";
                s.Cell("A2").Value = directive;
            });

            var ex = Assert.Throws<TemplateException>(() => Render(template).Dispose());
            Assert.StartsWith(message, ex.Message);
        }

        [Fact]
        public void CellError_NamesTheCell()
        {
            var template = Template(wb => wb.AddWorksheet("S").Cell("C3").Value = "#if($title)x");
            var ex = Assert.Throws<TemplateException>(() => Render(template).Dispose());
            Assert.Equal("'S'!C3: #if에 맞는 #end가 없습니다.", ex.Message);
        }

        [Theory]
        [InlineData("SCOTT.EMP", "SCOTT.EMP")]
        [InlineData("A/B:C*D?[E]", "A_B_C_D__E_")]
        [InlineData("'quoted' name", "_quoted_ name")]
        [InlineData("", "Sheet")]
        [InlineData("history", "Sheet")]
        [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789", "ABCDEFGHIJKLMNOPQRSTUVWXYZ01234")]
        public void SheetName_FollowsExcelRules(string name, string expected)
        {
            Assert.Equal(expected, ExcelTemplate.SheetName(name));
        }

        [Fact]
        public void UniqueSheetName_AddsSuffixWithin31()
        {
            var used = new List<string> { "EMP", "emp (2)", new string('X', 31) };
            Assert.Equal("EMP (3)", ExcelTemplate.UniqueSheetName("EMP", used));
            Assert.Equal(new string('X', 27) + " (2)", ExcelTemplate.UniqueSheetName(new string('X', 40), used));
        }

        // ---------- 기본 템플릿 ----------

        private static TableDescription Emp()
        {
            var emp = new TableDescription { Owner = "SCOTT", Name = "EMP", Type = "TABLE", Comment = "사원", NumRows = 14, Tablespace = "USERS", Status = "VALID" };
            emp.Columns.Add(new DescribedColumn { Column = new ColumnInfo { Name = "EMPNO", TypeLabel = "NUMBER(4)", Nullable = false, Position = 1 }, Comment = "사원 번호" });
            emp.Columns.Add(new DescribedColumn { Column = new ColumnInfo { Name = "ENAME", TypeLabel = "VARCHAR2(10 CHAR)", Nullable = true, Position = 2 }, Comment = "이름" });
            emp.Columns.Add(new DescribedColumn { Column = new ColumnInfo { Name = "HIREDATE", TypeLabel = "DATE", Nullable = true, Position = 3 }, Default = "SYSDATE" });
            emp.Columns.Add(new DescribedColumn { Column = new ColumnInfo { Name = "DEPTNO", TypeLabel = "NUMBER(2)", Nullable = true, Position = 4 }, Comment = "부서 번호" });
            emp.Indexes.Add(new IndexDescription { Name = "PK_EMP", Unique = true, Type = "NORMAL", Status = "VALID", Columns = "EMPNO" });
            var pk = new ConstraintDescription { Name = "PK_EMP", Type = "P", Status = "ENABLED", Columns = "EMPNO" };
            pk.ColumnNames.Add("EMPNO");
            var fk = new ConstraintDescription { Name = "FK_DEPTNO", Type = "R", Status = "ENABLED", Columns = "DEPTNO", Reference = "SCOTT.DEPT" };
            fk.ColumnNames.Add("DEPTNO");
            fk.ReferenceColumns.Add("DEPTNO");
            emp.Constraints.Add(pk);
            emp.Constraints.Add(fk);
            return emp;
        }

        private static TableDescription Dept()
        {
            var dept = new TableDescription { Owner = "SCOTT", Name = "DEPT", Type = "TABLE", Comment = "부서" };
            dept.Columns.Add(new DescribedColumn { Column = new ColumnInfo { Name = "DEPTNO", TypeLabel = "NUMBER(2)", Nullable = false, Position = 1 } });
            dept.Columns.Add(new DescribedColumn { Column = new ColumnInfo { Name = "DNAME", TypeLabel = "VARCHAR2(14)", Nullable = true, Position = 2 }, Comment = "부서 이름" });
            return dept;
        }

        [Fact]
        public void DefaultTemplate_MakesCoverListAndTableSheets()
        {
            var doc = new TableSpecDocument { Title = "주문 시스템 테이블 정의서", Project = "주문 시스템", Author = "홍길동", Version = "1.0", Date = new DateTime(2026, 10, 10) };
            var profile = new OracleConnectionProfile { Name = "개발", Host = "localhost", Port = 1521, ServiceName = "xe", UserId = "scott" };
            var context = TableSpecModel.Context(doc, profile, new[] { Emp(), Dept() });

            var output = new MemoryStream();
            ExcelTemplate.Render(TableSpecTemplate.Create(), context, output);
            SaveSample(output, "테이블정의서_샘플.xlsx");
            output.Position = 0;
            using (var result = new XLWorkbook(output))
            {
                Assert.Equal(new[] { TableSpecTemplate.CoverSheet, TableSpecTemplate.ListSheet, "EMP", "DEPT" }, result.Worksheets.Select(w => w.Name).ToArray());

                var cover = result.Worksheet(TableSpecTemplate.CoverSheet);
                Assert.Equal("주문 시스템 테이블 정의서", cover.Cell(8, 2).GetText());
                Assert.Equal("개발 (scott@localhost:1521/xe)", cover.Cell(17, 5).GetText());
                Assert.Equal("2개", cover.Cell(19, 5).GetText());

                var list = result.Worksheet(TableSpecTemplate.ListSheet);
                Assert.Equal(1d, list.Cell(5, 2).GetDouble());
                Assert.Equal("HYPERLINK(\"#'EMP'!A1\",\"EMP\")", list.Cell(5, 4).FormulaA1);
                Assert.Equal("부서", list.Cell(6, 5).GetText());
                Assert.Equal(14d, list.Cell(5, 8).GetDouble());
                Assert.True(list.Cell(6, 8).Value.IsBlank);
                Assert.True(list.Cell(7, 2).IsEmpty());

                // 테이블 시트: #sheet 줄이 빠져 한 줄씩 위로
                var emp = result.Worksheet("EMP");
                Assert.Equal("EMP", emp.Cell(1, 2).GetText());
                Assert.Equal("열 (4)", emp.Cell(8, 2).GetText());
                Assert.Equal("EMPNO", emp.Cell(10, 3).GetText());
                Assert.Equal("NUMBER", emp.Cell(10, 5).GetText());
                Assert.Equal("4", emp.Cell(10, 6).GetText());
                Assert.Equal("Y", emp.Cell(10, 8).GetText());
                Assert.Equal("VARCHAR2", emp.Cell(11, 5).GetText());
                Assert.Equal("10 CHAR", emp.Cell(11, 6).GetText());
                Assert.Equal("SYSDATE", emp.Cell(12, 10).GetText());
                Assert.Equal("Y", emp.Cell(13, 9).GetText());
                Assert.Equal("→ SCOTT.DEPT.DEPTNO", emp.Cell(13, 11).GetText());
                Assert.Equal("인덱스", emp.Cell(15, 2).GetText());
                Assert.Equal("PK_EMP", emp.Cell(17, 3).GetText());
                Assert.Equal("제약 조건", emp.Cell(19, 2).GetText());
                Assert.Equal("→ SCOTT.DEPT(DEPTNO)", emp.Cell(22, 7).GetText());

                var dept = result.Worksheet("DEPT");
                Assert.Equal("(인덱스 없음)", dept.Cell(15, 2).GetText());
                Assert.Equal("(제약 조건 없음)", dept.Cell(19, 2).GetText());
            }
        }

        [Fact]
        public void DefaultTemplate_HasHelpSheetForEditing()
        {
            var bytes = TableSpecTemplate.Create();
            SaveSample(new MemoryStream(bytes), "테이블정의서_기본템플릿.xlsx");
            using (var template = new XLWorkbook(new MemoryStream(bytes)))
            {
                Assert.Equal(new[] { TableSpecTemplate.CoverSheet, TableSpecTemplate.ListSheet, TableSpecTemplate.TableSheet, TableSpecTemplate.HelpSheet },
                    template.Worksheets.Select(w => w.Name).ToArray());
                Assert.Equal("#sheet($table in $tables)", template.Worksheet(TableSpecTemplate.TableSheet).Cell("A1").GetText());
                Assert.Contains(template.Worksheet(TableSpecTemplate.HelpSheet).CellsUsed(), c => c.GetText() == "$col.default");
            }
        }

        // TABLESPEC_SAMPLE_DIR가 있으면 결과 파일을 그 폴더에 남긴다(눈으로 확인용)
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

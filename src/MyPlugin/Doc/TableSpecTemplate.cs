using System.IO;
using ClosedXML.Excel;

namespace MyPlugin
{
    /// <summary>
    /// 기본 테이블 정의서 템플릿(.xlsx)을 코드로 만든다. 시트:
    /// - 표지: ${doc.title}(문서 이름)·프로젝트·대상 DB·스키마·버전·작성자·작성일, 개정 이력.
    /// - 목록: #foreach($t in $tables)로 테이블마다 한 줄(이름은 그 테이블 시트로 가는 링크).
    /// - ${table.sheetName}: 맨 위 #sheet($table in $tables)로 테이블마다 시트 하나 — 개요, 열(#foreach), 인덱스, 제약 조건(#if로 없음 표시).
    /// - _변수 설명: 문법·변수 안내(이름이 '_'로 시작해 결과에서 빠진다).
    /// 사용자는 [기본 템플릿 저장…]으로 이 파일을 받아 고친 뒤 [템플릿 파일]로 고를 수 있다.
    /// </summary>
    internal static class TableSpecTemplate
    {
        public const string CoverSheet = "표지";
        public const string ListSheet = "목록";
        public const string TableSheet = "${table.sheetName}";
        public const string HelpSheet = "_변수 설명";

        internal static readonly XLColor Navy = XLColor.FromHtml("#1F3864");
        internal static readonly XLColor HeaderFill = XLColor.FromHtml("#D9E1F2");
        internal static readonly XLColor LabelFill = XLColor.FromHtml("#F2F2F2");
        internal static readonly XLColor LineColor = XLColor.FromHtml("#8EA9DB");
        internal static readonly XLColor DirectiveFill = XLColor.FromHtml("#FFF2CC");
        internal static readonly XLColor DirectiveText = XLColor.FromHtml("#7F6000");
        internal static readonly XLColor Muted = XLColor.FromHtml("#595959");

        public static byte[] Create()
        {
            using (var workbook = new XLWorkbook())
            using (var stream = new MemoryStream())
            {
                workbook.Style.Font.FontName = "맑은 고딕";
                workbook.Style.Font.FontSize = 10;
                Cover(workbook.AddWorksheet(CoverSheet));
                List(workbook.AddWorksheet(ListSheet));
                Table(workbook.AddWorksheet(TableSheet));
                Help(workbook.AddWorksheet(HelpSheet));
                workbook.Worksheet(CoverSheet).SetTabActive();
                workbook.SaveAs(stream);
                return stream.ToArray();
            }
        }

        public static void Save(string path)
        {
            File.WriteAllBytes(path, Create());
        }

        // ================= 표지 =================

        private static void Cover(IXLWorksheet sheet)
        {
            sheet.ShowGridLines = false;
            sheet.Column(1).Width = 3;
            sheet.Columns(2, 9).Width = 12;
            sheet.Column(10).Width = 3;

            var title = Merge(sheet, 8, 2, 10, 9, "${doc.title}");
            title.Style.Font.FontSize = 28;
            title.Style.Font.Bold = true;
            title.Style.Font.FontColor = Navy;
            title.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            title.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            title.Style.Border.BottomBorder = XLBorderStyleValues.Medium;
            title.Style.Border.BottomBorderColor = Navy;

            var project = Merge(sheet, 11, 2, 11, 9, "${doc.project}");
            project.Style.Font.FontSize = 14;
            project.Style.Font.FontColor = Muted;
            project.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            sheet.Row(11).Height = 24;

            var facts = new[,]
            {
                { "문서 이름", "${doc.title}" },
                { "프로젝트", "${doc.project}" },
                { "대상 DB", "${db.name} (${db.address})" },
                { "대상 스키마", "${doc.schemas}" },
                { "대상 테이블", "${doc.tableCount}개" },
                { "버전", "${doc.version}" },
                { "작성자", "${doc.author}" },
                { "작성일", "${doc.date}" },
                { "설명", "${doc.description}" }
            };
            for (var i = 0; i < facts.GetLength(0); i++)
            {
                var row = 15 + i;
                var label = Merge(sheet, row, 3, row, 4, facts[i, 0]);
                Label(label);
                var value = Merge(sheet, row, 5, row, 8, facts[i, 1]);
                value.Style.Alignment.Indent = 1;
                Box(sheet.Range(row, 3, row, 8));
                sheet.Row(row).Height = 20;
            }

            Section(sheet.Cell(26, 3), "개정 이력");
            var history = new[] { "버전", "일자", "작성자", "내용" };
            var columns = new[] { 3, 4, 5, 6 };
            for (var i = 0; i < history.Length; i++)
            {
                var cell = i < 3 ? sheet.Cell(27, columns[i]) : Merge(sheet, 27, 6, 27, 8, history[i]);
                cell.Value = history[i];
                Header(i < 3 ? cell.AsRange() : sheet.Range(27, 6, 27, 8));
            }
            sheet.Cell(28, 3).Value = "${doc.version}";
            sheet.Cell(28, 4).Value = "${doc.date}";
            sheet.Cell(28, 5).Value = "${doc.author}";
            Merge(sheet, 28, 6, 28, 8, "최초 작성");
            for (var row = 29; row <= 31; row++)
                sheet.Range(row, 6, row, 8).Merge();
            Box(sheet.Range(27, 3, 31, 8));
            sheet.Range(28, 3, 31, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            sheet.Cell(34, 2).Value = "${doc.generatedAt} · DB Helper로 만듦";
            sheet.Cell(34, 2).Style.Font.FontColor = Muted;
            sheet.Cell(34, 2).Style.Font.FontSize = 9;

            sheet.PageSetup.PaperSize = XLPaperSize.A4Paper;
            sheet.PageSetup.PageOrientation = XLPageOrientation.Portrait;
            sheet.PageSetup.CenterHorizontally = true;
            sheet.PageSetup.FitToPages(1, 1);
        }

        // ================= 목록 =================

        private static void List(IXLWorksheet sheet)
        {
            sheet.ShowGridLines = false;
            var widths = new double[] { 2, 6, 14, 30, 40, 8, 8, 12 };
            for (var i = 0; i < widths.Length; i++)
                sheet.Column(i + 1).Width = widths[i];

            var title = sheet.Cell(1, 2);
            title.Value = "테이블 목록";
            title.Style.Font.FontSize = 16;
            title.Style.Font.Bold = true;
            title.Style.Font.FontColor = Navy;
            sheet.Row(1).Height = 28;
            sheet.Cell(2, 2).Value = "${db.name} · ${doc.schemas} · ${doc.tableCount}개";
            sheet.Cell(2, 2).Style.Font.FontColor = Muted;

            var headers = new[] { "No", "스키마", "테이블 이름", "설명", "종류", "열 수", "행 수(통계)" };
            for (var i = 0; i < headers.Length; i++)
                sheet.Cell(4, 2 + i).Value = headers[i];
            Header(sheet.Range(4, 2, 4, 8));

            Directive(sheet, 5, "#foreach($t in $tables)");
            sheet.Cell(6, 2).Value = "${foreach.count}";
            sheet.Cell(6, 3).Value = "${t.owner}";
            // 이름은 그 테이블 시트로 가는 링크(시트 이름은 $t.sheetName — 31자 안, 겹치지 않게 고친 값)
            sheet.Cell(6, 4).FormulaA1 = "HYPERLINK(\"#'${t.sheetName}'!A1\",\"${t.name}\")";
            sheet.Cell(6, 4).Style.Font.FontColor = XLColor.FromHtml("#0563C1");
            sheet.Cell(6, 4).Style.Font.Underline = XLFontUnderlineValues.Single;
            sheet.Cell(6, 5).Value = "${t.comment}";
            sheet.Cell(6, 6).Value = "${t.typeLabel}";
            sheet.Cell(6, 7).Value = "${t.columnCount}";
            sheet.Cell(6, 8).Value = "${t.numRows}";
            sheet.Cell(6, 8).Style.NumberFormat.Format = "#,##0";
            Box(sheet.Range(6, 2, 6, 8));
            sheet.Range(6, 2, 6, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            sheet.Range(6, 6, 6, 7).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            Directive(sheet, 7, "#end");

            Directive(sheet, 8, "#if(!$tables)");
            Merge(sheet, 9, 2, 9, 8, "(대상 테이블이 없습니다)").Style.Font.FontColor = Muted;
            Directive(sheet, 10, "#end");

            sheet.SheetView.FreezeRows(4);
            sheet.PageSetup.PaperSize = XLPaperSize.A4Paper;
            sheet.PageSetup.PageOrientation = XLPageOrientation.Landscape;
            sheet.PageSetup.FitToPages(1, 0);
            sheet.PageSetup.SetRowsToRepeatAtTop(4, 4);
        }

        // ================= 테이블 한 개 =================

        private static void Table(IXLWorksheet sheet)
        {
            sheet.ShowGridLines = false;
            // B No · C 열 이름 · D 설명 · E 형식 · F 길이 · G NULL · H PK · I FK · J 기본값 · K 비고
            var widths = new double[] { 2, 5, 24, 30, 13, 9, 6, 5, 5, 14, 26 };
            for (var i = 0; i < widths.Length; i++)
                sheet.Column(i + 1).Width = widths[i];

            Directive(sheet, 1, "#sheet($table in $tables)");

            var back = sheet.Cell(2, 11);
            back.FormulaA1 = "HYPERLINK(\"#'" + ListSheet + "'!A1\",\"← 목록\")";
            back.Style.Font.FontColor = XLColor.FromHtml("#0563C1");
            back.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

            var name = Merge(sheet, 2, 2, 2, 10, "${table.name}");
            name.Style.Font.FontSize = 16;
            name.Style.Font.Bold = true;
            name.Style.Font.FontColor = Navy;
            sheet.Row(2).Height = 26;
            var comment = Merge(sheet, 3, 2, 3, 11, "${table.comment}");
            comment.Style.Font.FontColor = Muted;
            comment.Style.Border.BottomBorder = XLBorderStyleValues.Medium;
            comment.Style.Border.BottomBorderColor = Navy;
            sheet.Range(3, 2, 3, 11).Style.Border.BottomBorder = XLBorderStyleValues.Medium;
            sheet.Range(3, 2, 3, 11).Style.Border.BottomBorderColor = Navy;

            // 개요: 이름표(B:C) 값(D) 이름표(E:F) 값(G:H) 이름표(I:J) 값(K)
            Fact(sheet, 5, "스키마", "${table.owner}", "종류", "${table.typeLabel}", "행 수(통계)", "${table.numRows}");
            Fact(sheet, 6, "테이블스페이스", "${table.tablespace}", "기본 키", "${table.pkName}", "기본 키 열", "${table.pkColumns}");
            Fact(sheet, 7, "만든 날", "${table.created}", "마지막 DDL", "${table.lastDdl}", "통계 날짜", "${table.lastAnalyzed}");
            sheet.Cell(5, 11).Style.NumberFormat.Format = "#,##0";
            sheet.Cell(5, 11).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
            Box(sheet.Range(5, 2, 7, 11));

            // 열
            Section(sheet.Cell(9, 2), "열 (${table.columnCount})");
            var headers = new[] { "No", "열 이름", "설명", "형식", "길이", "NULL", "PK", "FK", "기본값", "비고" };
            for (var i = 0; i < headers.Length; i++)
                sheet.Cell(10, 2 + i).Value = headers[i];
            Header(sheet.Range(10, 2, 10, 11));
            Directive(sheet, 11, "#foreach($col in $table.columns)");
            var values = new[] { "${col.no}", "${col.name}", "${col.comment}", "${col.dataType}", "${col.length}", "${col.nullable}", "${col.pk}", "${col.fk}", "${col.default}",
                "#if($col.isFk)→ ${col.fkRef}#end" };
            for (var i = 0; i < values.Length; i++)
                sheet.Cell(12, 2 + i).Value = values[i];
            Box(sheet.Range(12, 2, 12, 11));
            foreach (var column in new[] { 2, 6, 7, 8, 9 })
                sheet.Cell(12, column).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            sheet.Range(12, 2, 12, 11).Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
            sheet.Cell(12, 4).Style.Alignment.WrapText = true;
            Directive(sheet, 13, "#end");

            // 인덱스: No(B) 이름(C) 열(D:F) 고유(G) 종류(H:J) 상태(K)
            Section(sheet.Cell(15, 2), "인덱스");
            Row(sheet, 16, new[] { "No", "인덱스 이름", "열", "고유", "종류", "상태" });
            Header(sheet.Range(16, 2, 16, 11));
            Directive(sheet, 17, "#foreach($idx in $table.indexes)");
            Row(sheet, 18, new[] { "${idx.no}", "${idx.name}", "${idx.columns}", "${idx.unique}", "${idx.type}", "${idx.status}" });
            Box(sheet.Range(18, 2, 18, 11));
            sheet.Cell(18, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            sheet.Cell(18, 7).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            Directive(sheet, 19, "#end");
            Directive(sheet, 20, "#if(!$table.indexes)");
            Empty(sheet, 21, "(인덱스 없음)");
            Directive(sheet, 22, "#end");

            // 제약 조건: No(B) 이름(C) 종류(D) 열(E:F) 참조·조건(G:J) 상태(K)
            Section(sheet.Cell(24, 2), "제약 조건");
            ConstraintRow(sheet, 25, new[] { "No", "제약 조건 이름", "종류", "열", "참조 · 조건", "상태" });
            Header(sheet.Range(25, 2, 25, 11));
            Directive(sheet, 26, "#foreach($con in $table.constraints)");
            ConstraintRow(sheet, 27, new[] { "${con.no}", "${con.name}", "${con.typeLabel}", "${con.columns}",
                "#if($con.type == \"R\")→ ${con.reference}(${con.referenceColumns})#elseif($con.type == \"C\")${con.condition}#end", "${con.status}" });
            Box(sheet.Range(27, 2, 27, 11));
            sheet.Cell(27, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            Directive(sheet, 28, "#end");
            Directive(sheet, 29, "#if(!$table.constraints)");
            Empty(sheet, 30, "(제약 조건 없음)");
            Directive(sheet, 31, "#end");

            sheet.PageSetup.PaperSize = XLPaperSize.A4Paper;
            sheet.PageSetup.PageOrientation = XLPageOrientation.Landscape;
            sheet.PageSetup.FitToPages(1, 0);
        }

        // 이름표 B:C · 값 D · 이름표 E · 값 F:H(날짜·시스템 이름이 잘리지 않게) · 이름표 I:J · 값 K
        private static void Fact(IXLWorksheet sheet, int row, string label1, string value1, string label2, string value2, string label3, string value3)
        {
            Label(Merge(sheet, row, 2, row, 3, label1));
            sheet.Cell(row, 4).Value = value1;
            sheet.Cell(row, 5).Value = label2;
            Label(sheet.Cell(row, 5));
            Merge(sheet, row, 6, row, 8, value2);
            Label(Merge(sheet, row, 9, row, 10, label3));
            sheet.Cell(row, 11).Value = value3;
            foreach (var column in new[] { 4, 6, 11 })
                sheet.Cell(row, column).Style.Alignment.Indent = 1;
        }

        // 인덱스 줄: B, C, D:F, G, H:J, K
        private static void Row(IXLWorksheet sheet, int row, string[] values)
        {
            sheet.Cell(row, 2).Value = values[0];
            sheet.Cell(row, 3).Value = values[1];
            Merge(sheet, row, 4, row, 6, values[2]);
            sheet.Cell(row, 7).Value = values[3];
            Merge(sheet, row, 8, row, 10, values[4]);
            sheet.Cell(row, 11).Value = values[5];
        }

        // 제약 조건 줄: B, C, D, E:F, G:J, K
        private static void ConstraintRow(IXLWorksheet sheet, int row, string[] values)
        {
            sheet.Cell(row, 2).Value = values[0];
            sheet.Cell(row, 3).Value = values[1];
            sheet.Cell(row, 4).Value = values[2];
            Merge(sheet, row, 5, row, 6, values[3]);
            Merge(sheet, row, 7, row, 10, values[4]);
            sheet.Cell(row, 11).Value = values[5];
        }

        private static void Empty(IXLWorksheet sheet, int row, string text)
        {
            var cell = Merge(sheet, row, 2, row, 11, text);
            cell.Style.Font.FontColor = Muted;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            Box(sheet.Range(row, 2, row, 11));
        }

        // ================= 안내(결과에서 빠짐) =================

        /// <summary>문법·변수 안내 시트(명세서형 템플릿도 함께 쓴다).</summary>
        internal static void Help(IXLWorksheet sheet)
        {
            sheet.Column(1).Width = 2;
            sheet.Column(2).Width = 38;
            sheet.Column(3).Width = 60;
            sheet.Column(4).Width = 28;
            var title = sheet.Cell(1, 2);
            title.Value = "템플릿 문법과 변수 (이 시트는 이름이 '_'로 시작해 산출물에서 빠집니다)";
            title.Style.Font.FontSize = 14;
            title.Style.Font.Bold = true;
            title.Style.Font.FontColor = Navy;

            var rules = new[,]
            {
                { "${doc.title} · $doc.title", "셀 안 어디에나 쓸 수 있는 값. 이어지는 글자와 붙으면 ${…}로 감싼다", "${table.name}_백업" },
                { "$!{col.comment}", "값이 없으면 빈 칸(이 템플릿의 변수는 값이 없으면 빈 글자라 $! 없이도 된다)", "" },
                { "셀 = 참조 하나", "\"${col.no}\"처럼 셀 전체가 참조 하나면 숫자는 숫자 셀로 들어간다", "${table.numRows}" },
                { "#foreach($x in $list) / #end", "그 셀 하나만 있는 줄 = 줄 지시문. 사이 줄을 항목마다 되풀이한다. 지시문 줄은 결과에 남지 않는다", "#foreach($col in $table.columns)" },
                { "#if(식) / #elseif(식) / #else / #end", "줄 지시문: 조건이 참일 때만 사이 줄을 넣는다. 빈 목록·빈 글자·0·null은 거짓", "#if(!$table.indexes)" },
                { "#set($x = 식)", "줄 지시문 또는 셀 안에서 변수를 정한다", "#set($n = $table.columnCount)" },
                { "#sheet($table in $tables)", "시트 맨 위 줄 지시문: 항목마다 이 시트를 복사한다. 탭 이름도 템플릿(예: ${table.sheetName})", "" },
                { "셀 안 #if … #{else} … #end", "한 셀 안에서도 쓸 수 있다. 글자가 바로 이어지면 #{else}·#{end}처럼 중괄호로 감싼다", "#if($col.isPk)PK#{else}-#end" },
                { "식", "== != < <= > >= && || ! and or not, + - * / %, \"글자\", 숫자, true·false·null", "$col.no > 1 && $col.pk == \"Y\"" },
                { "$foreach.count", "#foreach 안 순번(1부터). index(0부터)·hasNext·first·last도 있다", "" },
                { "수식", "수식 안의 ${…}도 채운다(예: HYPERLINK로 테이블 시트 링크). $A$1 같은 절대 참조는 그대로 둔다", "=HYPERLINK(\"#'${t.sheetName}'!A1\",\"${t.name}\")" },
                { "\\$ · \\#", "$·# 글자 그대로", "" },
                { "## 주석 · #* 주석 *#", "결과에 남지 않는다", "" }
            };
            Section(sheet.Cell(3, 2), "문법");
            sheet.Cell(4, 2).Value = "쓰는 법";
            sheet.Cell(4, 3).Value = "뜻";
            sheet.Cell(4, 4).Value = "예";
            Header(sheet.Range(4, 2, 4, 4));
            for (var i = 0; i < rules.GetLength(0); i++)
            {
                for (var j = 0; j < 3; j++)
                    sheet.Cell(5 + i, 2 + j).SetValue(rules[i, j]);
            }
            var last = 4 + rules.GetLength(0);
            Box(sheet.Range(5, 2, last, 4));
            sheet.Range(5, 2, last, 4).Style.Alignment.WrapText = true;
            sheet.Range(5, 2, last, 4).Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;

            var start = last + 2;
            Section(sheet.Cell(start, 2), "변수");
            sheet.Cell(start + 1, 2).Value = "변수";
            sheet.Cell(start + 1, 3).Value = "뜻";
            sheet.Cell(start + 1, 4).Value = "예";
            Header(sheet.Range(start + 1, 2, start + 1, 4));
            var row = start + 2;
            foreach (var variable in TableSpecModel.Variables)
            {
                sheet.Cell(row, 2).SetValue(variable.Name);
                sheet.Cell(row, 3).SetValue(variable.Description);
                sheet.Cell(row, 4).SetValue(variable.Example);
                row++;
            }
            Box(sheet.Range(start + 2, 2, row - 1, 4));
            sheet.Range(start + 2, 2, row - 1, 4).Style.Alignment.WrapText = true;
        }

        // ================= 모양 도우미 =================

        internal static IXLCell Merge(IXLWorksheet sheet, int row1, int column1, int row2, int column2, string value)
        {
            var range = sheet.Range(row1, column1, row2, column2);
            range.Merge();
            var cell = sheet.Cell(row1, column1);
            cell.Value = value;
            return cell;
        }

        internal static void Header(IXLRange range)
        {
            range.Style.Fill.BackgroundColor = HeaderFill;
            range.Style.Font.Bold = true;
            range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            Box(range);
        }

        internal static void Label(IXLCell cell)
        {
            var range = cell.MergedRange() ?? cell.AsRange();
            range.Style.Fill.BackgroundColor = LabelFill;
            range.Style.Font.Bold = true;
            range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }

        internal static void Section(IXLCell cell, string text)
        {
            cell.Value = text;
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontSize = 11;
            cell.Style.Font.FontColor = Navy;
        }

        internal static void Box(IXLRange range)
        {
            range.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            range.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            range.Style.Border.OutsideBorderColor = LineColor;
            range.Style.Border.InsideBorderColor = LineColor;
        }

        // 지시문 줄: 결과에는 남지 않지만 템플릿에서 눈에 띄게
        private static void Directive(IXLWorksheet sheet, int row, string text)
        {
            Directive(sheet, row, text, sheet.Name == ListSheet ? 8 : 11);
        }

        /// <summary>지시문 줄(A열에 지시문, lastColumn까지 연노랑 바탕). GCM 양식도 쓴다.</summary>
        internal static void Directive(IXLWorksheet sheet, int row, string text, int lastColumn)
        {
            var cell = sheet.Cell(row, 1);
            cell.Value = text;
            cell.Style.Font.FontColor = DirectiveText;
            cell.Style.Font.Italic = true;
            cell.Style.Font.FontSize = 9;
            sheet.Range(row, 1, row, lastColumn).Style.Fill.BackgroundColor = DirectiveFill;
        }
    }
}

using System.IO;
using ClosedXML.Excel;

namespace MyPlugin
{
    /// <summary>
    /// 기본 GCM(공통 코드) 정의서 템플릿(.xlsx)을 코드로 만든다. 글꼴·색·지시문 줄 표시는 테이블 정의서 기본 양식(TableSpecTemplate)과 같다.
    /// 기본(Create):
    /// - 표지: ${doc.title}·프로젝트·시스템/모듈·대상 DB·DBMS·그룹 수·코드 수·버전·작성자·작성일·설명, 출처(SQL 이름·조회 시각·행 수·결과 열·SQL 본문), 개정 이력.
    /// - 목록: #foreach($g in $groups)로 그룹마다 한 줄(테이블명는 그 그룹 시트로 가는 링크).
    /// - ${group.sheetName}: 맨 위 #sheet($group in $groups)로 그룹마다 시트 하나 — 그룹 머리, 코드 표(#foreach($code in $group.codes)).
    /// - _변수 설명: 문법·변수 안내(이름이 '_'로 시작해 결과에서 빠진다).
    /// 단일 시트형(CreateFlat): 표지 + 코드(#foreach($code in $codes)로 모든 코드를 한 표에) + _변수 설명.
    /// 사용자는 [복사해서 고치기]로 이 파일을 받아 고친 뒤 양식 폴더(templates\gcm)에 두고 고를 수 있다.
    /// </summary>
    internal static class CodeSpecTemplate
    {
        public const string CoverSheet = "표지";
        public const string ListSheet = "목록";
        public const string GroupSheet = "${group.sheetName}";
        public const string CodesSheet = "코드";
        public const string HelpSheet = TableSpecTemplate.HelpSheet;

        private static readonly XLColor LinkColor = XLColor.FromHtml("#0563C1");

        /// <summary>기본 양식: 표지 · 목록 · 그룹별 시트 · _변수 설명.</summary>
        public static byte[] Create()
        {
            using (var workbook = new XLWorkbook())
            using (var stream = new MemoryStream())
            {
                workbook.Style.Font.FontName = "맑은 고딕";
                workbook.Style.Font.FontSize = 10;
                Cover(workbook.AddWorksheet(CoverSheet));
                List(workbook.AddWorksheet(ListSheet));
                Group(workbook.AddWorksheet(GroupSheet));
                Help(workbook.AddWorksheet(HelpSheet));
                workbook.Worksheet(CoverSheet).SetTabActive();
                workbook.SaveAs(stream);
                return stream.ToArray();
            }
        }

        /// <summary>단일 시트형 양식: 표지 · 코드(모든 코드 한 표) · _변수 설명.</summary>
        public static byte[] CreateFlat()
        {
            using (var workbook = new XLWorkbook())
            using (var stream = new MemoryStream())
            {
                workbook.Style.Font.FontName = "맑은 고딕";
                workbook.Style.Font.FontSize = 10;
                Cover(workbook.AddWorksheet(CoverSheet));
                Flat(workbook.AddWorksheet(CodesSheet));
                Help(workbook.AddWorksheet(HelpSheet));
                workbook.Worksheet(CoverSheet).SetTabActive();
                workbook.SaveAs(stream);
                return stream.ToArray();
            }
        }

        // ================= 표지(두 양식이 같이 쓴다) =================

        private static void Cover(IXLWorksheet sheet)
        {
            sheet.ShowGridLines = false;
            sheet.Column(1).Width = 3;
            sheet.Columns(2, 9).Width = 12;
            sheet.Column(10).Width = 3;

            var title = TableSpecTemplate.Merge(sheet, 6, 2, 8, 9, "${doc.title}");
            title.Style.Font.FontSize = 28;
            title.Style.Font.Bold = true;
            title.Style.Font.FontColor = TableSpecTemplate.Navy;
            title.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            title.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            title.Style.Border.BottomBorder = XLBorderStyleValues.Medium;
            title.Style.Border.BottomBorderColor = TableSpecTemplate.Navy;

            var project = TableSpecTemplate.Merge(sheet, 9, 2, 9, 9, "${doc.project}");
            project.Style.Font.FontSize = 14;
            project.Style.Font.FontColor = TableSpecTemplate.Muted;
            project.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            sheet.Row(9).Height = 24;

            // 문서 정보: 이름표 C:D · 값 E:H. 그룹 수·코드 수는 참조 하나만 두어 숫자 셀이 된다
            var facts = new[,]
            {
                { "문서 제목", "${doc.title}" },
                { "프로젝트", "${doc.project}" },
                { "시스템/모듈", "${doc.system}" },
                { "대상 DB", "${db.name} (${db.address})" },
                { "DBMS", "${db.version}" },
                { "그룹 수", "${doc.groupCount}" },
                { "코드 수", "${doc.codeCount}" },
                { "버전", "${doc.version}" },
                { "작성자", "${doc.author}" },
                { "작성일", "${doc.date}" },
                { "설명", "${doc.description}" }
            };
            var row = 12;
            for (var i = 0; i < facts.GetLength(0); i++, row++)
                Fact(sheet, row, facts[i, 0], facts[i, 1]);
            TableSpecTemplate.Box(sheet.Range(12, 3, row - 1, 8));

            // 출처: 어떤 SQL을 언제 실행해 만든 문서인지
            row++;
            TableSpecTemplate.Section(sheet.Cell(row, 3), "출처");
            row++;
            var sourceStart = row;
            Fact(sheet, row++, "SQL 이름", "${source.name}");
            Fact(sheet, row++, "조회 시각", "${source.fetchedAt}");
            Fact(sheet, row++, "행 수", "${source.rowCount}");
            Fact(sheet, row, "결과 열", "${source.columns}");
            sheet.Cell(row, 5).Style.Alignment.WrapText = true;
            sheet.Row(row).Height = 32;
            row++;
            // SQL 본문: 여러 줄 병합 칸(병합 칸은 엑셀이 높이를 맞추지 않으므로 줄 높이를 정해 둔다)
            var sqlLast = row + 9;
            var sqlLabel = TableSpecTemplate.Merge(sheet, row, 3, sqlLast, 4, "SQL 본문");
            TableSpecTemplate.Label(sqlLabel);
            sqlLabel.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
            var sql = TableSpecTemplate.Merge(sheet, row, 5, sqlLast, 8, "${source.sql}");
            sql.Style.Alignment.WrapText = true;
            sql.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
            sql.Style.Alignment.Indent = 1;
            sql.Style.Font.FontName = "Consolas";
            sql.Style.Font.FontSize = 9;
            for (var r = row; r <= sqlLast; r++)
                sheet.Row(r).Height = 16;
            TableSpecTemplate.Box(sheet.Range(sourceStart, 3, sqlLast, 8));
            row = sqlLast + 2;

            // 개정 이력
            TableSpecTemplate.Section(sheet.Cell(row, 3), "개정 이력");
            row++;
            var history = new[] { "버전", "일자", "작성자", "내용" };
            var columns = new[] { 3, 4, 5, 6 };
            for (var i = 0; i < history.Length; i++)
            {
                var cell = i < 3 ? sheet.Cell(row, columns[i]) : TableSpecTemplate.Merge(sheet, row, 6, row, 8, history[i]);
                cell.Value = history[i];
                TableSpecTemplate.Header(i < 3 ? cell.AsRange() : sheet.Range(row, 6, row, 8));
            }
            var historyHeader = row;
            row++;
            sheet.Cell(row, 3).Value = "${doc.version}";
            sheet.Cell(row, 4).Value = "${doc.date}";
            sheet.Cell(row, 5).Value = "${doc.author}";
            TableSpecTemplate.Merge(sheet, row, 6, row, 8, "최초 작성");
            for (var r = row + 1; r <= row + 3; r++)
                sheet.Range(r, 6, r, 8).Merge();
            TableSpecTemplate.Box(sheet.Range(historyHeader, 3, row + 3, 8));
            sheet.Range(row, 3, row + 3, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            row += 5;

            sheet.Cell(row, 2).Value = "${doc.generatedAt} · DB Helper로 만듦";
            sheet.Cell(row, 2).Style.Font.FontColor = TableSpecTemplate.Muted;
            sheet.Cell(row, 2).Style.Font.FontSize = 9;

            sheet.PageSetup.PaperSize = XLPaperSize.A4Paper;
            sheet.PageSetup.PageOrientation = XLPageOrientation.Portrait;
            sheet.PageSetup.CenterHorizontally = true;
            sheet.PageSetup.FitToPages(1, 1);
        }

        // 표지 한 줄: 이름표 C:D · 값 E:H
        private static void Fact(IXLWorksheet sheet, int row, string label, string value)
        {
            TableSpecTemplate.Label(TableSpecTemplate.Merge(sheet, row, 3, row, 4, label));
            var cell = TableSpecTemplate.Merge(sheet, row, 5, row, 8, value);
            cell.Style.Alignment.Indent = 1;
            cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            sheet.Row(row).Height = 20;
        }

        // ================= 목록 =================

        private static void List(IXLWorksheet sheet)
        {
            sheet.ShowGridLines = false;
            // B No · C 테이블명 · D 그룹명 · E 설명 · F 코드 수
            Widths(sheet, 2, 6, 28, 28, 50, 9);

            Title(sheet, "코드 그룹 목록");
            sheet.Cell(2, 2).Value = "${db.name} · 그룹 ${doc.groupCount}개 · 코드 ${doc.codeCount}개";
            sheet.Cell(2, 2).Style.Font.FontColor = TableSpecTemplate.Muted;

            var headers = new[] { "No", "테이블명", "그룹명", "설명", "코드 수" };
            for (var i = 0; i < headers.Length; i++)
                sheet.Cell(4, 2 + i).Value = headers[i];
            TableSpecTemplate.Header(sheet.Range(4, 2, 4, 6));

            TableSpecTemplate.Directive(sheet, 5, "#foreach($g in $groups)", 6);
            sheet.Cell(6, 2).Value = "${g.no}";
            // 테이블명는 그 그룹 시트로 가는 링크(시트 이름은 $g.sheetName — 31자 안, 겹치지 않게 고친 값)
            Link(sheet.Cell(6, 3), "HYPERLINK(\"#'${g.sheetName}'!A1\",\"${g.id}\")");
            sheet.Cell(6, 4).Value = "${g.name}";
            sheet.Cell(6, 5).Value = "${g.description}";
            sheet.Cell(6, 6).Value = "${g.codeCount}";
            TableSpecTemplate.Box(sheet.Range(6, 2, 6, 6));
            sheet.Range(6, 2, 6, 6).Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
            sheet.Cell(6, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            sheet.Cell(6, 6).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            sheet.Cell(6, 5).Style.Alignment.WrapText = true;
            TableSpecTemplate.Directive(sheet, 7, "#end", 6);

            TableSpecTemplate.Directive(sheet, 8, "#if(!$groups)", 6);
            Empty(sheet, 9, 6, "(그룹이 없습니다)");
            TableSpecTemplate.Directive(sheet, 10, "#end", 6);

            sheet.SheetView.FreezeRows(4);
            sheet.PageSetup.PaperSize = XLPaperSize.A4Paper;
            sheet.PageSetup.PageOrientation = XLPageOrientation.Portrait;
            sheet.PageSetup.FitToPages(1, 0);
            sheet.PageSetup.SetRowsToRepeatAtTop(4, 4);
        }

        // ================= 그룹 한 개 =================

        private static void Group(IXLWorksheet sheet)
        {
            sheet.ShowGridLines = false;
            // B No · C 코드 · D 코드명 · E 설명 · F 정렬 · G 사용 · H 추가 속성
            Widths(sheet, 2, 5, 16, 24, 40, 6, 6, 34);

            TableSpecTemplate.Directive(sheet, 1, "#sheet($group in $groups)", 8);

            var back = sheet.Cell(2, 8);
            Link(back, "HYPERLINK(\"#'" + ListSheet + "'!A1\",\"← 목록\")");
            back.Style.Font.Underline = XLFontUnderlineValues.None;
            back.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

            var id = TableSpecTemplate.Merge(sheet, 2, 2, 2, 7, "${group.id}");
            id.Style.Font.FontSize = 16;
            id.Style.Font.Bold = true;
            id.Style.Font.FontColor = TableSpecTemplate.Navy;
            sheet.Row(2).Height = 26;
            var name = TableSpecTemplate.Merge(sheet, 3, 2, 3, 8, "${group.name}");
            name.Style.Font.FontColor = TableSpecTemplate.Muted;
            sheet.Range(3, 2, 3, 8).Style.Border.BottomBorder = XLBorderStyleValues.Medium;
            sheet.Range(3, 2, 3, 8).Style.Border.BottomBorderColor = TableSpecTemplate.Navy;

            // 그룹 머리: 이름표 B:C · 값 D:H
            var facts = new[,]
            {
                { "테이블명", "${group.id}" },
                { "명칭", "${group.name}" },
                { "설명", "${group.description}" },
                { "코드 수", "${group.codeCount}" }
            };
            for (var i = 0; i < facts.GetLength(0); i++)
            {
                var row = 5 + i;
                TableSpecTemplate.Label(TableSpecTemplate.Merge(sheet, row, 2, row, 3, facts[i, 0]));
                var value = TableSpecTemplate.Merge(sheet, row, 4, row, 8, facts[i, 1]);
                value.Style.Alignment.Indent = 1;
                value.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
            }
            TableSpecTemplate.Box(sheet.Range(5, 2, 8, 8));

            // 코드 표
            TableSpecTemplate.Section(sheet.Cell(10, 2), "코드 (${group.codeCount})");
            // 추가 속성 머리글에는 이 그룹의 속성 이름을 적는다(JSON 열을 펼치면 그룹마다 다르다)
            var headers = new[] { "No", "코드", "코드명", "설명", "정렬", "사용", "#if($group.attrNames)추가 속성 (${group.attrNamesText})#{else}추가 속성#end" };
            for (var i = 0; i < headers.Length; i++)
                sheet.Cell(11, 2 + i).Value = headers[i];
            TableSpecTemplate.Header(sheet.Range(11, 2, 11, 8));
            TableSpecTemplate.Directive(sheet, 12, "#foreach($code in $group.codes)", 8);
            var values = new[] { "${code.no}", "${code.code}", "${code.name}", "${code.description}", "${code.sortNo}", "${code.useYn}", "${code.attrText}" };
            for (var i = 0; i < values.Length; i++)
                sheet.Cell(13, 2 + i).Value = values[i];
            CodeRow(sheet, 13, 8, new[] { 2, 6, 7 }, new[] { 5, 8 });
            TableSpecTemplate.Directive(sheet, 14, "#end", 8);
            TableSpecTemplate.Directive(sheet, 15, "#if(!$group.codes)", 8);
            Empty(sheet, 16, 8, "(코드가 없습니다)");
            TableSpecTemplate.Directive(sheet, 17, "#end", 8);

            sheet.PageSetup.PaperSize = XLPaperSize.A4Paper;
            sheet.PageSetup.PageOrientation = XLPageOrientation.Portrait;
            sheet.PageSetup.FitToPages(1, 0);
        }

        // ================= 단일 시트형: 모든 코드 한 표 =================

        private static void Flat(IXLWorksheet sheet)
        {
            sheet.ShowGridLines = false;
            // B No · C 테이블명 · D 그룹명 · E 코드 · F 코드명 · G 설명 · H 정렬 · I 사용 · J 추가 속성
            Widths(sheet, 2, 6, 20, 20, 14, 22, 36, 6, 6, 30);

            Title(sheet, "코드 목록");
            sheet.Cell(2, 2).Value = "${db.name} · 그룹 ${doc.groupCount}개 · 코드 ${doc.codeCount}개 · ${source.fetchedAt} 조회";
            sheet.Cell(2, 2).Style.Font.FontColor = TableSpecTemplate.Muted;

            var headers = new[] { "No", "테이블명", "그룹명", "코드", "코드명", "설명", "정렬", "사용", "추가 속성" };
            for (var i = 0; i < headers.Length; i++)
                sheet.Cell(4, 2 + i).Value = headers[i];
            TableSpecTemplate.Header(sheet.Range(4, 2, 4, 10));

            TableSpecTemplate.Directive(sheet, 5, "#foreach($code in $codes)", 10);
            var values = new[] { "${code.no}", "${code.group}", "${code.groupName}", "${code.code}", "${code.name}", "${code.description}", "${code.sortNo}", "${code.useYn}", "${code.attrText}" };
            for (var i = 0; i < values.Length; i++)
                sheet.Cell(6, 2 + i).Value = values[i];
            CodeRow(sheet, 6, 10, new[] { 2, 8, 9 }, new[] { 7, 10 });
            TableSpecTemplate.Directive(sheet, 7, "#end", 10);
            TableSpecTemplate.Directive(sheet, 8, "#if(!$codes)", 10);
            Empty(sheet, 9, 10, "(코드가 없습니다)");
            TableSpecTemplate.Directive(sheet, 10, "#end", 10);

            sheet.SheetView.FreezeRows(4);
            sheet.PageSetup.PaperSize = XLPaperSize.A4Paper;
            sheet.PageSetup.PageOrientation = XLPageOrientation.Landscape;
            sheet.PageSetup.FitToPages(1, 0);
            sheet.PageSetup.SetRowsToRepeatAtTop(4, 4);
        }

        // 반복되는 코드 줄 모양: 테두리·위 정렬, 가운데 맞춤 열, 줄바꿈 열(줄 높이는 정하지 않는다 — 엑셀이 내용에 맞춘다)
        private static void CodeRow(IXLWorksheet sheet, int row, int lastColumn, int[] centered, int[] wrapped)
        {
            TableSpecTemplate.Box(sheet.Range(row, 2, row, lastColumn));
            sheet.Range(row, 2, row, lastColumn).Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
            foreach (var column in centered)
                sheet.Cell(row, column).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            foreach (var column in wrapped)
                sheet.Cell(row, column).Style.Alignment.WrapText = true;
        }

        // ================= 안내(결과에서 빠짐) =================

        /// <summary>문법·변수 안내 시트. 문법 표는 테이블 정의서 양식의 것과 같고(예만 GCM 변수), 변수 표는 CodeSpecModel.Variables.</summary>
        private static void Help(IXLWorksheet sheet)
        {
            sheet.Column(1).Width = 2;
            sheet.Column(2).Width = 38;
            sheet.Column(3).Width = 60;
            sheet.Column(4).Width = 28;
            var title = sheet.Cell(1, 2);
            title.Value = "템플릿 문법과 변수 (이 시트는 이름이 '_'로 시작해 산출물에서 빠집니다)";
            title.Style.Font.FontSize = 14;
            title.Style.Font.Bold = true;
            title.Style.Font.FontColor = TableSpecTemplate.Navy;

            var rules = new[,]
            {
                { "${doc.title} · $doc.title", "셀 안 어디에나 쓸 수 있는 값. 이어지는 글자와 붙으면 ${…}로 감싼다", "${group.id}_코드" },
                { "$!{code.description}", "값이 없으면 빈 칸(이 템플릿의 변수는 값이 없으면 빈 글자라 $! 없이도 된다)", "" },
                { "셀 = 참조 하나", "\"${code.no}\"처럼 셀 전체가 참조 하나면 숫자는 숫자 셀로 들어간다", "${group.codeCount}" },
                { "#foreach($x in $list) / #end", "그 셀 하나만 있는 줄 = 줄 지시문. 사이 줄을 항목마다 되풀이한다. 지시문 줄은 결과에 남지 않는다", "#foreach($code in $group.codes)" },
                { "#if(식) / #elseif(식) / #else / #end", "줄 지시문: 조건이 참일 때만 사이 줄을 넣는다. 빈 목록·빈 글자·0·null은 거짓", "#if(!$group.codes)" },
                { "#set($x = 식)", "줄 지시문 또는 셀 안에서 변수를 정한다", "#set($n = $group.codeCount)" },
                { "#sheet($group in $groups)", "시트 맨 위 줄 지시문: 항목마다 이 시트를 복사한다. 탭 이름도 템플릿(예: ${group.sheetName})", "" },
                { "셀 안 #if … #{else} … #end", "한 셀 안에서도 쓸 수 있다. 글자가 바로 이어지면 #{else}·#{end}처럼 중괄호로 감싼다", "#if($code.isUsed)사용#{else}미사용#end" },
                { "식", "== != < <= > >= && || ! and or not, + - * / %, \"글자\", 숫자, true·false·null", "$code.no > 1 && $code.useYn == \"Y\"" },
                { "$foreach.count", "#foreach 안 순번(1부터). index(0부터)·hasNext·first·last도 있다", "" },
                { "수식", "수식 안의 ${…}도 채운다(예: HYPERLINK로 그룹 시트 링크). $A$1 같은 절대 참조는 그대로 둔다", "=HYPERLINK(\"#'${g.sheetName}'!A1\",\"${g.id}\")" },
                { "\\$ · \\#", "$·# 글자 그대로", "" },
                { "## 주석 · #* 주석 *#", "결과에 남지 않는다", "" }
            };
            TableSpecTemplate.Section(sheet.Cell(3, 2), "문법");
            sheet.Cell(4, 2).Value = "쓰는 법";
            sheet.Cell(4, 3).Value = "뜻";
            sheet.Cell(4, 4).Value = "예";
            TableSpecTemplate.Header(sheet.Range(4, 2, 4, 4));
            for (var i = 0; i < rules.GetLength(0); i++)
            {
                for (var j = 0; j < 3; j++)
                    sheet.Cell(5 + i, 2 + j).SetValue(rules[i, j]);
            }
            var last = 4 + rules.GetLength(0);
            TableSpecTemplate.Box(sheet.Range(5, 2, last, 4));
            sheet.Range(5, 2, last, 4).Style.Alignment.WrapText = true;
            sheet.Range(5, 2, last, 4).Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;

            var start = last + 2;
            TableSpecTemplate.Section(sheet.Cell(start, 2), "변수");
            sheet.Cell(start + 1, 2).Value = "변수";
            sheet.Cell(start + 1, 3).Value = "뜻";
            sheet.Cell(start + 1, 4).Value = "예";
            TableSpecTemplate.Header(sheet.Range(start + 1, 2, start + 1, 4));
            var row = start + 2;
            foreach (var variable in CodeSpecModel.Variables)
            {
                sheet.Cell(row, 2).SetValue(variable.Name);
                sheet.Cell(row, 3).SetValue(variable.Description);
                sheet.Cell(row, 4).SetValue(variable.Example);
                row++;
            }
            TableSpecTemplate.Box(sheet.Range(start + 2, 2, row - 1, 4));
            sheet.Range(start + 2, 2, row - 1, 4).Style.Alignment.WrapText = true;
        }

        // ================= 모양 도우미 =================

        private static void Widths(IXLWorksheet sheet, params double[] widths)
        {
            for (var i = 0; i < widths.Length; i++)
                sheet.Column(i + 1).Width = widths[i];
        }

        private static void Title(IXLWorksheet sheet, string text)
        {
            var title = sheet.Cell(1, 2);
            title.Value = text;
            title.Style.Font.FontSize = 16;
            title.Style.Font.Bold = true;
            title.Style.Font.FontColor = TableSpecTemplate.Navy;
            sheet.Row(1).Height = 28;
        }

        private static void Link(IXLCell cell, string formula)
        {
            cell.FormulaA1 = formula;
            cell.Style.Font.FontColor = LinkColor;
            cell.Style.Font.Underline = XLFontUnderlineValues.Single;
        }

        private static void Empty(IXLWorksheet sheet, int row, int lastColumn, string text)
        {
            var cell = TableSpecTemplate.Merge(sheet, row, 2, row, lastColumn, text);
            cell.Style.Font.FontColor = TableSpecTemplate.Muted;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            TableSpecTemplate.Box(sheet.Range(row, 2, row, lastColumn));
        }
    }
}

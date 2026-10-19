using System.IO;
using ClosedXML.Excel;

namespace MyPlugin
{
    /// <summary>
    /// 일일 점검 보고서 기본 양식(.xlsx)을 코드로 만든다(변수: <see cref="CheckReportModel"/>). 시트:
    /// - 요약: 점검일·점검자·DB 수·전체 요약, DB별 상태 수(가장 나쁜 상태로 줄 색), 주의 이상 항목(DB · 항목 · 상태 · 값 · 직전 대비).
    /// - ${db.sheetName}: #sheet로 DB마다 — 대상·점검 시각·요약, 점검 항목 표(상태별 줄 색), 발견 사항(항목마다 열 제목 + 행, 최대 100행).
    /// - _변수 설명: 문법·변수 안내(결과에서 빠짐).
    /// 줄 색은 #if($check.status == "crit") … #elseif … 로 상태마다 다른 줄을 고른다(조건부 서식은 줄 이동을 따라가지 않으므로).
    /// </summary>
    internal static class CheckReportTemplate
    {
        public const string SummarySheet = "요약";
        public const string DbSheet = "${db.sheetName}";
        public const string HelpSheet = "_변수 설명";
        public const string FileName = "일일 점검 양식.xlsx";

        private static readonly XLColor HeaderFill = XLColor.FromHtml("#3F4759");
        private static readonly XLColor LabelFill = XLColor.FromHtml("#F2F3F5");
        private static readonly XLColor SectionFill = XLColor.FromHtml("#EEF0FF");
        private static readonly XLColor SubFill = XLColor.FromHtml("#F7F7F9");
        private static readonly XLColor LineColor = XLColor.FromHtml("#C9CDD4");
        private static readonly XLColor LinkColor = XLColor.FromHtml("#2F5BD1");
        private static readonly XLColor NoteColor = XLColor.FromHtml("#8A8F98");

        // 상태별 줄 바탕·상태 글자색
        private static readonly XLColor CritFill = XLColor.FromHtml("#FBE3E4");
        private static readonly XLColor CritText = XLColor.FromHtml("#B3261E");
        private static readonly XLColor WarnFill = XLColor.FromHtml("#FFF4D6");
        private static readonly XLColor WarnText = XLColor.FromHtml("#8A5A00");
        private static readonly XLColor ErrorFill = XLColor.FromHtml("#FDEBDD");
        private static readonly XLColor ErrorText = XLColor.FromHtml("#B4500F");
        private static readonly XLColor SkipText = XLColor.FromHtml("#7A7F87");
        private static readonly XLColor InfoText = XLColor.FromHtml("#2F5BD1");
        private static readonly XLColor OkText = XLColor.FromHtml("#2E7D32");

        public static byte[] Create()
        {
            using (var workbook = new XLWorkbook())
            using (var stream = new MemoryStream())
            {
                workbook.Style.Font.FontName = "맑은 고딕";
                workbook.Style.Font.FontSize = 10;
                Summary(workbook.AddWorksheet(SummarySheet));
                Db(workbook.AddWorksheet(DbSheet));
                TableSpecTemplate.Help(workbook.AddWorksheet(HelpSheet), CheckReportModel.Variables);
                workbook.Worksheet(SummarySheet).SetTabActive();
                workbook.SaveAs(stream);
                return stream.ToArray();
            }
        }

        public static void Save(string path)
        {
            File.WriteAllBytes(path, Create());
        }

        // ================= 요약 =================

        // B No · C DB · D 대상 · E 위험 · F 오류 · G 주의 · H 건너뜀 · I 정상 · J 점검 시각 · K 시트
        private static void Summary(IXLWorksheet sheet)
        {
            Widths(sheet, 3, 5, 18, 34, 7, 7, 7, 8, 10, 17, 8);
            Title(sheet, 2, 2, 11, "${report.title}");
            var facts = new[,]
            {
                { "점검일", "${report.date}" },
                { "점검자", "${report.author}" },
                { "대상 DB", "${report.dbCount}개" },
                { "전체 요약", "${report.summary}" }
            };
            for (var i = 0; i < facts.GetLength(0); i++)
            {
                var row = 4 + i;
                Label(Merge(sheet, row, 2, row, 3, ""), facts[i, 0]);
                Merge(sheet, row, 4, row, 11, facts[i, 1]);
            }
            Box(sheet.Range(4, 2, 7, 11));

            Section(sheet, 9, 2, 11, "DB별 결과");
            Header(sheet, 10, 2, "No", "DB", "대상", "위험", "오류", "주의", "건너뜀", "정상", "점검 시각", "시트");
            Directive(sheet, 11, 11, "#foreach($db in $dbs)");
            Directive(sheet, 12, 11, "#if($db.error)");
            DbRow(sheet, 13, "error", "${db.error}");
            Directive(sheet, 14, 11, "#elseif($db.worstCode == \"crit\")");
            DbRow(sheet, 15, "crit", null);
            Directive(sheet, 16, 11, "#elseif($db.worstCode == \"error\")");
            DbRow(sheet, 17, "error", null);
            Directive(sheet, 18, 11, "#elseif($db.worstCode == \"warn\")");
            DbRow(sheet, 19, "warn", null);
            Directive(sheet, 20, 11, "#else");
            DbRow(sheet, 21, "ok", null);
            Directive(sheet, 22, 11, "#end");
            Directive(sheet, 23, 11, "#end");

            Section(sheet, 25, 2, 11, "주의 이상 항목");
            IssueRow(sheet, 26, "DB", "항목", "상태", "값", "직전 대비", "시트");
            HeaderStyle(sheet.Range(26, 3, 26, 11));
            Directive(sheet, 27, 11, "#foreach($db in $dbs)");
            Directive(sheet, 28, 11, "#foreach($check in $db.issues)");
            Directive(sheet, 29, 11, "#if($check.status == \"crit\")");
            IssueValues(sheet, 30, "crit");
            Directive(sheet, 31, 11, "#elseif($check.status == \"error\")");
            IssueValues(sheet, 32, "error");
            Directive(sheet, 33, 11, "#else");
            IssueValues(sheet, 34, "warn");
            Directive(sheet, 35, 11, "#end");
            Directive(sheet, 36, 11, "#end");
            Directive(sheet, 37, 11, "#end");
            Directive(sheet, 38, 11, "#if(!$report.issueCount)");
            Empty(sheet, 39, 3, 11, "(주의 이상 항목 없음)");
            Directive(sheet, 40, 11, "#end");

            Note(sheet, 42, 2, 11, "* ${report.generatedAt}에 DB Helper가 만들었습니다. 사용률은 최대 크기(AUTOEXTEND) 기준입니다. 판정 기준·조치는 DB 시트에 있습니다.");
            Note(sheet, 43, 2, 11, "* 발견 행(객체·사용자 이름 등)은 이 파일에만 있고, DB Helper의 점검 이력에는 상태·값·행 수만 남습니다.");

            sheet.PageSetup.PaperSize = XLPaperSize.A4Paper;
            sheet.PageSetup.PageOrientation = XLPageOrientation.Landscape;
            sheet.PageSetup.FitToPages(1, 0);
        }

        private static void DbRow(IXLWorksheet sheet, int row, string status, string error)
        {
            sheet.Cell(row, 2).Value = "${db.no}";
            sheet.Cell(row, 3).Value = "${db.name}";
            sheet.Cell(row, 4).Value = "#if($db.label)${db.label} · #end${db.address}";
            if (error != null)
            {
                Merge(sheet, row, 5, row, 9, "접속 실패 — " + error);
            }
            else
            {
                sheet.Cell(row, 5).Value = "${db.critCount}";
                sheet.Cell(row, 6).Value = "${db.errorCount}";
                sheet.Cell(row, 7).Value = "${db.warnCount}";
                sheet.Cell(row, 8).Value = "${db.skipCount}";
                sheet.Cell(row, 9).Value = "${db.okCount}";
                Center(sheet, row, 5, 6, 7, 8, 9);
            }
            sheet.Cell(row, 10).Value = "${db.startedAt}";
            SheetLink(sheet.Cell(row, 11), "${db.sheetName}");
            Center(sheet, row, 2, 10, 11);
            Line(sheet, row, 2, 11);
            Paint(sheet.Range(row, 2, row, 10), status, error != null ? sheet.Cell(row, 5) : StatusCell(sheet, row, status));
        }

        // 요약 줄에서 상태 글자색을 칠할 칸: 가장 나쁜 상태의 수
        private static IXLCell StatusCell(IXLWorksheet sheet, int row, string status)
        {
            switch (status)
            {
                case "crit": return sheet.Cell(row, 5);
                case "error": return sheet.Cell(row, 6);
                case "warn": return sheet.Cell(row, 7);
                default: return sheet.Cell(row, 9);
            }
        }

        // 주의 이상 항목: C DB · D 항목 · E 상태 · F:H 값 · I 직전 대비 · J:K 시트
        private static void IssueRow(IXLWorksheet sheet, int row, string db, string item, string status, string value, string trend, string link)
        {
            sheet.Cell(row, 3).Value = db;
            sheet.Cell(row, 4).Value = item;
            sheet.Cell(row, 5).Value = status;
            Merge(sheet, row, 6, row, 8, value);
            sheet.Cell(row, 9).Value = trend;
            Merge(sheet, row, 10, row, 11, link);
        }

        private static void IssueValues(IXLWorksheet sheet, int row, string status)
        {
            IssueRow(sheet, row, "${db.name}", "${check.category} · ${check.title}", "${check.statusText}", "${check.valueText}", "${check.trend}", "");
            SheetLink(sheet.Cell(row, 10), "${db.sheetName}");
            Center(sheet, row, 5, 9, 10);
            Line(sheet, row, 3, 11);
            Paint(sheet.Range(row, 3, row, 9), status, sheet.Cell(row, 5));
        }

        // ================= DB 한 개 =================

        // B No · C 분류 · D 항목 · E 상태 · F 값 · G 직전 대비 · H 판정 기준 · I 설명·조치 (발견 행은 B~I 8칸에 열 1~8)
        private static void Db(IXLWorksheet sheet)
        {
            Widths(sheet, 3, 5, 10, 28, 8, 22, 10, 26, 46);
            Directive(sheet, 1, 9, "#sheet($db in $dbs)");

            Title(sheet, 2, 2, 8, "일일 점검: ${db.name}");
            var back = sheet.Cell(2, 9);
            back.FormulaA1 = "HYPERLINK(\"#'" + SummarySheet + "'!A1\",\"← 요약\")";
            back.Style.Font.FontColor = LinkColor;
            back.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            back.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

            var facts = new[,]
            {
                { "대상", "#if($db.label)${db.label} · #end${db.address}" },
                { "제품·버전", "${db.version}" },
                { "점검 시각", "${db.startedAt} (${db.elapsed}초)#if($db.previousAt) · 직전 점검 ${db.previousAt}#end" },
                { "요약", "${db.summary}" }
            };
            for (var i = 0; i < facts.GetLength(0); i++)
            {
                var row = 3 + i;
                Label(Merge(sheet, row, 2, row, 3, ""), facts[i, 0]);
                Merge(sheet, row, 4, row, 9, facts[i, 1]).Style.Alignment.WrapText = true;
            }
            Box(sheet.Range(3, 2, 6, 9));
            Directive(sheet, 7, 9, "#if($db.error)");
            var failed = Merge(sheet, 8, 2, 8, 9, "접속 실패: ${db.error}");
            failed.Style.Font.FontColor = CritText;
            failed.Style.Font.Bold = true;
            failed.Style.Alignment.WrapText = true;
            sheet.Range(8, 2, 8, 9).Style.Fill.BackgroundColor = CritFill;
            Box(sheet.Range(8, 2, 8, 9));
            Directive(sheet, 9, 9, "#end");

            Section(sheet, 11, 2, 9, "점검 항목 (${db.checks.size()})");
            Header(sheet, 12, 2, "No", "분류", "항목", "상태", "값", "직전 대비", "판정 기준", "설명·조치");
            Directive(sheet, 13, 9, "#foreach($check in $db.checks)");
            var variants = new[] { "crit", "warn", "error", "skip", "info", "ok" };
            var row2 = 14;
            for (var i = 0; i < variants.Length; i++)
            {
                var status = variants[i];
                Directive(sheet, row2++, 9, i == 0 ? "#if($check.status == \"" + status + "\")" : i < variants.Length - 1 ? "#elseif($check.status == \"" + status + "\")" : "#else");
                CheckValues(sheet, row2++, status);
            }
            Directive(sheet, row2++, 9, "#end");
            Directive(sheet, row2++, 9, "#end");
            Directive(sheet, row2++, 9, "#if(!$db.checks)");
            Empty(sheet, row2++, 2, 9, "(점검 결과 없음)");
            Directive(sheet, row2++, 9, "#end");

            row2++;
            Section(sheet, row2++, 2, 9, "발견 사항 (주의·위험·정보 항목의 결과 행)");
            Directive(sheet, row2++, 9, "#foreach($check in $db.findings)");
            var sub = Merge(sheet, row2, 2, row2, 9,
                "${check.no}. [${check.statusText}] ${check.category} · ${check.title} — ${check.rowCount}행#if($check.rowCount > $check.shownRows) (앞 ${check.shownRows}행만)#end");
            sub.Style.Font.Bold = true;
            sheet.Range(row2, 2, row2, 9).Style.Fill.BackgroundColor = SubFill;
            row2++;
            for (var c = 0; c < CheckReportModel.CellColumns; c++)
                sheet.Cell(row2, 2 + c).Value = "${check.c" + (c + 1) + "}";
            var head = sheet.Range(row2, 2, row2, 9);
            head.Style.Font.Bold = true;
            head.Style.Fill.BackgroundColor = LabelFill;
            Box(head);
            row2++;
            Directive(sheet, row2++, 9, "#foreach($row in $check.rows)");
            for (var c = 0; c < CheckReportModel.CellColumns; c++)
                sheet.Cell(row2, 2 + c).Value = "${row.v" + (c + 1) + "}";
            Line(sheet, row2, 2, 9);
            row2++;
            Directive(sheet, row2++, 9, "#end");
            row2++; // 항목 사이 빈 줄
            Directive(sheet, row2++, 9, "#end");
            Directive(sheet, row2++, 9, "#if(!$db.findings)");
            Empty(sheet, row2++, 2, 9, "(없음)");
            Directive(sheet, row2++, 9, "#end");
            row2++;
            Note(sheet, row2, 2, 9, "* 발견 행은 항목마다 최대 100행입니다. 열이 8개를 넘으면 8번째 칸에 나머지를 '열: 값'으로 이었습니다.");

            sheet.PageSetup.PaperSize = XLPaperSize.A4Paper;
            sheet.PageSetup.PageOrientation = XLPageOrientation.Landscape;
            sheet.PageSetup.FitToPages(1, 0);
        }

        private static void CheckValues(IXLWorksheet sheet, int row, string status)
        {
            Values(sheet, row, 2, "${check.no}", "${check.category}", "${check.title}", "${check.statusText}", "${check.valueText}", "${check.trend}", "${check.rule}", "${check.note}");
            Center(sheet, row, 2, 5, 7);
            Line(sheet, row, 2, 9);
            Paint(sheet.Range(row, 2, row, 9), status, sheet.Cell(row, 5));
        }

        // ================= 모양 도우미 =================

        // 상태에 맞게 줄 바탕과 상태 칸 글자색을 칠한다
        private static void Paint(IXLRange range, string status, IXLCell statusCell)
        {
            XLColor fill = null;
            XLColor text;
            switch (status)
            {
                case "crit":
                    fill = CritFill;
                    text = CritText;
                    break;
                case "warn":
                    fill = WarnFill;
                    text = WarnText;
                    break;
                case "error":
                    fill = ErrorFill;
                    text = ErrorText;
                    break;
                case "skip":
                    text = SkipText;
                    range.Style.Font.FontColor = SkipText;
                    break;
                case "info":
                    text = InfoText;
                    break;
                default:
                    text = OkText;
                    break;
            }
            if (fill != null)
                range.Style.Fill.BackgroundColor = fill;
            statusCell.Style.Font.FontColor = text;
            statusCell.Style.Font.Bold = true;
        }

        private static void SheetLink(IXLCell cell, string sheetName)
        {
            cell.FormulaA1 = "HYPERLINK(\"#'" + sheetName + "'!A1\",\"이동\")";
            cell.Style.Font.FontColor = LinkColor;
            cell.Style.Font.Underline = XLFontUnderlineValues.Single;
        }

        private static void Widths(IXLWorksheet sheet, params double[] widths)
        {
            sheet.ShowGridLines = false;
            for (var i = 0; i < widths.Length; i++)
                sheet.Column(i + 1).Width = widths[i];
        }

        private static void Title(IXLWorksheet sheet, int row, int first, int last, string text)
        {
            var cell = Merge(sheet, row, first, row, last, text);
            cell.Style.Font.FontSize = 16;
            cell.Style.Font.Bold = true;
            cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            sheet.Row(row).Height = 26;
        }

        private static void Section(IXLWorksheet sheet, int row, int first, int last, string text)
        {
            var cell = Merge(sheet, row, first, row, last, text);
            cell.Style.Font.Bold = true;
            sheet.Range(row, first, row, last).Style.Fill.BackgroundColor = SectionFill;
        }

        private static void Header(IXLWorksheet sheet, int row, int first, params string[] titles)
        {
            for (var i = 0; i < titles.Length; i++)
                sheet.Cell(row, first + i).Value = titles[i];
            HeaderStyle(sheet.Range(row, first, row, first + titles.Length - 1));
        }

        private static void HeaderStyle(IXLRange range)
        {
            range.Style.Fill.BackgroundColor = HeaderFill;
            range.Style.Font.FontColor = XLColor.White;
            range.Style.Font.Bold = true;
            range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            Box(range);
        }

        private static void Values(IXLWorksheet sheet, int row, int first, params string[] values)
        {
            for (var i = 0; i < values.Length; i++)
                sheet.Cell(row, first + i).Value = values[i];
        }

        // 값 줄 모양(테두리·줄바꿈·위 정렬)
        private static void Line(IXLWorksheet sheet, int row, int first, int last)
        {
            var range = sheet.Range(row, first, row, last);
            range.Style.Alignment.WrapText = true;
            range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
            Box(range);
        }

        private static void Center(IXLWorksheet sheet, int row, params int[] columns)
        {
            foreach (var column in columns)
                sheet.Cell(row, column).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }

        private static void Label(IXLCell cell, string text)
        {
            cell.Value = text;
            var range = cell.MergedRange() ?? cell.AsRange();
            range.Style.Fill.BackgroundColor = LabelFill;
            range.Style.Font.Bold = true;
            range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
        }

        private static void Empty(IXLWorksheet sheet, int row, int first, int last, string text)
        {
            var cell = Merge(sheet, row, first, row, last, text);
            cell.Style.Font.FontColor = NoteColor;
            Box(sheet.Range(row, first, row, last));
        }

        private static void Note(IXLWorksheet sheet, int row, int first, int last, string text)
        {
            var cell = Merge(sheet, row, first, row, last, text);
            cell.Style.Font.FontSize = 9;
            cell.Style.Font.FontColor = NoteColor;
            cell.Style.Alignment.WrapText = true;
            cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
        }

        private static IXLCell Merge(IXLWorksheet sheet, int row1, int column1, int row2, int column2, string value)
        {
            return TableSpecTemplate.Merge(sheet, row1, column1, row2, column2, value);
        }

        private static void Box(IXLRange range)
        {
            range.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            range.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            range.Style.Border.OutsideBorderColor = LineColor;
            range.Style.Border.InsideBorderColor = LineColor;
        }

        // 지시문 줄: 결과에는 남지 않지만 템플릿에서 눈에 띄게(A열)
        private static void Directive(IXLWorksheet sheet, int row, int lastColumn, string text)
        {
            TableSpecTemplate.Directive(sheet, row, text, lastColumn);
        }
    }
}

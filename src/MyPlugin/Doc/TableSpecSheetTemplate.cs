using System.IO;
using ClosedXML.Excel;

namespace MyPlugin
{
    /// <summary>
    /// 명세서형 템플릿(.xlsx) — 워크스코브 "테이블 명세서 양식"(https://erd.workscove.com/ko/blog/table-spec-template)의 구성과 모양을 따라 코드로 만든다.
    /// 시트:
    /// - 표지: ${doc.title}, 프로젝트명·시스템/모듈·DBMS / 버전·대상 DB·스키마 기준일·작성자·문서 버전, 테이블 목록(물리명·논리명·설명·[이동] 링크).
    /// - ${table.sheetName}: #sheet로 테이블마다 — 테이블 설명, 컬럼 정의(컬럼명·논리명·자료형·NULL·기본값·키·설명),
    ///   외래키(참조 대상·삭제 시·갱신 시), 인덱스(유형 PK·UNIQUE·INDEX), 검사 조건(있을 때만).
    /// - 공통 코드 정의: 검사 조건 "열 IN (…)"에서 찾은 값을 미리 채우고 빈 줄을 남긴다(의미는 사람이 적는다).
    /// - 개정 이력.
    /// - _작성 가이드·_변수 설명: 안내(결과에서 빠짐).
    /// 원본 양식의 작성 예시·빈 양식 시트는 DB에서 채우므로 두지 않는다.
    /// </summary>
    internal static class TableSpecSheetTemplate
    {
        public const string CoverSheet = "표지";
        public const string TableSheet = "${table.sheetName}";
        public const string CodeSheet = "공통 코드 정의";
        public const string HistorySheet = "개정 이력";
        public const string GuideSheet = "_작성 가이드";

        private static readonly XLColor HeaderFill = XLColor.FromHtml("#3F4759");
        private static readonly XLColor LabelFill = XLColor.FromHtml("#F2F3F5");
        private static readonly XLColor SectionFill = XLColor.FromHtml("#EEF0FF");
        private static readonly XLColor LineColor = XLColor.FromHtml("#C9CDD4");
        private static readonly XLColor LinkColor = XLColor.FromHtml("#2F5BD1");
        private static readonly XLColor NoteColor = XLColor.FromHtml("#8A8F98");
        private static readonly XLColor DirectiveFill = XLColor.FromHtml("#FFF2CC");
        private static readonly XLColor DirectiveText = XLColor.FromHtml("#7F6000");

        public static byte[] Create()
        {
            using (var workbook = new XLWorkbook())
            using (var stream = new MemoryStream())
            {
                workbook.Style.Font.FontName = "맑은 고딕";
                workbook.Style.Font.FontSize = 10.5;
                Cover(workbook.AddWorksheet(CoverSheet));
                Table(workbook.AddWorksheet(TableSheet));
                Codes(workbook.AddWorksheet(CodeSheet));
                History(workbook.AddWorksheet(HistorySheet));
                Guide(workbook.AddWorksheet(GuideSheet));
                TableSpecTemplate.Help(workbook.AddWorksheet(TableSpecTemplate.HelpSheet));
                workbook.Worksheet(CoverSheet).SetTabActive();
                workbook.SaveAs(stream);
                return stream.ToArray();
            }
        }

        // ================= 표지 =================

        private static void Cover(IXLWorksheet sheet)
        {
            Widths(sheet, 3, 17, 22, 18, 44, 10);
            Title(sheet, 2, 2, 6, "${doc.title}");

            var facts = new[,]
            {
                { "프로젝트명", "${doc.project}" },
                { "시스템/모듈", "${doc.system}" },
                { "DBMS / 버전", "${db.version}" },
                { "대상 DB / 스키마", "${db.name} (${db.address}) · ${doc.schemas}" },
                { "스키마 기준일", "${doc.date}" },
                { "작성자 / 소속", "${doc.author}" },
                { "문서 버전", "${doc.version}" }
            };
            for (var i = 0; i < facts.GetLength(0); i++)
            {
                var row = 4 + i;
                Label(sheet.Cell(row, 2), facts[i, 0]);
                Merge(sheet, row, 3, row, 6, facts[i, 1]).Style.Alignment.WrapText = true;
            }
            Box(sheet.Range(4, 2, 10, 6));

            Section(sheet, 12, 2, 6, "테이블 목록 (${doc.tableCount}개)");
            Header(sheet, 13, 2, "No", "테이블명(물리)", "논리명", "설명", "시트");
            Directive(sheet, 14, 6, "#foreach($t in $tables)");
            Values(sheet, 15, 2, "${foreach.count}", "${t.name}", "${t.logicalName}", "${t.description}");
            var link = sheet.Cell(15, 6);
            link.FormulaA1 = "HYPERLINK(\"#'${t.sheetName}'!A1\",\"이동\")";
            link.Style.Font.FontColor = LinkColor;
            link.Style.Font.Underline = XLFontUnderlineValues.Single;
            Center(sheet, 15, 2, 6);
            Box(sheet.Range(15, 2, 15, 6));
            Directive(sheet, 16, 6, "#end");
            Directive(sheet, 17, 6, "#if(!$tables)");
            Empty(sheet, 18, 2, 6, "(대상 테이블 없음)");
            Directive(sheet, 19, 6, "#end");

            Note(sheet, 21, 2, 6, "* 스키마 기준일(${doc.date})은 이 문서가 어느 시점의 DB를 옮긴 것인지 나타냅니다. ${doc.generatedAt}에 DB Helper가 DB에서 읽어 만들었습니다.");
            Note(sheet, 22, 2, 6, "* 논리명·설명은 주석을 나눈 것입니다(첫 줄 또는 ':' 앞이 논리명, 나머지가 설명). 주석을 그렇게 적어 두면 그대로 옮겨집니다.");

            sheet.PageSetup.PaperSize = XLPaperSize.A4Paper;
            sheet.PageSetup.PageOrientation = XLPageOrientation.Portrait;
            sheet.PageSetup.FitToPages(1, 0);
        }

        // ================= 테이블 한 개 =================

        private static void Table(IXLWorksheet sheet)
        {
            // B No · C 컬럼명 · D 논리명 · E 자료형 · F NULL · G 기본값 · H 키 · I 설명 (자료형·참조 대상이 줄바뀌지 않게 E를 넓게)
            Widths(sheet, 3, 5.5, 20, 16, 20, 7, 18, 11, 34);
            Directive(sheet, 1, 9, "#sheet($table in $tables)");

            Title(sheet, 2, 2, 8, "테이블: ${table.name}#if($table.logicalName) (${table.logicalName})#end");
            var back = sheet.Cell(2, 9);
            back.FormulaA1 = "HYPERLINK(\"#'" + CoverSheet + "'!A1\",\"← 표지\")";
            back.Style.Font.FontColor = LinkColor;
            back.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            back.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

            Label(Merge(sheet, 3, 2, 3, 3, ""), "테이블 설명");
            Merge(sheet, 3, 4, 3, 9, "#if($table.description)${table.description}#{else}${table.comment}#end").Style.Alignment.WrapText = true;
            Label(Merge(sheet, 4, 2, 4, 3, ""), "스키마 / 종류");
            Merge(sheet, 4, 4, 4, 9, "${table.owner} · ${table.typeLabel}#if($table.numRowsText) · 행 수(통계) ${table.numRowsText}#end#if($table.tablespace) · ${table.tablespace}#end");
            Box(sheet.Range(3, 2, 4, 9));

            // 컬럼 정의
            Section(sheet, 6, 2, 9, "컬럼 정의 (${table.columnCount})");
            Header(sheet, 7, 2, "No", "컬럼명", "논리명", "자료형", "NULL", "기본값", "키", "설명");
            Directive(sheet, 8, 9, "#foreach($col in $table.columns)");
            Values(sheet, 9, 2, "${col.no}", "${col.name}", "${col.logicalName}", "${col.typeLabel}", "${col.nullable}", "${col.default}", "${col.key}",
                "#if($col.description)${col.description}#elseif($col.isFk)${col.fkRef} 참조#end");
            Row(sheet, 9, 2, 9);
            Center(sheet, 9, 2, 6, 8);
            Directive(sheet, 10, 9, "#end");

            // 외래키: 이름 B:C · 컬럼 D · 참조 대상 E:F · 삭제 시 G · 갱신 시 H · 비고 I
            Section(sheet, 12, 2, 9, "외래키 (참조 관계)");
            ForeignKeyRow(sheet, 13, "이름", "컬럼", "참조 대상", "삭제 시", "갱신 시", "비고");
            HeaderStyle(sheet.Range(13, 2, 13, 9));
            Directive(sheet, 14, 9, "#foreach($fk in $table.foreignKeys)");
            ForeignKeyRow(sheet, 15, "${fk.name}", "${fk.columns}", "${fk.target}", "${fk.deleteRule}", "${fk.updateRule}", "");
            Row(sheet, 15, 2, 9);
            Center(sheet, 15, 7, 8);
            Directive(sheet, 16, 9, "#end");
            Directive(sheet, 17, 9, "#if(!$table.foreignKeys)");
            Empty(sheet, 18, 2, 9, "(없음)");
            Directive(sheet, 19, 9, "#end");

            // 인덱스: 이름 B:C · 컬럼(순서 포함) D:F · 유형 G · 용도·비고 H:I
            Section(sheet, 21, 2, 9, "인덱스");
            IndexRow(sheet, 22, "이름", "컬럼(순서 포함)", "유형", "용도·비고");
            HeaderStyle(sheet.Range(22, 2, 22, 9));
            Directive(sheet, 23, 9, "#foreach($idx in $table.indexes)");
            IndexRow(sheet, 24, "${idx.name}", "${idx.columns}", "${idx.kind}", "");
            Row(sheet, 24, 2, 9);
            Center(sheet, 24, 7);
            Directive(sheet, 25, 9, "#end");
            Directive(sheet, 26, 9, "#if(!$table.indexes)");
            Empty(sheet, 27, 2, 9, "(없음)");
            Directive(sheet, 28, 9, "#end");

            // 검사 조건(있을 때만): 이름 B:C · 컬럼 D · 조건 E:I
            Directive(sheet, 29, 9, "#if($table.checks)");
            Section(sheet, 31, 2, 9, "검사 조건");
            CheckRow(sheet, 32, "이름", "컬럼", "조건");
            HeaderStyle(sheet.Range(32, 2, 32, 9));
            Directive(sheet, 33, 9, "#foreach($ck in $table.checks)");
            CheckRow(sheet, 34, "${ck.name}", "${ck.columns}", "${ck.condition}");
            Row(sheet, 34, 2, 9);
            Directive(sheet, 35, 9, "#end");
            Directive(sheet, 36, 9, "#end");

            sheet.SheetView.FreezeRows(7);
            sheet.PageSetup.PaperSize = XLPaperSize.A4Paper;
            sheet.PageSetup.PageOrientation = XLPageOrientation.Landscape;
            sheet.PageSetup.FitToPages(1, 0);
        }

        private static void ForeignKeyRow(IXLWorksheet sheet, int row, string name, string columns, string target, string onDelete, string onUpdate, string note)
        {
            Merge(sheet, row, 2, row, 3, name);
            sheet.Cell(row, 4).Value = columns;
            Merge(sheet, row, 5, row, 6, target);
            sheet.Cell(row, 7).Value = onDelete;
            sheet.Cell(row, 8).Value = onUpdate;
            sheet.Cell(row, 9).Value = note;
        }

        private static void IndexRow(IXLWorksheet sheet, int row, string name, string columns, string kind, string note)
        {
            Merge(sheet, row, 2, row, 3, name);
            Merge(sheet, row, 4, row, 6, columns);
            sheet.Cell(row, 7).Value = kind;
            Merge(sheet, row, 8, row, 9, note);
        }

        private static void CheckRow(IXLWorksheet sheet, int row, string name, string columns, string condition)
        {
            Merge(sheet, row, 2, row, 3, name);
            sheet.Cell(row, 4).Value = columns;
            Merge(sheet, row, 5, row, 9, condition);
        }

        // ================= 공통 코드 · 개정 이력 =================

        private static void Codes(IXLWorksheet sheet)
        {
            Widths(sheet, 3, 20, 9, 16, 24, 22);
            Title(sheet, 2, 2, 6, CodeSheet);
            Note(sheet, 3, 2, 6, "상태·유형처럼 코드값을 담는 컬럼의 값 의미를 모아 둡니다. 검사 조건 '열 IN (…)'에서 찾은 값은 미리 채웠습니다 — 의미는 직접 적으세요.");
            sheet.Row(3).Height = 28;
            Header(sheet, 5, 2, "코드 그룹", "코드", "의미", "사용 테이블.컬럼", "비고");
            Directive(sheet, 6, 6, "#foreach($code in $codes)");
            Values(sheet, 7, 2, "${code.group}", "${code.code}", "${code.meaning}", "${code.usage}", "${code.constraint}");
            Row(sheet, 7, 2, 6);
            Center(sheet, 7, 3);
            Directive(sheet, 8, 6, "#end");
            // 직접 적을 빈 줄
            Row(sheet, 9, 2, 6, 18);
            sheet.SheetView.FreezeRows(5);
            sheet.PageSetup.PaperSize = XLPaperSize.A4Paper;
            sheet.PageSetup.PageOrientation = XLPageOrientation.Landscape;
            sheet.PageSetup.FitToPages(1, 0);
        }

        private static void History(IXLWorksheet sheet)
        {
            Widths(sheet, 3, 9, 13, 14, 64);
            Title(sheet, 2, 2, 5, HistorySheet);
            Header(sheet, 4, 2, "버전", "일자", "작성자", "변경 내용");
            Values(sheet, 5, 2, "${doc.version}", "${doc.date}", "${doc.author}", "#if($doc.description)${doc.description}#{else}최초 작성#end");
            Row(sheet, 5, 2, 5, 14);
            Center(sheet, 5, 2, 3, 4);
            sheet.PageSetup.PaperSize = XLPaperSize.A4Paper;
            sheet.PageSetup.PageOrientation = XLPageOrientation.Portrait;
            sheet.PageSetup.FitToPages(1, 0);
        }

        // ================= 안내(결과에서 빠짐) =================

        private static void Guide(IXLWorksheet sheet)
        {
            Widths(sheet, 3, 16, 100);
            Title(sheet, 2, 2, 3, "작성 가이드 (이 시트는 이름이 '_'로 시작해 산출물에서 빠집니다)");
            Section(sheet, 4, 2, 3, "칸마다 DB에서 채우는 값");
            var rules = new[,]
            {
                { "컬럼명", "ALL_TAB_COLUMNS.COLUMN_NAME(물리명). ${col.name}" },
                { "논리명", "열 주석의 첫 줄(또는 ':'·'|'·' - ' 앞, '이름(설명)'의 이름). 주석이 한 덩어리면 주석 전체. ${col.logicalName}" },
                { "자료형", "길이까지: VARCHAR2(50 CHAR), NUMBER(12,2). ${col.typeLabel}" },
                { "NULL", "허용 Y, 불가 N. ${col.nullable}" },
                { "기본값", "DATA_DEFAULT 그대로(없으면 빈 칸). ${col.default}" },
                { "키", "PK(기본 키) · FK(외래 키) · UK(고유 제약·고유 인덱스) · IX(그 밖 인덱스). 겹치면 PK,FK. ${col.key}" },
                { "설명", "주석에서 논리명을 뺀 나머지. 비었고 외래 키면 '참조 대상 참조'. ${col.description}" },
                { "외래키", "참조 대상(테이블.열), 삭제 시(DELETE_RULE), 갱신 시(Oracle은 ON UPDATE가 없어 늘 NO ACTION). 비고는 직접 적는다" },
                { "인덱스", "유형: 기본 키가 쓰는 인덱스 PK, 고유 UNIQUE, 그 밖 INDEX. 용도·비고는 직접 적는다" },
                { "공통 코드", "검사 조건 '열 IN (…)'의 값. 의미는 직접 적는다" },
                { "스키마 기준일", "대상 선택 창의 작성일(${doc.date}). 어느 시점의 DB인지 꼭 남긴다" }
            };
            for (var i = 0; i < rules.GetLength(0); i++)
            {
                Label(sheet.Cell(5 + i, 2), rules[i, 0]);
                sheet.Cell(5 + i, 3).SetValue(rules[i, 1]);
                sheet.Cell(5 + i, 3).Style.Alignment.WrapText = true;
            }
            Box(sheet.Range(5, 2, 4 + rules.GetLength(0), 3));
            Note(sheet, 6 + rules.GetLength(0), 2, 3, "* 양식 구성은 워크스코브 '테이블 명세서 양식'(erd.workscove.com/ko/blog/table-spec-template)을 따랐습니다. 문법·변수 전체는 '_변수 설명' 시트를 보세요.");
        }

        // ================= 모양 도우미 =================

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

        // 값 줄 모양(테두리·줄바꿈·위 정렬). count만큼 같은 모양의 빈 줄을 더 만든다
        private static void Row(IXLWorksheet sheet, int row, int first, int last, int lastRow = 0)
        {
            var range = sheet.Range(row, first, lastRow > row ? lastRow : row, last);
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
            cell.Style.Font.FontSize = 9.5;
            cell.Style.Font.FontColor = NoteColor;
            cell.Style.Alignment.WrapText = true;
            cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
        }

        private static IXLCell Merge(IXLWorksheet sheet, int row1, int column1, int row2, int column2, string value)
        {
            sheet.Range(row1, column1, row2, column2).Merge();
            var cell = sheet.Cell(row1, column1);
            cell.Value = value;
            return cell;
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
            var cell = sheet.Cell(row, 1);
            cell.Value = text;
            cell.Style.Font.FontColor = DirectiveText;
            cell.Style.Font.Italic = true;
            cell.Style.Font.FontSize = 9;
            sheet.Range(row, 1, row, lastColumn).Style.Fill.BackgroundColor = DirectiveFill;
        }
    }
}

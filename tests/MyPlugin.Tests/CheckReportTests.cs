using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    /// <summary>일일 점검 보고서: 변수(CheckReportModel)와 기본 양식(CheckReportTemplate)을 실제로 그려 시트·칸·줄 색을 본다.</summary>
    public class CheckReportTests
    {
        private static CheckOutcome Outcome(string id, string category, string title, CheckStatus status, string value, CheckRule rule = null,
            string[] columns = null, string[][] rows = null, CheckTrend trend = CheckTrend.Same, string message = null)
        {
            var check = new MonitorCheck(id, category, title, "SELECT 1 FROM DUAL", rule ?? CheckRule.AnyRow(CheckLevel.Warning), "SELECT_CATALOG_ROLE", title + " 조치");
            return new CheckOutcome
            {
                Check = check,
                Status = status,
                ValueText = value,
                Message = message,
                Columns = (columns ?? new string[0]).ToList(),
                Rows = (rows ?? new string[0][]).ToList(),
                RowCount = (rows ?? new string[0][]).Length,
                Trend = trend,
                Elapsed = TimeSpan.FromMilliseconds(250)
            };
        }

        private static List<CheckReportDb> Sample()
        {
            var wide = Enumerable.Range(1, 10).Select(i => "C" + i).ToArray();
            return new List<CheckReportDb>
            {
                new CheckReportDb
                {
                    Name = "운영 DB", Address = "dba@db1:1521/prod", Label = "PROD (DBID 1)", Version = "Oracle Database 19c",
                    StartedAt = new DateTime(2026, 10, 20, 9, 0, 0), Elapsed = TimeSpan.FromSeconds(4.24), PreviousAt = new DateTime(2026, 10, 19, 9, 0, 0),
                    Outcomes = new List<CheckOutcome>
                    {
                        Outcome("ora.invalid_objects", "객체", "INVALID 객체", CheckStatus.Warning, "2건", null, new[] { "소유자", "객체" },
                            new[] { new[] { "SCOTT", "P_X" }, new[] { "HR", "V_Y" } }, CheckTrend.Worse),
                        Outcome("ora.tablespace", "공간", "테이블스페이스", CheckStatus.Critical, "96 — MES", CheckRule.Threshold("사용률(%)", 85, 95),
                            new[] { "테이블스페이스", "사용률(%)" }, new[] { new[] { "MES", "96" } }),
                        Outcome("ora.alert_log", "로그", "경고 로그", CheckStatus.Skipped, "건너뜀", null, null, null, CheckTrend.None, "권한이 없어 건너뜀"),
                        Outcome("ora.instance", "가용성", "인스턴스", CheckStatus.Ok, "정상"),
                        Outcome("user.wide", "사용자", "넓은 결과", CheckStatus.Info, "C1 v1", CheckRule.Info(), wide,
                            new[] { Enumerable.Range(1, 10).Select(i => "v" + i).ToArray() })
                    }
                },
                new CheckReportDb { Name = "개발 DB", Address = "dev@db2:1521/dev", Error = "ORA-12541: TNS:no listener" }
            };
        }

        [Fact]
        public void Model_Variables_CountsOrderAndCells()
        {
            var context = CheckReportModel.Context(Sample(), null, "dba01", new DateTime(2026, 10, 20, 9, 5, 0));
            object report;
            Assert.True(context.TryGet("report", out report));
            var r = (IDictionary<string, object>)report;
            Assert.Equal(CheckReportModel.DefaultTitle, r["title"]);
            Assert.Equal("2026-10-20", r["date"]);
            Assert.Equal("dba01", r["author"]);
            Assert.Equal(2, r["dbCount"]);
            Assert.Equal(2, r["issueCount"]);
            Assert.Equal("접속 실패 1 · 위험 1 · 주의 1 · 건너뜀 1 · 정상 2", r["summary"]);
            object dbs;
            Assert.True(context.TryGet("dbs", out dbs));
            var first = (IDictionary<string, object>)((IList<object>)dbs)[0];
            Assert.Equal("운영 DB", first["sheetName"]);
            Assert.Equal(1, first["critCount"]);
            Assert.Equal(1, first["warnCount"]);
            Assert.Equal(1, first["skipCount"]);
            Assert.Equal(2, first["okCount"]);
            Assert.Equal("crit", first["worstCode"]);
            Assert.Equal(4.2, first["elapsed"]);
            Assert.Equal("2026-10-19 09:00", first["previousAt"]);
            // 분류 순서: 가용성 → 공간 → 로그 → 객체 → 사용자
            var checks = ((IList<object>)first["checks"]).Cast<IDictionary<string, object>>().ToList();
            Assert.Equal(new[] { "ora.instance", "ora.tablespace", "ora.alert_log", "ora.invalid_objects", "user.wide" }, checks.Select(c => c["id"]));
            Assert.Equal(new object[] { 1, 2, 3, 4, 5 }, checks.Select(c => c["no"]));
            Assert.Equal("권한이 없어 건너뜀", checks[2]["note"]);
            Assert.Equal("INVALID 객체 조치", checks[3]["note"]);
            Assert.Equal("", checks[0]["note"]);
            Assert.Equal("↑ 나빠짐", checks[3]["trend"]);
            // 발견 사항: 주의·위험·정보 + 행 있음
            var findings = ((IList<object>)first["findings"]).Cast<IDictionary<string, object>>().Select(c => c["id"]).ToList();
            Assert.Equal(new[] { "ora.tablespace", "ora.invalid_objects", "user.wide" }, findings);
            // 열이 8개를 넘으면 8번째 칸에 이어 붙인다
            Assert.Equal("C8 · C9 · C10", checks[4]["c8"]);
            var row = (IDictionary<string, object>)((IList<object>)checks[4]["rows"])[0];
            Assert.Equal("v7", row["v7"]);
            Assert.Equal("C8: v8 · C9: v9 · C10: v10", row["v8"]);
            var failed = (IDictionary<string, object>)((IList<object>)dbs)[1];
            Assert.Equal("접속 실패", failed["summary"]);
            Assert.Equal("error", failed["worstCode"]);
        }

        [Fact]
        public void DefaultTemplate_Renders_SummaryAndDbSheets_StatusColors_FindingCells()
        {
            var template = CheckReportTemplate.Create();
            var context = CheckReportModel.Context(Sample(), null, "dba01", new DateTime(2026, 10, 20, 9, 5, 0));
            using (var output = new MemoryStream())
            {
                ExcelTemplate.Render(template, context, output);
                using (var book = new XLWorkbook(new MemoryStream(output.ToArray())))
                {
                    Assert.Equal(new[] { "요약", "운영 DB", "개발 DB" }, book.Worksheets.Select(w => w.Name));
                    var summary = book.Worksheet("요약");
                    var text = Cells(summary);
                    Assert.Contains(CheckReportModel.DefaultTitle, text);
                    Assert.Contains("dba01", text);
                    Assert.Contains("접속 실패 — ORA-12541: TNS:no listener", text);
                    Assert.Contains("객체 · INVALID 객체", text);
                    Assert.Contains("공간 · 테이블스페이스", text);
                    Assert.DoesNotContain(text, t => t.Contains("${") || t.StartsWith("#"));
                    // 운영 DB 줄은 가장 나쁜 상태(위험) 색
                    var prodRow = summary.CellsUsed().First(c => c.GetString() == "운영 DB").Address.RowNumber;
                    Assert.Equal(XLColor.FromHtml("#FBE3E4"), summary.Cell(prodRow, 3).Style.Fill.BackgroundColor);

                    var db = book.Worksheet("운영 DB");
                    var dbText = Cells(db);
                    Assert.Contains("일일 점검: 운영 DB", dbText);
                    Assert.Contains("PROD (DBID 1) · dba@db1:1521/prod", dbText);
                    Assert.Contains("2026-10-20 09:00 (4.2초) · 직전 점검 2026-10-19 09:00", dbText);
                    Assert.DoesNotContain(dbText, t => t.Contains("${") || t.StartsWith("#"));
                    Assert.DoesNotContain(dbText, t => t.StartsWith("접속 실패"));
                    var tablespace = db.CellsUsed().First(c => c.GetString() == "테이블스페이스" && c.Address.ColumnNumber == 4).Address.RowNumber;
                    Assert.Equal("위험", db.Cell(tablespace, 5).GetString());
                    Assert.Equal(XLColor.FromHtml("#FBE3E4"), db.Cell(tablespace, 2).Style.Fill.BackgroundColor);
                    Assert.Equal(XLColor.FromHtml("#B3261E"), db.Cell(tablespace, 5).Style.Font.FontColor);
                    var invalid = db.CellsUsed().First(c => c.GetString() == "INVALID 객체" && c.Address.ColumnNumber == 4).Address.RowNumber;
                    Assert.Equal(XLColor.FromHtml("#FFF4D6"), db.Cell(invalid, 2).Style.Fill.BackgroundColor);
                    Assert.Equal("↑ 나빠짐", db.Cell(invalid, 7).GetString());
                    Assert.Equal("INVALID 객체 조치", db.Cell(invalid, 9).GetString());
                    var skipped = db.CellsUsed().First(c => c.GetString() == "경고 로그" && c.Address.ColumnNumber == 4).Address.RowNumber;
                    Assert.Equal("권한이 없어 건너뜀", db.Cell(skipped, 9).GetString());
                    // 발견 사항: 열 제목 줄 + 값 줄(칸마다)
                    var header = db.CellsUsed().First(c => c.GetString() == "소유자").Address;
                    Assert.Equal("객체", db.Cell(header.RowNumber, header.ColumnNumber + 1).GetString());
                    Assert.Equal("SCOTT", db.Cell(header.RowNumber + 1, header.ColumnNumber).GetString());
                    Assert.Equal("V_Y", db.Cell(header.RowNumber + 2, header.ColumnNumber + 1).GetString());
                    Assert.Contains(dbText, t => t.StartsWith("2. [위험] 공간 · 테이블스페이스 — 1행"));
                    Assert.Contains("C8: v8 · C9: v9 · C10: v10", dbText);

                    var dev = Cells(book.Worksheet("개발 DB"));
                    Assert.Contains("접속 실패: ORA-12541: TNS:no listener", dev);
                    Assert.Contains("(점검 결과 없음)", dev);
                }
            }
        }

        [Fact]
        public void DefaultTemplate_NoIssues_ShowsEmptyLines_HelpSheetHasCheckVariables()
        {
            var clean = new List<CheckReportDb>
            {
                new CheckReportDb { Name = "XE", Address = "a@b:1521/xe", StartedAt = DateTime.Now, Outcomes = new List<CheckOutcome> { Outcome("ora.instance", "가용성", "인스턴스", CheckStatus.Ok, "정상") } }
            };
            var template = CheckReportTemplate.Create();
            using (var book = new XLWorkbook(new MemoryStream(template)))
            {
                var help = Cells(book.Worksheet(CheckReportTemplate.HelpSheet));
                Assert.Contains("$check.status / statusText", help);
                Assert.Contains("#sheet($table in $tables)", help);
            }
            using (var output = new MemoryStream())
            {
                ExcelTemplate.Render(template, CheckReportModel.Context(clean), output);
                using (var book = new XLWorkbook(new MemoryStream(output.ToArray())))
                {
                    Assert.Contains("(주의 이상 항목 없음)", Cells(book.Worksheet("요약")));
                    var db = Cells(book.Worksheet("XE"));
                    Assert.Contains("(없음)", db);
                    Assert.Contains("정상", db);
                }
            }
        }

        private static List<string> Cells(IXLWorksheet sheet)
        {
            return sheet.CellsUsed().Select(c => c.GetString()).Where(s => s.Length > 0).ToList();
        }
    }
}

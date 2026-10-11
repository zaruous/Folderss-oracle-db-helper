using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClosedXML.Excel;
using Xunit;
using Xunit.Abstractions;

namespace MyPlugin.OracleIT
{
    /// <summary>
    /// 기능 5. GCM 정의서. 시험 스키마의 GCM_CODE 표를 사용자 SQL(별칭 계약)로 읽어 [열 확인](ROWNUM 한 줄)과 [만들기](전체 조회 → 그룹 → 엑셀·JSON·CSV)를 실제 Oracle에서 확인한다.
    /// </summary>
    [Collection(OracleCollection.Name)]
    public sealed class F5_CodeSpecTests : OracleTestBase
    {
        private const string Owner = OracleFixture.User;

        private const string UserSql =
            "SELECT CODE_GROUP AS TABLE_NAME, GROUP_NM AS GROUP_NAME, CODE AS CODE, CODE_NM AS CODE_NAME, SORT_SEQ AS SORT_NO, USE_FLAG AS USE_YN, PLANT_CD\n" +
            "  FROM " + Owner + ".GCM_CODE\n" +
            " ORDER BY CODE_GROUP, SORT_SEQ;";

        public F5_CodeSpecTests(OracleFixture db, ITestOutputHelper output) : base(db, output, "F5 GCM 정의서") { }

        [OracleFact]
        public async Task F5_1_Probe_ChecksAliases_WithoutReadingRows()
        {
            using (var session = await Db.OpenSessionAsync())
            {
                long ms = 0;
                var check = await Timed(() => CodeSpecQueries.ProbeAsync(session, UserSql, CancellationToken.None), x => ms = x);
                Log("열 확인 " + ms + "ms: " + check.Summary);
                Assert.True(check.Ok, check.Summary);
                Assert.Equal(new[] { "PLANT_CD" }, check.Extra.ToArray());
                Assert.Equal(new[] { "GROUP_DESC", "CODE_DESC" }, check.MissingOptional.ToArray());

                var missing = await CodeSpecQueries.ProbeAsync(session, "SELECT CODE_GROUP, CODE FROM " + Owner + ".GCM_CODE", CancellationToken.None);
                Assert.False(missing.Ok);
                Assert.Equal(new[] { "TABLE_NAME", "CODE_NAME" }, missing.Missing.ToArray());
            }
        }

        [OracleFact]
        public async Task F5_2_Load_Groups_Excel_Json_Csv()
        {
            using (var session = await Db.OpenSessionAsync())
            {
                long ms = 0;
                var table = await Timed(() => CodeSpecQueries.LoadAsync(session, UserSql, CancellationToken.None), x => ms = x);
                Log("조회 " + ms + "ms: " + table.Rows.Count + "행, 열 " + string.Join(", ", table.Columns));
                var data = CodeSpecModel.Build(table, "it.sql", CodeSpecQueries.Clean(UserSql));
                Assert.Equal(new[] { "ORDER_STATUS", "YN" }, data.Groups.Select(g => g.Id).ToArray());
                Assert.Equal("주문 상태", data.Groups[0].Name);
                Assert.Equal(new[] { "A", "B", "Z" }, data.Groups[0].Codes.Select(c => c.Code).ToArray());
                Assert.Equal("P1", data.Groups[0].Codes[0].Attrs["PLANT_CD"]);
                Assert.False(data.Groups[0].Codes[2].IsUsed);

                var doc = new TableSpecDocument { Title = "GCM 시험", Project = "IT", Author = "it" };
                var profile = Db.Profile();
                var folder = Path.GetDirectoryName(Db.ReportPath);
                foreach (var template in CodeSpecTemplates.All)
                {
                    var path = Path.Combine(folder, "gcm-" + template.Id + ".xlsx");
                    using (var output = new MemoryStream())
                    {
                        ExcelTemplate.Render(template.Create(), CodeSpecModel.Context(doc, profile, data, "Oracle"), output);
                        File.WriteAllBytes(path, output.ToArray());
                    }
                    using (var book = new XLWorkbook(path))
                    {
                        Assert.DoesNotContain(book.Worksheets, w => w.Name.StartsWith("_", StringComparison.Ordinal));
                        var leftovers = book.Worksheets.SelectMany(w => w.CellsUsed()).Where(c => !c.HasFormula && c.Value.IsText)
                            .Select(c => c.GetText()).Where(t => t.Contains("${") || t.Contains("#foreach")).ToList();
                        Assert.True(leftovers.Count == 0, string.Join(" | ", leftovers));
                    }
                    Log("엑셀 " + template.Id + ": " + path);
                }
                var json = Path.Combine(folder, "gcm.json");
                File.WriteAllText(json, CodeSpecModel.Json(doc, profile, data, "Oracle"));
                var csv = Path.Combine(folder, "gcm.csv");
                File.WriteAllBytes(csv, CodeSpecModel.CsvBytes(data));
                Assert.Contains("\"ORDER_STATUS\"", File.ReadAllText(json));
                Assert.Equal(6, File.ReadAllLines(csv).Length);
                Log("JSON·CSV: " + json + ", " + csv);
            }
        }
    }
}

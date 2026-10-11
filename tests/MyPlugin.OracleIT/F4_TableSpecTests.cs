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
    /// 기능 4. 테이블 정의서. 대상 선택 창의 후보 조회와 정의서 일괄 조회(요약·열·인덱스·제약 조건)를 실제 데이터 사전에 던지고,
    /// 기본 템플릿으로 엑셀을 만들어 본다(결과 파일은 보고 파일 옆에 남긴다).
    /// </summary>
    [Collection(OracleCollection.Name)]
    public sealed class F4_TableSpecTests : OracleTestBase
    {
        private const string Owner = OracleFixture.User;

        public F4_TableSpecTests(OracleFixture db, ITestOutputHelper output) : base(db, output, "F4 정의서") { }

        [OracleFact]
        public async Task F4_1_Candidates_TablesViewsComments()
        {
            using (var session = await Db.OpenSessionAsync())
            {
                var tables = await session.QueryAsync(TableSpecQueries.Candidates(Owner, false), TableSpecQueries.ReadCandidate);
                var all = await session.QueryAsync(TableSpecQueries.Candidates(Owner, true), TableSpecQueries.ReadCandidate);
                Log("후보: 테이블 " + tables.Count + "개, 뷰 포함 " + all.Count + "개 — " + string.Join(", ", all.Select(c => c.Name + "/" + c.Type + "/" + c.Comment)));

                Assert.Equal(new[] { "GCM_CODE", "ORDERS", "ORDER_ITEMS" }, tables.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
                Assert.Equal(4, all.Count);
                var orders = all.Single(c => c.Name == "ORDERS");
                Assert.Equal("주문", orders.Comment);
                Assert.Equal(OracleFixture.OrderRows, orders.NumRows);
                Assert.Equal("VIEW", all.Single(c => c.Name == "V_ORDER_SUMMARY").Type);
            }
        }

        [OracleFact]
        public async Task F4_2_Load_ColumnsDefaultsIndexesConstraints()
        {
            using (var session = await Db.OpenSessionAsync())
            {
                var targets = new[] { "ORDERS", "ORDER_ITEMS", "V_ORDER_SUMMARY", "NO_SUCH_TABLE" }.Select(n => new TableKey(Owner, n)).ToList();
                long ms = 0;
                var tables = await Timed(() => TableSpecQueries.LoadAsync(session, targets, null, CancellationToken.None), x => ms = x);
                Log("일괄 조회 " + ms + "ms: " + string.Join(", ", tables.Select(t => t.Name + "(열 " + t.Columns.Count + ", 인덱스 " + t.Indexes.Count + ", 제약 " + t.Constraints.Count + ")")));

                Assert.Equal(new[] { "ORDERS", "ORDER_ITEMS", "V_ORDER_SUMMARY" }, tables.Select(t => t.Name).ToArray());

                var orders = tables[0];
                Assert.Equal("TABLE", orders.Type);
                Assert.Equal("주문", orders.Comment);
                Assert.Equal("USERS", orders.Tablespace);
                Assert.Equal(OracleFixture.OrderRows, orders.NumRows);
                Assert.Equal(new[] { "NUMBER(10)", "VARCHAR2(50 CHAR)", "NUMBER(12,2)", "NVARCHAR2(100)", "DATE", "CLOB" }, orders.Columns.Select(c => c.Column.TypeLabel).ToArray());
                Assert.Equal("주문 번호", orders.Columns[0].Comment);
                Assert.True(orders.Columns[0].Column.PrimaryKey);
                Assert.False(orders.Columns[1].Column.Nullable);
                Assert.Equal("SYSDATE", orders.Columns.Single(c => c.Column.Name == "CREATED_AT").Default);
                Assert.Null(orders.Columns.Single(c => c.Column.Name == "AMOUNT").Default);
                var index = orders.Indexes.Single(i => i.Name == "IX_ORDERS_NAME");
                Assert.Equal(new[] { "CUSTOMER_NAME", "CREATED_AT" }, index.ColumnNames.ToArray());
                Assert.False(index.Unique);
                Assert.True(orders.Indexes.Single(i => i.Name == "PK_ORDERS").Unique);

                var items = tables[1];
                var fk = items.Constraints.Single(c => c.Type == "R");
                Assert.Equal(Owner + ".ORDERS", fk.Reference);
                Assert.Equal(new[] { "ORDER_ID" }, fk.ColumnNames.ToArray());
                Assert.Equal(new[] { "ORDER_ID" }, fk.ReferenceColumns.ToArray());
                Assert.Equal("QTY > 0", items.Constraints.Single(c => c.Name == "CK_ORDER_ITEMS_QTY").Condition);
                // 시스템 이름의 NOT NULL 검사는 빠진다
                Assert.DoesNotContain(items.Constraints, c => c.Type == "C" && c.Name.StartsWith("SYS_", StringComparison.Ordinal));
                Assert.Equal(new[] { "P", "R", "C" }, items.Constraints.Select(c => c.Type).Distinct().OrderBy(t => t == "P" ? 0 : t == "R" ? 1 : 2).ToArray());

                var view = tables[2];
                Assert.Equal("VIEW", view.Type);
                Assert.Equal(2, view.Columns.Count);
                Assert.Empty(view.Indexes);
            }
        }

        [OracleFact]
        public async Task F4_3_ManyNames_SplitIntoChunks()
        {
            using (var session = await Db.OpenSessionAsync())
            {
                // 이름 1,200개(2개만 있음) — IN 목록을 500개씩 나눠 바인드한다
                var targets = Enumerable.Range(0, 1198).Select(i => new TableKey(Owner, "NOPE_" + i)).Concat(new[] { new TableKey(Owner, "ORDERS"), new TableKey(Owner, "ORDER_ITEMS") }).ToList();
                var steps = 0;
                var tables = await TableSpecQueries.LoadAsync(session, targets, new SyncProgress(_ => steps++), CancellationToken.None);
                Log("이름 " + targets.Count + "개 → 찾은 테이블 " + tables.Count + "개, 진행 알림 " + steps + "번");
                Assert.Equal(new[] { "ORDERS", "ORDER_ITEMS" }, tables.Select(t => t.Name).ToArray());
                Assert.Equal(3 * 4, steps);
            }
        }

        [OracleFact]
        public async Task F4_4_DefaultTemplate_MakesWorkbook()
        {
            using (var session = await Db.OpenSessionAsync())
            {
                var targets = TableSpecLogic.Order(new[] { "ORDERS", "ORDER_ITEMS", "V_ORDER_SUMMARY" }.Select(n => new TableKey(Owner, n)));
                var tables = await TableSpecQueries.LoadAsync(session, targets, null, CancellationToken.None);
                var document = new TableSpecDocument { Title = "주문 시스템 테이블 정의서", Project = "Oracle 실측", Author = "DB Helper", Date = DateTime.Today };
                var context = TableSpecModel.Context(document, Db.Profile(), tables);
                var path = Path.Combine(Path.GetDirectoryName(Db.ReportPath), "oracle-it-table-spec.xlsx");
                using (var output = new MemoryStream())
                {
                    ExcelTemplate.Render(TableSpecTemplate.Create(), context, output);
                    File.WriteAllBytes(path, output.ToArray());
                }
                Log("정의서: " + path);

                using (var book = new XLWorkbook(path))
                {
                    Assert.Equal(new[] { "표지", "목록", "ORDERS", "ORDER_ITEMS", "V_ORDER_SUMMARY" }, book.Worksheets.Select(w => w.Name).ToArray());
                    var orders = book.Worksheet("ORDERS");
                    Assert.Equal("주문", orders.Cell(2, 2).GetText());
                    Assert.Equal("CREATED_AT", orders.Cell(14, 3).GetText());
                    Assert.Equal("SYSDATE", orders.Cell(14, 10).GetText());
                    Assert.Equal("→ " + Owner + ".ORDERS.ORDER_ID", book.Worksheet("ORDER_ITEMS").Cell(11, 11).GetText());
                }
            }
        }

        [OracleFact]
        public async Task F4_5_SpecSheetTemplate_VersionDeleteRuleIndexKind()
        {
            using (var session = await Db.OpenSessionAsync())
            {
                var version = await TableSpecQueries.LoadServerVersionAsync(session, CancellationToken.None);
                Log("DB 버전: " + version);
                Assert.StartsWith("Oracle Database", version);

                var targets = TableSpecLogic.Order(new[] { "ORDERS", "ORDER_ITEMS" }.Select(n => new TableKey(Owner, n)));
                var tables = await TableSpecQueries.LoadAsync(session, targets, null, CancellationToken.None);
                var fk = tables[1].Constraints.Single(c => c.Type == "R");
                Assert.Equal("NO ACTION", fk.DeleteRule);
                Assert.Equal("PK_ORDERS", tables[0].Constraints.Single(c => c.Type == "P").IndexName);

                var document = new TableSpecDocument { Title = "주문 테이블 명세서", Project = "Oracle 실측", System = "주문", Author = "DB Helper", Date = DateTime.Today };
                var context = TableSpecModel.Context(document, Db.Profile(), tables, version);
                var path = Path.Combine(Path.GetDirectoryName(Db.ReportPath), "oracle-it-table-spec-sheet.xlsx");
                using (var output = new MemoryStream())
                {
                    ExcelTemplate.Render(TableSpecSheetTemplate.Create(), context, output);
                    File.WriteAllBytes(path, output.ToArray());
                }
                Log("명세서형: " + path);

                using (var book = new XLWorkbook(path))
                {
                    Assert.Equal(new[] { "표지", "ORDERS", "ORDER_ITEMS", "공통 코드 정의", "개정 이력" }, book.Worksheets.Select(w => w.Name).ToArray());
                    Assert.Equal(version, book.Worksheet("표지").Cell("C6").GetText());
                    var orders = book.Worksheet("ORDERS");
                    Assert.Equal("테이블: ORDERS (주문)", orders.Cell("B1").GetText());
                    Assert.Equal("PK", orders.Cell("H7").GetText());
                    Assert.Equal("IX", orders.Cell("H8").GetText()); // CUSTOMER_NAME: IX_ORDERS_NAME의 첫 열
                    Assert.Equal("NO ACTION", book.Worksheet("ORDER_ITEMS").Cell("G13").GetText());
                }
            }
        }

        // Progress<T>는 동기화 문맥으로 넘겨 늦게 불린다 — 시험에서는 바로 센다
        private sealed class SyncProgress : IProgress<string>
        {
            private readonly Action<string> _report;
            public SyncProgress(Action<string> report) { _report = report; }
            public void Report(string value) { _report(value); }
        }
    }
}

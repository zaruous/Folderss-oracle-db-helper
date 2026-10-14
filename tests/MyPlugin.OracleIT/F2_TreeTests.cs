using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace MyPlugin.OracleIT
{
    /// <summary>
    /// 기능 2. DB 트리 탐색과 서버 검색.
    /// DbTreePanel이 펼칠 때 하는 조회(스키마 → 묶음 개수 → 객체 목록 → 열)와 검색(스키마·객체·열)을 같은 쿼리·읽기 함수로 실제 데이터 사전에 던진다.
    /// </summary>
    [Collection(OracleCollection.Name)]
    public sealed class F2_TreeTests : OracleTestBase
    {
        private const string Owner = OracleFixture.User;

        public F2_TreeTests(OracleFixture db, ITestOutputHelper output) : base(db, output, "F2 트리") { }

        [OracleFact]
        public async Task F2_1_Schemas_UseOracleMaintained_AndHideBuiltins()
        {
            using (var session = await Db.OpenSessionAsync())
            {
                // 12c는 ALL_USERS.ORACLE_MAINTAINED가 있다 — QueryWithOmAsync의 첫 시도(om=true)가 ORA-00904 없이 성공해야 한다
                long ms = 0;
                var schemas = await Timed(() => session.QueryAsync(OracleMetadata.Schemas(true), r => OracleMetadata.ReadSchema(r, true)), x => ms = x);
                var mine = schemas.Single(s => s.Name == Owner);
                Assert.False(mine.OracleMaintained);
                Assert.True(schemas.Single(s => s.Name == "SYS").OracleMaintained);
                Assert.True(schemas.Single(s => s.Name == "SYSTEM").OracleMaintained);
                var visible = schemas.Where(s => !s.OracleMaintained).Select(s => s.Name).ToList();
                Log("스키마 " + schemas.Count + "개(" + ms + "ms), 내장 숨김 후 " + visible.Count + "개: " + string.Join(", ", visible));

                // 11g 대체 경로(KnownSystemSchemas)도 같은 서버에서 같은 판정을 내는지
                var fallback = await session.QueryAsync(OracleMetadata.Schemas(false), r => OracleMetadata.ReadSchema(r, false));
                var diff = schemas.Where(s => s.OracleMaintained != fallback.Single(f => f.Name == s.Name).OracleMaintained).Select(s => s.Name).ToList();
                Log("11g 대체 목록과 내장 판정이 다른 스키마: " + (diff.Count == 0 ? "없음" : string.Join(", ", diff)));
            }
        }

        [OracleFact]
        public async Task F2_2_GroupCounts_ObjectsAndPaging()
        {
            using (var session = await Db.OpenSessionAsync())
            {
                long countMs = 0;
                var rows = await Timed(() => session.QueryAsync(OracleMetadata.GroupCounts(Owner), r => TreeLoaderLogic.ReadGroupCount(r)), x => countMs = x);
                var counts = TreeLoaderLogic.GroupCounts(rows);
                Log("묶음 개수(" + countMs + "ms): " + string.Join(", ", counts.Select(kv => TreeGroups.Title(kv.Key) + " " + kv.Value)));
                Assert.Equal(3, counts[TreeGroups.Table]); // ORDERS, ORDER_ITEMS, GCM_CODE(F5)
                Assert.Equal(1, counts[TreeGroups.View]);
                Assert.Equal(1, counts[TreeGroups.Sequence]);
                Assert.Equal(3, counts[TreeGroups.Code]); // PROCEDURE·FUNCTION·PACKAGE (PACKAGE BODY 제외)

                var tables = await session.QueryAsync(OracleMetadata.Objects(Owner, TreeGroups.Table, 1000), r => OracleMetadata.ReadObject(r));
                var orders = tables.Single(t => t.Name == "ORDERS");
                Assert.Equal(OracleFixture.OrderRows, orders.NumRows);
                Assert.Equal("VALID", orders.Status);
                Log("테이블: " + string.Join(", ", tables.Select(t => t.Name + "(" + (t.NumRows.HasValue ? t.NumRows + "행" : "통계 없음") + ")")));

                var code = await session.QueryAsync(OracleMetadata.Objects(Owner, TreeGroups.Code, 1000), r => OracleMetadata.ReadObject(r));
                Assert.Equal(new[] { "F_DOUBLE", "PKG_UTIL", "P_TOUCH" }, code.Select(o => o.Name).ToArray());
                Assert.All(code, o => Assert.Null(o.NumRows));
                Log("코드: " + string.Join(", ", code.Select(o => o.Name + " " + o.Type + " " + o.Status)));

                // [더 보기] 페이징: limit=1이면 2행(limit+1)을 받아 1개만 보이고 HasMore
                var paged = await session.QueryAsync(OracleMetadata.Objects(Owner, TreeGroups.Table, 1), r => OracleMetadata.ReadObject(r));
                Assert.Equal(2, paged.Count);
                var page = TreeLoaderLogic.Page(paged, 1);
                Assert.Single(page.Items);
                Assert.True(page.HasMore);
                Log("페이징 limit=1 → 받은 행 " + paged.Count + ", 보이는 행 " + page.Items.Count + ", HasMore=" + page.HasMore);
            }
        }

        [OracleFact]
        public async Task F2_3_Columns_TypeLabelsPrimaryKeyNullable()
        {
            using (var session = await Db.OpenSessionAsync())
            {
                long ms = 0;
                var cols = await Timed(() => session.QueryAsync(OracleMetadata.Columns(Owner, "ORDERS"), r => OracleMetadata.ReadColumn(r)), x => ms = x);
                Log("ORDERS 열(" + ms + "ms): " + string.Join(", ", cols.Select(c => c.Name + " " + c.TypeLabel + (c.PrimaryKey ? " PK" : "") + (c.Nullable ? "" : " NOT NULL"))));
                Assert.Equal(new[] { "ORDER_ID", "CUSTOMER_NAME", "AMOUNT", "NOTE", "CREATED_AT", "MEMO" }, cols.Select(c => c.Name).ToArray());
                Assert.Equal(new[] { "NUMBER(10)", "VARCHAR2(50 CHAR)", "NUMBER(12,2)", "NVARCHAR2(100)", "DATE", "CLOB" }, cols.Select(c => c.TypeLabel).ToArray());
                Assert.True(cols[0].PrimaryKey);
                Assert.False(cols[0].Nullable);
                Assert.False(cols[1].Nullable);
                Assert.True(cols[2].Nullable);
                Assert.All(cols.Skip(1), c => Assert.False(c.PrimaryKey));

                // FK 열은 PK가 아니다(ORDER_ITEMS.ORDER_ID)
                var items = await session.QueryAsync(OracleMetadata.Columns(Owner, "ORDER_ITEMS"), r => OracleMetadata.ReadColumn(r));
                Assert.True(items.Single(c => c.Name == "ITEM_ID").PrimaryKey);
                Assert.False(items.Single(c => c.Name == "ORDER_ID").PrimaryKey);

                var view = await session.QueryAsync(OracleMetadata.Columns(Owner, "V_ORDER_SUMMARY"), r => OracleMetadata.ReadColumn(r));
                Assert.Equal(2, view.Count);
            }
        }

        [OracleFact]
        public async Task F2_4_ServerSearch_SchemasObjectsColumns()
        {
            using (var session = await Db.OpenSessionAsync())
            {
                var limit = OracleMetadata.SearchLimit;
                long ms = 0;
                var pattern = SqlScript.ContainsPattern(" order ");
                var objects = await Timed(() => session.QueryAsync(OracleMetadata.SearchObjects(pattern, false, true, limit), r => OracleMetadata.ReadObjectHit(r)), x => ms = x);
                var mine = objects.Where(h => h.Owner == Owner).Select(h => h.ObjectType + " " + h.ObjectName).ToList();
                Log("객체 검색 'order'(" + ms + "ms) 전체 " + objects.Count + "건, DBH_IT: " + string.Join(", ", mine));
                Assert.Contains("TABLE ORDERS", mine);
                Assert.Contains("TABLE ORDER_ITEMS", mine);
                Assert.Contains("VIEW V_ORDER_SUMMARY", mine);
                Assert.Contains("SEQUENCE SEQ_ORDER", mine);
                Assert.DoesNotContain(objects, h => h.Owner == "SYS" || h.Owner == "SYSTEM");

                var colPattern = SqlScript.ContainsPattern("order_id");
                var cols = await Timed(() => session.QueryAsync(OracleMetadata.SearchColumns(colPattern, false, true, limit), r => OracleMetadata.ReadColumnHit(r)), x => ms = x);
                var myCols = cols.Where(h => h.Owner == Owner).Select(h => h.ObjectName + "." + h.ColumnName + " " + h.ColumnType + " (" + h.ObjectType + ")").ToList();
                Log("열 검색 'order_id'(" + ms + "ms): " + string.Join(", ", myCols));
                Assert.Equal(3, myCols.Count); // ORDERS, ORDER_ITEMS, V_ORDER_SUMMARY

                // '_'는 와일드카드가 아니라 글자로 찾는다(ESCAPE '\') — "ORDERXID" 같은 이름이 걸리면 안 된다
                Assert.All(cols, h => Assert.Contains("ORDER_ID", h.ColumnName));

                var schemas = await session.QueryAsync(OracleMetadata.SearchSchemas(SqlScript.ContainsPattern("dbh_it"), false, true, limit), r => OracleMetadata.ReadSchema(r, true));
                Assert.Equal(new[] { OracleFixture.User, OracleFixture.SpecialUser }, schemas.Select(s => s.Name).ToArray());

                // 내장 스키마 포함 검색은 SYS 객체도 돌려준다. ALL_OBJECTS 기준이라 권한 있는 객체만 보인다(DBA_USERS는 안 보이고 PUBLIC에 열린 ALL_USERS는 보임)
                var withSystem = await Timed(() => session.QueryAsync(OracleMetadata.SearchObjects(SqlScript.ContainsPattern("_users"), true, true, limit), r => OracleMetadata.ReadObjectHit(r)), x => ms = x);
                Log("내장 포함 검색 '_users'(" + ms + "ms) " + withSystem.Count + "건: " + string.Join(", ", withSystem.Take(8).Select(h => h.Owner + "." + h.ObjectName)) + (withSystem.Count > 8 ? " …" : ""));
                Assert.Contains(withSystem, h => h.Owner == "SYS" && h.ObjectName == "ALL_USERS");
                Assert.DoesNotContain(withSystem, h => h.ObjectName == "DBA_USERS");
                var withoutSystem = await session.QueryAsync(OracleMetadata.SearchObjects(SqlScript.ContainsPattern("_users"), false, true, limit), r => OracleMetadata.ReadObjectHit(r));
                Log("같은 검색, 내장 제외: " + withoutSystem.Count + "건");
                Assert.Empty(withoutSystem);

                var result = TreeLoaderLogic.SearchResult(schemas, objects, cols, limit, null);
                Assert.False(result.Truncated);
                Log("검색 결과 묶음: 스키마 " + result.Schemas.Count + " · 객체 " + result.Objects.Count + " · 열 " + result.Columns.Count);
            }
        }

        [OracleFact]
        public async Task F2_5_Source_View_Procedure_Package()
        {
            // 트리 [소스 보기]·코드 객체 F4와 같은 조회: 뷰는 ALL_VIEWS.TEXT(LONG), 코드는 ALL_SOURCE
            using (var session = await Db.OpenSessionAsync())
            {
                var meta = DbProviders.Oracle.Metadata;
                var view = meta.BuildSource(Owner, "VIEW", "V_ORDER_SUMMARY",
                    await session.QueryAsync(meta.Source(Owner, "VIEW", "V_ORDER_SUMMARY"), ObjectSource.Read));
                Log(view);
                Assert.StartsWith("CREATE OR REPLACE VIEW " + Owner + ".V_ORDER_SUMMARY AS\n", view);
                Assert.Contains("ORDERS", view);

                var proc = meta.BuildSource(Owner, "PROCEDURE", "P_TOUCH",
                    await session.QueryAsync(meta.Source(Owner, "PROCEDURE", "P_TOUCH"), ObjectSource.Read));
                Log(proc);
                Assert.StartsWith("CREATE OR REPLACE PROCEDURE", proc);
                Assert.EndsWith("\n/\n", proc);

                var pkg = meta.BuildSource(Owner, "PACKAGE", "PKG_UTIL",
                    await session.QueryAsync(meta.Source(Owner, "PACKAGE", "PKG_UTIL"), ObjectSource.Read));
                Log(pkg);
                Assert.StartsWith("CREATE OR REPLACE PACKAGE ", pkg);
                Assert.Contains("\n\nCREATE OR REPLACE PACKAGE BODY ", pkg);
                // 넣은 그대로 SQL 탭에서 실행하면 명세·본문 두 문장
                Assert.Equal(2, SqlScript.Split(pkg).Count);

                Assert.Null(meta.BuildSource(Owner, "PROCEDURE", "NO_SUCH_PROC",
                    await session.QueryAsync(meta.Source(Owner, "PROCEDURE", "NO_SUCH_PROC"), ObjectSource.Read)));
            }
        }
    }
}

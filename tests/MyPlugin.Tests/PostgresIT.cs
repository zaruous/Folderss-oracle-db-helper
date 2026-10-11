using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MyPlugin;
using Xunit;
using Xunit.Abstractions;

namespace MyPlugin.Tests
{
    /// <summary>
    /// 실제 PostgreSQL에 붙는 시험(없으면 건너뜀). 환경 변수 PG_IT_DSN(호스트:포트/데이터베이스), PG_IT_USER, PG_IT_PW.
    /// 플러그인의 연결 흐름과 같은 경로(공급자 → 연결 문자열 → DbSession.Create → 데이터 사전·SQL 실행·GCM 조회)를 확인한다.
    /// 시험 대상 서버의 객체는 만들지 않는다(읽기만) — 어떤 DB든 현재 스키마에 테이블이 하나 이상 있으면 된다.
    /// PG_IT_GCM_SQL이 있으면 그 파일의 SQL로 GCM 별칭 검사·조회까지 돈다.
    /// </summary>
    public class PostgresIT
    {
        private readonly ITestOutputHelper _output;

        public PostgresIT(ITestOutputHelper output)
        {
            _output = output;
        }

        private sealed class PgFactAttribute : FactAttribute
        {
            public PgFactAttribute()
            {
                if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PG_IT_DSN")))
                    Skip = "PG_IT_DSN(예: localhost:5432/postgres)이 없어 실제 PostgreSQL 시험을 건너뜁니다.";
            }
        }

        private static OracleConnectionProfile Profile()
        {
            var dsn = Environment.GetEnvironmentVariable("PG_IT_DSN").Trim();
            var hostPort = dsn.Split('/')[0];
            return new OracleConnectionProfile
            {
                Id = "pg", Name = "PG 시험", Provider = "postgres",
                Host = hostPort.Split(':')[0],
                Port = int.Parse(hostPort.Split(':')[1]),
                ServiceName = dsn.Split('/')[1],
                UserId = Environment.GetEnvironmentVariable("PG_IT_USER") ?? "postgres"
            };
        }

        private static async Task<DbSession> OpenAsync()
        {
            var profile = Profile();
            var provider = DbProviders.For(profile);
            Assert.Same(DbProviders.Postgres, provider);
            var cs = OracleConnectionStore.BuildConnectionString(profile, Environment.GetEnvironmentVariable("PG_IT_PW") ?? "", ShellLogic.ConnectTimeoutSeconds);
            var session = DbSession.Create(provider, cs);
            await session.OpenAsync();
            return session;
        }

        [PgFact]
        public async Task Connect_CurrentSchema_Version_Tree_Search()
        {
            using (var session = await OpenAsync())
            {
                Assert.Same(DbProviders.Postgres, session.Provider);
                var meta = session.Provider.Metadata;
                var current = (await session.QueryAsync(meta.CurrentSchema(), r => r.GetValue(0).ToString())).Single();
                _output.WriteLine("current_schema: " + current);
                Assert.False(string.IsNullOrEmpty(current));

                var version = await TableSpecQueries.LoadServerVersionAsync(session, CancellationToken.None);
                _output.WriteLine("version: " + version);
                Assert.StartsWith("PostgreSQL", version);

                var schemas = await session.QueryAsync(meta.Schemas(false), r => meta.ReadSchema(r, false));
                _output.WriteLine("schemas: " + string.Join(", ", schemas.Select(s => s.Name + (s.OracleMaintained ? "(sys)" : ""))));
                Assert.Contains(schemas, s => s.Name == "pg_catalog" && s.OracleMaintained);
                Assert.Contains(schemas, s => s.Name == current && !s.OracleMaintained);

                var counts = TreeLoaderLogic.GroupCounts(await session.QueryAsync(meta.GroupCounts(current), r => TreeLoaderLogic.ReadGroupCount(r)));
                _output.WriteLine("counts: " + string.Join(", ", counts.Select(kv => kv.Key + "=" + kv.Value)));
                Assert.True(counts[TreeGroups.Table] > 0, "현재 스키마에 테이블이 있어야 합니다");

                var tables = await session.QueryAsync(meta.Objects(current, TreeGroups.Table, 5), r => meta.ReadObject(r));
                Assert.True(tables.Count <= 6);
                var page = TreeLoaderLogic.Page(tables, 5);
                _output.WriteLine("tables: " + string.Join(", ", page.Items.Select(t => t.Name + "(" + (t.NumRows.HasValue ? t.NumRows.ToString() : "?") + ")")) + (page.HasMore ? " …" : ""));
                var first = page.Items[0];
                Assert.Equal("TABLE", first.Type);

                var columns = await session.QueryAsync(meta.Columns(current, first.Name), r => meta.ReadColumn(r));
                _output.WriteLine("columns of " + first.Name + ": " + string.Join(", ", columns.Select(c => c.Name + " " + c.TypeLabel + (c.PrimaryKey ? " PK" : "") + (c.Nullable ? "" : " NN"))));
                Assert.NotEmpty(columns);
                Assert.Equal(Enumerable.Range(1, columns.Count).ToArray(), columns.Select(c => c.Position).ToArray());

                var code = await session.QueryAsync(meta.Objects(current, TreeGroups.Code, 3), r => meta.ReadObject(r));
                _output.WriteLine("code: " + string.Join(", ", code.Select(o => o.Name + "/" + o.Type)));

                var pattern = SqlScript.ContainsPattern(first.Name.Substring(0, Math.Min(4, first.Name.Length)));
                var hits = await session.QueryAsync(meta.SearchObjects(pattern, false, false, 50), r => meta.ReadObjectHit(r));
                Assert.Contains(hits, h => h.ObjectName == first.Name && h.Owner == current);
                var colHits = await session.QueryAsync(meta.SearchColumns(SqlScript.ContainsPattern(columns[0].Name), false, false, 50), r => meta.ReadColumnHit(r));
                Assert.Contains(colHits, h => h.ObjectName == first.Name && h.ColumnName == columns[0].Name);
                var schemaHits = await session.QueryAsync(meta.SearchSchemas(SqlScript.ContainsPattern(current), true, false, 50), r => meta.ReadSchema(r, false));
                Assert.Contains(schemaHits, s => s.Name == current);
            }
        }

        [PgFact]
        public async Task Execute_Select_Cursor_And_QueryTable_Probe()
        {
            using (var session = await OpenAsync())
            {
                var meta = session.Provider.Metadata;
                var current = (await session.QueryAsync(meta.CurrentSchema(), r => r.GetValue(0).ToString())).Single();
                var table = TreeLoaderLogic.Page(await session.QueryAsync(meta.Objects(current, TreeGroups.Table, 1), r => meta.ReadObject(r)), 1).Items[0];

                // 편집기와 같은 경로: 문장 분석 → 실행 → 커서 읽기
                var statement = SqlScript.Parse(WorkspaceLogic.SelectStatement(current, table.Name).TrimEnd().TrimEnd(';'));
                var result = await session.ExecuteAsync(statement, 3);
                _output.WriteLine("select " + table.Name + ": " + result.Summary + " · 열 " + string.Join(", ", result.Cursor.Columns.Select(c => c.Name + ":" + c.TypeLabel)));
                Assert.Equal(SqlKind.Query, result.Kind);
                Assert.True(result.Rows.Count <= 3);
                // 커서를 열어 둔 채 다른 조회(GCM 열 확인·메타데이터)를 해도 "A command is already in progress"가 나지 않아야 한다 — 세션이 커서를 먼저 닫는다
                var versionWhileOpen = await TableSpecQueries.LoadServerVersionAsync(session, CancellationToken.None);
                Assert.StartsWith("PostgreSQL", versionWhileOpen);
                Assert.True(result.Cursor.IsClosed);
                Assert.False(result.Cursor.HasMore);
                Assert.Empty(await session.FetchAsync(result.Cursor, 10));
                await session.CloseCursorAsync(result.Cursor);
                Assert.False(session.HasPendingChanges);

                // GCM 열 확인(LIMIT 1 방언)과 전체 조회
                var sql = "SELECT 'G' AS table_name, CAST(1 AS INT) AS code, 'one' AS code_name, 'x' AS extra";
                var check = await CodeSpecQueries.ProbeAsync(session, sql + ";", CancellationToken.None);
                Assert.True(check.Ok, check.Summary);
                Assert.Equal(new[] { "extra" }, check.Extra.ToArray());
                var data = CodeSpecModel.Build(await CodeSpecQueries.LoadAsync(session, sql, CancellationToken.None), null, sql);
                Assert.Equal("1", data.Groups[0].Codes[0].Code);
                Assert.Equal("x", data.Groups[0].Codes[0].Attrs["EXTRA"]);

                var file = Environment.GetEnvironmentVariable("PG_IT_GCM_SQL");
                // "default"면 플러그인 본보기 SQL로 돈다(사내 범용 코드 테이블이 있는 DB)
                if (file == "default" || (!string.IsNullOrEmpty(file) && System.IO.File.Exists(file)))
                {
                    var userSql = file == "default" ? CodeSpecLogic.DefaultSql : System.IO.File.ReadAllText(file);
                    var userCheck = await CodeSpecQueries.ProbeAsync(session, userSql, CancellationToken.None);
                    _output.WriteLine("GCM SQL 열 확인: " + userCheck.Summary);
                    Assert.True(userCheck.Ok, userCheck.Summary);
                    var userData = CodeSpecModel.Build(await CodeSpecQueries.LoadAsync(session, userSql, CancellationToken.None), "it.sql", userSql);
                    _output.WriteLine("GCM: 그룹 " + userData.Groups.Count + "개 · 코드 " + userData.CodeCount + "개 · 추가 속성 " + string.Join(", ", userData.Source.AttrNames));
                    Assert.True(userData.Groups.Count > 0);
                    // PG_IT_OUT 폴더가 있으면 실제 산출물(기본·단일 시트 엑셀, JSON, CSV)을 남겨 눈으로 확인한다
                    var outDir = Environment.GetEnvironmentVariable("PG_IT_OUT");
                    var doc = new TableSpecDocument { Title = "GCM 정의서(IT)", Project = "mesdb" };
                    foreach (var template in CodeSpecTemplates.All)
                    {
                        using (var output = new System.IO.MemoryStream())
                        {
                            ExcelTemplate.Render(template.Create(), CodeSpecModel.Context(doc, Profile(), userData, "PostgreSQL"), output);
                            Assert.True(output.Length > 0);
                            if (!string.IsNullOrEmpty(outDir))
                                System.IO.File.WriteAllBytes(System.IO.Path.Combine(outDir, template.Id + ".xlsx"), output.ToArray());
                        }
                    }
                    if (!string.IsNullOrEmpty(outDir))
                    {
                        System.IO.File.WriteAllText(System.IO.Path.Combine(outDir, "gcm.json"), CodeSpecModel.Json(doc, Profile(), userData, "PostgreSQL"));
                        System.IO.File.WriteAllBytes(System.IO.Path.Combine(outDir, "gcm.csv"), CodeSpecModel.CsvBytes(userData));
                    }
                }
            }
        }
    }
}

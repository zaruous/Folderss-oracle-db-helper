using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    /// <summary>
    /// 점검 실행기를 SQLite 메모리 DB 세션으로 시험한다: 차례로 돌고 하나가 실패해도 계속, 필요 조건 불충족은 건너뜀,
    /// 권한 오류는 건너뜀(필요 권한 안내), 항목 시간 제한은 오류(timeout), 바깥 취소는 OperationCanceledException.
    /// </summary>
    public class CheckRunnerTests
    {
        private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

        private static async Task<DbSession> OpenAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            // 행마다 ms만큼 쉬는 함수(시간 제한 시험)
            connection.CreateFunction("sleep_ms", (long ms) =>
            {
                Thread.Sleep((int)ms);
                return ms;
            });
            var session = new DbSession(connection);
            await session.OpenAsync();
            await session.QueryTableAsync("CREATE TABLE ts (name TEXT, pct REAL)", 0);
            await session.QueryTableAsync("INSERT INTO ts VALUES ('USERS', 40), ('MES', 91.5)", 0);
            return session;
        }

        private static MonitorCheck Check(string id, string sql, CheckRule rule, params MonitorRequirement[] requirements)
        {
            return new MonitorCheck(id, "공간", id, sql, rule, "SELECT_CATALOG_ROLE", "조치", requirements);
        }

        [Fact]
        public async Task RunsInOrder_FailureDoesNotStopTheRest_ProgressPerItem()
        {
            using (var session = await OpenAsync())
            {
                var checks = new[]
                {
                    Check("t.space", "SELECT name AS \"이름\", pct AS \"사용률(%)\" FROM ts", CheckRule.Threshold("사용률(%)", 85, 95)),
                    Check("t.broken", "SELECT * FROM no_such_table", CheckRule.AnyRow(CheckLevel.Warning)),
                    Check("t.none", "SELECT name FROM ts WHERE pct > 100", CheckRule.AnyRow(CheckLevel.Critical))
                };
                var seen = new List<string>();
                var outcomes = await CheckRunner.RunAsync(session, checks, o => seen.Add(o.Check.Id), CancellationToken.None).WaitAsync(Limit);
                Assert.Equal(new[] { "t.space", "t.broken", "t.none" }, seen);
                Assert.Equal(CheckStatus.Warning, outcomes[0].Status);
                Assert.Equal("91.5 — MES", outcomes[0].ValueText);
                Assert.Equal(CheckStatus.Error, outcomes[1].Status);
                Assert.Equal("error", outcomes[1].Reason);
                Assert.Contains("no_such_table", outcomes[1].Message);
                Assert.Equal(CheckStatus.Ok, outcomes[2].Status);
                Assert.All(outcomes, o => Assert.True(o.Elapsed >= TimeSpan.Zero));
            }
        }

        [Fact]
        public async Task RequirementNotMet_Skipped_WithMessage_QueryNotRun()
        {
            using (var session = await OpenAsync())
            {
                var check = Check("t.fra", "SELECT * FROM no_such_table", CheckRule.AnyRow(CheckLevel.Warning),
                    new MonitorRequirement("SELECT COUNT(*) FROM ts WHERE pct > 100", "FRA(복구 영역)를 쓰지 않습니다."));
                var outcome = (await CheckRunner.RunAsync(session, new[] { check }, null, CancellationToken.None).WaitAsync(Limit)).Single();
                Assert.Equal(CheckStatus.Skipped, outcome.Status);
                Assert.Equal("requirement", outcome.Reason);
                Assert.Equal("FRA(복구 영역)를 쓰지 않습니다.", outcome.Message);

                var met = Check("t.met", "SELECT name FROM ts", CheckRule.Info(), new MonitorRequirement("SELECT COUNT(*) FROM ts", "없음"));
                Assert.Equal(CheckStatus.Info, (await CheckRunner.RunAsync(session, new[] { met }, null, CancellationToken.None).WaitAsync(Limit)).Single().Status);
            }
        }

        [Fact]
        public async Task PermissionDenied_Skipped_NamesPrivilege()
        {
            using (var session = await OpenAsync())
            {
                var check = Check("t.alert", "SELECT * FROM v_diag_alert_ext", CheckRule.ByLevel("판정"));
                var outcome = (await CheckRunner.RunAsync(session, new[] { check }, null, CancellationToken.None, null,
                    ex => ex.Message.Contains("no such table")).WaitAsync(Limit)).Single();
                Assert.Equal(CheckStatus.Skipped, outcome.Status);
                Assert.Equal("privilege", outcome.Reason);
                Assert.Contains("필요 권한: SELECT_CATALOG_ROLE", outcome.Message);
                Assert.Contains("v_diag_alert_ext", outcome.Message);
            }
        }

        [Fact]
        public async Task SlowItem_TimesOut_AsError_NextItemStillRuns()
        {
            using (var session = await OpenAsync())
            {
                var slow = Check("t.slow", "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c WHERE x < 200) SELECT sleep_ms(50) AS v FROM c", CheckRule.Info());
                var next = Check("t.next", "SELECT name FROM ts", CheckRule.Info());
                var outcomes = await CheckRunner.RunAsync(session, new[] { slow, next }, null, CancellationToken.None, TimeSpan.FromMilliseconds(300)).WaitAsync(Limit);
                Assert.Equal(CheckStatus.Error, outcomes[0].Status);
                Assert.Equal("timeout", outcomes[0].Reason);
                Assert.Contains("시간 초과", outcomes[0].Message);
                Assert.True(outcomes[0].Elapsed < TimeSpan.FromSeconds(5), outcomes[0].Elapsed.ToString());
                Assert.Equal(CheckStatus.Info, outcomes[1].Status);
                Assert.Equal(2, outcomes[1].RowCount);
            }
        }

        [Fact]
        public async Task OuterCancel_Throws()
        {
            using (var session = await OpenAsync())
            using (var cancel = new CancellationTokenSource())
            {
                var checks = new[] { Check("t.a", "SELECT name FROM ts", CheckRule.Info()), Check("t.b", "SELECT name FROM ts", CheckRule.Info()) };
                var run = CheckRunner.RunAsync(session, checks, o => cancel.Cancel(), cancel.Token);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Limit));
            }
        }
    }
}

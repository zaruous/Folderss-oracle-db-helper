using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using MyPlugin;
using Oracle.ManagedDataAccess.Client;
using Xunit;

namespace MyPlugin.Tests
{
    /// <summary>
    /// DbSession을 SQLite 메모리 DB로 시험한다(커서·트랜잭션·잠금 동작). 메모리 DB는 세션이 연결을 열어 둔 동안만 살아 있다.
    /// Oracle에서만 생기는 동작(ORA-01013 취소, LOB 읽기, OracleTransaction)은 여기서 확인할 수 없다.
    /// </summary>
    public class DbSessionTests
    {
        private const string Memory = "Data Source=:memory:";

        // 끝나지 않는 작업이 시험 전체를 멈추지 않게 하는 한도
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

        [Fact]
        public async Task Query_FetchInBatches_ReturnsEveryRowOnceInOrder()
        {
            using (var session = await OpenAsync(new SqliteConnection(Memory)))
            {
                await CreateNumbersAsync(session, 1234);

                var result = await session.ExecuteAsync(Query("SELECT x, 'r' || x AS label FROM nums ORDER BY x"), 200);

                Assert.Equal(SqlKind.Query, result.Kind);
                Assert.Equal(200, result.Rows.Count);
                Assert.True(result.Cursor.HasMore);
                Assert.False(result.Cursor.IsClosed);
                Assert.Equal(200, result.Cursor.Fetched);
                Assert.Equal("200행 · 더 있음", result.Summary);
                Assert.Equal(-1, result.RecordsAffected);
                Assert.Equal(new[] { "x", "label" }, result.Cursor.Columns.Select(c => c.Name));

                var rows = new List<string[]>(result.Rows);
                var batches = new List<int>();
                while (result.Cursor.HasMore)
                {
                    var more = await session.FetchAsync(result.Cursor, 200);
                    batches.Add(more.Count);
                    rows.AddRange(more);
                    Assert.True(batches.Count < 100, "커서가 끝나지 않습니다.");
                }

                Assert.Equal(new[] { 200, 200, 200, 200, 200, 34 }, batches);
                Assert.Equal(Enumerable.Range(1, 1234).Select(Text), rows.Select(r => r[0]));
                Assert.Equal(Enumerable.Range(1, 1234).Select(i => "r" + Text(i)), rows.Select(r => r[1]));
                Assert.False(result.Cursor.HasMore);
                Assert.True(result.Cursor.IsClosed);
                Assert.Equal(1234, result.Cursor.Fetched);
                Assert.Empty(await session.FetchAsync(result.Cursor, 200));
            }
        }

        [Fact]
        public async Task Query_FetchCountEqualsTotal_HasMoreFalseAndCursorClosed()
        {
            using (var session = await OpenAsync(new SqliteConnection(Memory)))
            {
                await CreateNumbersAsync(session, 200);

                var result = await session.ExecuteAsync(Query("SELECT x FROM nums ORDER BY x"), 200);

                Assert.Equal(200, result.Rows.Count);
                Assert.False(result.Cursor.HasMore);
                Assert.True(result.Cursor.IsClosed);
                Assert.Equal("200행", result.Summary);
                Assert.Empty(await session.FetchAsync(result.Cursor, 200));
            }
        }

        [Fact]
        public async Task Query_NoRows_IsClosedButHasColumns()
        {
            using (var session = await OpenAsync(new SqliteConnection(Memory)))
            {
                await CreateNumbersAsync(session, 10);

                var result = await session.ExecuteAsync(Query("SELECT x FROM nums WHERE x < 0"), 200);

                Assert.Empty(result.Rows);
                Assert.False(result.Cursor.HasMore);
                Assert.True(result.Cursor.IsClosed);
                Assert.Equal(0, result.Cursor.Fetched);
                Assert.Equal("0행", result.Summary);
                Assert.Equal("x", Assert.Single(result.Cursor.Columns).Name);
            }
        }

        [Fact]
        public async Task Query_ZeroCounts_KeepLookaheadRowForNextFetch()
        {
            using (var session = await OpenAsync(new SqliteConnection(Memory)))
            {
                await CreateNumbersAsync(session, 5);

                var result = await session.ExecuteAsync(Query("SELECT x FROM nums ORDER BY x"), 0);
                Assert.Empty(result.Rows);
                Assert.True(result.Cursor.HasMore);
                Assert.Equal("0행 · 더 있음", result.Summary);

                Assert.Empty(await session.FetchAsync(result.Cursor, 0));
                Assert.True(result.Cursor.HasMore);

                var rows = await session.FetchAsync(result.Cursor, 100);
                Assert.Equal(new[] { "1", "2", "3", "4", "5" }, rows.Select(r => r[0]));
                Assert.True(result.Cursor.IsClosed);
                Assert.Equal(5, result.Cursor.Fetched);
            }
        }

        [Fact]
        public async Task Query_FormatsValuesAndDescribesColumns()
        {
            using (var session = await OpenAsync(new SqliteConnection(Memory)))
            {
                await SetupAsync(session,
                    "CREATE TABLE items (id INTEGER, price NUMERIC, name VARCHAR(10), note TEXT, data BLOB)",
                    "INSERT INTO items VALUES (1, 2.5, 'abc', NULL, x'0AFF')");

                var result = await session.ExecuteAsync(Query("SELECT id, price, name, note, data FROM items"), 10);

                var columns = result.Cursor.Columns;
                Assert.Equal(new[] { "id", "price", "name", "note", "data" }, columns.Select(c => c.Name));
                Assert.Equal(new[] { "INTEGER", "NUMERIC", "VARCHAR", "TEXT", "BLOB" }, columns.Select(c => c.TypeLabel));
                Assert.Equal(new[] { true, true, false, false, false }, columns.Select(c => c.IsNumeric));
                Assert.Equal(new[] { "1", "2.5", "abc", null, "0x0AFF" }, Assert.Single(result.Rows));
                Assert.Equal("1행", result.Summary);
            }
        }

        [Fact]
        public async Task Fetch_ClosedCursor_ReturnsEmptyList()
        {
            using (var session = await OpenAsync(new SqliteConnection(Memory)))
            {
                await CreateNumbersAsync(session, 20);
                var result = await session.ExecuteAsync(Query("SELECT x FROM nums ORDER BY x"), 5);
                Assert.True(result.Cursor.HasMore);

                await session.CloseCursorAsync(result.Cursor);

                Assert.True(result.Cursor.IsClosed);
                Assert.False(result.Cursor.HasMore);
                Assert.Empty(await session.FetchAsync(result.Cursor, 5));
                Assert.Equal(5, result.Cursor.Fetched);
                // 다시 닫아도 아무 일 없다
                await session.CloseCursorAsync(result.Cursor);
            }
        }

        [Fact]
        public async Task Dml_TracksPendingRowCountAndText()
        {
            using (var session = await OpenAsync(new SqliteConnection(Memory)))
            {
                await SetupAsync(session, "CREATE TABLE t (x INTEGER)");
                Assert.False(session.HasPendingChanges);
                Assert.Null(session.PendingText);

                var insert = await session.ExecuteAsync(Dml("INSERT INTO t VALUES (1), (2), (3)"), 0);

                Assert.Equal(SqlKind.Dml, insert.Kind);
                Assert.Equal(3, insert.RecordsAffected);
                Assert.Equal("3행 변경됨 (커밋 전)", insert.Summary);
                Assert.False(insert.TransactionEnded);
                Assert.True(session.HasPendingChanges);
                Assert.Equal(3, session.PendingRowCount);
                Assert.False(session.PendingCountUnknown);
                Assert.Equal("커밋 대기 3행", session.PendingText);

                var update = await session.ExecuteAsync(Dml("UPDATE t SET x = x + 10 WHERE x >= 2"), 0);
                Assert.Equal(2, update.RecordsAffected);
                Assert.Equal("2행 변경됨 (커밋 전)", update.Summary);
                Assert.Equal(5, session.PendingRowCount);
                Assert.Equal("커밋 대기 5행", session.PendingText);

                var none = await session.ExecuteAsync(Dml("DELETE FROM t WHERE x > 1000"), 0);
                Assert.Equal("0행 변경됨 (커밋 전)", none.Summary);
                Assert.Equal(5, session.PendingRowCount);
            }
        }

        [Fact]
        public async Task RollbackAsync_UndoesPendingChangesAndResetsState()
        {
            using (var session = await OpenAsync(new SqliteConnection(Memory)))
            {
                await SetupAsync(session, "CREATE TABLE t (x INTEGER)");
                await session.ExecuteAsync(Dml("INSERT INTO t VALUES (1), (2), (3)"), 0);
                Assert.Equal(3, await CountAsync(session, "t"));

                await session.RollbackAsync();

                Assert.Equal(0, await CountAsync(session, "t"));
                Assert.False(session.HasPendingChanges);
                Assert.Equal(0, session.PendingRowCount);
                Assert.Null(session.PendingText);
                Assert.False(session.IsBusy);
            }
        }

        [Fact]
        public async Task CommitAsync_PersistsChanges()
        {
            using (var session = await OpenAsync(new SqliteConnection(Memory)))
            {
                await SetupAsync(session, "CREATE TABLE t (x INTEGER)");
                await session.ExecuteAsync(Dml("INSERT INTO t VALUES (1), (2), (3)"), 0);

                await session.CommitAsync();

                Assert.False(session.HasPendingChanges);
                Assert.Equal(0, session.PendingRowCount);
                Assert.Null(session.PendingText);
                await session.RollbackAsync();
                Assert.Equal(3, await CountAsync(session, "t"));
            }
        }

        [Fact]
        public async Task CommitStatement_IsHandledLikeCommitAsyncWithoutSendingSql()
        {
            using (var session = await OpenAsync(new SqliteConnection(Memory)))
            {
                await SetupAsync(session, "CREATE TABLE t (x INTEGER)");
                // "COMMIT WORK"는 SQLite가 트랜잭션 안에서도 거부한다 — 아래 실행이 성공하면 SQL로 보내지 않았다는 뜻
                await AssertSqliteRejectsInTransaction(session, "COMMIT WORK");
                await session.ExecuteAsync(Dml("INSERT INTO t VALUES (1), (2)"), 0);

                var commit = await session.ExecuteAsync(Transaction("COMMIT WORK", SqlTransactionAction.Commit), 0);

                Assert.Equal(SqlKind.Transaction, commit.Kind);
                Assert.Equal("커밋함", commit.Summary);
                Assert.True(commit.TransactionEnded);
                Assert.False(session.HasPendingChanges);
                await session.RollbackAsync();
                Assert.Equal(2, await CountAsync(session, "t"));
            }
        }

        [Fact]
        public async Task RollbackStatement_IsHandledLikeRollbackAsyncWithoutSendingSql()
        {
            using (var session = await OpenAsync(new SqliteConnection(Memory)))
            {
                await SetupAsync(session, "CREATE TABLE t (x INTEGER)");
                await AssertSqliteRejectsInTransaction(session, "ROLLBACK WORK");
                await session.ExecuteAsync(Dml("INSERT INTO t VALUES (1), (2)"), 0);

                var rollback = await session.ExecuteAsync(Transaction("ROLLBACK WORK", SqlTransactionAction.Rollback), 0);

                Assert.Equal("롤백함", rollback.Summary);
                Assert.True(rollback.TransactionEnded);
                Assert.False(session.HasPendingChanges);
                Assert.Null(session.PendingText);
                Assert.Equal(0, await CountAsync(session, "t"));
            }
        }

        [Fact]
        public async Task Ddl_CommitsPendingChangesFirstAndEndsTransaction()
        {
            using (var session = await OpenAsync(new SqliteConnection(Memory)))
            {
                await SetupAsync(session, "CREATE TABLE t (x INTEGER)");
                await session.ExecuteAsync(Dml("INSERT INTO t VALUES (1), (2), (3)"), 0);

                var ddl = await session.ExecuteAsync(Ddl("CREATE TABLE u (y INTEGER)"), 0);

                Assert.Equal(SqlKind.Ddl, ddl.Kind);
                Assert.True(ddl.TransactionEnded);
                Assert.Equal("실행함 (DDL은 자동 커밋됨)", ddl.Summary);
                Assert.False(session.HasPendingChanges);
                Assert.Equal(0, session.PendingRowCount);
                // DDL 앞에서 커밋했으므로 롤백해도 INSERT는 남는다
                await session.RollbackAsync();
                Assert.Equal(3, await CountAsync(session, "t"));
                Assert.Equal(0, await CountAsync(session, "u"));
            }
        }

        [Fact]
        public async Task Ddl_WithoutPendingChanges_StillReportsTransactionEnded()
        {
            using (var session = await OpenAsync(new SqliteConnection(Memory)))
            {
                var ddl = await session.ExecuteAsync(Ddl("CREATE TABLE u (y INTEGER)"), 0);

                Assert.True(ddl.TransactionEnded);
                Assert.False(session.HasPendingChanges);
                Assert.Equal(0, await CountAsync(session, "u"));
            }
        }

        [Fact]
        public async Task PlSql_MarksPendingWithUnknownCount()
        {
            using (var session = await OpenAsync(new SqliteConnection(Memory)))
            {
                await SetupAsync(session, "CREATE TABLE t (x INTEGER)");

                // SQLite에는 PL/SQL이 없어 DML 문장을 PL/SQL 종류로 보낸다(트랜잭션 안에서 실행되는지 확인)
                var result = await session.ExecuteAsync(PlSql("INSERT INTO t VALUES (1)"), 0);

                Assert.Equal(SqlKind.PlSql, result.Kind);
                Assert.Equal("실행함 (커밋 전)", result.Summary);
                Assert.True(session.HasPendingChanges);
                Assert.True(session.PendingCountUnknown);
                Assert.Equal("커밋 대기(행 수 모름)", session.PendingText);

                await session.RollbackAsync();

                Assert.Equal(0, await CountAsync(session, "t"));
                Assert.False(session.PendingCountUnknown);
                Assert.Null(session.PendingText);
            }
        }

        [Fact]
        public async Task Savepoint_RunsInsideSessionTransaction_RollbackToMakesCountUnknown()
        {
            using (var session = await OpenAsync(new SqliteConnection(Memory)))
            {
                await SetupAsync(session, "CREATE TABLE t (x INTEGER)");

                // 트랜잭션이 없을 때의 SAVEPOINT가 세션 트랜잭션을 열어야 뒤의 DML이 같은 트랜잭션에 이어진다
                var savepoint = await session.ExecuteAsync(Transaction("SAVEPOINT a", SqlTransactionAction.Other), 0);
                Assert.Equal("실행함", savepoint.Summary);
                Assert.False(savepoint.TransactionEnded);
                await session.ExecuteAsync(Dml("INSERT INTO t VALUES (1)"), 0);
                await session.ExecuteAsync(Transaction("SAVEPOINT b", SqlTransactionAction.Other), 0);
                await session.ExecuteAsync(Dml("INSERT INTO t VALUES (2), (3)"), 0);
                Assert.Equal(3, session.PendingRowCount);
                Assert.False(session.PendingCountUnknown);

                await session.ExecuteAsync(Transaction("ROLLBACK TO b", SqlTransactionAction.Other), 0);

                Assert.True(session.HasPendingChanges);
                Assert.True(session.PendingCountUnknown);
                Assert.Equal("커밋 대기(행 수 모름)", session.PendingText);
                Assert.Equal(1, await CountAsync(session, "t"));

                await session.CommitAsync();
                await session.RollbackAsync();
                Assert.Equal(1, await CountAsync(session, "t"));
                Assert.False(session.PendingCountUnknown);
            }
        }

        [Fact]
        public async Task Other_RunsInsideOpenTransactionWithoutChangingPendingState()
        {
            using (var session = await OpenAsync(new SqliteConnection(Memory)))
            {
                await SetupAsync(session, "CREATE TABLE t (x INTEGER)");

                var pragma = await session.ExecuteAsync(Other("PRAGMA user_version = 3"), 0);
                Assert.Equal(SqlKind.Other, pragma.Kind);
                Assert.Equal("실행함", pragma.Summary);
                Assert.False(session.HasPendingChanges);

                await session.ExecuteAsync(Dml("INSERT INTO t VALUES (1)"), 0);
                // 열린 트랜잭션 안에서 실행되므로 롤백하면 함께 되돌아간다
                await session.ExecuteAsync(Other("INSERT INTO t VALUES (2)"), 0);
                Assert.Equal(1, session.PendingRowCount);
                Assert.Equal(2, await CountAsync(session, "t"));

                await session.RollbackAsync();
                Assert.Equal(0, await CountAsync(session, "t"));
            }
        }

        [Fact]
        public async Task Commands_GetSessionTransaction_EvenWhenProviderDoesNotAssignIt()
        {
            // SqliteConnection.CreateCommand()는 현재 트랜잭션을 명령에 넣어 주므로, 넣어 주지 않는 연결로 감싸 세션이 직접 넣는지 본다
            using (var session = await OpenAsync(new PlainCommandConnection(new SqliteConnection(Memory))))
            {
                await SetupAsync(session, "CREATE TABLE t (x INTEGER)");

                await session.ExecuteAsync(Dml("INSERT INTO t VALUES (1)"), 0);
                await session.ExecuteAsync(Dml("INSERT INTO t VALUES (2)"), 0);
                await session.ExecuteAsync(Other("INSERT INTO t VALUES (3)"), 0);
                var query = await session.ExecuteAsync(Query("SELECT x FROM t ORDER BY x"), 10);

                Assert.Equal(new[] { "1", "2", "3" }, query.Rows.Select(r => r[0]));
                await session.RollbackAsync();
                Assert.Equal(0, await CountAsync(session, "t"));
            }
        }

        [Fact]
        public async Task ForUpdate_MarksPending_TransactionEndClosesOnlyForUpdateCursors()
        {
            using (var session = await OpenAsync(new SqliteConnection(Memory)))
            {
                await CreateNumbersAsync(session, 100);
                var plain = await session.ExecuteAsync(Query("SELECT x FROM nums ORDER BY x"), 10);
                Assert.False(session.HasPendingChanges);

                // SQLite에는 FOR UPDATE가 없어 표시만 켠 SELECT로 시험한다
                var locked = await session.ExecuteAsync(Query("SELECT x FROM nums ORDER BY x DESC", forUpdate: true), 10);

                Assert.True(session.HasPendingChanges);
                Assert.True(locked.Cursor.HasMore);

                await session.CommitAsync();

                Assert.False(session.HasPendingChanges);
                Assert.True(locked.Cursor.IsClosed);
                Assert.False(locked.Cursor.HasMore);
                Assert.Empty(await session.FetchAsync(locked.Cursor, 10));
                // 보통 커서는 커밋 뒤에도 이어 읽는다
                Assert.False(plain.Cursor.IsClosed);
                var next = await session.FetchAsync(plain.Cursor, 10);
                Assert.Equal(Enumerable.Range(11, 10).Select(Text), next.Select(r => r[0]));
            }
        }

        [Fact]
        public async Task Busy_WhileSlowQueryRuns_OtherCallsThrowSessionBusyWithoutWaiting()
        {
            var connection = new SqliteConnection(Memory);
            var slow = new SlowFunction(connection);
            using (var session = await OpenAsync(connection))
            {
                await CreateNumbersAsync(session, 50);
                var open = await session.ExecuteAsync(Query("SELECT x FROM nums ORDER BY x"), 10);

                var running = session.ExecuteAsync(Query("SELECT sleep_ms(10000)"), 10);
                try
                {
                    slow.WaitStarted();
                    Assert.True(session.IsBusy);
                    await Assert.ThrowsAsync<SessionBusyException>(() => session.ExecuteAsync(Query("SELECT 1"), 10));
                    await Assert.ThrowsAsync<SessionBusyException>(() => session.FetchAsync(open.Cursor, 10));
                    await Assert.ThrowsAsync<SessionBusyException>(() => session.CommitAsync());
                    await Assert.ThrowsAsync<SessionBusyException>(() => session.RollbackAsync());
                    await Assert.ThrowsAsync<SessionBusyException>(() => session.CloseCursorAsync(open.Cursor));
                    Assert.False(running.IsCompleted);
                }
                finally
                {
                    slow.Release();
                }

                Assert.Equal("10000", Assert.Single((await Within(running)).Rows)[0]);
                Assert.False(session.IsBusy);
                // 거절된 가져오기는 커서를 건드리지 않았다
                Assert.False(open.Cursor.IsClosed);
                var next = await session.FetchAsync(open.Cursor, 10);
                Assert.Equal(Enumerable.Range(11, 10).Select(Text), next.Select(r => r[0]));
            }
        }

        [Fact]
        public async Task RunAsync_WhileSlowQueryRuns_WaitsThenSucceeds()
        {
            var connection = new SqliteConnection(Memory);
            var slow = new SlowFunction(connection);
            using (var session = await OpenAsync(connection))
            {
                var running = session.ExecuteAsync(Query("SELECT sleep_ms(10000)"), 10);
                Task<long> metadata;
                try
                {
                    slow.WaitStarted();
                    metadata = session.RunAsync((c, tx) => Scalar(c, tx, "SELECT 40 + 2"));
                    await Task.Delay(100);
                    Assert.False(metadata.IsCompleted);
                }
                finally
                {
                    slow.Release();
                }

                await Within(running);
                Assert.Equal(42, await Within(metadata));
                Assert.False(session.IsBusy);
            }
        }

        [Fact]
        public async Task RunAsync_WaitForLock_CanBeCanceled()
        {
            var connection = new SqliteConnection(Memory);
            var slow = new SlowFunction(connection);
            using (var session = await OpenAsync(connection))
            using (var cancel = new CancellationTokenSource())
            {
                var running = session.ExecuteAsync(Query("SELECT sleep_ms(10000)"), 10);
                try
                {
                    slow.WaitStarted();
                    var waiting = session.RunAsync((c, tx) => 1, cancel.Token);

                    cancel.Cancel();

                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Within(waiting));
                }
                finally
                {
                    slow.Release();
                }

                await Within(running);
                // 취소된 대기가 잠금을 풀어 버리지 않았는지: 다음 작업이 정상으로 잡고 놓는다
                Assert.Equal(1, await Within(session.RunAsync((c, tx) => 1)));
                Assert.False(session.IsBusy);
            }
        }

        [Fact]
        public async Task TwoOpenCursors_OnSameSession_FetchIndependently()
        {
            using (var session = await OpenAsync(new SqliteConnection(Memory)))
            {
                await CreateNumbersAsync(session, 500);

                var a = await session.ExecuteAsync(Query("SELECT x FROM nums ORDER BY x"), 100);
                var b = await session.ExecuteAsync(Query("SELECT x * 10 FROM nums WHERE x <= 300 ORDER BY x DESC"), 50);

                // 새 실행은 다른 탭의 커서를 닫지 않는다
                Assert.False(a.Cursor.IsClosed);
                var rowsA = a.Rows.Select(r => r[0]).ToList();
                var rowsB = b.Rows.Select(r => r[0]).ToList();
                var rounds = 0;
                while (a.Cursor.HasMore || b.Cursor.HasMore)
                {
                    rowsA.AddRange((await session.FetchAsync(a.Cursor, 70)).Select(r => r[0]));
                    rowsB.AddRange((await session.FetchAsync(b.Cursor, 45)).Select(r => r[0]));
                    Assert.True(++rounds < 100, "커서가 끝나지 않습니다.");
                }

                Assert.Equal(Enumerable.Range(1, 500).Select(Text), rowsA);
                Assert.Equal(Enumerable.Range(1, 300).Reverse().Select(i => Text(i * 10)), rowsB);
                Assert.True(a.Cursor.IsClosed);
                Assert.True(b.Cursor.IsClosed);
                Assert.Equal(500, a.Cursor.Fetched);
                Assert.Equal(300, b.Cursor.Fetched);
            }
        }

        [Fact]
        public async Task Dml_WhileCursorIsOpen_DoesNotDisturbCursor()
        {
            using (var session = await OpenAsync(new SqliteConnection(Memory)))
            {
                await CreateNumbersAsync(session, 30);
                await SetupAsync(session, "CREATE TABLE t (x INTEGER)");
                var open = await session.ExecuteAsync(Query("SELECT x FROM nums ORDER BY x"), 10);

                await session.ExecuteAsync(Dml("INSERT INTO t VALUES (1)"), 0);
                await session.RollbackAsync();

                Assert.False(open.Cursor.IsClosed);
                var rest = await session.FetchAsync(open.Cursor, 100);
                Assert.Equal(Enumerable.Range(11, 20).Select(Text), rest.Select(r => r[0]));
            }
        }

        [Fact]
        public async Task CloseAsyncWithoutCommit_RollsBackPendingChanges()
        {
            var connectionString = SharedMemory();
            using (var keeper = new SqliteConnection(connectionString))
            {
                // 같은 메모리 DB를 보는 두 번째 연결. 세션이 닫혀도 DB가 사라지지 않게 열어 둔다.
                keeper.Open();
                Exec(keeper, null, "CREATE TABLE t (x INTEGER)");
                var session = await OpenAsync(new SqliteConnection(connectionString));
                await session.ExecuteAsync(Dml("INSERT INTO t VALUES (1), (2)"), 0);
                var open = await session.ExecuteAsync(Query("SELECT 1 UNION ALL SELECT 2"), 1);
                Assert.True(open.Cursor.HasMore);

                await Within(session.CloseAsync(false));

                Assert.False(session.IsOpen);
                Assert.False(session.HasPendingChanges);
                Assert.True(open.Cursor.IsClosed);
                Assert.Equal(0, Scalar(keeper, null, "SELECT COUNT(*) FROM t"));
                await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync(Query("SELECT 1"), 1));
                session.Dispose();
            }
        }

        [Fact]
        public async Task CloseAsyncWithCommit_CommitsPendingChanges()
        {
            var connectionString = SharedMemory();
            using (var keeper = new SqliteConnection(connectionString))
            {
                keeper.Open();
                Exec(keeper, null, "CREATE TABLE t (x INTEGER)");
                var session = await OpenAsync(new SqliteConnection(connectionString));
                await session.ExecuteAsync(Dml("INSERT INTO t VALUES (1), (2)"), 0);

                await Within(session.CloseAsync(true));

                Assert.False(session.IsOpen);
                Assert.Equal(2, Scalar(keeper, null, "SELECT COUNT(*) FROM t"));
                // 이미 닫힌 세션: 아무것도 안 함
                await session.CloseAsync(false);
                session.Dispose();
            }
        }

        [Fact]
        public async Task CloseAsync_WhileQueryRuns_WaitsForItToFinish()
        {
            var connection = new SqliteConnection(Memory);
            var slow = new SlowFunction(connection);
            var session = await OpenAsync(connection);
            var running = session.ExecuteAsync(Query("SELECT sleep_ms(10000)"), 10);
            Task closing;
            try
            {
                slow.WaitStarted();
                // SQLite는 Cancel이 동작하지 않아 실행이 끝날 때까지 닫기를 기다린다
                closing = session.CloseAsync(false);
                await Task.Delay(100);
                Assert.False(closing.IsCompleted);
                Assert.True(session.IsOpen);
            }
            finally
            {
                slow.Release();
            }

            Assert.Equal("10000", Assert.Single((await Within(running)).Rows)[0]);
            await Within(closing);
            Assert.False(session.IsOpen);
            session.Dispose();
        }

        [Fact]
        public async Task Dispose_Twice_IsSafe_AndRollsBackAndClosesCursors()
        {
            var connectionString = SharedMemory();
            using (var keeper = new SqliteConnection(connectionString))
            {
                keeper.Open();
                Exec(keeper, null, "CREATE TABLE t (x INTEGER)");
                var session = await OpenAsync(new SqliteConnection(connectionString));
                await session.ExecuteAsync(Dml("INSERT INTO t VALUES (1)"), 0);
                var open = await session.ExecuteAsync(Query("SELECT 1 UNION ALL SELECT 2"), 1);

                session.Dispose();
                session.Dispose();

                Assert.False(session.IsOpen);
                Assert.False(session.HasPendingChanges);
                Assert.True(open.Cursor.IsClosed);
                Assert.Equal(0, Scalar(keeper, null, "SELECT COUNT(*) FROM t"));
                await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync(Query("SELECT 1"), 1));
                await session.CloseAsync(true);
            }
        }

        [Fact]
        public async Task Dispose_WhenWorkHoldsLockTooLong_GivesUpWaitingAndCloses()
        {
            var session = await OpenAsync(new SqliteConnection(Memory));
            using (var entered = new ManualResetEventSlim())
            using (var gate = new ManualResetEventSlim())
            {
                // 연결을 쓰지 않고 잠금만 오래 쥐는 작업(Cancel로 멈출 수 없음)
                var work = session.RunAsync((c, tx) =>
                {
                    entered.Set();
                    gate.Wait(Timeout);
                    return 7;
                });
                Assert.True(entered.Wait(Timeout));

                var watch = Stopwatch.StartNew();
                session.Dispose();
                watch.Stop();

                Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(1.5), "잠금을 기다리지 않았습니다: " + watch.Elapsed);
                Assert.True(watch.Elapsed < Timeout, "잠금을 너무 오래 기다렸습니다: " + watch.Elapsed);
                Assert.False(session.IsOpen);
                gate.Set();
                Assert.Equal(7, await Within(work));
                session.Dispose();
            }
        }

        [Fact]
        public async Task Cancel_WhenIdle_DoesNothing()
        {
            var session = new DbSession(new SqliteConnection(Memory));
            session.Cancel();
            await session.OpenAsync();

            session.Cancel();
            session.Cancel();

            var result = await session.ExecuteAsync(Query("SELECT 1"), 10);
            Assert.Equal("1", Assert.Single(result.Rows)[0]);
            Assert.False(session.IsBroken);
            session.Dispose();
        }

        [Fact]
        public async Task BeforeOpen_OperationsThrowInvalidOperation()
        {
            var session = new DbSession(new SqliteConnection(Memory));

            Assert.False(session.IsOpen);
            Assert.False(session.IsBusy);
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync(Query("SELECT 1"), 10));
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.CommitAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.RunAsync((c, tx) => 1));
            Assert.False(session.IsBusy);

            await session.OpenAsync();
            Assert.True(session.IsOpen);
            session.Dispose();
        }

        [Fact]
        public async Task Execute_InvalidArguments_Throw()
        {
            using (var session = await OpenAsync(new SqliteConnection(Memory)))
            {
                await Assert.ThrowsAsync<ArgumentNullException>(() => session.ExecuteAsync(null, 10));
                await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.ExecuteAsync(Query("SELECT 1"), -1));
                await Assert.ThrowsAsync<ArgumentException>(() => session.ExecuteAsync(Dml("  "), 10));
                Assert.False(session.IsBusy);
            }
        }

        [Fact]
        public async Task QueryError_LeavesSessionUsable()
        {
            using (var session = await OpenAsync(new SqliteConnection(Memory)))
            {
                var error = await Assert.ThrowsAsync<SqliteException>(() => session.ExecuteAsync(Query("SELECT * FROM missing_table"), 10));

                Assert.False(session.IsBusy);
                Assert.False(session.IsBroken);
                Assert.True(session.IsOpen);
                var message = DbSession.DescribeError(error);
                Assert.Contains("missing_table", message);
                Assert.DoesNotContain("\n", message);
                Assert.Equal("1", Assert.Single((await session.ExecuteAsync(Query("SELECT 1"), 10)).Rows)[0]);
            }
        }

        [Fact]
        public async Task FailedDml_KeepsEarlierPendingChanges()
        {
            using (var session = await OpenAsync(new SqliteConnection(Memory)))
            {
                await SetupAsync(session, "CREATE TABLE t (x INTEGER NOT NULL)");
                await session.ExecuteAsync(Dml("INSERT INTO t VALUES (1)"), 0);

                await Assert.ThrowsAsync<SqliteException>(() => session.ExecuteAsync(Dml("INSERT INTO t VALUES (NULL)"), 0));

                Assert.True(session.HasPendingChanges);
                Assert.Equal(1, session.PendingRowCount);
                await session.CommitAsync();
                Assert.Equal(1, await CountAsync(session, "t"));
            }
        }

        [Fact]
        public async Task PreCanceledToken_ThrowsWithoutTouchingSessionOrCursor()
        {
            using (var session = await OpenAsync(new SqliteConnection(Memory)))
            using (var cancel = new CancellationTokenSource())
            {
                await CreateNumbersAsync(session, 20);
                var open = await session.ExecuteAsync(Query("SELECT x FROM nums ORDER BY x"), 5);
                cancel.Cancel();

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ExecuteAsync(Query("SELECT 1"), 10, cancel.Token));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.FetchAsync(open.Cursor, 5, cancel.Token));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.RunAsync((c, tx) => 1, cancel.Token));

                Assert.False(session.IsBusy);
                Assert.False(open.Cursor.IsClosed);
                var next = await session.FetchAsync(open.Cursor, 5);
                Assert.Equal(Enumerable.Range(6, 5).Select(Text), next.Select(r => r[0]));
            }
        }

        [Fact]
        public async Task CursorFromAnotherSession_IsRejected()
        {
            using (var first = await OpenAsync(new SqliteConnection(Memory)))
            using (var second = await OpenAsync(new SqliteConnection(Memory)))
            {
                var open = await first.ExecuteAsync(Query("SELECT 1 UNION ALL SELECT 2"), 1);

                await Assert.ThrowsAsync<ArgumentException>(() => second.FetchAsync(open.Cursor, 1));
                await Assert.ThrowsAsync<ArgumentException>(() => second.CloseCursorAsync(open.Cursor));

                Assert.False(open.Cursor.IsClosed);
                Assert.Equal("2", Assert.Single(await first.FetchAsync(open.Cursor, 1))[0]);
            }
        }

        [Fact]
        public async Task RunAsync_PassesCurrentTransactionOrNull()
        {
            using (var session = await OpenAsync(new SqliteConnection(Memory)))
            {
                await SetupAsync(session, "CREATE TABLE t (x INTEGER)");
                Assert.True(await session.RunAsync((c, tx) => tx == null));

                await session.ExecuteAsync(Dml("INSERT INTO t VALUES (1)"), 0);

                // 같은 연결·트랜잭션이라 커밋 전 변경도 보인다
                var seen = await session.RunAsync((c, tx) =>
                {
                    Assert.NotNull(tx);
                    return Scalar(c, tx, "SELECT COUNT(*) FROM t");
                });
                Assert.Equal(1, seen);
                await session.RollbackAsync();
                Assert.True(await session.RunAsync((c, tx) => tx == null));
            }
        }

        [Fact]
        public async Task RunAsync_WorkThrows_ExceptionPropagatesAndLockIsReleased()
        {
            using (var session = await OpenAsync(new SqliteConnection(Memory)))
            {
                await Assert.ThrowsAsync<SqliteException>(() => session.RunAsync((c, tx) => Scalar(c, tx, "SELECT * FROM missing")));

                Assert.False(session.IsBusy);
                Assert.False(session.IsBroken);
                Assert.Equal(1, await session.RunAsync((c, tx) => Scalar(c, tx, "SELECT 1")));
            }
        }

        [Fact]
        public async Task ConnectionClosedUnderneath_MarksSessionBroken()
        {
            var connection = new SqliteConnection(Memory);
            using (var session = await OpenAsync(connection))
            {
                // 서버가 연결을 끊은 상황
                connection.Close();

                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync(Query("SELECT 1"), 10));

                Assert.True(session.IsBroken);
                Assert.False(session.IsOpen);
                Assert.False(session.IsBusy);
                Assert.Equal("DB 연결이 끊겼습니다. 다시 연결하세요.", DbSession.DescribeError(error));
            }
        }

        [Fact]
        public async Task CloseAsyncWithoutCommit_OnBrokenConnection_IgnoresRollbackFailure()
        {
            var connection = new SqliteConnection(Memory);
            var session = await OpenAsync(connection);
            await SetupAsync(session, "CREATE TABLE t (x INTEGER)");
            await session.ExecuteAsync(Dml("INSERT INTO t VALUES (1)"), 0);
            var open = await session.ExecuteAsync(Query("SELECT 1 UNION ALL SELECT 2"), 1);
            connection.Close();

            await Within(session.CloseAsync(false));

            Assert.True(session.IsBroken);
            Assert.False(session.IsOpen);
            Assert.False(session.HasPendingChanges);
            Assert.True(open.Cursor.IsClosed);
            session.Dispose();
        }

        [Fact]
        public async Task CloseAsyncWithCommit_OnBrokenConnection_ClosesAndReportsUnsavedChanges()
        {
            var connection = new SqliteConnection(Memory);
            var session = await OpenAsync(connection);
            await SetupAsync(session, "CREATE TABLE t (x INTEGER)");
            await session.ExecuteAsync(Dml("INSERT INTO t VALUES (1)"), 0);
            connection.Close();

            // 커밋하지 못한 변경은 조용히 버리지 않고 알린다. 세션은 그래도 닫힌다.
            await Assert.ThrowsAsync<InvalidOperationException>(() => Within(session.CloseAsync(true)));

            Assert.True(session.IsBroken);
            Assert.False(session.IsOpen);
            Assert.False(session.HasPendingChanges);
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync(Query("SELECT 1"), 1));
            await session.CloseAsync(true);
            session.Dispose();
        }

        [Fact]
        public void Cancellation_OperationAndTaskCanceled()
        {
            Assert.True(DbSession.IsCancellation(new OperationCanceledException()));
            Assert.True(DbSession.IsCancellation(new TaskCanceledException()));
            Assert.Equal("실행을 취소했습니다.", DbSession.DescribeError(new OperationCanceledException()));
            Assert.Equal("실행을 취소했습니다.", DbSession.DescribeError(new TaskCanceledException()));
        }

        [Fact]
        public void Cancellation_WrappedInAggregateException()
        {
            var nested = new AggregateException(new AggregateException(new TaskCanceledException()));

            Assert.True(DbSession.IsCancellation(nested));
            Assert.Equal("실행을 취소했습니다.", DbSession.DescribeError(nested));
            Assert.True(DbSession.IsCancellation(new AggregateException(new OperationCanceledException("작업 취소"))));
        }

        [Fact]
        public void Cancellation_MessageWithOra01013()
        {
            var error = new InvalidOperationException("ORA-01013: user requested cancel of current operation");

            Assert.True(DbSession.IsCancellation(error));
            Assert.Equal("실행을 취소했습니다.", DbSession.DescribeError(error));
            Assert.True(DbSession.IsCancellation(new Exception("실행 실패", new Exception("ORA-01013: user requested cancel of current operation"))));
            Assert.True(DbSession.IsCancellation(new AggregateException(new Exception("ORA-01013: user requested cancel of current operation"))));
        }

        [Fact]
        public void Cancellation_OracleExceptionNumber1013()
        {
            var error = NewOracleException(1013, "ORA-01013: user requested cancel of current operation");

            Assert.True(DbSession.IsCancellation(error));
            Assert.Equal("실행을 취소했습니다.", DbSession.DescribeError(new AggregateException(error)));
        }

        [Fact]
        public void IsCancellation_OtherErrors_False()
        {
            Assert.False(DbSession.IsCancellation(null));
            Assert.False(DbSession.IsCancellation(new InvalidOperationException("ORA-00942: table or view does not exist")));
            Assert.False(DbSession.IsCancellation(new SessionBusyException()));
            Assert.False(DbSession.IsCancellation(new AggregateException(new TimeoutException("시간 초과"))));
        }

        [Fact]
        public void DescribeError_ReturnsFirstLineAndUnwraps()
        {
            Assert.Equal("첫 줄", DbSession.DescribeError(new Exception("첫 줄\r\n둘째 줄")));
            Assert.Equal("속 오류", DbSession.DescribeError(new AggregateException(new TargetInvocationException(new InvalidOperationException("속 오류")))));
            Assert.Equal("알 수 없는 오류가 발생했습니다.", DbSession.DescribeError(null));
            Assert.Equal(new SessionBusyException().Message, DbSession.DescribeError(new SessionBusyException()));
        }

        [Fact]
        public void DescribeError_OracleError_SkipsHelpUrlLine()
        {
            var error = NewOracleException(942, "ORA-00942: table or view does not exist");

            Assert.Contains("https://", error.Message);
            Assert.Equal("ORA-00942: table or view does not exist", DbSession.DescribeError(error));
        }

        [Fact]
        public void DescribeError_OracleMessageWithoutCode_GetsCodePrefix()
        {
            var error = NewOracleException(1, "unique constraint (SCOTT.PK_EMP) violated");

            Assert.Equal("ORA-00001: unique constraint (SCOTT.PK_EMP) violated", DbSession.DescribeError(error));
        }

        [Fact]
        public void DescribeError_PlSqlCompileError_JoinsCauseLine()
        {
            var error = NewOracleException(6550,
                "ORA-06550: line 1, column 7:\nPLS-00201: identifier 'NO_SUCH_PROC' must be declared\nORA-06550: line 1, column 7:\nPL/SQL: Statement ignored");

            Assert.Equal("ORA-06550: line 1, column 7: PLS-00201: identifier 'NO_SUCH_PROC' must be declared", DbSession.DescribeError(error));
        }

        [Fact]
        public void DescribeError_ConnectFailure_PutsInnerCauseFirst()
        {
            // ODP.NET 23의 실제 모양(Oracle 12.1 실측): OracleException ORA-50201 ← NetworkException ORA-50201 ← NetworkException ORA-12514
            const string generic = "ORA-50201: Oracle Communication: Failed to connect to server or failed to parse connect string";
            var error = new Exception(generic + "\nhttps://docs.oracle.com/error-help/db/ora-50201/",
                new Exception(generic, new Exception("ORA-12514: TNS:listener does not currently know of service requested in connect descriptor")));

            Assert.Equal("ORA-12514: TNS:listener does not currently know of service requested in connect descriptor (ORA-50201)", DbSession.DescribeError(error));
            Assert.Equal("ORA-12541: TNS:no listener (ORA-50201)",
                DbSession.DescribeError(new AggregateException(new Exception(generic, new Exception("ORA-12541: TNS:no listener")))));
        }

        [Fact]
        public void DescribeError_InnerWithoutDifferentCode_KeepsOuterLine()
        {
            // 안쪽이 같은 번호이거나 번호가 없으면 겉 줄 그대로
            Assert.Equal("ORA-12154: TNS:could not resolve the connect identifier specified",
                DbSession.DescribeError(new Exception("ORA-12154: TNS:could not resolve the connect identifier specified",
                    new Exception("ORA-12154: TNS:could not resolve the connect identifier specified"))));
            Assert.Equal("ORA-50201: failed", DbSession.DescribeError(new Exception("ORA-50201: failed", new System.Net.Sockets.SocketException(10061))));
            // 겉 줄에 번호가 없으면 안쪽 번호를 끌어오지 않는다
            Assert.Equal("저장하지 못했습니다", DbSession.DescribeError(new Exception("저장하지 못했습니다", new Exception("ORA-00001: unique constraint"))));
            // "ORA-1234x"처럼 번호가 다섯 자리가 아니면 번호로 보지 않는다
            Assert.Equal("ORA-50201: failed", DbSession.DescribeError(new Exception("ORA-50201: failed", new Exception("ORA-1234x: odd"))));
        }

        [Fact]
        public void CreateOracle_UsesInBandBreakForCancel()
        {
            // OOB(TCP 긴급 데이터) 취소는 Docker·NAT를 지나면 서버에 닿지 않는다 — 세션을 만들 때 in-band로 바꾼다
            using (DbSession.CreateOracle("Data Source=localhost:1521/xe;User Id=u;Password=p"))
            {
                Assert.True(OracleConfiguration.DisableOOB);
            }
        }

        [Theory]
        [InlineData(3113, "ORA-03113")]
        [InlineData(3135, "ORA-03135")]
        [InlineData(12570, "ORA-12570")]
        [InlineData(28, "ORA-00028")]
        [InlineData(2396, "ORA-02396")]
        [InlineData(2399, "ORA-02399")]
        [InlineData(2392, "ORA-02392")]
        [InlineData(1092, "ORA-01092")]
        [InlineData(603, "ORA-00603")]
        public void DescribeError_BrokenConnectionNumbers(int number, string code)
        {
            var error = NewOracleException(number, code + ": connection lost");

            Assert.True(DbSession.IsBrokenError(error));
            Assert.Equal("DB 연결이 끊겼습니다. 다시 연결하세요. (" + code + ")", DbSession.DescribeError(new AggregateException(error)));
            Assert.False(DbSession.IsCancellation(error));
        }

        [Fact]
        public void DescribeError_DbLinkErrorMentioningBrokenNumber_IsNotBroken()
        {
            // 원격 DB 링크가 끊긴 것이지 이 연결이 끊긴 것이 아니다
            var error = NewOracleException(2068, "ORA-02068: following severe error from REMOTE_LINK\nORA-03113: end-of-file on communication channel");

            Assert.False(DbSession.IsBrokenError(error));
            Assert.Equal("ORA-02068: following severe error from REMOTE_LINK", DbSession.DescribeError(error));
        }

        [Fact]
        public void DescribeError_ClosedConnectionInvalidOperation_IsBroken()
        {
            var error = new InvalidOperationException("ORA-50001: Connection must be open for this operation");

            Assert.True(DbSession.IsBrokenError(error));
            Assert.Equal("DB 연결이 끊겼습니다. 다시 연결하세요.", DbSession.DescribeError(error));
            Assert.False(DbSession.IsBrokenError(new InvalidOperationException("다른 문제")));
            Assert.False(DbSession.IsBrokenError(new SessionBusyException()));
        }

        [Theory]
        [InlineData("Decimal", 38, 127, "NUMBER", null, null)]
        [InlineData("Decimal", 0, -127, "NUMBER", null, null)]
        [InlineData("Decimal", 7, 2, "NUMBER", 7, 2)]
        [InlineData("Int16", 4, 0, "NUMBER", 4, 0)]
        [InlineData("Varchar2", null, null, "VARCHAR2", null, null)]
        [InlineData("TimeStampTZ", null, 6, "TIMESTAMP WITH TIME ZONE", null, 6)]
        [InlineData("BinaryDouble", null, null, "BINARY_DOUBLE", null, null)]
        [InlineData("Vector", null, null, "Vector", null, null)]
        public void NormalizeOracleColumn_MapsProviderTypeNames(string providerName, int? precision, int? scale, string expected, int? expectedPrecision, int? expectedScale)
        {
            var p = precision;
            var s = scale;

            Assert.Equal(expected, DbSession.NormalizeOracleColumn(providerName, false, ref p, ref s));
            Assert.Equal(expectedPrecision, p);
            Assert.Equal(expectedScale, s);
        }

        [Fact]
        public void NormalizeOracleColumn_RowId()
        {
            int? p = null;
            int? s = null;

            Assert.Equal("ROWID", DbSession.NormalizeOracleColumn("Varchar2", true, ref p, ref s));
        }

        [Fact]
        public void SchemaInt_ReadsInt16AndBooleanSchemaValues()
        {
            // ODP.NET·SQLite 스키마 표의 정밀도·소수 자릿수는 Int16이다(GetColumnSchema()는 이 값을 버린다)
            var table = new DataTable();
            table.Columns.Add(SchemaTableColumn.NumericPrecision, typeof(short));
            table.Columns.Add(SchemaTableColumn.NumericScale, typeof(short));
            table.Columns.Add(SchemaTableColumn.ColumnSize, typeof(int));
            table.Columns.Add("IsRowID", typeof(bool));
            table.Rows.Add((short)7, DBNull.Value, 22, true);
            var row = table.Rows[0];

            Assert.Equal(7, DbSession.SchemaInt(row, SchemaTableColumn.NumericPrecision));
            Assert.Null(DbSession.SchemaInt(row, SchemaTableColumn.NumericScale));
            Assert.Equal(22, DbSession.SchemaInt(row, SchemaTableColumn.ColumnSize));
            Assert.Equal(1, DbSession.SchemaInt(row, "IsRowID"));
            Assert.Null(DbSession.SchemaInt(row, "Missing"));
            Assert.Null(DbSession.SchemaInt(null, SchemaTableColumn.ColumnSize));
        }

        [Fact]
        public async Task QueryAsync_BindsNamedParameters_AndReadsRows()
        {
            using (var session = await OpenAsync(new SqliteConnection(Memory)))
            {
                await CreateNumbersAsync(session, 20);
                var query = new SqlQuery { Sql = "SELECT x FROM nums WHERE x > :low AND x <= :high ORDER BY x" };
                query.Parameters.Add(new KeyValuePair<string, object>("low", 5));
                query.Parameters.Add(new KeyValuePair<string, object>("high", 8));

                var rows = await session.QueryAsync(query, r => Convert.ToInt32(r.GetValue(0), CultureInfo.InvariantCulture));

                Assert.Equal(new[] { 6, 7, 8 }, rows);
                Assert.False(session.IsBusy);
            }
        }

        [Fact]
        public async Task QueryAsync_NullParameterBindsDbNull_EmptyResultIsEmptyList()
        {
            using (var session = await OpenAsync(new SqliteConnection(Memory)))
            {
                await CreateNumbersAsync(session, 3);
                var query = new SqlQuery { Sql = "SELECT x FROM nums WHERE x = :v" };
                query.Parameters.Add(new KeyValuePair<string, object>("v", null));

                var rows = await session.QueryAsync(query, r => r.GetValue(0));

                Assert.Empty(rows);
            }
        }

        [Fact]
        public async Task QueryAsync_CancelledToken_ThrowsAndReleasesLock()
        {
            using (var session = await OpenAsync(new SqliteConnection(Memory)))
            {
                await CreateNumbersAsync(session, 3);
                var query = new SqlQuery { Sql = "SELECT x FROM nums" };
                using (var cts = new CancellationTokenSource())
                {
                    cts.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.QueryAsync(query, r => r.GetValue(0), cts.Token));
                }

                Assert.False(session.IsBusy);
                Assert.Equal(3, (await session.QueryAsync(query, r => r.GetValue(0))).Count);
            }
        }

        // ---- 커밋·롤백 실패(ODP.NET은 실패해도 트랜잭션 객체를 끝내 버린다) ----

        [Fact]
        public async Task CommitFailure_ServerRolledBack_EndsPendingAndLaterDmlCanStillBeRolledBack()
        {
            var odp = new OdpLikeConnection(new SqliteConnection(Memory));
            using (var session = await OpenAsync(odp))
            {
                session.TransactionProbeSql = OdpLikeConnection.ProbeSql;
                await SetupAsync(session, "CREATE TABLE t (x INTEGER)");
                await session.ExecuteAsync(Dml("INSERT INTO t VALUES (1), (2)"), 0);
                odp.FailNextCommit = CommitFailure.RolledBack;

                var error = await Assert.ThrowsAsync<TransactionEndedException>(() => session.CommitAsync());

                var message = DbSession.DescribeError(error);
                Assert.StartsWith("ORA-02091: transaction rolled back ORA-02291:", message);
                Assert.Contains("커밋 대기 변경이 남아 있지 않습니다", message);
                Assert.False(session.HasPendingChanges);
                Assert.Equal(0, await CountAsync(session, "t"));
                // 끝난 트랜잭션 객체를 쥐고 있으면 다음 DML이 트랜잭션 없이(자동 커밋) 실행되고 롤백도 실패한다
                await session.ExecuteAsync(Dml("INSERT INTO t VALUES (3)"), 0);
                Assert.Equal("커밋 대기 1행", session.PendingText);
                await session.RollbackAsync();
                Assert.Equal(0, await CountAsync(session, "t"));
            }
        }

        [Fact]
        public async Task CommitFailure_ServerKeptTransaction_KeepsPendingUnderNewTransaction()
        {
            var odp = new OdpLikeConnection(new SqliteConnection(Memory));
            using (var session = await OpenAsync(odp))
            {
                session.TransactionProbeSql = OdpLikeConnection.ProbeSql;
                await SetupAsync(session, "CREATE TABLE t (x INTEGER)");
                await session.ExecuteAsync(Dml("INSERT INTO t VALUES (1), (2)"), 0);
                odp.FailNextCommit = CommitFailure.KeptOpen;

                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.CommitAsync());

                Assert.Contains("ORA-02049", error.Message);
                Assert.False(session.IsBroken);
                Assert.Equal("커밋 대기 2행", session.PendingText);
                await session.ExecuteAsync(Dml("INSERT INTO t VALUES (3)"), 0);
                await session.RollbackAsync();
                Assert.False(session.HasPendingChanges);
                Assert.Equal(0, await CountAsync(session, "t"));
            }
        }

        [Fact]
        public async Task CommitStatementFailure_IsHandledLikeCommitAsync()
        {
            var odp = new OdpLikeConnection(new SqliteConnection(Memory));
            using (var session = await OpenAsync(odp))
            {
                session.TransactionProbeSql = OdpLikeConnection.ProbeSql;
                await SetupAsync(session, "CREATE TABLE t (x INTEGER)");
                await session.ExecuteAsync(Dml("INSERT INTO t VALUES (1)"), 0);
                odp.FailNextCommit = CommitFailure.KeptOpen;

                await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync(Transaction("COMMIT", SqlTransactionAction.Commit), 0));
                var commit = await session.ExecuteAsync(Transaction("COMMIT", SqlTransactionAction.Commit), 0);

                Assert.True(commit.TransactionEnded);
                Assert.False(session.HasPendingChanges);
                await session.RollbackAsync();
                Assert.Equal(1, await CountAsync(session, "t"));
            }
        }

        [Fact]
        public async Task CommitAndRollback_ReachServerTransactionStartedWithoutTransactionObject()
        {
            // 자동 커밋을 끈 ODP.NET 연결에서는 DB 링크 조회·EXPLAIN PLAN처럼 트랜잭션 객체 없이 서버 트랜잭션이 시작된다.
            // COMMIT·ROLLBACK을 서버에 보내지 않으면 그 트랜잭션은 끊을 때까지 남는다(SET TRANSACTION의 ORA-01453 등)
            var odp = new OdpLikeConnection(new SqliteConnection(Memory));
            using (var session = await OpenAsync(odp))
            {
                await SetupAsync(session, "CREATE TABLE t (x INTEGER)");
                odp.Server = odp.Inner.BeginTransaction();
                await session.ExecuteAsync(new SqlStatement { Text = "INSERT INTO t VALUES (1)", Kind = SqlKind.Other, Verb = "EXPLAIN" }, 0);

                var commit = await session.ExecuteAsync(Transaction("COMMIT", SqlTransactionAction.Commit), 0);

                Assert.True(commit.TransactionEnded);
                Assert.Null(odp.Server);
                Assert.Equal(1, await CountAsync(session, "t"));

                odp.Server = odp.Inner.BeginTransaction();
                await session.ExecuteAsync(new SqlStatement { Text = "INSERT INTO t VALUES (2)", Kind = SqlKind.Other, Verb = "EXPLAIN" }, 0);
                await session.RollbackAsync();

                Assert.Null(odp.Server);
                Assert.Equal(1, await CountAsync(session, "t"));
            }
        }

        [Fact]
        public async Task CommitFailure_ProviderKeepsTransaction_CanStillRollBack()
        {
            using (var session = await OpenAsync(new SqliteConnection(Memory)))
            {
                // SQLite는 지연 외래 키 위반으로 COMMIT이 실패해도 트랜잭션을 그대로 둔다(SqliteTransaction도 살아 있음)
                await SetupAsync(session, "PRAGMA foreign_keys = ON", "CREATE TABLE p (id INTEGER PRIMARY KEY)",
                    "CREATE TABLE c (pid INTEGER REFERENCES p (id) DEFERRABLE INITIALLY DEFERRED)");
                await session.ExecuteAsync(Dml("INSERT INTO c VALUES (1)"), 0);

                await Assert.ThrowsAsync<SqliteException>(() => session.CommitAsync());

                Assert.True(session.HasPendingChanges);
                await session.RollbackAsync();
                Assert.False(session.HasPendingChanges);
                Assert.Equal(0, await CountAsync(session, "c"));
            }
        }

        [Fact]
        public void DescribeError_TransactionRolledBack_KeepsCauseLine()
        {
            var error = NewOracleException(2091, "ORA-02091: transaction rolled back\nORA-02291: integrity constraint (SCOTT.FK_EMP_DEPT) violated - parent key not found");

            Assert.Equal("ORA-02091: transaction rolled back ORA-02291: integrity constraint (SCOTT.FK_EMP_DEPT) violated - parent key not found",
                DbSession.DescribeError(error));
        }

        // ---- PL/SQL 뒤 서버 트랜잭션 확인 ----

        [Fact]
        public async Task PlSql_CommittingInsideBlock_EndsEarlierPendingChanges()
        {
            var probe = new TransactionProbe();
            using (var session = await OpenAsync(probe.Attach(new SqliteConnection(Memory))))
            {
                session.TransactionProbeSql = TransactionProbe.Sql;
                await CreateNumbersAsync(session, 10);
                await SetupAsync(session, "CREATE TABLE t (x INTEGER)");
                await session.ExecuteAsync(Dml("INSERT INTO t VALUES (1)"), 0);
                var locked = await session.ExecuteAsync(Query("SELECT x FROM nums ORDER BY x", forUpdate: true), 2);
                probe.Active = false;

                var result = await session.ExecuteAsync(PlSql("INSERT INTO t VALUES (2)"), 0);

                Assert.True(result.TransactionEnded);
                Assert.Equal("실행함 (블록 안에서 커밋·롤백됨)", result.Summary);
                Assert.False(session.HasPendingChanges);
                Assert.Null(session.PendingText);
                // 블록의 커밋으로 FOR UPDATE 잠금이 풀려 그 커서는 더 읽을 수 없다
                Assert.True(locked.Cursor.IsClosed);
            }
        }

        [Fact]
        public async Task PlSql_WithoutChangesOrTransaction_IsNotPending()
        {
            var probe = new TransactionProbe { Active = false };
            using (var session = await OpenAsync(probe.Attach(new SqliteConnection(Memory))))
            {
                session.TransactionProbeSql = TransactionProbe.Sql;

                var result = await session.ExecuteAsync(PlSql("SELECT 1"), 0);

                Assert.Equal("실행함", result.Summary);
                Assert.True(result.TransactionEnded);
                Assert.False(session.HasPendingChanges);
            }
        }

        [Fact]
        public async Task PlSql_LeavingTransactionOpen_StaysPendingWithUnknownCount()
        {
            var probe = new TransactionProbe();
            using (var session = await OpenAsync(probe.Attach(new SqliteConnection(Memory))))
            {
                session.TransactionProbeSql = TransactionProbe.Sql;
                await SetupAsync(session, "CREATE TABLE t (x INTEGER)");

                var result = await session.ExecuteAsync(PlSql("INSERT INTO t VALUES (1)"), 0);

                Assert.False(result.TransactionEnded);
                Assert.Equal("실행함 (커밋 전)", result.Summary);
                Assert.Equal("커밋 대기(행 수 모름)", session.PendingText);
            }
        }

        [Fact]
        public async Task PlSql_FailingAfterInnerCommit_EndsEarlierPendingChanges()
        {
            var probe = new TransactionProbe();
            using (var session = await OpenAsync(probe.Attach(new SqliteConnection(Memory))))
            {
                session.TransactionProbeSql = TransactionProbe.Sql;
                await SetupAsync(session, "CREATE TABLE t (x INTEGER)");
                await session.ExecuteAsync(Dml("INSERT INTO t VALUES (1)"), 0);
                probe.Active = false;

                await Assert.ThrowsAsync<SqliteException>(() => session.ExecuteAsync(PlSql("INSERT INTO missing VALUES (1)"), 0));

                Assert.False(session.HasPendingChanges);
                Assert.False(session.IsBroken);
            }
        }

        [Fact]
        public async Task PlSql_AfterLockTable_KeepsLockPendingEvenIfServerShowsNoTransaction()
        {
            // Oracle의 LOCK TABLE은 트랜잭션 ID 없이 잠금(TM)만 잡아 LOCAL_TRANSACTION_ID가 NULL이다 — 잠금이 남았는데 대기 표시를 지우면 안 된다
            var probe = new TransactionProbe { Active = false };
            using (var session = await OpenAsync(probe.Attach(new SqliteConnection(Memory))))
            {
                session.TransactionProbeSql = TransactionProbe.Sql;
                await SetupAsync(session, "CREATE TABLE t (x INTEGER)");
                await session.ExecuteAsync(new SqlStatement { Text = "INSERT INTO t VALUES (1)", Kind = SqlKind.Other, Verb = "LOCK" }, 0);

                var result = await session.ExecuteAsync(PlSql("SELECT 1"), 0);

                Assert.False(result.TransactionEnded);
                Assert.Equal("커밋 대기(행 수 모름) · 잠금", session.PendingText);
                await Assert.ThrowsAsync<SqliteException>(() => session.ExecuteAsync(PlSql("INSERT INTO missing VALUES (1)"), 0));
                Assert.True(session.HasPendingChanges);
                await session.RollbackAsync();
                Assert.Null(session.PendingText);
                Assert.Equal(0, await CountAsync(session, "t"));
            }
        }

        // ---- LOCK TABLE·FOR UPDATE 잠금 ----

        [Fact]
        public async Task LockTable_RunsInsideTransaction_AndHoldsPendingUntilEnd()
        {
            using (var session = await OpenAsync(new SqliteConnection(Memory)))
            {
                await SetupAsync(session, "CREATE TABLE t (x INTEGER)");
                // SQLite에는 LOCK TABLE이 없어 LOCK으로 표시한 INSERT로 트랜잭션 안에서 실행되는지 본다(밖이면 바로 커밋된다)
                var statement = new SqlStatement { Text = "INSERT INTO t VALUES (1)", Kind = SqlKind.Other, Verb = "LOCK" };

                var result = await session.ExecuteAsync(statement, 0);

                Assert.Equal("실행함 (잠금 보유 — 커밋·롤백하면 풀림)", result.Summary);
                Assert.True(session.HasPendingChanges);
                Assert.Equal("커밋 대기(잠금 보유)", session.PendingText);
                await session.RollbackAsync();
                Assert.Null(session.PendingText);
                Assert.Equal(0, await CountAsync(session, "t"));
            }
        }

        [Fact]
        public async Task PendingText_ShowsLocksHeldByForUpdate()
        {
            using (var session = await OpenAsync(new SqliteConnection(Memory)))
            {
                await CreateNumbersAsync(session, 5);
                await session.ExecuteAsync(Query("SELECT x FROM nums", forUpdate: true), 10);
                Assert.Equal("커밋 대기(잠금 보유)", session.PendingText);

                await session.ExecuteAsync(Dml("DELETE FROM nums WHERE x = 1"), 0);
                Assert.Equal("커밋 대기 1행 · 잠금", session.PendingText);

                await session.ExecuteAsync(PlSql("DELETE FROM nums WHERE x = 2"), 0);
                Assert.Equal("커밋 대기(행 수 모름) · 잠금", session.PendingText);

                await session.CommitAsync();
                Assert.Null(session.PendingText);
                await session.ExecuteAsync(Dml("DELETE FROM nums WHERE x = 3"), 0);
                Assert.Equal("커밋 대기 1행", session.PendingText);
            }
        }

        // ---- DDL 컴파일 경고(ORA-24344) ----

        [Fact]
        public async Task Ddl_WithCompileWarning_ReportsWarningInsteadOfPlainSuccess()
        {
            var connection = new SqliteConnection(Memory);
            DbSession session = null;
            // ODP.NET은 InfoMessage로 경고를 알린다. 시험에서는 실행 중에 부르는 함수로 같은 신호를 보낸다
            connection.CreateFunction("warn", (long number) =>
            {
                session.NoteWarning((int)number);
                return number;
            });
            session = await OpenAsync(connection);
            using (session)
            {
                // 앞 문장의 경고가 다음 DDL에 남지 않는다
                await session.ExecuteAsync(Query("SELECT warn(24344)"), 1);
                var plain = await session.ExecuteAsync(Ddl("CREATE TABLE v (y INTEGER)"), 0);
                Assert.Null(plain.Warning);
                Assert.Equal("실행함 (DDL은 자동 커밋됨)", plain.Summary);

                var warned = await session.ExecuteAsync(Ddl("CREATE TABLE u AS SELECT warn(24344) AS x"), 0);

                Assert.True(warned.TransactionEnded);
                Assert.Contains("ORA-24344", warned.Warning);
                Assert.Equal("실행함 — 컴파일 오류 (DDL은 자동 커밋됨)", warned.Summary);
                // 표가 아니라 오류 목록(ALL_ERRORS)을 찾을 대상이 없다
                Assert.Empty(warned.WarningDetails);
            }
        }

        // ---- 값 읽기(형식 매핑이 없는 객체·컬렉션 열) ----

        [Theory]
        [InlineData("Object", "Text")]
        [InlineData("Array", "Text")]
        [InlineData("Ref", "Text")]
        [InlineData("BinaryDouble", "Value")]
        [InlineData("BinaryFloat", "Value")]
        [InlineData("Decimal", "ProviderSpecific")]
        [InlineData("TimeStampTZ", "ProviderSpecific")]
        [InlineData("XmlType", "ProviderSpecific")]
        public void OracleReadMode_ByProviderTypeName(string providerTypeName, string expected)
        {
            Assert.Equal(expected, DbSession.OracleReadMode(providerTypeName).ToString());
        }

        [Fact]
        public void ReadValue_UnmappedObjectType_FallsBackToProviderText()
        {
            // ODP.NET: 형식 매핑이 없는 객체 형식은 GetProviderSpecificValue·GetValue 모두 InvalidOperationException(ORA-50071)
            var mapping = new InvalidOperationException("Custom type mapping for 'MDSYS.SDO_GEOMETRY' is not specified or is invalid");
            using (var reader = new SingleValueReader("{\"SDO_GTYPE\":2001}", mapping))
            {
                Assert.Equal("{\"SDO_GTYPE\":2001}", DbSession.ReadValue(reader, 0, ColumnRead.ProviderSpecific));
                Assert.Equal("{\"SDO_GTYPE\":2001}", DbSession.ReadValue(reader, 0, ColumnRead.Text));
            }
            using (var reader = new SingleValueReader(null, mapping))
                Assert.Null(DbSession.ReadValue(reader, 0, ColumnRead.ProviderSpecific));
            using (var reader = new SingleValueReader("x", NewOracleException(50087, "ORA-50087: unsupported type")) { TextFails = true })
                Assert.Equal("(OBJECT)", DbSession.ReadValue(reader, 0, ColumnRead.ProviderSpecific));
        }

        [Fact]
        public void ReadValue_BrokenConnection_IsNotHidden()
        {
            using (var reader = new SingleValueReader("x", NewOracleException(3113, "ORA-03113: end-of-file on communication channel")))
                Assert.Throws<OracleException>(() => DbSession.ReadValue(reader, 0, ColumnRead.ProviderSpecific));
        }

        // ---------- QueryTableAsync (GCM 정의서의 사용자 SQL) ----------

        [Fact]
        public async Task QueryTable_ReturnsColumnNamesAndAllRows_NullAsNull_MaxRowsLimits()
        {
            using (var session = await OpenAsync(new SqliteConnection(Memory)))
            {
                await CreateNumbersAsync(session, 5);

                var table = await session.QueryTableAsync("SELECT x AS TABLE_NAME, 'c' || x AS code, NULL AS code_name FROM nums ORDER BY x", null);
                Assert.Equal(new[] { "TABLE_NAME", "code", "code_name" }, table.Columns.ToArray());
                Assert.Equal(5, table.Rows.Count);
                Assert.Equal(new[] { "1", "c1", null }, table.Rows[0]);

                var head = await session.QueryTableAsync("SELECT x FROM nums ORDER BY x", 2);
                Assert.Equal(2, head.Rows.Count);
                var none = await session.QueryTableAsync("SELECT x FROM nums", 0);
                Assert.Equal(new[] { "x" }, none.Columns.ToArray());
                Assert.Empty(none.Rows);
                // 조회가 끝난 뒤 세션은 그대로 쓸 수 있다
                Assert.False(session.IsBusy);
                await Assert.ThrowsAsync<ArgumentException>(() => session.QueryTableAsync(" ", null));
            }
        }

        /// <summary>SQLite 세션의 방언은 Generic(LIMIT 1) — 세션의 방언으로 감싼 열 확인 SQL이 실제로 한 행만 읽고, ProbeAsync가 별칭 검사까지 한다.</summary>
        [Fact]
        public async Task QueryTable_WithSessionDialect_ProbeReadsOneRow_AndChecksAliases()
        {
            using (var session = await OpenAsync(new SqliteConnection(Memory)))
            {
                await CreateNumbersAsync(session, 5);
                Assert.Same(SqlDialects.Generic, session.Dialect);

                var sql = "SELECT x AS TABLE_NAME, 'c' || x AS CODE, 'n' || x AS CODE_NAME, x AS PLANT_CD FROM nums ORDER BY x DESC;";
                var probe = await session.QueryTableAsync(CodeSpecQueries.ProbeSql(sql, session.Dialect), null);
                Assert.Equal(new[] { "TABLE_NAME", "CODE", "CODE_NAME", "PLANT_CD" }, probe.Columns.ToArray());
                Assert.Single(probe.Rows);
                // 인라인 뷰 안의 ORDER BY가 그대로 적용된다
                Assert.Equal("5", probe.Rows[0][0]);

                var check = await CodeSpecQueries.ProbeAsync(session, sql, CancellationToken.None);
                Assert.True(check.Ok, check.Summary);
                Assert.Equal(new[] { "PLANT_CD" }, check.Extra.ToArray());

                var missing = await CodeSpecQueries.ProbeAsync(session, "SELECT x FROM nums", CancellationToken.None);
                Assert.Equal(new[] { "TABLE_NAME", "CODE", "CODE_NAME" }, missing.Missing.ToArray());
                Assert.False(session.IsBusy);
            }
        }

        private static async Task<DbSession> OpenAsync(DbConnection connection)
        {
            var session = new DbSession(connection);
            await session.OpenAsync();
            return session;
        }

        /// <summary>두 연결이 함께 쓰는 메모리 DB. 풀링을 끄면 마지막 연결이 닫힐 때 DB도 사라진다.</summary>
        private static string SharedMemory()
        {
            return "Data Source=file:memdb" + Guid.NewGuid().ToString("N") + "?mode=memory&cache=shared;Pooling=False";
        }

        private static SqlStatement Query(string sql, bool forUpdate = false)
        {
            return new SqlStatement { Text = sql, Kind = SqlKind.Query, Verb = "SELECT", ForUpdate = forUpdate };
        }

        private static SqlStatement Dml(string sql)
        {
            return new SqlStatement { Text = sql, Kind = SqlKind.Dml, Verb = FirstWord(sql) };
        }

        private static SqlStatement Ddl(string sql)
        {
            return new SqlStatement { Text = sql, Kind = SqlKind.Ddl, Verb = FirstWord(sql) };
        }

        private static SqlStatement PlSql(string sql)
        {
            return new SqlStatement { Text = sql, Kind = SqlKind.PlSql, Verb = "BEGIN" };
        }

        private static SqlStatement Other(string sql)
        {
            return new SqlStatement { Text = sql, Kind = SqlKind.Other, Verb = FirstWord(sql) };
        }

        private static SqlStatement Transaction(string sql, SqlTransactionAction action)
        {
            return new SqlStatement { Text = sql, Kind = SqlKind.Transaction, Verb = FirstWord(sql), TransactionAction = action };
        }

        private static string FirstWord(string sql)
        {
            var text = sql.Trim();
            var space = text.IndexOf(' ');
            return (space < 0 ? text : text.Substring(0, space)).ToUpperInvariant();
        }

        private static string Text(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>sql을 SQLite가 (따로 연 트랜잭션 안에서도) 거부하는지 확인한다.</summary>
        private static Task AssertSqliteRejectsInTransaction(DbSession session, string sql)
        {
            return Assert.ThrowsAsync<SqliteException>(() => session.RunAsync((connection, transaction) =>
            {
                using (var own = connection.BeginTransaction())
                    Exec(connection, own, sql);
                return true;
            }));
        }

        /// <summary>세션의 연결에서 준비 SQL을 실행한다(트랜잭션 밖이면 자동 커밋).</summary>
        private static Task SetupAsync(DbSession session, params string[] sql)
        {
            return session.RunAsync((connection, transaction) =>
            {
                foreach (var text in sql)
                    Exec(connection, transaction, text);
                return true;
            });
        }

        /// <summary>nums(x) 표에 1..count.</summary>
        private static Task CreateNumbersAsync(DbSession session, int count)
        {
            return SetupAsync(session,
                "CREATE TABLE nums (x INTEGER PRIMARY KEY)",
                "WITH RECURSIVE n(v) AS (SELECT 1 UNION ALL SELECT v + 1 FROM n WHERE v < " + Text(count) + ") INSERT INTO nums SELECT v FROM n");
        }

        private static Task<long> CountAsync(DbSession session, string table)
        {
            return session.RunAsync((connection, transaction) => Scalar(connection, transaction, "SELECT COUNT(*) FROM " + table));
        }

        private static void Exec(DbConnection connection, DbTransaction transaction, string sql)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
        }

        private static long Scalar(DbConnection connection, DbTransaction transaction, string sql)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = sql;
                return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }

        private static async Task<T> Within<T>(Task<T> task)
        {
            await Within((Task)task);
            return await task;
        }

        private static async Task Within(Task task)
        {
            if (await Task.WhenAny(task, Task.Delay(Timeout)) != task)
                throw new TimeoutException("작업이 끝나지 않습니다.");
            await task;
        }

        /// <summary>ODP.NET의 OracleException은 공개 생성자가 없어 내부 생성자로 만든다(버전을 고정한 패키지 기준).</summary>
        private static OracleException NewOracleException(int number, string message)
        {
            var constructor = typeof(OracleException).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(int), typeof(string), typeof(string), typeof(string), typeof(int) }, null);
            Assert.True(constructor != null, "OracleException 내부 생성자를 찾지 못했습니다. ODP.NET 버전이 바뀌었으면 시험을 고치세요.");
            return (OracleException)constructor.Invoke(new object[] { number, "test", "test", message, -1 });
        }

        /// <summary>
        /// CreateCommand가 현재 트랜잭션을 명령에 넣어 주지 않는 연결(일반 ADO.NET 규칙). 그런 명령은 SQLite가 트랜잭션 중에 실행을 거부한다.
        /// </summary>
        private sealed class PlainCommandConnection : DbConnection
        {
            private readonly SqliteConnection _inner;

            public PlainCommandConnection(SqliteConnection inner)
            {
                _inner = inner;
            }

            public override string ConnectionString
            {
                get { return _inner.ConnectionString; }
                set { _inner.ConnectionString = value; }
            }

            public override string Database { get { return _inner.Database; } }

            public override string DataSource { get { return _inner.DataSource; } }

            public override string ServerVersion { get { return _inner.ServerVersion; } }

            public override ConnectionState State { get { return _inner.State; } }

            public override void ChangeDatabase(string databaseName)
            {
                _inner.ChangeDatabase(databaseName);
            }

            public override void Open()
            {
                _inner.Open();
            }

            public override void Close()
            {
                _inner.Close();
            }

            protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
            {
                return _inner.BeginTransaction(isolationLevel);
            }

            protected override DbCommand CreateDbCommand()
            {
                return new SqliteCommand { Connection = _inner };
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    _inner.Dispose();
                base.Dispose(disposing);
            }
        }

        /// <summary>
        /// sleep_ms(ms): 불리면 Started를 알리고, Release()되거나 ms가 지날 때까지 멈춘다.
        /// 바쁨 시험이 시간 간격에 기대지 않게 시험이 끝낼 때를 정한다.
        /// </summary>
        private sealed class SlowFunction
        {
            private readonly ManualResetEventSlim _started = new ManualResetEventSlim();
            private readonly ManualResetEventSlim _release = new ManualResetEventSlim();

            public SlowFunction(SqliteConnection connection)
            {
                connection.CreateFunction("sleep_ms", (long ms) =>
                {
                    _started.Set();
                    _release.Wait(TimeSpan.FromMilliseconds(ms));
                    return ms;
                });
            }

            public void WaitStarted()
            {
                Assert.True(_started.Wait(Timeout), "느린 조회가 시작되지 않았습니다.");
            }

            public void Release()
            {
                _release.Set();
            }
        }

        /// <summary>txn_probe(): Active면 트랜잭션 ID 같은 값, 아니면 NULL(DBMS_TRANSACTION.LOCAL_TRANSACTION_ID를 흉내).</summary>
        private sealed class TransactionProbe
        {
            public const string Sql = "SELECT txn_probe()";

            public bool Active { get; set; } = true;

            public SqliteConnection Attach(SqliteConnection connection)
            {
                connection.CreateFunction("txn_probe", () => Active ? "7.21.3410" : null);
                return connection;
            }
        }

        private enum CommitFailure
        {
            None,
            /// <summary>서버가 트랜잭션을 롤백했다(ORA-02091)</summary>
            RolledBack,
            /// <summary>서버 트랜잭션은 남았다</summary>
            KeptOpen
        }

        /// <summary>
        /// ODP.NET처럼 동작하는 연결(OracleConnection은 봉인되어 흉내만 낸다): 명령의 Transaction 설정을 무시하고 연결의 서버 트랜잭션에서 실행하며,
        /// BeginTransaction은 서버 트랜잭션이 이미 있으면 이어 받는다. 트랜잭션 객체는 커밋·롤백이 실패해도 끝난 것이 된다(다시 부르면 예외).
        /// txn_probe()는 서버 트랜잭션이 있으면 값, 없으면 NULL.
        /// </summary>
        private sealed class OdpLikeConnection : DbConnection
        {
            public const string ProbeSql = "SELECT txn_probe()";

            public OdpLikeConnection(SqliteConnection inner)
            {
                Inner = inner;
                inner.CreateFunction("txn_probe", () => Server != null ? "7.21.3410" : null);
            }

            public SqliteConnection Inner { get; }

            /// <summary>서버 쪽 트랜잭션(트랜잭션 객체가 끝나도 남을 수 있다).</summary>
            public SqliteTransaction Server { get; set; }

            public CommitFailure FailNextCommit { get; set; }

            public override string ConnectionString
            {
                get { return Inner.ConnectionString; }
                set { Inner.ConnectionString = value; }
            }

            public override string Database { get { return Inner.Database; } }

            public override string DataSource { get { return Inner.DataSource; } }

            public override string ServerVersion { get { return Inner.ServerVersion; } }

            public override ConnectionState State { get { return Inner.State; } }

            public override void ChangeDatabase(string databaseName)
            {
                Inner.ChangeDatabase(databaseName);
            }

            public override void Open()
            {
                Inner.Open();
            }

            public override void Close()
            {
                Inner.Close();
            }

            protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
            {
                if (Server == null)
                    Server = Inner.BeginTransaction();
                return new OdpLikeTransaction(this);
            }

            protected override DbCommand CreateDbCommand()
            {
                return new OdpLikeCommand(this);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    Inner.Dispose();
                base.Dispose(disposing);
            }
        }

        private sealed class OdpLikeTransaction : DbTransaction
        {
            private OdpLikeConnection _owner;

            public OdpLikeTransaction(OdpLikeConnection owner)
            {
                _owner = owner;
            }

            public override IsolationLevel IsolationLevel { get { return IsolationLevel.ReadCommitted; } }

            protected override DbConnection DbConnection { get { return _owner; } }

            public override void Commit()
            {
                var owner = Complete();
                var failure = owner.FailNextCommit;
                owner.FailNextCommit = CommitFailure.None;
                if (failure == CommitFailure.RolledBack)
                {
                    owner.Server.Rollback();
                    owner.Server = null;
                    throw new InvalidOperationException("ORA-02091: transaction rolled back\nORA-02291: integrity constraint (T.FK) violated - parent key not found");
                }
                if (failure == CommitFailure.KeptOpen)
                    throw new InvalidOperationException("ORA-02049: timeout: distributed transaction waiting for lock");
                owner.Server.Commit();
                owner.Server = null;
            }

            public override void Rollback()
            {
                var owner = Complete();
                owner.Server.Rollback();
                owner.Server = null;
            }

            // OracleTransaction처럼 결과와 관계없이 끝난 것으로 만든다(finally에서 m_completed = true)
            private OdpLikeConnection Complete()
            {
                var owner = _owner;
                if (owner == null)
                    throw new InvalidOperationException("Invalid operation on a closed object");
                _owner = null;
                return owner;
            }
        }

        private sealed class OdpLikeCommand : DbCommand
        {
            private readonly OdpLikeConnection _owner;
            private readonly SqliteCommand _inner;

            public OdpLikeCommand(OdpLikeConnection owner)
            {
                _owner = owner;
                _inner = owner.Inner.CreateCommand();
            }

            public override string CommandText
            {
                get { return _inner.CommandText; }
                set { _inner.CommandText = value; }
            }

            public override int CommandTimeout
            {
                get { return _inner.CommandTimeout; }
                set { _inner.CommandTimeout = value; }
            }

            public override CommandType CommandType
            {
                get { return _inner.CommandType; }
                set { _inner.CommandType = value; }
            }

            public override bool DesignTimeVisible { get; set; }

            public override UpdateRowSource UpdatedRowSource
            {
                get { return _inner.UpdatedRowSource; }
                set { _inner.UpdatedRowSource = value; }
            }

            protected override DbConnection DbConnection
            {
                get { return _owner; }
                set { }
            }

            protected override DbParameterCollection DbParameterCollection { get { return _inner.Parameters; } }

            // OracleCommand.DbTransaction처럼 설정을 무시한다 — 명령은 언제나 연결의 서버 트랜잭션에서 실행된다
            protected override DbTransaction DbTransaction
            {
                get { return null; }
                set { }
            }

            public override void Cancel()
            {
                _inner.Cancel();
            }

            public override int ExecuteNonQuery()
            {
                _inner.Transaction = _owner.Server;
                return _inner.ExecuteNonQuery();
            }

            public override object ExecuteScalar()
            {
                _inner.Transaction = _owner.Server;
                return _inner.ExecuteScalar();
            }

            public override void Prepare()
            {
                _inner.Prepare();
            }

            protected override DbParameter CreateDbParameter()
            {
                return _inner.CreateParameter();
            }

            protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
            {
                _inner.Transaction = _owner.Server;
                return _inner.ExecuteReader(behavior);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    _inner.Dispose();
                base.Dispose(disposing);
            }
        }

        /// <summary>
        /// 한 열짜리 리더. GetProviderSpecificValue·GetValue는 failure를 던지고(형식 매핑이 없는 객체 형식처럼) GetString은 text를 준다
        /// (TextFails면 GetString도 failure). text가 null이면 NULL 값.
        /// </summary>
        private sealed class SingleValueReader : DbDataReader
        {
            private readonly string _text;
            private readonly Exception _failure;

            public SingleValueReader(string text, Exception failure)
            {
                _text = text;
                _failure = failure;
            }

            public bool TextFails { get; set; }

            public override int FieldCount { get { return 1; } }

            public override bool HasRows { get { return true; } }

            public override bool IsClosed { get { return false; } }

            public override int RecordsAffected { get { return -1; } }

            public override int Depth { get { return 0; } }

            public override object this[int ordinal] { get { return GetValue(ordinal); } }

            public override object this[string name] { get { return GetValue(0); } }

            public override object GetProviderSpecificValue(int ordinal)
            {
                throw _failure;
            }

            public override object GetValue(int ordinal)
            {
                throw _failure;
            }

            public override bool IsDBNull(int ordinal)
            {
                return _text == null;
            }

            public override string GetString(int ordinal)
            {
                if (TextFails)
                    throw _failure;
                return _text;
            }

            public override bool Read() { return false; }

            public override bool NextResult() { return false; }

            public override string GetName(int ordinal) { return "C"; }

            public override int GetOrdinal(string name) { return 0; }

            public override string GetDataTypeName(int ordinal) { return "Object"; }

            public override Type GetFieldType(int ordinal) { return typeof(object); }

            public override int GetValues(object[] values) { throw new NotSupportedException(); }

            public override bool GetBoolean(int ordinal) { throw new NotSupportedException(); }

            public override byte GetByte(int ordinal) { throw new NotSupportedException(); }

            public override long GetBytes(int ordinal, long dataOffset, byte[] buffer, int bufferOffset, int length) { throw new NotSupportedException(); }

            public override char GetChar(int ordinal) { throw new NotSupportedException(); }

            public override long GetChars(int ordinal, long dataOffset, char[] buffer, int bufferOffset, int length) { throw new NotSupportedException(); }

            public override DateTime GetDateTime(int ordinal) { throw new NotSupportedException(); }

            public override decimal GetDecimal(int ordinal) { throw new NotSupportedException(); }

            public override double GetDouble(int ordinal) { throw new NotSupportedException(); }

            public override float GetFloat(int ordinal) { throw new NotSupportedException(); }

            public override Guid GetGuid(int ordinal) { throw new NotSupportedException(); }

            public override short GetInt16(int ordinal) { throw new NotSupportedException(); }

            public override int GetInt32(int ordinal) { throw new NotSupportedException(); }

            public override long GetInt64(int ordinal) { throw new NotSupportedException(); }

            public override System.Collections.IEnumerator GetEnumerator() { throw new NotSupportedException(); }
        }
    }
}

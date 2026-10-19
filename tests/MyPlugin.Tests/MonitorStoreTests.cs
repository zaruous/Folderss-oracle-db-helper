using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    /// <summary>
    /// 성능 모니터 이력 저장소(history.db): 스키마 만들기·버전, 더 새 버전은 읽기만, 손상 파일, 사용자 항목·덮어쓰기·실행 결과, 보관 기간, 두 연결 동시 쓰기.
    /// 시험마다 임시 데이터 폴더를 만들고 지운다.
    /// </summary>
    public class MonitorStoreTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "fmon-store-" + Guid.NewGuid().ToString("N"));

        public MonitorStoreTests()
        {
            Directory.CreateDirectory(_folder);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(_folder, true);
            }
            catch (IOException)
            {
                // 백신 등이 잠깐 쥐고 있으면 남는다 — 임시 폴더라 괜찮다
            }
        }

        private static StoredResult Result(string id, string status, double? value = null, int? rows = null, string reason = null)
        {
            return new StoredResult { ItemId = id, Status = status, Value = value, RowCount = rows, Reason = reason };
        }

        [Fact]
        public void Open_CreatesSchemaOnce_InMonitorHistoryFolder()
        {
            var store = MonitorStore.Open(_folder);
            Assert.Equal(Path.Combine(_folder, "monitor-history", "history.db"), store.FilePath);
            Assert.True(File.Exists(store.FilePath));
            Assert.False(store.ReadOnly);
            var again = MonitorStore.Open(_folder);
            Assert.False(again.ReadOnly);
            using (var connection = new SqliteConnection("Data Source=" + store.FilePath + ";Pooling=False"))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT COUNT(*), MAX(version) FROM schema_version";
                    using (var reader = command.ExecuteReader())
                    {
                        Assert.True(reader.Read());
                        Assert.Equal(1L, reader.GetInt64(0));
                        Assert.Equal((long)MonitorStore.SchemaVersion, reader.GetInt64(1));
                    }
                    command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name";
                    using (var reader = command.ExecuteReader())
                    {
                        var names = new System.Collections.Generic.List<string>();
                        while (reader.Read())
                            names.Add(reader.GetString(0));
                        Assert.Equal(new[] { "check_item", "check_override", "check_result", "check_run", "db", "schema_version" }, names);
                    }
                    command.CommandText = "PRAGMA journal_mode";
                    Assert.Equal("wal", (string)command.ExecuteScalar());
                }
            }
            Assert.Throws<ArgumentException>(() => MonitorStore.Open(null));
        }

        [Fact]
        public void NewerSchema_IsReadOnly_ReadsWork_WritesThrow()
        {
            var store = MonitorStore.Open(_folder);
            var key = store.TouchDb("oracle", "oracle:1:XE", "XE", DateTime.UtcNow);
            store.SaveRun(key, DateTime.UtcNow, 10, "1.9.0", new[] { Result("ora.a", "ok") });
            using (var connection = new SqliteConnection("Data Source=" + store.FilePath + ";Pooling=False"))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "UPDATE schema_version SET version = 99";
                    command.ExecuteNonQuery();
                }
            }
            var later = MonitorStore.Open(_folder);
            Assert.True(later.ReadOnly);
            Assert.NotNull(later.LastRunId(key));
            Assert.Empty(later.LoadUserItems());
            Assert.Throws<InvalidOperationException>(() => later.SaveRun(key, DateTime.UtcNow, 1, "", new StoredResult[0]));
            Assert.Throws<InvalidOperationException>(() => later.SaveUserItem(new UserCheckItem { Id = "user.a" }, DateTime.UtcNow));
            Assert.Throws<InvalidOperationException>(() => later.Purge(DateTime.UtcNow));
        }

        [Fact]
        public void CorruptFile_OpenThrows_SoCallerTurnsRecordingOff()
        {
            var path = MonitorStore.PathIn(_folder);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, Enumerable.Repeat((byte)0x5A, 8192).ToArray());
            Assert.ThrowsAny<SqliteException>(() => MonitorStore.Open(_folder));
        }

        [Fact]
        public void DbKey_StableHex_DiffersByKindAndIdentity()
        {
            var key = MonitorStore.DbKey("oracle", "oracle:2963:XE");
            Assert.Equal(64, key.Length);
            Assert.Matches("^[0-9a-f]+$", key);
            Assert.Equal(key, MonitorStore.DbKey("oracle", "oracle:2963:XE"));
            Assert.NotEqual(key, MonitorStore.DbKey("postgres", "oracle:2963:XE"));
            Assert.NotEqual(key, MonitorStore.DbKey("oracle", "oracle:2964:XE"));
        }

        [Fact]
        public void UserItems_Upsert_KeepsCreatedAt_DeleteRemovesOverrides()
        {
            var store = MonitorStore.Open(_folder);
            var first = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);
            var item = new UserCheckItem { Id = "user.a", Kind = "oracle", Category = "공간", Title = "큰 세그먼트", Sql = "SELECT 1 FROM DUAL", Rule = "{\"type\":\"info\"}", Advice = "조치" };
            store.SaveUserItem(item, first);
            item.Title = "아주 큰 세그먼트";
            store.SaveUserItem(item, first.AddHours(1));
            store.SaveOverride(null, new CheckOverride { ItemId = "user.a", Enabled = false });
            var loaded = store.LoadUserItems().Single();
            Assert.Equal("아주 큰 세그먼트", loaded.Title);
            Assert.Equal("2026-10-20T00:00:00.000Z", loaded.CreatedAt);
            Assert.Equal("2026-10-20T01:00:00.000Z", loaded.UpdatedAt);
            Assert.Equal("조치", loaded.Advice);
            Assert.True(store.DeleteUserItem("user.a"));
            Assert.Empty(store.LoadUserItems());
            Assert.Empty(store.LoadOverrides(null));
            Assert.False(store.DeleteUserItem("user.a"));
            Assert.Throws<ArgumentException>(() => store.SaveUserItem(new UserCheckItem(), first));
        }

        [Fact]
        public void Overrides_DbValuesOverGlobal_PerField_AllNullDeletes()
        {
            var store = MonitorStore.Open(_folder);
            store.SaveOverride(null, new CheckOverride { ItemId = "ora.tablespace", Warning = 80, Critical = 90 });
            store.SaveOverride("db1", new CheckOverride { ItemId = "ora.tablespace", Critical = 97 });
            store.SaveOverride("db1", new CheckOverride { ItemId = "ora.log_mode", Enabled = false });
            var global = store.LoadOverrides(null);
            Assert.Equal(80, global["ora.tablespace"].Warning);
            Assert.Equal(90, global["ora.tablespace"].Critical);
            Assert.False(global.ContainsKey("ora.log_mode"));
            var db1 = store.LoadOverrides("db1");
            Assert.Equal(80, db1["ora.tablespace"].Warning);
            Assert.Equal(97, db1["ora.tablespace"].Critical);
            Assert.False(db1["ora.log_mode"].Enabled);
            Assert.Null(db1["ora.log_mode"].Warning);
            store.SaveOverride(null, new CheckOverride { ItemId = "ora.tablespace" });
            Assert.Empty(store.LoadOverrides(null));
            Assert.Equal(97, store.LoadOverrides("db1")["ora.tablespace"].Critical);
        }

        [Fact]
        public void Runs_LastAndPrevious_Results_PurgeCascades()
        {
            var store = MonitorStore.Open(_folder);
            var now = DateTime.UtcNow;
            var key = store.TouchDb("oracle", "oracle:1:XE", "XE (DBID 1)", now);
            var other = store.TouchDb("oracle", "oracle:2:PROD", "PROD", now);
            Assert.Null(store.LastRunId(key));
            var old = store.SaveRun(key, now.AddDays(-100), 1200, "1.8.0", new[] { Result("ora.tablespace", "warn", 88.5, 3), Result("ora.alert_log", "skip", reason: "privilege") });
            var recent = store.SaveRun(key, now.AddMinutes(-5), 900, "1.9.0", new[] { Result("ora.tablespace", "crit", 96, 3) });
            store.SaveRun(other, now, 10, "1.9.0", new[] { Result("ora.tablespace", "ok", 10, 1) });
            Assert.Equal(recent, store.LastRunId(key));
            Assert.Equal(old, store.LastRunId(key, recent));
            Assert.Null(store.LastRunId(key, old));
            var results = store.Results(old);
            Assert.Equal(88.5, results["ora.tablespace"].Value);
            Assert.Equal(3, results["ora.tablespace"].RowCount);
            Assert.Equal("privilege", results["ora.alert_log"].Reason);
            Assert.Null(results["ora.alert_log"].Value);
            var at = store.RunStartedAt(recent);
            Assert.True(at.HasValue);
            Assert.Equal(DateTimeKind.Utc, at.Value.Kind);
            Assert.True(Math.Abs((at.Value - now.AddMinutes(-5)).TotalSeconds) < 1);
            Assert.Null(store.RunStartedAt(12345));
            Assert.Equal(3, store.RunCount());

            Assert.Equal(1, store.Purge(now.AddDays(-90)));
            Assert.Empty(store.Results(old));
            Assert.Equal(recent, store.LastRunId(key));
            Assert.Equal(2, store.Purge(now.AddYears(1)));
            Assert.Equal(0, store.RunCount());
        }

        [Fact]
        public async Task TwoStores_WriteAtOnce_BusyTimeoutWaits()
        {
            var a = MonitorStore.Open(_folder);
            var b = MonitorStore.Open(_folder);
            var key = a.TouchDb("oracle", "oracle:1:XE", "XE", DateTime.UtcNow);
            var results = Enumerable.Range(0, 17).Select(i => Result("ora." + i, "ok", i, 0)).ToArray();
            await Task.WhenAll(
                Task.Run(() => { for (var i = 0; i < 25; i++) a.SaveRun(key, DateTime.UtcNow, i, "a", results); }),
                Task.Run(() => { for (var i = 0; i < 25; i++) b.SaveRun(key, DateTime.UtcNow, i, "b", results); }));
            Assert.Equal(50, a.RunCount());
            Assert.Equal(17, b.Results(b.LastRunId(key).Value).Count);
        }
    }
}

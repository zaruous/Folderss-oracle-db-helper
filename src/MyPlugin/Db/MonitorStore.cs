using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace MyPlugin
{
    /// <summary>사용자 점검 항목(SQLite check_item 행). 규칙은 JSON 글(CheckLogic.RuleToJson).</summary>
    internal sealed class UserCheckItem
    {
        public string Id { get; set; }
        public string Kind { get; set; }
        public string Category { get; set; }
        public string Title { get; set; }
        public string Sql { get; set; }
        public string Rule { get; set; }
        public string Advice { get; set; }
        public string CreatedAt { get; set; }
        public string UpdatedAt { get; set; }
    }

    /// <summary>점검 항목의 켬·끔과 임계값 덮어쓰기(전체 '' 또는 DB별). NULL 칸은 위 단계(DB → 전체 → 기본)를 따른다.</summary>
    internal sealed class CheckOverride
    {
        public string ItemId { get; set; }
        public bool? Enabled { get; set; }
        public double? Warning { get; set; }
        public double? Critical { get; set; }
    }

    /// <summary>보관하는 점검 결과 하나(발견 행은 없다 — DB 내용은 파일에 남기지 않는다).</summary>
    internal sealed class StoredResult
    {
        public string ItemId { get; set; }
        /// <summary>ok · info · warn · crit · skip · error</summary>
        public string Status { get; set; }
        public double? Value { get; set; }
        public int? RowCount { get; set; }
        /// <summary>건너뜀·오류의 이유 분류(privilege · requirement · disabled · timeout · error). 오류 문장 원문은 넣지 않는다.</summary>
        public string Reason { get; set; }
    }

    /// <summary>
    /// 성능 모니터 이력 저장소(SQLite, 플러그인 데이터 폴더의 monitor-history\history.db). 설계: docs/monitor-history-design.md 5절 · monitor-dba-design.md 6장.
    /// - 지금(스키마 1)은 db · check_item · check_override · check_run · check_result. 지표 이력(snapshot · metric)은 이력 5-3에서 버전을 올려 더한다.
    /// - 연결은 작업마다 열고 닫는다(풀링 끔, WAL, busy_timeout 5초) — 같은 PC에서 DB Helper 창이 둘이어도 된다.
    /// - 더 새 스키마(나중 버전 플러그인이 만든 파일)면 <see cref="ReadOnly"/> — 쓰기는 예외. 열 수 없거나 손상이면 <see cref="Open"/>이 예외(부른 쪽이 기록만 끈다).
    /// - DB 키는 접속 프로필이 아니라 서버가 알려 주는 식별(Oracle DBID+이름)의 SHA-256 — 프로필 이름을 바꾸거나 복제해도 이력이 섞이지 않는다.
    /// </summary>
    internal sealed class MonitorStore
    {
        public const int SchemaVersion = 1;
        public const string FolderName = "monitor-history";
        public const string FileName = "history.db";

        private MonitorStore(string path, bool readOnly)
        {
            FilePath = path;
            ReadOnly = readOnly;
        }

        public string FilePath { get; }

        /// <summary>파일이 더 새 버전 플러그인의 것이라 읽기만 한다.</summary>
        public bool ReadOnly { get; }

        public static string PathIn(string dataDirectory)
        {
            return Path.Combine(dataDirectory, FolderName, FileName);
        }

        /// <summary>저장소를 열고(없으면 만들고) 스키마를 맞춘다. 실패하면 예외.</summary>
        public static MonitorStore Open(string dataDirectory)
        {
            if (string.IsNullOrEmpty(dataDirectory))
                throw new ArgumentException("데이터 폴더가 없습니다.", nameof(dataDirectory));
            var path = PathIn(dataDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var created = !File.Exists(path);
            using (var connection = Connect(path))
            {
                if (created)
                    Execute(connection, "PRAGMA auto_vacuum = INCREMENTAL");
                Execute(connection, "PRAGMA journal_mode = WAL");
                Execute(connection, "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL)");
                var version = Scalar(connection, "SELECT MAX(version) FROM schema_version");
                var current = version == null || version is DBNull ? 0 : Convert.ToInt32(version, CultureInfo.InvariantCulture);
                if (current > SchemaVersion)
                    return new MonitorStore(path, true);
                if (current < 1)
                {
                    using (var tx = connection.BeginTransaction())
                    {
                        Execute(connection, @"CREATE TABLE IF NOT EXISTS db (
  db_key TEXT PRIMARY KEY, kind TEXT NOT NULL, label TEXT NOT NULL, first_seen TEXT NOT NULL, last_seen TEXT NOT NULL)", tx);
                        Execute(connection, @"CREATE TABLE IF NOT EXISTS check_item (
  id TEXT PRIMARY KEY, kind TEXT NOT NULL, category TEXT NOT NULL, title TEXT NOT NULL, sql TEXT NOT NULL, rule TEXT NOT NULL,
  advice TEXT, created_at TEXT NOT NULL, updated_at TEXT NOT NULL)", tx);
                        Execute(connection, @"CREATE TABLE IF NOT EXISTS check_override (
  db_key TEXT NOT NULL DEFAULT '', item_id TEXT NOT NULL, enabled INTEGER, warn REAL, crit REAL, PRIMARY KEY (db_key, item_id)) WITHOUT ROWID", tx);
                        Execute(connection, @"CREATE TABLE IF NOT EXISTS check_run (
  id INTEGER PRIMARY KEY, db_key TEXT NOT NULL REFERENCES db(db_key), started_at TEXT NOT NULL, elapsed_ms INTEGER, app_version TEXT)", tx);
                        Execute(connection, "CREATE INDEX IF NOT EXISTS ix_check_run_db_time ON check_run (db_key, started_at)", tx);
                        Execute(connection, @"CREATE TABLE IF NOT EXISTS check_result (
  run_id INTEGER NOT NULL REFERENCES check_run(id) ON DELETE CASCADE, item_id TEXT NOT NULL, status TEXT NOT NULL,
  value REAL, row_count INTEGER, reason TEXT, PRIMARY KEY (run_id, item_id)) WITHOUT ROWID", tx);
                        Execute(connection, "DELETE FROM schema_version", tx);
                        Execute(connection, "INSERT INTO schema_version (version) VALUES (1)", tx);
                        tx.Commit();
                    }
                }
            }
            return new MonitorStore(path, false);
        }

        /// <summary>DB 키: SHA-256(종류 + 줄바꿈 + 서버 식별)의 16진 소문자.</summary>
        public static string DbKey(string kind, string identity)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes((kind ?? "") + "\n" + (identity ?? "")));
                var text = new StringBuilder(hash.Length * 2);
                foreach (var b in hash)
                    text.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return text.ToString();
            }
        }

        /// <summary>DB 행을 만들거나 마지막 본 시각·이름을 고친다. DB 키를 돌려준다.</summary>
        public string TouchDb(string kind, string identity, string label, DateTime nowUtc)
        {
            EnsureWritable();
            var key = DbKey(kind, identity);
            var now = Time(nowUtc);
            using (var connection = Connect(FilePath))
                Execute(connection, @"INSERT INTO db (db_key, kind, label, first_seen, last_seen) VALUES ($key, $kind, $label, $now, $now)
ON CONFLICT(db_key) DO UPDATE SET label = excluded.label, last_seen = excluded.last_seen", null,
                    ("$key", key), ("$kind", kind ?? ""), ("$label", label ?? ""), ("$now", now));
            return key;
        }

        // ---------- 사용자 점검 항목 ----------

        public List<UserCheckItem> LoadUserItems()
        {
            var items = new List<UserCheckItem>();
            using (var connection = Connect(FilePath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT id, kind, category, title, sql, rule, advice, created_at, updated_at FROM check_item ORDER BY category, title, id";
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        items.Add(new UserCheckItem
                        {
                            Id = reader.GetString(0), Kind = reader.GetString(1), Category = reader.GetString(2), Title = reader.GetString(3),
                            Sql = reader.GetString(4), Rule = reader.GetString(5), Advice = reader.IsDBNull(6) ? "" : reader.GetString(6),
                            CreatedAt = reader.GetString(7), UpdatedAt = reader.GetString(8)
                        });
                    }
                }
            }
            return items;
        }

        /// <summary>사용자 항목을 넣거나 고친다(만든 시각은 처음 것 유지).</summary>
        public void SaveUserItem(UserCheckItem item, DateTime nowUtc)
        {
            EnsureWritable();
            if (item == null || string.IsNullOrWhiteSpace(item.Id))
                throw new ArgumentException("사용자 점검 항목의 id가 없습니다.");
            var now = Time(nowUtc);
            using (var connection = Connect(FilePath))
                Execute(connection, @"INSERT INTO check_item (id, kind, category, title, sql, rule, advice, created_at, updated_at)
VALUES ($id, $kind, $category, $title, $sql, $rule, $advice, $now, $now)
ON CONFLICT(id) DO UPDATE SET kind = excluded.kind, category = excluded.category, title = excluded.title, sql = excluded.sql,
  rule = excluded.rule, advice = excluded.advice, updated_at = excluded.updated_at", null,
                    ("$id", item.Id), ("$kind", item.Kind ?? ""), ("$category", item.Category ?? ""), ("$title", item.Title ?? ""),
                    ("$sql", item.Sql ?? ""), ("$rule", item.Rule ?? ""), ("$advice", item.Advice ?? ""), ("$now", now));
        }

        public bool DeleteUserItem(string id)
        {
            EnsureWritable();
            using (var connection = Connect(FilePath))
            using (var tx = connection.BeginTransaction())
            {
                var removed = Execute(connection, "DELETE FROM check_item WHERE id = $id", tx, ("$id", id));
                Execute(connection, "DELETE FROM check_override WHERE item_id = $id", tx, ("$id", id));
                tx.Commit();
                return removed > 0;
            }
        }

        // ---------- 켬·끔·임계값 ----------

        /// <summary>항목별 덮어쓰기: 전체('') 위에 그 DB 값을 칸별로 얹는다(dbKey가 null이면 전체만).</summary>
        public Dictionary<string, CheckOverride> LoadOverrides(string dbKey)
        {
            var merged = new Dictionary<string, CheckOverride>(StringComparer.Ordinal);
            using (var connection = Connect(FilePath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT db_key, item_id, enabled, warn, crit FROM check_override WHERE db_key = '' OR db_key = $key ORDER BY CASE WHEN db_key = '' THEN 0 ELSE 1 END";
                command.Parameters.AddWithValue("$key", dbKey ?? "");
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var id = reader.GetString(1);
                        CheckOverride current;
                        if (!merged.TryGetValue(id, out current))
                            merged[id] = current = new CheckOverride { ItemId = id };
                        if (!reader.IsDBNull(2))
                            current.Enabled = reader.GetInt64(2) != 0;
                        if (!reader.IsDBNull(3))
                            current.Warning = reader.GetDouble(3);
                        if (!reader.IsDBNull(4))
                            current.Critical = reader.GetDouble(4);
                    }
                }
            }
            return merged;
        }

        /// <summary>덮어쓰기 저장(모든 칸이 NULL이면 행을 지운다). dbKey가 null·빈 글이면 전체.</summary>
        public void SaveOverride(string dbKey, CheckOverride value)
        {
            EnsureWritable();
            if (value == null || string.IsNullOrWhiteSpace(value.ItemId))
                throw new ArgumentException("덮어쓸 항목 id가 없습니다.");
            using (var connection = Connect(FilePath))
            {
                if (!value.Enabled.HasValue && !value.Warning.HasValue && !value.Critical.HasValue)
                {
                    Execute(connection, "DELETE FROM check_override WHERE db_key = $key AND item_id = $id", null, ("$key", dbKey ?? ""), ("$id", value.ItemId));
                    return;
                }
                Execute(connection, @"INSERT INTO check_override (db_key, item_id, enabled, warn, crit) VALUES ($key, $id, $enabled, $warn, $crit)
ON CONFLICT(db_key, item_id) DO UPDATE SET enabled = excluded.enabled, warn = excluded.warn, crit = excluded.crit", null,
                    ("$key", dbKey ?? ""), ("$id", value.ItemId), ("$enabled", value.Enabled.HasValue ? (object)(value.Enabled.Value ? 1 : 0) : DBNull.Value),
                    ("$warn", value.Warning.HasValue ? (object)value.Warning.Value : DBNull.Value), ("$crit", value.Critical.HasValue ? (object)value.Critical.Value : DBNull.Value));
            }
        }

        // ---------- 실행 결과 ----------

        /// <summary>실행 하나와 그 결과들을 한 트랜잭션으로 남긴다. 실행 id를 돌려준다.</summary>
        public long SaveRun(string dbKey, DateTime startedUtc, long elapsedMs, string appVersion, IEnumerable<StoredResult> results)
        {
            EnsureWritable();
            using (var connection = Connect(FilePath))
            using (var tx = connection.BeginTransaction())
            {
                Execute(connection, "INSERT INTO check_run (db_key, started_at, elapsed_ms, app_version) VALUES ($key, $at, $ms, $ver)", tx,
                    ("$key", dbKey), ("$at", Time(startedUtc)), ("$ms", elapsedMs), ("$ver", appVersion ?? ""));
                var id = Convert.ToInt64(Scalar(connection, "SELECT last_insert_rowid()", tx), CultureInfo.InvariantCulture);
                foreach (var result in results)
                {
                    Execute(connection, @"INSERT OR REPLACE INTO check_result (run_id, item_id, status, value, row_count, reason)
VALUES ($run, $item, $status, $value, $rows, $reason)", tx,
                        ("$run", id), ("$item", result.ItemId), ("$status", result.Status ?? ""),
                        ("$value", result.Value.HasValue ? (object)result.Value.Value : DBNull.Value),
                        ("$rows", result.RowCount.HasValue ? (object)result.RowCount.Value : DBNull.Value),
                        ("$reason", (object)result.Reason ?? DBNull.Value));
                }
                tx.Commit();
                return id;
            }
        }

        /// <summary>그 DB의 마지막 실행 id(before가 있으면 그보다 앞선 것). 없으면 null.</summary>
        public long? LastRunId(string dbKey, long? before = null)
        {
            using (var connection = Connect(FilePath))
            {
                var value = before.HasValue
                    ? Scalar(connection, "SELECT MAX(id) FROM check_run WHERE db_key = $key AND id < $before", null, ("$key", dbKey), ("$before", before.Value))
                    : Scalar(connection, "SELECT MAX(id) FROM check_run WHERE db_key = $key", null, ("$key", dbKey));
                return value == null || value is DBNull ? (long?)null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
            }
        }

        /// <summary>실행의 시작 시각(UTC). 없으면 null.</summary>
        public DateTime? RunStartedAt(long runId)
        {
            using (var connection = Connect(FilePath))
            {
                var value = Scalar(connection, "SELECT started_at FROM check_run WHERE id = $run", null, ("$run", runId)) as string;
                DateTime at;
                return value != null && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out at)
                    ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : (DateTime?)null;
            }
        }

        /// <summary>남아 있는 실행 수(설정 탭 표시용).</summary>
        public long RunCount()
        {
            using (var connection = Connect(FilePath))
                return Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM check_run"), CultureInfo.InvariantCulture);
        }

        public Dictionary<string, StoredResult> Results(long runId)
        {
            var results = new Dictionary<string, StoredResult>(StringComparer.Ordinal);
            using (var connection = Connect(FilePath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT item_id, status, value, row_count, reason FROM check_result WHERE run_id = $run";
                command.Parameters.AddWithValue("$run", runId);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        results[reader.GetString(0)] = new StoredResult
                        {
                            ItemId = reader.GetString(0), Status = reader.GetString(1), Value = reader.IsDBNull(2) ? (double?)null : reader.GetDouble(2),
                            RowCount = reader.IsDBNull(3) ? (int?)null : reader.GetInt32(3), Reason = reader.IsDBNull(4) ? null : reader.GetString(4)
                        };
                    }
                }
            }
            return results;
        }

        /// <summary>그 시각보다 오래된 실행(결과는 CASCADE)을 지우고 빈 공간을 돌려준다. 지운 실행 수.</summary>
        public int Purge(DateTime olderThanUtc)
        {
            EnsureWritable();
            using (var connection = Connect(FilePath))
            {
                var removed = Execute(connection, "DELETE FROM check_run WHERE started_at < $limit", null, ("$limit", Time(olderThanUtc)));
                if (removed > 0)
                    Execute(connection, "PRAGMA incremental_vacuum");
                return removed;
            }
        }

        // ---------- 도우미 ----------

        public static string Time(DateTime utc)
        {
            return utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        }

        private void EnsureWritable()
        {
            if (ReadOnly)
                throw new InvalidOperationException("이력 파일이 더 새 버전 DB Helper의 것이라 읽기만 합니다: " + FilePath);
        }

        private static SqliteConnection Connect(string path)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
            connection.Open();
            Execute(connection, "PRAGMA busy_timeout = 5000");
            Execute(connection, "PRAGMA foreign_keys = ON");
            return connection;
        }

        private static int Execute(SqliteConnection connection, string sql, SqliteTransaction tx = null, params (string Name, object Value)[] parameters)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = tx;
                command.CommandText = sql;
                foreach (var p in parameters)
                    command.Parameters.AddWithValue(p.Name, p.Value ?? DBNull.Value);
                return command.ExecuteNonQuery();
            }
        }

        private static object Scalar(SqliteConnection connection, string sql, SqliteTransaction tx = null, params (string Name, object Value)[] parameters)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = tx;
                command.CommandText = sql;
                foreach (var p in parameters)
                    command.Parameters.AddWithValue(p.Name, p.Value ?? DBNull.Value);
                return command.ExecuteScalar();
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using MyPlugin;
using Npgsql;
using Xunit;

namespace MyPlugin.Tests
{
    /// <summary>
    /// 성능 모니터: 공급자마다 섹션 계약(공통 id·순서·글·SQL 모양), 권한 오류 판정, 창의 글·열 판단(MonitorLogic).
    /// SQL은 실제 DBMS 없이 모양만 본다 — V$·pg_catalog·sys.dm_*는 SQLite로 흉내 낼 수 없다.
    /// 결과 열 별칭이 화면 머리글이므로 영어 식별자 그대로 보이는 열이 없는지(별칭이 빠진 식), Oracle 별칭이 30바이트를 넘지 않는지도 본다.
    /// </summary>
    public class MonitorTests
    {
        private static readonly IDbProvider[] Real = { DbProviders.Oracle, DbProviders.Postgres, DbProviders.SqlServer };

        // ---------- 섹션 계약 ----------

        [Fact]
        public void EveryProvider_HasCommonSections_InOrder_ThenOwn()
        {
            foreach (var provider in Real)
            {
                var ids = provider.Monitor.Sections.Select(s => s.Id).ToList();
                Assert.Equal(MonitorSections.Common, ids.Take(MonitorSections.Common.Count));
                Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
                foreach (var section in provider.Monitor.Sections)
                {
                    Assert.False(string.IsNullOrWhiteSpace(section.Title), provider.Kind + " " + section.Id + " title");
                    Assert.False(string.IsNullOrWhiteSpace(section.Description), provider.Kind + " " + section.Id + " description");
                    Assert.False(string.IsNullOrWhiteSpace(section.Privilege), provider.Kind + " " + section.Id + " privilege");
                    Assert.StartsWith("SELECT ", section.Sql.TrimStart(), StringComparison.Ordinal);
                    Assert.Equal(section.Id, section.Id.ToLowerInvariant());
                }
            }
            Assert.Contains(PostgresMonitor.Databases, DbProviders.Postgres.Monitor.Sections.Select(s => s.Id));
            Assert.Empty(DbProviders.Generic.Monitor.Sections);
            Assert.Same(NoMonitor.Instance, DbProviders.Generic.Monitor);
        }

        [Fact]
        public void Sections_ReadOnly_NoBinds_NoDataChange()
        {
            foreach (var provider in Real)
            foreach (var section in provider.Monitor.Sections)
            {
                var sql = section.Sql;
                // 바인드 변수 없음(:name·@name). 글자 리터럴('HH24:MI:SS')과 PostgreSQL의 ::text 형변환은 바인드가 아니다
                var bare = Regex.Replace(sql, "'[^']*'", "''").Replace("::", "");
                Assert.DoesNotMatch(@":[A-Za-z_]", bare);
                Assert.DoesNotMatch(@"(?<![@\w])@(?!@)[A-Za-z_]", bare);
                foreach (var word in new[] { "INSERT ", "UPDATE ", "DELETE ", "DROP ", "ALTER ", "TRUNCATE ", "EXEC ", "DBCC " })
                    Assert.False(Regex.IsMatch(sql, @"\b" + word, RegexOptions.IgnoreCase), provider.Kind + " " + section.Id + ": " + word);
                Assert.DoesNotContain(";", sql);
            }
        }

        [Fact]
        public void InfoSections_ReturnTwoColumns_ItemAndValue_Ordered()
        {
            foreach (var provider in Real)
            {
                var info = provider.Monitor.Sections.First(s => s.Id == MonitorSections.Info);
                Assert.Matches(@"^SELECT\s+[\[""]항목[\]""],\s*[\[""]값[\]""]\s+FROM\s*\(", info.Sql.Trim());
                Assert.EndsWith("ORDER BY ord", info.Sql.Trim(), StringComparison.OrdinalIgnoreCase);
                Assert.Contains("UNION ALL", info.Sql);
            }
        }

        [Fact]
        public void StorageSections_HaveSizeColumns()
        {
            foreach (var provider in Real)
            {
                var storage = provider.Monitor.Sections.First(s => s.Id == MonitorSections.Storage);
                Assert.Contains("크기(MB)", storage.Sql);
            }
            // 남은 용량·사용률은 한도가 있는 DBMS에서만(PostgreSQL 테이블스페이스는 파일 시스템이 한도)
            foreach (var provider in new[] { DbProviders.Oracle, DbProviders.SqlServer })
            {
                var sql = provider.Monitor.Sections.First(s => s.Id == MonitorSections.Storage).Sql;
                Assert.Contains("남음(MB)", sql);
                Assert.Contains("사용률(%)", sql);
                Assert.Contains("최대(MB)", sql);
            }
        }

        [Fact]
        public void Oracle_Sections_UseExpectedViews_AndAliasesFit11g()
        {
            var sections = DbProviders.Oracle.Monitor.Sections.ToDictionary(s => s.Id);
            Assert.Contains("V$DATABASE", sections[MonitorSections.Info].Sql);
            Assert.Contains("V$INSTANCE", sections[MonitorSections.Info].Sql);
            Assert.Contains("DBA_FREE_SPACE", sections[MonitorSections.Storage].Sql);
            Assert.Contains("DBA_TEMP_FILES", sections[MonitorSections.Storage].Sql);
            Assert.Contains("V$LOG ", sections[MonitorSections.Log].Sql);
            Assert.Contains("V$LOGFILE", sections[MonitorSections.Log].Sql);
            Assert.Contains("V$LOCK", sections[MonitorSections.Locks].Sql);
            Assert.Contains("l.BLOCK > 0 OR l.REQUEST > 0", sections[MonitorSections.Locks].Sql);
            Assert.Contains("V$SESSION", sections[MonitorSections.Sessions].Sql);
            Assert.Contains("s.TYPE = 'USER'", sections[MonitorSections.Sessions].Sql);
            // 11g의 식별자 한도 30바이트(AL32UTF8: 한글 3바이트)
            foreach (var section in sections.Values)
            foreach (Match alias in Regex.Matches(section.Sql, "AS \"([^\"]+)\""))
                Assert.True(System.Text.Encoding.UTF8.GetByteCount(alias.Groups[1].Value) <= 30, section.Id + " 별칭 " + alias.Groups[1].Value);
        }

        [Fact]
        public void Postgres_Sections_UseExpectedViews()
        {
            var sections = DbProviders.Postgres.Monitor.Sections.ToDictionary(s => s.Id);
            Assert.Contains("version()", sections[MonitorSections.Info].Sql);
            Assert.Contains("pg_tablespace_size", sections[MonitorSections.Storage].Sql);
            Assert.Contains("pg_current_wal_lsn()", sections[MonitorSections.Log].Sql);
            Assert.Contains("pg_is_in_recovery()", sections[MonitorSections.Log].Sql);
            Assert.Contains("pg_stat_archiver", sections[MonitorSections.Log].Sql);
            Assert.Contains("pg_blocking_pids(a.pid)", sections[MonitorSections.Locks].Sql);
            Assert.Contains("backend_type = 'client backend'", sections[MonitorSections.Sessions].Sql);
            Assert.Contains("pg_database_size(p.oid)", sections[PostgresMonitor.Databases].Sql);
        }

        [Fact]
        public void SqlServer_Sections_UseExpectedViews()
        {
            var sections = DbProviders.SqlServer.Monitor.Sections.ToDictionary(s => s.Id);
            Assert.Contains("SERVERPROPERTY('ProductVersion')", sections[MonitorSections.Info].Sql);
            Assert.Contains("sys.database_files", sections[MonitorSections.Storage].Sql);
            Assert.Contains("FILEPROPERTY(f.name, 'SpaceUsed')", sections[MonitorSections.Storage].Sql);
            Assert.Contains("sys.dm_db_log_space_usage", sections[MonitorSections.Log].Sql);
            Assert.Contains("log_reuse_wait_desc", sections[MonitorSections.Log].Sql);
            Assert.Contains("blocking_session_id <> 0", sections[MonitorSections.Locks].Sql);
            Assert.Contains("sys.dm_exec_sql_text", sections[MonitorSections.Locks].Sql);
            Assert.Contains("is_user_process = 1", sections[MonitorSections.Sessions].Sql);
        }

        [Fact]
        public void MonitorSection_RejectsBlankIdTitleSql()
        {
            Assert.Throws<ArgumentException>(() => new MonitorSection("", "t", "d", "p", "SELECT 1"));
            Assert.Throws<ArgumentException>(() => new MonitorSection("x", " ", "d", "p", "SELECT 1"));
            Assert.Throws<ArgumentException>(() => new MonitorSection("x", "t", "d", "p", null));
            var section = new MonitorSection("x", "t", null, null, "SELECT 1");
            Assert.Equal("", section.Description);
            Assert.Equal("", section.Privilege);
        }

        // ---------- 권한 오류 판정 ----------

        [Fact]
        public void IsPermissionDenied_ByProvider()
        {
            Assert.True(DbProviders.Postgres.IsPermissionDenied(new PostgresException("denied", "ERROR", "ERROR", PostgresErrorCodes.InsufficientPrivilege)));
            Assert.False(DbProviders.Postgres.IsPermissionDenied(new PostgresException("x", "ERROR", "ERROR", "42P01")));
            Assert.False(DbProviders.Postgres.IsPermissionDenied(new InvalidOperationException("x")));
            // OracleException·SqlException은 밖에서 만들 수 없다 — 다른 예외는 false
            Assert.False(DbProviders.Oracle.IsPermissionDenied(new InvalidOperationException("x")));
            Assert.False(DbProviders.Oracle.IsPermissionDenied(null));
            Assert.False(DbProviders.SqlServer.IsPermissionDenied(new InvalidOperationException("x")));
            Assert.False(DbProviders.Generic.IsPermissionDenied(new InvalidOperationException("x")));
        }

        // ---------- MonitorLogic ----------

        [Fact]
        public void MenuText_ShowsDbName_EscapesUnderscore()
        {
            Assert.Equal("성능 모니터(_P)… (트리에서 DB를 고르세요)", MonitorLogic.MenuText(null));
            Assert.Equal("성능 모니터 — 운영_DB(_P)…", MonitorLogic.MenuText("운영_DB").Replace("__", "_"));
            Assert.Contains("운영__DB", MonitorLogic.MenuText("운영_DB"));
        }

        [Fact]
        public void RefreshChoices_AndTexts()
        {
            Assert.Equal(0, MonitorLogic.RefreshSeconds[0]);
            Assert.Equal(new[] { 0, 5, 15, 60 }, MonitorLogic.RefreshSeconds);
            Assert.Equal("끔", MonitorLogic.RefreshText(0));
            Assert.Equal("15초", MonitorLogic.RefreshText(15));
            Assert.Equal("갱신 14:05:31 · 12행", MonitorLogic.RefreshedText(new DateTime(2026, 10, 19, 14, 5, 31), 12));
            Assert.Equal("갱신 09:00:00 · 앞 2,000행 · 더 있을 수 있음", MonitorLogic.RefreshedText(new DateTime(2026, 10, 19, 9, 0, 0), MonitorLogic.MaxRows));
            Assert.Equal("갱신 09:00:00 · 0행", MonitorLogic.RefreshedText(new DateTime(2026, 10, 19, 9, 0, 0), 0));
        }

        [Fact]
        public void StatusTexts()
        {
            var section = DbProviders.Oracle.Monitor.Sections[0];
            Assert.Equal("권한이 없어 가져오지 못했습니다 — 필요 권한: " + OracleMonitor.Privilege + " (ORA-00942: table or view does not exist)",
                MonitorLogic.PermissionDeniedText(section, "ORA-00942: table or view does not exist"));
            Assert.Equal("권한이 없어 가져오지 못했습니다", MonitorLogic.PermissionDeniedText(new MonitorSection("x", "t", null, null, "SELECT 1"), null));
            Assert.Equal("가져오지 못했습니다 (끊김)", MonitorLogic.FailedText(" 끊김 "));
            Assert.Equal("가져오지 못했습니다", MonitorLogic.FailedText(""));
            Assert.Contains("[새로 고침]", MonitorLogic.NoSessionText);
            Assert.Equal(" · 0.8초", MonitorLogic.ElapsedSuffix(TimeSpan.FromMilliseconds(812)));
            Assert.Equal(" · 12.0초", MonitorLogic.ElapsedSuffix(TimeSpan.FromSeconds(12)));
            Assert.Equal(" · 0.0초", MonitorLogic.ElapsedSuffix(TimeSpan.FromSeconds(-1)));
            var files = new MonitorDetail("files", "t", "d", "SELECT 1 WHERE x = :name");
            var segments = new MonitorDetail("segments", "t", "d", "SELECT 1 WHERE x = :name");
            Assert.NotEqual(MonitorLogic.DetailCacheKey(files, "MES_DATA"), MonitorLogic.DetailCacheKey(segments, "MES_DATA"));
            Assert.NotEqual(MonitorLogic.DetailCacheKey(files, "MES_DATA"), MonitorLogic.DetailCacheKey(files, "MES_IDX"));
            Assert.Equal(MonitorLogic.DetailCacheKey(files, "A"), MonitorLogic.DetailCacheKey(files, "A"));
            Assert.NotEqual(MonitorLogic.DetailCacheKey(files, "A"), MonitorLogic.DetailCacheKey(files, null));
            Assert.Equal("Generic 접속에서는 성능 모니터를 아직 지원하지 않습니다.", MonitorLogic.NotSupportedMessage("Generic"));
        }

        [Fact]
        public void Columns_NumericWhenEveryValueIsNumber_NullsIgnored()
        {
            var table = new QueryTable();
            table.Columns.AddRange(new[] { "SID", "사용자", "크기(MB)", "값", "모두NULL", "빈열" });
            table.Rows.Add(new[] { "12", "SCOTT", "1024.5", "ORCL", null, "" });
            table.Rows.Add(new[] { "7", null, null, "1521", null, "" });
            table.Rows.Add(new[] { "-3", "HR", "0", "12.1.0.2.0", null, "" });

            var columns = MonitorLogic.Columns(table);

            Assert.Equal(table.Columns, columns.Select(c => c.Name));
            Assert.All(columns, c => Assert.Equal("", c.TypeLabel));
            Assert.True(columns[0].IsNumeric);
            Assert.False(columns[1].IsNumeric);
            Assert.True(columns[2].IsNumeric);
            Assert.False(columns[3].IsNumeric);   // 글과 수가 섞임
            Assert.False(columns[4].IsNumeric);   // 값이 없으면 글 열
            Assert.False(columns[5].IsNumeric);   // 빈 글은 수가 아니다
        }

        [Fact]
        public void Columns_EmptyTable_AndShortRows()
        {
            var empty = new QueryTable();
            empty.Columns.Add("A");
            Assert.Single(MonitorLogic.Columns(empty));
            Assert.False(MonitorLogic.Columns(empty)[0].IsNumeric);
            var ragged = new QueryTable();
            ragged.Columns.AddRange(new[] { "A", "B" });
            ragged.Rows.Add(new[] { "1" });
            Assert.True(MonitorLogic.Columns(ragged)[0].IsNumeric);
            Assert.False(MonitorLogic.Columns(ragged)[1].IsNumeric);
            Assert.Throws<ArgumentNullException>(() => MonitorLogic.Columns(null));
        }

        // ---------- 행 상세 목록 ----------

        [Fact]
        public void StorageDetails_EveryProvider_KeyColumnInSql_DetailBindsName_ReadOnly()
        {
            foreach (var provider in Real)
            {
                var storage = provider.Monitor.Sections.First(s => s.Id == MonitorSections.Storage);
                Assert.NotEmpty(storage.Details);
                Assert.False(string.IsNullOrEmpty(storage.KeyColumn));
                Assert.Contains("\"" + storage.KeyColumn + "\"", storage.Sql.Replace("[", "\"").Replace("]", "\""));
                var marker = provider == DbProviders.SqlServer ? "@" + MonitorDetail.KeyParameter : ":" + MonitorDetail.KeyParameter;
                foreach (var detail in storage.Details)
                {
                    Assert.Contains(marker, detail.Sql);
                    Assert.False(string.IsNullOrWhiteSpace(detail.Title));
                    Assert.False(string.IsNullOrWhiteSpace(detail.Description));
                    var bare = Regex.Replace(detail.Sql, "'[^']*'", "''");
                    foreach (var word in new[] { "INSERT ", "UPDATE ", "DELETE ", "DROP ", "ALTER ", "TRUNCATE ", "EXEC " })
                        Assert.False(Regex.IsMatch(bare, @"\b" + word, RegexOptions.IgnoreCase), provider.Kind + " " + detail.Id + ": " + word);
                    // 행 수 제한이 SQL 안에 있다(파일 목록은 테이블스페이스당 몇 개라 제한 없음)
                    Assert.Matches(@"ROWNUM <= 200|LIMIT 200|TOP \(200\)|ORDER BY 1", detail.Sql);
                    Assert.False(detail.AsText);
                    var query = detail.Query("MES_DATA");
                    Assert.Same(detail.Sql, query.Sql);
                    Assert.Single(query.Parameters);
                    Assert.Equal(MonitorDetail.KeyParameter, query.Parameters[0].Key);
                    Assert.Equal("MES_DATA", query.Parameters[0].Value);
                }
                Assert.Equal(storage.Details.Count, storage.Details.Select(d => d.Id).Distinct().Count());
            }
            Assert.Equal(new[] { "files", "segments" }, DbProviders.Oracle.Monitor.Sections.First(s => s.Id == MonitorSections.Storage).Details.Select(d => d.Id));
            Assert.Contains("DBA_SEGMENTS", DbProviders.Oracle.Monitor.Sections.First(s => s.Id == MonitorSections.Storage).Details[1].Sql);
            Assert.Contains("reltablespace = 0", DbProviders.Postgres.Monitor.Sections.First(s => s.Id == MonitorSections.Storage).Details[0].Sql);
            Assert.Contains("sys.allocation_units", DbProviders.SqlServer.Monitor.Sections.First(s => s.Id == MonitorSections.Storage).Details[0].Sql);
            // 기본 정보·로그·대기 이벤트(·데이터베이스)에는 상세·조치가 없다
            foreach (var provider in Real)
            foreach (var section in provider.Monitor.Sections.Where(s => s.Id == MonitorSections.Info || s.Id == MonitorSections.Log || s.Id == MonitorSections.Waits || s.Id == PostgresMonitor.Databases))
            {
                Assert.Empty(section.Details);
                Assert.Null(section.KeyColumn);
                Assert.Empty(section.Actions);
            }
            Assert.Empty(DbProviders.Oracle.Monitor.Sections.First(s => s.Id == MonitorSections.Storage).Actions);
        }

        [Fact]
        public void Oracle_Storage_TempAndUndoUsage_UseCurrentUsageViews()
        {
            var sql = DbProviders.Oracle.Monitor.Sections.First(s => s.Id == MonitorSections.Storage).Sql;
            Assert.Contains("V$SORT_SEGMENT", sql);
            Assert.Contains("USED_BLOCKS", sql);
            Assert.Contains("t.BLOCK_SIZE", sql);
            Assert.Contains("DBA_UNDO_EXTENTS WHERE STATUS IN ('ACTIVE', 'UNEXPIRED')", sql);
            Assert.Contains("WHERE t.CONTENTS <> 'UNDO'", sql);
            Assert.Contains("WHERE t.CONTENTS = 'UNDO'", sql);
            Assert.DoesNotContain("V$TEMP_SPACE_HEADER", sql);
        }

        // ---------- 세션 상세·세션 종료 ----------

        [Fact]
        public void SessionsAndLocks_HaveSessionDetails_TextForSqlAndPlan()
        {
            foreach (var provider in Real)
            foreach (var id in new[] { MonitorSections.Sessions, MonitorSections.Locks })
            {
                var section = provider.Monitor.Sections.First(s => s.Id == id);
                Assert.NotEmpty(section.Details);
                Assert.False(string.IsNullOrEmpty(section.KeyColumn));
                Assert.Contains("\"" + section.KeyColumn + "\"", section.Sql.Replace("[", "\"").Replace("]", "\""));
                var sql = section.Details.First(d => d.Id == "sql");
                Assert.True(sql.AsText);
                var marker = provider == DbProviders.SqlServer ? "@" + MonitorDetail.KeyParameter : ":" + MonitorDetail.KeyParameter;
                foreach (var detail in section.Details)
                    Assert.Contains(marker, detail.Sql);
                Assert.Single(section.Actions);
                Assert.Equal("kill", section.Actions[0].Id);
                Assert.EndsWith("…", section.Actions[0].Title);
                Assert.False(string.IsNullOrWhiteSpace(section.Actions[0].Warning));
                Assert.Contains(section.KeyColumn, section.Actions[0].Columns);
            }
            Assert.True(DbProviders.Oracle.Monitor.Sections.First(s => s.Id == MonitorSections.Sessions).Details.First(d => d.Id == "plan").AsText);
            Assert.Contains("DBMS_XPLAN.DISPLAY_CURSOR", DbProviders.Oracle.Monitor.Sections.First(s => s.Id == MonitorSections.Sessions).Details.First(d => d.Id == "plan").Sql);
            Assert.Contains("V$SESSTAT", DbProviders.Oracle.Monitor.Sections.First(s => s.Id == MonitorSections.Sessions).Details.First(d => d.Id == "stats").Sql);
            // PostgreSQL은 다른 세션의 실행 계획을 주지 않는다
            Assert.DoesNotContain(DbProviders.Postgres.Monitor.Sections.First(s => s.Id == MonitorSections.Sessions).Details, d => d.Id == "plan");
            Assert.Contains("CAST(:name AS integer)", DbProviders.Postgres.Monitor.Sections.First(s => s.Id == MonitorSections.Sessions).Details[0].Sql);
            Assert.Contains("sys.dm_exec_text_query_plan", DbProviders.SqlServer.Monitor.Sections.First(s => s.Id == MonitorSections.Sessions).Details.First(d => d.Id == "plan").Sql);
            Assert.Contains("most_recent_sql_handle", DbProviders.SqlServer.Monitor.Sections.First(s => s.Id == MonitorSections.Sessions).Details[0].Sql);
        }

        [Fact]
        public void KillActions_BuildStatement_OnlyFromDigits()
        {
            var oracle = DbProviders.Oracle.Monitor.Sections.First(s => s.Id == MonitorSections.Sessions).Actions[0];
            Assert.Equal(new[] { "SID", "SERIAL#" }, oracle.Columns);
            Assert.Equal("ALTER SYSTEM KILL SESSION '412,53021' IMMEDIATE", oracle.Build(new[] { "412", "53021" }));
            Assert.Null(oracle.Build(new[] { "412", "53021' OR 1=1 --" }));
            Assert.Null(oracle.Build(new[] { "412", null }));
            Assert.Null(oracle.Build(new[] { "412" }));
            Assert.Null(oracle.Build(null));
            var pg = DbProviders.Postgres.Monitor.Sections.First(s => s.Id == MonitorSections.Sessions).Actions[0];
            Assert.Equal("SELECT pg_terminate_backend(412)", pg.Build(new[] { "412" }));
            Assert.Null(pg.Build(new[] { "412; DROP TABLE x" }));
            var mssql = DbProviders.SqlServer.Monitor.Sections.First(s => s.Id == MonitorSections.Sessions).Actions[0];
            Assert.Equal("KILL 57", mssql.Build(new[] { "57" }));
            Assert.Null(mssql.Build(new[] { "-57" }));
            Assert.True(MonitorAction.IsDigits("0123"));
            Assert.False(MonitorAction.IsDigits(""));
            Assert.False(MonitorAction.IsDigits(null));
            Assert.False(MonitorAction.IsDigits("12 "));
            Assert.Throws<ArgumentException>(() => new MonitorAction("k", "t", new string[0], "w", v => "x"));
            Assert.Throws<ArgumentException>(() => new MonitorAction("k", "t", new[] { "A" }, "w", null));
        }

        [Fact]
        public void ActionValues_DetailText_ActionTexts()
        {
            var columns = new List<ResultColumn> { new ResultColumn { Name = "SID" }, new ResultColumn { Name = "SERIAL#" }, new ResultColumn { Name = "사용자" } };
            var action = DbProviders.Oracle.Monitor.Sections.First(s => s.Id == MonitorSections.Sessions).Actions[0];
            Assert.Equal(new[] { "412", "53021" }, MonitorLogic.ActionValues(columns, new[] { "412", "53021", "SCOTT" }, action));
            Assert.Null(MonitorLogic.ActionValues(columns, new[] { "412", null, "SCOTT" }, action));
            Assert.Null(MonitorLogic.ActionValues(new List<ResultColumn> { new ResultColumn { Name = "SID" } }, new[] { "412" }, action));
            Assert.Null(MonitorLogic.ActionValues(columns, new[] { "412", "1", "x" }, null));

            var table = new QueryTable();
            table.Columns.Add("PLAN_TABLE_OUTPUT");
            table.Rows.Add(new[] { "Plan hash value: 1" });
            table.Rows.Add(new[] { (string)null });
            table.Rows.Add(new[] { "| 0 | SELECT STATEMENT |" });
            Assert.Equal("Plan hash value: 1" + Environment.NewLine + "" + Environment.NewLine + "| 0 | SELECT STATEMENT |", MonitorLogic.DetailText(table));
            Assert.Equal("", MonitorLogic.DetailText(new QueryTable()));
            Assert.Equal("", MonitorLogic.DetailText(null));
            Assert.Equal("실행함: KILL 57", MonitorLogic.ActionDoneText(" KILL 57 "));
            Assert.Contains("읽기 전용", MonitorLogic.ActionReadOnlyText);
        }

        [Fact]
        public void MonitorSection_DetailsRequireKeyColumn_DetailRejectsBlank()
        {
            var detail = new MonitorDetail("d", "t", null, "SELECT 1 WHERE x = :name");
            Assert.Equal("", detail.Description);
            Assert.Throws<ArgumentException>(() => new MonitorSection("x", "t", "d", "p", "SELECT 1", null, null, new[] { detail }));
            Assert.Throws<ArgumentException>(() => new MonitorSection("x", "t", "d", "p", "SELECT 1", null, " ", new[] { detail }));
            var ok = new MonitorSection("x", "t", "d", "p", "SELECT 1", null, "K", new[] { detail });
            Assert.Equal("K", ok.KeyColumn);
            Assert.Single(ok.Details);
            // 상세가 없으면 keyColumn은 버린다
            Assert.Null(new MonitorSection("x", "t", "d", "p", "SELECT 1", null, "K").KeyColumn);
            Assert.Throws<ArgumentException>(() => new MonitorDetail("", "t", "d", "SELECT 1"));
            Assert.Throws<ArgumentException>(() => new MonitorDetail("d", "t", "d", " "));
            Assert.Equal("", detail.Query(null).Parameters[0].Value);
        }

        [Fact]
        public void KeyOf_DetailTexts()
        {
            var columns = new List<ResultColumn> { new ResultColumn { Name = "테이블스페이스" }, new ResultColumn { Name = "크기(MB)" } };
            Assert.Equal("MES_DATA", MonitorLogic.KeyOf(columns, new[] { "MES_DATA", "100" }, "테이블스페이스"));
            Assert.Null(MonitorLogic.KeyOf(columns, new[] { null, "100" }, "테이블스페이스"));
            Assert.Null(MonitorLogic.KeyOf(columns, new[] { "MES_DATA" }, "크기(MB)"));
            Assert.Null(MonitorLogic.KeyOf(columns, new[] { "MES_DATA", "100" }, "없는열"));
            Assert.Null(MonitorLogic.KeyOf(null, new[] { "x" }, "테이블스페이스"));
            Assert.Null(MonitorLogic.KeyOf(columns, null, "테이블스페이스"));
            var detail = new MonitorDetail("files", "데이터 파일", "d", "SELECT 1 WHERE x = :name");
            Assert.Equal("데이터 파일 — MES_DATA", MonitorLogic.DetailTitleText(detail, "MES_DATA"));
            Assert.Equal("데이터 파일 — (없음)", MonitorLogic.DetailTitleText(detail, null));
            Assert.Equal("상세 — X", MonitorLogic.DetailTitleText(null, "X"));
            Assert.Equal("없음", MonitorLogic.DetailRowsText(0));
            Assert.Equal("1,200행", MonitorLogic.DetailRowsText(1200));
            Assert.Equal("표의 행이나 도넛을 누르면 상세 목록을 보입니다.", MonitorLogic.DetailHintText);
        }

        /// <summary>상세 조회 흐름(SQLite): 바인드 name으로 행을 걸러 QueryAsync로 읽는다.</summary>
        [Fact]
        public async System.Threading.Tasks.Task Detail_QueryAsync_BindsKey()
        {
            using (var connection = new SqliteConnection("Data Source=:memory:"))
            using (var session = new DbSession(connection))
            {
                await session.OpenAsync();
                var detail = new MonitorDetail("d", "t", "d", "SELECT 'f_' || :name AS \"파일\", 7 AS \"크기(MB)\" WHERE :name = 'MES_DATA'");
                var rows = await session.QueryAsync(detail.Query("MES_DATA"), r => new[] { r.GetString(0), r.GetValue(1).ToString() });
                Assert.Equal(new[] { "f_MES_DATA", "7" }, rows.Single());
                Assert.Empty(await session.QueryAsync(detail.Query("OTHER"), r => r.GetString(0)));
            }
        }

        // ---------- 느린 SQL · 대기 이벤트 ----------

        [Fact]
        public void TopSql_EveryProvider_Top50_ByElapsed_WithSqlDetail()
        {
            foreach (var provider in Real)
            {
                var top = provider.Monitor.Sections.First(s => s.Id == MonitorSections.TopSql);
                Assert.Equal("느린 SQL", top.Title);
                Assert.Matches(@"ROWNUM <= 50|LIMIT 50|TOP \(50\)", top.Sql);
                Assert.Contains("소요(초)", top.Sql);
                Assert.Contains("실행당(ms)", top.Sql);
                Assert.Contains("\"SQL\"", top.Sql.Replace("[", "\"").Replace("]", "\""));
                Assert.False(string.IsNullOrEmpty(top.KeyColumn));
                Assert.Contains("\"" + top.KeyColumn + "\"", top.Sql.Replace("[", "\"").Replace("]", "\""));
                var sql = top.Details.First(d => d.Id == "sql");
                Assert.True(sql.AsText);
                Assert.Empty(top.Actions);
                Assert.Null(top.Chart);
                var waits = provider.Monitor.Sections.First(s => s.Id == MonitorSections.Waits);
                Assert.Equal("대기 이벤트", waits.Title);
                Assert.Empty(waits.Details);
            }
            var oracle = DbProviders.Oracle.Monitor.Sections.First(s => s.Id == MonitorSections.TopSql);
            Assert.Contains("V$SQLAREA", oracle.Sql);
            Assert.Contains("ORDER BY a.ELAPSED_TIME DESC", oracle.Sql);
            Assert.Equal(new[] { "sql", "plan", "children" }, oracle.Details.Select(d => d.Id));
            Assert.Contains("DISPLAY_CURSOR(:name, NULL, 'TYPICAL')", oracle.Details[1].Sql);
            Assert.Contains("WAIT_CLASS <> 'Idle'", DbProviders.Oracle.Monitor.Sections.First(s => s.Id == MonitorSections.Waits).Sql);

            var pg = DbProviders.Postgres.Monitor.Sections.First(s => s.Id == MonitorSections.TopSql);
            Assert.Contains("pg_stat_statements", pg.Sql);
            Assert.Contains("total_exec_time", pg.Sql);
            Assert.Contains("pg_stat_statements", pg.Privilege);
            Assert.Contains("CAST(:name AS bigint)", pg.Details[0].Sql);
            Assert.Contains("a.state = 'active'", DbProviders.Postgres.Monitor.Sections.First(s => s.Id == MonitorSections.Waits).Sql);

            var mssql = DbProviders.SqlServer.Monitor.Sections.First(s => s.Id == MonitorSections.TopSql);
            Assert.Contains("sys.dm_exec_query_stats", mssql.Sql);
            Assert.Contains("statement_start_offset", mssql.Sql);
            Assert.Equal("계획 핸들", mssql.KeyColumn);
            Assert.Contains("CONVERT(varbinary(64), @name, 1)", mssql.Details[0].Sql);
            Assert.Contains("CONVERT(varbinary(64), @name, 1), 0, -1", mssql.Details[1].Sql);
            Assert.Contains("sys.dm_os_wait_stats", DbProviders.SqlServer.Monitor.Sections.First(s => s.Id == MonitorSections.Waits).Sql);
            Assert.Contains("NOT LIKE 'BROKER%'", DbProviders.SqlServer.Monitor.Sections.First(s => s.Id == MonitorSections.Waits).Sql);
        }

        // ---------- 필요 조건(확장 설치·버전) ----------

        [Fact]
        public void Requirements_OnlyPostgresTopSql_VersionThenExtension()
        {
            foreach (var provider in Real)
            foreach (var section in provider.Monitor.Sections)
            {
                if (provider == DbProviders.Postgres && section.Id == MonitorSections.TopSql)
                    continue;
                Assert.Empty(section.Requirements);
            }
            var top = DbProviders.Postgres.Monitor.Sections.First(s => s.Id == MonitorSections.TopSql);
            Assert.Equal(2, top.Requirements.Count);
            Assert.Contains("server_version_num", top.Requirements[0].Sql);
            Assert.Contains("130000", top.Requirements[0].Sql);
            Assert.Contains("13 이상", top.Requirements[0].Message);
            Assert.Contains("pg_extension WHERE extname = 'pg_stat_statements'", top.Requirements[1].Sql);
            Assert.Contains("CREATE EXTENSION pg_stat_statements", top.Requirements[1].Message);
            Assert.Contains("shared_preload_libraries", top.Requirements[1].Message);
            // 로컬호스트에서 메시지 탭에 남길 설치 절차·링크
            Assert.Contains("https://www.postgresql.org/docs/current/pgstatstatements.html", top.Requirements[1].InstallGuide);
            Assert.Contains("SHOW config_file;", top.Requirements[1].InstallGuide);
            Assert.Contains("CREATE EXTENSION pg_stat_statements;", top.Requirements[1].InstallGuide);
            Assert.Contains("https://www.postgresql.org/download/", top.Requirements[0].InstallGuide);
            Assert.Equal("", new MonitorRequirement("SELECT 1", "m").InstallGuide);
            Assert.Throws<ArgumentException>(() => new MonitorRequirement("", "m"));
            Assert.Throws<ArgumentException>(() => new MonitorRequirement("SELECT 1", " "));
        }

        [Fact]
        public void RequirementMet_ByFirstValue_AndMissingText()
        {
            Assert.False(MonitorLogic.RequirementMet(null));
            Assert.False(MonitorLogic.RequirementMet(new QueryTable()));
            foreach (var value in new[] { null, "", "0", "false", "f", "FALSE", "no", "N" })
            {
                var table = new QueryTable();
                table.Columns.Add("x");
                table.Rows.Add(new[] { value });
                Assert.False(MonitorLogic.RequirementMet(table), value ?? "null");
            }
            foreach (var value in new[] { "1", "true", "t", "yes", "pg_stat_statements", "2" })
            {
                var table = new QueryTable();
                table.Columns.Add("x");
                table.Rows.Add(new[] { value });
                Assert.True(MonitorLogic.RequirementMet(table), value);
            }
            var req = new MonitorRequirement("SELECT 1", " 확장을 설치하세요 ");
            Assert.Equal("설치·설정 필요 — 확장을 설치하세요", MonitorLogic.RequirementMissingText(req));
            Assert.Equal("설치·설정 필요 — 확장을 설치하세요 (설치 절차는 메시지 탭에)", MonitorLogic.RequirementMissingText(req, true));
        }

        [Fact]
        public void IsLocalHost_AndInstallGuideMessage()
        {
            foreach (var host in new[] { "localhost", "LOCALHOST", " localhost ", "127.0.0.1", "127.1.2.3", "::1", "[::1]", "MYPC", "mypc", "mypc.corp.local" })
                Assert.True(MonitorLogic.IsLocalHost(host, "MYPC"), host);
            foreach (var host in new[] { null, "", "db-prod.local", "10.0.0.5", "128.0.0.1", "mypc2", "localhost.example.com" })
                Assert.False(MonitorLogic.IsLocalHost(host, "MYPC"), host ?? "null");
            // machineName을 안 주면 이 PC 이름을 쓴다
            Assert.True(MonitorLogic.IsLocalHost(Environment.MachineName));

            var section = DbProviders.Postgres.Monitor.Sections.First(s => s.Id == MonitorSections.TopSql);
            var message = MonitorLogic.InstallGuideMessage(section, section.Requirements[1]);
            Assert.StartsWith("[성능 모니터 · 느린 SQL] 설치·설정 필요: pg_stat_statements 확장이", message);
            Assert.Contains(Environment.NewLine + "  1. 설정 파일 위치 확인: SHOW config_file;", message);
            Assert.Contains("https://www.postgresql.org/docs/current/pgstatstatements.html", message);
            Assert.DoesNotContain("\\n", message);
            Assert.Null(MonitorLogic.InstallGuideMessage(section, new MonitorRequirement("SELECT 1", "m")));
            Assert.Null(MonitorLogic.InstallGuideMessage(section, null));
        }

        /// <summary>점검 SQL을 QueryTableAsync(1행)로 읽는 흐름(SQLite): 행 없음 → 미충족, 1 → 충족.</summary>
        [Fact]
        public async System.Threading.Tasks.Task Requirement_Check_WithSqlite()
        {
            using (var connection = new SqliteConnection("Data Source=:memory:"))
            using (var session = new DbSession(connection))
            {
                await session.OpenAsync();
                Assert.False(MonitorLogic.RequirementMet(await session.QueryTableAsync("SELECT 1 WHERE 1 = 0", 1)));
                Assert.False(MonitorLogic.RequirementMet(await session.QueryTableAsync("SELECT CASE WHEN 120000 >= 130000 THEN 1 ELSE 0 END", 1)));
                Assert.True(MonitorLogic.RequirementMet(await session.QueryTableAsync("SELECT CASE WHEN 160000 >= 130000 THEN 1 ELSE 0 END", 1)));
            }
        }

        // ---------- 사용률 차트(대시보드) ----------

        [Fact]
        public void StorageChart_OracleAndSqlServer_ColumnsExistInSql_PostgresNone()
        {
            foreach (var provider in new[] { DbProviders.Oracle, DbProviders.SqlServer })
            {
                var storage = provider.Monitor.Sections.First(s => s.Id == MonitorSections.Storage);
                Assert.NotNull(storage.Chart);
                foreach (var column in new[] { storage.Chart.NameColumn, storage.Chart.UsedColumn, storage.Chart.TotalColumn })
                    Assert.Contains("\"" + column + "\"", storage.Sql.Replace("[", "\"").Replace("]", "\""));
            }
            Assert.Null(DbProviders.Postgres.Monitor.Sections.First(s => s.Id == MonitorSections.Storage).Chart);
            foreach (var provider in Real)
            foreach (var section in provider.Monitor.Sections.Where(s => s.Id != MonitorSections.Storage))
                Assert.Null(section.Chart);
            Assert.Throws<ArgumentException>(() => new MonitorUsageChart("a", "", "c"));
        }

        [Fact]
        public void Usage_SlicesFromTable_SkipsUnreadable_ClampsAndCaps()
        {
            var chart = new MonitorUsageChart("테이블스페이스", "사용(MB)", "크기(MB)");
            var table = new QueryTable();
            table.Columns.AddRange(new[] { "테이블스페이스", "종류", "크기(MB)", "사용(MB)" });
            table.Rows.Add(new[] { "MES_DATA", "PERMANENT", "204800", "191562" });
            table.Rows.Add(new[] { "EMPTY", "PERMANENT", "0", "0" });          // 전체 0 → 건너뜀
            table.Rows.Add(new[] { "BROKEN", "PERMANENT", "100", null });       // 사용 NULL → 건너뜀
            table.Rows.Add(new[] { "OVER", "PERMANENT", "100", "120" });        // 100으로 자름
            table.Rows.Add(new[] { "SHORT" });                                   // 열 부족 → 건너뜀
            table.Rows.Add(new[] { null, "UNDO", "8192", "1517.5" });

            var slices = MonitorLogic.Usage(table, chart, MonitorLogic.MaxCharts);

            Assert.Equal(new[] { "MES_DATA", "OVER", "" }, slices.Select(s => s.Name));
            Assert.Equal(93.5, slices[0].Percent, 1);
            Assert.Equal(100, slices[1].Percent);
            Assert.Equal(18.5, slices[2].Percent, 1);
            Assert.Equal("191,562 / 204,800", MonitorLogic.UsageText(slices[0]));
            Assert.Equal("1,517.5 / 8,192", MonitorLogic.UsageText(slices[2]));

            Assert.Equal(2, MonitorLogic.Usage(table, chart, 2).Count);
            Assert.Empty(MonitorLogic.Usage(table, new MonitorUsageChart("없는열", "사용(MB)", "크기(MB)"), 12));
            Assert.Empty(MonitorLogic.Usage(table, null, 12));
            Assert.Empty(MonitorLogic.Usage(null, chart, 12));
        }

        [Fact]
        public void UsageLevel_PercentText_MoreCharts()
        {
            Assert.Equal(0, MonitorLogic.UsageLevel(0));
            Assert.Equal(0, MonitorLogic.UsageLevel(79.9));
            Assert.Equal(1, MonitorLogic.UsageLevel(80));
            Assert.Equal(1, MonitorLogic.UsageLevel(89.9));
            Assert.Equal(2, MonitorLogic.UsageLevel(90));
            Assert.Equal(2, MonitorLogic.UsageLevel(100));
            Assert.Equal("94%", MonitorLogic.PercentText(93.5));
            Assert.Equal("0%", MonitorLogic.PercentText(0.4));
            Assert.Equal("100%", MonitorLogic.PercentText(100));
            Assert.Null(MonitorLogic.MoreChartsText(12, 12));
            Assert.Equal("외 3개는 표에서", MonitorLogic.MoreChartsText(15, 12));
            Assert.Equal(12, MonitorLogic.MaxCharts);
        }

        // ---------- 잠금 연쇄 트리 ----------

        [Fact]
        public void LockTree_OnlyOracleForNow()
        {
            Assert.NotNull(DbProviders.Oracle.Monitor.LockTree);
            Assert.Null(DbProviders.Postgres.Monitor.LockTree);
            Assert.Null(DbProviders.SqlServer.Monitor.LockTree);
            Assert.Null(DbProviders.Generic.Monitor.LockTree);
        }

        [Fact]
        public void Oracle_LockTree_Sql_GvSession_ContractColumnsFirst_ReadOnly_AliasesFit11g()
        {
            var tree = DbProviders.Oracle.Monitor.LockTree;
            var sql = tree.Sql;
            Assert.StartsWith("WITH w AS (SELECT INST_ID, SID, BLOCKING_INSTANCE, BLOCKING_SESSION FROM GV$SESSION WHERE BLOCKING_SESSION_STATUS = 'VALID')", sql);
            // 계약 열이 이 순서로 앞에
            var positions = MonitorLockTree.ContractColumns.Select(c => sql.IndexOf(" AS " + c + ",", StringComparison.Ordinal)).ToList();
            Assert.All(positions, p => Assert.True(p > 0));
            Assert.Equal(positions.OrderBy(p => p), positions);
            Assert.Contains("FROM GV$SESSION s", sql);
            Assert.Contains("GV$TRANSACTION t ON t.INST_ID = s.INST_ID AND t.ADDR = s.TADDR", sql);
            // 쉬는 근원의 Idle 대기(SQL*Net message from client)는 대기 초에서 뺀다
            Assert.Contains("CASE WHEN s.STATE = 'WAITING' AND s.WAIT_CLASS <> 'Idle' THEN ROUND(s.WAIT_TIME_MICRO / 1000000) END AS WAIT_SECONDS", sql);
            Assert.Contains("NVL(s.SQL_ID, s.PREV_SQL_ID)", sql);
            Assert.DoesNotContain(" V$SESSION", sql);
            // 바인드·DML·문장 끝 없음
            var bare = Regex.Replace(sql, "'[^']*'", "''");
            Assert.DoesNotMatch(@":[A-Za-z_]", bare);
            foreach (var word in new[] { "INSERT ", "UPDATE ", "DELETE ", "DROP ", "ALTER ", "TRUNCATE " })
                Assert.False(Regex.IsMatch(bare, @"\b" + word, RegexOptions.IgnoreCase), word);
            Assert.DoesNotContain(";", sql);
            foreach (Match alias in Regex.Matches(sql, "AS \"([^\"]+)\""))
                Assert.True(System.Text.Encoding.UTF8.GetByteCount(alias.Groups[1].Value) <= 30, "별칭 " + alias.Groups[1].Value);
            // 종료 조치에 쓰는 표시 열이 있다
            foreach (var column in tree.Actions.SelectMany(a => a.Columns))
                Assert.Contains("AS \"" + column + "\"", sql);
            Assert.False(string.IsNullOrWhiteSpace(tree.Description));
            Assert.Equal(OracleMonitor.Privilege, tree.Privilege);
        }

        [Fact]
        public void Oracle_LockTree_Details_SameTitlesAsLocksSection_KeyIsInstanceAndSid()
        {
            var tree = DbProviders.Oracle.Monitor.LockTree;
            var locks = DbProviders.Oracle.Monitor.Sections.First(s => s.Id == MonitorSections.Locks);
            // 화면의 상세 버튼은 섹션 상세의 제목으로 만든다 — 트리 상세도 같은 제목·순서·글 여부여야 한다
            Assert.Equal(locks.Details.Select(d => d.Title), tree.Details.Select(d => d.Title));
            Assert.Equal(locks.Details.Select(d => d.AsText), tree.Details.Select(d => d.AsText));
            foreach (var detail in tree.Details)
            {
                Assert.Contains("TO_NUMBER(SUBSTR(:name, 1, INSTR(:name, ',') - 1))", detail.Sql);
                Assert.Contains("TO_NUMBER(SUBSTR(:name, INSTR(:name, ',') + 1))", detail.Sql);
                Assert.Contains("GV$", detail.Sql);
                Assert.Equal("1,412", detail.Query("1,412").Parameters.Single().Value);
            }
            var plan = tree.Details.First(d => d.Id == "plan").Sql;
            Assert.Contains("s.INST_ID = USERENV('INSTANCE')", plan);
            Assert.Contains("s.INST_ID <> USERENV('INSTANCE')", plan);
        }

        [Fact]
        public void Oracle_LockTree_Kill_WithInstance_OnlyDigits()
        {
            var kill = DbProviders.Oracle.Monitor.LockTree.Actions.Single();
            Assert.Equal(new[] { "인스턴스", "SID", "SERIAL#" }, kill.Columns);
            Assert.Equal("ALTER SYSTEM KILL SESSION '412,53021,@2' IMMEDIATE", kill.Build(new[] { "2", "412", "53021" }));
            Assert.Null(kill.Build(new[] { "2", "412'", "53021" }));
            Assert.Null(kill.Build(new[] { "x", "412", "53021" }));
            Assert.Null(kill.Build(new[] { "2", "412" }));
            Assert.Equal("근원 세션 종료…", MonitorLogic.RootActionTitle(kill));
        }

        // ---------- 전체 DB 상태 ----------

        [Fact]
        public void Overview_OnlyOracleForNow()
        {
            Assert.NotNull(DbProviders.Oracle.Monitor.Overview);
            Assert.Null(DbProviders.Postgres.Monitor.Overview);
            Assert.Null(DbProviders.SqlServer.Monitor.Overview);
            Assert.Null(DbProviders.Generic.Monitor.Overview);
        }

        [Fact]
        public void Oracle_Overview_LongFormat_ClosedMetricList_ReadOnly()
        {
            var overview = DbProviders.Oracle.Monitor.Overview;
            var sql = overview.Sql;
            // 첫 가지에서 열 이름·형식을 정한다(리터럴이 CHAR면 짧은 이름이 공백으로 채워짐)
            Assert.StartsWith("SELECT CAST('active_sessions' AS VARCHAR2(30)) AS METRIC, CAST(NULL AS VARCHAR2(128)) AS ITEM, COUNT(*) AS VALUE, CAST(NULL AS VARCHAR2(64)) AS TEXT", sql);
            // 지표 이름은 닫힌 목록 안에서만, 판정하는 지표는 모두 준다
            var names = Regex.Matches(sql, @"SELECT (?:CAST\()?'([a-z_0-9]+)'").Cast<Match>().Select(m => m.Groups[1].Value).ToList();
            Assert.All(names, n => Assert.Contains(n, MonitorMetrics.All));
            Assert.Equal(names.Count, names.Distinct().Count());
            foreach (var judged in OverviewLogic.DefaultThresholds.Keys)
                Assert.Contains(judged, names);
            Assert.Contains(MonitorMetrics.CpuCount, names);
            // 함정: UNDO·TEMP 제외, FRA는 회수 가능 공간을 뺀다, 잠금은 3초 이상, 아카이브는 오류 상태만
            Assert.Contains("WHERE t.CONTENTS = 'PERMANENT'", sql);
            Assert.Contains("(SPACE_USED - SPACE_RECLAIMABLE) * 100 / SPACE_LIMIT", sql);
            Assert.Contains("SPACE_LIMIT > 0", sql);
            Assert.Contains("WAIT_TIME_MICRO >= 3000000", sql);
            Assert.Contains("STATUS IN ('ERROR', 'DISABLED', 'BAD PARAM', 'FULL')", sql);
            Assert.Contains("STATUS LIKE 'COMPLETED%'", sql);
            var bare = Regex.Replace(sql, "'[^']*'", "''");
            Assert.DoesNotMatch(@":[A-Za-z_]", bare);
            foreach (var word in new[] { "INSERT ", "UPDATE ", "DELETE ", "DROP ", "ALTER ", "TRUNCATE " })
                Assert.False(Regex.IsMatch(bare, @"\b" + word, RegexOptions.IgnoreCase), word);
            Assert.DoesNotContain(";", sql);
            Assert.Equal(OracleMonitor.Privilege, overview.Privilege);
        }

        // ---------- 실측(2026-10-19)에서 찾은 것 ----------

        /// <summary>SQL Server 기본 정보: SERVERPROPERTY(서버 정렬)와 sys.databases 열(Latin1_General_CI_AS_KS_WS)을 UNION ALL 하면 정렬 충돌 — 첫 가지에서 값 열의 정렬을 정한다.</summary>
        [Fact]
        public void SqlServer_Info_PinsValueCollation_InFirstBranch()
        {
            var sql = DbProviders.SqlServer.Monitor.Sections.First(s => s.Id == MonitorSections.Info).Sql;
            var collate = sql.IndexOf("COLLATE DATABASE_DEFAULT AS [값]", StringComparison.Ordinal);
            Assert.True(collate > 0);
            Assert.True(collate < sql.IndexOf("UNION ALL", StringComparison.Ordinal));
        }

        /// <summary>SQL Server 세션 SQL 전문: MARS 세션은 dm_exec_connections에 행이 여럿이라 같은 문장이 행 수만큼 반복됐다 — 한 문장만, 요청 먼저.</summary>
        [Fact]
        public void SqlServer_SessionSql_PicksOneStatement_RequestFirst()
        {
            foreach (var id in new[] { MonitorSections.Sessions, MonitorSections.Locks })
            {
                var sql = DbProviders.SqlServer.Monitor.Sections.First(s => s.Id == id).Details.First(d => d.Id == "sql").Sql;
                Assert.StartsWith("SELECT TOP (1) t.text FROM (", sql);
                Assert.Contains("sys.dm_exec_requests r WHERE r.session_id = CONVERT(int, @name)", sql);
                Assert.Contains("sys.dm_exec_connections c WHERE c.session_id = CONVERT(int, @name)", sql);
                Assert.Contains("WHERE t.text IS NOT NULL", sql);
                Assert.EndsWith("ORDER BY h.src, h.at DESC", sql);
            }
        }

        /// <summary>Oracle 잠금 객체: NULL || '.' || NULL은 '.'이라 객체가 없는 TX 행에 '.'이 보였다. 행 잠금을 기다리는 TX는 ROW_WAIT_OBJ#의 테이블.</summary>
        [Fact]
        public void Oracle_Locks_ObjectBlankWhenUnknown_WaitingTxShowsTable()
        {
            var sql = DbProviders.Oracle.Monitor.Sections.First(s => s.Id == MonitorSections.Locks).Sql;
            Assert.Contains("CASE WHEN o.OBJECT_NAME IS NOT NULL THEN o.OWNER || '.' || o.OBJECT_NAME END AS \"객체\"", sql);
            Assert.Contains("WHEN l.TYPE = 'TM' THEN l.ID1", sql);
            Assert.Contains("WHEN l.REQUEST > 0 AND s.ROW_WAIT_OBJ# > 0 THEN s.ROW_WAIT_OBJ#", sql);
        }

        /// <summary>
        /// PostgreSQL 크기 함수는 볼 권한이 없으면 42501로 문장 전체를 실패시킨다(일반 계정은 pg_global 때문에 테이블스페이스 섹션을 통째로 못 봤다).
        /// 서버의 허용 조건일 때만 한 번 부르고, 아니면 그 행의 크기만 비운다.
        /// </summary>
        [Fact]
        public void Postgres_SizeFunctions_OnlyWhenAllowed_RowStaysVisible()
        {
            var sections = DbProviders.Postgres.Monitor.Sections.ToDictionary(s => s.Id);
            var storage = sections[MonitorSections.Storage].Sql;
            Assert.Single(Regex.Matches(storage, @"pg_tablespace_size\("));
            Assert.Contains("CASE WHEN (has_tablespace_privilege(p.oid, 'CREATE') OR pg_has_role('pg_read_all_stats', 'USAGE'))"
                + " OR p.oid = (SELECT d.dattablespace FROM pg_database d WHERE d.datname = current_database()) THEN pg_tablespace_size(p.oid) END", storage);
            Assert.Contains("ORDER BY t.bytes DESC NULLS LAST", storage);
            var databases = sections[PostgresMonitor.Databases].Sql;
            Assert.Single(Regex.Matches(databases, @"pg_database_size\("));
            Assert.Contains("CASE WHEN (has_database_privilege(p.oid, 'CONNECT') OR pg_has_role('pg_read_all_stats', 'USAGE')) THEN pg_database_size(p.oid) END", databases);
            foreach (var sql in new[] { storage, databases })
                Assert.Contains("COALESCE(pg_size_pretty(", sql);
        }

        /// <summary>섹션 SQL을 QueryTableAsync로 읽는 흐름(SQLite): 열 이름이 별칭 그대로이고 MaxRows만큼만 읽는다.</summary>
        [Fact]
        public async System.Threading.Tasks.Task QueryTable_WithMaxRows_ReadsOnlyThatMany()
        {
            using (var connection = new SqliteConnection("Data Source=:memory:"))
            using (var session = new DbSession(connection))
            {
                await session.OpenAsync();
                var table = await session.QueryTableAsync("SELECT 1 AS \"항목\", 'a' AS \"값\" UNION ALL SELECT 2, 'b' UNION ALL SELECT 3, 'c'", 2);
                Assert.Equal(new[] { "항목", "값" }, table.Columns);
                Assert.Equal(2, table.Rows.Count);
                var columns = MonitorLogic.Columns(table);
                Assert.True(columns[0].IsNumeric);
                Assert.False(columns[1].IsNumeric);
            }
        }
    }
}

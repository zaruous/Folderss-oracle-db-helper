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
            Assert.Contains("pg_database_size(d.oid)", sections[PostgresMonitor.Databases].Sql);
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
            Assert.Equal("'운영'에서 실행 중이라 가져오지 않았습니다. 끝난 뒤 [새로 고침]을 누르세요.", MonitorLogic.BusyText("운영"));
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

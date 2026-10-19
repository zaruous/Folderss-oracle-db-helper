using System.Collections.Generic;

namespace MyPlugin
{
    /// <summary>
    /// SQL Server 성능 모니터 섹션(sys.databases · sys.database_files · sys.dm_*). SQL Server 2012 이상.
    /// - sys.dm_exec_sessions·dm_exec_requests·dm_db_log_space_usage는 VIEW SERVER STATE가 없으면 자기 세션만 보이거나 오류 300·297.
    ///   msdb.dbo.backupset은 msdb 접근 권한이 없으면 916.
    /// - 기본 정보: @@SERVERNAME · SERVERPROPERTY · 현재 DB의 복구 모델·상태·호환성 수준 · 시작 시각(tempdb 생성 시각) · 연결 수.
    /// - 데이터 파일: 현재 DB의 sys.database_files(FILEPROPERTY SpaceUsed) — 테이블스페이스에 해당. 로그 파일도 한 줄. 행을 누르면 그 파일 그룹의 객체 상세.
    /// - 트랜잭션 로그: sys.dm_db_log_space_usage · log_reuse_wait_desc · 마지막 전체/로그 백업.
    /// - 잠금: blocking_session_id로 막히거나 막는 세션(요청 없이 트랜잭션만 연 채 막는 세션도).
    /// - 느린 SQL: sys.dm_exec_query_stats 누적 소요 상위 50(계획 핸들 상세: 전문·계획 XML). 대기 이벤트: sys.dm_os_wait_stats(유휴·내부 대기 제외)와 지금 대기 작업 수.
    /// - 세션: sys.dm_exec_sessions의 사용자 세션. 잠금·세션 행을 누르면 SQL 전문·실행 계획(XML)·세션 정보, 오른쪽 메뉴 [세션 종료…](KILL).
    /// </summary>
    internal sealed class SqlServerMonitor : IMonitorProvider
    {
        public const string Privilege = "VIEW SERVER STATE (백업 정보는 msdb 읽기)";

        private const string CurrentDb = "FROM sys.databases WHERE database_id = DB_ID()";
        private const string Mb = "* 8 / 1024.0";

        /// <summary>
        /// 세션 상세: 실행 중이면 그 요청의 문장(sys.dm_exec_requests.sql_handle), 쉬면 마지막 문장(sys.dm_exec_connections.most_recent_sql_handle).
        /// MARS 세션은 dm_exec_connections에 물리 연결 1행 + 논리 연결 행들이 있어(물리 행의 핸들은 0) 한 문장만 고른다 — 요청이 먼저, 그다음 최근에 읽은 연결.
        /// 실행 계획은 실행 중인 요청의 plan_handle에서(sys.dm_exec_text_query_plan, XML 글). 쉬는 세션은 비어 있다.
        /// </summary>
        private static readonly IReadOnlyList<MonitorDetail> SessionDetails = new[]
        {
            new MonitorDetail("sql", "SQL 전문", "sys.dm_exec_sql_text — 실행 중이면 그 요청의 문장, 쉬면 이 세션의 마지막 문장.",
                "SELECT TOP (1) t.text FROM ("
                + " SELECT r.sql_handle AS h, 0 AS src, r.start_time AS at FROM sys.dm_exec_requests r WHERE r.session_id = CONVERT(int, @name)"
                + " UNION ALL SELECT c.most_recent_sql_handle, 1, c.last_read FROM sys.dm_exec_connections c WHERE c.session_id = CONVERT(int, @name)"
                + ") h CROSS APPLY sys.dm_exec_sql_text(h.h) t WHERE t.text IS NOT NULL ORDER BY h.src, h.at DESC", true),
            new MonitorDetail("plan", "실행 계획", "sys.dm_exec_text_query_plan — 실행 중인 요청의 계획(XML). 쉬는 세션은 비어 있다.",
                "SELECT p.query_plan FROM sys.dm_exec_requests r"
                + " CROSS APPLY sys.dm_exec_text_query_plan(r.plan_handle, r.statement_start_offset, r.statement_end_offset) p"
                + " WHERE r.session_id = CONVERT(int, @name)", true),
            new MonitorDetail("info", "세션 정보", "sys.dm_exec_sessions · sys.dm_exec_requests의 이 세션 열(항목·값).",
                "SELECT [항목], [값] FROM ("
                + " SELECT 1 AS ord, N'login_name' AS [항목], CONVERT(nvarchar(256), s.login_name) AS [값] FROM sys.dm_exec_sessions s WHERE s.session_id = CONVERT(int, @name)"
                + " UNION ALL SELECT 2, N'host / program', s.host_name + N' / ' + s.program_name FROM sys.dm_exec_sessions s WHERE s.session_id = CONVERT(int, @name)"
                + " UNION ALL SELECT 3, N'database', DB_NAME(s.database_id) FROM sys.dm_exec_sessions s WHERE s.session_id = CONVERT(int, @name)"
                + " UNION ALL SELECT 4, N'status', s.status FROM sys.dm_exec_sessions s WHERE s.session_id = CONVERT(int, @name)"
                + " UNION ALL SELECT 5, N'login_time', CONVERT(nvarchar(30), s.login_time, 120) FROM sys.dm_exec_sessions s WHERE s.session_id = CONVERT(int, @name)"
                + " UNION ALL SELECT 6, N'last_request', CONVERT(nvarchar(30), s.last_request_start_time, 120) + N' ~ ' + CONVERT(nvarchar(30), s.last_request_end_time, 120) FROM sys.dm_exec_sessions s WHERE s.session_id = CONVERT(int, @name)"
                + " UNION ALL SELECT 7, N'open_transaction_count', CONVERT(nvarchar(10), s.open_transaction_count) FROM sys.dm_exec_sessions s WHERE s.session_id = CONVERT(int, @name)"
                + " UNION ALL SELECT 8, N'cpu_time(ms) / reads / writes', CONVERT(nvarchar(20), s.cpu_time) + N' / ' + CONVERT(nvarchar(20), s.logical_reads) + N' / ' + CONVERT(nvarchar(20), s.writes) FROM sys.dm_exec_sessions s WHERE s.session_id = CONVERT(int, @name)"
                + " UNION ALL SELECT 9, N'isolation_level', CONVERT(nvarchar(10), s.transaction_isolation_level) FROM sys.dm_exec_sessions s WHERE s.session_id = CONVERT(int, @name)"
                + " UNION ALL SELECT 10, N'request: command / wait', r.command + N' / ' + ISNULL(r.wait_type, N'-') + N' ' + ISNULL(r.wait_resource, N'') FROM sys.dm_exec_requests r WHERE r.session_id = CONVERT(int, @name)"
                + " UNION ALL SELECT 11, N'request: blocking_session_id', CONVERT(nvarchar(10), r.blocking_session_id) FROM sys.dm_exec_requests r WHERE r.session_id = CONVERT(int, @name)"
                + " UNION ALL SELECT 12, N'request: percent_complete', CONVERT(nvarchar(10), CONVERT(decimal(5, 1), r.percent_complete)) FROM sys.dm_exec_requests r WHERE r.session_id = CONVERT(int, @name)"
                + ") t ORDER BY ord")
        };

        /// <summary>세션 종료: KILL n. 값이 수일 때만(KILL은 바인드가 안 된다).</summary>
        private static readonly IReadOnlyList<MonitorAction> SessionActions = new[]
        {
            new MonitorAction("kill", "세션 종료…", new[] { "세션" },
                "이 세션의 트랜잭션은 롤백되고 클라이언트는 끊깁니다. 되돌릴 수 없습니다.",
                values => MonitorAction.IsDigits(values[0]) ? "KILL " + values[0] : null)
        };

        private static readonly IReadOnlyList<MonitorSection> All = new[]
        {
            new MonitorSection(MonitorSections.Info, "기본 정보",
                "@@SERVERNAME · SERVERPROPERTY · sys.databases(현재 DB) · sys.dm_exec_sessions 연결 수. 시작 시각은 tempdb 생성 시각.", Privilege,
                "SELECT [항목], [값] FROM ("
                // 값 열의 정렬 규칙을 첫 가지에서 정한다. SERVERPROPERTY는 서버 정렬, sys.databases 열은 Latin1_General_CI_AS_KS_WS라
                // 그대로 UNION ALL 하면 "Cannot resolve collation conflict"(실측: 2022 SQL_Latin1_General_CP1_CI_AS · 2008 Korean_Wansung_CI_AS)
                + " SELECT 1 AS ord, N'서버' AS [항목], CONVERT(nvarchar(256), @@SERVERNAME) COLLATE DATABASE_DEFAULT AS [값]"
                + " UNION ALL SELECT 2, N'버전', CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion')) + N' ' + CONVERT(nvarchar(128), SERVERPROPERTY('ProductLevel'))"
                + " UNION ALL SELECT 3, N'에디션', CONVERT(nvarchar(256), SERVERPROPERTY('Edition'))"
                + " UNION ALL SELECT 4, N'시작 시각', CONVERT(nvarchar(30), (SELECT create_date FROM sys.databases WHERE name = N'tempdb'), 120)"
                + " UNION ALL SELECT 5, N'데이터베이스', DB_NAME()"
                + " UNION ALL SELECT 6, N'복구 모델', (SELECT recovery_model_desc " + CurrentDb + ")"
                + " UNION ALL SELECT 7, N'상태', (SELECT state_desc " + CurrentDb + ")"
                + " UNION ALL SELECT 8, N'호환성 수준', (SELECT CONVERT(nvarchar(10), compatibility_level) " + CurrentDb + ")"
                + " UNION ALL SELECT 9, N'정렬(Collation)', (SELECT collation_name " + CurrentDb + ")"
                + " UNION ALL SELECT 10, N'연결 수(사용자)', CONVERT(nvarchar(10), (SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE is_user_process = 1))"
                + ") t ORDER BY ord"),

            new MonitorSection(MonitorSections.Storage, "데이터 파일",
                "현재 DB의 sys.database_files · FILEPROPERTY(SpaceUsed). 최대 -1은 무제한. 다른 DB를 보려면 그 DB로 접속한다.", Privilege,
                "SELECT f.name AS [파일], f.type_desc AS [종류], ISNULL(fg.name, N'(로그)') AS [파일 그룹],"
                + " CONVERT(decimal(18, 1), f.size " + Mb + ") AS [크기(MB)],"
                + " CONVERT(decimal(18, 1), FILEPROPERTY(f.name, 'SpaceUsed') " + Mb + ") AS [사용(MB)],"
                + " CONVERT(decimal(18, 1), (f.size - FILEPROPERTY(f.name, 'SpaceUsed')) " + Mb + ") AS [남음(MB)],"
                + " CONVERT(decimal(5, 1), FILEPROPERTY(f.name, 'SpaceUsed') * 100.0 / NULLIF(f.size, 0)) AS [사용률(%)],"
                + " CASE WHEN f.max_size = -1 THEN N'무제한' WHEN f.max_size = 268435456 THEN N'2 TB' ELSE CONVERT(nvarchar(20), CONVERT(bigint, f.max_size) * 8 / 1024) END AS [최대(MB)],"
                + " CASE WHEN f.is_percent_growth = 1 THEN CONVERT(nvarchar(10), f.growth) + N'%' ELSE CONVERT(nvarchar(20), CONVERT(bigint, f.growth) * 8 / 1024) + N' MB' END AS [증가],"
                + " f.physical_name AS [경로]"
                + " FROM sys.database_files f LEFT JOIN sys.filegroups fg ON fg.data_space_id = f.data_space_id"
                + " ORDER BY f.type, f.name",
                new MonitorUsageChart("파일", "사용(MB)", "크기(MB)"),
                "파일", new[]
                {
                    new MonitorDetail("objects", "객체", "sys.allocation_units — 이 파일이 속한 파일 그룹에 저장된 테이블·인덱스(파일 단위로는 나뉘지 않는다), 큰 것부터 200개. 로그 파일은 비어 있다.",
                        "SELECT TOP (200) s.name + '.' + o.name AS [객체], o.type_desc AS [종류], ISNULL(i.name, N'(힙)') AS [인덱스],"
                        + " CONVERT(decimal(18, 1), SUM(au.total_pages) * 8 / 1024.0) AS [크기(MB)],"
                        + " CONVERT(decimal(18, 1), SUM(au.used_pages) * 8 / 1024.0) AS [사용(MB)]"
                        + " FROM sys.database_files f"
                        + " JOIN sys.allocation_units au ON au.data_space_id = f.data_space_id"
                        + " JOIN sys.partitions p ON (au.type IN (1, 3) AND p.hobt_id = au.container_id) OR (au.type = 2 AND p.partition_id = au.container_id)"
                        + " JOIN sys.objects o ON o.object_id = p.object_id"
                        + " JOIN sys.schemas s ON s.schema_id = o.schema_id"
                        + " LEFT JOIN sys.indexes i ON i.object_id = p.object_id AND i.index_id = p.index_id"
                        + " WHERE f.name = @name AND o.is_ms_shipped = 0"
                        + " GROUP BY s.name, o.name, o.type_desc, i.name"
                        + " ORDER BY SUM(au.total_pages) DESC")
                }),

            new MonitorSection(MonitorSections.Log, "트랜잭션 로그",
                "현재 DB의 sys.dm_db_log_space_usage · log_reuse_wait_desc · msdb.dbo.backupset의 마지막 전체·로그 백업.", Privilege,
                "SELECT [항목], [값] FROM ("
                + " SELECT 1 AS ord, N'로그 크기(MB)' AS [항목], CONVERT(nvarchar(30), CONVERT(decimal(18, 1), total_log_size_in_bytes / 1048576.0)) AS [값] FROM sys.dm_db_log_space_usage"
                + " UNION ALL SELECT 2, N'로그 사용(MB)', CONVERT(nvarchar(30), CONVERT(decimal(18, 1), used_log_space_in_bytes / 1048576.0)) FROM sys.dm_db_log_space_usage"
                + " UNION ALL SELECT 3, N'로그 사용률(%)', CONVERT(nvarchar(30), CONVERT(decimal(5, 1), used_log_space_in_percent)) FROM sys.dm_db_log_space_usage"
                + " UNION ALL SELECT 4, N'로그 재사용 대기', log_reuse_wait_desc " + CurrentDb
                + " UNION ALL SELECT 5, N'복구 모델', recovery_model_desc " + CurrentDb
                + " UNION ALL SELECT 6, N'마지막 전체 백업', ISNULL(CONVERT(nvarchar(30), (SELECT MAX(backup_finish_date) FROM msdb.dbo.backupset WHERE database_name = DB_NAME() AND type = 'D'), 120), N'(없음)')"
                + " UNION ALL SELECT 7, N'마지막 로그 백업', ISNULL(CONVERT(nvarchar(30), (SELECT MAX(backup_finish_date) FROM msdb.dbo.backupset WHERE database_name = DB_NAME() AND type = 'L'), 120), N'(없음)')"
                + ") t ORDER BY ord"),

            new MonitorSection(MonitorSections.Locks, "잠금",
                "sys.dm_exec_requests의 blocking_session_id로 막혀 있거나 다른 세션을 막는 세션(트랜잭션만 연 채 쉬는 세션 포함). 비어 있으면 잠금 대기가 없다.", Privilege,
                "SELECT s.session_id AS [세션], r.blocking_session_id AS [막는 세션], s.login_name AS [로그인], DB_NAME(s.database_id) AS [DB],"
                + " s.status AS [상태], r.wait_type AS [대기 종류], r.wait_time / 1000 AS [대기(초)], r.wait_resource AS [대기 자원], r.command AS [명령],"
                + " s.host_name AS [호스트], s.program_name AS [프로그램], LEFT(t.text, 300) AS [SQL]"
                + " FROM sys.dm_exec_sessions s"
                + " LEFT JOIN sys.dm_exec_requests r ON r.session_id = s.session_id"
                + " OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) t"
                + " WHERE r.blocking_session_id <> 0"
                + " OR s.session_id IN (SELECT b.blocking_session_id FROM sys.dm_exec_requests b WHERE b.blocking_session_id <> 0)"
                + " ORDER BY r.blocking_session_id, s.session_id",
                null, "세션", SessionDetails, SessionActions),

            new MonitorSection(MonitorSections.Sessions, "세션",
                "sys.dm_exec_sessions의 사용자 세션(시스템 세션 제외). 실행 중이면 sys.dm_exec_requests의 명령·대기.", Privilege,
                "SELECT s.session_id AS [세션], s.login_name AS [로그인], DB_NAME(s.database_id) AS [DB], s.status AS [상태],"
                + " s.host_name AS [호스트], s.program_name AS [프로그램], r.command AS [명령], r.wait_type AS [대기 종류], r.wait_time / 1000 AS [대기(초)],"
                + " CONVERT(nvarchar(30), s.login_time, 120) AS [접속 시각], s.cpu_time AS [CPU(ms)], s.logical_reads AS [논리 읽기],"
                + " LEFT(t.text, 300) AS [SQL]"
                + " FROM sys.dm_exec_sessions s"
                + " LEFT JOIN sys.dm_exec_requests r ON r.session_id = s.session_id"
                + " OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) t"
                + " WHERE s.is_user_process = 1 ORDER BY s.status, s.session_id",
                null, "세션", SessionDetails, SessionActions),

            new MonitorSection(MonitorSections.TopSql, "느린 SQL",
                "sys.dm_exec_query_stats — 계획 캐시에 남은 문장 가운데 누적 소요 상위 50개(캐시에서 밀려난 문장은 없다). 행을 누르면 전문·실행 계획(XML).", Privilege,
                "SELECT TOP (50) CONVERT(decimal(18, 1), qs.total_elapsed_time / 1000000.0) AS [소요(초)], qs.execution_count AS [실행 수],"
                + " CONVERT(decimal(18, 1), qs.total_elapsed_time / 1000.0 / NULLIF(qs.execution_count, 0)) AS [실행당(ms)],"
                + " CONVERT(decimal(18, 1), qs.total_worker_time / 1000000.0) AS [CPU(초)],"
                + " qs.total_logical_reads AS [논리 읽기], qs.total_physical_reads AS [물리 읽기], qs.total_rows AS [처리 행],"
                + " CONVERT(nvarchar(30), qs.last_execution_time, 120) AS [마지막 실행], DB_NAME(t.dbid) AS [DB],"
                + " LEFT(SUBSTRING(t.text, (qs.statement_start_offset / 2) + 1,"
                + " ((CASE qs.statement_end_offset WHEN -1 THEN DATALENGTH(t.text) ELSE qs.statement_end_offset END - qs.statement_start_offset) / 2) + 1), 200) AS [SQL],"
                + " CONVERT(varchar(140), qs.plan_handle, 1) AS [계획 핸들]"
                + " FROM sys.dm_exec_query_stats qs CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) t"
                + " ORDER BY qs.total_elapsed_time DESC",
                null, "계획 핸들", new[]
                {
                    new MonitorDetail("sql", "SQL 전문", "sys.dm_exec_sql_text(plan_handle) — 그 배치 전체 문장.",
                        "SELECT t.text FROM sys.dm_exec_sql_text(CONVERT(varbinary(64), @name, 1)) t", true),
                    new MonitorDetail("plan", "실행 계획", "sys.dm_exec_text_query_plan(plan_handle, 0, -1) — 배치 전체의 캐시된 계획(XML).",
                        "SELECT p.query_plan FROM sys.dm_exec_text_query_plan(CONVERT(varbinary(64), @name, 1), 0, -1) p", true)
                }),

            new MonitorSection(MonitorSections.Waits, "대기 이벤트",
                "sys.dm_os_wait_stats — 서버 시작(또는 통계 초기화) 뒤 누적 대기 상위 50개. 유휴·내부 대기(SLEEP·BROKER·XE·QDS·HADR 등)는 뺐다. [지금 대기]는 이 순간 그 종류로 기다리는 작업 수.", Privilege,
                "SELECT TOP (50) w.wait_type AS [대기 종류], w.waiting_tasks_count AS [대기 횟수],"
                + " CONVERT(decimal(18, 1), w.wait_time_ms / 1000.0) AS [총 대기(초)], CONVERT(decimal(18, 1), w.signal_wait_time_ms / 1000.0) AS [시그널(초)],"
                + " CONVERT(decimal(18, 2), w.wait_time_ms * 1.0 / NULLIF(w.waiting_tasks_count, 0)) AS [평균(ms)],"
                + " (SELECT COUNT(*) FROM sys.dm_os_waiting_tasks wt WHERE wt.wait_type = w.wait_type) AS [지금 대기]"
                + " FROM sys.dm_os_wait_stats w"
                + " WHERE w.waiting_tasks_count > 0"
                + " AND w.wait_type NOT IN ('CLR_SEMAPHORE', 'LAZYWRITER_SLEEP', 'RESOURCE_QUEUE', 'SLEEP_TASK', 'SLEEP_SYSTEMTASK', 'SQLTRACE_BUFFER_FLUSH', 'WAITFOR',"
                + " 'LOGMGR_QUEUE', 'CHECKPOINT_QUEUE', 'REQUEST_FOR_DEADLOCK_SEARCH', 'XE_TIMER_EVENT', 'FT_IFTS_SCHEDULER_IDLE_WAIT', 'DIRTY_PAGE_POLL',"
                + " 'SP_SERVER_DIAGNOSTICS_SLEEP', 'ONDEMAND_TASK_QUEUE', 'DBMIRROR_EVENTS_QUEUE', 'DBMIRRORING_CMD', 'SERVER_IDLE_CHECK', 'WAIT_XTP_HOST_WAIT',"
                + " 'WAIT_XTP_OFFLINE_CKPT_NEW_LOG', 'WAIT_XTP_CKPT_CLOSE', 'SOS_WORK_DISPATCHER', 'TRACEWRITE', 'CLR_AUTO_EVENT', 'CLR_MANUAL_EVENT',"
                + " 'DISPATCHER_QUEUE_SEMAPHORE', 'KSOURCE_WAKEUP', 'SNI_HTTP_ACCEPT', 'SQLTRACE_INCREMENTAL_FLUSH_SLEEP', 'MSQL_XP', 'PWAIT_ALL_COMPONENTS_INITIALIZED')"
                + " AND w.wait_type NOT LIKE 'BROKER%' AND w.wait_type NOT LIKE 'XE%' AND w.wait_type NOT LIKE 'QDS%' AND w.wait_type NOT LIKE 'HADR%'"
                + " AND w.wait_type NOT LIKE 'PREEMPTIVE_XE%' AND w.wait_type NOT LIKE 'SLEEP%' AND w.wait_type NOT LIKE 'PARALLEL_REDO%'"
                + " ORDER BY w.wait_time_ms DESC")
        };

        public IReadOnlyList<MonitorSection> Sections { get { return All; } }

        /// <summary>잠금 연쇄 트리는 아직 Oracle만(설계 docs/monitor-dba-design.md 3장의 대응표대로 나중에 채운다).</summary>
        public MonitorLockTree LockTree { get { return null; } }
    }
}

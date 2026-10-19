using System.Collections.Generic;

namespace MyPlugin
{
    /// <summary>
    /// SQL Server 성능 모니터 섹션(sys.databases · sys.database_files · sys.dm_*). SQL Server 2012 이상.
    /// - sys.dm_exec_sessions·dm_exec_requests·dm_db_log_space_usage는 VIEW SERVER STATE가 없으면 자기 세션만 보이거나 오류 300·297.
    ///   msdb.dbo.backupset은 msdb 접근 권한이 없으면 916.
    /// - 기본 정보: @@SERVERNAME · SERVERPROPERTY · 현재 DB의 복구 모델·상태·호환성 수준 · 시작 시각(tempdb 생성 시각) · 연결 수.
    /// - 데이터 파일: 현재 DB의 sys.database_files(FILEPROPERTY SpaceUsed) — 테이블스페이스에 해당. 로그 파일도 한 줄.
    /// - 트랜잭션 로그: sys.dm_db_log_space_usage · log_reuse_wait_desc · 마지막 전체/로그 백업.
    /// - 잠금: blocking_session_id로 막히거나 막는 세션(요청 없이 트랜잭션만 연 채 막는 세션도).
    /// - 세션: sys.dm_exec_sessions의 사용자 세션.
    /// </summary>
    internal sealed class SqlServerMonitor : IMonitorProvider
    {
        public const string Privilege = "VIEW SERVER STATE (백업 정보는 msdb 읽기)";

        private const string CurrentDb = "FROM sys.databases WHERE database_id = DB_ID()";
        private const string Mb = "* 8 / 1024.0";

        private static readonly IReadOnlyList<MonitorSection> All = new[]
        {
            new MonitorSection(MonitorSections.Info, "기본 정보",
                "@@SERVERNAME · SERVERPROPERTY · sys.databases(현재 DB) · sys.dm_exec_sessions 연결 수. 시작 시각은 tempdb 생성 시각.", Privilege,
                "SELECT [항목], [값] FROM ("
                + " SELECT 1 AS ord, N'서버' AS [항목], CONVERT(nvarchar(256), @@SERVERNAME) AS [값]"
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
                new MonitorUsageChart("파일", "사용(MB)", "크기(MB)")),

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
                + " ORDER BY r.blocking_session_id, s.session_id"),

            new MonitorSection(MonitorSections.Sessions, "세션",
                "sys.dm_exec_sessions의 사용자 세션(시스템 세션 제외). 실행 중이면 sys.dm_exec_requests의 명령·대기.", Privilege,
                "SELECT s.session_id AS [세션], s.login_name AS [로그인], DB_NAME(s.database_id) AS [DB], s.status AS [상태],"
                + " s.host_name AS [호스트], s.program_name AS [프로그램], r.command AS [명령], r.wait_type AS [대기 종류], r.wait_time / 1000 AS [대기(초)],"
                + " CONVERT(nvarchar(30), s.login_time, 120) AS [접속 시각], s.cpu_time AS [CPU(ms)], s.logical_reads AS [논리 읽기],"
                + " LEFT(t.text, 300) AS [SQL]"
                + " FROM sys.dm_exec_sessions s"
                + " LEFT JOIN sys.dm_exec_requests r ON r.session_id = s.session_id"
                + " OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) t"
                + " WHERE s.is_user_process = 1 ORDER BY s.status, s.session_id")
        };

        public IReadOnlyList<MonitorSection> Sections { get { return All; } }
    }
}

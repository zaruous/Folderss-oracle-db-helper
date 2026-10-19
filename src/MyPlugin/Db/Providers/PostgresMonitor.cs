using System.Collections.Generic;

namespace MyPlugin
{
    /// <summary>
    /// PostgreSQL 성능 모니터 섹션(pg_catalog · pg_stat_*). PostgreSQL 10 이상(pg_current_wal_lsn·backend_type·pg_blocking_pids).
    /// - 권한이 없어도 대부분 오류 없이 돌지만, 다른 사용자 세션의 query는 "&lt;insufficient privilege&gt;"로 보이고(pg_read_all_stats 역할이 있어야 보임)
    ///   pg_tablespace_size·pg_database_size는 권한이 없으면 42501이다.
    /// - 기본 정보: version()·current_database()·서버 주소·시작 시각·복구 여부·DB 크기·연결 수/max_connections.
    /// - 테이블스페이스: pg_tablespace와 크기·위치. 남은 용량은 파일 시스템이 정하므로 여기서는 알 수 없다. 행을 누르면 그 안의 객체(pg_class) 상세.
    /// - 데이터베이스(PostgreSQL에만): pg_database마다 크기·연결 수.
    /// - WAL: 위치·파일·wal_level·archive_mode·pg_stat_archiver. 스탠바이면 재생 위치.
    /// - 잠금: pg_blocking_pids로 막히거나 막는 세션만.
    /// - 세션: pg_stat_activity의 client backend.
    /// </summary>
    internal sealed class PostgresMonitor : IMonitorProvider
    {
        public const string Privilege = "pg_read_all_stats 역할(다른 세션의 SQL·pg_tablespace_size·pg_database_size)";

        /// <summary>PostgreSQL에만 있는 섹션: 데이터베이스별 크기·연결 수.</summary>
        public const string Databases = "databases";

        private const string Elapsed = "EXTRACT(EPOCH FROM (now() - a.query_start))::int";

        private static readonly IReadOnlyList<MonitorSection> All = new[]
        {
            new MonitorSection(MonitorSections.Info, "기본 정보",
                "version() · pg_postmaster_start_time() · pg_database_size(현재 DB) · pg_stat_activity 연결 수 · max_connections.", Privilege,
                "SELECT \"항목\", \"값\" FROM ("
                + " SELECT 1 AS ord, '버전' AS \"항목\", split_part(version(), ' on ', 1) AS \"값\""
                + " UNION ALL SELECT 2, '데이터베이스', current_database()"
                + " UNION ALL SELECT 3, '서버 주소', COALESCE(inet_server_addr()::text, '(소켓)') || ':' || COALESCE(inet_server_port()::text, '')"
                + " UNION ALL SELECT 4, '시작 시각', to_char(pg_postmaster_start_time(), 'YYYY-MM-DD HH24:MI:SS')"
                + " UNION ALL SELECT 5, '복구(스탠바이) 중', CASE WHEN pg_is_in_recovery() THEN '예' ELSE '아니요' END"
                + " UNION ALL SELECT 6, '인코딩', pg_encoding_to_char(encoding) FROM pg_database WHERE datname = current_database()"
                + " UNION ALL SELECT 7, '시간대', current_setting('TimeZone')"
                + " UNION ALL SELECT 8, 'DB 크기', pg_size_pretty(pg_database_size(current_database()))"
                + " UNION ALL SELECT 9, '연결 수 / 최대', (SELECT count(*)::text FROM pg_stat_activity WHERE backend_type = 'client backend') || ' / ' || current_setting('max_connections')"
                + " UNION ALL SELECT 10, 'shared_buffers', current_setting('shared_buffers')"
                + " UNION ALL SELECT 11, 'work_mem', current_setting('work_mem')"
                + ") t ORDER BY ord"),

            new MonitorSection(MonitorSections.Storage, "테이블스페이스",
                "pg_tablespace · pg_tablespace_size(). 남은 용량은 파일 시스템이 정하므로 서버 OS에서 확인한다.", Privilege,
                "SELECT t.spcname AS \"테이블스페이스\", pg_get_userbyid(t.spcowner) AS \"소유자\","
                + " COALESCE(NULLIF(pg_tablespace_location(t.oid), ''), '(데이터 폴더)') AS \"위치\","
                + " round(pg_tablespace_size(t.oid) / 1048576.0) AS \"크기(MB)\", pg_size_pretty(pg_tablespace_size(t.oid)) AS \"크기\""
                + " FROM pg_tablespace t ORDER BY pg_tablespace_size(t.oid) DESC, t.spcname",
                null, "테이블스페이스", new[]
                {
                    new MonitorDetail("relations", "객체", "pg_class — 이 테이블스페이스에 저장된 테이블·인덱스·TOAST(DB 기본 테이블스페이스면 reltablespace = 0인 것), 큰 것부터 200개(현재 DB만).",
                        "SELECT n.nspname || '.' || c.relname AS \"객체\","
                        + " CASE c.relkind WHEN 'r' THEN 'TABLE' WHEN 'p' THEN 'PARTITIONED TABLE' WHEN 'i' THEN 'INDEX' WHEN 'I' THEN 'PARTITIONED INDEX'"
                        + " WHEN 'm' THEN 'MATERIALIZED VIEW' WHEN 't' THEN 'TOAST' WHEN 'S' THEN 'SEQUENCE' ELSE c.relkind::text END AS \"종류\","
                        + " pg_get_userbyid(c.relowner) AS \"소유자\","
                        + " round(pg_relation_size(c.oid) / 1048576.0, 1) AS \"크기(MB)\", pg_size_pretty(pg_relation_size(c.oid)) AS \"크기\""
                        + " FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace"
                        + " WHERE c.relkind IN ('r', 'p', 'i', 'I', 'm', 't', 'S')"
                        + " AND (CASE WHEN c.reltablespace = 0 THEN (SELECT d.dattablespace FROM pg_database d WHERE d.datname = current_database()) ELSE c.reltablespace END)"
                        + " = (SELECT t.oid FROM pg_tablespace t WHERE t.spcname = :name)"
                        + " ORDER BY pg_relation_size(c.oid) DESC LIMIT 200")
                }),

            new MonitorSection(MonitorSections.Log, "WAL",
                "pg_current_wal_lsn() · wal_level · archive_mode · pg_stat_archiver. 스탠바이면 재생 위치를 보인다.", Privilege,
                "SELECT \"항목\", \"값\" FROM ("
                + " SELECT 1 AS ord, 'WAL 위치' AS \"항목\", CASE WHEN pg_is_in_recovery() THEN pg_last_wal_replay_lsn()::text ELSE pg_current_wal_lsn()::text END AS \"값\""
                + " UNION ALL SELECT 2, 'WAL 파일', CASE WHEN pg_is_in_recovery() THEN '(스탠바이)' ELSE pg_walfile_name(pg_current_wal_lsn()) END"
                + " UNION ALL SELECT 3, 'wal_level', current_setting('wal_level')"
                + " UNION ALL SELECT 4, 'max_wal_size', current_setting('max_wal_size')"
                + " UNION ALL SELECT 5, 'archive_mode', current_setting('archive_mode')"
                + " UNION ALL SELECT 6, '아카이브 수', archived_count::text FROM pg_stat_archiver"
                + " UNION ALL SELECT 7, '마지막 아카이브', COALESCE(last_archived_wal || ' (' || to_char(last_archived_time, 'YYYY-MM-DD HH24:MI:SS') || ')', '') FROM pg_stat_archiver"
                + " UNION ALL SELECT 8, '아카이브 실패 수', failed_count::text FROM pg_stat_archiver"
                + " UNION ALL SELECT 9, '마지막 실패', COALESCE(last_failed_wal || ' (' || to_char(last_failed_time, 'YYYY-MM-DD HH24:MI:SS') || ')', '') FROM pg_stat_archiver"
                + ") t ORDER BY ord"),

            new MonitorSection(MonitorSections.Locks, "잠금",
                "pg_stat_activity 가운데 pg_blocking_pids()로 막혀 있거나 다른 세션을 막는 세션. 비어 있으면 잠금 대기가 없다.", Privilege,
                "SELECT a.pid AS \"PID\", a.usename AS \"사용자\", a.datname AS \"DB\", a.state AS \"상태\","
                + " array_to_string(pg_blocking_pids(a.pid), ', ') AS \"막는 PID\","
                + " a.wait_event_type AS \"대기 종류\", a.wait_event AS \"대기 이벤트\","
                + " " + Elapsed + " AS \"지속(초)\", a.application_name AS \"응용\", a.client_addr::text AS \"클라이언트\","
                + " left(a.query, 300) AS \"SQL\""
                + " FROM pg_stat_activity a"
                + " WHERE cardinality(pg_blocking_pids(a.pid)) > 0"
                + " OR a.pid IN (SELECT unnest(pg_blocking_pids(b.pid)) FROM pg_stat_activity b)"
                + " ORDER BY \"막는 PID\", a.pid"),

            new MonitorSection(MonitorSections.Sessions, "세션",
                "pg_stat_activity의 client backend(백그라운드 프로세스 제외).", Privilege,
                "SELECT a.pid AS \"PID\", a.usename AS \"사용자\", a.datname AS \"DB\", a.state AS \"상태\","
                + " a.application_name AS \"응용\", a.client_addr::text AS \"클라이언트\","
                + " a.wait_event_type AS \"대기 종류\", a.wait_event AS \"대기 이벤트\","
                + " to_char(a.backend_start, 'YYYY-MM-DD HH24:MI:SS') AS \"접속 시각\", " + Elapsed + " AS \"실행(초)\","
                + " left(a.query, 300) AS \"SQL\""
                + " FROM pg_stat_activity a WHERE a.backend_type = 'client backend' ORDER BY a.state, a.query_start"),

            new MonitorSection(Databases, "데이터베이스",
                "pg_database마다 pg_database_size()와 연결 수(이 서버의 모든 DB).", Privilege,
                "SELECT d.datname AS \"데이터베이스\", pg_get_userbyid(d.datdba) AS \"소유자\", pg_encoding_to_char(d.encoding) AS \"인코딩\","
                + " round(pg_database_size(d.oid) / 1048576.0) AS \"크기(MB)\", pg_size_pretty(pg_database_size(d.oid)) AS \"크기\","
                + " (SELECT count(*) FROM pg_stat_activity a WHERE a.datid = d.oid) AS \"연결 수\","
                + " CASE WHEN d.datallowconn THEN '예' ELSE '아니요' END AS \"접속 허용\""
                + " FROM pg_database d WHERE NOT d.datistemplate ORDER BY pg_database_size(d.oid) DESC, d.datname")
        };

        public IReadOnlyList<MonitorSection> Sections { get { return All; } }
    }
}

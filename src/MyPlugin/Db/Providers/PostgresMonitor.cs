using System.Collections.Generic;

namespace MyPlugin
{
    /// <summary>
    /// PostgreSQL 성능 모니터 섹션(pg_catalog · pg_stat_*). PostgreSQL 10 이상(pg_current_wal_lsn·backend_type·pg_blocking_pids).
    /// - 권한이 없어도 대부분 오류 없이 돌지만, 다른 사용자 세션의 query는 "&lt;insufficient privilege&gt;"로 보이고(pg_read_all_stats 역할이 있어야 보임)
    ///   pg_tablespace_size·pg_database_size는 권한이 없으면 42501이라 허용 조건(<see cref="SizeAllowed"/>)일 때만 불러 그 행의 크기만 비운다.
    /// - 기본 정보: version()·current_database()·서버 주소·시작 시각·복구 여부·DB 크기·연결 수/max_connections.
    /// - 테이블스페이스: pg_tablespace와 크기·위치. 남은 용량은 파일 시스템이 정하므로 여기서는 알 수 없다. 행을 누르면 그 안의 객체(pg_class) 상세.
    /// - 데이터베이스(PostgreSQL에만): pg_database마다 크기·연결 수.
    /// - WAL: 위치·파일·wal_level·archive_mode·pg_stat_archiver. 스탠바이면 재생 위치.
    /// - 잠금: pg_blocking_pids로 막히거나 막는 세션만.
    /// - 느린 SQL: pg_stat_statements(확장 필요, 13+ 열 이름) 누적 소요 상위 50. 가져오기 전에 버전·확장 설치를 점검해 안 되면 설치 안내(MonitorRequirement). 대기 이벤트: 이 순간 활성 세션의 wait_event 집계(누적 통계는 없다).
    /// - 세션: pg_stat_activity의 client backend. 잠금·세션 행을 누르면 SQL 전문·세션 정보, 오른쪽 메뉴 [세션 종료…](pg_terminate_backend).
    /// </summary>
    internal sealed class PostgresMonitor : IMonitorProvider
    {
        public const string Privilege = "pg_read_all_stats 역할(다른 세션의 SQL·pg_tablespace_size·pg_database_size)";

        /// <summary>PostgreSQL에만 있는 섹션: 데이터베이스별 크기·연결 수.</summary>
        public const string Databases = "databases";

        private const string Elapsed = "EXTRACT(EPOCH FROM (now() - a.query_start))::int";

        /// <summary>
        /// 세션(PID) 상세: pg_stat_activity.query 전문(track_activity_query_size, 기본 1,024자까지만 서버가 보관)과 세션 정보(항목·값).
        /// 다른 세션의 실행 계획은 PostgreSQL이 주지 않는다(EXPLAIN은 그 문장을 다시 계획해야 하므로 두지 않음).
        /// </summary>
        private static readonly IReadOnlyList<MonitorDetail> SessionDetails = new[]
        {
            new MonitorDetail("sql", "SQL 전문", "pg_stat_activity.query — 서버는 track_activity_query_size(기본 1,024자)까지만 보관한다. 다른 사용자의 세션은 pg_read_all_stats가 있어야 보인다.",
                "SELECT a.query FROM pg_stat_activity a WHERE a.pid = CAST(:name AS integer)", true),
            new MonitorDetail("info", "세션 정보", "pg_stat_activity의 이 세션 열 전부(항목·값).",
                "SELECT \"항목\", \"값\" FROM ("
                + " SELECT 1 AS ord, 'usename' AS \"항목\", a.usename::text AS \"값\" FROM pg_stat_activity a WHERE a.pid = CAST(:name AS integer)"
                + " UNION ALL SELECT 2, 'datname', a.datname::text FROM pg_stat_activity a WHERE a.pid = CAST(:name AS integer)"
                + " UNION ALL SELECT 3, 'application_name', a.application_name FROM pg_stat_activity a WHERE a.pid = CAST(:name AS integer)"
                + " UNION ALL SELECT 4, 'client_addr', a.client_addr::text || ':' || a.client_port::text FROM pg_stat_activity a WHERE a.pid = CAST(:name AS integer)"
                + " UNION ALL SELECT 5, 'state', a.state FROM pg_stat_activity a WHERE a.pid = CAST(:name AS integer)"
                + " UNION ALL SELECT 6, 'wait_event', a.wait_event_type || ' / ' || a.wait_event FROM pg_stat_activity a WHERE a.pid = CAST(:name AS integer)"
                + " UNION ALL SELECT 7, 'backend_start', to_char(a.backend_start, 'YYYY-MM-DD HH24:MI:SS') FROM pg_stat_activity a WHERE a.pid = CAST(:name AS integer)"
                + " UNION ALL SELECT 8, 'xact_start', to_char(a.xact_start, 'YYYY-MM-DD HH24:MI:SS') FROM pg_stat_activity a WHERE a.pid = CAST(:name AS integer)"
                + " UNION ALL SELECT 9, 'query_start', to_char(a.query_start, 'YYYY-MM-DD HH24:MI:SS') FROM pg_stat_activity a WHERE a.pid = CAST(:name AS integer)"
                + " UNION ALL SELECT 10, 'state_change', to_char(a.state_change, 'YYYY-MM-DD HH24:MI:SS') FROM pg_stat_activity a WHERE a.pid = CAST(:name AS integer)"
                + " UNION ALL SELECT 11, 'backend_xid', a.backend_xid::text FROM pg_stat_activity a WHERE a.pid = CAST(:name AS integer)"
                + " UNION ALL SELECT 12, 'blocked by', array_to_string(pg_blocking_pids(a.pid), ', ') FROM pg_stat_activity a WHERE a.pid = CAST(:name AS integer)"
                + ") t ORDER BY ord")
        };

        /// <summary>세션 종료: pg_terminate_backend(pid). 값이 수일 때만.</summary>
        private static readonly IReadOnlyList<MonitorAction> SessionActions = new[]
        {
            new MonitorAction("kill", "세션 종료…", new[] { "PID" },
                "이 세션의 트랜잭션은 롤백되고 클라이언트는 끊깁니다. 되돌릴 수 없습니다.",
                values => MonitorAction.IsDigits(values[0]) ? "SELECT pg_terminate_backend(" + values[0] + ")" : null)
        };

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
                "pg_tablespace · pg_tablespace_size(). 남은 용량은 파일 시스템이 정하므로 서버 OS에서 확인한다. 크기를 볼 권한이 없는 테이블스페이스(CREATE 권한·pg_read_all_stats가 없고 이 DB의 기본도 아님)는 크기를 비운다.", Privilege,
                "SELECT t.spcname AS \"테이블스페이스\", pg_get_userbyid(t.spcowner) AS \"소유자\","
                + " COALESCE(NULLIF(pg_tablespace_location(t.oid), ''), '(데이터 폴더)') AS \"위치\","
                + " round(t.bytes / 1048576.0) AS \"크기(MB)\", COALESCE(pg_size_pretty(t.bytes), '(권한 필요)') AS \"크기\""
                // pg_tablespace_size는 볼 권한이 없으면 42501로 문장 전체를 실패시킨다(일반 계정은 pg_global에서) — 허용 조건일 때만 잰다
                + " FROM (SELECT p.oid, p.spcname, p.spcowner, CASE WHEN " + SizeAllowed("has_tablespace_privilege(p.oid, 'CREATE')")
                + " OR p.oid = (SELECT d.dattablespace FROM pg_database d WHERE d.datname = current_database())"
                + " THEN pg_tablespace_size(p.oid) END AS bytes FROM pg_tablespace p) t"
                + " ORDER BY t.bytes DESC NULLS LAST, t.spcname",
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
                + " ORDER BY \"막는 PID\", a.pid",
                null, "PID", SessionDetails, SessionActions),

            new MonitorSection(MonitorSections.Sessions, "세션",
                "pg_stat_activity의 client backend(백그라운드 프로세스 제외).", Privilege,
                "SELECT a.pid AS \"PID\", a.usename AS \"사용자\", a.datname AS \"DB\", a.state AS \"상태\","
                + " a.application_name AS \"응용\", a.client_addr::text AS \"클라이언트\","
                + " a.wait_event_type AS \"대기 종류\", a.wait_event AS \"대기 이벤트\","
                + " to_char(a.backend_start, 'YYYY-MM-DD HH24:MI:SS') AS \"접속 시각\", " + Elapsed + " AS \"실행(초)\","
                + " left(a.query, 300) AS \"SQL\""
                + " FROM pg_stat_activity a WHERE a.backend_type = 'client backend' ORDER BY a.state, a.query_start",
                null, "PID", SessionDetails, SessionActions),

            new MonitorSection(MonitorSections.TopSql, "느린 SQL",
                "pg_stat_statements(확장이 설치돼 있어야 하고 PostgreSQL 13 이상의 열 이름) — 통계를 지운 뒤 누적 소요 상위 50개. 문장은 상수가 $1처럼 정규화돼 있다. 행을 누르면 전문.", Privilege + ", pg_stat_statements 확장",
                "SELECT s.queryid::text AS \"쿼리 ID\", round((s.total_exec_time / 1000)::numeric, 1) AS \"소요(초)\", s.calls AS \"실행 수\","
                + " round(s.mean_exec_time::numeric, 1) AS \"실행당(ms)\", s.rows AS \"처리 행\","
                + " s.shared_blks_hit AS \"버퍼 히트\", s.shared_blks_read AS \"디스크 읽기\","
                + " pg_get_userbyid(s.userid) AS \"사용자\", d.datname AS \"DB\", left(s.query, 200) AS \"SQL\""
                + " FROM pg_stat_statements s JOIN pg_database d ON d.oid = s.dbid"
                + " ORDER BY s.total_exec_time DESC LIMIT 50",
                null, "쿼리 ID", new[]
                {
                    new MonitorDetail("sql", "SQL 전문", "pg_stat_statements.query — 상수가 $1·$2로 정규화된 문장. 실행 계획은 pg_stat_statements가 주지 않는다(EXPLAIN은 실제 값이 필요).",
                        "SELECT s.query FROM pg_stat_statements s WHERE s.queryid = CAST(:name AS bigint) LIMIT 1", true)
                },
                null, new[]
                {
                    // 열 이름이 13에서 바뀌었다(total_time → total_exec_time). 먼저 버전을 보고, 그다음 확장이 이 DB에 만들어졌는지 본다
                    new MonitorRequirement("SELECT CASE WHEN current_setting('server_version_num')::int >= 130000 THEN 1 ELSE 0 END",
                        "PostgreSQL 13 이상에서만 지원합니다. 12 이하는 pg_stat_statements의 열 이름(total_time)이 달라 이 섹션을 쓸 수 없습니다.",
                        "PostgreSQL 13 이상으로 올리면 [느린 SQL]을 쓸 수 있습니다.\n"
                        + "  1. 지금 버전 확인: SELECT version();\n"
                        + "  2. 내려받기: https://www.postgresql.org/download/\n"
                        + "  3. 큰 버전 업그레이드는 pg_upgrade 또는 pg_dump/pg_restore — https://www.postgresql.org/docs/current/upgrading.html"),
                    new MonitorRequirement("SELECT 1 FROM pg_extension WHERE extname = 'pg_stat_statements'",
                        "pg_stat_statements 확장이 이 데이터베이스에 설치돼 있지 않습니다. 관리자가 postgresql.conf의 shared_preload_libraries에 pg_stat_statements를 넣고 서버를 재시작한 뒤,"
                        + " 이 DB에서 CREATE EXTENSION pg_stat_statements; 를 실행해야 합니다.",
                        "pg_stat_statements 설치 절차(이 PC의 PostgreSQL):\n"
                        + "  1. 설정 파일 위치 확인: SHOW config_file;\n"
                        + "  2. postgresql.conf에 추가(이미 있으면 쉼표로 이어 적기): shared_preload_libraries = 'pg_stat_statements'\n"
                        + "  3. 서버 재시작 — Windows: 서비스(services.msc)에서 postgresql-x64-<버전> 다시 시작, 또는 pg_ctl restart -D <데이터 폴더>\n"
                        + "  4. 이 DB에서 실행(수퍼유저): CREATE EXTENSION pg_stat_statements;\n"
                        + "  5. 성능 모니터 [느린 SQL]에서 [새로 고침]\n"
                        + "  문서: https://www.postgresql.org/docs/current/pgstatstatements.html")
                }),

            new MonitorSection(MonitorSections.Waits, "대기 이벤트",
                "pg_stat_activity — 이 순간 활성 client backend가 기다리는 이벤트별 세션 수(누적 통계는 PostgreSQL에 없다). 대기가 없는 세션은 CPU에서 실행 중.", Privilege,
                "SELECT COALESCE(a.wait_event_type, '(실행 중)') AS \"분류\", COALESCE(a.wait_event, '-') AS \"이벤트\", count(*) AS \"세션 수\","
                + " string_agg(a.pid::text, ', ' ORDER BY a.pid) AS \"PID\""
                + " FROM pg_stat_activity a WHERE a.backend_type = 'client backend' AND a.state = 'active'"
                + " GROUP BY a.wait_event_type, a.wait_event ORDER BY count(*) DESC, 1, 2"),

            new MonitorSection(Databases, "데이터베이스",
                "pg_database마다 pg_database_size()와 연결 수(이 서버의 모든 DB). CONNECT 권한이 없는 DB는 크기를 비운다(pg_read_all_stats면 모두 보임).", Privilege,
                "SELECT d.datname AS \"데이터베이스\", pg_get_userbyid(d.datdba) AS \"소유자\", pg_encoding_to_char(d.encoding) AS \"인코딩\","
                + " round(d.bytes / 1048576.0) AS \"크기(MB)\", COALESCE(pg_size_pretty(d.bytes), '(권한 필요)') AS \"크기\","
                + " (SELECT count(*) FROM pg_stat_activity a WHERE a.datid = d.oid) AS \"연결 수\","
                + " CASE WHEN d.datallowconn THEN '예' ELSE '아니요' END AS \"접속 허용\""
                // pg_database_size도 CONNECT 권한이 없는 DB가 하나라도 있으면 42501로 문장 전체가 실패한다
                + " FROM (SELECT p.oid, p.datname, p.datdba, p.encoding, p.datallowconn,"
                + " CASE WHEN " + SizeAllowed("has_database_privilege(p.oid, 'CONNECT')") + " THEN pg_database_size(p.oid) END AS bytes"
                + " FROM pg_database p WHERE NOT p.datistemplate) d"
                + " ORDER BY d.bytes DESC NULLS LAST, d.datname")
        };

        public IReadOnlyList<MonitorSection> Sections { get { return All; } }

        /// <summary>잠금 연쇄 트리는 아직 Oracle만(설계 docs/monitor-dba-design.md 3장의 대응표대로 나중에 채운다).</summary>
        public MonitorLockTree LockTree { get { return null; } }

        /// <summary>전체 DB 화면의 지표는 아직 Oracle만(타일은 "지원 예정").</summary>
        public MonitorOverview Overview { get { return null; } }

        /// <summary>
        /// 크기 함수(pg_tablespace_size·pg_database_size)를 불러도 되는 조건: 객체 권한(CREATE·CONNECT) 또는 pg_read_all_stats의 권한.
        /// 서버가 함수 안에서 같은 조건을 검사해 실패하면 42501로 문장 전체가 실패하므로, 조건이 맞을 때만 CASE로 부른다(수퍼유저는 모두 참).
        /// </summary>
        private static string SizeAllowed(string objectPrivilege)
        {
            return "(" + objectPrivilege + " OR pg_has_role('pg_read_all_stats', 'USAGE'))";
        }
    }
}

using System.Collections.Generic;

namespace MyPlugin
{
    /// <summary>
    /// Oracle 성능 모니터 섹션(V$·DBA_* 뷰 — SELECT_CATALOG_ROLE이 필요하다. 없으면 ORA-00942).
    /// - 11gR2에서도 돌게 LISTAGG·NULLS LAST까지만 쓴다. 따옴표 별칭은 30바이트 이내(한글 10자).
    /// - 항목·값 꼴의 UNION ALL은 첫 가지 리터럴을 VARCHAR2로 CAST한다(모두 CHAR 리터럴이면 결과가 CHAR로 패딩된다).
    /// - 기본 정보: V$DATABASE·V$INSTANCE·NLS_DATABASE_PARAMETERS·V$LOG_HISTORY(24시간 로그 스위치)·V$SESSION(사용자 세션 수).
    /// - 테이블스페이스: 영구는 DBA_DATA_FILES − DBA_FREE_SPACE, UNDO는 DBA_UNDO_EXTENTS의 ACTIVE·UNEXPIRED만 사용(만료된 익스텐트는 재사용 가능),
    ///   임시는 V$SORT_SEGMENT.USED_BLOCKS × 블록 크기(V$TEMP_SPACE_HEADER는 고수위라 과장). 최대는 자동 확장이면 MAXBYTES.
    ///   표 위 대시보드에 테이블스페이스마다 사용률 도넛(사용(MB) / 크기(MB)). 행을 누르면 아래에 데이터 파일·세그먼트(DBA_SEGMENTS) 상세.
    /// - 리두 로그: V$LOG 그룹마다 멤버 파일(V$LOGFILE).
    /// - 잠금: V$LOCK의 TM·TX 가운데 막고 있거나(BLOCK) 기다리는(REQUEST) 것만. 객체는 ALL_OBJECTS로 푼다(권한 밖이면 빈 칸).
    /// - 느린 SQL: V$SQLAREA 누적 소요 상위 50(SQL_ID 상세: 전문·DISPLAY_CURSOR·자식 커서). 대기 이벤트: V$SYSTEM_EVENT(Idle 제외)와 지금 대기 세션 수.
    /// - 세션: V$SESSION의 사용자 세션. 잠금·세션 행을 누르면 SQL 전문(V$SQL)·실행 계획(DBMS_XPLAN.DISPLAY_CURSOR)·세션 통계,
    ///   오른쪽 메뉴 [세션 종료…](ALTER SYSTEM KILL SESSION … IMMEDIATE, 확인 체크 뒤).
    /// </summary>
    internal sealed class OracleMonitor : IMonitorProvider
    {
        public const string Privilege = "SELECT_CATALOG_ROLE (또는 해당 V$·DBA_ 뷰 SELECT)";

        private const string LockMode = "0, '없음', 1, 'Null', 2, 'Row-S', 3, 'Row-X', 4, 'Share', 5, 'S/Row-X', 6, 'Exclusive'";

        /// <summary>
        /// 세션(SID) 상세: 지금(없으면 직전) 문장의 전문과 DBMS_XPLAN.DISPLAY_CURSOR 실행 계획. 둘 다 글로 보인다.
        /// V$SQL은 SQL_ID·CHILD_NUMBER로 찾는다(세션이 쉬는 중이면 PREV_SQL_ID).
        /// </summary>
        private static readonly IReadOnlyList<MonitorDetail> SessionDetails = new[]
        {
            new MonitorDetail("sql", "SQL 전문", "V$SQL.SQL_FULLTEXT — 이 세션이 지금 실행 중인(없으면 직전) 문장.",
                "SELECT q.SQL_FULLTEXT FROM V$SESSION s"
                + " JOIN V$SQL q ON q.SQL_ID = NVL(s.SQL_ID, s.PREV_SQL_ID) AND q.CHILD_NUMBER = NVL2(s.SQL_ID, s.SQL_CHILD_NUMBER, s.PREV_CHILD_NUMBER)"
                + " WHERE s.SID = :name AND ROWNUM = 1", true),
            new MonitorDetail("plan", "실행 계획", "DBMS_XPLAN.DISPLAY_CURSOR(SQL_ID, 자식 번호, 'TYPICAL') — 공유 풀에 남아 있는 커서의 실제 계획.",
                "SELECT p.PLAN_TABLE_OUTPUT FROM V$SESSION s,"
                + " TABLE(DBMS_XPLAN.DISPLAY_CURSOR(NVL(s.SQL_ID, s.PREV_SQL_ID), NVL2(s.SQL_ID, s.SQL_CHILD_NUMBER, s.PREV_CHILD_NUMBER), 'TYPICAL')) p"
                + " WHERE s.SID = :name", true),
            new MonitorDetail("stats", "세션 통계", "V$SESSTAT · V$STATNAME — CPU·논리/물리 읽기·리두·커밋 수 등 이 세션의 누적 통계(값이 큰 것부터 50개).",
                "SELECT * FROM (SELECT n.NAME AS \"통계\", st.VALUE AS \"값\", n.CLASS AS \"분류\""
                + " FROM V$SESSTAT st JOIN V$STATNAME n ON n.STATISTIC# = st.STATISTIC#"
                + " WHERE st.SID = :name AND st.VALUE > 0 ORDER BY st.VALUE DESC) WHERE ROWNUM <= 50")
        };

        /// <summary>세션 종료: ALTER SYSTEM KILL SESSION 'sid,serial#' IMMEDIATE. 바인드가 안 되는 문장이라 두 값이 모두 수일 때만.</summary>
        private static readonly IReadOnlyList<MonitorAction> SessionActions = new[]
        {
            new MonitorAction("kill", "세션 종료…", new[] { "SID", "SERIAL#" },
                "이 세션의 커밋하지 않은 변경은 롤백되고 사용자는 ORA-00028로 끊깁니다. 되돌릴 수 없습니다.",
                values => MonitorAction.IsDigits(values[0]) && MonitorAction.IsDigits(values[1])
                    ? "ALTER SYSTEM KILL SESSION '" + values[0] + "," + values[1] + "' IMMEDIATE" : null)
        };

        private static readonly IReadOnlyList<MonitorSection> All = new[]
        {
            new MonitorSection(MonitorSections.Info, "기본 정보",
                "V$DATABASE · V$INSTANCE · NLS_DATABASE_PARAMETERS. 로그 스위치는 최근 24시간(V$LOG_HISTORY).", Privilege,
                "SELECT \"항목\", \"값\" FROM ("
                // 첫 가지를 VARCHAR2로 두어야 UNION ALL 결과가 CHAR(최대 길이)로 패딩되지 않는다('DBID   ')
                + " SELECT 1 AS ORD, CAST('DB 이름' AS VARCHAR2(40)) AS \"항목\", d.NAME AS \"값\" FROM V$DATABASE d"
                + " UNION ALL SELECT 2, 'DBID', TO_CHAR(d.DBID) FROM V$DATABASE d"
                + " UNION ALL SELECT 3, '인스턴스', i.INSTANCE_NAME FROM V$INSTANCE i"
                + " UNION ALL SELECT 4, '호스트', i.HOST_NAME FROM V$INSTANCE i"
                + " UNION ALL SELECT 5, '버전', i.VERSION FROM V$INSTANCE i"
                + " UNION ALL SELECT 6, '상태', i.STATUS || ' / ' || d.OPEN_MODE FROM V$INSTANCE i, V$DATABASE d"
                + " UNION ALL SELECT 7, '시작 시각', TO_CHAR(i.STARTUP_TIME, 'YYYY-MM-DD HH24:MI:SS') FROM V$INSTANCE i"
                + " UNION ALL SELECT 8, '로그 모드', d.LOG_MODE FROM V$DATABASE d"
                + " UNION ALL SELECT 9, '플래시백', d.FLASHBACK_ON FROM V$DATABASE d"
                + " UNION ALL SELECT 10, '문자 집합', p.VALUE FROM NLS_DATABASE_PARAMETERS p WHERE p.PARAMETER = 'NLS_CHARACTERSET'"
                + " UNION ALL SELECT 11, '로그 스위치(24시간)', TO_CHAR(COUNT(*)) FROM V$LOG_HISTORY h WHERE h.FIRST_TIME >= SYSDATE - 1"
                + " UNION ALL SELECT 12, '세션 수(사용자)', TO_CHAR(COUNT(*)) FROM V$SESSION s WHERE s.TYPE = 'USER'"
                + ") ORDER BY ORD"),

            new MonitorSection(MonitorSections.Storage, "테이블스페이스",
                "DBA_TABLESPACES · DBA_DATA_FILES · DBA_FREE_SPACE. UNDO는 만료 전(ACTIVE·UNEXPIRED) 익스텐트, 임시는 지금 쓰는 정렬 세그먼트(V$SORT_SEGMENT)만 사용으로 센다. 최대는 자동 확장 한도.", Privilege,
                "SELECT \"테이블스페이스\", \"종류\", \"상태\", \"크기(MB)\", \"사용(MB)\", \"남음(MB)\", \"사용률(%)\", \"최대(MB)\", \"자동 확장\" FROM ("
                + " SELECT t.TABLESPACE_NAME AS \"테이블스페이스\", t.CONTENTS AS \"종류\", t.STATUS AS \"상태\","
                + " ROUND(f.TOTAL_BYTES / 1048576) AS \"크기(MB)\","
                + " ROUND((f.TOTAL_BYTES - NVL(s.FREE_BYTES, 0)) / 1048576) AS \"사용(MB)\","
                + " ROUND(NVL(s.FREE_BYTES, 0) / 1048576) AS \"남음(MB)\","
                + " ROUND((f.TOTAL_BYTES - NVL(s.FREE_BYTES, 0)) * 100 / NULLIF(f.TOTAL_BYTES, 0), 1) AS \"사용률(%)\","
                + " ROUND(f.MAX_BYTES / 1048576) AS \"최대(MB)\", f.AUTOEXTEND AS \"자동 확장\""
                + " FROM DBA_TABLESPACES t"
                + " JOIN (" + FileSums("DBA_DATA_FILES") + ") f ON f.TABLESPACE_NAME = t.TABLESPACE_NAME"
                + " LEFT JOIN (SELECT TABLESPACE_NAME, SUM(BYTES) AS FREE_BYTES FROM DBA_FREE_SPACE GROUP BY TABLESPACE_NAME) s ON s.TABLESPACE_NAME = t.TABLESPACE_NAME"
                + " WHERE t.CONTENTS <> 'UNDO'"
                // UNDO: 만료 전(ACTIVE·UNEXPIRED) 익스텐트만 사용으로 센다. DBA_FREE_SPACE 기준이면 보존 기간 안의 만료된 익스텐트까지 사용으로 잡혀 늘 가득 차 보인다
                + " UNION ALL"
                + " SELECT t.TABLESPACE_NAME, t.CONTENTS, t.STATUS,"
                + " ROUND(f.TOTAL_BYTES / 1048576),"
                + " ROUND(NVL(u.USED_BYTES, 0) / 1048576),"
                + " ROUND((f.TOTAL_BYTES - NVL(u.USED_BYTES, 0)) / 1048576),"
                + " ROUND(NVL(u.USED_BYTES, 0) * 100 / NULLIF(f.TOTAL_BYTES, 0), 1),"
                + " ROUND(f.MAX_BYTES / 1048576), f.AUTOEXTEND"
                + " FROM DBA_TABLESPACES t"
                + " JOIN (" + FileSums("DBA_DATA_FILES") + ") f ON f.TABLESPACE_NAME = t.TABLESPACE_NAME"
                + " LEFT JOIN (SELECT TABLESPACE_NAME, SUM(BYTES) AS USED_BYTES FROM DBA_UNDO_EXTENTS WHERE STATUS IN ('ACTIVE', 'UNEXPIRED') GROUP BY TABLESPACE_NAME) u ON u.TABLESPACE_NAME = t.TABLESPACE_NAME"
                + " WHERE t.CONTENTS = 'UNDO'"
                // 임시: 지금 쓰고 있는 정렬 세그먼트 블록(V$SORT_SEGMENT.USED_BLOCKS). V$TEMP_SPACE_HEADER는 고수위라 늘 높게 보인다
                + " UNION ALL"
                + " SELECT t.TABLESPACE_NAME, t.CONTENTS, t.STATUS,"
                + " ROUND(f.TOTAL_BYTES / 1048576),"
                + " ROUND(NVL(h.USED_BLOCKS, 0) * t.BLOCK_SIZE / 1048576),"
                + " ROUND((f.TOTAL_BYTES - NVL(h.USED_BLOCKS, 0) * t.BLOCK_SIZE) / 1048576),"
                + " ROUND(NVL(h.USED_BLOCKS, 0) * t.BLOCK_SIZE * 100 / NULLIF(f.TOTAL_BYTES, 0), 1),"
                + " ROUND(f.MAX_BYTES / 1048576), f.AUTOEXTEND"
                + " FROM DBA_TABLESPACES t"
                + " JOIN (" + FileSums("DBA_TEMP_FILES") + ") f ON f.TABLESPACE_NAME = t.TABLESPACE_NAME"
                + " LEFT JOIN (SELECT TABLESPACE_NAME, SUM(USED_BLOCKS) AS USED_BLOCKS FROM V$SORT_SEGMENT GROUP BY TABLESPACE_NAME) h ON h.TABLESPACE_NAME = t.TABLESPACE_NAME"
                + ") ORDER BY \"사용률(%)\" DESC NULLS LAST, \"테이블스페이스\"",
                new MonitorUsageChart("테이블스페이스", "사용(MB)", "크기(MB)"),
                "테이블스페이스", new[]
                {
                    new MonitorDetail("files", "데이터 파일", "DBA_DATA_FILES · DBA_TEMP_FILES — 이 테이블스페이스의 파일과 자동 확장.",
                        "SELECT f.FILE_NAME AS \"파일\", ROUND(f.BYTES / 1048576) AS \"크기(MB)\", f.AUTOEXTENSIBLE AS \"자동 확장\","
                        + " ROUND(f.MAXBYTES / 1048576) AS \"최대(MB)\", ROUND(f.INCREMENT_BY * t.BLOCK_SIZE / 1048576) AS \"증가(MB)\","
                        + " f.STATUS AS \"상태\", f.ONLINE_STATUS AS \"온라인\""
                        + " FROM DBA_DATA_FILES f JOIN DBA_TABLESPACES t ON t.TABLESPACE_NAME = f.TABLESPACE_NAME WHERE f.TABLESPACE_NAME = :name"
                        + " UNION ALL"
                        + " SELECT f.FILE_NAME, ROUND(f.BYTES / 1048576), f.AUTOEXTENSIBLE, ROUND(f.MAXBYTES / 1048576),"
                        + " ROUND(f.INCREMENT_BY * t.BLOCK_SIZE / 1048576), f.STATUS, CAST(NULL AS VARCHAR2(7))"
                        + " FROM DBA_TEMP_FILES f JOIN DBA_TABLESPACES t ON t.TABLESPACE_NAME = f.TABLESPACE_NAME WHERE f.TABLESPACE_NAME = :name"
                        + " ORDER BY 1"),
                    new MonitorDetail("segments", "세그먼트", "DBA_SEGMENTS — 이 테이블스페이스에 저장된 테이블·인덱스·LOB(파티션은 이름으로 합침), 큰 것부터 200개.",
                        "SELECT * FROM ("
                        + " SELECT s.OWNER || '.' || s.SEGMENT_NAME AS \"객체\", s.SEGMENT_TYPE AS \"종류\","
                        + " ROUND(SUM(s.BYTES) / 1048576, 1) AS \"크기(MB)\", SUM(s.EXTENTS) AS \"익스텐트\", COUNT(*) AS \"세그먼트 수\""
                        + " FROM DBA_SEGMENTS s WHERE s.TABLESPACE_NAME = :name"
                        + " GROUP BY s.OWNER, s.SEGMENT_NAME, s.SEGMENT_TYPE ORDER BY SUM(s.BYTES) DESC"
                        + ") WHERE ROWNUM <= 200")
                }),

            new MonitorSection(MonitorSections.Log, "리두 로그",
                "V$LOG 그룹과 멤버 파일(V$LOGFILE). 로그 모드·24시간 로그 스위치 수는 [기본 정보]에.", Privilege,
                "SELECT l.GROUP# AS \"그룹\", l.THREAD# AS \"스레드\", l.SEQUENCE# AS \"시퀀스\", ROUND(l.BYTES / 1048576) AS \"크기(MB)\","
                + " l.MEMBERS AS \"멤버 수\", l.ARCHIVED AS \"아카이브\", l.STATUS AS \"상태\","
                + " TO_CHAR(l.FIRST_TIME, 'YYYY-MM-DD HH24:MI:SS') AS \"첫 기록\","
                + " (SELECT LISTAGG(f.MEMBER, ', ') WITHIN GROUP (ORDER BY f.MEMBER) FROM V$LOGFILE f WHERE f.GROUP# = l.GROUP#) AS \"파일\""
                + " FROM V$LOG l ORDER BY l.THREAD#, l.GROUP#"),

            new MonitorSection(MonitorSections.Locks, "잠금",
                "V$LOCK의 테이블(TM)·행(TX) 잠금 가운데 다른 세션을 막고 있거나 기다리는 것. 비어 있으면 잠금 대기가 없다.", Privilege,
                "SELECT s.SID AS \"SID\", s.SERIAL# AS \"SERIAL#\", s.USERNAME AS \"사용자\", s.STATUS AS \"상태\","
                + " s.BLOCKING_SESSION AS \"막는 세션\", l.TYPE AS \"잠금 종류\","
                + " DECODE(l.LMODE, " + LockMode + ", TO_CHAR(l.LMODE)) AS \"보유 모드\","
                + " DECODE(l.REQUEST, " + LockMode + ", TO_CHAR(l.REQUEST)) AS \"요청 모드\","
                + " o.OWNER || '.' || o.OBJECT_NAME AS \"객체\", l.CTIME AS \"지속(초)\","
                + " s.SQL_ID AS \"SQL_ID\", s.PROGRAM AS \"프로그램\", s.MACHINE AS \"머신\""
                + " FROM V$LOCK l JOIN V$SESSION s ON s.SID = l.SID"
                + " LEFT JOIN ALL_OBJECTS o ON l.TYPE = 'TM' AND o.OBJECT_ID = l.ID1"
                + " WHERE l.TYPE IN ('TM', 'TX') AND (l.BLOCK > 0 OR l.REQUEST > 0)"
                + " ORDER BY l.BLOCK DESC, l.CTIME DESC",
                null, "SID", SessionDetails, SessionActions),

            new MonitorSection(MonitorSections.Sessions, "세션",
                "V$SESSION의 사용자 세션(백그라운드 제외).", Privilege,
                "SELECT s.SID AS \"SID\", s.SERIAL# AS \"SERIAL#\", s.USERNAME AS \"사용자\", s.STATUS AS \"상태\","
                + " s.OSUSER AS \"OS 사용자\", s.MACHINE AS \"머신\", s.PROGRAM AS \"프로그램\","
                + " s.EVENT AS \"대기 이벤트\", s.SECONDS_IN_WAIT AS \"대기(초)\", s.SQL_ID AS \"SQL_ID\","
                + " TO_CHAR(s.LOGON_TIME, 'YYYY-MM-DD HH24:MI:SS') AS \"접속 시각\", s.BLOCKING_SESSION AS \"막는 세션\""
                + " FROM V$SESSION s WHERE s.TYPE = 'USER' ORDER BY s.STATUS, s.SID",
                null, "SID", SessionDetails, SessionActions),

            new MonitorSection(MonitorSections.TopSql, "느린 SQL",
                "V$SQLAREA — 공유 풀에 있는 문장 가운데 누적 소요 시간(커서가 올라온 뒤 합계)이 큰 50개. 열 머리로 CPU·버퍼 겟·실행당 순으로 바꿔 볼 수 있다. 행을 누르면 전문·실행 계획.", Privilege,
                "SELECT * FROM ("
                + " SELECT a.SQL_ID AS \"SQL_ID\", ROUND(a.ELAPSED_TIME / 1000000, 1) AS \"소요(초)\", a.EXECUTIONS AS \"실행 수\","
                + " ROUND(a.ELAPSED_TIME / 1000 / NULLIF(a.EXECUTIONS, 0), 1) AS \"실행당(ms)\", ROUND(a.CPU_TIME / 1000000, 1) AS \"CPU(초)\","
                + " a.BUFFER_GETS AS \"버퍼 겟\", a.DISK_READS AS \"디스크 읽기\", a.ROWS_PROCESSED AS \"처리 행\","
                + " a.PARSING_SCHEMA_NAME AS \"스키마\", a.MODULE AS \"모듈\", TO_CHAR(a.LAST_ACTIVE_TIME, 'YYYY-MM-DD HH24:MI:SS') AS \"마지막 실행\","
                + " SUBSTR(a.SQL_TEXT, 1, 200) AS \"SQL\""
                + " FROM V$SQLAREA a WHERE a.EXECUTIONS > 0 ORDER BY a.ELAPSED_TIME DESC"
                + ") WHERE ROWNUM <= 50",
                null, "SQL_ID", new[]
                {
                    new MonitorDetail("sql", "SQL 전문", "V$SQLAREA.SQL_FULLTEXT.", "SELECT q.SQL_FULLTEXT FROM V$SQLAREA q WHERE q.SQL_ID = :name", true),
                    new MonitorDetail("plan", "실행 계획", "DBMS_XPLAN.DISPLAY_CURSOR(SQL_ID, NULL, 'TYPICAL') — 자식 커서 전부.",
                        "SELECT p.PLAN_TABLE_OUTPUT FROM TABLE(DBMS_XPLAN.DISPLAY_CURSOR(:name, NULL, 'TYPICAL')) p", true),
                    new MonitorDetail("children", "자식 커서", "V$SQL — 같은 SQL_ID의 자식 커서마다 계획 해시·실행 수·소요·바인드 민감 여부.",
                        "SELECT q.CHILD_NUMBER AS \"자식\", q.PLAN_HASH_VALUE AS \"계획 해시\", q.EXECUTIONS AS \"실행 수\","
                        + " ROUND(q.ELAPSED_TIME / 1000000, 1) AS \"소요(초)\", ROUND(q.CPU_TIME / 1000000, 1) AS \"CPU(초)\","
                        + " q.BUFFER_GETS AS \"버퍼 겟\", q.DISK_READS AS \"디스크 읽기\", q.IS_BIND_SENSITIVE AS \"바인드 민감\","
                        + " q.OPTIMIZER_COST AS \"비용\", TO_CHAR(q.LAST_ACTIVE_TIME, 'YYYY-MM-DD HH24:MI:SS') AS \"마지막 실행\""
                        + " FROM V$SQL q WHERE q.SQL_ID = :name ORDER BY q.CHILD_NUMBER")
                }),

            new MonitorSection(MonitorSections.Waits, "대기 이벤트",
                "V$SYSTEM_EVENT — 인스턴스 시작 뒤 누적 대기(Idle 분류 제외) 상위 50개. [지금 세션]은 이 순간 그 이벤트를 기다리는 활성 세션 수(V$SESSION).", Privilege,
                "SELECT * FROM ("
                + " SELECT e.EVENT AS \"이벤트\", e.WAIT_CLASS AS \"분류\", e.TOTAL_WAITS AS \"대기 횟수\","
                + " ROUND(e.TIME_WAITED_MICRO / 1000000, 1) AS \"총 대기(초)\", ROUND(e.AVERAGE_WAIT * 10, 2) AS \"평균(ms)\","
                + " (SELECT COUNT(*) FROM V$SESSION s WHERE s.EVENT = e.EVENT AND s.STATUS = 'ACTIVE' AND s.WAIT_TIME = 0) AS \"지금 세션\""
                + " FROM V$SYSTEM_EVENT e WHERE e.WAIT_CLASS <> 'Idle' ORDER BY e.TIME_WAITED_MICRO DESC"
                + ") WHERE ROWNUM <= 50")
        };

        public IReadOnlyList<MonitorSection> Sections { get { return All; } }

        /// <summary>파일 뷰(DBA_DATA_FILES·DBA_TEMP_FILES)를 테이블스페이스별 크기·최대·자동 확장으로 합친다.</summary>
        private static string FileSums(string view)
        {
            return "SELECT TABLESPACE_NAME, SUM(BYTES) AS TOTAL_BYTES,"
                + " SUM(CASE WHEN AUTOEXTENSIBLE = 'YES' THEN GREATEST(MAXBYTES, BYTES) ELSE BYTES END) AS MAX_BYTES,"
                + " MAX(AUTOEXTENSIBLE) AS AUTOEXTEND"
                + " FROM " + view + " GROUP BY TABLESPACE_NAME";
        }
    }
}

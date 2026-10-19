using System.Collections.Generic;

namespace MyPlugin
{
    /// <summary>
    /// Oracle 성능 모니터 섹션(V$·DBA_* 뷰 — SELECT_CATALOG_ROLE이 필요하다. 없으면 ORA-00942).
    /// - 11gR2에서도 돌게 LISTAGG·NULLS LAST까지만 쓴다. 따옴표 별칭은 30바이트 이내(한글 10자).
    /// - 항목·값 꼴의 UNION ALL은 첫 가지 리터럴을 VARCHAR2로 CAST한다(모두 CHAR 리터럴이면 결과가 CHAR로 패딩된다).
    /// - 기본 정보: V$DATABASE·V$INSTANCE·NLS_DATABASE_PARAMETERS·V$LOG_HISTORY(24시간 로그 스위치)·V$SESSION(사용자 세션 수).
    /// - 테이블스페이스: 영구·UNDO는 DBA_DATA_FILES − DBA_FREE_SPACE, 임시는 DBA_TEMP_FILES와 V$TEMP_SPACE_HEADER(사용은 고수위). 최대는 자동 확장이면 MAXBYTES.
    ///   표 위 대시보드에 테이블스페이스마다 사용률 도넛(사용(MB) / 크기(MB)).
    /// - 리두 로그: V$LOG 그룹마다 멤버 파일(V$LOGFILE).
    /// - 잠금: V$LOCK의 TM·TX 가운데 막고 있거나(BLOCK) 기다리는(REQUEST) 것만. 객체는 ALL_OBJECTS로 푼다(권한 밖이면 빈 칸).
    /// - 세션: V$SESSION의 사용자 세션.
    /// </summary>
    internal sealed class OracleMonitor : IMonitorProvider
    {
        public const string Privilege = "SELECT_CATALOG_ROLE (또는 해당 V$·DBA_ 뷰 SELECT)";

        private const string LockMode = "0, '없음', 1, 'Null', 2, 'Row-S', 3, 'Row-X', 4, 'Share', 5, 'S/Row-X', 6, 'Exclusive'";

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
                "DBA_TABLESPACES · DBA_DATA_FILES · DBA_FREE_SPACE(임시는 DBA_TEMP_FILES · V$TEMP_SPACE_HEADER, 사용은 고수위). 최대는 자동 확장 한도.", Privilege,
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
                + " UNION ALL"
                + " SELECT t.TABLESPACE_NAME, t.CONTENTS, t.STATUS,"
                + " ROUND(f.TOTAL_BYTES / 1048576),"
                + " ROUND(NVL(h.USED_BYTES, 0) / 1048576),"
                + " ROUND((f.TOTAL_BYTES - NVL(h.USED_BYTES, 0)) / 1048576),"
                + " ROUND(NVL(h.USED_BYTES, 0) * 100 / NULLIF(f.TOTAL_BYTES, 0), 1),"
                + " ROUND(f.MAX_BYTES / 1048576), f.AUTOEXTEND"
                + " FROM DBA_TABLESPACES t"
                + " JOIN (" + FileSums("DBA_TEMP_FILES") + ") f ON f.TABLESPACE_NAME = t.TABLESPACE_NAME"
                + " LEFT JOIN (SELECT TABLESPACE_NAME, SUM(BYTES_USED) AS USED_BYTES FROM V$TEMP_SPACE_HEADER GROUP BY TABLESPACE_NAME) h ON h.TABLESPACE_NAME = t.TABLESPACE_NAME"
                + ") ORDER BY \"사용률(%)\" DESC NULLS LAST, \"테이블스페이스\"",
                new MonitorUsageChart("테이블스페이스", "사용(MB)", "크기(MB)")),

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
                + " ORDER BY l.BLOCK DESC, l.CTIME DESC"),

            new MonitorSection(MonitorSections.Sessions, "세션",
                "V$SESSION의 사용자 세션(백그라운드 제외).", Privilege,
                "SELECT s.SID AS \"SID\", s.SERIAL# AS \"SERIAL#\", s.USERNAME AS \"사용자\", s.STATUS AS \"상태\","
                + " s.OSUSER AS \"OS 사용자\", s.MACHINE AS \"머신\", s.PROGRAM AS \"프로그램\","
                + " s.EVENT AS \"대기 이벤트\", s.SECONDS_IN_WAIT AS \"대기(초)\", s.SQL_ID AS \"SQL_ID\","
                + " TO_CHAR(s.LOGON_TIME, 'YYYY-MM-DD HH24:MI:SS') AS \"접속 시각\", s.BLOCKING_SESSION AS \"막는 세션\""
                + " FROM V$SESSION s WHERE s.TYPE = 'USER' ORDER BY s.STATUS, s.SID")
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

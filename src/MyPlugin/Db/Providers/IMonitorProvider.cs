using System;
using System.Collections.Generic;
using System.Linq;

namespace MyPlugin
{
    /// <summary>
    /// 성능 모니터 창(<c>MonitorWindow</c>)이 보일 섹션을 DBMS마다 공급한다. 공급자(<see cref="IDbProvider.Monitor"/>)가 구현.
    /// - 섹션 하나 = 조회 SQL 하나. 결과 열이 그대로 표가 되므로 열 별칭이 곧 화면 머리글이다(한국어 별칭을 따옴표로).
    ///   화면은 열 이름·행 값(글자)만 알고 DBMS 고유 뷰(V$·pg_catalog·sys.dm_*)는 여기 안에만 둔다.
    /// - 바인드 변수 없음, 자료를 바꾸지 않는 SELECT만. 같은 세션의 트랜잭션 안에서 실행되므로 사용자 변경에 영향을 주면 안 된다.
    /// - 권한이 없는 계정이면 섹션마다 따로 실패한다(다른 섹션은 그대로). 어떤 권한이 필요한지는 <see cref="MonitorSection.Privilege"/>에 적어
    ///   <see cref="IDbProvider.IsPermissionDenied"/>로 판정한 오류 메시지에 함께 보인다.
    /// - 공통 섹션 id(<see cref="MonitorSections"/>)는 세 DBMS가 같은 순서로 갖는다. DBMS에만 있는 섹션은 그 뒤에 더한다.
    /// </summary>
    internal interface IMonitorProvider
    {
        /// <summary>섹션 목록(화면 순서). 이 DBMS를 지원하지 않으면 빈 목록.</summary>
        IReadOnlyList<MonitorSection> Sections { get; }

        /// <summary>
        /// 잠금 연쇄 트리(누가 누구를 막는지 — 근원 블로커 아래에 막힌 세션). 있으면 [잠금] 섹션이 트리로 보이고 [표로 보기]로 섹션 표에 돌아간다.
        /// 지원하지 않으면 null(그 DBMS에서는 [잠금] 섹션 표만). 설계: docs/monitor-dba-design.md 3장.
        /// </summary>
        MonitorLockTree LockTree { get; }

        /// <summary>
        /// 전체 DB 한 화면의 타일 지표(가벼운 뷰만, 한 번 왕복). 지원하지 않으면 null(그 DBMS 접속의 타일은 "지원 예정").
        /// 설계: docs/monitor-dba-design.md 5장.
        /// </summary>
        MonitorOverview Overview { get; }
    }

    /// <summary>
    /// 전체 DB 화면의 지표 조회. 결과는 긴 형식 한 장 — 행 하나 = 지표 하나: <see cref="Metric"/>(<see cref="MonitorMetrics"/>의 이름) ·
    /// <see cref="Item"/>(테이블스페이스 이름 등, 없으면 NULL) · <see cref="Value"/>(수, 없으면 NULL) · <see cref="Text"/>(글 값, 선택).
    /// DBMS가 줄 수 없는 지표는 행을 주지 않으면 된다. 목록 밖 이름은 화면이 무시한다. 판정은 <c>OverviewLogic</c>이 한다.
    /// </summary>
    internal sealed class MonitorOverview
    {
        public const string Metric = "METRIC";
        public const string Item = "ITEM";
        public const string Value = "VALUE";
        public const string Text = "TEXT";

        public MonitorOverview(string privilege, string sql)
        {
            if (string.IsNullOrWhiteSpace(sql))
                throw new ArgumentException("sql이 비었습니다.", nameof(sql));
            Privilege = privilege ?? "";
            Sql = sql;
        }

        /// <summary>필요한 권한(권한 오류 타일에 보임).</summary>
        public string Privilege { get; }

        /// <summary>조회 SQL(바인드 없음, SELECT만).</summary>
        public string Sql { get; }
    }

    /// <summary>
    /// 전체 DB 화면·이력이 함께 쓰는 지표 이름(닫힌 목록). 이력 설계(monitor-history-design.md 5절)의 metric.name과 같은 이름을 쓴다.
    /// </summary>
    internal static class MonitorMetrics
    {
        /// <summary>지금 활성인 사용자 세션 수(Idle 대기 제외).</summary>
        public const string ActiveSessions = "active_sessions";
        /// <summary>평균 활성 세션(최근 60초, Oracle V$SYSMETRIC 'Average Active Sessions').</summary>
        public const string AverageActiveSessions = "aas";
        /// <summary>CPU 수(평균 활성 세션 판정 기준).</summary>
        public const string CpuCount = "cpu_count";
        /// <summary>다른 세션에 막혀 3초 이상 기다리는 세션 수(잠금 트리의 기본 최소 대기와 같음).</summary>
        public const string BlockedSessions = "blocked_sessions";
        /// <summary>막혀 기다리는 세션의 가장 긴 대기(초).</summary>
        public const string LongestBlockSeconds = "longest_block_s";
        /// <summary>영구 테이블스페이스 사용률(자동 확장 최대 크기 기준, %). ITEM = 이름. 높은 것부터 몇 개.</summary>
        public const string TablespaceUsedPercent = "ts_used_pct";
        /// <summary>복구 영역(FRA) 사용률(회수 가능 공간을 뺀 값, %). 쓰지 않으면 행 없음.</summary>
        public const string RecoveryAreaUsedPercent = "fra_used_pct";
        /// <summary>오류 상태인 아카이브 대상 수.</summary>
        public const string ArchiveDestErrors = "archive_dest_errors";
        /// <summary>마지막으로 성공한 백업에서 지난 시간(시간). 백업 기록이 없으면 VALUE NULL.</summary>
        public const string LastBackupHours = "last_backup_h";
        /// <summary>최근 24시간에 실패한 예약 작업 수.</summary>
        public const string FailedJobs24h = "failed_jobs_24h";
        /// <summary>인스턴스 가동 시간(시간).</summary>
        public const string UptimeHours = "uptime_h";
        /// <summary>로그 모드(TEXT: ARCHIVELOG · NOARCHIVELOG) — 판정 없이 보이기만.</summary>
        public const string LogMode = "log_mode";
        /// <summary>DB 역할(TEXT: PRIMARY · PHYSICAL STANDBY …) — 판정 없이 보이기만.</summary>
        public const string DbRole = "db_role";

        public static readonly IReadOnlyList<string> All = new[]
        {
            ActiveSessions, AverageActiveSessions, CpuCount, BlockedSessions, LongestBlockSeconds, TablespaceUsedPercent,
            RecoveryAreaUsedPercent, ArchiveDestErrors, LastBackupHours, FailedJobs24h, UptimeHours, LogMode, DbRole
        };
    }

    /// <summary>
    /// 잠금 연쇄 트리 조회. 결과 행 하나 = 간선 하나(세션 → 그 세션을 막는 세션). 트리 만들기는 공급자가 아니라 <c>LockTreeLogic</c>가 한다.
    /// - 계약 열(로직이 읽는 고정 이름, 화면에 그대로 보이지 않음): <see cref="SessionKey"/> · <see cref="BlockerKey"/>(근원이면 NULL) ·
    ///   <see cref="WaitSeconds"/>(지금 대기 중인 초, 아니면 NULL) · <see cref="IdleSeconds"/>(쉬는 세션의 유휴 초, 활성이면 NULL).
    /// - 그 밖의 열은 표시 열(별칭이 머리글). 블로커가 여럿이면 같은 세션이 행 여러 개로 온다 — 표시 열은 첫 행 값을 쓴다.
    /// - 키는 공급자가 정하는 불투명한 글(Oracle "INST_ID,SID" · PostgreSQL pid · SQL Server session_id). 상세는 키를 바인드(name)하고,
    ///   조치(세션 종료)는 표시 열 값으로 문장을 만든다.
    /// </summary>
    internal sealed class MonitorLockTree
    {
        public const string SessionKey = "SESSION_KEY";
        public const string BlockerKey = "BLOCKER_KEY";
        public const string WaitSeconds = "WAIT_SECONDS";
        public const string IdleSeconds = "IDLE_SECONDS";

        /// <summary>계약 열(이 순서로 SQL 앞에 둔다). 화면은 이 열들을 표시 열에서 뺀다.</summary>
        public static readonly IReadOnlyList<string> ContractColumns = new[] { SessionKey, BlockerKey, WaitSeconds, IdleSeconds };

        public MonitorLockTree(string description, string privilege, string sql, IReadOnlyList<MonitorDetail> details = null, IReadOnlyList<MonitorAction> actions = null)
        {
            if (string.IsNullOrWhiteSpace(sql))
                throw new ArgumentException("sql이 비었습니다.", nameof(sql));
            Description = description ?? "";
            Privilege = privilege ?? "";
            Sql = sql;
            Details = details ?? Array.Empty<MonitorDetail>();
            Actions = actions ?? Array.Empty<MonitorAction>();
        }

        /// <summary>무엇을 어디서 읽는지 한 줄(트리 위에 보임).</summary>
        public string Description { get; }

        /// <summary>필요한 권한(권한 오류 메시지에 보임).</summary>
        public string Privilege { get; }

        /// <summary>조회 SQL(바인드 없음).</summary>
        public string Sql { get; }

        /// <summary>트리 행을 눌렀을 때의 상세(키 = SESSION_KEY). 화면의 상세 버튼은 [잠금] 섹션 상세와 같은 제목·순서여야 한다.</summary>
        public IReadOnlyList<MonitorDetail> Details { get; }

        /// <summary>트리 행의 오른쪽 메뉴 조치(세션 종료). 화면이 [근원 …]과 [이 …] 두 가지로 보인다. 열 이름은 표시 열.</summary>
        public IReadOnlyList<MonitorAction> Actions { get; }
    }

    /// <summary>세 DBMS가 함께 갖는 섹션 id(이 순서대로).</summary>
    internal static class MonitorSections
    {
        /// <summary>DB 기본 정보(항목·값 두 열).</summary>
        public const string Info = "info";
        /// <summary>저장 공간: 테이블스페이스(Oracle·PostgreSQL)·데이터 파일(SQL Server)과 남은 용량.</summary>
        public const string Storage = "storage";
        /// <summary>로그: 리두 로그(Oracle)·WAL(PostgreSQL)·트랜잭션 로그(SQL Server).</summary>
        public const string Log = "log";
        /// <summary>잠금: 다른 세션을 막거나 기다리는 세션.</summary>
        public const string Locks = "locks";
        /// <summary>세션 목록.</summary>
        public const string Sessions = "sessions";
        /// <summary>느린 SQL: 누적 소요 시간이 큰 문장(Oracle V$SQLAREA · PostgreSQL pg_stat_statements · SQL Server sys.dm_exec_query_stats).</summary>
        public const string TopSql = "topsql";
        /// <summary>대기 이벤트 요약(어디서 기다리는가: 잠금·I/O·CPU).</summary>
        public const string Waits = "waits";

        public static readonly IReadOnlyList<string> Common = new[] { Info, Storage, Log, Locks, Sessions, TopSql, Waits };
    }

    /// <summary>
    /// 섹션 위 대시보드의 사용률 원형(도넛) 차트: 행마다 하나, 이름 열·사용 열·전체 열 이름(결과 열 별칭)으로 그린다.
    /// 전체가 0이거나 수로 읽히지 않는 행은 건너뛴다. 한도가 없는 저장 공간(PostgreSQL 테이블스페이스)은 차트를 두지 않는다.
    /// </summary>
    internal sealed class MonitorUsageChart
    {
        public MonitorUsageChart(string nameColumn, string usedColumn, string totalColumn)
        {
            if (string.IsNullOrWhiteSpace(nameColumn) || string.IsNullOrWhiteSpace(usedColumn) || string.IsNullOrWhiteSpace(totalColumn))
                throw new ArgumentException("차트 열 이름이 비었습니다.");
            NameColumn = nameColumn;
            UsedColumn = usedColumn;
            TotalColumn = totalColumn;
        }

        public string NameColumn { get; }
        public string UsedColumn { get; }
        public string TotalColumn { get; }
    }

    /// <summary>
    /// 섹션 표의 한 행을 눌렀을 때 아래에 보이는 상세 목록 하나(테이블스페이스의 데이터 파일·세그먼트 등).
    /// 조회는 바인드 하나(이름 "name" — 공급자 방언대로 :name·@name)로 그 행의 키(<see cref="MonitorSection.KeyColumn"/> 값)를 받는다.
    /// 결과 열 별칭이 머리글인 것은 섹션과 같다. 행 수는 SQL 안에서 제한한다(200행 정도).
    /// </summary>
    internal sealed class MonitorDetail
    {
        /// <summary>바인드 이름(접두 없이). 공급자 SQL은 :name 또는 @name으로 쓴다.</summary>
        public const string KeyParameter = "name";

        public MonitorDetail(string id, string title, string description, string sql, bool asText = false)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(sql))
                throw new ArgumentException("상세 목록의 id·title·sql이 비었습니다.");
            Id = id;
            Title = title;
            Description = description ?? "";
            Sql = sql;
            AsText = asText;
        }

        public string Id { get; }
        public string Title { get; }
        public string Description { get; }
        public string Sql { get; }

        /// <summary>
        /// true면 표가 아니라 글(고정폭 읽기 전용 상자)로 보인다: 결과 첫 열을 행 순서대로 줄바꿈으로 잇는다(SQL 전문·실행 계획).
        /// 글은 자르지 않는다(표의 4,000자 제한과 다름).
        /// </summary>
        public bool AsText { get; }

        /// <summary>키 값을 바인드한 조회.</summary>
        public SqlQuery Query(string key)
        {
            var query = new SqlQuery { Sql = Sql };
            query.Parameters.Add(new KeyValuePair<string, object>(KeyParameter, key ?? ""));
            return query;
        }
    }

    /// <summary>
    /// 섹션 표의 한 행에 하는 조치(세션 종료). 행의 열 값들로 문장을 만들고, 창이 확인 대화상자(문장·경고·확인 체크)를 거쳐 실행한다.
    /// - 바인드가 안 되는 문장(ALTER SYSTEM KILL SESSION, KILL)이라 값은 <see cref="Build"/>가 검사해 문장에 직접 넣는다.
    ///   값이 형식에 맞지 않으면(숫자가 아님 등) null — 창은 조치를 끈다. 되돌릴 수 없으므로 읽기 전용 접속에서는 창이 막는다.
    /// </summary>
    internal sealed class MonitorAction
    {
        private readonly Func<string[], string> _build;

        public MonitorAction(string id, string title, IReadOnlyList<string> columns, string warning, Func<string[], string> build)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title) || columns == null || columns.Count == 0 || build == null)
                throw new ArgumentException("조치의 id·title·columns·build가 비었습니다.");
            Id = id;
            Title = title;
            Columns = columns;
            Warning = warning ?? "";
            _build = build;
        }

        public string Id { get; }

        /// <summary>메뉴 글: "세션 종료…".</summary>
        public string Title { get; }

        /// <summary>문장에 넣을 결과 열 이름들(순서대로 <see cref="Build"/>에 넘긴다).</summary>
        public IReadOnlyList<string> Columns { get; }

        /// <summary>확인 대화상자의 경고 문구.</summary>
        public string Warning { get; }

        /// <summary>열 값들(Columns 순서)로 실행 문장. 값이 비었거나 형식에 맞지 않으면 null.</summary>
        public string Build(string[] values)
        {
            if (values == null || values.Length != Columns.Count || values.Any(v => string.IsNullOrWhiteSpace(v)))
                return null;
            return _build(values);
        }

        /// <summary>문장에 직접 넣어도 안전한 값인지: 숫자(음수 없음)만. 세션 번호·시리얼은 모두 수다.</summary>
        public static bool IsDigits(string value)
        {
            return !string.IsNullOrEmpty(value) && value.All(char.IsDigit);
        }
    }

    /// <summary>
    /// 섹션을 가져오기 전에 확인하는 필요 조건(확장 설치·서버 버전 같은, 권한이 아니라 "설치·설정"이 필요한 것).
    /// 점검 SQL은 값 하나를 돌려준다: 행이 없거나 첫 값이 NULL·0·false·'f'면 충족되지 않은 것(<see cref="MonitorLogic.RequirementMet"/>).
    /// 충족되지 않으면 섹션 조회를 하지 않고 <see cref="Message"/>(무엇을 어떻게 설치·설정해야 하는지)를 경고색으로 보인다.
    /// 점검 SQL 자체가 실패하면(권한 등) 그 오류를 보인다.
    /// </summary>
    internal sealed class MonitorRequirement
    {
        public MonitorRequirement(string sql, string message, string installGuide = null)
        {
            if (string.IsNullOrWhiteSpace(sql) || string.IsNullOrWhiteSpace(message))
                throw new ArgumentException("필요 조건의 sql·message가 비었습니다.");
            Sql = sql;
            Message = message;
            InstallGuide = installGuide ?? "";
        }

        public string Sql { get; }
        public string Message { get; }

        /// <summary>
        /// 접속이 로컬호스트(이 PC의 서버)일 때 메시지 탭에 남기는 설치 절차와 링크(여러 줄). 서버가 다른 PC면 사용자가 설치할 수 없으므로 남기지 않는다.
        /// 비면 안내 없음.
        /// </summary>
        public string InstallGuide { get; }
    }

    /// <summary>성능 모니터 섹션 하나: 제목·설명·필요 권한·조회 SQL(·사용률 차트·행 상세 목록·행 조치·필요 조건).</summary>
    internal sealed class MonitorSection
    {
        public MonitorSection(string id, string title, string description, string privilege, string sql, MonitorUsageChart chart = null,
            string keyColumn = null, IReadOnlyList<MonitorDetail> details = null, IReadOnlyList<MonitorAction> actions = null,
            IReadOnlyList<MonitorRequirement> requirements = null)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("id가 비었습니다.", nameof(id));
            if (string.IsNullOrWhiteSpace(title))
                throw new ArgumentException("title이 비었습니다.", nameof(title));
            if (string.IsNullOrWhiteSpace(sql))
                throw new ArgumentException("sql이 비었습니다.", nameof(sql));
            Id = id;
            Title = title;
            Description = description ?? "";
            Privilege = privilege ?? "";
            Sql = sql;
            Chart = chart;
            Details = details ?? Array.Empty<MonitorDetail>();
            Actions = actions ?? Array.Empty<MonitorAction>();
            Requirements = requirements ?? Array.Empty<MonitorRequirement>();
            if (Details.Count > 0 && string.IsNullOrWhiteSpace(keyColumn))
                throw new ArgumentException("상세 목록이 있으면 keyColumn이 있어야 합니다.", nameof(keyColumn));
            KeyColumn = Details.Count > 0 ? keyColumn : null;
        }

        /// <summary>식별자(<see cref="MonitorSections"/> 또는 DBMS 고유). 소문자.</summary>
        public string Id { get; }

        /// <summary>탭 글.</summary>
        public string Title { get; }

        /// <summary>무엇을 어디서 읽는지 한 줄(표 위에 보임).</summary>
        public string Description { get; }

        /// <summary>필요한 권한(권한 오류 메시지에 보임). 예: "SELECT_CATALOG_ROLE".</summary>
        public string Privilege { get; }

        /// <summary>조회 SQL(바인드 없음). 결과 열 별칭이 화면 머리글.</summary>
        public string Sql { get; }

        /// <summary>표 위에 그릴 사용률 원형 차트. 없으면 null(표만).</summary>
        public MonitorUsageChart Chart { get; }

        /// <summary>행의 키가 되는 결과 열 이름(상세 목록의 바인드 값). 상세 목록이 없으면 null.</summary>
        public string KeyColumn { get; }

        /// <summary>행을 눌렀을 때 아래에 보이는 상세 목록들(없으면 빈 목록). 여럿이면 고르는 버튼이 생긴다.</summary>
        public IReadOnlyList<MonitorDetail> Details { get; }

        /// <summary>행의 오른쪽 메뉴에 두는 조치들(세션 종료). 없으면 빈 목록.</summary>
        public IReadOnlyList<MonitorAction> Actions { get; }

        /// <summary>가져오기 전에 순서대로 점검하는 필요 조건(확장 설치·서버 버전). 처음 충족되지 않은 것의 안내를 보인다. 없으면 빈 목록.</summary>
        public IReadOnlyList<MonitorRequirement> Requirements { get; }
    }

    /// <summary>섹션이 없는 공급자(Generic)용.</summary>
    internal sealed class NoMonitor : IMonitorProvider
    {
        public static readonly NoMonitor Instance = new NoMonitor();

        public IReadOnlyList<MonitorSection> Sections { get { return Array.Empty<MonitorSection>(); } }

        public MonitorLockTree LockTree { get { return null; } }

        public MonitorOverview Overview { get { return null; } }
    }
}

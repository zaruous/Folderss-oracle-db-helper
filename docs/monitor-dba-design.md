# 성능 모니터 — DBA 기능 설계안 (잠금 트리 · 활성 세션 이력 · 전체 DB · 일일 점검)

작성 2026-10-19. 대상: TODO 2의 남은 부분(잠금 연쇄 트리), TODO 5-1의 확장(활성 세션 이력), 새 항목 8(전체 DB 한 화면)·9(일일 점검 리포트).
방향: **Oracle을 먼저 구현하되, 인터페이스는 세 DBMS에 중립으로 설계한다.** PostgreSQL·SQL Server는 같은 계약에 SQL만 채우면 같은 화면이 동작해야 한다.
이력 저장소·임계값은 [`monitor-history-design.md`](monitor-history-design.md)와 같은 것을 쓴다.

## 0. 원칙

- **화면·로직은 DBMS 고유 뷰를 모른다.** 지금 `MonitorSection`과 같다. 공급자가 SQL을 주고 화면은 결과만 읽는다.
- **계약 열과 표시 열을 나눈다.** 새 기능은 로직(트리 만들기·구간 집계·판정)이 값을 읽어야 하므로, 로직이 읽는 열은 영문 대문자 고정 이름(`SESSION_KEY`, `WAIT_CLASS`, `METRIC` …)으로 받는다.
  그 밖의 열은 지금처럼 한국어 별칭이 곧 머리글인 **표시 열**이다. DBMS마다 표시 열은 달라도 된다.
- **지원하지 않는 공급자는 null·빈 목록.** 화면은 그 기능의 메뉴를 끄고 "아직 Oracle만 지원"이라고 알린다(지금 `NoMonitor`와 같은 방식).
- **라이선스 팩이 필요한 것은 쓰지 않는다.** `V$ACTIVE_SESSION_HISTORY`·`DBA_HIST_*`(Diagnostics Pack, EE 전용), `V$SQL_MONITOR`(Tuning Pack)는 쓰지 않는다.
  SE·XE 현장에서도 같은 기능이 동작해야 한다(그래서 활성 세션 이력은 `V$SESSION` 표본으로 만든다).
- **RAC는 새 기능부터 처음부터.** 새 SQL은 `GV$` + `INST_ID`로 쓴다(단일 인스턴스에서도 같은 SQL, INST_ID = 1).
  세션 키에 인스턴스를 넣고, 종료 문장은 `ALTER SYSTEM KILL SESSION 'sid,serial#,@inst' IMMEDIATE`다. 기존 섹션(`V$`)의 전환은 별도로 한다.

## 1. 참고 도구에서 가져올 것 (조사 2026-10-19)

| 기능 | 참고 도구의 실제 동작 | 가져올 것 | 가져오지 않을 것 |
|---|---|---|---|
| 잠금 트리 | OEM Performance Hub Blocking Sessions: 열(사용자·상태·잠금·세션(인스턴스·SID·Serial)·SQL ID·대기 이벤트·객체·Blocking Time·Wait Time), **최소 대기 3초 필터**, 종료는 확인 대화상자. Spotlight on Oracle: 블로커가 루트인 트리 + Kill / Kill Immediate. SSMS: Head Blocker 열(쉬는 세션도 head일 수 있음). SolarWinds DPA: **루트 블로커별 유발 대기 합계**, 쉬는 블로커에 "마지막 활동" 링크 | 루트 블로커 트리, 최소 대기 필터, 루트별 막은 세션 수·유발 대기, 쉬는 루트의 마지막 SQL·유휴 시간·트랜잭션 시작 시각, 루트 종료 | 잠금 모드 해석 화면(V$LOCK 행 단위) — 지금 [잠금] 표가 맡는다 |
| 활성 세션 이력 | OEM Top Activity: **1시간 타임라인, wait class별 누적 영역, 5분 음영 박스를 끌어 구간 선택 → 아래 Top SQL(Activity %)·Top Sessions**, 차원 전환(모듈·서비스·클라이언트·객체), 범례를 누르면 그 분류만. Top Activity Lite: 갱신 15초·30초·1분·끔. OraSASH(SE용): 1초 간격 표본, 자기 세션·Idle 제외. MaxGauge: 초 단위 기록을 남겨 과거를 실시간처럼 재생 | 타임라인·누적 영역·음영 박스·Top 패널·차원 전환·범례 필터, 표본 방식(자기 세션·Idle 제외), 분류 색(EM 13.5 색과 비슷하게) | X$ 직접 표본(SYS에 뷰를 만들어야 함), 서버 쪽 저장소(DB 링크·잡) |
| 전체 DB | Redgate Global Dashboard: **DB마다 타일, 가장 심각한 현재/최근 알림 색(빨강·주황·파랑·초록), 최근 15분 미니 추이, 심각도 순 정렬·맨 위 고정**. Spotlight heatmap: 심각한 것이 크고 왼쪽 위. DPA: 문제 인스턴스를 위로 | 타일, 가장 심각한 지표 한 줄(왜 빨간가), 심각도 정렬, 고정, 미니 추이 | 예측(통계적 이상치) — 이력이 쌓인 뒤 |
| 일일 점검 | Oracle AHF Health Check: **PASS / WARNING / FAIL과 색, 직전 수집보다 나빠지면 표시**. 국내 점검표: 경고 로그, 공간, 로그 스위치 간격, 아카이브, 백업, OS 디스크 | 상태 4단(정상·주의·위험 + 건너뜀/오류), 분류별 항목, 엑셀 산출물, (2차) 직전 대비 | Orange·EXEM의 점검표 형식은 공개 자료가 없어 확인하지 못함 |

## 2. 공통 기반

### 2.1 공급자 인터페이스 (안)

```csharp
internal interface IMonitorProvider
{
    IReadOnlyList<MonitorSection> Sections { get; }   // 지금 그대로
    MonitorLockTree LockTree { get; }                 // 3장 — null이면 지원 안 함
    MonitorSampler Sampler { get; }                   // 4장 — null이면 지원 안 함
    MonitorOverview Overview { get; }                 // 5장 — null이면 지원 안 함
    IReadOnlyList<MonitorCheck> Checks { get; }       // 6장 — 빈 목록이면 지원 안 함
}
```

- 처음에는 `OracleMonitor`만 채우고 `PostgresMonitor`·`SqlServerMonitor`·`NoMonitor`는 null·빈 목록을 돌려준다.
- 네 타입 모두 **SQL + 계약 열 + 필요 권한 + 필요 조건(`MonitorRequirement` 재사용)** 을 갖는다. 상세(`MonitorDetail`)·조치(`MonitorAction`)는 지금 것을 그대로 쓴다.
- 판정·집계·트리 만들기는 공급자가 아니라 **중립 로직 클래스**(`LockTreeLogic`·`SampleLogic`·`OverviewLogic`·`CheckRunner`)에 둔다. 리눅스 CI에서 단위 테스트한다.

### 2.2 세션 키

로직은 세션을 **불투명한 글 키**로만 다룬다(비교·표시). 키를 풀어 문장을 만드는 것은 공급자다.

| DBMS | `SESSION_KEY` | 종료 문장 |
|---|---|---|
| Oracle | `INST_ID,SID`(예: `1,412`). 종료에는 표시 열 `인스턴스`·`SID`·`SERIAL#`을 쓴다 | `ALTER SYSTEM KILL SESSION '412,53021,@1' IMMEDIATE` |
| PostgreSQL | `pid` | `SELECT pg_terminate_backend(pid)` |
| SQL Server | `session_id` | `KILL n` |

Oracle 세션 상세(SQL 전문·실행 계획·통계)는 키를 `INST_ID`·`SID`로 나눠 `GV$`를 조회하도록 바꾼다.
`DBMS_XPLAN.DISPLAY_CURSOR`는 접속한 인스턴스의 커서만 보므로, 다른 인스턴스 세션의 계획은 `DBMS_XPLAN.DISPLAY('GV$SQL_PLAN_STATISTICS_ALL', …)`의 필터로 읽는다(구현 때 실측).

### 2.3 대기 분류

공통 분류 = **Oracle WAIT_CLASS 12개(Idle 제외) + `CPU`**. 화면 색은 분류마다 하나다(EM 13.5와 비슷하게: CPU 초록, User I/O 파랑, System I/O 청록, Concurrency 보라, Application 주황빨강, Commit 주황, Network 하늘, Configuration 노랑 …).
테마 7종에서의 대비는 구현 때 맞춘다.

| 공통 분류 | Oracle | PostgreSQL `wait_event_type`(안) | SQL Server Query Store 범주(안) |
|---|---|---|---|
| CPU | `STATE <> 'WAITING'` | `state = 'active' AND wait_event IS NULL` | CPU(SOS_SCHEDULER_YIELD) |
| Application | Application | Lock | Lock(LCK_M_%) |
| Concurrency | Concurrency | LWLock · BufferPin | Latch · Buffer Latch |
| User I/O | User I/O | IO | Buffer IO(PAGEIOLATCH_%) |
| Commit | Commit | — | Tran Log IO |
| Network | Network | Client | Network IO |
| Other | Other 외 | IPC · Timeout · Extension | 그 밖 |

PostgreSQL·SQL Server 대응은 각 공급자를 구현할 때 확정한다. 지금 표는 계약이 세 DBMS를 담을 수 있다는 확인용이다.

### 2.4 지표와 판정

- **지표는 긴 형식 한 장**(`METRIC`·`ITEM`·`VALUE`·`TEXT`)으로 받는다. 이력 설계 5절과 같은 닫힌 목록을 쓰므로 전체 DB 화면의 값을 그대로 이력에 남길 수 있다.
- 상태는 `정상 · 주의 · 위험` 3단에 `건너뜀(권한·버전·미사용)`과 `오류`를 더한다. 타일·점검 항목의 상태는 그중 가장 나쁜 것이다.
- 임계값은 지표마다 주의·위험 두 값이다. 플러그인 설정에 두고 접속별로 덮어쓴다(이력 설계 6절과 같은 표). 색은 `Theme.Success`·`Warning`·`Danger`.

## 3. 잠금 연쇄 트리 (TODO 2의 남은 부분)

### 화면

[잠금] 섹션을 트리로 그린다. [표로 보기]로 지금의 평면 표(복사용)도 남긴다.

```
잠금 ─ 근원 2 · 막힌 세션 5 · 최장 대기 4:12             최소 대기 [3초 ▾]   [표로 보기]
▼ ● 1·412  SCOTT  쉼 4:15 · 트랜잭션 10:02:11부터 · 마지막 SQL 7h2k… · MES_UI.exe @ PC-031   막은 세션 4 · 유발 대기 9:40
   ├ 1·88   APP  enq: TX - row lock contention  4:12  MES.LOT_HIST   UPDATE LOT_HIST …
   │  └ 2·301 APP  enq: TX - row lock contention  2:01  MES.LOT_HIST   (인스턴스 2)
   └ 1·90   APP  enq: TX - row lock contention  1:03  MES.LOT_HIST
▶ ● 1·17   …
```

- **근원(루트) 줄**에는 막은 세션 수(자손 전체), 유발 대기 합(자손 대기 시간의 합, DPA 방식), 최장 대기를 보인다.
  근원이 쉬고 있으면 유휴 시간(`LAST_CALL_ET`), 트랜잭션 시작 시각, 마지막 SQL(`PREV_SQL_ID`)을 보인다.
  근원은 대개 커밋하지 않고 쉬는 세션이라, 지금 SQL만 보이면 빈 칸이 된다(DPA·Microsoft 모두 같은 지적).
- 정렬은 근원의 막은 세션 수, 그다음 유발 대기 순이다. **최소 대기 필터는 기본 3초**(Performance Hub)로, 아주 짧은 대기 잡음을 숨긴다.
- 행을 누르면 아래 상세(지금 세션 상세 재사용)를 보인다. 오른쪽 메뉴는 **[근원 세션 종료…]**(근원 줄만)와 [이 세션 종료…]이고, 확인 대화상자는 지금 것이다.
- 종료 뒤에도 세션이 `KILLED`로 남아 목록에 보이면 "종료 표시됨(정리 중)"으로 구분한다(EM 사례).
- **순환**(교착)은 트리로 펴지 않고 "순환" 묶음으로 보인다. Oracle은 몇 초 안에 ORA-00060으로 깨므로 드물지만, 트리를 만들 때 무한 반복을 막아야 한다.

### 계약

```csharp
internal sealed class MonitorLockTree
{
    // 행 = 간선 하나. 계약 열:
    //   SESSION_KEY  세션
    //   BLOCKER_KEY  이 세션을 막는 세션(근원이면 NULL). 블로커가 여럿이면(PostgreSQL) 같은 세션이 행 여러 개로 온다
    //   WAIT_SECONDS 지금 대기 중인 초(대기 중이 아니면 NULL)
    //   IDLE_SECONDS 쉬는 세션의 유휴 초(활성이면 NULL)
    // 나머지 열은 표시 열(첫 행의 값을 그 세션의 값으로 쓴다).
    public string Sql { get; }
    public string Privilege { get; }
    public IReadOnlyList<MonitorDetail> Details { get; }   // SESSION_KEY를 바인드
    public IReadOnlyList<MonitorAction> Actions { get; }   // 세션 종료
}

internal static class LockTreeLogic
{
    // 근원 = 막는 쪽으로만 나오고 막히지 않은 세션. 자손 수·유발 대기·깊이·순환 판정. 블로커가 여럿이면 각 부모 아래에 표시(두 번째부터 "또 막힘" 표시)
    public static LockForest Build(QueryTable rows, int minWaitSeconds);
}
```

### Oracle SQL (스케치, 11.2 이상)

```sql
WITH w AS (SELECT INST_ID, SID, BLOCKING_INSTANCE, BLOCKING_SESSION
           FROM GV$SESSION WHERE BLOCKING_SESSION_STATUS = 'VALID')
SELECT s.INST_ID || ',' || s.SID AS SESSION_KEY,
       w.BLOCKING_INSTANCE || ',' || w.BLOCKING_SESSION AS BLOCKER_KEY,
       CASE WHEN s.STATE = 'WAITING' THEN ROUND(s.WAIT_TIME_MICRO / 1000000) END AS WAIT_SECONDS,
       CASE WHEN s.STATUS = 'INACTIVE' THEN s.LAST_CALL_ET END AS IDLE_SECONDS,
       s.INST_ID AS "인스턴스", s.SID AS "SID", s.SERIAL# AS "SERIAL#", s.USERNAME AS "사용자", s.STATUS AS "상태",
       CASE WHEN s.STATE = 'WAITING' THEN s.EVENT END AS "대기 이벤트",
       NVL(s.SQL_ID, s.PREV_SQL_ID) AS "SQL_ID", TO_CHAR(t.START_DATE, 'HH24:MI:SS') AS "트랜잭션 시작",
       … 객체(ROW_WAIT_OBJ#), 프로그램, 머신, 모듈
FROM GV$SESSION s
LEFT JOIN w ON w.INST_ID = s.INST_ID AND w.SID = s.SID
LEFT JOIN GV$TRANSACTION t ON t.INST_ID = s.INST_ID AND t.ADDR = s.TADDR
WHERE (s.INST_ID, s.SID) IN (SELECT INST_ID, SID FROM w UNION SELECT BLOCKING_INSTANCE, BLOCKING_SESSION FROM w)
```

- `BLOCKING_SESSION`은 V$LOCK의 TM·TX보다 넓다. 라이브러리 캐시 핀, `cursor: pin S wait on X`, buffer busy처럼 다른 세션을 기다리는 대기도 잡힌다. 근원 판정에는 이쪽이 맞다.
- `FINAL_BLOCKING_SESSION`(11gR2+)은 SQL에서 쓰지 않는다. 트리는 간선으로 만들고, `FINAL_BLOCKING_*`는 단위 테스트의 기대값(실측)을 확인할 때만 쓴다. 순환이면 Oracle이 아무거나 하나를 고르기 때문이다.
- 11.2 XE에서 `GV$SESSION`의 `BLOCKING_*`·`FINAL_BLOCKING_*`·`WAIT_TIME_MICRO`·`ROW_WAIT_OBJ#` 열이 있음을 확인했다(2026-10-19). SELECT_CATALOG_ROLE 계정으로 조회된다.

### 다른 DBMS 대응

- **PostgreSQL**: `SESSION_KEY = pid`, `BLOCKER_KEY = unnest(pg_blocking_pids(pid))`. 블로커가 여럿일 수 있다(hard·soft block). prepared 트랜잭션은 0이다.
  `pg_blocking_pids`는 잠금 관리자를 잠시 독점하므로 자동 새로 고침을 짧게 두지 않는다.
- **SQL Server**: `blocking_session_id`(`sys.dm_exec_requests`)로 간선을 만든다. 근원은 쉬는 세션일 수 있으므로 노드는 `sys.dm_exec_sessions` 기준으로 모은다(지금 [잠금] 섹션과 같음).

### 구현·실측 (Oracle, 2026-10-19)

- **구현 방식**:
  - 트리는 TreeView가 아니라 결과 그리드에 선 글자(● ├ └)로 편다. 복사·상세·오른쪽 메뉴를 그대로 쓰기 위해서다.
  - 앞 열은 세션·막은 세션·유발 대기(초)·대기(초)·유휴(초)다. 막은 세션·유발 대기는 계산값이라 끝 줄도 0이다.
  - 공급자 계약은 `IMonitorProvider.LockTree`(`MonitorLockTree`), 로직은 `LockTreeLogic`이다.
- **쉬는 근원의 대기**:
  - 쉬는 근원도 `STATE = 'WAITING'`(`SQL*Net message from client`, Idle)이다.
  - 그 대기를 세면 "최장 대기"가 부풀므로 `WAIT_SECONDS`는 Idle 분류를 뺀다. 유휴 시간은 `IDLE_SECONDS`(`LAST_CALL_ET`)가 따로 보인다.
- **11.2 XE·19.9**:
  - 2단 사슬(A 쉼 → B → C)에서 트리 근원 = `FINAL_BLOCKING_SESSION` = A로 일치했다.
  - 세 세션의 상세(SQL 전문·실행 계획·세션 통계)가 모두 조회됐다. 쉬는 A의 SQL 전문은 직전 문장이다.
  - `ALTER SYSTEM KILL SESSION 'sid,serial#,@1' IMMEDIATE` 실행 뒤 B가 진행하고 B가 새 근원(→ C)이 됐다. B를 롤백하자 C가 진행했고 트리는 비었다.
- **Folderss 1.8.8 안에서(UI)**:
  - 트리·요약·근원 상세·[표로 보기]↔[트리로 보기]를 확인했다.
  - 오른쪽 메뉴 [근원 세션 종료…] → 확인 대화상자(문장 `… '82,455,@1' IMMEDIATE`) → 실행 → 새로 고침에서 B가 근원이 됐다.
- **KILLED 잔존**: `IMMEDIATE`로 끊은 세션은 두 버전 모두 바로 사라져 따로 표시하지 않았다. 남으면 상태 열에 `KILLED`로 보인다.
- **아직 못 한 것**: RAC(다른 인스턴스의 세션·종료·실행 계획 안내 줄)는 RAC 환경을 구해 실측해야 한다. 순환(교착)은 Oracle이 몇 초 안에 깨서 실측하지 못했고 단위 테스트만 있다.

## 4. 활성 세션 이력 (TODO 5-1 확장) — 라이선스 없는 Top Activity

### 화면

```
활성 세션 ─ 최근 [60분 ▾]  표본 2초 · 1,800개 · 수집 부하 0.4%                  [일시 정지]
 평균 활성 세션                                        ┌──────┐
  6 ┤           ▁▂▅█▆▃                                 │ 선택 │ ← 끌어서 옮기고 양 끝으로 크기(기본 5분)
    ┤▁▁▂▂▃▃▅▆███████▅▃▂▁▁▂▃▃▂▁▁▁▁▂▂▃▅▆▇█▇▆▅▃▂           └──────┘
    └──────────────────────────────────────────────────────
  ■ CPU ■ User I/O ■ Concurrency ■ Application ■ Commit ■ …   (누르면 그 분류만)
선택 14:05~14:10 · 평균 활성 세션 4.2
 [Top SQL] [Top 세션] [대기 이벤트] [모듈] [프로그램] [객체]
 SQL_ID          활동 %   분류 몫            표본    SQL(누르면 전문·실행 계획)
 7h2k…           38%     ████CPU███I/O      912     SELECT …
```

- OEM Top Activity를 따른다. 1시간 타임라인, 분류별 누적 영역, 5분 음영 박스, 아래 Top 패널(활동 %), 차원 전환, 범례 필터다.
- Top SQL 행은 지금 [느린 SQL]의 상세(전문·실행 계획·자식 커서)를 그대로 쓴다. Top 세션 행은 세션 상세를 쓴다.
- **수집 부하를 보인다**(표본 SQL 소요 ÷ 주기). 20%를 넘으면 주기를 스스로 늘리고 그 사실을 상태 글에 남긴다.
- 표본은 **전용 세션**으로 모은다. 섹션 조회(무거운 DBA_FREE_SPACE 등)가 표본 주기를 밀지 않게 하려는 것으로, DB에는 세션이 하나 더 생긴다.
- **언제 모으나**(D2):
  - 기본은 모니터 창이 열린 동안이다.
  - 설정에서 접속별 "상시 표본"을 켜면 DB Helper가 열린 동안 계속 모은다.
  - 표본 엔진은 창과 떨어진 서비스(DB마다 하나)이고, 창은 그 버퍼를 본다. 그래서 상시 표본이 켜져 있으면 창을 연 순간 그 전 1시간이 보인다.
- 차트는 도넛처럼 WPF Path로 직접 그린다. 차트 라이브러리는 쓰지 않는다.

### 계약

```csharp
internal sealed class MonitorSampler
{
    // 행 = 지금 활성인 세션 하나. 계약 열:
    //   SESSION_KEY, WAIT_CLASS(2.3의 공통 분류 이름), EVENT, SQL_KEY(없으면 NULL),
    //   USER_NAME, PROGRAM, MODULE, OBJECT_KEY(없으면 NULL), BLOCKER_KEY(없으면 NULL)
    // 자기 세션·Idle 대기는 SQL 안에서 뺀다. SQL 문장은 받지 않는다(SQL_KEY만) — 문장은 상세를 열 때 조회.
    public string Sql { get; }
    public string Privilege { get; }
    public IReadOnlyList<MonitorDetail> SqlDetails { get; }       // SQL_KEY 바인드
    public IReadOnlyList<MonitorDetail> SessionDetails { get; }   // SESSION_KEY 바인드
}

internal sealed class SampleBuffer   // 시각·행 링 버퍼. 기간(기본 60분)과 메모리 상한을 넘으면 오래된 것부터 버린다. 같은 글은 한 번만 보관
internal static class SampleLogic    // 구간 → 분류별 평균 활성 세션, 구간 + 차원(+분류 필터) → Top 행(키, 표본 수, 활동 %, 분류별 몫)
```

### Oracle SQL (스케치)

```sql
SELECT s.INST_ID || ',' || s.SID AS SESSION_KEY,
       CASE WHEN s.STATE = 'WAITING' THEN s.WAIT_CLASS ELSE 'CPU' END AS WAIT_CLASS,
       CASE WHEN s.STATE = 'WAITING' THEN s.EVENT ELSE 'ON CPU' END AS EVENT,
       s.SQL_ID AS SQL_KEY, s.USERNAME AS USER_NAME, s.PROGRAM AS PROGRAM, s.MODULE AS MODULE,
       CASE WHEN s.ROW_WAIT_OBJ# > 0 THEN TO_CHAR(s.ROW_WAIT_OBJ#) END AS OBJECT_KEY,
       CASE WHEN s.BLOCKING_SESSION_STATUS = 'VALID' THEN s.BLOCKING_INSTANCE || ',' || s.BLOCKING_SESSION END AS BLOCKER_KEY
FROM GV$SESSION s
WHERE s.STATUS = 'ACTIVE' AND (s.STATE <> 'WAITING' OR s.WAIT_CLASS <> 'Idle')
  AND NOT (s.INST_ID = USERENV('INSTANCE') AND s.SID = SYS_CONTEXT('USERENV', 'SID'))
```

- **`STATE`를 반드시 함께 본다.** 대기 중이 아니면 `EVENT`·`WAIT_CLASS`에는 마지막 대기가 남아 있어 CPU 사용을 대기로 잘못 셀 수 있다.
- 11.2 XE에서 문법과 SELECT_CATALOG_ROLE 계정 조회를 확인했다. 부하는 세션 수를 늘린 상태에서 구현 때 실측한다.
- 주기는 기본 2초로 하고 1·2·5·10초 중에서 고른다. ASH·OraSASH는 1초이고, RAC의 GV$ 조회는 인스턴스마다 다녀오므로 2초로 시작한다.

### 다른 DBMS 대응

- **PostgreSQL**: `pg_stat_activity`(`state = 'active'`, `backend_type = 'client backend'`)를 쓴다. 분류는 2.3, `SQL_KEY = query_id`다(14+ `compute_query_id`, 없으면 NULL).
- **SQL Server**: `sys.dm_exec_requests` ⋈ `sys.dm_exec_sessions`를 쓴다. 분류는 Query Store 범주, `SQL_KEY = query_hash`, `BLOCKER_KEY = blocking_session_id`다.

## 5. 전체 DB 한 화면 (새 TODO 8)

### 화면

```
전체 DB ─ 12개 · 위험 1 · 주의 2 · 접속 실패 1    정렬 [심각도 ▾]  자동 [60초 ▾]  [새로 고침]  [고른 DB 점검…]
┌ 운영 ────────────────┐ ┌ 운영 ────────────────┐ ┌ 검증 ───────────────┐
│ ● MES_PRD      위험   │ │ ● RPT_PRD      주의   │ │ ● MES_QA      정상   │
│ 잠금 대기 5 (4:12)    │ │ 마지막 백업 31시간 전  │ │                      │
│ 활성 6.3  ▁▃▆█▆      │ │ 활성 0.8  ▁▁▂▁▁      │ │ 활성 1.2  ▁▂▁▂▁     │
│ 공간 72%(최대 기준)    │ │ 공간 88% MES_DATA     │ │ 공간 41%             │
│ FRA 40% · 갱신 5초 전  │ │ FRA 12% · 갱신 5초 전  │ │ 백업 9시간 전         │
└──────────────────────┘ └──────────────────────┘ └──────────────────────┘
```

- Redgate·Spotlight를 따른다. 타일 하나에 DB 하나이고, 가장 심각한 상태의 색을 쓰고, 심각한 것을 앞에 두며, 고정(핀)할 수 있다.
- 타일 둘째 줄은 **왜 그 색인지**를 보인다(가장 심각한 지표 하나). 미니 추이는 창이 열린 동안의 값이다.
- 타일 머리 띠는 지금 접속 색(초록·노랑·빨강 = 개발·검증·운영)을 그대로 쓴다.
- 타일을 누르면 그 DB의 성능 모니터 창이 열린다. 오른쪽 메뉴는 [잠금 트리]·[점검 실행]·[이 화면에서 빼기]다.
- 접속:
  - **저장된 비밀번호가 있는 접속만 자동으로 붙는다.** 없으면 타일에 "비밀번호 필요 — 눌러서 입력"을 보인다(비밀번호 창이 한꺼번에 여러 개 뜨지 않게).
  - 동시에 4개, DB마다 10초 시간 제한이다.
  - 세션은 창이 열린 동안 DB마다 하나를 유지한다.

### 계약

```csharp
internal sealed class MonitorOverview
{
    // 긴 형식 한 장(한 번 왕복, 가벼운 뷰만): METRIC, ITEM(없으면 NULL), VALUE(수), TEXT(선택)
    public string Sql { get; }
    public string Privilege { get; }
}
internal static class MonitorMetrics { /* 닫힌 목록 — 아래 표. 이력 설계 5절의 지표와 같은 이름 */ }
internal static class OverviewLogic { /* 지표 + 임계값 → 타일 상태·이유 줄 */ }
```

### Oracle 지표 (11.2 XE, SELECT_CATALOG_ROLE 계정 실측)

| METRIC | 뷰 | 기본 임계값(주의/위험) | 실측 |
|---|---|---|---|
| `active_sessions` | GV$SESSION(활성·Idle 아님) | — | ✔ |
| `aas` | V$SYSMETRIC 'Average Active Sessions'(60초 구간) | CPU 수 대비 | ✔ (라이선스 목록에 없음 — Oracle의 명시는 미확인) |
| `blocked_sessions` · `longest_block_s` | GV$SESSION `BLOCKING_SESSION_STATUS = 'VALID'` | 1 / 5 · 60초 / 300초 | ✔ |
| `ts_used_pct`(ITEM = 이름, 상위 3) | DBA_TABLESPACE_USAGE_METRICS | 85 / 95 | ✔ |
| `fra_used_pct` | V$RECOVERY_FILE_DEST (`SPACE_USED - SPACE_RECLAIMABLE`) | 80 / 90 | ✔ |
| `archive_dest_errors` | V$ARCHIVE_DEST_STATUS | — / 1 | ✔ |
| `last_backup_h` | V$RMAN_BACKUP_JOB_DETAILS(`COMPLETED%`의 마지막 끝 시각) | 26 / 50 | ✔ |
| `failed_jobs_24h` | DBA_SCHEDULER_JOB_RUN_DETAILS | 1 / — | ✔ |

### 사용률 기준 — 반드시 표기

`DBA_TABLESPACE_USAGE_METRICS.USED_PERCENT`는 **자동 확장 최대 크기 대비**다. 같은 XE의 SYSTEM이 [테이블스페이스] 섹션(지금 크기 대비)에서는 97.4%, 이 뷰에서는 61.7%로 나왔다.
늘어날 수 있으면 위험이 아니므로 **타일·점검은 최대 기준**으로 하고 "(최대 기준)"을 붙인다. [테이블스페이스] 섹션에는 "최대 대비(%)" 열을 더한다.
다만 최대가 OS 디스크 여유보다 크면 안심을 주는 쪽으로 오도한다. 이 한계는 TODO 6과 도움말에 적는다.
19c 문서는 ASM·파일 시스템 여유를 반영한다고 하는데, 버전별로 실측한다.

### 구현·실측 (Oracle, 2026-10-19)

- **계약**: `IMonitorProvider.Overview`(`MonitorOverview`), 지표 이름은 `MonitorMetrics`(닫힌 목록), 판정·글·순서는 `OverviewLogic`, 화면은 `OverviewWindow`이다.
- **표의 지표에서 바꾼 것·더한 것**:
  - 공간은 **영구 테이블스페이스만** 본다. 조사에 따르면 `DBA_TABLESPACE_USAGE_METRICS`는 UNDO를 만료된 익스텐트까지, TEMP를 고수위까지 사용으로 세어 오경보가 나기 때문이다.
  - 아카이브 대상 오류는 `ERROR`·`DISABLED`·`BAD PARAM`·`FULL`만 센다.
  - `cpu_count`(`GV$OSSTAT NUM_CPUS`)를 더해 평균 활성 세션을 CPU 수 대비(1배 주의, 2배 위험)로 판정한다.
  - 판정 없이 보이기만 하는 `uptime_h`·`log_mode`·`db_role`을 더했다.
- **11.2 XE, SELECT_CATALOG_ROLE 계정**: 지표 SQL 한 번이 0.1초다.
- **Folderss 1.8.8 안에서(격리 호스트)**:
  - 접속 7개가 각각 다음 상태로 나왔다: 주의(백업 기록 없음 — XE·19c), 권한 필요(ORA-00942), 비밀번호 필요, 접속 실패(닫힌 포트, ORA-12541, 약 7초), 지원 예정(PostgreSQL·SQL Server).
  - XE에 근원 1 + 대기 5 잠금을 만들자 위험 "잠금 대기 5"가 됐다.
  - 타일 메뉴 [잠금 트리 열기]가 성능 모니터를 [잠금] 섹션으로 열었고, 트리에서 근원을 끊자 타일이 주의 "잠금 대기 4"(새 근원)로, 풀린 뒤에는 원래대로 돌아왔다.
  - 비밀번호 창 열기·취소, 고정·빼기·다시 보이기·이름순, 설정 저장을 확인했다. 창을 닫으면 타일 세션이 정리되고(V$SESSION), 다시 열어도 고정이 남는다.
- **실측으로 고친 것**: 순서를 모든 타일이 끝난 뒤에만 고치면 느린 DB(닫힌 포트) 하나가 정렬을 몇 초 붙잡는다. 그래서 타일 하나가 끝날 때마다 순서·요약을 고친다. 고친 뒤 1.2초에 닿는 DB들이 이미 심각도순이었다.
- **남은 것**: 임계값의 접속별 덮어쓰기(8-3a SQLite와 함께), 미니 추이의 분 단위 보관(4장·이력), PostgreSQL·SQL Server 지표(8-5).

## 6. 일일 점검 리포트 (새 TODO 9)

### 화면

- 실행:
  - DB Helper 도구 > [일일 점검…], 또는 전체 DB 화면에서 여러 DB를 고른 뒤 [점검…]으로 연다.
  - 대화상자에서 대상 DB(여러 개)·점검 묶음·양식·저장 위치를 고른다.
  - 버튼은 기본 묶음과 기본 양식으로 바로 실행하고, ▾를 누를 때만 옵션 대화상자를 연다(Folderss의 옵션 규칙과 같음).
- 결과 창:
  - 분류별로 항목을 나열하고, 항목마다 상태 칩(정상·주의·위험·건너뜀·오류)과 값 요약을 보인다. 펼치면 발견 행(최대 100행)이 나온다.
  - 건너뜀은 이유를 보인다("권한 — SELECT_CATALOG_ROLE 필요", "11g — 경고 로그 뷰는 SYS 권한").
- [엑셀로 저장]:
  - 기본 양식은 표지(점검일·점검자·DB 수), 요약(DB × 항목 상태 표), DB별 상세 시트(`#sheet`)다.
  - 정의서처럼 사용자 양식으로 바꿀 수 있다.

### 계약

```csharp
internal sealed class MonitorCheck
{
    public string Id { get; }          // 'ora.tablespace' — 설정·결과 보관의 키(바꾸지 않음)
    public string Category { get; }    // 가용성 · 공간 · 백업 · 로그 · 객체 · 작업·세션 · 보안
    public string Title { get; }
    public string Sql { get; }         // 결과 행 = 발견 사항. 계약 열(선택): VALUE(수), LEVEL('OK' | 'WARN' | 'CRIT')
    public CheckRule Rule { get; }     // AnyRow(상태) · Threshold(VALUE, 주의, 위험) · ByLevel · Info
    public string Privilege { get; }
    public IReadOnlyList<MonitorRequirement> Requirements { get; }   // 버전·기능(아카이브 모드·FRA 사용 등) — 충족 안 되면 건너뜀(이유 표시)
    public string Advice { get; }      // 주의·위험일 때 조치 안내 한두 줄
}
internal sealed class CheckResult { /* Check, Status, Value, Columns, Rows(≤100), Elapsed, Message */ }
internal static class CheckRunner  { /* 전용 세션에서 항목을 차례로 — 항목마다 시간 제한, 하나가 실패해도 계속 */ }
```

- 임계값은 2.4와 같은 표를 쓴다(접속별 덮어쓰기).
- 기본 묶음 항목은 코드에 두고, **사용자 항목·덮어쓰기·실행 결과는 SQLite**에 둔다(아래 "저장소").
- 엑셀 변수는 다음과 같다. 정의서의 `ExcelTemplate`(`#sheet`·`#foreach`)을 그대로 쓴다.
  - `$report`(title·date·author·generatedAt)
  - `$dbs` → `$db`(name·address·version·role·summary(ok·warn·crit·skip·error)·checks)
  - `$check`(id·category·title·status·statusText·value·rowCount·columns·rows·advice·elapsed·message)

### Oracle 기본 묶음 (11.2 XE, SELECT_CATALOG_ROLE 계정 실측)

| 분류 | 항목 | 뷰 | 기본 판정 | 실측 |
|---|---|---|---|---|
| 가용성 | 인스턴스 상태·가동 시간·로그 모드 | V$INSTANCE · V$DATABASE | Info, NOARCHIVELOG면 주의 | ✔(지금 섹션) |
| 가용성 | 아카이브 대상 오류 | V$ARCHIVE_DEST_STATUS | 행 있으면 위험 | ✔ |
| 가용성 | Data Guard 지연 | V$DATAGUARD_STATS | 임계값(스탠바이일 때만 — 필요 조건) | 뷰 있음, 미실측 |
| 공간 | 테이블스페이스(최대 기준) | DBA_TABLESPACE_USAGE_METRICS | 85 / 95 | ✔ |
| 공간 | FRA(회수 가능 제외) | V$RECOVERY_FILE_DEST | 80 / 90 | ✔ |
| 백업 | 마지막 성공 백업 경과 | V$RMAN_BACKUP_JOB_DETAILS | 26 / 50시간 | ✔ |
| 백업 | 실패·경고 백업(24시간) | 같은 뷰 `STATUS` | 행 있으면 위험 | ✔ |
| 로그 | 경고 로그 ORA-(24시간) | V$DIAG_ALERT_EXT | 주의, ORA-00600·07445는 위험 | **✘ 11g는 ORA-00942** — 아래 참고 |
| 로그 | 로그 스위치 시간당 최대(24시간) | V$LOG_HISTORY | 임계값 | ✔(지금 섹션) |
| 객체 | INVALID 객체(내장 스키마 제외) | DBA_OBJECTS | 행 있으면 주의 | 구현 때 |
| 객체 | UNUSABLE 인덱스·파티션 | DBA_INDEXES · DBA_IND_(SUB)PARTITIONS | 행 있으면 위험 | 구현 때 |
| 객체 | 오래된 통계 | DBA_TAB_STATISTICS | 개수 임계값 | **✘ 그대로 쓰면 ORA-20001** — 아래 참고 |
| 작업·세션 | 실패한 스케줄러 잡(24시간) | DBA_SCHEDULER_JOB_RUN_DETAILS | 행 있으면 주의 | ✔ |
| 작업·세션 | 오래 실행 중인 세션(30분↑) | GV$SESSION `LAST_CALL_ET` | 행 있으면 주의 | ✔ |
| 작업·세션 | 진행 중 장기 작업 | V$SESSION_LONGOPS | Info | ✔ |
| 보안 | 14일 안에 만료되는 비밀번호 | DBA_USERS | 행 있으면 주의 | ✔ (내장 계정 제외 필요 — XE에서 14행) |

- **경고 로그**:
  - `V$DIAG_ALERT_EXT`는 11.2.0.2 XE에 뷰가 있지만, SELECT_CATALOG_ROLE 계정은 ORA-00942가 난다(sysdba는 됨). 문서화는 12.2부터다.
  - 11g~12.1은 필요 조건으로 "SYS가 따로 권한을 줘야 함"을 안내하고 건너뛴다.
  - 경고 로그는 XML 전체를 읽으므로 **마지막으로 읽은 시각 이후만** 조회한다.
  - PDB에 접속하면 그 컨테이너의 메시지만 보인다.
- **오래된 통계**:
  - `STALE_STATS`는 행마다 `DBMS_STATS`를 불러 계산하는데, 휴지통 객체(`BIN$…`)에서 ORA-20001로 문장 전체가 실패한다(휴지통 8개인 XE에서 재현).
  - `SELECT /*+ NO_MERGE */ … WHERE table_name NOT LIKE 'BIN$%'` 인라인 뷰로 먼저 거른 뒤 `STALE_STATS`를 보면 된다(실측 확인).
  - 같은 WHERE에 조건만 넣는 방식도 이번엔 통했지만, 실행 계획에 따라 달라질 수 있어 쓰지 않는다.
- **FRA**: `SPACE_USED`에는 예전 FRA에 만든 파일도 들어간다. 반드시 `SPACE_RECLAIMABLE`을 뺀다.
- **DB 링크 연결 시험**은 부작용(원격 세션)이 있어 기본 묶음에서 뺀다.

### 저장소 — 이력 설계의 SQLite(history.db)에 함께 (결정 2026-10-19)

사용자 점검 항목, 항목별 켬·끔과 임계값 덮어쓰기, 실행 결과를 이력 설계 5절의 `history.db`에 같은 방식으로 둔다(테이블 추가, `schema_version` 올림).

> 이 SQLite 계층은 아직 구현되지 않았다(이력 설계 5-3에서 결정만 함). 그래서 일일 점검 단계(9장 8-3a)에서 먼저 만든다.
> 첫 작업은 Windows의 Folderss 안에서 `e_sqlite3`가 불러지는지 확인하는 PoC다.
> 실패하면 점검 항목·결과는 JSON 파일로 대체한다(이력 설계의 JSONL 대체안과 같은 판단).

```sql
CREATE TABLE check_item (            -- 사용자 점검 항목(기본 묶음은 코드에 있고 여기 없다)
  id         TEXT PRIMARY KEY,       -- 'user.<이름>' — 기본 항목 id('ora.*')와 겹치지 않게
  kind       TEXT NOT NULL,          -- oracle · postgres · mssql
  category   TEXT NOT NULL,
  title      TEXT NOT NULL,
  sql        TEXT NOT NULL,          -- SELECT 한 문장만(저장 전에 SqlScript로 검사 — 지금 섹션 SQL 시험과 같은 기준)
  rule       TEXT NOT NULL,          -- JSON: {"type":"anyRow","level":"warn"} · {"type":"threshold","column":"VALUE","warn":80,"crit":90} · {"type":"byLevel"} · {"type":"info"}
  advice     TEXT,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);
CREATE TABLE check_override (        -- 기본·사용자 항목의 켬·끔과 임계값을 전체('') 또는 DB(db_key)별로
  db_key  TEXT NOT NULL DEFAULT '',
  item_id TEXT NOT NULL,
  enabled INTEGER,                   -- NULL이면 위 단계 값을 따른다(DB → 전체 → 기본)
  warn    REAL,
  crit    REAL,
  PRIMARY KEY (db_key, item_id)
) WITHOUT ROWID;
CREATE TABLE check_run (
  id          INTEGER PRIMARY KEY,
  db_key      TEXT NOT NULL REFERENCES db(db_key),
  started_at  TEXT NOT NULL,         -- UTC ISO-8601
  elapsed_ms  INTEGER,
  app_version TEXT                   -- 플러그인 버전(기본 묶음이 바뀐 결과를 구분)
);
CREATE TABLE check_result (
  run_id    INTEGER NOT NULL REFERENCES check_run(id) ON DELETE CASCADE,
  item_id   TEXT NOT NULL,
  status    TEXT NOT NULL,           -- ok · warn · crit · skip · error
  value     REAL,                    -- 판정에 쓴 수(없으면 NULL)
  row_count INTEGER,
  reason    TEXT,                    -- 건너뜀·오류의 이유 분류('privilege' · 'version' · 'timeout' …) — 오류 문장 원문은 넣지 않는다
  PRIMARY KEY (run_id, item_id)
) WITHOUT ROWID;
```

- **발견 행은 저장하지 않는다.** 결과에는 상태·값·행 수·이유 분류만 남긴다.
  - 행에는 객체·사용자 이름 같은 DB 내용이 들어 있어서, 이력 설계의 "파일에는 숫자와 테이블스페이스 이름만" 원칙을 지키려는 것이다.
  - 행은 엑셀 산출물과 결과 창(그 실행 동안)에만 있다.
  - 사용자 항목의 SQL은 사용자가 쓴 설정이라 저장한다.
- **직전 대비**(AHF의 "악화" 표시): 같은 DB의 직전 `check_run`과 항목별 상태·값을 비교해 결과 창·엑셀에 "↑ 나빠짐 / ↓ 좋아짐 / 새로 생김"을 보인다.
- **언제 기록하나**:
  - 사용자가 점검을 실행할 때마다 기록한다. 주기적 지표 이력(기본 끔)과 달리, 사용자가 직접 실행한 결과이기 때문이다.
  - 설정 탭 "성능 모니터"에서 끌 수 있고, 보관 기간은 이력과 같은 값(기본 90일)이다.
- **나눠 쓰기**: SQLite는 PC마다 따로라, 사용자 항목은 [내보내기]·[가져오기](JSON 파일 하나)로 팀에 나눈다. 가져올 때 같은 id가 있으면 덮어쓸지 묻는다.
- **편집 화면**: 점검 결과 창의 [항목 관리…]에서 추가·고치기·지우기를 한다.
  - [시험 실행]은 고른 DB에 대고 SQL을 한 번 돌려 판정 결과를 미리 보인다.
  - 규칙 편집기는 결과 열 이름을 고르는 콤보로, 열 이름을 손으로 치지 않게 한다.

### 구현·실측 (Oracle, 2026-10-20)

- **계약**: `IMonitorProvider.CheckSet`(`MonitorCheckSet` — 서버 식별 SQL `IDENTITY`·`LABEL` + `MonitorCheck` 목록). 위 스케치의 `Value`·`Level` 계약 열 대신
  규칙이 **열 이름**을 가리킨다(`Threshold("사용률(%)", 85, 95)`·`ByLevel("판정")`) — 결과 열 별칭이 곧 화면·엑셀 머리글이라 별칭 하나로 둘 다 된다.
  `CheckRule.Threshold`에 **값이 없을 때 단계**(`missing`)를 더했다 — 마지막 백업은 기록이 없으면 `MAX`가 NULL 한 행이라 "값 없음 → 주의".
- **실행**: `CheckRunner`(DBMS 중립, `Ui/CheckLogic.cs`) — 필요 조건 → 조회(항목마다 60초, 1,000행까지 세고 100행 보관) → 판정. 권한 오류는 공급자 `IsPermissionDenied`로
  "건너뜀(privilege)", 조건 불충족은 "건너뜀(requirement)", 시간 초과는 "오류(timeout)". 창은 DB마다 점검 전용 세션(동시 4개, 비밀번호 질문은 하나씩).
- **저장소**: `MonitorStore`(`Db/MonitorStore.cs`) — 위 스키마 그대로 + `db`(db_key·kind·label·처음/마지막 본 시각). WAL·busy_timeout 5초·풀링 끔(작업마다 열고 닫음),
  `auto_vacuum=INCREMENTAL` + 보관 기간 정리 뒤 `incremental_vacuum`. 더 새 스키마면 읽기만, 열 수 없으면 창이 기록을 끄고 기본 항목만 돈다.
  **PoC(8-3a ①)**: Folderss와 같은 `AssemblyDependencyResolver` 로드 컨텍스트에서 `e_sqlite3.dll`(publish 폴더 바로 아래)이 `LoadUnmanagedDll`로 불러짐 — JSON 대체는 필요 없다.
- **엑셀**: `CheckReportModel`·`CheckReportTemplate` — 요약 시트(DB별 상태 수, 가장 나쁜 상태 줄 색, 주의 이상 항목) + `#sheet($db in $dbs)`(항목 표 상태별 줄 색을
  `#if($check.status == "crit") … #elseif …`로 고른다 — 조건부 서식은 줄 이동을 따라가지 않음, 발견 사항은 열 8칸 `c1~c8`·`v1~v8`). 표지는 요약 시트에 합쳤다.
- **실측(XE 11.2, SELECT_CATALOG_ROLE 계정, 헤드리스 화면 시험)**: 17개 중 주의 2(NOARCHIVELOG · RMAN 기록 없음), 건너뜀 1(경고 로그 ORA-00942 — 필요 권한 안내), 정상 14,
  0.7초. 닫힌 포트 DB는 "점검 못 함 ORA-12541"로 다른 DB와 따로. 두 번째 실행에 직전 시각·"같음", 끈 항목은 "건너뜀 — 꺼 둔 항목", 덮어쓴 문턱이 판정에 쓰임.
- **아직**: Data Guard 지연(스탠바이 환경), DB별 덮어쓰기 화면, 예약 실행, 19c 실측, PostgreSQL·SQL Server 묶음(8-5).

## 7. 결정 사항

| # | 질문 | 결정(2026-10-19) |
|---|---|---|
| D1 | 구현 순서 | **3장 잠금 트리 → 5장 전체 DB → 6장 일일 점검 → 4장 활성 세션 이력**. 잠금 트리가 가장 작고 세션 키·GV$·RAC 종료 같은 공통 기반을 먼저 세운다. 전체 DB와 일일 점검은 지표·임계값·여러 DB 실행기·엑셀을 함께 쓴다. 활성 세션 이력이 가장 크다(차트·표본 엔진·전용 세션) |
| D2 | 활성 세션 표본은 언제 모으나 | **기본은 모니터 창이 열린 동안, 설정에서 접속별 "상시 표본"을 켜면 DB Helper가 열린 동안 계속.** 표본 엔진은 창과 떨어진 서비스로 두고 창은 그 버퍼를 본다(창을 열면 그 전 표본부터 보인다). 파일 저장(분 단위 요약)은 나중에 이력 5-3과 함께 정하며, SQL_ID를 파일에 남겨도 되는지를 그때 결정 |
| D3 | 전체 DB 대상 | (추천안으로 진행) 저장된 비밀번호가 있는 모든 접속 + 타일 메뉴 [이 화면에서 빼기] |
| D4 | 사용률 기준 | (추천안으로 진행) 타일·점검은 최대 기준 + 표기, 섹션 표에는 둘 다 |
| D5 | 점검 결과 보관(직전 대비) | **SQLite(history.db)에 기록** — 상태·값·행 수·이유 분류만(발견 행 제외). 6장 "저장소" |
| D6 | 사용자 점검 항목 | **SQLite에 저장하고 화면에서 추가·고치기**, 팀 공유는 JSON 내보내기·가져오기. 6장 "저장소" |
| D7 | RAC | (추천안으로 진행) 새 기능은 처음부터 GV$. 기존 섹션 전환은 RAC 실측 환경을 구한 뒤 |

## 8. 하지 않는 것

- Diagnostics·Tuning Pack 뷰(ASH·AWR·SQL Monitor). 3단 서버 이력(이력 설계 5-5)에서 팩 설정이 있을 때만 다룬다.
- 서버에 수집 테이블·잡 만들기, X$ 직접 접근(SYS에 뷰를 만들어야 함) — 안내만 한다.
- 창이 닫혀 있을 때의 감시·OS 알림 — 이력 설계 6절과 같은 한계다.
- DB 링크 연결 시험.

## 9. 단계와 완료 조건

| 단계 | 내용 | 완료 조건(검증 가능) |
|---|---|---|
| 8-0 | 2장 공통 기반: `IMonitorProvider` 확장, 세션 키, 대기 분류, 지표 목록, 상태·임계값 | 중립 로직 단위 테스트, PG·SQL Server·Generic은 null·빈 목록으로 기존 테스트 통과 |
| 8-1 ✔ | 3장 잠금 트리 (Oracle — 2026-10-19 완료, 3장 "구현·실측") | `LockTreeLogic` 테스트(근원·자손 수·유발 대기·순환·여러 블로커·최소 대기). XE 11g·19c에서 2단 연쇄(A→B→C)와 쉬는 근원을 만들어 실측(`FINAL_BLOCKING_SESSION`과 근원 일치), 근원 종료 뒤 B·C 진행 |
| 8-2 ✔ | 5장 전체 DB (Oracle — 2026-10-19 완료, 5장 "구현·실측") | `OverviewLogic` 경계값 테스트, 접속 실패·비밀번호 없음·시간 초과 타일, 동시 4개 제한 테스트. 실측: 3개 이상 DB를 동시에 |
| 8-3a ✔ | SQLite 저장소(이력 설계 5-3의 파일·스키마 버전·손상 처리) + 점검 테이블 | ① Windows Folderss 안에서 `history.db` 열고 쓰고 읽기 PoC(실패하면 JSON 대체로 전환) ② 스키마 생성·버전 올림·새 버전 파일은 읽기만 ③ 두 연결 동시 쓰기(busy_timeout) ④ 손상 파일이면 기록만 끄고 예외 없음 |
| 8-3b ✔ | 6장 일일 점검 실행·판정·엑셀 | `CheckRunner` 테스트(시간 제한·권한·필요 조건·하나 실패해도 계속), 직전 대비 비교 테스트, 결과에 발견 행이 저장되지 않음 테스트, 기본 양식 렌더 테스트. 실측: SELECT_CATALOG_ROLE 계정으로 전체 묶음, 11g의 경고 로그 건너뜀 안내 |
| 8-3c ✔ | 사용자 점검 항목 관리 | SELECT 한 문장 검사·규칙 JSON 검사·id 충돌 테스트, 내보내기→가져오기 왕복 테스트, [시험 실행] 실측 |
| 8-4 | 4장 활성 세션 이력 | `SampleBuffer`·`SampleLogic` 테스트(구간 집계·분류 몫·Top N·메모리 상한·STATE 판정). 실측: 표본 1회 소요(세션 100개), 수집 부하 표시, 일부러 만든 부하(CPU·잠금·I/O)가 차트 분류로 나뉘는지 |
| 8-5 | PostgreSQL·SQL Server | 2.3 대응표 확정 후 공급자별로 같은 화면이 동작 — 각 기능의 실측을 같은 방식으로 |

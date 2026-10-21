# 추후 작업

미완료 개발 요청 목록. 끝나면 `[x]`로 바꾸고 PR·버전을 적는다.

## 결과 편집 (빠른 조회 F1)

원칙: **큰 데이터(LOB·잘려 보이는 값)는 편집 대상에서 빼고, 화면과 저장 두 겹으로 막는다.**

- [x] 저장 단계 LOB 차단 — `RowEditLogic.UpdateStatement`가 값만 비교하므로 편집할 수 없는 형식(CLOB·BLOB·RAW·XMLTYPE·bytea 등)과
      잘린 값("…(전체 n자)")은 값이 달라 보여도 SET에서 빼거나 저장을 거부한다. 지금은 편집 창이 읽기 전용으로 막는 것에만 기댄다
      (다른 경로로 행 값을 바꾸면 잘린 표시 글이 LOB에 저장될 수 있음). 테스트로 묶을 것.
      → 편집 창과 같은 `EditBlockReason`으로 `UpdateBlockReason`·`InsertBlockReason`을 두고, 고칠 수 없는 칸이 바뀌었으면
      저장 전체를 거부(열 이름 넣은 메시지, Update/InsertStatement는 예외). 그대로면 SET에서 빠짐. INSERT는 잘린 값만 거부(LOB 열은 원래대로 뺌).
      PR: · 버전:
- [x] 편집 팝업 변경 관리 — 원래 값과 다른 칸 강조, 툴팁 "원래 값: …", 칸별 ↶ 되돌리기·[모두 되돌리기],
      이미 고친 행을 다시 열어도 원래 값(`ResultGridRow.Original`) 기준으로 비교.
      → `RowEditChanges`(Ui/RowEditChangeLogic.cs). 새 행은 창을 열 때 값이 기준("처음 값"). PR: · 버전:
- [x] 편집 팝업 닫기 확인 — 바꾼 칸이 있는데 [취소]·Esc면 "버리고 닫을까요?"(기본 [계속 편집]).
      X·Alt+F4도 같다. 비교는 창을 열 때 값 기준(`RowEditChanges.HasChanges`), [확인]이면 묻지 않음. PR:  · 버전:
- [ ] 그리드에서 바뀐 칸만 강조 — 행은 지금처럼 주황, 바뀐 칸은 더 진하게(찾기 강조와 같은 방식).
- [ ] 복제 시 LOB 처리 — 지금은 LOB 열이 INSERT에서 빠져 NULL(원본 내용 손실, NOT NULL이면 오류).
      서버에서 그대로 복사(`INSERT … SELECT 원본 LOB FROM t WHERE 키 = :원본키`)하거나 최소한 복제 창에 경고.
- [ ] 동시 수정 검사(낙관적 잠금) — UPDATE WHERE에 바꾼 열의 원래 값도 넣어 다른 세션의 변경을 덮어쓰지 않게.
      FLOAT·초 미만 날짜처럼 표시 값과 DB 값이 정확히 같지 않은 형식은 비교에서 빼야 함(오탐 0행), NULL은 IS NULL.
- [ ] RAW 기본 키 지원 — RAW(16) GUID 기본 키 테이블은 지금 편집 불가. RAW는 2,000바이트 이하라 잘리지 않으므로
      "0x…" 표시를 바이트로 되돌려 바인드. 표시 형식은 테스트로 고정.
- [ ] (보류) 짧은 CLOB 편집·BLOB 파일 넣기/꺼내기 — 큰 데이터는 편집하지 않는 원칙에 따라 필요해질 때 다시 검토.
- [x] 기본 키가 없는 테이블 편집 — 고유 키(NOT NULL UNIQUE) → Oracle ROWID 순으로 대체하는 방안 검토
      (PostgreSQL ctid는 UPDATE마다 바뀌어 제외, SQL Server는 행 주소 없음).
      → 기본 키 → NOT NULL 고유 키(열 수·이름순) → Oracle 보통 테이블 ROWID(`RowEditLogic.ChooseKey`).
      ROWID는 빠른 조회 SELECT 끝에 붙여 가져와 그리드에 보이기 전에 뗌, WHERE `ROWID = :rid`. 그 밖 DBMS는 편집 불가 그대로.
      PR: · 버전:

## SQL 편집기 구문 강조 (AvalonEdit)

계획·실측 항목·테스트 데이터: [`docs/sql-editor-avalonedit-plan.md`](../sql-editor-avalonedit-plan.md). 범위는 SQL 작업 영역 편집기만.

- [x] 1. PoC 실측(Windows) — 플러그인 로드, 성능, 한글 IME, 단축키 충돌, 테마 7종. 막는 문제가 있으면 여기서 중단.
      → 시험판 창은 main에 들어감(PR #24). 헤드리스 A·B·D·E 실측(2026-10-21, `EditorPocHeadlessTests`): 막는 문제 없음.
      C 한글 IME: 사용자가 Folderss에서 써 보고 특별한 문제 없다고 함(2026-10-22) — 아래 항목은 하나씩 따로 보지 않았고, 쓰면서 문제가 보이면 따로 고친다. 키·줄 간격·키워드 범위는 2026-10-21 결정(계획서 8절) — 2-3에 반영.
- [x] 2. 주석·문자열 구간 계산 — `SqlScript.HighlightSpans`(문장 나누기와 같은 토큰 규칙), D1 경계 사례 테스트 `SqlHighlightTests`.
      단독 시간(Linux 참고값): 1만 줄 6.6ms · 5만 줄 37ms · 10만 줄 53ms → 1만 줄 넘으면 4단계에서 지연·증분 계산 필요.
- [x] 2-1. DBMS별 구문 규칙 `SqlLexRules`(문장 나누기·강조 공통) — Oracle(그대로), PostgreSQL($$·E''·중첩 주석, 블록 없음),
      SQL Server(GO 줄·[식별자]·BEGIN TRAN). SQL 탭은 대상 DB 규칙으로 실행. 테스트 `SqlLexRulesTests`, PostgresIT 스크립트 실행.
- [x] 2-2. 키워드·증분 강조(2026-10-21 요청: SELECT·FROM·조건·테이블 구문 등 키워드 색) — `SqlScript.EditorSpans`·DBMS별 `SqlKeywords`,
      바뀐 곳 근처만 다시 계산하는 `SqlHighlighter`, 편집기에 붙이는 `SqlEditorHighlighting`(4단계가 그대로 씀), 테마별 색 `SqlHighlightLogic`.
      PoC 창에 넣어 헤드리스로 확인: 키워드 47곳·테마 7종 대비 4.5:1·키 입력 3~12ms(전체 다시 계산은 9~73ms). 테스트 `SqlKeywordTests`·`SqlHighlighterTests`·`SqlHighlightLogicTests`.
- [x] 2-3. 설정 "SQL 편집기" 탭(2026-10-21 요청: 줄 간격 같은 선택 설정, 키워드·강조 스타일과 대상 관리, 긴 줄 기준 기본 2만 자·변경 가능) —
      `SqlEditorSettingsPage`·`SqlEditorSettings`(키 sqlEditor.settings, JSON)·`SqlEditorSetup`(합성 글꼴로 줄 간격, 키 결정: Ctrl+D 끔·Insert 겹쳐쓰기),
      자료형을 키워드와 따로(`SqlSpanKind.DataType`). 저장하면 열린 편집기가 바로 따름. 테스트 `SqlEditorSettingsTests`, 헤드리스 `S_SettingsPage_AppliesToEditor`.
- [x] 3. `SqlEditor` 전용 컨트롤로 교체 — TextBox 1:1 흉내가 아닌 의도 단위 API, 직접 그린 줄 번호 삭제, 컨트롤 기술서 갱신.
      → 2026-10-22 `Ui/SqlEditor.cs`(AvalonEdit을 품음), SQL 탭·편집 메뉴·찾기·파일·임시 저장을 옮김, 직접 그린 줄 번호와 PoC 창(도구 메뉴) 삭제,
      컨트롤 기술서 `sql-editor`. 회귀 확인표(계획서 6절)는 헤드리스 `SqlEditorRegressionTests`(R1~R7, Oracle 실행 포함).
- [x] 4. 강조 색칠 연결 — `SqlEditor`에 `SqlEditorHighlighting`을 붙이고 탭 대상 DB가 바뀌면 규칙을 바꾼다, 실측 재측정. → 3단계와 함께(2026-10-22).
- [ ] (별도) 테이블 이름 강조 — 메타데이터 대조 방식, 1~4 이후 검토.

## 성능 모니터 (DBA가 실제로 쓰려면)

검토(2026-10-19): 지금은 "가끔 들여다보는 조회 창"이다. 아래 순서로 채운다. 1·2는 진행하기로 함.

- [ ] 1. 세 DBMS 실 검증 — 모든 섹션·상세·조치 SQL을 Oracle 11g/19c · PostgreSQL 10+ · SQL Server 2012+에 실제로 붙여 돌려 본다
      (이 세션의 리눅스 환경에서는 못 함). 문법 오류 한 건이 도구 전체 신뢰를 깎는다.
      → 임시(V$SORT_SEGMENT)·UNDO(DBA_UNDO_EXTENTS ACTIVE·UNEXPIRED) 사용률 수정은 함. PR: #20 · 버전: 1.9.0
      → 실측(2026-10-19, Folderss 1.8.8 안에서): Oracle 11.2 XE · 19.9, PostgreSQL 16, SQL Server 2022(·2008 SP1) — 모든 섹션·상세·실제 잠금·세션 종료.
      찾은 것 4건 수정(SQL Server 기본 정보 정렬 충돌, MARS 세션 SQL 전문 중복, Oracle 잠금 객체 '.', PostgreSQL 크기 함수 권한). 남은 것: PostgreSQL 10~15,
      SQL Server 2012~2019 실측, SQL Server 2012 미만 버전 안내(필요 조건).
- [x] 2. 세션 상세와 세션 종료 — 잠금·세션 행을 누르면 SQL 전문·실행 계획(Oracle DBMS_XPLAN, SQL Server XML)·세션 정보/통계,
      오른쪽 메뉴 [세션 종료…](ALTER SYSTEM KILL SESSION · pg_terminate_backend · KILL)는 위험한 문장 확인 대화상자 + 읽기 전용 접속 차단.
      → 잠금 연쇄(누가 누구를 막는지)를 트리로 보이는 것은 아직 아님. PR: #20 · 버전: 1.9.0
      → 잠금 연쇄 트리 설계: [`docs/monitor-dba-design.md`](../monitor-dba-design.md) 3장(근원 블로커·유발 대기·최소 대기 3초·근원 종료, GV$·RAC 종료).
      → 잠금 연쇄 트리 Oracle 구현(8-1): [잠금] 섹션 기본 보기, [표로 보기], [근원 세션 종료…]. 11.2 XE·19.9와 Folderss 안에서 실측. PR: #21.
      남은 것: PostgreSQL·SQL Server 트리(8-5), RAC 실측.
- [x] 3. 느린 SQL — Top SQL 섹션(Oracle V$SQLAREA, PostgreSQL pg_stat_statements(확장 유무 확인), SQL Server sys.dm_exec_query_stats)과
      대기 이벤트 요약(V$SESSION_WAIT 집계 · pg_stat_activity wait_event 집계 · sys.dm_os_wait_stats).
      → [느린 SQL] 누적 소요 상위 50(행 상세: 전문·실행 계획·Oracle 자식 커서), [대기 이벤트] Oracle V$SYSTEM_EVENT(Idle 제외)·SQL Server dm_os_wait_stats(유휴 제외)·
      PostgreSQL은 이 순간 활성 세션 집계(누적 없음). pg_stat_statements는 13+ 열 이름이라 12 이하는 오류. PR: #20 · 버전: 1.9.0
- [x] 4. 모니터 전용 세션 분리 — 지금은 SQL 탭과 세션(트랜잭션·잠금)을 같이 써서 자동 새로 고침이 사용자 실행과 서로 기다린다.
      상세 결과 캐시와 조회 소요 시간 표시도 함께.
      → 창마다 전용 DbSession(처음 가져올 때 열고 끊기면 다시 엶, 창·DB Helper 창을 닫으면 정리). 비밀번호는 저장값, 없으면 다시 묻는다(SQL 탭에서 입력한 값은
      메모리에 두지 않는 원칙). 상태 글에 소요 시간, 상세는 (상세, 키)별 캐시(섹션 새로 고침 때 비움). PR: #20 · 버전: 1.9.0
- [ ] 5. 변화 보기 — 직전 스냅샷과의 차이(세션 수 증감·테이블스페이스 증가량·로그 스위치 증가), 창이 열려 있는 동안의 간단한 이력,
      임계값(지금 80·90% 고정) 설정과 Folderss 메시지 탭·트레이 알림. 수집기·저장소가 필요한 영역이라 범위를 먼저 정할 것.
      → 설계안: [`docs/monitor-history-design.md`](../monitor-history-design.md). 채택: 3단(메모리 · 로컬 숫자 파일 · 서버 자체 이력),
      로컬 파일 이력은 설정 탭 "성능 모니터"의 선택지(기본 끔, 주기·제외 접속, 보관 기간은 기본 90일·설정에서 숫자로 정하는 선택 값 7~3650일), 저장소는 SQLite(history.db 하나 — PGlite 비권장,
      Windows에서 e_sqlite3 로드가 안 되면 JSONL). 남은 결정: OS 알림 계약 변경, AWR 읽기.
      순서 5-1 메모리 구간·추이 → 5-2 임계값·배지·메시지 → 5-3 이력 파일·설정 탭 → 5-4 가득 참 예측.
      → 5-1 확장(라이선스 없는 Top Activity — V$SESSION 표본, 분류별 누적 차트·구간 선택·Top SQL/세션): [`docs/monitor-dba-design.md`](../monitor-dba-design.md) 4장.
- [ ] 6. 저장 공간 보강 — 증가 추세("며칠 뒤 가득 차는가": DBA_HIST_TBSPC_SPACE_USAGE는 Diagnostic Pack 라이선스 필요, 아니면 자체 스냅샷),
      ASM·OS 디스크 여유(자동 확장의 실제 한도 — 없으면 "최대(MB)"가 안심을 주는 쪽으로 오도).
- [ ] 7. 권한 현실 — 운영 DB에서 애플리케이션 계정에 SELECT_CATALOG_ROLE을 주는 곳은 드물다. 같은 DB에 "모니터 전용 계정"을 묶는 접속 설정,
      PostgreSQL `<insufficient privilege>` 값을 권한 안내로 바꾸기.
- [ ] 8. 전체 DB 한 화면 — 등록한 DB마다 타일(가장 심각한 상태 색과 그 이유 한 줄, 활성 세션·미니 추이, 공간(최대 기준)·FRA·마지막 백업), 심각도 정렬·고정,
      누르면 그 DB의 성능 모니터. Oracle 먼저, 지표는 긴 형식(METRIC·ITEM·VALUE)으로 이력 설계와 같은 이름. 설계: [`docs/monitor-dba-design.md`](../monitor-dba-design.md) 5장.
      → Oracle 구현(8-2): 도구 > 전체 DB 상태…, 타일 상태 9가지, 고정·빼기, [잠금 트리 열기]. Folderss 안에서 7개 접속으로 실측. PR: #21.
      남은 것: 타일 임계값 덮어쓰기(SQLite 저장소는 8-3에서 생김 — 화면만), PostgreSQL·SQL Server 지표(8-5).
- [ ] 9. 일일 점검 리포트 — 점검 항목(가용성·공간·백업·로그·객체·작업·보안)을 여러 DB에 한 번에 돌려 정상·주의·위험·건너뜀으로 판정하고 엑셀로(정의서 양식 엔진 재사용).
      Oracle 기본 묶음 먼저. 11g 경고 로그 권한·오래된 통계의 휴지통 ORA-20001 같은 함정은 설계 6장. 설계: [`docs/monitor-dba-design.md`](../monitor-dba-design.md) 6장.
      사용자 점검 항목·덮어쓰기·실행 결과(상태·값만, 발견 행 제외)는 SQLite history.db에 — 저장소 계층(이력 5-3)이 아직 없으므로 이 단계에서 먼저 만든다(8-3a).
      공통 기반(공급자 인터페이스 확장·세션 키·대기 분류·지표·임계값)과 구현 순서·결정 사항은 같은 문서 2·7장.
      → Oracle 구현(8-3a·b·c, 2026-10-20): 도구 > 일일 점검…(전체 DB 상태의 [일일 점검…]), 기본 항목 17개, history.db(SQLite — 플러그인 로드 컨텍스트에서
      e_sqlite3 로드 확인)에 상태·값·행 수 기록과 직전 대비, 기본·내 엑셀 양식, [항목 관리…](켬·끔·문턱·사용자 항목·시험 실행·JSON 내보내기/가져오기),
      설정 탭 "성능 모니터"(기록·보관 기간·이력 지우기). XE 11.2에 대고 헤드리스 화면 시험으로 확인. PR: #21.
      남은 것: DB별 덮어쓰기 화면, Data Guard 지연 항목(스탠바이 실측 환경), 예약 실행, PostgreSQL·SQL Server 묶음(8-5), 19c 실측.
      바로 이어서 할 일과 시험 환경: [`next-steps.md`](next-steps.md).

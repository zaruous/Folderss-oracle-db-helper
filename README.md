# DB Helper

[Folderss](https://github.com/zaruous/Folderss) 파일 관리자 안에서 Oracle·PostgreSQL·SQL Server DB에 접속해 객체를 찾아보고 SQL을 실행하며, 성능 모니터(기본 정보·테이블스페이스·로그·잠금·세션)를 보는 플러그인입니다.
`⋯ 메뉴 > 플러그인 > DB Helper`를 누르면 팝업 창으로 열립니다.

## 설치

1. Folderss `설정 > 플러그인 > GitHub에서 설치…`에 이 저장소 주소(`https://github.com/zaruous/Folderss-oracle-db-helper`)를 넣습니다.
   GitHub 설치 기능이 없는 Folderss라면 [Releases](https://github.com/zaruous/Folderss-oracle-db-helper/releases)에서 zip을 받아 `플러그인 찾기…`로 등록합니다.
2. `⋯ 메뉴 > 플러그인 > DB Helper`를 열고 `접속 관리…`에서 접속을 추가합니다(호스트·포트·서비스명·사용자·비밀번호).
3. 트리의 DB 앞 ▶를 누르거나 DB를 두 번 눌러 연결합니다.

## 기능

| 영역 | 내용 |
|---|---|
| 메뉴·아이콘 | 맨 위 메뉴 막대(파일·편집·SQL·연결·도움말, `Alt+F` 등)와 그 아래 줄의 작은 아이콘 막대(새 탭·열기·저장 / 실행·취소 / 커밋·롤백 / 연결·다시 연결·끊기). 도움말 > 단축키에 전체 목록 |
| SQL 파일 | `Ctrl+N` 새 탭, `Ctrl+O` 열기(여러 개), `Ctrl+S` 저장, `Ctrl+Shift+S` 다른 이름으로 저장, `Ctrl+W` 탭 닫기(탭 머리 오른쪽 클릭: 다른 탭·왼쪽·오른쪽 탭 모두 닫기), 탭이 넘치면 휠 스크롤·▾ 탭 목록, 최근 파일 10개. 연 파일의 인코딩(UTF-8·UTF-8 BOM·UTF-16·**CP949**)과 줄바꿈(CRLF·LF)을 그대로 저장. 고친 탭은 이름 뒤에 `*`, 닫을 때 저장할지 물어봄 |
| 편집 | `Ctrl+F` 찾기 팝업(대소문자 무시, `F3`·`Shift+F3` 다음·이전, 찾은 위치로 스크롤), `Ctrl+/` 줄 주석 토글, `Ctrl+Shift+U`·`Ctrl+Shift+L` 선택 영역 대·소문자, 되돌리기·잘라내기·복사·붙여넣기 메뉴 |
| 접속 관리 | 추가·복제·삭제·편집, 접속 테스트, 비밀번호는 DPAPI로 암호화해 저장(저장 안 하고 연결 때마다 입력도 가능), 읽기 전용, 색 표시(개발 초록 · 검증 노랑 · 운영 빨강) |
| DB 트리 | DB → 스키마(내 스키마 먼저, 내장 스키마는 숨김) → 테이블·뷰·시퀀스·프로시저/함수/패키지 → 열. 펼칠 때 불러오고, 처음 1,000개를 보인 뒤 [더 보기]로 더 불러옴. 오른쪽 메뉴: 연결·**다시 연결**(끊고 저장된 접속 정보로 다시 연결, 커밋 대기는 물어봄)·연결 끊기, 테이블·뷰는 빠른 조회·테이블 정보·SELECT 문 넣기 |
| 빠른 조회 | 트리의 테이블·뷰에서 `F1`: 앞 100행을 그 DB 탭에서 바로 조회(편집기 글은 그대로, 실행 기록에 남음). [다음 행 가져오기]로 이어 읽음. 결과 위 편집 막대의 [편집]은 고른 행의 값을 편집 창(열마다 값·NULL 체크, 날짜 칸은 📅·`Alt+↓`로 여는 전용 날짜·시각 선택(달력·1달/1년 이동·시각·[오늘]·[지금], 테마 색))에서 고치고, [삭제]는 고른 행에 삭제 표시(다시 누르면 풂, 저장 전 새 행은 바로 뺌), [행 추가]는 빈 새 행, [복제]는 고른 행 값으로 새 행을 편집 창에서 채워 추가(LOB·잘린 값은 비움), [저장]은 삭제 표시한 행마다 `DELETE`, 고친 행마다 `UPDATE`(바뀐 열만) — 둘 다 **기본 키**(없으면 NOT NULL 고유 키, Oracle 테이블은 그것도 없으면 숨겨 가져온 ROWID)로 — 새 행마다 `INSERT`(빈 칸 열은 빼서 DB 기본값·자동 번호)를 이 순서로 실행(확인 창에 문장을 보임). 버튼은 [행 추가]·[복제]·[편집]·[삭제] │ [저장] │ WHERE 순. 고친 행은 주황 바탕·`✎`, 삭제 표시한 행은 빨강 바탕·`✖`·취소선, 새 행은 초록 바탕·`＋`로 표시. 저장한 변경은 다른 DML처럼 커밋 대기([커밋]·[롤백]). 편집 막대의 **WHERE 칸**에 조건을 넣고 `Enter`·[다시 조회]하면 그 조건으로 같은 테이블을 다시 조회해 편집(조건은 `WHERE ( … )` 괄호 안에서만 실행 — `;`·주석·짝 안 맞는 괄호·`q'`/`E'` 같은 접두 문자열·대괄호·`$` 인용은 막음, 저장 안 한 변경이 있으면 버릴지 물음). 기본 키·NOT NULL 고유 키가 없는 테이블·뷰(Oracle 테이블은 ROWID로 편집), 읽기 전용 접속, LOB·RAW·시간대 있는 TIMESTAMP 같은 열과 잘려 보이는 값은 편집하지 않음 |
| 소스 보기 | 트리의 뷰·시퀀스·프로시저·함수·패키지에서 오른쪽 클릭 [소스 보기](시퀀스·프로시저·함수·패키지는 `F4`): 소스 창(읽기 전용, [SQL 탭에 넣기]·[복사]). Oracle은 `CREATE OR REPLACE` 문(코드는 `/` 줄, 패키지는 명세+본문), PostgreSQL은 `pg_get_viewdef`·`pg_get_functiondef`(오버로드마다), SQL Server는 만든 그대로의 정의. 시퀀스는 데이터 사전 속성으로 만든 `CREATE SEQUENCE` 문(Oracle `START WITH`는 지금의 `LAST_NUMBER`). Oracle·PostgreSQL·SQL Server 모두 |
| 테이블 정보 | 편집기에서 테이블 이름 위(또는 `스키마.이름` 선택)에서 `F4`, 트리에서 `F4`: 열(형식·NULL·PK·주석)·인덱스·제약 조건(기본 키·고유·외래 키와 참조 테이블·검사)·행 수(통계)·주석 창. 이름만 쓰면 내 스키마 → 동의어 → PUBLIC 동의어 → 다른 스키마 순으로 찾음 |
| 트리 검색 | 서버에서 스키마·객체(·열) 이름을 찾아 일치하는 계층만 보여 줌. Enter·Shift+Enter로 이동, Esc로 지우기 |
| 여러 DB | 여러 DB에 동시에 연결. SQL 탭마다 대상 DB를 정하고, 트리에서 다른 DB의 테이블에 [SELECT 문 넣기](오른쪽 메뉴·Enter)·`F1`을 하면 그 DB용 탭으로 감. 테이블을 두 번 눌러도 SELECT를 넣지 않음 |
| SQL 편집기 | 탭마다 AvalonEdit 편집기: 줄 번호, 주석·문자열·키워드·자료형 강조(탭 대상 DB 문법 — Oracle·PostgreSQL·SQL Server, 대상을 바꾸면 다시 칠함), 여러 줄 Tab·Shift+Tab 들여쓰기·내어쓰기, Enter 들여쓰기 유지. 설정 창 "SQL 편집기" 탭에서 줄 간격·강조 대상과 모양·긴 줄 기준 |
| SQL 실행 | 커서가 있는 문장 또는 선택 영역을 `Ctrl+Enter`로 실행, 취소, 결과는 처음 200행(1,000·5,000 선택)만 가져오고 [다음 행 가져오기]로 이어 읽기(다시 조회하지 않음) |
| 여러 문장 | 문장이 여럿인 선택 영역(`Ctrl+Enter`)이나 스크립트 실행(`F5`, 편집기 전체)은 차례로 실행하고 조회마다 **결과 하위 탭**(결과 1 · 테이블 이름 (행 수))을 만듦. 문장이 하나면 지금처럼 결과 하나. 오류가 나면 그 문장에서 멈춤. 위험한 문장은 시작 전에 확인 |
| 임시 저장 | 쓰던 SQL 탭(대상 DB·커서 위치·파일 포함)을 입력이 멈추고 1.5초 뒤, 탭을 닫거나 창을 닫을 때 자동 저장하고 다음에 창을 열면 되살림(창을 닫을 때는 묻지 않음). 저장하지 않은 변경이 있던 파일 탭은 그 내용으로, 변경이 없던 파일 탭은 파일을 다시 읽어 되살림. 빈 탭·처음 안내문 그대로인 탭은 저장하지 않음 |
| 트랜잭션 | 자동 커밋 끔. DML은 커밋 전까지 "커밋 대기"로 표시하고 [커밋]·[롤백]으로 끝냄. 연결을 끊거나 창을 닫을 때 커밋 대기가 있으면 물어봄 |
| 안전장치 | WHERE 없는 UPDATE·DELETE, DROP·TRUNCATE·ALTER 등은 확인 체크 후 실행. 커밋 대기 중 DDL 실행(자동 커밋됨) 경고. 컴파일 오류가 있는 PL/SQL은 메시지 탭에 오류 목록 표시 |
| 결과 | NULL·숫자 구분, 열 머리에 Oracle 형식, 열 머리를 눌러 정렬(오름차순 → 내림차순 → 해제, 숫자 열은 수로, NULL은 Oracle 기본처럼 오름차순 맨 뒤), TSV 복사(`Ctrl+C`·[복사], 화면 순서), 오른쪽 메뉴 [셀 복사](누른 칸 값 하나)·[INSERT 문 만들기](빠른 조회 결과에서 고른 행의 INSERT 문을 클립보드에, Oracle `TO_DATE`·SQL Server `N''`/`DATETIME2` 등 DB에 맞춘 값), `Ctrl+F` 찾기 팝업(가져온 행의 값에서 찾아 그 행을 고르고 칸이 보이게 세로·가로 스크롤 후 강조), 메시지·실행 기록 |
| 테이블 정의서 (PoC) | 트리에서 테이블을 `Ctrl`·`Shift`+클릭으로 여러 개 고르거나 스키마·DB를 고른 뒤 오른쪽 메뉴(또는 `도구 > 테이블 정의서 만들기…`). 대상 선택 창에서 스키마별로 **정규식**(대소문자 무시·주석도 찾기)이나 여러 줄 선택 + `Space`로 체크하고, 문서 정보(제목·프로젝트·작성자·버전·작성일)를 넣으면 **엑셀 템플릿**에 Velocity 변수를 채운 .xlsx를 만듦. 고친 양식은 **사용자 양식 폴더**(팝업의 [양식 폴더])에 넣으면 목록에 "내 양식"으로 보이고, [복사해서 고치기]로 기본 양식을 꺼내 엑셀에서 고칠 수 있음. LLM으로 양식을 만들 때는 [LLM 지침 복사] — 지침: https://zaruous.github.io/Folderss-oracle-db-helper/table-spec-templates/ . [만들기…] 때 입력한 문서 정보(제목·프로젝트·시스템/모듈·작성자·버전·작성일·설명)와 고른 양식·저장 폴더는 플러그인 설정에 저장해 다음에 창을 열 때 불러옴. 기본 양식은 2종: **기본**(표지 · 목록(시트 링크) · 테이블마다 열·인덱스·제약 조건)과 **명세서형**(워크스코브 테이블 명세서 양식 구성 — 논리명·키 PK/FK/UK/IX·외래키 삭제 규칙·인덱스 유형·공통 코드·개정 이력). |
| GCM 정의서 | `도구 > GCM 정의서 만들기…`(트리에서 대상 DB를 고른 뒤). **공통 코드 테이블을 읽는 SELECT 문을 직접 쓰고**, 결과 열에 `AS` 별칭만 맞추면 됩니다 — 필수 `TABLE_NAME`·`CODE`·`CODE_NAME`, 선택 `GROUP_NAME`·`GROUP_DESC`·`CODE_DESC`·`SORT_NO`·`USE_YN`, 그 밖의 열은 추가 속성(`$code.attrs.열이름`). 플러그인은 SQL을 해석하지 않으므로 환경마다 테이블·열 이름이 달라도 SQL만 바꿉니다. [열 확인]은 SQL을 `ROWNUM <= 1`로 감싸 실행해 별칭을 검사하고, [저장…]으로 SQL을 **SQL 폴더**(`plugin-data\…\code-sql\*.sql`)에 저장하면 목록에 보이며 접속마다 마지막에 쓴 파일을 기억합니다. 그룹 정규식으로 `TABLE_NAME`를 걸러 일부만 넣을 수 있고, 출력은 **엑셀 양식**(기본 2종: 그룹별 시트 · 단일 시트, 내 양식은 `templates\gcm\`) · **JSON**(doc·db·source·groups[].codes[]) · **CSV**(UTF-8 BOM) 중 고릅니다. "코드 테이블마다 열 이름이 다른" 범용 코드 테이블(정의 테이블이 열 이름, 데이터 테이블이 값)은 **이름 열과 값 열을 짝으로 나열**하면 됩니다 — 추가 열 `X`와 `X_NAME`이 함께 있으면 `X_NAME` 값이 속성 이름, `X` 값이 속성 값(`KEY_1_NAME`/`KEY_1`, `DATA_1_NAME`/`DATA_1` …). 평범한 열 나열이라 어떤 DBMS에서나 같은 SQL이고, 그룹마다 실제 열 이름이 속성이 되어 양식의 `$group.attrNames`·`$code.groupAttrValues`로 씁니다. JSON 객체 열(`jsonb_build_object`)도 같은 식으로 펼치지만 DBMS 고유 함수라 선택 사항입니다. 창 머리의 [대상 DB] 콤보로 다른 접속으로 바꿀 수 있고, 트리의 DB 행 오른쪽 메뉴에도 항목이 있습니다. [LLM에게 SQL 요청]은 별칭 계약이 든 요청 글을 복사하고, [본보기 SQL]은 플러그인 본보기(범용 코드 테이블 꼴)를 편집기에 넣습니다. 본보기 전문은 사내 테이블 구조라 코드·DLL·문서에 평문으로 두지 않고 플러그인 안에 잠가 둡니다(`SqlCipherLogic`, 고정 키). 조회가 0행이거나 정규식에 맞는 그룹이 없으면 빈 문서를 만들지 않고 상태 줄에 이유를 보입니다. 양식 지침: https://zaruous.github.io/Folderss-oracle-db-helper/code-spec-templates/ |

| 성능 모니터 | `도구 > 성능 모니터…`(트리에서 대상 DB를 고른 뒤) 또는 트리의 DB 행 오른쪽 메뉴. 모달이 아닌 창(DB마다 하나)에 **기본 정보**(버전·인스턴스·시작 시각·로그 모드·세션 수 …) · **저장 공간**(Oracle 테이블스페이스 크기·사용·남음·사용률·자동 확장 한도, PostgreSQL 테이블스페이스·데이터베이스 크기, SQL Server 데이터·로그 파일 — 표 위 대시보드에 테이블스페이스·파일마다 사용률 도넛 차트, 80%↑ 경고색·90%↑ 위험색. 표의 행이나 도넛을 누르면 아래에 **상세 목록**: Oracle은 데이터 파일·세그먼트(DBA_SEGMENTS, 큰 것부터 200개), PostgreSQL은 그 테이블스페이스의 객체, SQL Server는 그 파일 그룹의 객체) · **로그**(Oracle 리두 로그 그룹, PostgreSQL WAL·아카이브, SQL Server 트랜잭션 로그 사용률·마지막 백업) · **잠금**(Oracle은 **근원 블로커 트리** — 근원 아래에 막힌 세션, 막은 세션 수·유발 대기·쉬는 근원의 유휴 시간·직전 SQL, 최소 대기 필터, [근원 세션 종료…]; [표로 보기]로 잠금 모드 표) · **세션** · **느린 SQL**(누적 소요 상위 50 — Oracle `V$SQLAREA`, PostgreSQL `pg_stat_statements`(확장·13+), SQL Server `sys.dm_exec_query_stats`; 행을 누르면 전문·실행 계획) · **대기 이벤트**(Oracle `V$SYSTEM_EVENT`, SQL Server `sys.dm_os_wait_stats`, PostgreSQL은 이 순간 집계)를 표(정렬·복사)로 보입니다. 창은 **모니터 전용 세션**을 따로 열어 SQL 탭 실행과 서로 기다리지 않습니다(트리 연결 불필요, 저장된 비밀번호가 없으면 한 번 묻기). 잠금·세션 행을 누르면 아래에 **SQL 전문 · 실행 계획**(Oracle `DBMS_XPLAN.DISPLAY_CURSOR`, SQL Server 계획 XML; PostgreSQL은 전문만) · **세션 정보/통계**가 보이고, 오른쪽 메뉴 **[세션 종료…]**(`ALTER SYSTEM KILL SESSION … IMMEDIATE` · `pg_terminate_backend` · `KILL`)는 문장·경고·확인 체크 대화상자를 거쳐 실행합니다(읽기 전용 접속은 막음). Oracle 임시 테이블스페이스는 지금 쓰는 정렬 세그먼트(`V$SORT_SEGMENT`), UNDO는 만료 전 익스텐트만 사용으로 셉니다. 섹션은 처음 고를 때 가져오고 [새로 고침](`F5`)·자동 새로 고침(5·15·60초)은 보고 있는 섹션만 다시 가져옵니다. 계정에 조회 권한(Oracle `SELECT_CATALOG_ROLE`, PostgreSQL `pg_read_all_stats`, SQL Server `VIEW SERVER STATE`)이 없으면 그 섹션만 "권한이 없어 가져오지 못했습니다 — 필요 권한: …"으로 보이고 다른 섹션은 그대로 동작합니다. **전체 DB 상태**(`도구 > 전체 DB 상태…`, 지금은 Oracle): 저장한 접속마다 타일 — 가장 심각한 상태와 그 이유(잠금 대기·공간(최대 기준)·FRA·아카이브·백업·평균 활성 세션/CPU·실패한 예약 작업), 심각도순·고정·빼기, 누르면 그 DB의 성능 모니터(저장된 비밀번호가 있는 접속만 스스로 붙음). 세 DBMS는 공급자의 `IMonitorProvider`(섹션 = 조회 SQL 하나, 결과 열 별칭이 머리글)로 같은 창을 씁니다. 도움말: `help/monitor.html` |
| 일일 점검 | `도구 > 일일 점검…`(또는 전체 DB 상태의 [일일 점검…], 지금은 Oracle). 고른 DB들에 점검 묶음 17개(인스턴스·로그 모드·아카이브 대상·테이블스페이스(최대 기준)·FRA·마지막 백업·실패 백업·경고 로그·로그 스위치·INVALID·UNUSABLE·오래된 통계·실패 작업·DBMS_JOB·오래 실행 세션·잠금 대기·비밀번호 만료)를 돌려 항목마다 정상·주의·위험·건너뜀(권한·조건 — 이유와 필요 권한)·오류, 값, **직전 대비**(↑ 나빠짐 · ↓ 좋아짐)를 보이고, 누르면 판정 기준·조치·발견 행(최대 100행). 결과(상태·값·행 수 — 발견 행은 빼고)는 플러그인 데이터 폴더의 `monitor-history\history.db`(SQLite)에 남고 설정 > 성능 모니터에서 끄거나 보관 기간(기본 90일)을 바꿉니다. **[엑셀로 저장…]**은 요약 + DB별 시트 기본 양식 또는 내 양식(정의서와 같은 템플릿 문법). **[항목 관리…]**에서 기본 항목 켬·끔·임계값, **사용자 점검 항목**(SELECT 한 문장 + 판정 규칙, [시험 실행], JSON 내보내기·가져오기). 도움말: `help/monitor.html#checks` |

## 알아 둘 점

- **PostgreSQL·SQL Server 접속**(Npgsql·Microsoft.Data.SqlClient): 접속 관리의 [DBMS]에서 고르면 서비스명 칸이 데이터베이스가 되고 기본 포트는 5432·1433입니다. 지원 범위는 접속·트리(스키마·테이블·뷰·시퀀스·함수/프로시저·열)·검색·SQL 실행·빠른 조회·GCM 정의서입니다. 테이블 정보(F4)·테이블 정의서는 아직 Oracle 데이터 사전 기반이라 안내만 띄웁니다. 결과 열 형식은 그 DBMS 이름 그대로(`character varying(20)`, `nvarchar(50)`), DDL은 Oracle처럼 실행 전에 자동 커밋합니다. SQL Server는 암호화 Optional·TrustServerCertificate·MARS로 붙습니다(사내 자체 서명 인증서). PostgreSQL은 연결당 결과 커서가 하나라, 조회 결과를 열어 둔 채 다른 문장·조회(GCM 정의서 포함)를 실행하면 이전 결과의 [다음 행 가져오기]가 꺼집니다(가져온 행은 그대로). 이전 버전의 접속 설정은 그대로 Oracle로 읽힙니다.
- **SQL 파일 드래그 드롭**: 탐색기에서 SQL 편집기로 파일을 끌어다 놓으면 그 파일 탭으로 열립니다(이미 연 파일은 그 탭으로). 이후 편집·`Ctrl+S` 저장은 그 파일에 됩니다. GCM 정의서 창의 SQL 편집기에 놓으면 그 .sql을 불러오고 [저장…]이 그 파일을 먼저 보입니다. 창을 닫았다 열면 쓰던 탭(파일 탭 포함)은 임시 저장으로 되살아납니다.
- **DB마다 세션 1개**: 같은 DB를 대상으로 하는 탭들은 트랜잭션을 함께 씁니다(한 탭의 커밋이 다른 탭의 변경도 커밋). 같은 DB에서는 한 번에 한 문장만 실행합니다.
- **읽기 전용은 플러그인 쪽 차단**입니다. 운영 DB는 DB 계정 권한도 읽기 전용으로 두세요.
- **비밀번호**는 이 PC의 이 Windows 사용자만 풀 수 있게 저장합니다. 다른 PC로 옮기면 다시 입력해야 합니다.
- **SQL 파일**은 5MB까지 엽니다. BOM이 없고 UTF-8로 읽히지 않는 파일은 CP949로 읽고, 그대로 CP949로 저장합니다(CP949로 나타낼 수 없는 글자가 생기면 UTF-8로 저장하고 알림). 새 파일은 BOM 없는 UTF-8입니다.
- **정렬은 가져온 행 안에서만** 합니다. 더 가져온 행도 정렬 위치에 들어가지만, 전체 결과의 순서는 `ORDER BY`로 정하세요.
- **임시 저장 위치**: `%LOCALAPPDATA%\Folderss\plugin-data\<플러그인 id>\sql-drafts\`(창마다 파일 하나, 평문). GCM 창의 마지막 SQL과 본보기 SQL은 플러그인 고정 키(AES-GCM)로 잠가 둡니다(비밀 보호가 아니라 노출 방지용). 창을 여러 개 열어도 서로 덮어쓰지 않고, 닫힌 창의 것은 다음에 여는 창이 넘겨받습니다.
- **여러 문장 실행**에서는 문장 안에 빈 줄이 있어도 됩니다: 빈 줄 다음이 `FROM`·`WHERE`·`SET`·`AND`처럼 문장을 잇는 말이면 같은 문장으로 봅니다(`;`·`/` 줄은 언제나 문장 끝). 한 번에 결과는 20개까지 남깁니다.
- **SQL 편집기 설정**(설정 창 "SQL 편집기" 탭): 줄 간격, 구문 강조 대상(키워드·자료형·문자열·주석)마다 칠함·색·굵게·기울임, 더 칠할·칠하지 않을 낱말, 칠하지 않을 긴 줄 기준(기본 20,000자). 저장하면 열린 SQL 탭이 바로 따릅니다.
- **문장 구분**: `;`, 빈 줄, 그리고 DBMS별 구분 줄. SQL 탭은 그 탭 대상 DB의 문법으로 나눕니다(규칙 `SqlLexRules`, 구문 강조도 같은 규칙).
  - Oracle: `/`만 있는 줄. PL/SQL 블록(BEGIN·DECLARE·CREATE PROCEDURE 등)은 `/` 줄로 끝내세요. `q'[…]'` 문자열.
  - PostgreSQL: 함수·DO 본문의 `$$…$$`·`$태그$…$태그$`, `E'…'`, 중첩 블록 주석을 압니다. 블록이 없어 `;`로 끝나고(`BEGIN;`은 트랜잭션 시작) `/` 줄은 구분자가 아닙니다.
  - SQL Server: `GO`만 있는 줄이 배치 구분입니다. CREATE PROCEDURE·BEGIN … END·DECLARE는 `GO` 줄까지 한 덩어리, `BEGIN TRAN`은 보통 문장, `[식별자]`를 압니다. `GO n`(반복)은 지원하지 않습니다.
  - 대상이 없는 탭은 Oracle 규칙입니다.
- **접속 방식**: 호스트·포트·서비스명(EZConnect)만 지원합니다. TNS 별칭과 Wallet(mTLS)은 아직 지원하지 않습니다.
- **확인 상태**: 문장 분석·조회 SQL·세션·트리 로직·템플릿 엔진·엑셀 렌더러·LLM용 양식 지침은 단위 테스트(1,500여 개, Linux·Windows)로, 접속·트리 조회·SQL 실행·트랜잭션·취소·테이블 정의서 조회는 실제 Oracle 12.1(Docker)에 붙는 시험(`tests/MyPlugin.OracleIT`)으로 확인했습니다. Windows 화면은 Folderss 테마를 입힌 시험 창에서 편집기 정렬·SQL 임시 저장·트리 메뉴(다시 연결)·결과 정렬·메뉴 막대·SQL 파일 열기/저장·여러 문장 결과·빠른 조회(F1)·테이블 정보(F4)를, 테이블 정의서(트리 여러 줄 선택·오른쪽 메뉴·도구 메뉴·대상 선택 팝업·내 양식·만들기)는 Folderss 본체 안에서 확인했습니다. 전체 DB 상태·일일 점검(실행·기록·직전 대비·엑셀·항목 관리·설정 탭)은 플러그인만 띄우는 헤드리스 화면 시험(`tests/MyPlugin.UiTests`, 아래 "화면 시험")으로 실제 Oracle 11.2 XE에 대고 확인합니다.
- **취소**: [취소]는 in-band break로 보냅니다. 기본값(OOB, TCP 긴급 데이터)은 Docker Desktop·일부 방화벽·NAT를 지나지 못해 서버가 취소를 받지 못합니다.

### 테이블 정의서 템플릿

템플릿은 엑셀 파일(.xlsx)입니다. 기본 양식은 코드가 만듭니다(`Doc/TableSpecTemplate.cs` 기본, `Doc/TableSpecSheetTemplate.cs` 명세서형, 목록은 `Doc/TableSpecTemplates.cs`). 대상 선택 창의 [복사해서 고치기]로 **사용자 양식 폴더**(`%LOCALAPPDATA%\Folderss\plugin-data\zaruous.folderss-db-helper\templates\`)에 꺼내 엑셀에서 고치면 [양식] 목록에 "내 양식"으로 보입니다. 다른 위치의 파일은 [템플릿 파일]로 고릅니다.

**LLM으로 양식 만들기**: 작성 지침을 GitHub Pages에 올려 두었습니다 — [사람용](https://zaruous.github.io/Folderss-oracle-db-helper/table-spec-templates/) · [Markdown 원문](https://zaruous.github.io/Folderss-oracle-db-helper/table-spec-templates/guide.md) · [variables.json](https://zaruous.github.io/Folderss-oracle-db-helper/table-spec-templates/variables.json) · [llms.txt](https://zaruous.github.io/Folderss-oracle-db-helper/llms.txt). 팝업의 [LLM 지침 복사]를 누르면 지침 주소와 양식 폴더가 든 요청 글이 복사되니, 원하는 모양을 덧붙여 LLM(Claude Code 등)에게 보내면 됩니다. LLM은 지침의 `check_template.py`로 결과를 스스로 검사합니다. 지침 원본은 `docs/table-spec-templates/`(바꾸면 `gh-pages` 브랜치에 다시 올림). 쓸 수 있는 변수와 문법은 템플릿의 `_변수 설명` 시트에 있습니다(이름이 `_`로 시작하는 시트는 결과에서 빠짐).

| 쓰는 곳 | 문법 | 예 |
|---|---|---|
| 셀 안 | `${…}`·`$…` 참조, `$!{…}`(값이 없으면 빈 칸). 셀 전체가 참조 하나면 숫자는 숫자 셀로 | `${table.name}`, `${col.no}` |
| 셀 안 | `#if(식)…#{else}…#end`, `#set($x = 식)` | `#if($col.isFk)→ ${col.fkRef}#end` |
| 줄 지시문 | 한 줄에 그 셀 하나만 있으면 줄 단위로 동작하고 결과에서는 지워짐: `#foreach($x in $목록)` ~ `#end`(사이 줄을 항목마다 되풀이), `#if`·`#elseif`·`#else`·`#end`, `#set` | `#foreach($col in $table.columns)` |
| 시트 지시문 | 시트 맨 위 `#sheet($table in $tables)`: 항목마다 시트를 복사. 탭 이름도 템플릿 | 탭 이름 `${table.sheetName}` |
| 수식 | `${`가 든 수식만 채움(`$A$1` 같은 절대 참조는 그대로) | `=HYPERLINK("#'${t.sheetName}'!A1","${t.name}")` |

변수: `$doc`(title·project·system·author·version·date·schemas·tableCount), `$db`(name·user·address·version), `$tables` → `$table`(owner·name·logicalName·description·comment·type·numRows·tablespace·pkName·pkColumns·columns·indexes·constraints·foreignKeys·checks…) → `$col`(no·name·logicalName·description·dataType·length·typeLabel·nullable·key·pk·fk·fkRef·default), `$idx`(name·columns·kind·unique·type·status), `$con`(name·typeLabel·columns·target·deleteRule·condition·status), `$codes` → `$code`(group·code·usage), `$foreach.count`.

논리명은 Oracle에 따로 없어 **주석을 나눠** 씁니다: 여러 줄이면 첫 줄, 한 줄이면 첫 `:`·`|`·` - ` 앞, `이름(설명)`이면 이름 — 나머지가 설명.

- Velocity 문법 일부만 직접 구현했습니다(`#macro`·`#parse`·인자 있는 메서드 호출은 없음). 엑셀은 ClosedXML로 읽고 씁니다.
- 데이터는 스키마마다 이름 500개씩 묶어 4번(요약·열·인덱스·제약 조건) 조회합니다. 기본값(`DATA_DEFAULT`)·검사 조건도 읽습니다.
- 조건부 서식·데이터 유효성·그림은 줄을 되풀이해도 따라 움직이지 않습니다.

### GCM 정의서 (별칭 계약)

GCM(공통 코드) 정의서는 테이블 정의서와 같은 엔진(Velocity 엑셀 템플릿)을 쓰되, 데이터는 **사용자 SQL**에서 옵니다. 플러그인이 아는 것은 결과 열의 별칭뿐입니다.

| 별칭 | 필수 | 뜻 |
|---|---|---|
| `TABLE_NAME` | 필수 | 코드 그룹(코드 테이블) 식별자. 옛 별칭 `GROUP_ID`도 받음 |
| `CODE` | 필수 | 코드 값 |
| `CODE_NAME` | 필수 | 코드 명칭 |
| `GROUP_NAME` · `GROUP_DESC` | 선택 | 그룹 명칭·설명(행마다 반복되면 첫 값) |
| `CODE_DESC` | 선택 | 코드 설명 |
| `SORT_NO` | 선택 | 그룹 안 정렬(숫자로 읽히면 수로). 없으면 SQL 순서 |
| `USE_YN` | 선택 | 사용 여부(Y/N·1/0·true/false, 비우면 사용) |
| 그 밖의 열 | — | 추가 속성: `${code.attrs.열이름}`, `${code.attrText}`("이름=값; …"), JSON의 `attrs`, CSV의 뒤쪽 열 |

변수: `$doc`(title·project·system·author·version·date·description·groupCount·codeCount·generatedAt), `$db`(name·user·address·version), `$source`(name·sql·fetchedAt·rowCount·columns·attrNames), `$groups` → `$group`(no·id·name·description·sheetName·codeCount·codes) → `$code`(no·group·groupName·code·name·description·sortNo·useYn·isUsed·attrs·attrValues·attrText), `$codes`(모든 코드를 편 목록). 지침·변수 목록은 `docs/code-spec-templates/`(사람용: https://zaruous.github.io/Folderss-oracle-db-helper/code-spec-templates/ ).

- 본보기 SQL이 처음 열 때 들어 있습니다. `FROM`·열 이름만 환경에 맞게 바꾸세요. 바인드 변수는 받지 않습니다(값을 직접 적습니다).
- 조회는 모든 행을 메모리에 올립니다. 그룹이 아주 많으면 그룹별 시트 양식 대신 단일 시트 양식이나 그룹 정규식을 쓰세요.

### 실제 Oracle로 시험

`ORACLE_IT_DSN`이 없으면 모든 시험을 건너뜁니다(CI는 단위 테스트만 실행). SYSTEM 계정으로 시험 스키마(`DBH_IT`)를 만들고 끝나면 지웁니다.

```powershell
docker start oracle-12c            # 예: truevoly/oracle-12c (system/oracle, 서비스 xe)
$env:ORACLE_IT_DSN = "localhost:1521/xe"
$env:ORACLE_IT_SYS_PW = "oracle"   # 기본값 oracle
dotnet test tests/MyPlugin.OracleIT
```

측정값(연결·조회 시간, 사용자에게 보이는 오류 문장 등)은 `tests/MyPlugin.OracleIT/bin/Debug/net8.0/oracle-it-report.txt`에 남습니다.

### 미리보기 (플러그인만 띄우기)

Folderss에 설치하지 않고 플러그인 창만 띄워 직접 써 봅니다(`tools/PluginPreview`). publish한 zip을 Folderss와 같은 로드 컨텍스트로 불러 `CreateView()`를 창에 담고, Folderss 설정 창의 플러그인 탭(SQL 편집기 등)은 창 위 **[플러그인 설정…]**에서 엽니다.
설정·접속·임시 저장은 작업 폴더(기본 `%TEMP%\folderss-preview`)의 `plugin-data`에 남아 다시 띄워도 이어지고, 사용 중인 Folderss의 데이터는 건드리지 않습니다. 테마를 주지 않으면 Folderss에서 쓰는 테마를 따릅니다.

```powershell
pwsh scripts/preview.ps1                  # publish → 빌드 → 미리보기 창
pwsh scripts/preview.ps1 -Theme Light     # 테마: Black·Light·GitHub·Nord·Solarized·Dracula·Catppuccin
pwsh scripts/preview.ps1 -NoPublish       # 이미 publish한 zip으로
pwsh scripts/preview.ps1 -Fresh           # 설정·접속·임시 저장을 지우고 처음 상태로
```

### 화면 시험 (헤드리스)

`tests/MyPlugin.UiTests`는 Folderss 없이 **플러그인만** 띄워 실제 화면을 조작합니다. publish한 zip을 풀어 Folderss와 같은 로드 컨텍스트(`.deps.json`으로 DLL·네이티브 DLL을 찾음)로 불러, 시험용 `IPluginManager`(설정은 데이터 폴더의 settings.json, 접속은 DPAPI로 넣음)를 주고 `CreateView()`를 창에 담습니다. WPF는 STA 스레드 하나에 Folderss 테마(색 사전 7종 + AvalonDock을 뺀 `Controls.xaml` 사본, 기본 Black)를 입히고 소프트웨어 렌더링으로 그립니다.

- `scripts/ui-test.ps1`이 **숨은 데스크톱**(CreateDesktop — 화면 전환 없음)에서 `dotnet test`를 돌립니다. 사용자의 마우스·키보드·포커스·창을 건드리지 않고, 도는 동안 사용자 데스크톱에 시험 창이 하나라도 뜨면 실패로 알립니다. 숨은 데스크톱이 아니면 시험은 건너뜁니다.
- 화면은 자동화 피어(Invoke)·WPF 트리·AutomationId로 찾고 누릅니다. 모달 창을 여는 버튼은 `UiThread.Post`로 누르고, Windows 파일 대화상자는 Win32(UI 스레드의 #32770 창, WM_SETTEXT·IDOK)로 채웁니다. 캡처는 `RenderTargetBitmap`(실행 폴더의 shots).
- 실제 Oracle이 필요한 시험은 `FMON_UI_ORACLE`(host:port/service)이 있을 때만 — 계정은 `FMON_UI_ORACLE_USER`·`FMON_UI_ORACLE_PW`(SELECT_CATALOG_ROLE).
- SQL 탭 편집기(`SqlEditorHeadlessTests`): 로드·키워드 색(탭 대상 DB별)·설정 "SQL 편집기" 탭(미리 보기·저장 뒤 반영)·성능(데이터 크기·종류별로 증분 강조·전체 다시 계산·강조 없음·SQL 탭·옛 TextBox 비교)·키·테마 7종,
  회귀 확인(`SqlEditorRegressionTests` — 탭 전환 상태 유지, 되돌리기 한 번, 찾기·스크롤, 파일 열기·저장 줄 끝·BOM, 임시 저장 되살리기, 편집 메뉴, Oracle 실행·F4)을 재서 shots에 `poc-measure.md`·`poc-keys.md`·`poc-themes.md`와 캡처를 남깁니다.
  Ctrl·Shift는 UI 스레드의 키 상태(`SetKeyboardState`)로 흉내 냅니다 — 키 입력을 보내지 않으므로 다른 창에는 영향이 없습니다. 결과와 판단은 [`docs/sql-editor-avalonedit-plan.md`](docs/sql-editor-avalonedit-plan.md) 7·8절.

```powershell
$env:FMON_UI_ORACLE = "localhost:1521/xe"
pwsh scripts/ui-test.ps1                 # publish → 빌드 → 숨은 데스크톱에서 시험
pwsh scripts/ui-test.ps1 -NoPublish -Filter "FullyQualifiedName~CheckHeadlessTests"
```

---

# 플러그인 개발 안내

이 저장소는 Folderss 플러그인 템플릿에서 시작했습니다. 아래는 그 템플릿의 개발 안내입니다(프로젝트·클래스 이름은 아직 템플릿 이름 `MyPlugin`).

[Folderss](https://github.com/zaruous/Folderss) 파일 관리자의 플러그인을 바로 만들 수 있는 기본 틀입니다.
이 저장소를 복사해 이름만 바꾸고 `dotnet publish`하면, Folderss에 등록할 수 있는 플러그인 zip이 만들어집니다.

```
결과: src/MyPlugin/bin/Release/net8.0-windows/win-x64/MyPlugin.zip
├── plugin.json         ← 메뉴 이름, 진입점 정보
├── MyPlugin.dll        ← 플러그인 코드
├── MyPlugin.deps.json  ← 의존성 목록 (Folderss가 이걸로 DLL을 찾음)
└── (NuGet 패키지 DLL)  ← PackageReference로 추가한 라이브러리
```

Folderss에서 `⋯ 메뉴 > 플러그인 > My Plugin`을 누르면 플러그인 화면이 팝업 창으로 뜹니다.

---

## 요구 사항

| 항목 | 내용 |
|---|---|
| OS | Windows 10/11 (실행). 컴파일은 Linux·macOS에서도 됩니다 |
| SDK | [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) |
| Folderss | 플러그인 기능이 들어간 버전 (계약 1.0.0) |
| IDE (선택) | Visual Studio 2022 또는 VS Code + C# Dev Kit |

---

## 빠른 시작 (5분)

### 1. 저장소 복사

GitHub에서 **Use this template**를 누르거나, 직접 복제합니다.

```powershell
git clone https://github.com/zaruous/Folderss-oracle-db-helper.git MyFolderssPlugin
cd MyFolderssPlugin
```

### 2. 이름 바꾸기

처음에는 **`plugin.json`의 `id`와 `name`만** 바꿔도 동작합니다.

```json
{
  "id": "yourname.my-plugin",
  "name": "My Plugin",
  ...
}
```

| 키 | 바꿀 값 | 규칙 |
|---|---|---|
| `id` | 예: `hong.order-form` | 영문·숫자로 시작, 영문·숫자·`.`·`_`·`-`만, 최대 64자. 다른 플러그인과 겹치면 안 되고, **배포한 뒤에는 바꾸지 마세요**(설정이 이어지지 않음) |
| `name` | 예: `주문서` | `⋯ 메뉴 > 플러그인`에 보이는 메뉴 이름이자 팝업 창 제목 |

프로젝트·클래스 이름까지 바꾸려면 [이름 전체 바꾸기](#이름-전체-바꾸기)를 보세요.

### 3. 빌드

```powershell
dotnet publish src/MyPlugin
```

마지막 줄 근처에 zip 경로가 나옵니다. `dotnet build`만 해서는 zip이 만들어지지 않습니다.

```
Folderss 플러그인: bin/Release/net8.0-windows/win-x64/MyPlugin.zip
```

### 4. Folderss에 등록하고 실행

1. Folderss에서 `⋯ 메뉴 > 설정 > 플러그인 > 플러그인 찾기…`를 누릅니다.
2. 3단계의 `MyPlugin.zip`을 고르고, 권한 경고를 확인한 뒤 `예`를 누릅니다.
3. `⋯ 메뉴 > 플러그인 > My Plugin`을 누르면 "My Plugin" 타이틀이 있는 팝업이 뜹니다.

### 5. 코드를 고친 뒤

1. `dotnet publish src/MyPlugin`
2. `플러그인 찾기…`로 같은 zip을 다시 등록합니다(같은 `id`라 교체됩니다).
3. **Folderss를 재시작합니다.** 한 번 로드한 플러그인 DLL은 종료할 때까지 내릴 수 없어서, 재시작해야 새 버전이 로드됩니다.

---

## 폴더 구조

```
.
├── FolderssPluginTemplate.sln
├── LICENSE                      ← MIT
├── .github/workflows/build.yml  ← 빌드·릴리스 자동화
├── src/
│   └── MyPlugin/
│       ├── MyPlugin.csproj      ← publish하면 zip까지 만든다 (PackPlugin 타깃)
│       ├── plugin.json          ← 플러그인 정보
│       └── MyPlugin.cs          ← 진입점 (IFolderssPlugin 구현)
└── contract/
    └── Folderss.PluginContract/ ← Folderss 계약(인터페이스) 사본. 컴파일용이며 zip에 넣지 않음
        ├── Folderss.PluginContract.csproj
        └── PluginContracts.cs
```

**여러분이 고칠 곳은 `src/MyPlugin/`뿐입니다.** `contract/`는 Folderss가 계약을 갱신할 때만 교체합니다([계약 사본 관리](#계약-사본-관리)).

---

## 플러그인이 동작하는 방식

| 시점 | 일어나는 일 |
|---|---|
| 등록 | Folderss가 `plugin.json`을 검사하고 zip을 `%LOCALAPPDATA%\Folderss\plugins\<id>.zip`으로 복사합니다. 코드는 실행되지 않습니다 |
| `⋯ 메뉴 > 플러그인` 열기 | `plugin.json`의 `name`으로 메뉴를 만듭니다. 코드는 실행되지 않습니다 |
| **처음 실행** | zip 해제 → DLL 로드 → `Initialize(manager)` → `CreateView()` → 팝업 |
| 다시 실행 | `CreateView()` → 새 팝업 (창이 하나 더 뜹니다) |

```csharp
public sealed class MyPlugin : IFolderssPlugin
{
    public void Initialize(IPluginManager manager) { ... }   // 처음 한 번
    public FrameworkElement CreateView() { ... }             // 실행할 때마다, 매번 새 요소를 돌려줌
}
```

팝업 창은 Folderss가 만듭니다. 제목은 `name`, 크기는 900×600이고 비모달입니다. 배경·글자색·글꼴은 현재 테마를 따릅니다.

---

## 자주 쓰는 기능

`Initialize`에서 받은 `IPluginManager`로 본체 기능을 씁니다.

### 플러그인 설정 저장·읽기

```csharp
_manager.SetSetting("lastOrderNo", "A-0001");      // 바로 파일에 저장. 실패하면 예외
var last = _manager.GetSetting("lastOrderNo");      // 없으면 null
```

저장 위치는 `%LOCALAPPDATA%\Folderss\plugin-data\<id>\settings.json`입니다. 파일을 직접 쓰려면 `_manager.DataDirectory` 폴더를 쓰세요.

### 본체 설정 읽기 (읽기 전용)

```csharp
string theme;
_manager.GetAppSettings().TryGetValue("theme", out theme);   // "Black", "Light", ...
```

키 목록: `theme`, `git.*`, `diff.*`, `console.*`. 전체 목록은 `contract/Folderss.PluginContract/PluginContracts.cs`의 `GetAppSettings` 주석에 있습니다. 다른 플러그인의 설정은 들어 있지 않습니다.

### 폴더 패널 넣기

```csharp
var panel = _manager.CreateFolderPanel(@"C:\work");
root.Children.Add(panel.View);
panel.PathChanged += (s, e) => title.Text = panel.CurrentPath;
```

파일을 더블클릭하면 Folderss 메인 창의 뷰어 탭에서 열립니다.

### 설정 창에 탭 추가

`src/MyPlugin/MySettingsPage.cs`를 새로 만듭니다.

```csharp
using Folderss.Plugins;
using System.Windows;
using System.Windows.Controls;

namespace MyPlugin
{
    public sealed class MySettingsPage : IPluginSettingsPage
    {
        private readonly IPluginManager _manager;
        private TextBox _greeting;

        public MySettingsPage(IPluginManager manager) { _manager = manager; }

        public string Title { get { return "My Plugin"; } }

        public FrameworkElement CreateView()          // 설정 창을 열 때마다 새로 만든다
        {
            _greeting = new TextBox { Text = _manager.GetSetting("greeting") ?? "" };
            return _greeting;
        }

        public void Save()                            // 설정 창 [저장]을 누를 때
        {
            _manager.SetSetting("greeting", _greeting.Text);
        }
    }
}
```

그런 다음 두 곳을 고칩니다.
- `MyPlugin.cs`의 `Initialize`에 있는 `manager.AddSettingsPage(...)` 주석을 풉니다.
- `plugin.json`의 `"hasSettings"`를 `true`로 바꿉니다. 그러면 플러그인을 실행하기 전에도 설정 창에 안내 탭이 보입니다.

설정 탭은 **플러그인을 한 번 실행한 뒤** 설정 창을 열어야 나타납니다. 플러그인은 사용자가 메뉴에서 실행할 때만 로드되기 때문입니다.

### NuGet 패키지 쓰기

`PackageReference`만 추가하면 됩니다. publish할 때 패키지 DLL과 `.deps.json`이 zip에 함께 들어갑니다.

```powershell
dotnet add src/MyPlugin package Oracle.ManagedDataAccess.Core
```

- 플러그인마다 따로 로드되므로, Folderss나 다른 플러그인이 쓰는 같은 라이브러리의 버전과 관계없이 동작합니다.
- 패키지는 **.NET 8(`net8.0`) 이하를 지원**해야 합니다. Folderss가 .NET 8에서 실행되기 때문입니다.
- `csproj`의 `RuntimeIdentifier`(`win-x64`)는 지우지 마세요. 패키지의 Windows용 DLL(`runtimes/win/...`)이 이 설정으로 골라집니다.
- 패키지의 형식(예: `OracleConnection`)은 플러그인 안에서만 쓰세요. 다른 플러그인과 같은 형식으로 취급되지 않습니다.

### 테마 색 맞추기

색을 고정값으로 넣지 말고 Folderss 테마 리소스 키에 연결하세요.

```csharp
grid.SetResourceReference(Control.BackgroundProperty, "PanelBackground");
grid.SetResourceReference(Control.ForegroundProperty, "PrimaryText");
```

| 용도 | 키 |
|---|---|
| 배경 | `WindowBackground`, `PanelBackground`, `SurfaceBackground`, `ControlBackground` |
| 글자 | `PrimaryText`, `SecondaryText`, `DisabledTextBrush` |
| 테두리·강조·선택 | `BorderBrush`, `AccentBrush`, `AccentHoverBrush`, `SelectionBrush`, `RowHoverBrush` |
| 마우스 상태 | `ControlHoverBrush`, `ControlPressedBrush` |
| 글꼴 | `AppFontFamily` |

`DataGrid`는 Folderss 테마에 스타일이 없어서, 연결하지 않으면 어두운 테마에서도 흰 배경으로 나옵니다.

---

## 꼭 지켜야 할 것

플러그인은 Folderss와 **같은 프로세스**에서 실행됩니다. 플러그인이 잘못하면 Folderss 전체가 종료될 수 있습니다.

| 하지 마세요 | 이유 / 대신 |
|---|---|
| `Task.Run`, `Thread`, 타이머 안에서 예외를 던진 채 두기 | **Folderss가 종료됩니다.** 반드시 `try/catch`로 감싸세요. `async void` 이벤트 처리기도 마찬가지입니다 |
| `Application.Current.Shutdown()`, `Environment.Exit()` | Folderss가 종료됩니다(막을 수 없음). 원인은 로그에 남고, 다음 시작 때 사용자에게 알려집니다 |
| `CreateView()`에서 같은 요소를 다시 돌려주기 | WPF 예외가 납니다. 매번 새로 만드세요 |
| `PluginDirectory`(압축을 푼 폴더)에 파일 쓰기 | zip을 교체하면 새 폴더에 풀려서 쓴 파일이 사라집니다. 쓰기는 `DataDirectory`에 하세요 |
| Folderss 본체 DLL(`Folderss.exe`) 참조 | 본체가 바뀌면 깨집니다. `Folderss.Plugins` 인터페이스만 쓰세요 |

UI 스레드에서 난 예외와 `Initialize`/`CreateView`의 예외는 Folderss가 잡아서 메시지로 보여 주고 계속 실행합니다.
팝업을 코드로 닫으려면 `Window.GetWindow(view)?.Close()`를 쓰세요.

플러그인은 사용자 권한으로 파일을 읽고 쓰고 프로그램을 실행할 수 있습니다. 사용자는 등록할 때 이 경고를 봅니다. 필요한 일만 하도록 만들어 주세요.

---

## 이름 전체 바꾸기

`MyPlugin` 대신 원하는 이름(예: `OrderForm`)을 쓰려면 아래를 모두 바꿉니다. **하나라도 어긋나면 실행할 때 "플러그인을 열지 못했습니다"가 뜹니다.**

| 바꿀 곳 | 예 |
|---|---|
| 폴더 `src/MyPlugin/` → `src/OrderForm/` | |
| 파일 `MyPlugin.csproj` → `OrderForm.csproj` | 어셈블리 이름(DLL·zip 이름)이 이 파일 이름을 따릅니다 |
| `FolderssPluginTemplate.sln`의 프로젝트 경로 | `dotnet sln remove src/MyPlugin/MyPlugin.csproj` 후 `dotnet sln add src/OrderForm/OrderForm.csproj` |
| `MyPlugin.cs`의 `namespace MyPlugin`, `class MyPlugin` | `namespace OrderForm`, `class OrderFormPlugin` |
| `plugin.json`의 `"assembly"` | `"OrderForm.dll"` |
| `plugin.json`의 `"type"` | `"OrderForm.OrderFormPlugin"` (네임스페이스.클래스) |

---

## 계약 사본 관리

`contract/Folderss.PluginContract/PluginContracts.cs`는 Folderss 저장소의
[`Folderss.PluginContract/PluginContracts.cs`](https://github.com/zaruous/Folderss/blob/master/Folderss.PluginContract/PluginContracts.cs) 사본입니다.

- **컴파일용**입니다. 실행할 때는 Folderss에 들어 있는 같은 이름의 DLL이 쓰이므로 zip에 넣지 않습니다(`Private="false"`).
- 어셈블리 이름(`Folderss.PluginContract`), 네임스페이스(`Folderss.Plugins`), 형식 이름은 **절대 바꾸지 마세요.** 바꾸면 Folderss가 플러그인을 인식하지 못합니다.
- **Folderss가 계약을 갱신하면** 이 파일을 원본으로 교체하고 `.csproj`의 `<Version>`도 맞추세요.
  - 사본이 사용자의 Folderss보다 **새 버전**이면, 새 기능을 쓰는 플러그인은 구버전 Folderss에서 로드되지 않거나 `MissingMethodException`이 납니다.
  - 사본이 **구버전**이면 문제없습니다. 새 기능만 못 쓸 뿐입니다.

사본 대신 설치된 Folderss의 DLL을 직접 참조해도 됩니다. `MyPlugin.csproj`의 `ProjectReference`를 아래로 바꾸세요(경로는 설치 위치에 맞게).

```xml
<ItemGroup>
  <Reference Include="Folderss.PluginContract">
    <HintPath>C:\Program Files\Folderss\Folderss.PluginContract.dll</HintPath>
    <Private>false</Private>
  </Reference>
</ItemGroup>
```

---

## 릴리스 (GitHub Actions)

`.github/workflows/build.yml`이 빌드와 배포를 자동으로 합니다.

| 언제 | 하는 일 |
|---|---|
| `main` 푸시, PR | publish하고 플러그인 zip을 Actions 실행 결과의 **Artifacts**에 올린다 (확인용) |
| `v*` 태그 푸시 | `plugin.json`의 `version`과 태그가 같은지 확인한 뒤, **GitHub 릴리스**를 만들고 `<id>-<version>.zip`을 첨부한다 |

릴리스 순서:

```powershell
# 1. plugin.json의 "version"을 올린다 (예: 1.1.0) → 커밋·푸시
# 2. 같은 버전으로 태그
git tag v1.1.0
git push origin v1.1.0
```

- 태그와 `version`이 다르면 빌드가 실패하고 릴리스는 만들어지지 않습니다. `plugin.json`을 고친 뒤 태그를 다시 붙이세요.
- 플러그인 폴더 이름을 바꿔도(`src/<이름>/`) 그대로 동작합니다. 다만 `src` 아래 `plugin.json`은 하나만 있어야 합니다.
- 빌드는 Ubuntu 러너에서 합니다. WPF 플러그인이지만 `EnableWindowsTargeting` 설정 덕분에 컴파일은 Linux에서도 됩니다. 실행은 Windows에서만 됩니다.

---

## 디버깅

1. `dotnet publish src/MyPlugin -c Debug`로 만든 zip(`bin/Debug/net8.0-windows/win-x64/MyPlugin.zip`)을 등록합니다.
2. Visual Studio에서 **디버그 > 프로세스에 연결 > `Folderss.exe`** 를 고릅니다.
3. `⋯ 메뉴 > 플러그인`에서 실행하면 중단점에 걸립니다. 심볼이 안 잡히면 `MyPlugin.csproj`의 `PluginFiles`에서 `.pdb`를 빼는 조건을 지우세요.

로그와 파일 위치(`%LOCALAPPDATA%\Folderss\` 기준):

| 경로 | 내용 |
|---|---|
| `plugin-log.txt` | 플러그인 오류, 비정상 종료 기록 |
| `plugins\<id>.zip` | 등록된 플러그인 |
| `plugins\extracted\<id>-<해시>\` | 압축을 푼 폴더 |
| `plugin-data\<id>\` | 플러그인 설정·데이터 |

---

## 문제 해결

| 증상 | 확인할 것 |
|---|---|
| 등록할 때 "플러그인 파일이 아닙니다" | zip 루트에 `plugin.json`이 있는지, JSON 형식, `assembly`의 DLL이 zip에 있는지 |
| "플러그인을 열지 못했습니다 … TypeLoadException" | `plugin.json`의 `type`이 `네임스페이스.클래스`와 정확히 같은지 |
| "… IFolderssPlugin을 구현하지 않습니다" | 클래스가 `IFolderssPlugin`을 구현하는지, 계약 사본의 어셈블리 이름·버전을 바꾸지 않았는지 |
| 메뉴에 플러그인이 안 보임 | 설정 > 플러그인 목록. "읽지 못한 zip n개"가 있으면 툴팁에 이유가 있음 |
| 고친 코드가 반영되지 않음 | zip을 다시 등록하고 **Folderss를 재시작**했는지 |
| 어두운 테마에서 일부 컨트롤만 흰색 | 테마 리소스 키를 연결했는지 ([테마 색 맞추기](#테마-색-맞추기)) |

---

## 더 읽을거리

- [Folderss 플러그인 개발 가이드](https://github.com/zaruous/Folderss/blob/master/docs/plugin-development.md): plugin.json 전체, API 표, 오류 처리 범위
- [튜토리얼: 주문서 플러그인 만들기](https://github.com/zaruous/Folderss/blob/master/docs/plugin-tutorial-order-form.md): 타이틀 + 그리드 화면을 단계별로
- [예제: HelloPlugin](https://github.com/zaruous/Folderss/tree/master/samples/HelloPlugin): 폴더 패널, 본체 설정, 설정 탭

---

## 라이선스

[MIT](LICENSE)

이 템플릿으로 만든 플러그인은 원하는 라이선스로 배포해도 됩니다. 새 저장소를 만들면 `LICENSE`의 저작권자(`Copyright (c) 2026 zaruous`)를 본인으로 바꾸거나, 다른 라이선스로 교체하세요.

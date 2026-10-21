# 다음 작업

> 2026-10-22 기준(v1.10.0 릴리스). 들어간 것: 성능 모니터 DBA 도구 8-1 잠금 트리 · 8-2 전체 DB 상태 · 8-3 일일 점검(PR #21),
> 끊긴 연결에서 실행하면 다시 연결 확인(PR #22), SQL 편집기 AvalonEdit — DBMS별 문법·키워드·증분 강조·설정 "SQL 편집기" 탭(PR #23~#26),
> 미리보기 `scripts/preview.ps1`(PR #28).
> 설계는 [`docs/monitor-dba-design.md`](../monitor-dba-design.md)·[`docs/sql-editor-avalonedit-plan.md`](../sql-editor-avalonedit-plan.md), 큰 목록은 [`TODO.md`](TODO.md).
> 이 문서는 바로 이어서 할 일과 그 준비물만 적는다.

## 1. 8-4 활성 세션 이력 (라이선스 없는 Top Activity) — 다음 큰 단계

- 설계 4장, 결정 D2: 기본은 **모니터 창이 열린 동안** 표본, 설정에서 접속별 **"상시 표본"**(DB Helper가 열린 동안).
  표본 엔진은 창과 떨어진 서비스로 두고 창은 그 버퍼를 본다(창을 열면 그 전 표본부터).
- 완료 조건(설계 9장): `SampleBuffer`·`SampleLogic` 단위 테스트(구간 집계 · 분류 몫 · Top N · 메모리 상한 · STATE 판정).
  실측: 표본 1회 소요(세션 100개), 수집 부하 표시, 일부러 만든 부하(CPU · 잠금 · I/O)가 차트 분류로 나뉘는지.
- 정할 것: 분 단위 요약을 history.db에 남길지(스키마 2 — `snapshot`·`metric`), SQL_ID를 파일에 남겨도 되는지(D2의 미결).
- 화면 시험은 헤드리스로(아래 4). 부하는 XE에서 PL/SQL 루프(CPU) · 잠금 연쇄(8-1 때 쓴 `lock_test`) · 큰 테이블 전체 읽기(I/O).

## 2. 8-3 남은 것 (작게 나눠 할 수 있음)

| 항목 | 내용 | 비고 |
|---|---|---|
| DB별 덮어쓰기 화면 | 저장소(`check_override.db_key`)는 DB별을 받는데 [항목 관리…]는 전체('')만 고친다 | 점검 창에서 DB를 고른 채 [항목 관리…] → "이 DB에만" 칸 |
| 전체 DB 타일 임계값 | 타일 판정(`OverviewLogic.DefaultThresholds`)은 아직 고정값 | `check_override`와 같은 방식의 표를 더할지, 점검 항목 문턱을 함께 쓸지 결정 |
| 19c 실측 | 점검 묶음 17개를 19c(oracle19c 컨테이너)에서 — 12.2부터 `V$DIAG_ALERT_EXT`가 SELECT_CATALOG_ROLE로 보이는지(경고 로그가 건너뜀이 아니게 되는지) | 아래 5의 계정 만들기 |
| Data Guard 지연 | `V$DATAGUARD_STATS` 항목 — 스탠바이일 때만(필요 조건) | 스탠바이 실측 환경이 없음 |
| 예약 실행 | 지금은 창을 열고 [점검 실행]을 눌러야 돈다 | 창이 닫혀 있을 때의 감시는 하지 않는 원칙(설계 8장) — "매일 처음 열 때 자동 실행" 정도로 |
| 내 양식 목록 | 지금은 파일 하나(`monitor.check.template`) | 정의서처럼 데이터 폴더 `templates`의 목록으로 바꿀지 |

## 3. 8-5 PostgreSQL · SQL Server

- 같은 계약(`IMonitorProvider.LockTree` · `Overview` · `CheckSet`)을 채우면 화면은 그대로 돈다. 공급자마다 `null`을 돌려주는 곳이 시작점.
- 일일 점검 후보 — PostgreSQL: 복제 지연 · `pg_stat_archiver` 실패 · 오래 열린 트랜잭션 · XID 나이(wraparound) · 테이블 bloat 추정 · `pg_stat_statements` 유무.
  SQL Server: 마지막 전체/로그 백업(msdb) · 에이전트 작업 실패 · 로그 재사용 대기 · DBCC CHECKDB 경과 · 자동 확장 남은 공간.
- 실측 DB: PostgreSQL 16·SQL Server 2022/2008 컨테이너(PR #20 때 쓴 것 — 지금은 지움). 다시 만들 때 포트 충돌 주의(SQL Server는 사용자 컨테이너 `mssql-agent-test`가 1433을 쓴다).

## 4. 헤드리스 화면 시험 호스트 보강

- 지금 시험(`tests/MyPlugin.UiTests`, 18개): 전체 DB 상태 1개, 일일 점검 3개, SQL 탭 편집기 7개(로드·탭 대상별 키워드 색·성능·키·테마 7종·설정·둘러보기 캡처)와
  회귀 확인 R1~R7(탭 전환 상태 유지·되돌리기 한 번·찾기·파일 줄 끝/BOM·임시 저장·편집 메뉴·Oracle 실행/F4).
  성능 모니터 창·잠금 트리(8-1)는 아직 Folderss 본체 안에서 확인한 기록뿐 — 헤드리스 시험으로 옮긴다.
- 사람이 직접 써 볼 때는 미리보기 `pwsh scripts/preview.ps1`(Folderss 없이 플러그인 창만 사용자 화면에, 데이터는 따로).
- CI: GitHub Actions의 Windows 러너에서 `scripts/ui-test.ps1`(Oracle 없이 돌 수 있는 시험만)을 돌릴 수 있는지 — 숨은 데스크톱이 러너 세션에서 만들어지는지부터 확인.
- 알아 둔 것: 자동화 Invoke는 디스패처에 맡겨져 **나중에** 돈다(누른 직후 결과를 보지 말고 `WaitUntil`). 모달을 여는 버튼은 `UiThread.Post`.
  UI 자동화 클라이언트는 숨은 데스크톱의 창을 못 봐서 파일 대화상자는 Win32(`NativeDialogs`)로 채운다. 체크 상자는 `Ui.ClickToggle`(Toggle 패턴은 Click을 내지 않음).
  창을 닫아도 `Unloaded`(SQL 임시 저장)는 나중에 온다 — 다음 시험이 공유 데이터(sql-drafts)를 지우기 전에 한가해질 때까지 기다린다(`OpenSqlTab`).

## 5. 시험 환경 다시 만들기

로컬 XE 11.2(Windows 서비스 `OracleServiceXE`)에 시험 계정을 만든다(sysdba). 시험이 끝나면 지운다.

```sql
create user fmon_dba identified by "Fmon_2026x" default tablespace users quota 20m on users;
grant create session, create table, alter system, select_catalog_role to fmon_dba;
create user fmon_low identified by "Fmon_2026x";
grant create session to fmon_low;
-- 정리
drop user fmon_dba cascade;
drop user fmon_low cascade;
```

```powershell
$env:FMON_UI_ORACLE = "localhost:1521/xe"   # 계정 기본값 fmon_dba / Fmon_2026x (FMON_UI_ORACLE_USER · FMON_UI_ORACLE_PW로 바꿈)
pwsh scripts/ui-test.ps1
```

## 6. SQL 편집기 (AvalonEdit) 남은 것

- 한글 IME(계획서 4절 C): 사용자가 써 보고 특별한 문제 없다고 함(2026-10-22). 항목별 확인은 쓰면서, 문제가 보이면 따로 고친다.
- (별도) 테이블 이름 강조 — 메타데이터 대조 방식. TODO "SQL 편집기 구문 강조".
- 기능 보고서 [`docs/reports/2026-10-21-sql-editor-highlight`](../reports/2026-10-21-sql-editor-highlight/index.html)는 2-3단계(PoC 창) 기준 기록이다. 3·4단계 실측은 계획서 7절.

## 7. 릴리스

- v1.10.0(2026-10-22): `plugin.json` 1.10.0 → `v1.10.0` 태그. 태그를 올리면 Actions가 GitHub 릴리스를 만들고 zip을 붙인다(릴리스 노트는 PR 목록으로 자동).
- 다음 릴리스 전: `plugin.json` 버전을 먼저 올리고 같은 이름으로 태그(README "릴리스"), Folderss 본체 안에서 일일 점검 한 번 확인(헤드리스와 같은지 — v1.10.0 전에는 못 함).

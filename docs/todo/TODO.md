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
- [ ] 편집 팝업 변경 관리 — 원래 값과 다른 칸 강조, 툴팁 "원래 값: …", 칸별 ↶ 되돌리기·[모두 되돌리기],
      이미 고친 행을 다시 열어도 원래 값(`ResultGridRow.Original`) 기준으로 비교.
- [x] 편집 팝업 닫기 확인 — 바꾼 칸이 있는데 [취소]·Esc면 "버리고 닫을까요?"(기본 [계속 편집]).
      X·Alt+F4도 같다. 비교는 창을 열 때 값 기준(`RowEditLogic.HasChanges`), [확인]이면 묻지 않음. PR:  · 버전:
- [ ] 그리드에서 바뀐 칸만 강조 — 행은 지금처럼 주황, 바뀐 칸은 더 진하게(찾기 강조와 같은 방식).
- [ ] 복제 시 LOB 처리 — 지금은 LOB 열이 INSERT에서 빠져 NULL(원본 내용 손실, NOT NULL이면 오류).
      서버에서 그대로 복사(`INSERT … SELECT 원본 LOB FROM t WHERE 키 = :원본키`)하거나 최소한 복제 창에 경고.
- [ ] 동시 수정 검사(낙관적 잠금) — UPDATE WHERE에 바꾼 열의 원래 값도 넣어 다른 세션의 변경을 덮어쓰지 않게.
      FLOAT·초 미만 날짜처럼 표시 값과 DB 값이 정확히 같지 않은 형식은 비교에서 빼야 함(오탐 0행), NULL은 IS NULL.
- [ ] RAW 기본 키 지원 — RAW(16) GUID 기본 키 테이블은 지금 편집 불가. RAW는 2,000바이트 이하라 잘리지 않으므로
      "0x…" 표시를 바이트로 되돌려 바인드. 표시 형식은 테스트로 고정.
- [ ] (보류) 짧은 CLOB 편집·BLOB 파일 넣기/꺼내기 — 큰 데이터는 편집하지 않는 원칙에 따라 필요해질 때 다시 검토.
- [ ] 기본 키가 없는 테이블 편집 — 고유 키(NOT NULL UNIQUE) → Oracle ROWID 순으로 대체하는 방안 검토
      (PostgreSQL ctid는 UPDATE마다 바뀌어 제외, SQL Server는 행 주소 없음).

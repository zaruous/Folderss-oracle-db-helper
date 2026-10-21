# SQL 편집기 AvalonEdit 전환 — 진행·실측 계획

작성일 2026-10-14. 상태: **1단계 실측 중** — 헤드리스로 A·B·D·E를 쟀고(2026-10-21, 7·8절) 막는 문제는 없었다.
남은 것: C(한글 IME, 사람이 확인)와 8절의 키·화면 결정. 실측 결과가 나오기 전에는 본 교체를 시작하지 않는다.

## 0. 목표와 범위

- 목표: SQL 작업 영역 편집기에 구문 강조(1차: 주석·문자열)를 넣는다.
- 방법: `ICSharpCode.AvalonEdit`을 품은 전용 컨트롤 `SqlEditor`를 만들고, 지금 `TextBox`를 쓰는 호출부를 그 컨트롤로 옮긴다.
- 범위: **SQL 작업 영역 편집기만**(`SqlWorkspace.CreateEditor`, `SqlTabState.Editor`).
  `CodeSpecDialog._sql`, `SqlWorkspaceEdit._editWhere` 등 다른 입력칸은 바꾸지 않는다.
- 범위 밖(별도 작업): 테이블 이름 강조(메타데이터 대조), 자동 완성.

## 1. 지금 구조 (2026-10-14 기준)

- 편집기: `TextBox` (`src/MyPlugin/Ui/SqlWorkspace.cs` `CreateEditor`). 탭마다 하나씩 두어 글·캐럿·선택·스크롤·되돌리기 기록을 탭별로 유지.
- 편집기에 기대는 TextBox API(약 40곳, `SqlWorkspace*.cs`):
  `Text`, `CaretIndex`, `SelectionStart/Length`, `SelectedText`, `Select`, `VerticalOffset/HorizontalOffset`,
  `ScrollToLine`, `ScrollToHorizontalOffset`, `ScrollToHome`, `GetRectFromCharacterIndex`, `GetLineIndexFromCharacterIndex`,
  `GetFirst/LastVisibleLineIndex`, 이벤트 `TextChanged`·`SelectionChanged`·`PreviewKeyDown`.
- 저수준 좌표 API를 쓰는 곳은 두 군데뿐이다.
  - 줄 번호 위치 맞추기 `PositionGutter`/`UpdateGutter` — AvalonEdit 내장 줄 번호로 **삭제 대상**.
  - 찾은 글자로 스크롤 `SqlWorkspaceFind.ScrollEditorTo` — AvalonEdit `ScrollTo(line, column)`로 대체.
- 편집기 단축키(`Editor_PreviewKeyDown`): F4, F3 / Shift+F3, Ctrl+F, Ctrl+Enter, Ctrl+/ (주석 전환), Ctrl+Shift+U / L (대·소문자).
- TextBox에 준 설정 중 AvalonEdit으로 그대로 옮겨지지 않는 것: `TextBlock.LineHeight`(무시됨), `IsInactiveSelectionHighlightEnabled`,
  `CaretBrush`·선택 색, Folderss 암시적 `TextBox` 스타일.
- 문자열·주석 구분 로직은 이미 `Db/SqlScript.cs`에 있다(`'…'`, `q'[…]'`, `nq'…'`, `N'…'`, `"…"`, `--`, `/* */`, 닫히지 않은 것은 끝까지).
  다만 구간 목록을 돌려주는 공개 함수는 없다.
- 파일 열기 상한: `SqlFileLogic.MaxFileBytes` = 5MB. 붙여넣기는 상한 없음.
- 테스트 프로젝트는 `net8.0`(WPF 아님)이고 `Db/**`, `Ui/*Logic.cs`만 링크한다 → 강조 구간 계산은 WPF와 떨어진 곳에 두어야 테스트된다.

## 2. 설계 원칙 (전용 컨트롤)

- `SqlEditor`는 `TextEditor`를 **상속하지 않고 품는다**(AvalonEdit API가 밖으로 새지 않게).
- TextBox 함수를 1:1로 흉내 내지 않는다. 좌표 기준(여백·테두리 포함 여부)이 달라 이름만 같으면 몇 px씩 어긋나는 버그가 된다.
  밖에 내는 것은 의도 단위의 작은 집합만:
  ```
  Text, CaretOffset, SelectionStart, SelectionLength, SelectedText, Select(start, len)
  Replace(start, len, text)   // 되돌리기 한 번 단위 (지금 ApplyEdit의 의도)
  ScrollIntoView(offset)
  TextChanged, SelectionChanged
  ```
- 강조 구간 계산은 순수 함수 `SqlScript.HighlightSpans(text, rules)`(주석·문자열 구간 목록)로 두고 단위 테스트한다.
  화면 쪽은 그 결과로 색만 칠한다(`DocumentColorizingTransformer`).
- **DBMS별 규칙**(2026-10-20 반영): 문장 나누기와 강조가 같은 `SqlLexRules`를 따른다 — 스캐너는 하나, 규칙은 데이터
  (`SqlLexRules.Oracle`·`Postgres`·`SqlServer`, 공급자 `IDbProvider.Lex`). 3·4단계의 편집기는 탭 대상 DB의 규칙으로 칠하고,
  **탭 대상을 바꾸면 다시 칠해야 한다**(같은 글이 DB마다 다르게 나뉨).

## 3. 진행 단계와 완료 조건

| 단계 | 내용 | 완료 조건 (확인 방법) |
|---|---|---|
| 1. PoC (시험판 창 main에 들어감 — PR #24. 헤드리스 A·B·D·E 2026-10-21, C 남음) | 별도 브랜치에서 SQL 탭 하나만 AvalonEdit으로 띄운다. 강조는 xshd 기본 SQL 정의로 충분 | 4절 실측 A~E 모두 기록, 막는 문제(✗) 없음. 하나라도 ✗면 여기서 멈추고 대안 검토 |
| 2. 구간 계산 (완료 2026-10-20: `SqlScript.HighlightSpans`, `tests/MyPlugin.Tests/SqlHighlightTests.cs`) | `SqlScript`에서 주석·문자열 구간을 내는 공개 함수 추가 | 5절 D1의 기대 구간을 단위 테스트로 먼저 작성 → 통과. 기존 `SqlScriptTests` 전부 통과 |
| 3. 교체 | `SqlEditor` 도입, `SqlTabState.Editor`·호출부 교체, 직접 그린 줄 번호 삭제 | 빌드 통과, 6절 회귀 확인표 전부 ✓, `docs/controls/sql-workspace.yml`(+ 새 컨트롤이면 `sql-editor.yml`) 갱신, `python3 scripts/check_controls_docs.py` 통과 |
| 4. 강조 연결 | 2단계 구간으로 색칠, 테마 7종 색 지정 | 실측 B·C 다시 측정해 PoC 대비 악화 없음 |

## 4. 실측 항목 (Windows, 1단계 PoC에서)

### PoC 시험판 쓰는 법 (main에 들어 있음 — PR #24. 3단계 교체 뒤 지운다)
1. main 빌드 zip(Actions 산출물, 또는 `dotnet publish src/MyPlugin -c Release`가 만든 `MyPlugin.zip`)을 Folderss에 설치한다(플러그인 교체).
2. DB Helper 창 **도구 > AvalonEdit PoC (시험판)…** — 지금 SQL 탭의 글과 대상 DB 규칙으로 창이 열린다(본 SQL 탭은 그대로).
3. 창 위 버튼으로 시험 데이터(D1·D3-1~3·D4-2)를 넣고, 규칙 콤보로 Oracle·PostgreSQL·SQL Server를 바꿔 본다.
4. 창 아래에 실측값이 보인다: 넣기→첫 화면, 구간 계산(중앙값·최대), 최근 키가 **편집기가 처리**했는지 **통과**했는지(D 단축키 충돌 판단).
5. 한글 IME(C)·테마 7종(E)은 눈으로 확인한다(Folderss 테마를 바꾸며). [결과 복사] → 아래 7절 기록란에 붙이고 판단을 적는다.
- 창이 아예 안 뜨거나 오류가 나면(A 플러그인 로드 실패) 오류 메시지·`plugin-log.txt`를 기록한다.

### 헤드리스 자동 실측 (`tests/MyPlugin.UiTests/EditorPocHeadlessTests.cs`)
```powershell
pwsh scripts/ui-test.ps1 -Filter "FullyQualifiedName~EditorPocHeadlessTests"
```
- 숨은 데스크톱에서 도구 > AvalonEdit PoC 창을 열어 A·B·D·E를 재고, 실행 폴더 `shots/`에 `poc-measure.md`(7절 표 줄)·`poc-keys.md`(D 키 비교)·`poc-themes.md`(E 명암비)·캡처를 남긴다.
  막는 문제(A 로드·그리기·색칠 실패)만 시험 실패로 보고, 기준을 넘는 수치는 표에 ✗로 적는다.
- 비교 기준은 같은 크기 창의 **지금 SQL 탭 설정 TextBox**(`SqlWorkspace.CreateEditor`와 같은 값)와 **강조 없는 AvalonEdit**(편집기 자체 비용).
- 키는 UI 스레드의 키 상태(`SetKeyboardState` — 이 스레드만, 사용자 입력과 무관)로 Ctrl·Shift를 흉내 내고 키보드 포커스를 준 뒤 PreviewKeyDown → KeyDown을 낸다
  (WPF TextBox의 Tab·Enter는 포커스가 없으면 아무것도 안 한다). Ctrl+X/C/V는 사용자 클립보드를 건드리지 않게 빼고 설정만 읽는다.
- 숨은 데스크톱은 소프트웨어 렌더링이라 시간은 GPU로 그리는 실제 Folderss보다 느린 쪽(상한)이다. 실행마다 ±50% 정도 흔들려 판단은 여러 번 돌린 범위로 한다.
- 사람이 볼 것: C(한글 IME 전부)와 Folderss 본체 안 최종 확인(같은 zip).


측정자는 Windows에서 Folderss에 PoC 빌드 zip을 설치해 확인한다(이 저장소 CI는 Linux라 화면 확인 불가).
시간 측정은 PoC 빌드에만 `Stopwatch` 로그를 넣는다(본 교체에는 넣지 않음). 각 수치는 3회 측정 중앙값.

### A. 플러그인 로드 (가부)
- [x] 플러그인 전용 로드 영역(`PluginLoadContext`)에서 AvalonEdit DLL이 로드되고, 자체 리소스(`Themes/generic.xaml`, pack URI) 오류 없이 편집기가 그려진다.
      (헤드리스 2026-10-21: 플러그인 컨텍스트에 6.3.0.90, 기본 템플릿 적용, 주석·문자열 색칠, UI 예외 0)
- [x] Folderss 재시작 후에도 같다. zip에 `ICSharpCode.AvalonEdit.dll`이 들어가고 `.deps.json`에 올라간다.
      (zip·.deps.json 확인, 헤드리스는 실행마다 새 프로세스. Folderss 본체 안 재시작은 C 확인 때 함께)

### B. 성능 (TextBox와 같은 데이터로 비교)
| 항목 | 측정 방법 | 기준 |
|---|---|---|
| 파일 열기 → 첫 표시 | `OpenPath` 시작 ~ 편집기 `Loaded`/첫 렌더 | TextBox 이하 또는 체감 차 없음 |
| 키 입력 지연 | D3 파일 중간·끝에서 연속 입력 시 체감 + `TextChanged` ~ 강조 갱신 완료 시간 | 16ms 이하(한 프레임). 넘으면 강조를 지연/증분 처리로 설계 변경 |
| 전체 다시 칠하기 | D4-2(맨 앞에 `/*` 입력 → 문서 전체가 주석) | 1초 이내, UI 멈춤 없음 |
| 스크롤 | D3 끝까지 휠·끌기 | 끊김 없음 |
| 탭 여러 개 메모리 | D5(탭 20개) 열기 전후 작업 관리자 메모리(전용 작업 집합) | TextBox 대비 증가량 기록(판단 기준: 탭당 수 MB 이내) |
| 강조 구간 계산 | 순수 함수 단독 시간, D3 각 크기 | 키 입력마다 전체 다시 계산 가능한지 판단 자료 |

블록 주석·`q'[…]'`는 여러 줄에 걸치므로 줄 단위 색칠만으로는 앞 줄 상태가 필요하다. 전체 다시 계산이 기준을 넘으면
"바뀐 위치 이후만 다시 계산" 같은 증분 방식으로 바꾼다 — 그 판단을 이 측정으로 한다.

### C. 한글 입력(IME) — 가장 큰 실사용 위험 (사람이 확인 — 헤드리스로 못 봄)
- [ ] 조합 중인 글자가 캐럿 위치에 바로 보인다(밑줄 표시, 엉뚱한 위치에 뜨지 않음).
- [ ] 한자 변환(한자 키) 후보 창이 캐럿 근처에 뜬다.
- [ ] 조합 중 Backspace가 자모 단위로 지운다.
- [ ] 조합 중 Ctrl+Enter / F4 / Ctrl+F를 누르면: 조합이 확정된 뒤 동작한다(글자 유실·중복 없음). 지금 코드는 `Key.ImeProcessed` → `ImeProcessedKey`로 처리 중.
- [ ] 조합 중 마우스로 다른 위치 클릭, 다른 SQL 탭으로 전환 → 조합 글자 유실·중복 없음.
- [ ] 주석·문자열 안 한글(강조 구간 안)에서도 위 항목 동일.
- [ ] 한/영 전환 직후 첫 글자 정상.

### D. 단축키 충돌
TextBox 빌드와 PoC 빌드에서 같은 키를 눌러 결과를 비교한다.
- [x] 기존 편집기 키: F4, F3 / Shift+F3, Ctrl+F, Ctrl+Enter, Ctrl+/, Ctrl+Shift+U, Ctrl+Shift+L
      (헤드리스: 8개 모두 편집기를 품은 컨트롤의 PreviewKeyDown에 먼저 온다 — AvalonEdit은 터널 단계에서 먹지 않음.
      3단계에서 `Editor_PreviewKeyDown`을 `SqlEditor`에 붙이면 지금처럼 SQL 탭이 먼저 처리한다)
- [x] 기본 편집 키: Tab, Shift+Tab(선택 영역에서 AvalonEdit은 들여쓰기/내어쓰기를 함 — 지금 동작과 다름), Ctrl+Z / Ctrl+Y, Ctrl+A, Ctrl+X/C/V, Ctrl+D, Ctrl+I, Insert, Ctrl+Backspace/Delete, Home/End, Ctrl+Home/End
      (헤드리스 18가지 중 8가지 다름 → 8절 결정 표)
- [x] Folderss 본체 전역 단축키가 편집기에 포커스가 있을 때 지금과 같게 동작하는지
      (해당 없음: 플러그인은 본체가 띄운 별도 창(`PluginManager.ShowPluginWindow`)이라 본체 `Window_PreviewKeyDown`을 지나지 않는다.
      플러그인 창 단축키 Ctrl+N·O·S·Shift+S·W, F5는 `DbHelperView`의 PreviewKeyDown(터널)이라 편집기보다 먼저 받는다)
- 다른 결과가 나오면 "지금 동작 유지 / AvalonEdit 동작 채택"을 키마다 결정해 이 문서에 적는다.

### E. 화면·테마
- [ ] 테마 7종(Black, Catppuccin, Dracula, GitHub, Light, Nord, Solarized): 바탕·글자·캐럿·선택색·줄 번호가 테마를 따른다. 테마 바꾸면 즉시 반영.
      (헤드리스: 바탕·글자·선택·줄 번호는 7종 모두 따르고 즉시 바뀜. **주석색은 테마를 바꿔도 다시 칠할 때까지 그대로** — PoC가 다시 계산할 때만 색을 읽음 → 4단계. 캐럿은 눈으로)
- [ ] 주석·문자열 강조색이 테마 7종 모두에서 바탕 대비 읽힌다(WCAG 대비 4.5:1 목표, 측정값 기록).
      (헤드리스: 주석 5.2~9.2:1 ✓, **문자열 GitHub·Light 2.5:1 ✗** — 문자열색이 테마와 무관한 고정색 `Theme.Warning`(#D19A66) → 4단계에서 테마별 색)
- [x] 찾기 칸에 포커스가 있을 때 편집기의 찾은 선택 영역이 보인다(지금 `IsInactiveSelectionHighlightEnabled`가 하는 일).
      (헤드리스: 포커스 없는 편집기에 선택 영역이 7종 모두 그려짐, 선택 위 글자 대비 6.9~13.1:1)
- [ ] 줄 간격: 지금(`EditorLineHeight`)과 차이를 스크린샷으로 비교, 허용 여부 결정.
      (헤드리스: AvalonEdit 기본 줄 높이 글꼴 13에서 15px, 지금 TextBox 19px 고정 → 8절 결정)
- [x] 글꼴 크기 변경(`EditorFontSize`) 반영. (헤드리스: 13 → 15px, 16 → 19px로 줄 높이가 따라옴)

## 5. 실측용 테스트 데이터

모든 파일은 UTF-8. 회사 실제 SQL을 쓸 때는 테이블·컬럼 이름과 리터럴 값을 가린 사본을 쓴다(저장소에 올리지 않음).

### D1. 정확성 — 경계 사례 (약 60줄, 단위 테스트 기대값으로도 사용)
한 파일에 아래를 모두 넣고, 각 줄의 기대 구간(주석/문자열/일반)을 표로 적는다.
```sql
-- 줄 주석 한글
SELECT 1 FROM DUAL; -- 끝 주석
/* 블록
   여러 줄 -- 안의 줄 주석 기호는 주석의 일부 */
SELECT /*+ INDEX(t IX_T) */ * FROM T t;          -- 힌트(블록 주석으로 칠함)
SELECT 'a -- 문자열 안' , 'it''s', '/* 아님 */' FROM DUAL;
SELECT q'[ -- 아님 ' 아님 ]', q'{ } }', q'( ) )', q'< >', q'! ! !', nq'[ 한글 ]', N'유니코드' FROM DUAL;
SELECT "Col -- 아님", "a""b" FROM DUAL;
SELECT 'line1
line2 -- 여러 줄 문자열 안' FROM DUAL;
SELECT 1 - -1 FROM DUAL;                           -- 빼기·음수(주석 아님)
SELECT 10/2 FROM DUAL;                            -- 나눗셈(주석 아님)
SELECT '😀 이모지' FROM DUAL; -- 서로게이트 쌍: 오프셋 어긋남 확인
BEGIN
  NULL; -- PL/SQL 안 주석
END;
/
SELECT '닫히지 않은 문자열 → 끝까지 문자열
```
- 줄 끝 형식 변형: 같은 내용을 CRLF / LF / 섞임 세 벌(오프셋이 `\r\n` 2글자 기준으로 맞는지).
- 탭 문자 들여쓰기 한 벌.
- 별도 파일: `/* 닫히지 않은 블록 주석 → 끝까지 주석` 으로 시작하는 파일.

### D2. 일상 — 실제 작업 SQL (300~1,000줄)
- 사용자가 실제로 쓰는 스크립트 2~3개(가린 사본): 주석 비율이 높은 것, PL/SQL 패키지 본문, 긴 SELECT 여러 개.
- 용도: 체감 성능·한글 입력·단축키(C, D) 확인의 기본 데이터.

### D3. 대용량 — 생성 데이터 (크기별)
| 이름 | 줄 수 | 크기(대략) | 비고 |
|---|---|---|---|
| D3-1 | 10,000 | 0.5MB | |
| D3-2 | 50,000 | 2.5MB | |
| D3-3 | 100,000 | 5MB | 파일 열기 상한(`MaxFileBytes`) 근처 |

구성(반복 단위, 고정 비율로 재현 가능하게): 줄 주석 20%, 블록 주석 10%(3~10줄), 문자열 리터럴 포함 문장 30%(`q'[…]'` 포함),
일반 SELECT/INSERT/UPDATE 40%, 한글은 주석·문자열에 절반 정도. 같은 반복 단위를 줄 수만큼 이어 붙여 만든다(생성 방법은 PoC 때 스크립트로 고정).

### D4. 극단 사례
- D4-1: 한 줄 1MB (긴 `INSERT … VALUES (…),(…),…` 한 줄) — 가로 스크롤·입력 지연.
- D4-2: D3-3 맨 앞에 `/*` 를 입력했다 지우기 — 문서 전체 강조가 뒤집혔다 돌아오는 시간.
- D4-3: D3-3 전체가 하나의 블록 주석인 파일.
- D4-4: 클립보드로 10MB 붙여넣기(파일 상한을 우회하는 경로).

### D5. 탭 여러 개
- D2 파일을 탭 20개로 열기(메모리·탭 전환 시간).

## 6. 회귀 확인표 (3단계 교체 후, Windows)

- [ ] 탭별 글·캐럿·선택·스크롤·되돌리기 기록이 탭 전환 후에도 유지
- [ ] Ctrl+Enter(캐럿 위치 문장)·F4·스크립트 실행·선택 영역 실행
- [ ] 트리에서 SELECT 문 넣기(맨 위 삽입), 대·소문자 바꾸기, 주석 전환 — 각각 되돌리기 한 번으로 원복
- [ ] 찾기(Ctrl+F, F3/Shift+F3, 찾은 글자로 스크롤 — 세로·가로)
- [ ] SQL 파일 열기·저장·다른 이름 저장, 수정 표시, 줄 끝(CRLF·LF) 파일을 열고 저장했을 때 지금과 같은 결과
- [ ] 임시 저장(`SqlDraftLogic`) 복원
- [ ] 줄 번호가 스크롤·글꼴 크기 변경에 맞음
- [ ] 4절 C·D·E 재확인

## 7. 실측 결과 기록란

| 날짜 | 측정자 | 환경(Windows·Folderss·AvalonEdit 버전) | 항목 | 결과 | 판단 |
|---|---|---|---|---|---|
| 2026-10-20 | Claude | Linux 컨테이너 4코어, .NET 8 Release (Windows·AvalonEdit 아님 — 순수 함수만) | 강조 구간 계산 `SqlScript.HighlightSpans` 단독, D3 구성(CRLF), 5회 중앙값 | D3-1 1만 줄(0.6MB) 6.6ms · D3-2 5만 줄(2.8MB) 37.1ms · D3-3 10만 줄(5.5MB) 52.9ms | 1만 줄까지는 키 입력마다 전체 다시 계산해도 16ms 안. 그 위는 넘으므로 4단계에서 입력이 멈춘 뒤 계산(지연)하거나 바뀐 위치 이후만 다시 계산해야 한다. Windows PoC에서 같은 데이터로 다시 잰다 |
| 2026-10-21 | Claude (헤드리스) | Windows 11 (26200) · Core Ultra 7 155H · .NET 8.0.32 · AvalonEdit 6.3.0.90 · 숨은 데스크톱(소프트웨어 렌더링) | A 플러그인 로드 | 플러그인 PluginLoadContext에서 6.3.0.90 로드, 기본 템플릿(generic.xaml) 적용, D1 25줄 그려짐, 주석·문자열 색칠, UI 예외 0 | ✓ |
| 2026-10-21 | Claude (헤드리스) | 〃 | B 넣기→첫 화면 D3-1·2·3 (3회 중앙값) | AvalonEdit+강조 54 · 58 · 75ms / 강조 없음 21 · 55 · 88ms / TextBox 617 · 2,608 · 4,494ms | ✓ TextBox보다 10~60배 빠름 |
| 2026-10-21 | Claude (헤드리스) | 〃 | B 키 입력 지연 D3-1·2·3 가운데 (10회 중앙값) | AvalonEdit+강조 21.7 · 21.1 · 26.6ms / 강조 없음 4.9 · 3.9 · 1.9ms / TextBox 2.6 · 9.1 · 17.5ms | ✗ 강조 포함만 16ms 넘음(네 번 돌려 14~41ms). 편집기 자체는 2~9ms — 8절 |
| 2026-10-21 | Claude (헤드리스) | 〃 | B 강조 구간 계산 단독 D3-1·2·3 (5회 중앙값) | 1.2 · 8.7 · 8.7ms (네 번 돌려 1.2~14ms) | ✓ Linux 참고값(6.6 · 37 · 53ms)보다 빠름 |
| 2026-10-21 | Claude (헤드리스) | 〃 | B 전체 다시 칠하기 D4-2 (D3-3 맨 앞 /* 넣기·빼기) | 넣기 23.5ms · 빼기 22.7ms | ✓ (기준 1초) |
| 2026-10-21 | Claude (헤드리스) | 〃 | B 스크롤 D3-3 (끝으로 · 한 화면씩 30회) | AvalonEdit+강조 끝으로 8.4ms · 한 화면 7.7ms(최대 13.2) / TextBox 5.7ms · 5.5ms(최대 17.2) | ✓ |
| 2026-10-21 | Claude (헤드리스) | 〃 | B 탭 20개 메모리 (1천 줄씩, 관리 힙, 탭 닫으면 줄어듦 확인) | AvalonEdit 탭당 0.6MB · TextBox 0.25MB, 탭을 닫으면 둘 다 그만큼 줄어듦 | ✓ (기준 탭당 수 MB) |
| 2026-10-21 | Claude (헤드리스) | 〃 | D SQL 탭 편집기 키 8개 | 모두 편집기를 품은 컨트롤의 PreviewKeyDown에 먼저 옴 | ✓ |
| 2026-10-21 | Claude (헤드리스) | 〃 | D 기본 편집 키 18가지 | 8가지 다름(Tab·Shift+Tab 선택, Shift+Tab 캐럿, Ctrl+D, Ctrl+I, Insert, Home, Enter) + 선택 없는 Ctrl+C·X가 줄 전체(CutCopyWholeLine) | 키마다 결정 — 8절 |
| 2026-10-21 | Claude (헤드리스) | 〃 | D Folderss 본체 전역 단축키 | 해당 없음(플러그인은 별도 창) | ✓ |
| 2026-10-21 | Claude (헤드리스) | 〃 | E 테마 7종 | 바탕·글자·선택·줄 번호 ✓, 주석 5.2~9.2:1, 문자열 GitHub·Light 2.5:1 ✗, 주석색은 테마를 바꿔도 다시 칠할 때까지 그대로 | 4단계에서 고침 — 8절 |
| 2026-10-21 | Claude (헤드리스) | 〃 | E 줄 간격·글꼴 크기 | AvalonEdit 글꼴 13에서 15px(16에서 19px), 지금 TextBox 19px 고정 | 결정 — 8절 |
| | | | | | |

## 8. 1단계 중간 판단 (2026-10-21, 헤드리스 A·B·D·E)

막는 문제(✗로 멈출 것)는 없다. **C(한글 IME)를 사람이 확인해 ✗가 없으면 3단계로 간다.** 아래는 3·4단계에 넘길 결정이다.

### 성능 → 4단계 설계 변경
- AvalonEdit 자체는 지금 TextBox보다 낫다: 넣기 10~60배 빠르고(10만 줄 0.1초 대 4.5~6초), 강조 없이 키 입력 2~9ms.
- PoC처럼 **글이 바뀔 때마다 전체를 다시 계산·다시 그리면** 1만 줄에서도 14~23ms, 5만~10만 줄 17~41ms로(네 번 돌린 범위) 16ms를 넘는다.
  구간 계산 단독은 1~14ms라 나머지는 글 꺼내기(`Document.Text` — 문서 전체 문자열)와 화면 전체 `Redraw()`다.
- 그래서 4단계는 증분으로: (1) 줄 시작 상태(주석·문자열 안인지)를 줄마다 두고 바뀐 줄부터 상태가 같아질 때까지만 다시 계산
  (AvalonEdit `DocumentHighlighter`가 쓰는 방식), (2) 다시 그리기는 상태가 바뀐 범위만(`TextView.Redraw(offset, length)`),
  (3) 그래도 넘는 큰 문서는 입력이 멈춘 뒤 계산. D4-2(맨 앞 `/*`)처럼 전체가 뒤집히는 경우는 지금도 23ms라 문제없다.

### 키 — 결정 필요 (헤드리스 비교, 표 원본은 실행 폴더 `shots/poc-keys.md`)
| 키 | 지금 TextBox | AvalonEdit | 제안 | 결정 |
|---|---|---|---|---|
| Tab (여러 줄 선택) | 선택한 글을 탭 하나로 바꿈(글이 지워짐) | 줄마다 들여쓰기, 선택 유지 | AvalonEdit | |
| Shift+Tab (여러 줄 선택) | 선택한 글을 탭 하나로 바꿈 | 줄마다 내어쓰기 | AvalonEdit | |
| Shift+Tab (캐럿만) | 탭 넣음 | 그 줄 내어쓰기 | AvalonEdit | |
| Enter (들여쓴 줄 끝) | 줄바꿈만 | 앞 줄 들여쓰기 유지 | AvalonEdit | |
| Home | 줄 맨 앞 | 첫 글자 → 한 번 더 누르면 줄 맨 앞 | AvalonEdit | |
| Ctrl+C·X (선택 없음) | 아무것도 안 함 | 줄 전체 복사·잘라내기 (`CutCopyWholeLine`) | AvalonEdit(끄려면 옵션) | |
| Ctrl+D | 없음 | **줄 삭제** | 끔(실수로 줄이 지워짐 — 되돌리기는 됨) | |
| Insert | 겹쳐쓰기 전환 | 키는 먹지만 전환 안 함(`AllowToggleOverstrikeMode` 기본 끔) | 옵션을 켜 지금과 같게 | |
| Ctrl+I | 없음 | 먹음(글은 그대로) | 그대로 | |
| 같음 | Tab(선택 없음), Ctrl+Z·Y·A, Ctrl+Backspace·Delete, End, Ctrl+Home·End | | | |

Ctrl+V는 시험이 클립보드를 건드리지 않게 빼서 C 확인 때 함께 본다.

### 화면 — 결정 필요
- 줄 간격: AvalonEdit 기본은 글꼴 13에서 15px로 지금(19px)보다 촘촘하다. 그대로 둘지, 19px에 맞출지(AvalonEdit 6.3에 줄 간격 옵션이 없어 방법부터 조사) 정한다.
- 문자열색: 테마와 무관한 고정색이라 밝은 테마(GitHub·Light)에서 2.5:1 — 4단계에서 테마별 색(밝은 테마는 진한 색)을 둔다.
- 주석색: 테마를 바꾸면 다시 칠할 때까지 옛 색 — 4단계에서는 그릴 때 리소스에서 색을 읽거나 테마 변경 때 다시 칠한다.

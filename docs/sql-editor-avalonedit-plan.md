# SQL 편집기 AvalonEdit 전환 — 진행·실측 계획

작성일 2026-10-14. 상태: **3단계(교체) 완료 2026-10-22** — SQL 탭 편집기는 `SqlEditor`(AvalonEdit)이고 4단계(강조 연결)도 함께 끝났다.
직접 그린 줄 번호 칸과 PoC 창(도구 메뉴 항목)은 지웠다. 6절 회귀 확인표는 헤드리스 시험으로 확인했다(Oracle 실행 포함).
C(한글 IME): 사용자가 Folderss에서 써 보고 특별한 문제 없다고 함(2026-10-22) — 아래 항목은 하나씩 따로 보지 않았고, 쓰면서 문제가 보이면 따로 고친다. 1·2·2-2·2-3단계의 실측·결정은 7·8절.
기능 보고서(2-3단계까지): [docs/reports/2026-10-21-sql-editor-highlight/index.html](reports/2026-10-21-sql-editor-highlight/index.html).

## 0. 목표와 범위

- 목표: SQL 작업 영역 편집기에 구문 강조(주석·문자열·키워드 — 키워드는 2026-10-21 사용자 요청으로 넣음)를 넣는다.
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
- **키워드·증분**(2026-10-21): 편집기 구간은 `SqlScript.EditorSpans`(주석·문자열 + 키워드 + 칠하지 않는 따옴표 식별자) — 같은 토크나이저의 낱말 토큰이
  `SqlLexRules.Keywords`(DBMS별 목록)에 있으면 키워드다. 글이 바뀌면 `SqlHighlighter`가 바뀐 줄(여러 줄 토큰 안이면 그 토큰이 시작한 줄)부터
  새 구간이 예전 구간과 같아지는 곳까지만 다시 훑는다(문서 전체 문자열을 만들지 않음). 편집기에 붙이는 것은 `SqlEditorHighlighting`
  (`Ui/`, 4단계 SqlEditor가 그대로 쓴다), 색은 `SqlHighlightLogic.Palette`(테마 색에서 계산, 바탕 대비 4.5:1·주석과 본문 구분).
- **DBMS별 규칙**(2026-10-20 반영): 문장 나누기와 강조가 같은 `SqlLexRules`를 따른다 — 스캐너는 하나, 규칙은 데이터
  (`SqlLexRules.Oracle`·`Postgres`·`SqlServer`, 공급자 `IDbProvider.Lex`). 3·4단계의 편집기는 탭 대상 DB의 규칙으로 칠하고,
  **탭 대상을 바꾸면 다시 칠해야 한다**(같은 글이 DB마다 다르게 나뉨).

## 3. 진행 단계와 완료 조건

| 단계 | 내용 | 완료 조건 (확인 방법) |
|---|---|---|
| 1. PoC (시험판 창 main에 들어감 — PR #24. 헤드리스 A·B·D·E 2026-10-21, C 남음) | 별도 브랜치에서 SQL 탭 하나만 AvalonEdit으로 띄운다. 강조는 xshd 기본 SQL 정의로 충분 | 4절 실측 A~E 모두 기록, 막는 문제(✗) 없음. 하나라도 ✗면 여기서 멈추고 대안 검토 |
| 2. 구간 계산 (완료 2026-10-20: `SqlScript.HighlightSpans`, `tests/MyPlugin.Tests/SqlHighlightTests.cs`) | `SqlScript`에서 주석·문자열 구간을 내는 공개 함수 추가 | 5절 D1의 기대 구간을 단위 테스트로 먼저 작성 → 통과. 기존 `SqlScriptTests` 전부 통과 |
| 2-2. 키워드·증분 강조 (완료 2026-10-21: `SqlScript.EditorSpans`·`SqlKeywords`·`SqlHighlighter`·`SqlEditorHighlighting`·`SqlHighlightLogic`) | DBMS별 키워드 색, 바뀐 곳 근처만 다시 계산, 테마별 색 | 키워드 단위 테스트(`SqlKeywordTests`), 무작위 고치기에서 증분 = 전체(`SqlHighlighterTests`), 테마 7종 대비(`SqlHighlightLogicTests`), 헤드리스 키워드 색·성능(`EditorPocHeadlessTests`) |
| 2-3. 편집기 설정·키 결정 (완료 2026-10-21: `SqlEditorSettingsPage`·`SqlEditorSettings`·`SqlEditorSetup`) | 설정 "SQL 편집기" 탭(줄 간격, 긴 줄 기준, 대상별 칠함·색·굵게·기울임, 더 칠할·칠하지 않을 낱말, 미리 보기), 자료형을 키워드와 따로, 키 동작 결정 적용 | `SqlEditorSettingsTests`, 헤드리스 `S_SettingsPage_AppliesToEditor`(미리 보기·저장 뒤 열린 편집기 반영), 키 비교에서 Ctrl+D·Insert가 TextBox와 같음 |
| 3. 교체 (완료 2026-10-22: `Ui/SqlEditor.cs`, 직접 그린 줄 번호·PoC 창 삭제) | `SqlEditor` 도입, `SqlTabState.Editor`·호출부 교체, 직접 그린 줄 번호 삭제 | 빌드 통과, 6절 회귀 확인표 전부 ✓, `docs/controls/sql-workspace.yml`(+ 새 컨트롤이면 `sql-editor.yml`) 갱신, `python3 scripts/check_controls_docs.py` 통과 |
| 4. 강조 연결 (완료 2026-10-22 — 3단계와 함께) | `SqlEditor`에 `SqlEditorHighlighting`을 붙이고 탭 대상 DB가 바뀌면 `Rules`를 바꾼다(색·증분은 2-2에서 끝남) | 실측 B·C 다시 측정해 PoC 대비 악화 없음 |

## 4. 실측 항목 (Windows, 1단계 PoC에서)

### PoC 시험판 쓰는 법 (2026-10-22 3단계 교체와 함께 PoC 창을 지웠다 — 아래는 기록. 지금은 SQL 탭 편집기에서 잰다)
1. main 빌드 zip(Actions 산출물, 또는 `dotnet publish src/MyPlugin -c Release`가 만든 `MyPlugin.zip`)을 Folderss에 설치한다(플러그인 교체).
2. DB Helper 창 **도구 > AvalonEdit PoC (시험판)…** — 지금 SQL 탭의 글과 대상 DB 규칙으로 창이 열린다(본 SQL 탭은 그대로).
3. 창 위 버튼으로 시험 데이터(D1·D3-1~3·D4-2)를 넣고, 규칙 콤보로 Oracle·PostgreSQL·SQL Server를 바꿔 본다.
4. 창 아래에 실측값이 보인다: 넣기→첫 화면, 구간 계산(중앙값·최대), 최근 키가 **편집기가 처리**했는지 **통과**했는지(D 단축키 충돌 판단).
5. 한글 IME(C)·테마 7종(E)은 눈으로 확인한다(Folderss 테마를 바꾸며). [결과 복사] → 아래 7절 기록란에 붙이고 판단을 적는다.
- 창이 아예 안 뜨거나 오류가 나면(A 플러그인 로드 실패) 오류 메시지·`plugin-log.txt`를 기록한다.

### 헤드리스 자동 실측 (`tests/MyPlugin.UiTests/SqlEditorHeadlessTests.cs` — 3단계 전에는 PoC 창으로 쟀던 `EditorPocHeadlessTests`)
```powershell
pwsh scripts/ui-test.ps1 -Filter "FullyQualifiedName~SqlEditorHeadlessTests"
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
사용자가 Folderss에서 써 보고 특별한 문제 없다고 함(2026-10-22) — 아래 항목은 하나씩 따로 보지 않았고, 쓰면서 문제가 보이면 따로 고친다.

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
- [x] 테마 7종(Black, Catppuccin, Dracula, GitHub, Light, Nord, Solarized): 바탕·글자·캐럿·선택색·줄 번호가 테마를 따른다. 테마 바꾸면 즉시 반영.
      (헤드리스: 바탕·글자·선택·줄 번호·강조색 모두 7종에서 따르고 테마를 바꾸면 바로 다시 칠함 — 2026-10-21 첫 측정 때 주석색이 늦게 바뀌던 것은
      `SqlEditorHighlighting`이 바탕 변경 때 색을 다시 정해 고침. 캐럿은 눈으로)
- [x] 주석·문자열 강조색이 테마 7종 모두에서 바탕 대비 읽힌다(WCAG 대비 4.5:1 목표, 측정값 기록).
      (헤드리스: 주석 4.6~7.5 · 문자열 5.1~7.7 · 키워드 4.6~7.6:1, 주석과 본문 구분 2.3:1 이상. 첫 측정의 문자열 2.5:1(GitHub·Light)과
      Nord 주석이 본문과 거의 같던 것(1.17:1)은 `SqlHighlightLogic.Palette`가 테마 색에서 다시 정해 고침)
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

2026-10-22 헤드리스(`tests/MyPlugin.UiTests/SqlEditorRegressionTests.cs`, 키는 편집기 입력 칸에 내서 SQL 탭·DB Helper 창 처리기를 그대로 지남):
- [x] 탭별 글·캐럿·선택·스크롤·되돌리기 기록이 탭 전환 후에도 유지 — R1(두 탭을 오가도 글·선택·세로 스크롤 그대로, Ctrl+Z는 그 탭 것만)
- [x] Ctrl+Enter(캐럿 위치 문장)·F4·스크립트 실행·선택 영역 실행 — R7(Oracle XE 11.2: 캐럿 문장·선택 영역·F5 결과 2개·F4 테이블 정보 창). 연결 안 된 대상은 지금처럼 상태줄 [연결]
- [x] 트리에서 SELECT 문 넣기(맨 위 삽입), 대·소문자 바꾸기, 주석 전환 — 각각 되돌리기 한 번으로 원복 — R2(맨 위 넣기는 트리·기록이 부르는 같은 메서드)
- [x] 찾기(Ctrl+F, F3/Shift+F3, 찾은 글자로 스크롤 — 세로·가로) — R3(찾기 칸 Enter·Shift+Enter, 편집기 F3·Shift+F3, Esc)
- [x] SQL 파일 열기·저장, 수정 표시, 줄 끝(CRLF·LF) 파일을 열고 저장했을 때 지금과 같은 결과 — R4(LF·BOM 없음, CRLF·BOM 둘 다 그대로, 탭 *). 다른 이름 저장은 대화상자라 헤드리스로 안 봄
- [x] 임시 저장(`SqlDraftLogic`) 복원 — R5(창을 닫고 다시 열면 글·캐럿)
- [x] 줄 번호가 스크롤·글꼴 크기 변경에 맞음 — AvalonEdit 내장 줄 번호(A, 둘러보기 캡처)
- [x] 편집 메뉴(모두 선택·실행 취소)와 위치 표시 — R6
- [x] 4절 C·D·E 재확인 — D(키, SQL 탭 처리기가 먼저)·E(테마 7종) ✓, C(한글 IME)는 사용자가 Folderss에서 써 보고 특별한 문제 없다고 함(2026-10-22) — 아래 항목은 하나씩 따로 보지 않았고, 쓰면서 문제가 보이면 따로 고친다

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
| 2026-10-21 | Claude (헤드리스) | 〃 | 2-2 키워드 색 (세 DBMS, 그려진 글자 색을 위치마다 읽음) | Oracle 29/29 · PostgreSQL 8/8 · SQL Server 10/10 — 문자열·주석·"Select"·[select]·t.date·:from·@from은 키워드 아님 | ✓ |
| 2026-10-21 | Claude (헤드리스) | 〃 | 2-2 키 입력, 증분 강조 (SqlEditorHighlighting만 붙인 편집기, 10회 중앙값) | D2 실무형 1만 줄 5.4·9.3 · D3-1 3.2·6.0 · D3-2 7.8·4.6 · D3-3 10만 줄 9.1·11.9 · D4-3 전부 주석 3.7·2.9ms (가운데·끝) | ✓ 16ms 안 |
| 2026-10-21 | Claude (헤드리스) | 〃 | 2-2 키 입력, 비교 | 전체 다시 계산 8.6~72.7ms · 강조 없는 AvalonEdit 2.6~8.4ms · 지금 TextBox 2.3~45.6ms | 증분이 강조 없는 편집기와 비슷 |
| 2026-10-21 | Claude (헤드리스) | 〃 | 2-2 한 줄 1MB (D4-1) | 그 줄 가운데 입력: 증분 4.0초 · 강조 없음 4.7초 · TextBox 3.4초 / 다음 줄 입력: 증분 38.7ms(범위 조정 전 2.2초) | 편집기 한계 — 강조와 무관 |
| 2026-10-21 | Claude (헤드리스) | 〃 | 2-2 색이 뒤집히는 고치기 (D3-3) | 증분: 맨 앞 /* 13.9ms · 가운데 ' 0.8ms(약 4천 자만 읽음) / 전체: 57.4 · 53.3ms(290만 자) | ✓ |
| 2026-10-21 | Claude (헤드리스) | 〃 | 2-2 스크롤 D3-3 한 화면 (30회 중앙값) | 증분 7.5~16.8ms · 강조 없음 6.8~14.4ms · TextBox 4.8~7.2ms (실행마다 흔들림) | 강조로 1~2ms 더함 |
| 2026-10-21 | Claude (헤드리스) | 〃 | 2-2 테마 7종 (SqlHighlightLogic.Palette) | 주석 4.6~7.5 · 문자열 5.1~7.7 · 키워드 4.6~7.6:1, 주석:본문 2.3:1 이상, 테마 바꾸면 바로 다시 칠함 | ✓ |
| 2026-10-22 | Claude (헤드리스) | 〃 | 3단계 B 넣기→첫 화면, SQL 탭 편집기(SqlEditor, 3회 중앙값) | D2 실무형 31 · D3-1 30 · D3-2 62 · D3-3 80 · D4-3 전부 주석 62ms / 옛 TextBox 0.6 · 0.7 · 3.6 · 4.6 · 5.0초 | ✓ |
| 2026-10-22 | Claude (헤드리스) | 〃 | 3단계 B 키 입력, SQL 탭 편집기(SQL 탭 처리기 — 위치 표시·임시 저장 타이머 포함, 10회 중앙값 가운데·끝) | D2 9.3·7.6 · D3-1 7.8·6.6 · D3-2 6.3·5.4 · D3-3 5.7·8.7 · D4-3 6.3·8.5ms (최대 8~35ms, 한두 번 튐) / 같은 실행의 강조만 붙인 편집기 3.5~8.4ms · 옛 TextBox 3.5~25.4ms | ✓ 16ms 안 — SQL 탭 처리기가 1~4ms 더함 |
| 2026-10-22 | Claude (헤드리스) | 〃 | 3단계 회귀 확인표(6절) R1~R7 · A·K·D·E·S·T | 모두 통과(R7은 Oracle XE 11.2 실행·F4) | ✓ — C(IME)는 사용자 확인(특별한 문제 없음) |
| | | | | | |

## 8. 1단계 중간 판단 (2026-10-21, 헤드리스 A·B·D·E)

막는 문제(✗로 멈출 것)는 없다. **C(한글 IME)를 사람이 확인해 ✗가 없으면 3단계로 간다.** 아래는 3·4단계에 넘길 결정이다.

### 성능 — 증분 강조로 해결 (2026-10-21 2-2단계, 측정값은 7절)
- AvalonEdit 자체는 지금 TextBox보다 낫다: 넣기 10~60배 빠르고(10만 줄 0.1초 대 4.5~10초), 강조 없이 키 입력 2~9ms.
- 첫 측정에서 PoC처럼 **글이 바뀔 때마다 전체를 다시 계산·다시 그리면** 키 입력 14~61ms로 16ms를 넘었다(글 꺼내기 + 전체 계산 + 화면 전체 `Redraw()`).
- 그래서 `SqlHighlighter`(증분)로 바꿨다. 바뀐 줄부터 다시 훑다가 새 구간이 예전 구간과 같아지면 멈추고, 그 범위만 다시 칠한다.
  여러 줄 토큰(블록 주석·문자열) 한가운데를 고칠 때 그 토큰을 끝낼 글자(`*/`·따옴표 등)가 둘레에 없으면 둘레만 읽는다.
  결과: D2 실무형·D3 1만~10만 줄·D4-3 전부 주석에서 키 입력 3~12ms(강조 없는 AvalonEdit 3~8ms), 한 번 계산은 1ms 안팎(약 4천 자만 읽음).
  맨 앞 `/*`(D4-2)·가운데 `'`처럼 뒤쪽 색이 뒤집히는 고치기도 첫 `*/`·다음 따옴표에서 다시 맞아 1~21ms.
- 정확성: 무작위 고치기(조각 넣기·지우기) 세 DBMS × 수십만 번에서 증분 결과 = 처음부터 훑은 결과(`SqlHighlighterTests`, 무거운 반복은 한 번만 돌리고 저장소에는 4천 번).
- 측정하며 고친 것: 다시 칠할 범위가 줄 시작에 닿으면 AvalonEdit이 앞 줄까지 다시 만든다(앞 줄이 1MB 한 줄이면 키마다 2초) → 범위를 한 글자씩 안으로.
  테마 색을 줄마다 리소스에서 찾으면 스크롤이 한 화면 17ms → 테마가 바뀔 때만 정함(7.5~16.8ms, 강조 없음 6.8~14.4ms — 실행마다 흔들림).
  긴 주석 안 고치기는 처음에 따옴표·* 같은 글자가 둘레에 있으면 보통 길로 가서 10만 줄 주석에서 20ms → 토큰마다 끝낼 수 있는 것(주석은 */)만 보게 해 3.7ms.
- 남는 한계: **한 줄 1MB(D4-1)는 AvalonEdit·TextBox 모두 키 입력 2~4초** — 편집기 자체의 한계(그 줄을 통째로 다시 배치)라 강조와 무관하다.
  2만 자 넘는 줄은 칠하지 않는다(`MaxColoredLine`, 한 줄에 구간 수만 개면 AvalonEdit 줄 그리기가 제곱으로 느려짐).

### 키 — 결정 (2026-10-21 사용자: 제안대로, 헤드리스 비교 원본은 실행 폴더 `shots/poc-keys.md`)
| 키 | 지금 TextBox | AvalonEdit | 제안 | 결정 |
|---|---|---|---|---|
| Tab (여러 줄 선택) | 선택한 글을 탭 하나로 바꿈(글이 지워짐) | 줄마다 들여쓰기, 선택 유지 | AvalonEdit | AvalonEdit |
| Shift+Tab (여러 줄 선택) | 선택한 글을 탭 하나로 바꿈 | 줄마다 내어쓰기 | AvalonEdit | AvalonEdit |
| Shift+Tab (캐럿만) | 탭 넣음 | 그 줄 내어쓰기 | AvalonEdit | AvalonEdit |
| Enter (들여쓴 줄 끝) | 줄바꿈만 | 앞 줄 들여쓰기 유지 | AvalonEdit | AvalonEdit |
| Home | 줄 맨 앞 | 첫 글자 → 한 번 더 누르면 줄 맨 앞 | AvalonEdit | AvalonEdit |
| Ctrl+C·X (선택 없음) | 아무것도 안 함 | 줄 전체 복사·잘라내기 (`CutCopyWholeLine`) | AvalonEdit(끄려면 옵션) | AvalonEdit |
| Ctrl+D | 없음 | **줄 삭제** | 끔(실수로 줄이 지워짐 — 되돌리기는 됨) | 끔 — `SqlEditorSetup`이 줄 삭제 명령 연결을 뺌, 헤드리스에서 TextBox와 같음 |
| Insert | 겹쳐쓰기 전환 | 키는 먹지만 전환 안 함(`AllowToggleOverstrikeMode` 기본 끔) | 옵션을 켜 지금과 같게 | 옵션 켬 — 헤드리스에서 TextBox와 같음 |
| Ctrl+I | 없음 | 먹음(글은 그대로) | 그대로 | 그대로 |
| 같음 | Tab(선택 없음), Ctrl+Z·Y·A, Ctrl+Backspace·Delete, End, Ctrl+Home·End | | | |

Ctrl+V는 시험이 클립보드를 건드리지 않게 빼서 C 확인 때 함께 본다.

### 화면 — 결정 (2026-10-21)
- 줄 간격: Folderss 설정에는 없는 항목이라 **플러그인 설정 "SQL 편집기" 탭의 선택 설정**으로 둔다 — 기본은 글꼴 기본(13에서 15px), 16~32px에서 고른다(19px는 지금 SQL 탭과 같음).
  AvalonEdit 6.3에는 줄 간격 속성이 없어 같은 글꼴을 가리키는 합성 글꼴의 `LineSpacing`·기준선으로 정한다(`SqlEditorSetup.WithLineHeight` — 한글·이모지 대체 글꼴과 줄 번호가 따라옴을 헤드리스로 확인).
- (해결) 문자열색·주석색 대비와 테마 변경 반영 — 2-2단계 `SqlHighlightLogic`·`SqlEditorHighlighting`.

### 키워드 — 결정 (2026-10-21)
- 강조 대상과 모양은 **설정 "SQL 편집기" 탭에서 관리**한다: 키워드·자료형·문자열·주석마다 칠함·색(비우면 테마 색)·굵게·기울임, 지금 테마 바탕 대비 명암비 표시, 미리 보기.
  자료형(NUMBER·VARCHAR2·TEXT·NVARCHAR …)은 키워드와 따로 떼어(`SqlLexRules.Types`, `SqlSpanKind.DataType`) 기본은 키워드와 같은 색이다.
  함수 이름은 기본 목록에 넣지 않고, 필요하면 "더 칠할 낱말"(예: NVL DECODE)로 더한다. 열 이름과 겹치는 낱말은 "칠하지 않을 낱말"로 뺀다. 목록은 `Db/SqlKeywords.cs`.
- 긴 줄: 칠하지 않을 기준(기본 20,000자)도 같은 탭에서 바꾼다(1,000~1,000,000).
- 키워드가 아닌 것: 문자열·주석·따옴표 식별자(`"Select"`·`[select]`) 안, 바로 앞이 `.`·`:`·`@`·`&`인 낱말(`t.date`·`:from`·`@from`·`&var`).
  키워드와 같은 이름의 열(PostgreSQL `text` 등)은 키워드색으로 보인다(이름만 보고 판단 — 메타데이터 대조는 범위 밖).
- **PostgreSQL 함수 본문 `$$ … $$`는 통째로 문자열색**이다. 문장 나누기와 같은 기준(본문은 문자열 리터럴)이라 그렇다.
  DBeaver·pgAdmin처럼 본문 안을 SQL로 칠하려면 달러 따옴표 안을 한 번 더 훑어야 한다 — 보류(필요해지면 강조 대상 설정에 넣는다).

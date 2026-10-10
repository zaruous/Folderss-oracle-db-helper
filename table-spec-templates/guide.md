# DB Helper 테이블 정의서 양식(엑셀 템플릿) 작성 지침

> **대상**: 이 문서를 읽고 DB Helper용 테이블 정의서 양식(.xlsx)을 만들거나 고치는 사람과 LLM.
> **DB Helper**: [Folderss](https://github.com/zaruous/Folderss) 파일 관리자의 Oracle 플러그인([저장소](https://github.com/zaruous/Folderss-oracle-db-helper)). `도구 > 테이블 정의서 만들기`(또는 트리 오른쪽 메뉴)로 Oracle 데이터 사전을 읽어 이 양식에 채운 엑셀 정의서를 만든다.
> **원문(Markdown)**: [guide.md](guide.md) · **변수 목록(JSON)**: [variables.json](variables.json) · **예제**: [make_template.py](examples/make_template.py) · **검사 도구**: [check_template.py](examples/check_template.py)

*English summary for LLMs: An xlsx template is rendered by a Velocity-subset engine. Cells may contain `${var}` references and inline `#if/#foreach`. A row whose only non-empty cell is a single directive (`#foreach(...)`, `#if(...)`, `#elseif(...)`, `#else`, `#end`, `#set(...)`, `#sheet(...)`) is a row directive: rows between `#foreach` and `#end` repeat per item and directive rows are removed. `#sheet($table in $tables)` at the top of a sheet clones the sheet per item. Sheets whose names start with `_` are dropped from output. Save the .xlsx into the user template folder; validate with `check_template.py`.*

---

## 1. LLM 작업 순서 (요약)

1. 이 문서 전체를 읽는다. 특히 **3. 렌더링 규칙**, **4. 문법**, **5. 변수**, **8. 검증 체크리스트**.
2. 사용자가 원하는 모양을 정한다(표지 항목, 시트 구성, 열 목록의 칸, 부록 등). 모르는 것은 사용자에게 묻는다.
3. 양식 파일을 만든다.
   - 바탕 양식(사용자가 준 파일, 또는 DB Helper에서 `[복사해서 고치기]`로 꺼낸 기본 양식)이 있으면 **복사본**을 고친다. 원본은 덮어쓰지 않는다.
   - 없으면 [examples/make_template.py](examples/make_template.py)를 바탕으로 openpyxl로 새로 만든다.
4. **사용자 양식 폴더**에 `.xlsx`로 저장한다(2장). 파일 이름이 목록에 보이는 이름이 된다. 사용자가 다른 저장 위치를 말하면 그 위치에 저장하고, DB Helper에서 쓰려면 양식 폴더로 복사하거나 `[템플릿 파일] > [찾아보기…]`로 고르라고 알린다.
5. [examples/check_template.py](examples/check_template.py)로 검사하고 오류를 모두 고친다: `python check_template.py 양식.xlsx` (실행 방법은 8장)
6. 사용자에게 알린다: 파일 경로, 시트 구성, 쓴 변수, DB Helper에서 `[양식]` 목록을 펼치면 `내 양식 — 파일 이름`으로 보인다는 것.

---

## 2. 양식이 쓰이는 곳

- **사용자 양식 폴더**(Windows): `%LOCALAPPDATA%\Folderss\plugin-data\zaruous.folderss-db-helper\templates\`
  - DB Helper의 테이블 정의서 팝업 → `[양식 폴더]`로 열 수 있다. `[LLM 지침 복사]`를 누르면 이 폴더의 실제 경로가 들어간 요청 글이 복사된다.
  - 이 폴더의 `*.xlsx`가 `[양식]` 목록에 `내 양식 — {파일 이름(확장자 뺌)}`으로 보인다(목록을 펼칠 때마다 다시 읽음, 파일 이름 순).
  - `~$`로 시작하는 파일(엑셀 잠금 파일)과 숨김 파일은 무시한다. `.xlsm`·`.xls`는 쓰지 않는다 — **`.xlsx`만**.
- 다른 위치의 파일도 팝업의 `[템플릿 파일] > [찾아보기…]`로 쓸 수 있다.
- 엑셀에서 열어 둔 채로도 DB Helper가 읽는다 — 고치고 저장한 뒤 바로 `[만들기…]`로 확인할 수 있다.
- 기본 양식 두 가지(`기본`, `명세서형`)는 플러그인 코드가 만든다. 그대로 고칠 수는 없고 `[복사해서 고치기]`로 양식 폴더에 꺼내 고친다. 꺼낸 파일에는 `_변수 설명` 시트(문법·변수 표)가 들어 있다.

---

## 3. 렌더링 규칙 (엔진이 양식을 처리하는 방법)

엔진: C# + ClosedXML. 양식 파일을 읽어 시트 순서대로 처리하고 새 통합 문서로 저장한다.

### 3.1 시트

| 규칙 | 내용 |
|---|---|
| `_` 시트 | 이름이 `_`로 시작하는 시트는 결과에서 **빠진다** — 안내·메모·예시용 |
| 시트 지시문 | 시트의 **첫 지시문 줄**이 `#sheet($변수 in $목록)`이면 목록 항목마다 그 시트를 복사해 그린다. 항목이 없으면 시트가 빠진다 |
| 시트 이름 | 탭 이름도 템플릿이다. 반복 시트는 **`${table.sheetName}`을 권장**(31자 안·겹치지 않게 미리 고친 이름 — 목록의 링크 `${t.sheetName}`과 정확히 같아진다). 다른 꼴(`${table.owner}_${table.name}` 등)을 쓰면 결과 이름을 엔진이 `[]:*?/\'` → `_`, 31자로 자르기, 겹치면 ` (2)`로 고치므로 `${t.sheetName}` 링크와 **어긋날 수 있다**. 비거나 `History`면 `Sheet` |
| 템플릿 파일의 시트 이름 | 양식 파일 자체도 올바른 엑셀이어야 한다 — 모든 탭 이름(템플릿 글자 그대로, 예: `${table.sheetName}` 18자)이 **31자 이하**, `[]:*?/\` 없이. 길면 엑셀이 파일을 "복구"하며 이름을 바꾼다 |
| `#sheet` 위치 | 다른 내용보다 먼저, 시트에 한 번만. 보통 A1. 그 줄은 결과에서 지워진다(아래 내용이 한 줄 위로) |
| 모든 시트가 빠지면 | `빈 문서` 시트 하나만 남는다 |

### 3.2 줄 지시문 (행 반복·조건)

**한 줄(행)에 값이 있는 셀이 딱 하나**이고 그 셀 글자가 **지시문 하나뿐**이면 그 줄은 줄 지시문이다. 서식(색·테두리)만 있는 셀은 세지 않는다.

```
A열(보통)                       결과
#foreach($col in $table.columns)   ← 지워짐
${col.no} | ${col.name} | …        ← 열마다 한 줄씩 되풀이
#end                               ← 지워짐
#if(!$table.indexes)               ← 지워짐
(인덱스 없음)                        ← 인덱스가 없을 때만
#end                               ← 지워짐
```

- 지시문: `#foreach($x in 식)`, `#if(식)`, `#elseif(식)`, `#else`, `#end`, `#set($x = 식)`, `#sheet($x in 식)`. 중괄호 꼴(`#{else}`, `#{end}`, `#{if}(…)`)도 된다.
- 중첩 가능: `#foreach` 안의 `#if`, `#foreach` 안의 `#foreach`.
- 같은 줄의 다른 셀에 값이 있으면 줄 지시문이 **아니다** — 그 셀은 셀 템플릿으로 그려진다(인라인 지시문, 4장).
- 지시문 셀은 보통 A열에 두고 A열을 좁게(폭 2~3) 둔다. 기본 양식은 지시문 줄을 연노랑 바탕·갈색 기울임 글자로 칠해 눈에 띄게 한다(결과에는 남지 않음).

### 3.3 셀

- 글자에 `$`나 `#`이 있으면 템플릿으로 그린다. 참조·지시문이 없으면 그대로 둔다(`가격 $5`, `#1`은 글자).
- **셀 전체가 참조 하나**(`${col.no}`, `$table.numRows`)면 값의 형식을 그대로 넣는다 — 숫자 변수는 **숫자 셀**(서식 `#,##0` 등이 먹는다). 앞뒤에 글자가 붙으면 글자 셀.
- 결과가 빈 글자면 **빈 셀**.
- `'`로 시작하는 결과(`'N'` 같은 Oracle 기본값)도 그대로 보존된다.
- 셀 서식(글꼴·색·테두리·맞춤·숫자 서식·줄바꿈)은 템플릿 셀 그대로 복사된다.

### 3.4 수식

- 수식은 **`${`(또는 `$!{`)가 들어 있을 때만** 그린다 — `$A$1` 같은 절대 참조를 건드리지 않기 위해. 수식 안에서는 반드시 `${…}` 꼴을 쓴다.
- 예: 목록에서 테이블 시트로 가는 링크 `=HYPERLINK("#'${t.sheetName}'!A1","${t.name}")`, 표지로 돌아가는 링크 `=HYPERLINK("#'표지'!A1","← 표지")`.

### 3.5 줄을 따라가는 것 / 따라가지 않는 것

| 따라감(복사·조정) | 따라가지 않음(지원 안 함) |
|---|---|
| 셀 값·서식, 줄 높이(템플릿에서 높이를 정한 줄만), 숨긴 줄 | 조건부 서식, 데이터 유효성(드롭다운) |
| 병합 — **병합 범위의 줄이 결과에 연달아 함께 나올 때만** 다시 만든다(반복 줄 안의 가로 병합 OK, 지시문 줄을 걸치는 병합은 버려짐) | 그림·도형·차트(템플릿 위치에 그대로 남음 — 반복 영역에 두지 말 것) |
| 틀 고정 줄·인쇄 반복 제목 줄(지시문 줄이 빠진 만큼 위로) | 인쇄 영역(지워짐 — 줄 수가 바뀌므로) |
| 열 너비, 페이지 설정, 탭 색, 눈금선 표시 | 셀 메모(보장 안 함), 매크로 |

**줄 높이와 줄바꿈**: 엔진은 템플릿에서 높이를 직접 정한 줄만 그 높이로 복사하고, 나머지는 높이를 정하지 않는다 → 엑셀이 열 때 내용에 맞춰 높이를 정한다. 그래서
- 내용이 여러 줄이 될 수 있는 반복 줄(설명·주석·조건 칸)은 **줄 높이를 정하지 말고** 셀에 `줄 바꿈(wrap_text)`만 켠다.
- **병합한 칸은 엑셀이 높이를 맞추지 않는다** → 줄바꿈이 필요한 내용은 병합하지 말고 열 너비를 넓힌다. 병합은 짧은 값(이름·코드)에만 쓴다.
- 서명란처럼 높이가 정해져야 하는 줄은 높이를 정해 둔다(그대로 복사된다).

---

## 4. 문법 (Velocity 일부)

### 4.1 참조

| 쓰는 법 | 뜻 |
|---|---|
| `$name`, `${name}` | 변수 |
| `$a.b.c`, `${a.b.c}` | 속성(이름 대소문자 무시) |
| `$!{a.b}`, `$!a` | 값이 없으면 빈 글자(이 엔진의 변수는 늘 값이 있어 거의 필요 없음) |
| `$list.size()`, `$list.size` | 목록 개수(인자 없는 호출은 속성으로 본다). 같은 뜻: `length`, `count`. 비었는지: `isEmpty`(= `empty`). 글자에도 `length`가 된다 |
| `${table.name}_백업` | 이름 바로 뒤에 글자가 오면 `${…}`로 감싼다. `$table.name님`은 된다(한글은 이름이 아님) |
| `\$`, `\#` | `$`, `#` 글자 |

- 변수 이름은 **영문·숫자·`_`만**(영문·`_`로 시작).
- **없는 변수나 오타는 쓴 글자 그대로 결과에 남는다**(예: `${col.nmae}`). 결과에 `$`가 남아 있으면 오타다.

### 4.2 지시문 (셀 안에서도, 줄 지시문으로도)

| 쓰는 법 | 뜻 |
|---|---|
| `#if(식)…#elseif(식)…#else…#end` | 조건 |
| `#foreach($x in $list)…#end` | 반복. 안에서 `$foreach.count`(1부터)·`$foreach.index`(0부터)·`$foreach.hasNext`·`$foreach.first`·`$foreach.last`, `$velocityCount`(1부터) |
| `#set($x = 식)` | 변수 정하기(바깥 범위에 이미 있으면 그 값을 바꿈) |
| `#{else}`, `#{end}` | 글자가 바로 붙을 때: `#if($col.isPk)PK#{else}-#end`. **`#else`·`#end`는 인자를 받지 않는다** — `#else(확인 불가)`의 `(확인 불가)`는 그냥 글자지만, 헷갈리지 않게 뒤에 글자가 오면 늘 `#{else}`·`#{end}`로 쓴다 |
| `## 주석`, `#* 주석 *#` | 결과에 남지 않음 |

셀 안 예: `#if($col.isFk)→ ${col.fkRef}#end`, `#if($con.type == "R")→ ${con.target}#elseif($con.type == "C")${con.condition}#end`,
`#foreach($c in $table.columns)$c.name#if($foreach.hasNext), #end#end`.

**중첩 반복과 `$foreach`**: `$foreach`는 **가장 안쪽** 반복(`#foreach` 또는 `#sheet`)을 가리킨다. `#sheet` 시트 안의 셀에서 `#foreach`를 쓰면 그 셀 안에서는 셀 안 반복의 순번이 된다. 바깥 순번이 필요하면 안쪽 반복 전에 `#set($tableNo = $foreach.count)`로 담아 두거나, 테이블 순번은 `$table.no`를 쓴다. 반복 변수와 `$foreach`는 그 반복이 끝나면 사라진다.

### 4.3 식

- 값: `$참조`, `"글자"`·`'글자'`, 숫자, `true`·`false`·`null`
- 연산자: `!`·`not`, `&&`·`and`, `||`·`or`, `==` `!=` `<` `<=` `>` `>=`(`eq ne lt le gt ge`), `+ - * / %`, 괄호. 글자에 `+`는 이어 붙이기.
- 참·거짓: `null`·`false`·빈 글자·빈 목록·`0`은 거짓, 나머지는 참. → `#if($table.indexes)`는 "인덱스가 있으면", `#if(!$table.indexes)`는 "없으면".
- 비교: 숫자끼리는 수로, 그 밖은 글자로(`$col.no == "1"`도 참).

### 4.4 지원하지 않음

`#macro`, `#parse`, `#include`, `#break`, `#stop`, 인자 있는 메서드 호출(`$s.substring(0,3)`), 목록·범위·맵 리터럴(`[1..3]`, `{}`), 사용자 정의 도구.

---

## 5. 변수

모든 값은 null이 아니다(없으면 빈 글자 `""`). 숫자 표시가 있는 변수는 셀 전체에 하나만 쓰면 숫자 셀이 된다. 기계용 목록: [variables.json](variables.json).

### 5.1 문서·접속 (`$doc`, `$db`)

| 변수 | 뜻 | 예 |
|---|---|---|
| `$doc.title` | 문서 제목(비우면 "테이블 정의서") | 주문 관리 테이블 명세서 |
| `$doc.project` | 프로젝트 | 주문 관리 시스템 |
| `$doc.system` | 시스템/모듈 | 주문 · 결제 |
| `$doc.author` | 작성자 | 홍길동 |
| `$doc.version` | 문서 버전 | 1.0 |
| `$doc.date` | 작성일 = **스키마 기준일**(yyyy-MM-dd) | 2026-10-10 |
| `$doc.description` | 문서 설명 | 1차 오픈 기준 |
| `$doc.schemas` | 대상 스키마(쉼표) | SHOP, HR |
| `$doc.tableCount` | 대상 테이블 수 *(숫자)* | 12 |
| `$doc.generatedAt` | 만든 시각 | 2026-10-10 14:30 |
| `$db.name` | 접속 이름 | 개발 DB |
| `$db.user` | 접속 사용자 | shop |
| `$db.host` / `$db.port` / `$db.service` | 호스트 / 포트 *(숫자)* / 서비스명 | db / 1521 / orcl |
| `$db.address` | user@host:port/service | shop@db:1521/orcl |
| `$db.version` | DBMS **제품 이름 + 버전 전체**(버전 숫자만 따로 있는 변수는 없음). 권한 등으로 못 읽으면 빈 글자 → `#if($db.version)${db.version}#{else}(확인 불가)#end`처럼 대신할 글자를 둔다 | Oracle Database 19c Enterprise Edition 19.0.0.0.0 |

### 5.2 목록

| 변수 | 항목 | 뜻 |
|---|---|---|
| `$tables` | `$table` | 대상 테이블(스키마 이름 → 테이블 이름 순). 보통 `#foreach($t in $tables)` 또는 `#sheet($table in $tables)` |
| `$table.columns` | `$col` | 열(COLUMN_ID 순) |
| `$table.indexes` | `$idx` | 인덱스(이름 순) |
| `$table.constraints` | `$con` | 제약 조건(P → U → R → C, 시스템 이름의 NOT NULL 검사는 빠짐) |
| `$table.foreignKeys` | `$con` | 외래 키(R)만 |
| `$table.checks` | `$con` | 검사 조건(C)만 |
| `$codes` | `$code` | 공통 코드 후보(검사 조건 `열 IN (…)`의 값) |

반복 변수 이름은 자유다(`$t`, `$table`, `$c`, `$col` …). 아래 표의 `$table`·`$col` 등은 그 항목 종류를 뜻한다.

### 5.3 테이블 (`$table`)

| 변수 | 뜻 |
|---|---|
| `$table.no` | 순번 *(숫자, 1부터)* |
| `$table.owner` | 스키마 |
| `$table.name` | 테이블 이름(물리명) |
| `$table.qualifiedName` | 스키마.이름 |
| `$table.sheetName` | 이 테이블의 시트 이름(31자 안, 겹치지 않음) — `#sheet` 탭 이름과 목록 링크에 쓴다 |
| `$table.comment` | 테이블 주석 전체 |
| `$table.logicalName` | 주석에서 나눈 논리명(6장) |
| `$table.description` | 주석에서 논리명을 뺀 나머지 |
| `$table.type` / `$table.typeLabel` | TABLE·VIEW / 테이블·뷰 |
| `$table.status` | VALID·INVALID |
| `$table.numRows` | 통계 행 수 *(숫자, 통계가 없으면 빈 글자)* |
| `$table.numRowsText` | 1,000 꼴 행 수(없으면 빈 글자) |
| `$table.lastAnalyzed` | 통계 날짜 |
| `$table.created` / `$table.lastDdl` | 만든 날 / 마지막 DDL |
| `$table.tablespace` | 테이블스페이스 |
| `$table.pkName` / `$table.pkColumns` | 기본 키 이름 / 열(쉼표) |
| `$table.columnCount` | 열 수 *(숫자)* |
| `$table.columns`, `$table.indexes`, `$table.constraints`, `$table.foreignKeys`, `$table.checks` | 목록(5.2) |

### 5.4 열 (`$col`)

| 변수 | 뜻 | 예 |
|---|---|---|
| `$col.no` | 열 순서 *(숫자)* | 1 |
| `$col.name` | 열 이름(물리명) | ORDER_ID |
| `$col.comment` | 열 주석 전체 | 상태: R=접수, D=배송 |
| `$col.logicalName` | 논리명 | 상태 |
| `$col.description` | 설명(주석 나머지) | R=접수, D=배송 |
| `$col.typeLabel` | 형식 전체 | VARCHAR2(10 CHAR) |
| `$col.dataType` / `$col.length` | 형식 이름 / 괄호 안 | VARCHAR2 / 10 CHAR |
| `$col.nullable` | Y·N | N |
| `$col.notNull` | NOT NULL이면 Y | Y |
| `$col.isNullable` | 참·거짓 | false |
| `$col.pk` / `$col.pkOrder` / `$col.isPk` | Y / 기본 키 안 순서 *(숫자)* / 참·거짓 | Y / 1 / true |
| `$col.fk` / `$col.fkRef` / `$col.isFk` | Y / 가리키는 열 / 참·거짓 | Y / SHOP.CUSTOMERS.NO / true |
| `$col.key` | PK·FK·UK·IX(겹치면 쉼표) | FK,IX |
| `$col.default` | 기본값 | SYSDATE |

### 5.5 인덱스 (`$idx`)

| 변수 | 뜻 |
|---|---|
| `$idx.no` / `$idx.name` | 순번 *(숫자)* / 이름 |
| `$idx.columns` | 열(순서대로, DESC 포함) |
| `$idx.unique` / `$idx.uniqueness` | 고유면 Y / UNIQUE·NONUNIQUE |
| `$idx.kind` | PK(기본 키가 쓰는 인덱스)·UNIQUE·INDEX |
| `$idx.type` / `$idx.status` | NORMAL 등 / VALID 등 |

### 5.6 제약 조건 (`$con`)

| 변수 | 뜻 |
|---|---|
| `$con.no` / `$con.name` | 순번 *(숫자)* / 이름 |
| `$con.type` / `$con.typeLabel` | P·U·R·C / 기본 키·고유·외래 키·검사 |
| `$con.columns` | 열(쉼표) |
| `$con.reference` / `$con.referenceColumns` | 외래 키가 가리키는 테이블 / 열 |
| `$con.target` | 참조 대상 `OWNER.TABLE.COL`(열이 여럿이면 `OWNER.TABLE(A, B)`) |
| `$con.deleteRule` / `$con.updateRule` | 외래 키 삭제 시(NO ACTION·CASCADE·SET NULL) / 갱신 시(Oracle은 늘 NO ACTION) |
| `$con.condition` | 검사 조건 |
| `$con.indexName` | 기본 키·고유 제약이 쓰는 인덱스 |
| `$con.status` | ENABLED·DISABLED |

### 5.7 공통 코드 (`$code`)와 반복 정보 (`$foreach`)

| 변수 | 뜻 |
|---|---|
| `$code.no` | 순번 *(숫자)* |
| `$code.group` / `$code.code` | 코드 그룹(열 이름) / 코드 값 |
| `$code.meaning` | 의미 — **늘 빈 글자**(사람이 채움). 빈 칸으로 두는 열에 쓴다 |
| `$code.usage` | 사용 테이블.열 |
| `$code.table` / `$code.column` / `$code.constraint` | 테이블 / 열 / 검사 조건 이름 |
| `$foreach.count` / `$foreach.index` | `#foreach`·`#sheet` 안 순번 1부터 / 0부터 *(숫자)* |
| `$foreach.hasNext` / `$foreach.first` / `$foreach.last` | 다음 항목이 있음 / 첫 / 끝 |

---

## 6. 데이터 해석 규칙 (값이 어떻게 정해지는지)

- **논리명·설명**: Oracle에는 논리명이 없어 **주석을 나눈다**.
  여러 줄이면 첫 줄이 논리명, 나머지가 설명 · 한 줄이면 첫 `:`·`|`·` - ` 앞이 논리명 · `이름(설명)` 꼴이면 괄호 앞 · 나눌 곳이 없으면 주석 전체가 논리명, 설명은 빈 글자.
  예: `상태: R=접수, D=배송` → 상태 / R=접수, D=배송 · `사용 여부(Y/N)` → 사용 여부 / Y/N.
  설명 칸은 비기 쉬우므로 `#if($col.description)${col.description}#elseif($col.isFk)${col.fkRef} 참조#end`처럼 대신할 값을 두면 좋다.
- **키 표시 `$col.key`**: PK(기본 키) · FK(외래 키) · UK(고유 제약 또는 고유 인덱스, PK 열 제외) · IX(그 밖 인덱스에 든 열, PK 열 제외).
- **공통 코드 `$codes`**: 검사 조건이 `열 IN ('A','B')` 꼴이면 값마다 한 항목. 그 밖의 검사 조건(`SAL > 0`)은 후보가 아니다.
- **행 수**: 통계(ALL_TABLES.NUM_ROWS) 값이라 실제 건수와 다를 수 있다. 통계가 없으면 빈 글자.
- **뷰**: 사용자가 `[뷰 포함]`을 켰을 때만 대상이 된다. 뷰에는 인덱스·제약 조건이 없다(빈 목록) → `#if(!$table.indexes)` 같은 "없음" 처리를 둔다.
- **권한**: 접속 계정이 볼 수 있는 객체만(ALL_* 뷰).

---

## 7. 만드는 방법

### 7.1 openpyxl로 새로 만들기 (권장 순서)

[examples/make_template.py](examples/make_template.py) 전체 예제. 핵심만:

```python
from openpyxl import Workbook
from openpyxl.styles import Font, PatternFill, Alignment, Border, Side

wb = Workbook()
cover = wb.active
cover.title = "표지"
cover["B2"] = "${doc.title}"                     # 문서 제목
cover["B4"], cover["C4"] = "프로젝트", "${doc.project}"
cover["B5"], cover["C5"] = "스키마 기준일", "${doc.date}"

lst = wb.create_sheet("목록")
lst.append(["", "No", "테이블", "설명"])          # 1행: 머리글
lst["A2"] = "#foreach($t in $tables)"             # 줄 지시문: 그 줄에 이 셀 하나만
lst["B3"] = "${foreach.count}"
lst["C3"] = '=HYPERLINK("#\'${t.sheetName}\'!A1","${t.name}")'
lst["D3"] = "${t.comment}"
lst["A4"] = "#end"

ts = wb.create_sheet("${table.sheetName}")       # 탭 이름도 템플릿
ts["A1"] = "#sheet($table in $tables)"            # 시트 맨 위 지시문
ts["B2"] = "${table.name}"
ts["A4"] = "#foreach($col in $table.columns)"
ts["B5"], ts["C5"], ts["D5"] = "${col.no}", "${col.name}", "${col.typeLabel}"
ts["A6"] = "#end"

wb.create_sheet("_안내")["A1"] = "이 시트는 결과에서 빠진다"
wb.save("내_양식.xlsx")
```

주의:
- **지시문 셀이 있는 줄에는 다른 값을 넣지 않는다.** 그 줄의 다른 셀에 서식(채우기)은 넣어도 된다.
- 반복할 줄(데이터 줄)에 테두리·맞춤·숫자 서식을 넣어 두면 반복된 줄에 그대로 복사된다.
- 열 너비는 `ws.column_dimensions["C"].width = 24`처럼 템플릿에서 정한다(결과에 그대로).
- 병합은 반복 줄 안에서 가로로만 쓰는 것이 안전하다(`ws.merge_cells("D5:F5")`).
- 틀 고정은 머리 줄 아래로: `ws.freeze_panes = "A6"`(지시문 줄이 위에 있으면 엔진이 그만큼 올려 준다).
- 글꼴은 한글이 보이는 `맑은 고딕`을 권장한다.

### 7.2 기존 양식 고치기

- DB Helper에서 `[복사해서 고치기]`로 꺼낸 기본 양식(또는 사용자가 준 파일)을 **복사해서** 고친다.
- openpyxl로 열어 고치면 그림·도형·차트가 사라질 수 있다. 그런 요소가 있으면 사용자에게 알리고 엑셀에서 직접 넣게 한다.
- 꺼낸 기본 양식의 `_변수 설명`·`_작성 가이드` 시트는 그대로 두어도 결과에서 빠진다.

### 7.3 패턴 모음

| 하고 싶은 것 | 템플릿 |
|---|---|
| 테이블마다 시트 | 시트 A1 `#sheet($table in $tables)`, 탭 이름 `${table.sheetName}` |
| 한 시트에 모든 테이블을 차례로 | `#foreach($t in $tables)` 줄 → 테이블 머리 줄 → `#foreach($c in $t.columns)` 줄 → 열 줄 → `#end` → 빈 줄 → `#end` |
| 목록 → 테이블 시트 링크 | `=HYPERLINK("#'${t.sheetName}'!A1","${t.name}")` |
| 없을 때 "(없음)" 줄 | `#if(!$table.foreignKeys)` 줄 → "(없음)" 줄 → `#end` 줄 |
| 기본 키 열만 굵게 | 불가(조건부 서식 미지원) → 별도 칸에 `${col.pk}` 표시 |
| 열 이름 쉼표 목록 | 셀 `#foreach($c in $table.columns)$c.name#if($foreach.hasNext), #end#end` |
| 외래 키 참조 표시 | `#if($col.isFk)→ ${col.fkRef}#end` |
| 짝수 줄 줄무늬 | 불가(줄마다 다른 서식 미지원) — 줄 테두리로 대신 |
| 숫자 서식 | 셀 전체를 `${table.numRows}`로 두고 셀 서식 `#,##0` |

---

## 8. 검증 체크리스트

[examples/check_template.py](examples/check_template.py)가 아래 대부분을 검사한다.

```
pip install openpyxl
python check_template.py 양식.xlsx                    # variables.json을 아래 순서로 찾는다
python check_template.py 양식.xlsx variables.json     # 변수 목록 파일을 직접 지정
```

- variables.json 찾는 순서: 두 번째 인자 → 스크립트와 같은 폴더 → 스크립트의 상위 폴더(저장소 구조) → 현재 폴더 → [인터넷 주소](variables.json)에서 내려받기(네트워크 필요). 인터넷이 안 되면 [variables.json](variables.json)을 스크립트 옆에 같이 받아 둔다.
- 결과 마지막 줄 `검사한 시트 N개 · 오류 0개 · 경고 M개`. 오류가 있으면 종료 코드 1. 경고는 동작은 하지만 의도와 다를 수 있는 것.
- 검사하지 못하는 것: 그림·도형 위치, 줄 높이·병합의 보기 좋음, 실제 데이터로 그렸을 때의 모양 — 사용자에게 DB Helper에서 한 번 만들어 보게 한다.

- [ ] `.xlsx`로 저장했고 사용자 양식 폴더에 있다. 파일 이름이 `~$`로 시작하지 않는다.
- [ ] 시트마다 `#foreach`/`#if`/`#sheet`와 `#end`의 짝이 맞는다(`#sheet`는 `#end` 없음).
- [ ] 지시문 셀은 그 줄에서 값이 있는 유일한 셀이다(같은 줄에 다른 값 없음).
- [ ] `#sheet`는 시트의 첫 지시문이고 그 위에 내용이 없다.
- [ ] 모든 참조의 변수·속성 이름이 5장(또는 variables.json)에 있다. 반복 변수는 그 `#foreach`/`#sheet` 안에서만 쓴다.
- [ ] 수식 안 참조는 `${…}` 꼴이다.
- [ ] 반복 시트의 탭 이름은 `${table.sheetName}`이다(목록 링크 `${t.sheetName}`과 맞음). 모든 탭 이름(템플릿 글자 그대로)이 31자 이하이며 `[]:*?/\` 를 쓰지 않는다.
- [ ] 여러 줄이 될 수 있는 반복 칸은 줄 바꿈만 켜고 줄 높이를 정하지 않았으며 병합하지 않았다.
- [ ] 바깥 반복의 순번이 필요한 곳은 `$table.no`나 `#set`으로 담은 값을 쓴다(안쪽 `#foreach` 안의 `$foreach`는 안쪽 순번).
- [ ] 안내용 시트는 이름을 `_`로 시작했다.
- [ ] 조건부 서식·데이터 유효성·그림을 반복 영역에 기대지 않았다.

## 9. 자주 하는 실수

| 증상(결과 파일) | 원인 | 고치기 |
|---|---|---|
| `${col.nmae}`가 그대로 보임 | 변수 오타 | 5장 이름으로 |
| `#foreach(...)` 글자가 결과에 남음 | 같은 줄에 다른 값이 있어 줄 지시문이 아님 | 지시문 셀만 있는 줄로 옮김 |
| DB Helper가 "템플릿 오류 — '시트'!A5: #foreach에 맞는 #end가 없습니다" | 짝 안 맞음 | `#end` 줄 추가 |
| 링크가 동작하지 않음 | 수식에 `$t.sheetName`(중괄호 없음)을 씀, 또는 시트 이름을 직접 적음 | `${t.sheetName}` |
| 숫자가 글자로 들어감 | 셀에 `${table.numRows}행`처럼 글자를 붙임 | 숫자 셀은 참조 하나만, 단위는 옆 칸/머리글로 |
| 반복 줄의 병합이 사라짐 | 병합이 지시문 줄을 걸침 | 데이터 줄 안에서만 병합 |
| 결과에 안내 시트가 남음 | 이름이 `_`로 시작하지 않음 | `_안내`처럼 |
| 목록 링크를 누르면 "참조가 올바르지 않음" | 반복 시트 탭 이름이 `${table.sheetName}`이 아니라 결과 이름이 달라짐 | 탭 이름을 `${table.sheetName}`로 |
| 긴 설명이 한 줄로 잘려 보임 | 병합 칸이거나 줄 높이를 정해 둠 | 병합 풀기·줄 높이 지우기·줄 바꿈 켜기 |
| 표지에 DBMS 칸이 비어 있음 | 계정 권한으로 버전을 못 읽음 | `#if($db.version)…#{else}(확인 불가)#end` |
| 열 표의 No가 1부터 다시 시작하지 않음/엉뚱함 | 셀 안 `#foreach` 안에서 바깥 `$foreach`를 기대 | `$col.no`(열 순서)나 `#set`으로 담은 값 |

---

이 지침은 DB Helper 저장소의 `docs/table-spec-templates/`에 있는 원본에서 만들어진다. 엔진·변수가 바뀌면 함께 고친다(변수 목록은 단위 테스트로 코드와 맞춰 확인한다).

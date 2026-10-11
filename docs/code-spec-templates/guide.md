# DB Helper GCM(공통 코드) 정의서 양식(엑셀 템플릿) 작성 지침

> **대상**: 이 문서를 읽고 DB Helper용 GCM(공통 코드) 정의서 양식(.xlsx)을 만들거나 고치는 사람과 LLM.
> **DB Helper**: [Folderss](https://github.com/zaruous/Folderss) 파일 관리자의 Oracle 플러그인([저장소](https://github.com/zaruous/Folderss-oracle-db-helper)). `도구 > GCM 정의서 만들기`로 **사용자가 고른 SQL**을 실행해 그 결과(코드 그룹·코드)를 이 양식에 채운 엑셀 정의서를 만든다.
> **원문(Markdown)**: [guide.md](guide.md) · **변수 목록(JSON)**: [variables.json](variables.json) · **엔진 규칙·문법·검사 도구**: [테이블 정의서 양식 지침](../table-spec-templates/guide.md)(같은 엔진)

*English summary for LLMs: A GCM (common code) spec is rendered from the user's own SQL. The plugin does not parse the SQL; it only maps result column aliases (TABLE_NAME, CODE, CODE_NAME required; GROUP_NAME, GROUP_DESC, CODE_DESC, SORT_NO, USE_YN optional) and keeps every other column as an extra attribute (`$code.attrs.COLUMN`, `$code.attrValues`, `$code.attrText`). The xlsx template engine, row directives (`#foreach/#if/#end`, `#sheet($group in $groups)`), `_` sheets and the checker script are identical to the table-spec guide; only the variables (`$doc`, `$db`, `$source`, `$groups`/`$group`, `$codes`/`$code`) differ. Save the .xlsx into the user template folder `templates\gcm`.*

---

## 1. LLM 작업 순서 (요약)

1. 이 문서 전체를 읽는다. 특히 **3. 별칭 계약**, **5. 변수**, **8. 검증 체크리스트**. 엔진 규칙과 문법은 [테이블 정의서 양식 지침의 3장·4장](../table-spec-templates/guide.md)과 같다 — 그 두 장도 읽는다.
2. 사용자가 원하는 모양을 정한다(표지 항목, 그룹마다 시트인지 한 표인지, 코드 표의 칸, 추가 속성을 어떻게 보일지). 모르는 것은 사용자에게 묻는다.
3. 양식 파일을 만든다.
   - 바탕 양식(사용자가 준 파일, 또는 DB Helper에서 `[복사해서 고치기]`로 꺼낸 기본 양식)이 있으면 **복사본**을 고친다. 원본은 덮어쓰지 않는다.
   - 없으면 테이블 정의서 지침의 [make_template.py](../table-spec-templates/examples/make_template.py)를 바탕으로 openpyxl로 새로 만든다(변수만 이 문서의 것으로 바꾼다).
4. **사용자 양식 폴더**(2장)에 `.xlsx`로 저장한다. 파일 이름이 목록에 보이는 이름이 된다.
5. [check_template.py](../table-spec-templates/examples/check_template.py)로 검사하고 오류를 모두 고친다 — 변수 목록으로 **이 폴더의 variables.json**을 준다: `python check_template.py 양식.xlsx variables.json`
6. 사용자에게 알린다: 파일 경로, 시트 구성, 쓴 변수, 양식이 기대하는 별칭(SQL의 AS 절 이름), DB Helper에서 `[양식]` 목록을 펼치면 `내 양식 — 파일 이름`으로 보인다는 것.

---

## 2. 양식이 쓰이는 곳

- **사용자 양식 폴더**(Windows): `%LOCALAPPDATA%\Folderss\plugin-data\zaruous.folderss-db-helper\templates\gcm\`
  - 테이블 정의서 양식 폴더(`templates`)의 **하위 폴더 `gcm`**이다. 두 정의서의 양식이 섞이지 않게 나누어 두었다 — GCM 양식을 `templates` 바로 아래에 두면 테이블 정의서 목록에 나타나고 GCM 목록에는 보이지 않는다.
  - DB Helper의 GCM 정의서 팝업 → `[양식 폴더]`로 열 수 있다. `[LLM 지침 복사]`를 누르면 이 폴더의 실제 경로가 들어간 요청 글이 복사된다.
  - 이 폴더의 `*.xlsx`가 `[양식]` 목록에 `내 양식 — {파일 이름(확장자 뺌)}`으로 보인다(목록을 펼칠 때마다 다시 읽음, 파일 이름 순). `~$`로 시작하는 파일과 숨김 파일은 무시한다. **`.xlsx`만**.
- 다른 위치의 파일도 팝업의 `[템플릿 파일] > [찾아보기…]`로 쓸 수 있다.
- 기본 양식 두 가지(`기본`, `단일 시트형`, 6장)는 플러그인 코드가 만든다. `[복사해서 고치기]`로 양식 폴더에 꺼내 고친다. 꺼낸 파일에는 `_변수 설명` 시트(문법·변수 표)가 들어 있다.

---

## 3. 별칭 계약 (SQL 결과 열 → 변수)

플러그인은 사용자 SQL을 해석하지 않는다. **결과 열의 이름(별칭)** 이 아래 계약과 맞는지만 본다(대소문자 무시, 앞뒤 공백 무시). 환경마다 다른 GCM 테이블·열 이름은 SQL의 `AS` 절로 맞춘다.

| 별칭 | 필수 | 뜻 | 예 | 변수 |
|---|---|---|---|---|
| `TABLE_NAME` | **필수** | 코드 그룹(코드 테이블) 식별자 — 그룹을 나누는 기준. 옛 별칭 GROUP_ID도 받음 | ORDER_STATUS | `$group.id`, `$code.group` |
| `CODE` | **필수** | 코드 값 | A | `$code.code` |
| `CODE_NAME` | **필수** | 코드 명칭 | 접수 | `$code.name` |
| `GROUP_NAME` | 선택 | 그룹 명칭(행마다 반복되면 첫 값) | 주문 상태 | `$group.name`, `$code.groupName` |
| `GROUP_DESC` | 선택 | 그룹 설명(첫 값) | 주문 진행 단계 | `$group.description` |
| `CODE_DESC` | 선택 | 코드 설명 | 고객이 주문을 넣은 상태 | `$code.description` |
| `SORT_NO` | 선택 | 정렬 순서(있으면 그룹 안에서 이 값으로 정렬, 없으면 SQL 순서) | 1 | `$code.sortNo` |
| `USE_YN` | 선택 | 사용 여부(Y/N, 1/0, true/false — 비우면 사용) | Y | `$code.useYn`, `$code.isUsed` |

- 필수 별칭이 빠지면 정의서를 만들지 않는다("빠진 필수 별칭: CODE_NAME").
- **계약에 없는 열은 모두 추가 속성**이 된다(결과 열 순서, 원래 이름). 양식에서는 세 가지로 쓴다:
  - `$code.attrs.열이름` — 열 이름으로 꺼낸 값(이름 대소문자 무시). 예: `${code.attrs.PLANT_CD}`. 그 열이 없으면 글자 그대로 남으므로 양식이 기대하는 열 이름을 사용자에게 알린다.
  - `$code.attrValues` — 값 목록(`$source.attrNames`와 같은 순서). 열 이름을 모르는 범용 양식에서 `#foreach($v in $code.attrValues)` 로 돈다.
  - `$code.attrText` — `PLANT_CD=P1; LANG=ko`처럼 이름=값을 `; `로 이은 글자(값이 빈 속성은 뺌). 한 칸에 모두 보일 때.
- **코드 테이블마다 열 이름이 다른 구조(이름/값 짝 열)**: 정의 테이블이 열 이름을 정하고 데이터 테이블이 값을 담는 범용 코드 테이블은 **이름 열과 값 열을 짝으로 나열**한다 — 추가 열 `X`와 `X_NAME`이 함께 있으면 행마다 `X_NAME`의 값이 속성 이름, `X`의 값이 속성 값이 된다(이름이 비어 있는데 값이 있으면 값 열 이름 `DATA_2`가 속성 이름, 둘 다 비면 뺀다). 예: `KEY_1_NAME`/`KEY_1`, `DATA_1_NAME`/`DATA_1` … → 그룹마다 그 그룹의 열 이름이 속성이 된다. 평범한 SELECT 열 나열이라 **어떤 DBMS에서나 같은 SQL**로 된다(본보기는 플러그인 창의 [본보기 SQL] 버튼 — 사내 구조라 문서에는 싣지 않는다). 그래서 그룹 시트에서는 `$group.attrNames`(이 그룹에 있는 속성 이름)·`$code.groupAttrValues`(같은 순서의 값)를 쓰고, 전체 표에서는 `$source.attrNames`·`$code.attrValues`를 쓴다. 펼친 열은 `$source.expandedColumnsText`에 남는다(`KEY_1_NAME→KEY_1, …`).
- **JSON 객체 열(선택, PostgreSQL 등 JSON 함수가 있는 DBMS)**: 추가 속성 열의 비어 있지 않은 값이 모두 `{"이름": 값, …}` 꼴이면 그 열 대신 키 하나하나가 추가 속성이 된다(값이 객체·배열이면 JSON 글자 그대로, `null`은 뺌). 같은 결과를 내지만 DBMS 고유 함수(`jsonb_build_object`)를 쓰므로 이식성이 필요하면 위의 짝 열 방식을 권한다. 예: [examples/madmtbl_postgresql_json.sql](examples/madmtbl_postgresql_json.sql).
- 그룹은 `TABLE_NAME`가 같은 행을 처음 나온 순서로 묶는다. 그룹 명칭·설명은 그 그룹에서 처음 비어 있지 않은 값.
- `SORT_NO`가 있으면 그룹 안에서 그 값으로 정렬(숫자로 읽히면 수로, 아니면 글자로, 빈 값은 맨 뒤). 없으면 SQL의 순서 그대로 — 순서가 중요하면 SQL에 `ORDER BY`를 둔다.

예시 SQL(별칭만 맞추면 어떤 테이블이든 된다):

```sql
SELECT g.grp_cd      AS TABLE_NAME,
       g.grp_nm      AS GROUP_NAME,
       g.grp_desc    AS GROUP_DESC,
       c.cd          AS CODE,
       c.cd_nm       AS CODE_NAME,
       c.cd_desc     AS CODE_DESC,
       c.sort_seq    AS SORT_NO,
       c.use_yn      AS USE_YN,
       c.plant_cd    AS PLANT_CD        -- 계약에 없는 열 → 추가 속성
  FROM gcm_group g JOIN gcm_code c ON c.grp_cd = g.grp_cd
 ORDER BY g.grp_cd, c.sort_seq
```

---

## 4. 렌더링 규칙과 문법

엔진은 테이블 정의서와 **같다**. 규칙(시트·줄 지시문·셀·수식·줄 높이)과 문법(참조·지시문·식)은 [테이블 정의서 양식 지침의 3장·4장](../table-spec-templates/guide.md)을 그대로 따른다. 여기서는 GCM 양식에 해당하는 이름만 적는다.

| 테이블 정의서에서 | GCM 정의서에서 |
|---|---|
| `#sheet($table in $tables)` + 탭 이름 `${table.sheetName}` | `#sheet($group in $groups)` + 탭 이름 **`${group.sheetName}`** |
| 목록 링크 `=HYPERLINK("#'${t.sheetName}'!A1","${t.name}")` | `=HYPERLINK("#'${g.sheetName}'!A1","${g.id}")` |
| `#foreach($col in $table.columns)` | `#foreach($code in $group.codes)` 또는 모든 코드를 한 표에 `#foreach($code in $codes)` |
| `#if(!$tables)` "(대상 테이블이 없습니다)" | `#if(!$groups)` "(그룹이 없습니다)", `#if(!$codes)` "(코드가 없습니다)" |

- 반복 줄의 설명·추가 속성 칸은 **줄 높이를 정하지 말고** 줄 바꿈(wrap_text)만 켠다(엑셀이 내용에 맞춰 높이를 정한다). 병합한 칸은 높이를 맞추지 않으므로 긴 글이 올 칸은 병합하지 않는다.
- 표지의 SQL 본문(`${source.sql}`)처럼 여러 줄이 들어갈 병합 칸은 줄 높이를 넉넉히 정해 둔다(기본 양식은 10줄을 병합해 둔다).
- 안내용 시트는 이름을 `_`로 시작한다(결과에서 빠짐). 모든 탭 이름(템플릿 글자 그대로, 예: `${group.sheetName}` 18자)은 **31자 이하**.

---

## 5. 변수

모든 값은 null이 아니다(없으면 빈 글자 `""`). 숫자 표시가 있는 변수는 셀 전체에 하나만 쓰면 숫자 셀이 된다. 기계용 목록: [variables.json](variables.json).

### 5.1 문서·접속 (`$doc`, `$db`)

| 변수 | 뜻 | 예 |
|---|---|---|
| `$doc.title` | 문서 제목(비우면 "공통 코드 정의서") | 공통 코드 정의서 |
| `$doc.project` | 프로젝트(시스템) 이름 | MES |
| `$doc.system` | 시스템/모듈 | 공통 |
| `$doc.author` | 작성자 | 홍길동 |
| `$doc.version` | 문서 버전 | 1.0 |
| `$doc.date` | 작성일(yyyy-MM-dd) | 2026-10-11 |
| `$doc.description` | 문서 설명 | 운영 DB 기준 |
| `$doc.groupCount` / `$doc.codeCount` | 그룹 수 / 코드 수 *(숫자)* | 12 / 180 |
| `$doc.generatedAt` | 만든 시각(yyyy-MM-dd HH:mm) | 2026-10-11 14:30 |
| `$db.name` | 접속 이름 | 개발 DB |
| `$db.user` | 접속 사용자 | MES |
| `$db.host` / `$db.port` / `$db.service` | 호스트 / 포트 *(숫자)* / 서비스명 | localhost / 1521 / xe |
| `$db.address` | user@host:port/service | mes@localhost:1521/xe |
| `$db.version` | DBMS 제품 이름 + 버전 전체. 권한 등으로 못 읽으면 빈 글자 → `#if($db.version)${db.version}#{else}(확인 불가)#end` | Oracle Database 19c Enterprise Edition 19.0.0.0.0 |

### 5.2 출처 (`$source`) — 어떤 SQL을 언제 실행했는지

| 변수 | 뜻 | 예 |
|---|---|---|
| `$source.name` | SQL 이름(저장한 SQL 파일 이름, 없으면 빈 글자) | GCM_운영.sql |
| `$source.sql` | 실행한 SQL 본문(여러 줄) | SELECT … |
| `$source.fetchedAt` | 조회 시각(yyyy-MM-dd HH:mm) | 2026-10-11 14:29 |
| `$source.rowCount` | 조회한 행 수 *(숫자)* | 180 |
| `$source.columns` | 결과 열 이름(쉼표로 이음) | TABLE_NAME, CODE, CODE_NAME, PLANT_CD |
| `$source.attrNames` | 추가 속성 열 이름 목록 — `#foreach($a in $source.attrNames)`로 돈다(머리글을 동적으로 만들 때) | |
| `$source.attrNamesText` | 추가 속성 열 이름(쉼표로 이음) | PLANT_CD, LANG |
| `$source.expandedColumnsText` | 열 대신 이름→값으로 펼친 열(이름/값 짝 열 `X_NAME→X`, JSON 객체 열, 쉼표로 이음, 없으면 빈 칸) | KEY_1_NAME→KEY_1, DATA_1_NAME→DATA_1 |

### 5.3 목록

| 변수 | 항목 | 뜻 |
|---|---|---|
| `$groups` | `$group` | 코드 그룹(TABLE_NAME이 처음 나온 순서). 보통 `#foreach($g in $groups)` 또는 `#sheet($group in $groups)`. `$groups.size()`는 개수 |
| `$group.codes` | `$code` | 그룹의 코드(정렬 규칙은 3장) |
| `$codes` | `$code` | 모든 코드를 그룹 순서대로 편 목록(한 표에 모두 넣을 때). `$code.no`는 전체 순번 |
| `$source.attrNames` | 글자 | 추가 속성 이름 전체 |
| `$group.attrNames` | 글자 | 이 그룹에 있는 추가 속성 이름(JSON 열을 펼치면 그룹마다 다름) |
| `$code.attrValues` | 글자 | 추가 속성 값(`$source.attrNames`와 같은 순서) |
| `$code.groupAttrValues` | 글자 | 추가 속성 값(`$group.attrNames`와 같은 순서) |

반복 변수 이름은 자유다(`$g`, `$group`, `$c`, `$code` …). 아래 표의 `$group`·`$code`는 그 항목 종류를 뜻한다.

### 5.4 그룹 (`$group`)

| 변수 | 뜻 | 예 |
|---|---|---|
| `$group.no` | 그룹 순번 *(숫자, 1부터)* | 1 |
| `$group.id` | 그룹 식별자(TABLE_NAME) | ORDER_STATUS |
| `$group.name` / `$group.description` | 그룹 명칭 / 설명(GROUP_NAME · GROUP_DESC의 첫 값) | 주문 상태 / 주문 진행 단계 |
| `$group.sheetName` | 이 그룹의 시트 이름(31자 안, 겹치지 않음) — `#sheet` 탭 이름과 목록의 링크에 쓴다 | ORDER_STATUS |
| `$group.codeCount` | 그룹의 코드 수 *(숫자)* | 5 |
| `$group.codes` | 그룹의 코드 목록(5.3) | |
| `$group.attrNames` / `$group.attrNamesText` | 이 그룹의 코드가 가진 추가 속성 이름 목록 / 쉼표로 이은 글자 — JSON 열을 펼치면 그룹마다 다르다(`#foreach($a in $group.attrNames)`로 그룹 시트의 머리글을 만들 때) | AREA_CODE, COLOR_HEX_CODE |

### 5.5 코드 (`$code`)

| 변수 | 뜻 | 예 |
|---|---|---|
| `$code.no` | 코드 순번 *(숫자)* — `$group.codes`에서는 그룹 안 1부터, `$codes`에서는 전체 순번 | 1 |
| `$code.group` / `$code.groupName` | 그룹 식별자 / 그룹 명칭(한 표 양식에서 그룹을 표시할 때) | ORDER_STATUS / 주문 상태 |
| `$code.code` / `$code.name` | 코드 값 / 코드 명칭 | A / 접수 |
| `$code.description` | 코드 설명(CODE_DESC) | 고객이 주문을 넣은 상태 |
| `$code.sortNo` | 정렬 순서(SORT_NO, 글자 그대로) | 1 |
| `$code.useYn` / `$code.isUsed` | 사용 여부(USE_YN 글자 그대로) / `#if`에 쓰는 참·거짓(비우거나 Y·1·TRUE·T·사용이면 참) | Y / true |
| `$code.attrs.열이름` | 추가 속성 값(계약에 없는 열, 이름 대소문자 무시). `$code.attrs`만 쓰면 사전 전체 | `${code.attrs.PLANT_CD}` → P1 |
| `$code.attrValues` | 추가 속성 값 목록(`$source.attrNames`와 같은 순서) | |
| `$code.groupAttrValues` | 추가 속성 값 목록(`$group.attrNames`와 같은 순서 — 그룹 시트에서 동적 열을 만들 때) | |
| `$code.attrText` | 추가 속성을 `이름=값; …`으로 이은 글자(값이 빈 속성은 뺌) | PLANT_CD=P1; LANG=ko |

### 5.6 반복 정보 (`$foreach`)

| 변수 | 뜻 |
|---|---|
| `$foreach.count` / `$foreach.index` | `#foreach`·`#sheet` 안 순번 1부터 / 0부터 *(숫자)* |
| `$foreach.hasNext` / `$foreach.first` / `$foreach.last` | 다음 항목이 있음 / 첫 / 끝 |

`$foreach`는 **가장 안쪽** 반복을 가리킨다. `#sheet($group in $groups)` 시트 안의 `#foreach($code in $group.codes)` 안에서는 코드 순번이다. 그룹 순번은 `$group.no`, 코드 순번은 `$code.no`를 쓰면 헷갈리지 않는다.

---

## 6. 기본 양식 2종

둘 다 `[복사해서 고치기]`로 꺼내 바탕으로 쓸 수 있다. 글꼴 맑은 고딕, 지시문 줄은 연노랑 바탕·갈색 기울임 글자(결과에는 남지 않음).

### 6.1 기본 — 표지 · 그룹 목록 · 그룹별 시트 (`GCM정의서_기본.xlsx`)

| 시트 | 내용 |
|---|---|
| `표지` | `${doc.title}`, 프로젝트, 시스템/모듈, 대상 DB `${db.name} (${db.address})`, DBMS `${db.version}`, 그룹 수·코드 수, 버전·작성자·작성일·설명. **출처**: SQL 이름 `${source.name}`, 조회 시각 `${source.fetchedAt}`, 행 수 `${source.rowCount}`, 결과 열 `${source.columns}`, SQL 본문 `${source.sql}`(10줄 병합·줄 바꿈). 개정 이력 표 |
| `목록` | `#foreach($g in $groups)` — No `${g.no}`, 테이블명(`=HYPERLINK("#'${g.sheetName}'!A1","${g.id}")` 로 그 그룹 시트 링크), 그룹명, 설명, 코드 수. 그룹이 없으면 `(그룹이 없습니다)` |
| `${group.sheetName}` | A1 `#sheet($group in $groups)`. 그룹 머리(ID·명칭·설명·코드 수), `← 목록` 링크, 코드 표 `#foreach($code in $group.codes)`: No·코드·코드명·설명·정렬 `${code.sortNo}`·사용 `${code.useYn}`·추가 속성 `${code.attrText}`. 코드가 없으면 `(코드가 없습니다)` |
| `_변수 설명` | 문법·변수 표(결과에서 빠짐) |

### 6.2 단일 시트형 — 모든 코드 한 표 (`GCM정의서_단일시트.xlsx`)

| 시트 | 내용 |
|---|---|
| `표지` | 6.1과 같다 |
| `코드` | 머리글(No·테이블명·그룹명·코드·코드명·설명·정렬·사용·추가 속성) 뒤 `#foreach($code in $codes)`로 코드마다 한 줄 — `${code.no}`, `${code.group}`, `${code.groupName}`, `${code.code}`, `${code.name}`, `${code.description}`, `${code.sortNo}`, `${code.useYn}`, `${code.attrText}`. 코드가 없으면 `(코드가 없습니다)`. 머리글 줄 틀 고정·인쇄 반복 |
| `_변수 설명` | 6.1과 같다 |

추가 속성을 열마다 따로 보이고 싶으면 `${code.attrText}` 칸 대신 열 이름을 정해 `${code.attrs.PLANT_CD}`, `${code.attrs.LANG}`처럼 칸을 둔다(양식이 그 열 이름을 기대한다는 것을 사용자에게 알린다).

---

## 7. 출력 형식

GCM 정의서 팝업은 세 가지로 내보낸다. **양식은 엑셀일 때만 쓴다** — JSON·CSV는 양식과 무관하게 고정된 구조로 쓴다.

| 형식 | 내용 |
|---|---|
| 엑셀(.xlsx) | 고른 양식에 5장의 변수를 채운다 |
| JSON | `{ doc, db, source, groups: [ { id, name, description, codes: [ { code, name, description, sortNo, useYn, isUsed, attrs: { 열이름: 값 } } ] } ] }`. `doc`은 title·project·system·author·version·date·description·groupCount·codeCount·generatedAt, `db`는 name·user·address·version, `source`는 name·sql·fetchedAt·rowCount·columns(목록)·attrNames(목록). 한글은 그대로(이스케이프 안 함), 들여쓰기, 값이 없으면 빈 글자 |
| CSV | 머리글 `TABLE_NAME, GROUP_NAME, GROUP_DESC, CODE, CODE_NAME, CODE_DESC, SORT_NO, USE_YN` + 추가 속성 열(결과 순서). 코드마다 한 줄(그룹 순서), 줄 끝 CRLF, UTF-8 BOM(엑셀이 한글을 바로 읽음). 쉼표·따옴표·줄바꿈이 든 값은 따옴표로 감싼다 |

---

## 8. 검증 체크리스트

[check_template.py](../table-spec-templates/examples/check_template.py)(테이블 정의서 지침의 검사 도구)가 아래 대부분을 검사한다. 변수 목록으로 **이 폴더의 variables.json**을 꼭 준다(주지 않으면 테이블 정의서 변수로 검사해 `$groups`·`$code.attrs`가 오류로 나온다).

```
pip install openpyxl
python check_template.py 양식.xlsx variables.json
```

- [ ] `.xlsx`로 저장했고 사용자 양식 폴더 **`templates\gcm`** 에 있다(`templates` 바로 아래가 아니다). 파일 이름이 `~$`로 시작하지 않는다.
- [ ] 시트마다 `#foreach`/`#if`/`#sheet`와 `#end`의 짝이 맞는다(`#sheet`는 `#end` 없음).
- [ ] 지시문 셀은 그 줄에서 값이 있는 유일한 셀이다(같은 줄에 다른 값 없음).
- [ ] `#sheet`는 시트의 첫 지시문이고 그 위에 내용이 없다.
- [ ] 모든 참조의 변수·속성 이름이 5장(또는 variables.json)에 있다. 반복 변수는 그 `#foreach`/`#sheet` 안에서만 쓴다. `$code`는 `$group.codes` 또는 `$codes`를 도는 반복 안에서만 쓴다.
- [ ] `$code.attrs.열이름`을 썼으면 그 열 이름이 사용자 SQL의 별칭에 있다(없으면 글자 그대로 남는다). 열 이름을 모르는 양식은 `$code.attrText` 또는 `$code.attrValues`를 쓴다.
- [ ] 수식 안 참조는 `${…}` 꼴이다.
- [ ] 반복 시트의 탭 이름은 `${group.sheetName}`이다(목록 링크 `${g.sheetName}`과 맞음). 모든 탭 이름(템플릿 글자 그대로)이 31자 이하이며 `[]:*?/\` 를 쓰지 않는다.
- [ ] 여러 줄이 될 수 있는 반복 칸(설명·추가 속성)은 줄 바꿈만 켜고 줄 높이를 정하지 않았으며 병합하지 않았다.
- [ ] 그룹·코드가 0개일 때도 그려진다(`#if(!$groups)`·`#if(!$codes)`로 "(없습니다)" 줄을 둔다).
- [ ] 안내용 시트는 이름을 `_`로 시작했다.
- [ ] 조건부 서식·데이터 유효성·그림을 반복 영역에 기대지 않았다.

## 9. 자주 하는 실수

| 증상(결과 파일) | 원인 | 고치기 |
|---|---|---|
| `${code.attrs.PLANT_CD}`가 그대로 보임 | 사용자 SQL 결과에 `PLANT_CD` 열이 없음 | SQL에 `AS PLANT_CD`를 더하거나 `${code.attrText}`로 |
| `${code.nmae}`가 그대로 보임 | 변수 오타 | 5장 이름으로 |
| 양식이 `[양식]` 목록에 없음 | `templates` 바로 아래에 두었음(테이블 정의서 목록에 보임) | `templates\gcm`으로 옮김 |
| "빠진 필수 별칭: CODE_NAME" | SQL 결과 열 이름이 계약과 다름 | SQL의 `AS` 절을 3장 별칭으로 |
| 그룹 시트 링크가 "참조가 올바르지 않음" | 반복 시트 탭 이름이 `${group.sheetName}`이 아님 | 탭 이름을 `${group.sheetName}`로 |
| 코드가 뒤섞여 보임 | `SORT_NO`가 없고 SQL에 `ORDER BY`도 없음 | SQL에 `ORDER BY` 또는 `SORT_NO` 별칭 |
| 코드 표의 No가 1부터 다시 시작하지 않음 | `$codes`를 돌면서 `$code.no`(전체 순번)를 씀 | 그룹 안 순번은 `$group.codes`를 돌며 `$code.no`, 또는 `$foreach.count` |
| 긴 설명이 한 줄로 잘려 보임 | 병합 칸이거나 줄 높이를 정해 둠 | 병합 풀기·줄 높이 지우기·줄 바꿈 켜기 |

---

이 지침은 DB Helper 저장소의 `docs/code-spec-templates/`에 있는 원본에서 만들어진다. 엔진·변수가 바뀌면 함께 고친다.

**단위 테스트로 확인하는 것**(저장소 `tests/MyPlugin.Tests`):
- `CodeSpecTemplateTests` — 기본 양식 2종을 실제 데이터로 그려 시트 구성·값·빈 그룹 처리를 본다.
- `CodeSpecGuideTests` — `variables.json`의 변수가 모델(`CodeSpecModel.Context`)이 넣는 값과 정확히 같은지, 5장이 모든 변수를 적었는지, 2장의 폴더와 3장의 별칭을 모두 적었는지.

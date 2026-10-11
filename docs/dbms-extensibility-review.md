# DBMS 확장성 검수 — GCM 정의서와 그 바탕 계층

작성일 2026-10-11. 대상: `src/MyPlugin` 가운데 GCM(공통 코드) 정의서 기능(`Db/CodeSpecQueries.cs`, `Doc/CodeSpecModel.cs`, `Ui/CodeSpec*.cs`)과 그것이 기대는 세션(`Db/DbSession.cs`)·데이터 사전(`Db/OracleMetadata.cs`, `Db/TableSpecQueries.cs`, `Db/TableDescription.cs`)·접속(`OracleConnections.cs`, `Ui/ConnectionManager*.cs`, `Ui/ShellLogic.cs`, `Ui/DbHelperView.cs`)·값 표시(`Db/ValueFormatter.cs`)·문장 분류(`Db/SqlScript.cs`).

질문: "PostgreSQL·MSSQL·MySQL 같은 다른 DBMS를 추가할 때 어디가 걸리고, 어떻게 구조화해야 하는가."

## 0. 요약(핵심 지적)

1. **DBMS 고유 SQL이 네 곳에 흩어져 있다** — 세션(`DbSession`: DUAL 트랜잭션 확인·ORA 번호·OracleCommand 설정), 데이터 사전(`OracleMetadata`·`TableSpecQueries`·`TableInfo`: ALL_* 뷰·ROWNUM·LISTAGG·DECODE·SYS_CONTEXT), 접속(`OracleConnectionStore.BuildConnectionString`), 그리고 새 기능(`CodeSpecQueries.ProbeSql`의 ROWNUM — 이번에 seam을 둠). 공급자 하나를 추가하려면 지금은 이 네 곳을 모두 `if (oracle)`로 분기해야 한다. → `IDbProvider`(루트) 아래 `ISqlDialect`·`IMetadataProvider`·`IErrorClassifier`·`IColumnReader`로 모은다(3절).
2. **`DbSession`이 ODP.NET 형식에 직접 의존한다**(`OracleConnection`·`OracleCommand`·`OracleDataReader`·`OracleTransaction`·`OracleException`·`INullable` — 1.1절 표). 세션의 잠금·커서·트랜잭션 로직 자체는 중립이므로, 공급자 의존 부분만 `ISessionBehavior`(명령 설정·트랜잭션 확인·경고·끊김 판정)로 떼어 내면 된다. 가장 큰 파일이고 테스트가 가장 두터우므로(SQLite 시험 1,200여 건) **마지막 단계**에 한다.
3. **접속 프로필이 Oracle 전제다** — `OracleConnectionProfile.ServiceName`·`Port = 1521`과 `Validate`의 "서비스명을 입력하세요", 연결 문자열은 `OracleConnectionStringBuilder` 하나. DBMS 종류 필드(`Provider`, 기본값 `"oracle"`)를 넣으면 기존 설정 JSON은 그대로 읽힌다(System.Text.Json은 없는 속성을 기본값으로, 모르는 속성은 무시). 다만 프로필을 **필드마다 손으로 복사하는 곳이 4군데**(`ShellLogic.SessionCopy`·`ProfileChanged`, `ConnectionManagerLogic.Clone`·`SameProfile`) 있어 필드를 더하면 빠뜨리기 쉽다 → 복사·비교를 한 곳으로 모으고 왕복 테스트를 둔다(4절).
4. **GCM 새 기능은 대체로 중립이다** — 별칭 계약·그룹 묶기·JSON·CSV·설정·SQL 파일은 DBMS를 모른다. 중립이 아닌 곳: (a) `ProbeSql`의 ROWNUM(이번에 `ISqlDialect`로 고침), (b) `CodeSpecDialog.Run`이 Oracle 전용 `TableSpecQueries.LoadServerVersionAsync`(PRODUCT_COMPONENT_VERSION)를 부름, (c) `CodeSpecModel.Context/Json`의 `$db.host/port/service`와 `OracleConnectionProfile` 매개변수, (d) `CodeSpecLogic.LlmSqlPrompt`의 "Oracle SELECT 문" 문구(5절).
5. **값 읽기·형식 이름이 Oracle 기준이다** — `ValueFormatter.Format`의 `OracleDecimal`·`OracleTimeStampTZ` 등 분기, `DbSession.OracleTypeName/OracleReadMode`(ODP.NET `GetDataTypeName` → "NUMBER" 되돌리기), `ResultColumn` 라벨의 "NUMBER(p,s)"·"VARCHAR2(n)". 다른 공급자는 `GetValue` + `Convert.ToString`으로도 돌아가므로(지금도 SQLite가 그 길) 당장 막히진 않지만, PostgreSQL `numeric`·`timestamptz`, MSSQL `datetime2`·`money`가 들어오면 표시 품질이 떨어진다 → 공급자별 `IColumnReader`(읽기 모드·형식 이름 정규화·형식 값 표시)로 분리.

**이번에 적용한 것(위험 없음)**: `src/MyPlugin/Db/SqlDialect.cs` 신설(`ISqlDialect`·`OracleDialect`·`GenericDialect`·`SqlDialects.For(DbConnection)`), `DbSession.Dialect` 속성, `CodeSpecQueries.ProbeSql(sql, dialect)`·`ProbeAsync`가 세션 방언 사용, 테스트 3건(8절).

## 1. DBMS 고유 코드 목록

표기: 파일:줄 — 내용 — 왜 걸리는가 — 어디로 갈 것인가.

### 1.1 세션 — `src/MyPlugin/Db/DbSession.cs`

| 파일:줄 | 내용 | 왜 걸리는가 | 갈 곳 |
|---|---|---|---|
| 135, 610 | `CancelErrorNumber = 1013`, `IsCancellation`의 `OracleException.Number` | 취소 판정이 ORA-01013 하나. PG는 `57014`(SqlState), MSSQL은 `SqlException.Number == 0`+메시지, MySQL은 1317 | `IErrorClassifier.IsCancellation(Exception)` |
| 138, 919-930, 964 | `CompileErrorWarning = 24344`, `Connection_InfoMessage(OracleInfoMessageEventArgs)`, `ReadCompileErrors`→`OracleMetadata.CompileErrors` | PL/SQL 컴파일 경고는 Oracle 고유. PG `RAISE NOTICE`·MSSQL `PRINT`는 다른 이벤트 형식 | `ISessionBehavior.AttachWarnings(DbConnection, Action<int>)` + `IMetadataProvider.CompileErrors`(없는 공급자는 null) |
| 140, 189, 199 | `OracleTransactionProbe = "SELECT DBMS_TRANSACTION.LOCAL_TRANSACTION_ID FROM DUAL"`, `TransactionProbeSql` | 블록 안 커밋 여부를 묻는 문장. PG는 `txid_current_if_assigned()`, MSSQL은 `@@TRANCOUNT` | `ISqlDialect.TransactionProbeSql`(null = 모름) — 이미 `internal set`으로 열려 있어 옮기기 쉽다 |
| 144, 1553, 1571, 1601, 1624-1631 | `BrokenErrorNumbers`(ORA 28, 603, 3113 …), `BrokenErrorNumber`, `OracleCode("ORA-…")`, `ErrorCodeOf("ORA-"/"TNS-")`, `FirstLine`의 ORA-06550·ORA-02091 특수 처리 | 끊김 판정·오류 문구 다듬기가 모두 ORA 번호. PG는 SqlState `08***`, `57P01`; MSSQL은 Number −2·233·10054 … | `IErrorClassifier.IsBroken / Describe / CodeOf` — `DescribeError`는 껍데기만 두고 공급자에 위임 |
| 186-194, 206-230 | 생성자의 `connection as OracleConnection`·`InfoMessage +=`, `CreateOracle`, `UseInBandBreak`(`OracleConfiguration.DisableOOB`) | 연결 생성 경로가 Oracle 정적 메서드. 공급자별 프로세스 전역 설정은 공급자가 알아야 한다 | `IDbProvider.CreateConnection(profile, password, timeout)` + `IDbProvider.ConfigureProcess()`(한 번) — `DbSession`은 `DbConnection`만 받는다(지금도 그렇다) |
| 283-285 | `OpenAsync`의 `oracle.AutoCommit = false` | ODP.NET 전용 속성 | `ISessionBehavior.AfterOpen(DbConnection)` |
| 378, 1128, 1148-1164 | `reader is OracleDataReader` → `OracleReadMode(GetDataTypeName)`, 아니면 모두 `ColumnRead.Value` | 공급자 특정 값(큰 NUMBER·TZ)을 잃지 않으려는 분기. Npgsql도 `GetProviderSpecificValue`가 있다 | `IColumnReader.ReadModeOf(typeName)` — 공급자마다 표 |
| 634-713 | `OracleTypeName`(ODP.NET 이름 → SQL 형식 이름), `NormalizeOracleColumn`(ROWID·NUMBER(38,127) 보정) | 형식 이름 정규화가 Oracle 표 하나 | `IColumnReader.DescribeColumn(reader, i, schemaRow)` → `ResultColumn` |
| 1102, 1108 | `IsUnreadableValue`의 `ex is OracleException`, `DisposeValue`의 `Oracle…INullable` | 값 하나 못 읽는 오류와 Null 싱글턴 판정이 Oracle 형식 | `IColumnReader.IsUnreadable(ex)`, `IColumnReader.DisposeValue(object)` |
| 1211-1220 | `CreateCommand`: `BindByName`, `InitialLOBFetchSize`, `InitialLONGFetchSize = -1`, `CommandTimeout = 0` | OracleCommand 전용. MSSQL은 `CommandTimeout = 0` 가능, SQLite는 두면 안 됨(이미 주석에 있음) | `ISessionBehavior.PrepareCommand(DbCommand)` |
| 1307 | `AfterFailedEnd`의 `transaction is OracleTransaction` | 실패한 트랜잭션 객체를 버릴지가 공급자 동작 | `ISessionBehavior.TransactionDiesOnFailure` (bool) |
| 1388 | `CloseConnection`의 `InfoMessage -=` | 위 경고 부착의 짝 | `ISessionBehavior.DetachWarnings` |
| `Execute` 795-830(DDL 자동 커밋, LOCK TABLE, PL/SQL 블록 뒤 서버 확인) | DDL은 "트랜잭션 있으면 먼저 커밋 후 트랜잭션 없이 실행" — Oracle 의미를 모든 공급자에서 흉내 냄 | PG는 DDL이 트랜잭션 안에 들어간다(롤백 가능). 지금 구조는 "DDL 전 자동 커밋"을 강제하므로 PG에서는 사용자 기대와 다를 수 있다 | `ISqlDialect.DdlAutoCommits`(bool)로 분기. 당장 바꾸지 말고 공급자 추가 때 결정 |

중립인 것(그대로 둘 것): `SemaphoreSlim` 잠금과 `EnterNow/RunAsync`, 커서 `Lookahead`·`HasMore`, `Cancel()`, `CloseAsync/Dispose` 순서, `QueryTableAsync` 뼈대, `RunDb`, `Unwrap`. 즉 `DbSession`의 60% 이상은 그대로 쓴다.

### 1.2 데이터 사전 — `OracleMetadata.cs` · `TableSpecQueries.cs` · `TableDescription.cs(TableInfo)`

| 파일:줄 | 내용 | 왜 걸리는가 | 갈 곳 |
|---|---|---|---|
| `Db/OracleMetadata.cs:11-15` `SqlQuery` | 바인드 이름 ':' 없이, `BindByName` 전제 | PG/MySQL은 `@name`, MSSQL `@name`, SQLite `$name`/`@name`. SQL 본문에는 `:owner`가 박혀 있다(`:owner`, `:name`, `:pattern`, `:limit`, `:t0…`) | `SqlQuery`는 그대로 두고 **SQL 본문은 공급자가 만든다**. 공통 도우미 `ISqlDialect.Param(name)` → ":owner"/"@owner" |
| 21, 78-87, 141-155, 303-307, 371-391 | `SchemaInfo.OracleMaintained`, `KnownSystemSchemas`, `Schemas(hasOracleMaintained)`, `SearchSchemas`, `IsInvalidIdentifier`(ORA-00904 → 11g 대체 쿼리) | "내장 스키마" 판정과 11g 폴백이 호출자(`DbTreePanel.cs:1157-1179`, `DbHelperView.cs:522-532 HasOracleMaintained`, `TableSpecDialog.cs:483-489`)까지 번져 있다 | `IMetadataProvider.Schemas()`가 **스스로** 폴백을 처리하고 `SchemaInfo.IsSystem`만 돌려준다. `HasOracleMaintained` 상태·`QueryWithOmAsync`는 Oracle 공급자 내부로 들어간다 |
| 99, `TableDescription.cs:99,107-133` | `SYS_CONTEXT('USERENV','CURRENT_SCHEMA')`, `ALL_SYNONYMS`·`DB_LINK` | 현재 스키마·동의어 해석은 Oracle 개념. PG는 `current_schema()`·search_path, MSSQL은 `SCHEMA_NAME()`·synonyms 있음, MySQL은 스키마 = 데이터베이스 | `IMetadataProvider.CurrentSchema()`, `ResolveTable(owner, name)`(동의어 없는 공급자는 1·4 순위만) |
| 115-136 | `ALL_ERRORS` 컴파일 오류 | Oracle 고유 | `IMetadataProvider.CompileErrors` → null 허용 |
| 161-235, 235-260 | `ALL_OBJECTS`·`ALL_TABLES.NUM_ROWS`·`ALL_TAB_COLUMNS`·`ALL_CONSTRAINTS`·`BIN$%` 휴지통 | 트리·열·PK 조회 전부. PG는 `information_schema`+`pg_catalog`, MSSQL `sys.*`, MySQL `information_schema` | `IMetadataProvider.GroupCounts / Objects / Columns`. 결과 형식(`DbObjectInfo`, `ColumnInfo`)은 이미 중립 — 그대로 계약으로 삼는다 |
| 272-300, `TableSpecQueries.cs:190` | `FormatColumnType("VARCHAR2"… CHAR_USED·BYTE)` | Oracle 열 형식 조립 | `IMetadataProvider.FormatColumnType(IDataRecord)` 또는 공급자별 `ColumnTypeFormatter` |
| 408-419 | `Limit`: `SELECT * FROM (…) WHERE ROWNUM <= :limit` | 행 제한 문법 | `ISqlDialect.Limit(sql, n)` — 이번에 둔 `LimitOne`의 일반형. `GenericDialect`는 `LIMIT`, MSSQL은 `TOP`/`OFFSET FETCH` |
| `Db/TableSpecQueries.cs:64-66, 130, 145-148, 154` | `ChunkSize = 500`(IN 1000 한도), `LISTAGG … WITHIN GROUP`, `CHR(31)`, `DECODE` | Oracle 함수. PG `string_agg`, MSSQL `STRING_AGG`(2017+), MySQL `GROUP_CONCAT` | `IMetadataProvider.TableSpec.Summaries/Columns/Indexes/Constraints(owner, names)` — 공급자가 통째로 만든다. `Chunks`·`SplitList`·`Read*`는 중립이므로 공용으로 |
| 231-262 | `PRODUCT_COMPONENT_VERSION`, `" - Production"` 다듬기, `LoadServerVersionAsync` | 표지의 DBMS/버전. PG `version()`, MSSQL `@@VERSION`, MySQL `VERSION()`. `DbConnection.ServerVersion`으로도 대부분 됨 | `ISqlDialect.ServerVersionSql` + `IMetadataProvider.ReadServerVersion`, 또는 단순히 `connection.ServerVersion` + 공급자 이름 접두 |
| `Db/TableDescription.cs:201-247` | `TableInfo.Indexes/Constraints`의 `LISTAGG`·`DECODE`·`GENERATED = 'GENERATED NAME'` | F4 테이블 정보. 위와 같은 이유 | `IMetadataProvider.Indexes / Constraints(owner, name)` |

중립인 것: `SqlQuery`(이름만 바꾸면 됨), `SchemaInfo`·`DbObjectInfo`·`ColumnInfo`·`SearchHit`·`TableDescription`·`DescribedColumn`·`IndexDescription`·`ConstraintDescription`, `TableKey`, `Chunks`, `SplitList`, `Resolve(candidates)`, `TreeGroups` 대응. 즉 **결과 모델은 이미 공급자 계약으로 쓸 수 있다**. 문제는 SQL 문자열이 모델과 같은 파일에 있는 것뿐이다.

### 1.3 접속 — `OracleConnections.cs` · `Ui/ConnectionManager*.cs` · `Ui/ShellLogic.cs` · `Ui/DbHelperView.cs`

| 파일:줄 | 내용 | 왜 걸리는가 | 갈 곳 |
|---|---|---|---|
| `OracleConnections.cs:12-31` | `OracleConnectionProfile { Host, Port = 1521, ServiceName, UserId, ProtectedPassword, ReadOnly, Color }` | DBMS 종류 필드가 없다. `ServiceName`은 Oracle 용어(PG·MySQL은 database, MSSQL은 database+instance) | `ConnectionProfile`에 `Provider`(문자열, 기본 "oracle") 추가. `ServiceName`은 JSON 이름을 지키고 의미만 "데이터베이스/서비스"로 넓힌다(4절) |
| 44 | DPAPI `Entropy = "Folderss.OracleHelper.v1"` | 문자열에 Oracle이 있으나 **바꾸면 저장된 비밀번호를 못 푼다** | 그대로 둔다(이름만 상수 주석으로 설명) |
| 73-98 | `Validate`: "서비스명을 입력하세요", 포트 1~65535 | 필수 필드가 공급자마다 다르다(SQLite 파일 경로는 호스트·포트 없음) | `IDbProvider.Validate(profile)` 또는 `ConnectionRules.For(provider)` — `Validate`는 공통(이름·중복)만 남긴다 |
| 104-121 | `BuildConnectionString`: `OracleConnectionStringBuilder`, `host:port/service`, `Pooling = false` | 빌더가 공급자마다 다르다(`NpgsqlConnectionStringBuilder`, `SqlConnectionStringBuilder`) | `IDbProvider.BuildConnectionString(profile, password, timeout)` |
| `Ui/ConnectionManagerDialog.cs:2, 653, 1012-1019` | `using Oracle…`, 새 접속 `Port = 1521`, 접속 테스트 `new OracleConnection` + `ServerVersion` | 화면이 공급자 형식을 직접 안다 | 공급자 콤보 + `IDbProvider.DefaultPort`, 테스트는 `provider.CreateConnection(...)` |
| `Ui/ConnectionManagerLogic.cs:282-287, 338-345` | `Address` "user@host:port/service", `TestSuccess` "접속 성공 · Oracle …" | 표시 문구가 Oracle 고정 | `IDbProvider.DisplayName`을 앞에 붙인다. `Address`는 공급자별 형식(`IDbProvider.Address(profile)`) |
| `Ui/ConnectionManagerLogic.cs:135-160` | `Clone`, `SameProfile` — 필드를 하나씩 복사·비교 | 필드 추가 시 빠뜨리기 쉬움 | 프로필 클래스에 `Copy()`·`SameAs()`를 두고 네 곳이 그것을 부른다 |
| `Ui/ShellLogic.cs:97-131` | `SessionCopy`, `ProfileChanged` — 위와 같은 손복사 | 같음 | 같음 |
| `Ui/DbHelperView.cs:996-1003` | `OracleConnectionStore.BuildConnectionString` → `DbSession.CreateOracle` → `OracleMetadata.CurrentSchema()` | 연결 흐름이 세 Oracle 정적 호출로 짜여 있다 | `var provider = DbProviders.For(profile.Provider); session = new DbSession(provider.CreateConnection(...), provider); mySchema = await provider.Metadata.CurrentSchemaAsync(session)` |
| `ConnectionSettingsPage.cs:10, 22, 124` | 설정 탭 제목 "Oracle 접속" | 문구 | "DB 접속" |
| `src/MyPlugin/MyPlugin.csproj`, 두 테스트 csproj | `Oracle.ManagedDataAccess.Core`만 참조. `PackPlugin`은 publish 산출물을 전부 zip | 공급자 패키지(Npgsql·Microsoft.Data.SqlClient·MySqlConnector)를 더하면 zip 크기만 늘고 구조상 문제는 없다 | 공급자마다 PackageReference 추가. 선택 로딩(별도 DLL)은 Folderss가 `.deps.json`으로 찾으므로 **처음엔 한 어셈블리에 모두 넣는 것**이 안전 |

### 1.4 값·형식 — `Db/ValueFormatter.cs`

| 파일:줄 | 내용 | 왜 걸리는가 | 갈 곳 |
|---|---|---|---|
| 5, 58-59, 73-106 | `using Oracle.ManagedDataAccess.Types`, `INullable`, `OracleDecimal`·`OracleDate`·`OracleTimeStamp(TZ/LTZ)`·`OracleInterval*`·`OracleBinary`·`OracleBoolean`·`OracleClob/Blob/BFile/RefCursor/XmlType` | 공급자 형식 분기가 `Format` 한 함수에. PG의 `NpgsqlInterval`·`NpgsqlRange`, MSSQL `SqlDecimal`·`SqlGeography` 등은 들어갈 자리가 없다 | `Format(object)`은 .NET 기본형만 다루고, 공급자 형식은 `IColumnReader.FormatProviderValue(object, out string)`로 먼저 묻는다(체인) |
| 35-52 | `NumericTypeNames`(Oracle·ODP.NET·.NET·"그 밖 DB" 이름 섞임), `SizedTypeNames`("VARCHAR2"…) | 이미 다른 DB 이름이 일부 들어가 있어 공급자 표로 나누는 편이 명확 | `IColumnReader.IsNumeric(typeName)`, `SizedTypes` |
| 131-168 | `DescribeColumn`: "NUMBER" → `NumberLabel`, "VARCHAR2(n)" | 열 머리 라벨 규칙이 Oracle | `IColumnReader.DescribeColumn` |

### 1.5 문장 분류·편집 — `Db/SqlScript.cs` · `Ui/WorkspaceLogic.cs`

| 파일:줄 | 내용 | 왜 걸리는가 | 갈 곳 |
|---|---|---|---|
| `Db/SqlScript.cs:7-20, 37, 61, 86, 436-437, 559, 938` | `SqlKind.PlSql`(BEGIN·DECLARE·CALL·EXEC), '/' 종결, 끝의 ';' 떼기(ORA-00911), `q'[…]'` 대체 따옴표, Oracle 숫자 리터럴(`2f`, `3D`), DDL 자동 커밋 목록 | 스크립트 분할·분류가 Oracle SQL*Plus 관례. PG는 `$$ … $$` 달러 인용·`DO $$`, MSSQL은 `GO` 배치·`[식별자]`·`N'…'`, MySQL은 `` `식별자` ``·`DELIMITER` | `ISqlDialect.Lexer`(인용 규칙·배치 구분자) + `ISqlDialect.Classify(verb)`. 토크나이저 뼈대는 공용, 규칙 표만 공급자별. **가장 손이 많이 가는 곳** — 스크립트 실행 기능을 공급자마다 켤지 결정 후 진행 |
| `Ui/WorkspaceLogic.cs:34-45, 322, 468-495` | Oracle 예약어 표, ORA-01555 안내, NULL 정렬(NULLS LAST)·BINARY 정렬 가정 | 편집기 보조·결과 정렬 가정 | `ISqlDialect.ReservedWords`, `IErrorClassifier.Hint(described)`; 결과 정렬은 화면 내부 정렬이라 그대로 둬도 됨 |

### 1.6 GCM 새 기능 — 5절에서 따로 다룸

## 2. 현재 구조와 목표 구조

지금:

```
Ui(DbHelperView·DbTreePanel·SqlWorkspace·TableSpecDialog·CodeSpecDialog)
  └─ 직접 호출 → OracleConnectionStore / DbSession.CreateOracle / OracleMetadata / TableSpecQueries / TableInfo / CodeSpecQueries
       └─ DbSession(DbConnection + is Oracle* 분기) ─ ValueFormatter(Oracle 형식 분기)
```

목표(공급자 추상화):

```
Ui ── 중립 모델만 안다(ConnectionProfile, SchemaInfo, DbObjectInfo, ColumnInfo, TableDescription, QueryTable, ResultColumn)
  └─ IDbProvider  (Db/Providers/IDbProvider.cs)         ← DbProviders.For(profile.Provider)
       ├─ Kind·DisplayName·DefaultPort·Validate·BuildConnectionString·CreateConnection·ConfigureProcess
       ├─ ISqlDialect        (Db/SqlDialect.cs — 이번에 신설)    : LimitOne·Limit·Param·TransactionProbeSql·ServerVersionSql·DdlAutoCommits
       ├─ IMetadataProvider  (Db/Providers/Oracle/OracleMetadataProvider.cs 등) : CurrentSchema·Schemas·GroupCounts·Objects·Columns·Search*·ResolveTable·Indexes·Constraints·TableSpec(Summaries·Columns·Indexes·Constraints)·CompileErrors·ServerVersion
       ├─ IErrorClassifier   : IsCancellation·IsBroken·IsInvalidIdentifier·Describe·CodeOf·Hint
       ├─ IColumnReader      : ReadModeOf·DescribeColumn·FormatProviderValue·IsUnreadable·DisposeValue·IsNumeric
       └─ ISessionBehavior   : AfterOpen·PrepareCommand·AttachWarnings/Detach·TransactionDiesOnFailure
  DbSession(DbConnection, IDbProvider) — 잠금·커서·트랜잭션(중립) + 위 다섯을 호출
```

배치: `src/MyPlugin/Db/Providers/` 아래 `IDbProvider.cs`, `DbProviders.cs`(등록·조회), `Oracle/`(지금의 `OracleMetadata`·`TableSpecQueries`·`TableInfo`의 SQL 부분과 `ValueFormatter`의 Oracle 분기, `DbSession`의 ORA 표), `Generic/`(SQLite 시험용 — `LIMIT`, `GetValue`, information_schema 없음). 중립 모델(`SchemaInfo` 등)은 `Db/` 루트에 그대로. 테스트 csproj는 `Db\**\*.cs`로 글롭을 넓히면 된다(지금은 `Db\*.cs`).

메타데이터 공급자의 반환 형식은 지금처럼 **`SqlQuery` + `Func<IDataRecord, T>` 쌍**을 유지하는 편이 좋다 — `DbSession.QueryAsync`가 중립이고, SQL 문자열 단위 테스트(`OracleMetadataTests`)가 그대로 살기 때문이다. 폴백(11g `ORACLE_MAINTAINED`)처럼 "두 번 물어야" 하는 것만 `Task<List<SchemaInfo>> SchemasAsync(DbSession)`처럼 세션을 받는 메서드로 둔다.

## 3. 접속 프로필: DBMS 종류 필드와 저장 호환

현재 저장 형식(`OracleConnectionStore.Serialize`, camelCase): `[{ "id", "name", "host", "port", "serviceName", "userId", "protectedPassword", "readOnly", "color" }]`. 설정 키 `"connections"` 하나에 JSON 배열.

권장:

1. `OracleConnectionProfile`에 `public string Provider { get; set; } = "oracle";`를 더한다(JSON `"provider"`). `Deserialize`에서 비었으면 `"oracle"`로 채운다(`Id`·`Color` 보정과 같은 자리, `OracleConnections.cs:51-58`). 이전 JSON에는 속성이 없으므로 기본값으로 읽히고, 새 JSON을 **이전 버전 플러그인**이 읽으면 System.Text.Json이 모르는 속성을 무시해 Oracle로 동작한다 — 양방향 호환.
2. `ServiceName`은 JSON 이름(`serviceName`)을 바꾸지 않는다(바꾸면 이전 설정의 서비스명이 사라진다). 의미는 "데이터베이스/서비스"로 넓히고 화면 라벨만 공급자별로 바꾼다(Oracle "서비스명", PG/MySQL/MSSQL "데이터베이스"). 공급자별 추가 항목(MSSQL 인스턴스, PG SSL 모드, SQLite 파일 경로)은 `Dictionary<string,string> Options`로 둔다 — 열거형 필드를 계속 늘리지 않게.
3. 클래스 이름 `OracleConnectionProfile`·`OracleConnectionStore`는 **3단계에서** `ConnectionProfile`·`ConnectionStore`로 바꾼다(public이고 약 20개 파일이 쓰므로 IDE 이름 바꾸기로 한 커밋에). 그 전까지는 이름만 Oracle이고 내용은 중립이어도 된다.
4. 복사·비교 네 곳(`ShellLogic.SessionCopy:97`, `ProfileChanged:123`, `ConnectionManagerLogic.Clone:135`, `SameProfile:152`)에 `Provider`·`Options`를 반드시 더한다. 더 좋은 것은 프로필에 `Copy()`·`SameAs()`를 두고 네 곳을 그것으로 바꾸는 것 — 다음 필드부터는 한 곳만 고친다.
5. 테스트(`OracleConnectionStoreTests`, `ConnectionManagerLogicTests`, `ShellLogicTests`)에 더할 것: (a) `provider` 없는 이전 JSON → `"oracle"`, (b) `provider: "postgres"` 왕복, (c) `SessionCopy`·`Clone`이 `Provider`를 옮기는지, (d) `Validate`가 공급자별 필수 항목을 묻는지.
6. 위험: DPAPI `Entropy`는 건드리지 않는다. `Port` 기본값 1521은 클래스 초기값이라 `provider`에 따라 화면에서 바꿔 준다(`IDbProvider.DefaultPort`).

## 4. GCM 기능의 DBMS 중립성

중립(그대로 둔다):

- `Doc/CodeSpecModel.cs` — `CodeSpecContract`(별칭 계약·`Check`), `CodeEntry.IsUsed`, `Build`(그룹 묶기·SORT_NO 정렬), `Filter`, `Json`, `Csv/CsvBytes/CsvField`. 입력이 `QueryTable`(열 이름 + 글자 행)뿐이라 공급자를 모른다.
- `Ui/CodeSpecLogic.cs` — 설정 직렬화, 출력 형식, 파일 이름, SQL 폴더, 상태 글. `DefaultSql`(59-74)은 표준 SQL(JOIN·AS·ORDER BY)이라 어느 DBMS에서도 보기로 쓸 수 있다.
- `Db/DbSession.QueryTableAsync` — `reader is OracleDataReader`(378)로 읽기 모드만 고르고, 아니면 `GetValue`. 다른 공급자도 바로 돌아간다(SQLite 시험이 그 증거).
- `Db/CodeSpecQueries.Clean`(16-32) — 끝의 ';'와 SQL*Plus '/' 줄을 뗀다. '/'는 Oracle 관례지만 다른 DBMS SQL에 홀로 '/' 줄이 올 일은 없어 해롭지 않다.

중립이 아닌 곳:

| 파일:줄 | 내용 | 조치 |
|---|---|---|
| `Db/CodeSpecQueries.cs:35-40`(수정 전 `ProbeSql(string)` ROWNUM) | 열 확인 SQL이 `WHERE ROWNUM <= 1` 고정 | **이번에 고침**: `ProbeSql(sql, ISqlDialect)` → `dialect.LimitOne(Clean(sql))`, `ProbeAsync`는 `session.Dialect` 사용 |
| `Ui/CodeSpecDialog.cs:770` | `TableSpecQueries.LoadServerVersionAsync`(PRODUCT_COMPONENT_VERSION) | 실패하면 ""을 돌려주므로 다른 DBMS에서 멈추진 않지만 표지의 버전 칸이 빈다. 2단계에서 `IMetadataProvider.ServerVersionAsync(session)`로 바꾼다(테이블 정의서 `TableSpecDialog.cs:826`도 같은 호출) |
| `Doc/CodeSpecModel.cs:376, 432-442, 495, 515-521` | `Context/Json(…, OracleConnectionProfile profile, …)`, `$db.host/port/service/address` | 변수 이름 `$db.service`는 양식 호환 때문에 지켜야 한다(기본 양식·사용자 양식이 쓴다). 값은 공급자에 따라 "데이터베이스"가 들어가도 된다. 매개변수 형식은 3단계 이름 바꾸기와 함께. `$db.product`(공급자 표시 이름)를 **추가**하면 양식에서 "Oracle"/"PostgreSQL"을 구분할 수 있다 — 기존 변수는 그대로 |
| `Doc/CodeSpecModel.cs:228-230` | 변수 설명의 예시 "localhost / 1521 / xe", "Oracle Database 19c" | 문서 예시일 뿐. `$db.product` 추가 때 함께 다듬는다 |
| `Ui/CodeSpecLogic.cs:334` | `LlmSqlPrompt` "GCM 정의서용 **Oracle** SELECT 문을 … 작성해 줘" | 공급자 표시 이름을 매개변수로 받아 끼운다(`LlmSqlPrompt(string dbms)`). 테스트 `CodeSpecTests.Texts`에 영향 — 2단계에 |
| `Ui/CodeSpecDialog.cs:21` | 요약 주석 "ROWNUM 한 줄로 실행해" | 주석뿐. 다음 손댈 때 "한 행만 읽어"로 |
| `tests/MyPlugin.OracleIT/F5_CodeSpecTests.cs` | 실제 Oracle만 시험 | 공급자가 늘면 `F5`를 공급자별 fixture로 돌릴 수 있게 `UserSql`만 바꾸면 되는 구조다(별칭 계약 덕) |

결론: GCM 기능은 "사용자 SQL + 별칭 계약" 설계 덕에 거의 중립이며, 남은 비중립은 세 군데(서버 버전 조회, `$db` 변수의 Oracle 용어, LLM 문구)로 모두 **공급자 표시 이름·버전**만 넘겨받으면 해결된다.

## 5. 권장 리팩토링 순서

### 0단계 — 새 기능에 seam만 두기 (이번에 적용)

- `Db/SqlDialect.cs`: `ISqlDialect { Name; LimitOne(sql) }`, `OracleDialect`(ROWNUM 인라인 뷰), `GenericDialect`(`… ) AS q LIMIT 1` — SQLite·PostgreSQL·MySQL), `SqlDialects.Oracle/Generic/For(DbConnection)`.
- `DbSession.Dialect`(internal, 생성자에서 연결 형식으로 결정) — 기존 로직은 건드리지 않음.
- `CodeSpecQueries.ProbeSql(sql, dialect)`, `ProbeAsync`가 세션 방언 사용. 호출 시그니처(`ProbeAsync(session, sql, token)`)는 그대로라 `CodeSpecDialog`·`F5_CodeSpecTests`는 수정 없음.
- 지켜야 할 테스트: `CodeSpecTests` 전체(특히 `ProbeSql_*`, `Clean_*`), `DbSessionTests.QueryTable_*`, `F5_CodeSpecTests`(Oracle 있을 때).

### 1단계 — 메타데이터 공급자 분리 (난이도 중, 위험 낮음)

1. `IMetadataProvider` 인터페이스를 `Db/Providers/`에 만들고, `OracleMetadata`·`TableSpecQueries`·`TableInfo`의 **SQL 생성 메서드**를 `OracleMetadataProvider`로 옮긴다(읽기 함수 `Read*`·`FormatColumnType`는 함께 이동, 결과 모델은 `Db/` 루트에 남김). 기존 정적 클래스는 한 릴리스 동안 공급자 인스턴스로 위임하는 껍데기로 남겨 호출자를 점진적으로 바꾼다.
2. `ISqlDialect`에 `Limit(sql, n)`·`Param(name)`·`ServerVersionSql`·`TransactionProbeSql`을 더하고, `OracleMetadata.Limit`(416)·`DbSession.TransactionProbeSql`(189)이 그것을 쓰게 한다.
3. 호출자 치환: `DbTreePanel.cs:1110-1132, 1460-1488`(트리·검색), `DbHelperView.cs:1003`(현재 스키마), `TableSpecDialog.cs:483-489, 825-826`, `CodeSpecDialog.cs:770`, `TableInfoWindow`. `HasOracleMaintained`(`DbTreePanel.cs:46-48`, `DbHelperView.cs:522-532`)와 `QueryWithOmAsync`는 Oracle 공급자 안으로 — `IMetadataProvider.SchemasAsync(session, includeSystem)`가 폴백을 품는다.
4. `CodeSpecLogic.LlmSqlPrompt(dbms)`, `CodeSpecModel`에 `$db.product` 추가.
5. 지켜야 할 테스트: `OracleMetadataTests`(SQL 문자열·읽기 — 공급자 클래스로 옮겨도 단언은 그대로), `TableSpecTests`, `TableInfoTests`, `TreeLoaderLogicTests`, `TreePanelLogicTests`, `TreeModelTests`, `CodeSpecTests.Texts`(문구 바뀌면 함께 수정), OracleIT `F2_TreeTests`·`F4_TableSpecTests`·`F5`.

### 2단계 — 접속 프로필과 공급자 등록 (난이도 중, 위험 중: 저장 데이터)

1. `Provider`·`Options` 필드, `Deserialize` 기본값 채우기, `Copy()/SameAs()`로 손복사 네 곳 치환.
2. `IDbProvider`(루트)와 `DbProviders.For(name)`; `OracleProvider`가 `BuildConnectionString`·`CreateConnection`·`UseInBandBreak`·`Validate`·`DefaultPort`·`DisplayName`·`Address`를 맡는다. `DbSession.CreateOracle`은 `OracleProvider.CreateSession`으로 위임하는 껍데기.
3. 접속 관리 화면에 공급자 콤보(Oracle만 있어도 둔다), 라벨 공급자별, 접속 테스트는 `provider.CreateConnection`.
4. 지켜야 할 테스트: `OracleConnectionStoreTests`(이전 JSON 호환 케이스 추가), `ConnectionManagerLogicTests`, `ShellLogicTests`, OracleIT `F1_ConnectionTests`.

### 3단계 — 세션 공급자 의존 분리 (난이도 상, 위험 상: 트랜잭션·취소 동작)

1. `ISessionBehavior`·`IErrorClassifier`·`IColumnReader`를 만들고 `DbSession`의 `is Oracle*` 분기 14곳(1.1절)을 호출로 바꾼다. 생성자는 `DbSession(DbConnection, IDbProvider)`; 기존 `DbSession(DbConnection)`은 `SqlDialects.For`처럼 연결 형식으로 공급자를 추론해 남긴다(SQLite 시험이 계속 돌게).
2. `ValueFormatter.Format`의 Oracle 형식 분기를 `OracleColumnReader.FormatProviderValue`로, `OracleTypeName/OracleReadMode/NormalizeOracleColumn`도 함께.
3. `SqlScript`는 마지막 — 공급자마다 스크립트 실행을 켤지 결정한 뒤 `ISqlDialect.Lexer` 규칙 표로.
4. 이름 바꾸기: `OracleConnectionProfile → ConnectionProfile`, `OracleConnectionStore → ConnectionStore`(한 커밋).
5. 지켜야 할 테스트: `DbSessionTests` 전체(1,200여 건 — 이 단계의 안전망), `ValueFormatterTests`, `SqlScriptTests`, `WorkspaceLogicTests`, OracleIT `F3_SqlExecutionTests`(취소·커밋·DDL 경고는 실제 Oracle에서만 확인된다 — 이 단계는 Oracle 서버를 띄우고 진행할 것).

첫 실제 공급자(권장: PostgreSQL — 데이터 사전이 풍부하고 Npgsql이 `GetProviderSpecificValue`를 지원)는 2단계 뒤에 **읽기 전용·트리·정의서·GCM**만 켜고, 스크립트 실행(3단계 `SqlScript`)은 뒤로 미루는 것이 현실적이다.

## 6. 위험·난이도 요약

| 항목 | 난이도 | 위험 | 비고 |
|---|---|---|---|
| 0단계 seam(`ISqlDialect`) | 하 | 없음 | 적용 완료. 동작 변화 없음(Oracle 문자열 동일) |
| 메타데이터 공급자 분리 | 중 | 하 | SQL 문자열 테스트가 그대로 보호. 호출자 치환이 많아 분량만 큼 |
| 11g `ORACLE_MAINTAINED` 폴백을 공급자 안으로 | 중 | 중 | `HasOracleMaintained` 캐시가 DB 상태(`DbHelperView.DbState`)에 있어 옮길 때 재시도 횟수가 늘지 않게 공급자 인스턴스를 세션마다 둔다 |
| 프로필 `Provider` 필드 | 하 | 중 | 이전 JSON 호환은 기본값으로 해결. 손복사 네 곳 누락이 실제 위험 |
| 연결 문자열 빌더 분리 | 하 | 하 | `OracleConnectionStringBuilder` 그대로 공급자 안으로 |
| `DbSession` 분기 분리 | 상 | 상 | 취소(ORA-01013)·커밋 실패 뒤 트랜잭션 재부착·InfoMessage는 실제 Oracle에서만 검증됨. 단계적으로, 메서드 하나씩 |
| `ValueFormatter` 분리 | 중 | 하 | 순수 함수. `ValueFormatterTests`가 보호 |
| `SqlScript` 방언화 | 상 | 중 | 토크나이저 규칙이 Oracle 관례와 얽힘. 공급자별 실행 기능 범위를 먼저 정할 것 |
| DDL 자동 커밋 의미(`Execute`) | 중 | 중 | PG는 트랜잭션 DDL. 지금 로직은 "DDL 전 커밋" 강제 — 공급자 플래그 필요 |

## 7. 이번에 적용한 변경

| 파일 | 변경 |
|---|---|
| `src/MyPlugin/Db/SqlDialect.cs` (신규) | `ISqlDialect { Name; LimitOne(sql) }`, `OracleDialect`(`SELECT * FROM (\n…\n) WHERE ROWNUM <= 1`), `GenericDialect`(`SELECT * FROM (\n…\n) AS q LIMIT 1`), `SqlDialects.Oracle/Generic/For(DbConnection)` |
| `src/MyPlugin/Db/DbSession.cs:186, 196-197` | 생성자에서 `Dialect = SqlDialects.For(connection)`; `internal ISqlDialect Dialect { get; }`. 그 밖은 손대지 않음 |
| `src/MyPlugin/Db/CodeSpecQueries.cs:35-49` | `ProbeSql(string sql, ISqlDialect dialect)`(null이면 `ArgumentNullException`), `ProbeAsync`가 `session.Dialect`로 감쌈. 요약 주석 갱신 |
| `tests/MyPlugin.Tests/CodeSpecTests.cs:415-428` | `ProbeSql_WrapsAsInlineViewWithRownum`에 `SqlDialects.Oracle` 전달; `ProbeSql_GenericDialect_UsesLimit_NullDialectThrows` 추가 |
| `tests/MyPlugin.Tests/DbSessionTests.cs:1535-1560` | `QueryTable_WithSessionDialect_ProbeReadsOneRow_AndChecksAliases` — SQLite 세션의 `Dialect`가 Generic인지, 감싼 SQL이 실제로 한 행만(인라인 뷰 안 `ORDER BY DESC` 유지) 읽는지, `ProbeAsync`의 별칭 검사(필수·추가 속성·빠진 별칭) |

`Ui/CodeSpecDialog.cs`·`tests/MyPlugin.OracleIT/F5_CodeSpecTests.cs`는 `ProbeAsync` 시그니처가 그대로라 수정하지 않았다. 기존 Oracle 전용 계층(`OracleMetadata`·`TableSpecQueries`·`TableInfo`·`OracleConnections`·`DbSession` 나머지·`ValueFormatter`·`SqlScript`)은 고치지 않고 위 표에만 적었다.

확인 결과(2026-10-11): `dotnet build src/MyPlugin/MyPlugin.csproj -c Debug` 오류 0·경고 0, `dotnet test tests/MyPlugin.Tests` 1,271건 전체 통과(새 테스트 3건 포함, 다른 에이전트의 `CodeSpecTemplateTests`도 통과), `dotnet build tests/MyPlugin.OracleIT` 오류 0.

## 8. 2단계 적용 기록 (2026-10-11)

- `Db/Providers/IDbProvider.cs`: `IDbProvider`(Kind·DisplayName·DefaultPort·ServiceLabel·Dialect·Metadata·Validate·BuildConnectionString·CreateConnection·ConfigureProcess·ServerVersionOf·Address·IsCancellation·IsBroken)와 `IMetadataProvider`(트리·검색·버전 조회의 SqlQuery+읽기 쌍, `HasMaintainedFlagFallback`, `SupportsTableInfo`).
- `Db/Providers/DbProviders.cs`: 등록·조회(`For(kind)`·`For(profile)`·`For(DbConnection)`). 모르는 값·빈 값은 Oracle.
- `Db/Providers/OracleProvider.cs`: 기존 `OracleMetadata`·`TableSpecQueries`에 위임. `ProviderRules`(공통 검사·주소), `GenericProvider`(SQLite 시험).
- `Db/Providers/PostgresProvider.cs`·`PostgresMetadata.cs`: Npgsql 연결 문자열, pg_catalog 조회(결과 열 이름을 Oracle 공급자와 같게 맞춤), 57014 취소·08*** 끊김 판정.
- 프로필: `Provider` 필드(기본 "oracle", 이전 JSON 호환), `Copy()`·`SameAs()`로 손복사 네 곳 치환. `Validate`·`BuildConnectionString`은 공급자에 위임.
- 세션: `DbSession(DbConnection, IDbProvider)`·`Provider` 속성·`Create(provider, cs)`. 정적 `IsCancellation`·`IsBrokenError`가 공급자에게도 묻는다. Oracle 분기(명령 설정·ORA 표)는 그대로(3단계).
- 화면: 접속 관리에 [DBMS] 콤보(라벨·기본 포트 전환, 접속 테스트는 공급자 연결), 연결 흐름이 `DbSession.Create`·`provider.Metadata.CurrentSchema()`, 트리·검색이 `session.Provider.Metadata`, F4·테이블 정의서는 `SupportsTableInfo`로 가드.
- 테스트: `DbProvidersTests`(등록·호환·연결 문자열·PG SQL·읽기), `PostgresIT`(PG_IT_DSN·PG_IT_USER·PG_IT_PW가 있을 때 실제 서버로 연결·트리·검색·SELECT 커서·GCM 조회 — 사내 mesdb로 통과 확인).
- 남은 것(3단계): `DbSession`의 Oracle 분기 14곳, `ValueFormatter`, `SqlScript` 방언, 이름 바꾸기(`OracleConnectionProfile → ConnectionProfile`). PostgreSQL의 테이블 정보·테이블 정의서는 1단계(메타데이터 공급자에 `TableInfo`·`TableSpec` 조회 추가)에서.

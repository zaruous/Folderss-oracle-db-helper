using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using Oracle.ManagedDataAccess.Client;

namespace MyPlugin
{
    /// <summary>실행할 SQL과 바인드 값(이름은 ':' 없이, BindByName 기준).</summary>
    public sealed class SqlQuery
    {
        public string Sql { get; set; }
        public List<KeyValuePair<string, object>> Parameters { get; } = new List<KeyValuePair<string, object>>();
    }

    public sealed class SchemaInfo
    {
        public string Name { get; set; }
        /// <summary>Oracle 내장 스키마(ALL_USERS.ORACLE_MAINTAINED = 'Y', 11g는 <see cref="OracleMetadata.KnownSystemSchemas"/>).</summary>
        public bool OracleMaintained { get; set; }
    }

    public sealed class DbObjectInfo
    {
        public string Owner { get; set; }
        public string Name { get; set; }
        /// <summary>ALL_OBJECTS.OBJECT_TYPE (TABLE, VIEW, SEQUENCE, PROCEDURE, FUNCTION, PACKAGE)</summary>
        public string Type { get; set; }
        /// <summary>테이블 통계 행 수(ALL_TABLES.NUM_ROWS). 통계가 없거나 테이블이 아니면 null.</summary>
        public long? NumRows { get; set; }
        /// <summary>ALL_OBJECTS.STATUS (VALID/INVALID)</summary>
        public string Status { get; set; }
    }

    public sealed class ColumnInfo
    {
        public string Owner { get; set; }
        /// <summary>테이블 또는 뷰 이름</summary>
        public string ObjectName { get; set; }
        public string Name { get; set; }
        /// <summary>표시용 형식. 예: VARCHAR2(10 CHAR), NUMBER(7,2), NUMBER, DATE, TIMESTAMP(6)</summary>
        public string TypeLabel { get; set; }
        public bool Nullable { get; set; }
        public bool PrimaryKey { get; set; }
        public int Position { get; set; }
    }

    /// <summary>트리 검색 결과 한 건. 객체 검색이면 ColumnName이 null.</summary>
    public sealed class SearchHit
    {
        public string Owner { get; set; }
        public string ObjectType { get; set; }
        public string ObjectName { get; set; }
        public string ColumnName { get; set; }
        public string ColumnType { get; set; }
    }

    /// <summary>
    /// 트리·검색용 데이터 사전 조회 SQL과 결과 읽기(순수 로직, 테스트 대상).
    /// - ALL_* 뷰만 쓴다(DBA_*는 권한 필요). 바인드는 이름 바인드(:owner 등).
    /// - 11g 호환: 행 제한은 ROWNUM, ALL_USERS.ORACLE_MAINTAINED가 없으면(ORA-00904) KnownSystemSchemas로 대신한다.
    /// - 행 제한 쿼리는 limit+1행까지 돌려준다(호출자가 "더 있음"을 판단). limit 0은 제한 없음, 음수는 ArgumentOutOfRangeException.
    /// - 휴지통 객체(BIN$…)는 뺀다.
    /// - 검색은 UPPER(이름) LIKE :pattern ESCAPE '\' (pattern은 SqlScript.ContainsPattern). includeSystem=false면 내장 스키마의 결과를 뺀다.
    /// </summary>
    public static class OracleMetadata
    {
        public const int SearchLimit = 500;

        /// <summary>컴파일 오류를 메시지 탭에 보이는 최대 줄 수.</summary>
        public const int CompileErrorLimit = 20;

        // ALL_ERRORS.TEXT를 한 줄로 보일 최대 길이
        private const int CompileErrorTextLength = 300;

        /// <summary>ORACLE_MAINTAINED가 없는 DB(11g)에서 내장으로 볼 스키마. 11gR2 기본 계정(APEX_030200, EXFSYS, SYSMAN 등)도 포함한다.</summary>
        public static readonly string[] KnownSystemSchemas =
        {
            "ANONYMOUS", "APEX_030200", "APEX_PUBLIC_USER", "APPQOSSYS", "AUDSYS", "CTXSYS", "DBSFWUSER", "DBSNMP", "DIP", "DVF", "DVSYS",
            "EXFSYS", "FLOWS_FILES", "GGSYS", "GSMADMIN_INTERNAL", "GSMCATUSER", "GSMUSER", "LBACSYS", "MDDATA", "MDSYS", "MGMT_VIEW", "OJVMSYS",
            "OLAPSYS", "ORACLE_OCM", "ORDDATA", "ORDPLUGINS", "ORDSYS", "OUTLN", "OWBSYS", "OWBSYS_AUDIT", "REMOTE_SCHEDULER_AGENT", "SI_INFORMTN_SCHEMA",
            "SPATIAL_CSW_ADMIN_USR", "SPATIAL_WFS_ADMIN_USR", "SYS", "SYS$UMF", "SYSBACKUP", "SYSDG", "SYSKM", "SYSMAN", "SYSRAC", "SYSTEM",
            "WMSYS", "XDB", "XS$NULL"
        };

        // 트리에 보이는 ALL_OBJECTS.OBJECT_TYPE. PACKAGE BODY는 같은 이름의 PACKAGE와 겹치므로 넣지 않는다.
        private static readonly string[] TreeObjectTypes = { "TABLE", "VIEW", "SEQUENCE", "PROCEDURE", "FUNCTION", "PACKAGE" };

        // 열이 있는 형식. ALL_TAB_COLUMNS에는 클러스터 열도 있어 ALL_OBJECTS로 걸러야 한다.
        private static readonly string[] ColumnObjectTypes = { "TABLE", "VIEW" };

        // DROP한 테이블은 PURGE 전까지 BIN$… 이름으로 ALL_OBJECTS·ALL_TAB_COLUMNS에 남는다.
        private const string NotRecycleBin = " NOT LIKE 'BIN$%'";

        private const int InvalidIdentifierNumber = 904;

        /// <summary>SELECT SYS_CONTEXT('USERENV','CURRENT_SCHEMA') FROM DUAL</summary>
        public static SqlQuery CurrentSchema()
        {
            return new SqlQuery { Sql = "SELECT SYS_CONTEXT('USERENV','CURRENT_SCHEMA') FROM DUAL" };
        }

        /// <summary>
        /// 객체 하나의 컴파일 오류: SELECT TYPE, LINE, POSITION, TEXT FROM ALL_ERRORS … ORDER BY TYPE, SEQUENCE(limit+1행까지).
        /// owner가 null이면 현재 스키마(SYS_CONTEXT('USERENV','CURRENT_SCHEMA') — NULL을 바인드하지 않는다).
        /// </summary>
        public static SqlQuery CompileErrors(string owner, string name, IEnumerable<string> types, int limit)
        {
            var list = (types ?? Enumerable.Empty<string>()).Where(t => !string.IsNullOrEmpty(t)).ToList();
            if (string.IsNullOrEmpty(name) || list.Count == 0)
                throw new ArgumentException("객체 이름과 종류가 있어야 합니다.");
            var query = new SqlQuery
            {
                Sql = "SELECT TYPE, LINE, POSITION, TEXT FROM ALL_ERRORS"
                    + " WHERE OWNER = " + (owner == null ? "SYS_CONTEXT('USERENV','CURRENT_SCHEMA')" : ":owner")
                    + " AND NAME = :name AND TYPE IN " + InList(list)
                    + " ORDER BY TYPE, SEQUENCE"
            };
            if (owner != null)
                Bind(query, "owner", owner);
            Bind(query, "name", name);
            return Limit(query, limit);
        }

        /// <summary>컴파일 오류 한 줄: "줄 3, 열 5: PLS-00103: …"(withType이면 앞에 "PACKAGE BODY "). TEXT의 줄바꿈·연속 공백은 공백 하나로.</summary>
        public static string ReadCompileError(IDataRecord record, bool withType)
        {
            var line = Number(record, "LINE");
            var position = Number(record, "POSITION");
            var text = string.Join(" ", (Text(record, "TEXT") ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
            if (text.Length > CompileErrorTextLength)
                text = text.Substring(0, CompileErrorTextLength) + "…";
            return (withType ? Text(record, "TYPE") + " " : "")
                + "줄 " + (line.HasValue ? Integer(line.Value) : "?") + ", 열 " + (position.HasValue ? Integer(position.Value) : "?") + ": " + text;
        }

        /// <summary>ALL_USERS의 USERNAME(+ORACLE_MAINTAINED), USERNAME 순.</summary>
        public static SqlQuery Schemas(bool hasOracleMaintained)
        {
            return new SqlQuery { Sql = "SELECT " + SchemaColumns(hasOracleMaintained) + " FROM ALL_USERS ORDER BY USERNAME" };
        }

        public static SchemaInfo ReadSchema(IDataRecord record, bool hasOracleMaintained)
        {
            var name = Text(record, "USERNAME");
            return new SchemaInfo
            {
                Name = name,
                OracleMaintained = hasOracleMaintained
                    ? string.Equals(Text(record, "ORACLE_MAINTAINED"), "Y", StringComparison.OrdinalIgnoreCase)
                    : Array.IndexOf(KnownSystemSchemas, name) >= 0
            };
        }

        /// <summary>스키마 하나의 객체 종류별 개수: SELECT OBJECT_TYPE, COUNT(*) … WHERE OWNER = :owner AND OBJECT_TYPE IN (…) GROUP BY OBJECT_TYPE.</summary>
        public static SqlQuery GroupCounts(string owner)
        {
            var query = new SqlQuery
            {
                Sql = "SELECT OBJECT_TYPE, COUNT(*) AS CNT FROM ALL_OBJECTS"
                    + " WHERE OWNER = :owner"
                    + " AND OBJECT_TYPE IN " + InList(TreeObjectTypes)
                    + " AND OBJECT_NAME" + NotRecycleBin
                    + " GROUP BY OBJECT_TYPE"
            };
            Bind(query, "owner", owner);
            return query;
        }

        /// <summary>GroupCounts 결과를 묶음(TreeGroups) → 개수로. PROCEDURE·FUNCTION·PACKAGE는 CODE로 합친다. 없는 묶음은 0.</summary>
        public static Dictionary<string, int> ReadGroupCounts(IDataReader reader)
        {
            var counts = new Dictionary<string, int>
            {
                { TreeGroups.Table, 0 },
                { TreeGroups.View, 0 },
                { TreeGroups.Sequence, 0 },
                { TreeGroups.Code, 0 }
            };
            while (reader.Read())
            {
                var group = GroupOfType(Text(reader, "OBJECT_TYPE"));
                if (group != null)
                    counts[group] += (int)(Number(reader, "CNT") ?? 0);
            }
            return counts;
        }

        /// <summary>
        /// 스키마 하나의 묶음(TreeGroups) 객체 목록, 이름 순. TABLE은 ALL_TABLES.NUM_ROWS를 함께 읽는다(LEFT JOIN). limit+1행까지.
        /// 열: OWNER, OBJECT_NAME, OBJECT_TYPE, STATUS, NUM_ROWS(TABLE이 아니면 NULL). 모르는 묶음이면 ArgumentException.
        /// </summary>
        public static SqlQuery Objects(string owner, string group, int limit)
        {
            var types = ObjectTypesOf(group);
            if (types == null)
                throw new ArgumentException("알 수 없는 객체 묶음입니다: " + group, nameof(group));

            var isTable = group == TreeGroups.Table;
            // LEFT JOIN: 객체 테이블(CREATE TABLE … OF 형식)·XMLType 테이블은 ALL_OBJECTS에는 있지만 ALL_TABLES에는 없다.
            var sql = "SELECT o.OWNER, o.OBJECT_NAME, o.OBJECT_TYPE, o.STATUS, "
                    + (isTable ? "t.NUM_ROWS FROM ALL_OBJECTS o LEFT JOIN ALL_TABLES t ON t.OWNER = o.OWNER AND t.TABLE_NAME = o.OBJECT_NAME"
                               : "CAST(NULL AS NUMBER) AS NUM_ROWS FROM ALL_OBJECTS o")
                    + " WHERE o.OWNER = :owner"
                    + " AND o.OBJECT_TYPE IN " + InList(types)
                    + " AND o.OBJECT_NAME" + NotRecycleBin
                    + " ORDER BY o.OBJECT_NAME";
            var query = new SqlQuery { Sql = sql };
            Bind(query, "owner", owner);
            return Limit(query, limit);
        }

        public static DbObjectInfo ReadObject(IDataRecord record)
        {
            var numRows = Number(record, "NUM_ROWS");
            return new DbObjectInfo
            {
                Owner = Text(record, "OWNER"),
                Name = Text(record, "OBJECT_NAME"),
                Type = Text(record, "OBJECT_TYPE"),
                Status = Text(record, "STATUS"),
                NumRows = numRows.HasValue ? (long)numRows.Value : (long?)null
            };
        }

        /// <summary>
        /// 소스: 뷰는 ALL_VIEWS.TEXT(LONG — 세션이 InitialLONGFetchSize = -1로 읽는다), 프로시저·함수·패키지는 ALL_SOURCE 줄(LINE 순, 패키지는 명세 다음 본문).
        /// 열: PART, TEXT. 볼 권한이 없는 패키지 본문은 행이 없다(명세만 나온다).
        /// </summary>
        public static SqlQuery Source(string owner, string objectType, string name)
        {
            string sql;
            if (objectType == "VIEW")
            {
                sql = "SELECT 'VIEW' AS PART, TEXT FROM ALL_VIEWS WHERE OWNER = :owner AND VIEW_NAME = :name";
            }
            else if (objectType == "SEQUENCE")
            {
                // 숫자는 TO_CHAR로(MAXVALUE 기본값이 28자리라 .NET decimal 경계에 걸리지 않게). LAST_VALUE는 분석 함수 이름이라 따옴표 별칭
                sql = "SELECT 'SEQUENCE' AS PART, TO_CHAR(LAST_NUMBER) AS \"LAST_VALUE\", TO_CHAR(INCREMENT_BY) AS INCREMENT_BY,"
                    + " TO_CHAR(MIN_VALUE) AS MIN_VALUE, TO_CHAR(MAX_VALUE) AS MAX_VALUE, TO_CHAR(CACHE_SIZE) AS CACHE_SIZE,"
                    + " CYCLE_FLAG, ORDER_FLAG FROM ALL_SEQUENCES WHERE SEQUENCE_OWNER = :owner AND SEQUENCE_NAME = :name";
            }
            else
            {
                var package = objectType == "PACKAGE";
                sql = "SELECT TYPE AS PART, TEXT FROM ALL_SOURCE WHERE OWNER = :owner AND NAME = :name"
                    + (package ? " AND TYPE IN ('PACKAGE', 'PACKAGE BODY') ORDER BY CASE TYPE WHEN 'PACKAGE' THEN 1 ELSE 2 END, LINE"
                        : " AND TYPE = :type ORDER BY LINE");
            }
            var query = new SqlQuery { Sql = sql };
            Bind(query, "owner", owner);
            Bind(query, "name", name);
            if (objectType != "VIEW" && objectType != "SEQUENCE" && objectType != "PACKAGE")
                Bind(query, "type", objectType);
            return query;
        }

        /// <summary>뷰: CREATE OR REPLACE VIEW 스키마.이름 AS + 원문 + ';' 줄. 코드: 조각마다 CREATE OR REPLACE + 원문 + '/' 줄.</summary>
        public static string BuildSource(string owner, string objectType, string name, IList<SourcePart> parts)
        {
            if (objectType == "SEQUENCE")
            {
                // 처음 START WITH 값은 남지 않는다 — LAST_NUMBER(캐시를 넘긴 다음 번호)로 다시 만들면 이미 쓴 번호와 겹치지 않는다
                return ObjectSource.SequenceDdl(parts,
                    r => "START WITH는 지금의 LAST_NUMBER(다음에 나올 번호 근처)입니다. 처음 만들 때의 시작 값은 데이터 사전에 남지 않습니다.",
                    r => WorkspaceLogic.QualifiedName(owner, name),
                    r => new[]
                    {
                        "START WITH " + r.Field("LAST_VALUE"),
                        "INCREMENT BY " + r.Field("INCREMENT_BY"),
                        "MINVALUE " + r.Field("MIN_VALUE"),
                        "MAXVALUE " + r.Field("MAX_VALUE"),
                        r.Field("CACHE_SIZE") == "0" ? "NOCACHE" : "CACHE " + r.Field("CACHE_SIZE"),
                        r.Field("CYCLE_FLAG") == "Y" ? "CYCLE" : "NOCYCLE",
                        r.Field("ORDER_FLAG") == "Y" ? "ORDER" : "NOORDER"
                    });
            }
            if (objectType != "VIEW")
                return ObjectSource.JoinPlSql(parts);
            var text = parts.Count == 0 ? null : parts[0].Text;
            if (string.IsNullOrWhiteSpace(text))
                return null;
            // ';'는 줄을 따로 둔다(원문 끝이 -- 주석이어도 문장이 끝나게)
            return "CREATE OR REPLACE VIEW " + WorkspaceLogic.QualifiedName(owner, name) + " AS\n" + text.TrimEnd() + "\n;\n";
        }

        /// <summary>
        /// 테이블·뷰의 열(ALL_TAB_COLUMNS) COLUMN_ID 순 + 기본 키 여부(ALL_CONSTRAINTS 'P' / ALL_CONS_COLUMNS).
        /// 열: OWNER, TABLE_NAME, COLUMN_NAME, DATA_TYPE, DATA_LENGTH, DATA_PRECISION, DATA_SCALE, NULLABLE, COLUMN_ID, CHAR_USED, CHAR_LENGTH, IS_PK(1/0).
        /// </summary>
        public static SqlQuery Columns(string owner, string objectName)
        {
            var query = new SqlQuery
            {
                Sql = "SELECT c.OWNER, c.TABLE_NAME, c.COLUMN_NAME, c.DATA_TYPE, c.DATA_LENGTH, c.DATA_PRECISION, c.DATA_SCALE,"
                    + " c.NULLABLE, c.COLUMN_ID, c.CHAR_USED, c.CHAR_LENGTH,"
                    + " CASE WHEN EXISTS (SELECT 1 FROM ALL_CONSTRAINTS k"
                    + " JOIN ALL_CONS_COLUMNS kc ON kc.OWNER = k.OWNER AND kc.CONSTRAINT_NAME = k.CONSTRAINT_NAME AND kc.TABLE_NAME = k.TABLE_NAME"
                    + " WHERE k.CONSTRAINT_TYPE = 'P' AND k.OWNER = c.OWNER AND k.TABLE_NAME = c.TABLE_NAME AND kc.COLUMN_NAME = c.COLUMN_NAME)"
                    + " THEN 1 ELSE 0 END AS IS_PK"
                    + " FROM ALL_TAB_COLUMNS c"
                    + " WHERE c.OWNER = :owner AND c.TABLE_NAME = :name"
                    + " ORDER BY c.COLUMN_ID"
            };
            Bind(query, "owner", owner);
            Bind(query, "name", objectName);
            return query;
        }

        public static ColumnInfo ReadColumn(IDataRecord record)
        {
            return new ColumnInfo
            {
                Owner = Text(record, "OWNER"),
                ObjectName = Text(record, "TABLE_NAME"),
                Name = Text(record, "COLUMN_NAME"),
                TypeLabel = ReadColumnType(record),
                // 값을 모르면 NOT NULL로 단정하지 않는다.
                Nullable = !string.Equals(Text(record, "NULLABLE"), "N", StringComparison.OrdinalIgnoreCase),
                PrimaryKey = (Number(record, "IS_PK") ?? 0) != 0,
                Position = (int)(Number(record, "COLUMN_ID") ?? 0)
            };
        }

        /// <summary>
        /// ALL_TAB_COLUMNS 값으로 표시 형식을 만든다. 예:
        /// VARCHAR2·NVARCHAR2·CHAR·NCHAR → "VARCHAR2(10 CHAR)"/"VARCHAR2(10 BYTE)"(CHAR_USED 'C'면 CHAR_LENGTH와 CHAR, 아니면 DATA_LENGTH와 BYTE; N 형식은 단위 없이 CHAR_LENGTH),
        /// NUMBER: 정밀도 없으면 "NUMBER", 소수 자릿수 0이면 "NUMBER(p)", 아니면 "NUMBER(p,s)"; FLOAT(p); RAW(n);
        /// 그 밖(DATE, CLOB, TIMESTAMP(6), TIMESTAMP(6) WITH TIME ZONE …)은 DATA_TYPE 그대로.
        /// 정밀도가 없으면 소수 자릿수가 있어도 "NUMBER"다(INTEGER = NUMBER(*,0)도 "NUMBER"). DATA_TYPE이 없으면 "".
        /// </summary>
        public static string FormatColumnType(string dataType, decimal? dataLength, decimal? precision, decimal? scale, string charUsed, decimal? charLength)
        {
            if (string.IsNullOrEmpty(dataType))
                return "";
            switch (dataType)
            {
                case "VARCHAR2":
                case "CHAR":
                    if (string.Equals(charUsed, "C", StringComparison.OrdinalIgnoreCase) && charLength.HasValue)
                        return dataType + "(" + Integer(charLength.Value) + " CHAR)";
                    return dataLength.HasValue ? dataType + "(" + Integer(dataLength.Value) + " BYTE)" : dataType;
                case "NVARCHAR2":
                case "NCHAR":
                    // N 형식의 길이는 늘 문자 수다(DATA_LENGTH는 바이트라 문자 집합에 따라 2~3배).
                    return charLength.HasValue ? dataType + "(" + Integer(charLength.Value) + ")" : dataType;
                case "NUMBER":
                    if (!precision.HasValue)
                        return dataType;
                    return scale.GetValueOrDefault() == 0
                        ? dataType + "(" + Integer(precision.Value) + ")"
                        : dataType + "(" + Integer(precision.Value) + "," + Integer(scale.Value) + ")";
                case "FLOAT":
                    return precision.HasValue ? dataType + "(" + Integer(precision.Value) + ")" : dataType;
                case "RAW":
                    return dataLength.HasValue ? dataType + "(" + Integer(dataLength.Value) + ")" : dataType;
                default:
                    return dataType;
            }
        }

        /// <summary>스키마 이름 검색(ALL_USERS). USERNAME 순. 열은 <see cref="Schemas"/>와 같다(ReadSchema로 읽음). limit+1행까지.</summary>
        public static SqlQuery SearchSchemas(string pattern, bool includeSystem, bool hasOracleMaintained, int limit)
        {
            var sql = "SELECT " + SchemaColumns(hasOracleMaintained) + " FROM ALL_USERS"
                    + " WHERE UPPER(USERNAME) LIKE :pattern ESCAPE '\\'";
            if (!includeSystem)
                sql += hasOracleMaintained ? " AND ORACLE_MAINTAINED = 'N'" : " AND USERNAME NOT IN " + InList(KnownSystemSchemas);
            var query = new SqlQuery { Sql = sql + " ORDER BY USERNAME" };
            Bind(query, "pattern", pattern);
            return Limit(query, limit);
        }

        /// <summary>객체 이름 검색(ALL_OBJECTS, 트리에 보이는 종류만). OWNER, OBJECT_TYPE, OBJECT_NAME 순. limit+1행까지.</summary>
        public static SqlQuery SearchObjects(string pattern, bool includeSystem, bool hasOracleMaintained, int limit)
        {
            var query = new SqlQuery
            {
                Sql = "SELECT OWNER, OBJECT_TYPE, OBJECT_NAME FROM ALL_OBJECTS"
                    + " WHERE UPPER(OBJECT_NAME) LIKE :pattern ESCAPE '\\'"
                    + " AND OBJECT_TYPE IN " + InList(TreeObjectTypes)
                    + " AND OBJECT_NAME" + NotRecycleBin
                    + NonSystemOwner("OWNER", includeSystem, hasOracleMaintained)
                    + " ORDER BY OWNER, OBJECT_TYPE, OBJECT_NAME"
            };
            Bind(query, "pattern", pattern);
            return Limit(query, limit);
        }

        /// <summary>
        /// 열 이름 검색(ALL_TAB_COLUMNS, 테이블·뷰). OWNER, TABLE_NAME, COLUMN_ID 순. 결과의 ObjectType은 TABLE 또는 VIEW. limit+1행까지.
        /// 열: OWNER, OBJECT_TYPE, OBJECT_NAME, COLUMN_NAME, DATA_TYPE, DATA_LENGTH, DATA_PRECISION, DATA_SCALE, CHAR_USED, CHAR_LENGTH.
        /// </summary>
        public static SqlQuery SearchColumns(string pattern, bool includeSystem, bool hasOracleMaintained, int limit)
        {
            // TABLE·VIEW는 같은 이름 공간이라 조인해도 열이 겹치지 않는다(같은 이름의 TABLE PARTITION·INDEX 행은 형식으로 걸러짐).
            var query = new SqlQuery
            {
                Sql = "SELECT c.OWNER, o.OBJECT_TYPE, c.TABLE_NAME AS OBJECT_NAME, c.COLUMN_NAME,"
                    + " c.DATA_TYPE, c.DATA_LENGTH, c.DATA_PRECISION, c.DATA_SCALE, c.CHAR_USED, c.CHAR_LENGTH"
                    + " FROM ALL_TAB_COLUMNS c"
                    + " JOIN ALL_OBJECTS o ON o.OWNER = c.OWNER AND o.OBJECT_NAME = c.TABLE_NAME"
                    + " WHERE UPPER(c.COLUMN_NAME) LIKE :pattern ESCAPE '\\'"
                    + " AND o.OBJECT_TYPE IN " + InList(ColumnObjectTypes)
                    + " AND o.OBJECT_NAME" + NotRecycleBin
                    + NonSystemOwner("c.OWNER", includeSystem, hasOracleMaintained)
                    + " ORDER BY c.OWNER, c.TABLE_NAME, c.COLUMN_ID"
            };
            Bind(query, "pattern", pattern);
            return Limit(query, limit);
        }

        public static SearchHit ReadObjectHit(IDataRecord record)
        {
            return new SearchHit
            {
                Owner = Text(record, "OWNER"),
                ObjectType = Text(record, "OBJECT_TYPE"),
                ObjectName = Text(record, "OBJECT_NAME")
            };
        }

        public static SearchHit ReadColumnHit(IDataRecord record)
        {
            var hit = ReadObjectHit(record);
            hit.ColumnName = Text(record, "COLUMN_NAME");
            hit.ColumnType = ReadColumnType(record);
            return hit;
        }

        /// <summary>ORA-00904(잘못된 식별자) — ORACLE_MAINTAINED 열이 없는 11g에서 대체 쿼리로 다시 시도할 때 판단한다. 예외 메시지·OracleException.Number로 판단.</summary>
        public static bool IsInvalidIdentifier(Exception ex)
        {
            return IsInvalidIdentifier(ex, 0);
        }

        private static bool IsInvalidIdentifier(Exception ex, int depth)
        {
            // Task·리플렉션을 거치면 AggregateException·TargetInvocationException 등에 싸여 온다. 깊이 제한은 비정상적으로 깊은 중첩 대비.
            if (ex == null || depth > 32)
                return false;
            var oracle = ex as OracleException;
            if (oracle != null && oracle.Number == InvalidIdentifierNumber)
                return true;
            if (ex.Message != null && ex.Message.IndexOf("ORA-00904", StringComparison.Ordinal) >= 0)
                return true;
            var aggregate = ex as AggregateException;
            if (aggregate != null)
                return aggregate.InnerExceptions.Any(inner => IsInvalidIdentifier(inner, depth + 1));
            return IsInvalidIdentifier(ex.InnerException, depth + 1);
        }

        private static string SchemaColumns(bool hasOracleMaintained)
        {
            return hasOracleMaintained ? "USERNAME, ORACLE_MAINTAINED" : "USERNAME";
        }

        // includeSystem=false면 내장 스키마가 소유한 결과를 뺀다. SQL에는 상수(내장 스키마 목록)만 넣는다.
        private static string NonSystemOwner(string ownerColumn, bool includeSystem, bool hasOracleMaintained)
        {
            if (includeSystem)
                return "";
            return hasOracleMaintained
                ? " AND " + ownerColumn + " IN (SELECT USERNAME FROM ALL_USERS WHERE ORACLE_MAINTAINED = 'N')"
                : " AND " + ownerColumn + " NOT IN " + InList(KnownSystemSchemas);
        }

        private static SqlQuery Limit(SqlQuery query, int limit)
        {
            if (limit < 0)
                throw new ArgumentOutOfRangeException(nameof(limit), limit, "limit은 0(제한 없음) 이상이어야 합니다.");
            if (limit == 0)
                return query;
            // 11g에는 FETCH FIRST가 없어 ROWNUM을 쓴다. 정렬한 쿼리를 감싸야 정렬한 뒤에 자른다.
            // 한 행 더 읽어 호출자가 "더 있음"을 판단한다(int.MaxValue면 넘치지 않게 그대로).
            query.Sql = "SELECT * FROM (" + query.Sql + ") WHERE ROWNUM <= :limit";
            Bind(query, "limit", limit == int.MaxValue ? limit : limit + 1);
            return query;
        }

        private static void Bind(SqlQuery query, string name, object value)
        {
            query.Parameters.Add(new KeyValuePair<string, object>(name, value ?? DBNull.Value));
        }

        private static string InList(IEnumerable<string> values)
        {
            return "(" + string.Join(", ", values.Select(v => "'" + v.Replace("'", "''") + "'")) + ")";
        }

        // TreeGroups.ObjectTypes·GroupOf와 같은 대응. 이 파일만으로 SQL을 만들 수 있게 따로 둔다.
        private static string[] ObjectTypesOf(string group)
        {
            switch (group)
            {
                case TreeGroups.Table: return new[] { "TABLE" };
                case TreeGroups.View: return new[] { "VIEW" };
                case TreeGroups.Sequence: return new[] { "SEQUENCE" };
                case TreeGroups.Code: return new[] { "PROCEDURE", "FUNCTION", "PACKAGE" };
                default: return null;
            }
        }

        private static string GroupOfType(string objectType)
        {
            switch (objectType)
            {
                case "TABLE": return TreeGroups.Table;
                case "VIEW": return TreeGroups.View;
                case "SEQUENCE": return TreeGroups.Sequence;
                case "PROCEDURE":
                case "FUNCTION":
                case "PACKAGE": return TreeGroups.Code;
                default: return null;
            }
        }

        private static string ReadColumnType(IDataRecord record)
        {
            return FormatColumnType(Text(record, "DATA_TYPE"), Number(record, "DATA_LENGTH"), Number(record, "DATA_PRECISION"),
                Number(record, "DATA_SCALE"), Text(record, "CHAR_USED"), Number(record, "CHAR_LENGTH"));
        }

        private static object Value(IDataRecord record, string name)
        {
            var value = record[name];
            return value is DBNull ? null : value;
        }

        private static string Text(IDataRecord record, string name)
        {
            var value = Value(record, name);
            return value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        // Oracle NUMBER는 공급자·형식 지정에 따라 decimal, int, long, double 등으로 온다.
        private static decimal? Number(IDataRecord record, string name)
        {
            var value = Value(record, name);
            return value == null ? (decimal?)null : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        }

        // decimal은 10.0처럼 소수 자릿수를 품을 수 있어 정수로 잘라 쓴다(길이·정밀도·소수 자릿수는 정수).
        private static string Integer(decimal value)
        {
            return decimal.Truncate(value).ToString(CultureInfo.InvariantCulture);
        }
    }
}

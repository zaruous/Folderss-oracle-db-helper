using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace MyPlugin
{
    /// <summary>
    /// SQL Server 공급자(Microsoft.Data.SqlClient). 접속 프로필의 ServiceName은 데이터베이스 이름, 주소는 host,port.
    /// 암호화는 Optional + TrustServerCertificate(사내 서버의 자체 서명 인증서에서도 붙게). 지원 범위는 PostgreSQL 공급자와 같다
    /// (접속·트리·검색·SQL 실행·GCM 정의서, 테이블 정보·테이블 정의서는 아직 아님).
    /// </summary>
    internal sealed class SqlServerProvider : IDbProvider
    {
        public const string KindName = "mssql";

        private readonly IMetadataProvider _metadata = new SqlServerMetadata();

        public string Kind { get { return KindName; } }
        public string DisplayName { get { return "SQL Server"; } }
        public int DefaultPort { get { return 1433; } }
        public string ServiceLabel { get { return "데이터베이스"; } }
        public ISqlDialect Dialect { get { return SqlDialects.SqlServer; } }
        public IMetadataProvider Metadata { get { return _metadata; } }

        public List<string> Validate(OracleConnectionProfile profile, string label)
        {
            return ProviderRules.ValidateHostPortServiceUser(profile, label, ServiceLabel);
        }

        public string BuildConnectionString(OracleConnectionProfile profile, string password, int timeoutSeconds)
        {
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = profile.Host.Trim() + "," + profile.Port,
                InitialCatalog = profile.ServiceName.Trim(),
                UserID = profile.UserId.Trim(),
                Password = password,
                ConnectTimeout = timeoutSeconds,
                Pooling = false,
                Encrypt = SqlConnectionEncryptOption.Optional,
                TrustServerCertificate = true,
                // 결과 커서를 열어 둔 채 다른 문장·메타데이터 조회를 하려면 MARS가 필요하다
                MultipleActiveResultSets = true,
                // 시간 제한 없음(사용자가 취소한다)
                CommandTimeout = 0,
                ApplicationName = "Folderss DB Helper"
            };
            return builder.ConnectionString;
        }

        public DbConnection CreateConnection(string connectionString)
        {
            return new SqlConnection(connectionString);
        }

        public void ConfigureProcess()
        {
        }

        public string ServerVersionOf(DbConnection connection)
        {
            return connection.ServerVersion;
        }

        public string Address(OracleConnectionProfile profile)
        {
            return ProviderRules.Address(profile);
        }

        /// <summary>연결 문자열에 MARS를 켜므로 리더 여러 개가 된다.</summary>
        public bool SupportsMultipleOpenReaders { get { return true; } }

        /// <summary>SqlCommand.Cancel 뒤의 예외: 번호 0, "Operation cancelled by user".</summary>
        public bool IsCancellation(Exception ex)
        {
            var sql = ex as SqlException;
            return sql != null && sql.Number == 0 && (sql.Message ?? "").IndexOf("cancel", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>전송 오류(233 파이프, 10053·10054·10060 소켓, 64 네트워크 이름)나 심각도 20 이상(연결이 끊긴다).</summary>
        public bool IsBroken(Exception ex)
        {
            var sql = ex as SqlException;
            if (sql == null)
                return false;
            switch (sql.Number)
            {
                case 233:
                case 10053:
                case 10054:
                case 10060:
                case 64:
                    return true;
            }
            return sql.Class >= 20;
        }
    }

    /// <summary>
    /// SQL Server 데이터 사전(sys.*) 조회 SQL과 읽기(순수 로직, 테스트 대상). 결과 열 이름은 Oracle 공급자와 같게 맞춘다.
    /// - 내장 스키마: sys·INFORMATION_SCHEMA·guest·db_*. 바인드는 @owner·@name·@pattern(SqlClient는 이름 앞 @를 붙여 준다).
    /// - 행 제한은 TOP (limit+1) — 정렬된 쿼리를 감싸지 않고 SELECT 안에 넣는다.
    /// - 객체: 테이블(U), 뷰(V), 시퀀스(SO), 프로시저(P·PC), 함수(FN·IF·TF·FS·FT·AF) — 트리 묶음 CODE.
    /// - 열 형식: "nvarchar(50)"·"decimal(10,2)"·"varchar(max)".
    /// </summary>
    internal sealed class SqlServerMetadata : IMetadataProvider
    {
        private const string SystemSchemaCondition = "(s.name IN ('sys', 'INFORMATION_SCHEMA', 'guest') OR s.name LIKE 'db[_]%')";
        private const string FunctionTypes = "('FN', 'IF', 'TF', 'FS', 'FT', 'AF')";
        private const string ProcedureTypes = "('P', 'PC')";

        public bool HasMaintainedFlagFallback { get { return false; } }
        public bool SupportsTableInfo { get { return false; } }

        public SqlQuery CurrentSchema()
        {
            return new SqlQuery { Sql = "SELECT SCHEMA_NAME()" };
        }

        public SqlQuery Schemas(bool hasMaintainedFlag)
        {
            return new SqlQuery
            {
                Sql = "SELECT s.name AS USERNAME, CASE WHEN " + SystemSchemaCondition + " THEN 'Y' ELSE 'N' END AS ORACLE_MAINTAINED"
                    + " FROM sys.schemas s ORDER BY s.name"
            };
        }

        public SchemaInfo ReadSchema(IDataRecord record, bool hasMaintainedFlag)
        {
            return new SchemaInfo
            {
                Name = Text(record, "USERNAME"),
                OracleMaintained = string.Equals(Text(record, "ORACLE_MAINTAINED"), "Y", StringComparison.OrdinalIgnoreCase)
            };
        }

        public SqlQuery GroupCounts(string owner)
        {
            var query = new SqlQuery
            {
                Sql = "SELECT " + TypeCase("o") + " AS OBJECT_TYPE, COUNT(*) AS CNT"
                    + " FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id"
                    + " WHERE s.name = @owner AND o.type IN ('U', 'V', 'SO', 'P', 'PC', 'FN', 'IF', 'TF', 'FS', 'FT', 'AF') AND o.is_ms_shipped = 0"
                    + " GROUP BY " + TypeCase("o")
            };
            Bind(query, "owner", owner);
            return query;
        }

        public SqlQuery Objects(string owner, string group, int limit)
        {
            string types;
            var withRows = false;
            switch (group)
            {
                case TreeGroups.Table:
                    types = "('U')";
                    withRows = true;
                    break;
                case TreeGroups.View:
                    types = "('V')";
                    break;
                case TreeGroups.Sequence:
                    types = "('SO')";
                    break;
                case TreeGroups.Code:
                    types = "('P', 'PC', 'FN', 'IF', 'TF', 'FS', 'FT', 'AF')";
                    break;
                default:
                    throw new ArgumentException("알 수 없는 객체 묶음입니다: " + group, nameof(group));
            }
            var query = new SqlQuery
            {
                Sql = "SELECT " + Top(limit) + "s.name AS OWNER, o.name AS OBJECT_NAME, " + TypeCase("o") + " AS OBJECT_TYPE, 'VALID' AS STATUS, "
                    + (withRows
                        ? "(SELECT SUM(p.rows) FROM sys.partitions p WHERE p.object_id = o.object_id AND p.index_id IN (0, 1)) AS NUM_ROWS"
                        : "CAST(NULL AS BIGINT) AS NUM_ROWS")
                    + " FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id"
                    + " WHERE s.name = @owner AND o.type IN " + types + " AND o.is_ms_shipped = 0"
                    + " ORDER BY o.name"
            };
            Bind(query, "owner", owner);
            return query;
        }

        public DbObjectInfo ReadObject(IDataRecord record)
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

        public SqlQuery Columns(string owner, string objectName)
        {
            var query = new SqlQuery
            {
                Sql = "SELECT s.name AS OWNER, o.name AS TABLE_NAME, c.name AS COLUMN_NAME, " + TypeLabel("c", "t") + " AS TYPE_LABEL,"
                    + " CASE WHEN c.is_nullable = 1 THEN 'Y' ELSE 'N' END AS NULLABLE, c.column_id AS COLUMN_ID,"
                    + " CASE WHEN EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id"
                    + " WHERE i.is_primary_key = 1 AND ic.object_id = c.object_id AND ic.column_id = c.column_id) THEN 1 ELSE 0 END AS IS_PK"
                    + " FROM sys.columns c"
                    + " JOIN sys.objects o ON o.object_id = c.object_id"
                    + " JOIN sys.schemas s ON s.schema_id = o.schema_id"
                    + " JOIN sys.types t ON t.user_type_id = c.user_type_id"
                    + " WHERE s.name = @owner AND o.name = @name"
                    + " ORDER BY c.column_id"
            };
            Bind(query, "owner", owner);
            Bind(query, "name", objectName);
            return query;
        }

        public ColumnInfo ReadColumn(IDataRecord record)
        {
            return new ColumnInfo
            {
                Owner = Text(record, "OWNER"),
                ObjectName = Text(record, "TABLE_NAME"),
                Name = Text(record, "COLUMN_NAME"),
                TypeLabel = Text(record, "TYPE_LABEL") ?? "",
                Nullable = !string.Equals(Text(record, "NULLABLE"), "N", StringComparison.OrdinalIgnoreCase),
                PrimaryKey = (Number(record, "IS_PK") ?? 0) != 0,
                Position = (int)(Number(record, "COLUMN_ID") ?? 0)
            };
        }

        public SqlQuery SearchSchemas(string pattern, bool includeSystem, bool hasMaintainedFlag, int limit)
        {
            var query = new SqlQuery
            {
                Sql = "SELECT " + Top(limit) + "s.name AS USERNAME, CASE WHEN " + SystemSchemaCondition + " THEN 'Y' ELSE 'N' END AS ORACLE_MAINTAINED"
                    + " FROM sys.schemas s WHERE UPPER(s.name) LIKE @pattern ESCAPE '\\'"
                    + (includeSystem ? "" : " AND NOT " + SystemSchemaCondition)
                    + " ORDER BY s.name"
            };
            Bind(query, "pattern", pattern);
            return query;
        }

        public SqlQuery SearchObjects(string pattern, bool includeSystem, bool hasMaintainedFlag, int limit)
        {
            var query = new SqlQuery
            {
                Sql = "SELECT " + Top(limit) + "s.name AS OWNER, " + TypeCase("o") + " AS OBJECT_TYPE, o.name AS OBJECT_NAME"
                    + " FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id"
                    + " WHERE o.type IN ('U', 'V', 'SO', 'P', 'PC', 'FN', 'IF', 'TF', 'FS', 'FT', 'AF') AND o.is_ms_shipped = 0"
                    + " AND UPPER(o.name) LIKE @pattern ESCAPE '\\'"
                    + (includeSystem ? "" : " AND NOT " + SystemSchemaCondition)
                    + " ORDER BY s.name, OBJECT_TYPE, o.name"
            };
            Bind(query, "pattern", pattern);
            return query;
        }

        public SearchHit ReadObjectHit(IDataRecord record)
        {
            return new SearchHit
            {
                Owner = Text(record, "OWNER"),
                ObjectType = Text(record, "OBJECT_TYPE"),
                ObjectName = Text(record, "OBJECT_NAME")
            };
        }

        public SqlQuery SearchColumns(string pattern, bool includeSystem, bool hasMaintainedFlag, int limit)
        {
            var query = new SqlQuery
            {
                Sql = "SELECT " + Top(limit) + "s.name AS OWNER, " + TypeCase("o") + " AS OBJECT_TYPE, o.name AS OBJECT_NAME, c.name AS COLUMN_NAME, "
                    + TypeLabel("c", "t") + " AS TYPE_LABEL"
                    + " FROM sys.columns c"
                    + " JOIN sys.objects o ON o.object_id = c.object_id"
                    + " JOIN sys.schemas s ON s.schema_id = o.schema_id"
                    + " JOIN sys.types t ON t.user_type_id = c.user_type_id"
                    + " WHERE o.type IN ('U', 'V') AND o.is_ms_shipped = 0 AND UPPER(c.name) LIKE @pattern ESCAPE '\\'"
                    + (includeSystem ? "" : " AND NOT " + SystemSchemaCondition)
                    + " ORDER BY s.name, o.name, c.column_id"
            };
            Bind(query, "pattern", pattern);
            return query;
        }

        public SearchHit ReadColumnHit(IDataRecord record)
        {
            var hit = ReadObjectHit(record);
            hit.ColumnName = Text(record, "COLUMN_NAME");
            hit.ColumnType = Text(record, "TYPE_LABEL") ?? "";
            return hit;
        }

        public SqlQuery ServerVersion()
        {
            return new SqlQuery { Sql = "SELECT @@VERSION" };
        }

        /// <summary>"Microsoft SQL Server 2019 (RTM-CU18) …\n\tDeveloper Edition …" → 첫 줄의 " (" 앞까지: "Microsoft SQL Server 2019".</summary>
        public string ReadServerVersion(IDataRecord record)
        {
            var text = (record.IsDBNull(0) ? "" : Convert.ToString(record.GetValue(0), System.Globalization.CultureInfo.InvariantCulture)).Trim();
            var newline = text.IndexOfAny(new[] { '\r', '\n' });
            if (newline > 0)
                text = text.Substring(0, newline).Trim();
            var open = text.IndexOf(" (", StringComparison.Ordinal);
            return open > 0 ? text.Substring(0, open).Trim() : text;
        }

        /// <summary>sys.sql_modules.definition: 만든 그대로의 CREATE 문. 암호화(WITH ENCRYPTION)·CLR 객체는 NULL·행 없음.</summary>
        public SqlQuery Source(string owner, string objectType, string name)
        {
            var query = new SqlQuery
            {
                Sql = objectType == "SEQUENCE"
                    // 값 열은 sql_variant — 글로 바꿔 읽는다. decimal·numeric 시퀀스는 정밀도를 붙인다
                    ? "SELECT 'SEQUENCE' AS PART, QUOTENAME(s.name) + '.' + QUOTENAME(q.name) AS QNAME,"
                        + " TYPE_NAME(q.user_type_id) + CASE WHEN TYPE_NAME(q.user_type_id) IN ('decimal', 'numeric') THEN '(' + CAST(q.precision AS VARCHAR(10)) + ', 0)' ELSE '' END AS TYPE_NAME,"
                        + " CONVERT(VARCHAR(40), q.start_value) AS START_VALUE, CONVERT(VARCHAR(40), q.increment) AS INCREMENT_BY,"
                        + " CONVERT(VARCHAR(40), q.minimum_value) AS MIN_VALUE, CONVERT(VARCHAR(40), q.maximum_value) AS MAX_VALUE,"
                        + " CASE WHEN q.is_cycling = 1 THEN 'Y' ELSE 'N' END AS CYCLE_FLAG, CASE WHEN q.is_cached = 1 THEN 'Y' ELSE 'N' END AS CACHE_FLAG,"
                        + " CAST(q.cache_size AS VARCHAR(20)) AS CACHE_SIZE, CONVERT(VARCHAR(40), q.current_value) AS LAST_VALUE"
                        + " FROM sys.sequences q JOIN sys.schemas s ON s.schema_id = q.schema_id WHERE s.name = @owner AND q.name = @name"
                    : "SELECT o.type_desc AS PART, m.definition AS TEXT"
                        + " FROM sys.sql_modules m JOIN sys.objects o ON o.object_id = m.object_id JOIN sys.schemas s ON s.schema_id = o.schema_id"
                        + " WHERE s.name = @owner AND o.name = @name"
            };
            Bind(query, "owner", owner);
            Bind(query, "name", name);
            return query;
        }

        public string BuildSource(string owner, string objectType, string name, IList<SourcePart> parts)
        {
            if (objectType == "SEQUENCE")
            {
                return ObjectSource.SequenceDdl(parts,
                    r => "현재 값(current_value): " + (r.Field("LAST_VALUE") ?? "없음"),
                    r => r.Field("QNAME"),
                    r => new[]
                    {
                        "AS " + r.Field("TYPE_NAME"),
                        "START WITH " + r.Field("START_VALUE"),
                        "INCREMENT BY " + r.Field("INCREMENT_BY"),
                        "MINVALUE " + r.Field("MIN_VALUE"),
                        "MAXVALUE " + r.Field("MAX_VALUE"),
                        r.Field("CYCLE_FLAG") == "Y" ? "CYCLE" : "NO CYCLE",
                        r.Field("CACHE_FLAG") != "Y" ? "NO CACHE" : r.Field("CACHE_SIZE") == null ? "CACHE" : "CACHE " + r.Field("CACHE_SIZE")
                    });
            }
            return ObjectSource.JoinStatements(parts, "");
        }

        // ---------- 도우미 ----------

        private static string TypeCase(string alias)
        {
            return "CASE WHEN " + alias + ".type = 'U' THEN 'TABLE' WHEN " + alias + ".type = 'V' THEN 'VIEW' WHEN " + alias + ".type = 'SO' THEN 'SEQUENCE'"
                + " WHEN " + alias + ".type IN " + ProcedureTypes + " THEN 'PROCEDURE' WHEN " + alias + ".type IN " + FunctionTypes + " THEN 'FUNCTION' ELSE " + alias + ".type_desc END";
        }

        // "nvarchar(50)"·"nvarchar(max)"·"decimal(10,2)"·"datetime2(7)"·"int". 문자 길이는 n 형식이면 바이트/2
        private static string TypeLabel(string c, string t)
        {
            return "CASE"
                + " WHEN " + t + ".name IN ('varchar', 'char', 'varbinary', 'binary') THEN " + t + ".name + '(' + CASE WHEN " + c + ".max_length = -1 THEN 'max' ELSE CAST(" + c + ".max_length AS VARCHAR(10)) END + ')'"
                + " WHEN " + t + ".name IN ('nvarchar', 'nchar') THEN " + t + ".name + '(' + CASE WHEN " + c + ".max_length = -1 THEN 'max' ELSE CAST(" + c + ".max_length / 2 AS VARCHAR(10)) END + ')'"
                + " WHEN " + t + ".name IN ('decimal', 'numeric') THEN " + t + ".name + '(' + CAST(" + c + ".precision AS VARCHAR(10)) + ',' + CAST(" + c + ".scale AS VARCHAR(10)) + ')'"
                + " WHEN " + t + ".name IN ('datetime2', 'time', 'datetimeoffset') THEN " + t + ".name + '(' + CAST(" + c + ".scale AS VARCHAR(10)) + ')'"
                + " ELSE " + t + ".name END";
        }

        /// <summary>"TOP (limit+1) "(0은 제한 없음 → "", 음수는 예외).</summary>
        public static string Top(int limit)
        {
            if (limit < 0)
                throw new ArgumentOutOfRangeException(nameof(limit), limit, "limit은 0(제한 없음) 이상이어야 합니다.");
            if (limit == 0)
                return "";
            var n = limit == int.MaxValue ? limit : limit + 1;
            return "TOP (" + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + ") ";
        }

        private static void Bind(SqlQuery query, string name, object value)
        {
            query.Parameters.Add(new KeyValuePair<string, object>(name, value ?? DBNull.Value));
        }

        private static object Value(IDataRecord record, string name)
        {
            var value = record[name];
            return value == null || value is DBNull ? null : value;
        }

        private static string Text(IDataRecord record, string name)
        {
            var value = Value(record, name);
            return value == null ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        private static decimal? Number(IDataRecord record, string name)
        {
            var value = Value(record, name);
            return value == null ? (decimal?)null : Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;

namespace MyPlugin
{
    /// <summary>
    /// PostgreSQL 데이터 사전(pg_catalog) 조회 SQL과 읽기(순수 로직, 테스트 대상).
    /// - 결과 열 이름은 Oracle 공급자와 같게 따옴표 별칭("OWNER"·"OBJECT_NAME" …)으로 맞춰 읽기 함수와 트리 로직을 공유한다.
    /// - 내장 스키마: pg_catalog·information_schema·pg_toast·pg_temp*. hasMaintainedFlag 인자는 쓰지 않는다(폴백 없음).
    /// - 행 제한은 LIMIT(limit+1행). 바인드는 :owner·:name·:pattern·:limit(Npgsql은 ':이름'을 받는다).
    /// - 객체 종류: 테이블(relkind r·p·f), 뷰(v·m), 시퀀스(S), 함수(prokind f)·프로시저(p) — 트리 묶음 CODE.
    /// - 열 형식은 format_type()으로 "character varying(20)"·"numeric(10,2)" 그대로.
    /// </summary>
    internal sealed class PostgresMetadata : IMetadataProvider
    {
        private const string SystemSchemaCondition = "(n.nspname IN ('pg_catalog', 'information_schema', 'pg_toast') OR n.nspname LIKE 'pg\\_toast\\_%' OR n.nspname LIKE 'pg\\_temp\\_%')";
        private const string TableKinds = "('r', 'p', 'f')";
        private const string ViewKinds = "('v', 'm')";

        public bool HasMaintainedFlagFallback { get { return false; } }
        public bool SupportsTableInfo { get { return false; } }

        public SqlQuery CurrentSchema()
        {
            return new SqlQuery { Sql = "SELECT current_schema()" };
        }

        public SqlQuery Schemas(bool hasMaintainedFlag)
        {
            return new SqlQuery
            {
                Sql = "SELECT n.nspname AS \"USERNAME\", CASE WHEN " + SystemSchemaCondition + " THEN 'Y' ELSE 'N' END AS \"ORACLE_MAINTAINED\""
                    + " FROM pg_catalog.pg_namespace n ORDER BY n.nspname"
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
                Sql = "SELECT t.\"OBJECT_TYPE\", COUNT(*) AS \"CNT\" FROM ("
                    + " SELECT " + RelKindCase("c") + " AS \"OBJECT_TYPE\""
                    + " FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace"
                    + " WHERE n.nspname = :owner AND c.relkind IN ('r', 'p', 'f', 'v', 'm', 'S')"
                    + " UNION ALL"
                    + " SELECT DISTINCT " + ProKindCase("p") + " || '|' || p.proname"
                    + " FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace"
                    + " WHERE n.nspname = :owner AND p.prokind IN ('f', 'p')"
                    + ") t GROUP BY t.\"OBJECT_TYPE\""
            };
            Bind(query, "owner", owner);
            // 함수는 오버로드가 있어 이름당 하나로 세려고 "종류|이름"으로 DISTINCT 했다 — 묶음 수로 합칠 때 종류만 남긴다(ReadGroupCount가 '|' 앞을 본다)
            query.Sql = "SELECT split_part(g.\"OBJECT_TYPE\", '|', 1) AS \"OBJECT_TYPE\", SUM(g.\"CNT\") AS \"CNT\" FROM (" + query.Sql + ") g GROUP BY split_part(g.\"OBJECT_TYPE\", '|', 1)";
            return query;
        }

        public SqlQuery Objects(string owner, string group, int limit)
        {
            string sql;
            switch (group)
            {
                case TreeGroups.Table:
                    sql = ClassObjects(TableKinds, "'TABLE'", true);
                    break;
                case TreeGroups.View:
                    sql = ClassObjects(ViewKinds, "'VIEW'", false);
                    break;
                case TreeGroups.Sequence:
                    sql = ClassObjects("('S')", "'SEQUENCE'", false);
                    break;
                case TreeGroups.Code:
                    sql = "SELECT DISTINCT n.nspname AS \"OWNER\", p.proname AS \"OBJECT_NAME\", " + ProKindCase("p") + " AS \"OBJECT_TYPE\","
                        + " 'VALID' AS \"STATUS\", CAST(NULL AS BIGINT) AS \"NUM_ROWS\""
                        + " FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace"
                        + " WHERE n.nspname = :owner AND p.prokind IN ('f', 'p')"
                        + " ORDER BY p.proname";
                    break;
                default:
                    throw new ArgumentException("알 수 없는 객체 묶음입니다: " + group, nameof(group));
            }
            var query = new SqlQuery { Sql = sql };
            Bind(query, "owner", owner);
            return Limit(query, limit);
        }

        // 테이블·뷰·시퀀스: 통계 행 수는 테이블만(reltuples; 통계가 없으면 -1 → NULL)
        private static string ClassObjects(string kinds, string typeLiteral, bool withRows)
        {
            return "SELECT n.nspname AS \"OWNER\", c.relname AS \"OBJECT_NAME\", " + typeLiteral + " AS \"OBJECT_TYPE\", 'VALID' AS \"STATUS\", "
                + (withRows ? "CASE WHEN c.reltuples >= 0 THEN CAST(c.reltuples AS BIGINT) END" : "CAST(NULL AS BIGINT)") + " AS \"NUM_ROWS\""
                + " FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace"
                + " WHERE n.nspname = :owner AND c.relkind IN " + kinds
                + " ORDER BY c.relname";
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
                Sql = "SELECT n.nspname AS \"OWNER\", c.relname AS \"TABLE_NAME\", a.attname AS \"COLUMN_NAME\","
                    + " pg_catalog.format_type(a.atttypid, a.atttypmod) AS \"TYPE_LABEL\","
                    + " CASE WHEN a.attnotnull THEN 'N' ELSE 'Y' END AS \"NULLABLE\", a.attnum AS \"COLUMN_ID\","
                    + " CASE WHEN EXISTS (SELECT 1 FROM pg_catalog.pg_constraint k WHERE k.conrelid = c.oid AND k.contype = 'p' AND a.attnum = ANY (k.conkey))"
                    + " THEN 1 ELSE 0 END AS \"IS_PK\""
                    + " FROM pg_catalog.pg_attribute a"
                    + " JOIN pg_catalog.pg_class c ON c.oid = a.attrelid"
                    + " JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace"
                    + " WHERE n.nspname = :owner AND c.relname = :name AND a.attnum > 0 AND NOT a.attisdropped"
                    + " ORDER BY a.attnum"
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
                Sql = "SELECT n.nspname AS \"USERNAME\", CASE WHEN " + SystemSchemaCondition + " THEN 'Y' ELSE 'N' END AS \"ORACLE_MAINTAINED\""
                    + " FROM pg_catalog.pg_namespace n WHERE UPPER(n.nspname) LIKE :pattern ESCAPE '\\'"
                    + (includeSystem ? "" : " AND NOT " + SystemSchemaCondition)
                    + " ORDER BY n.nspname"
            };
            Bind(query, "pattern", pattern);
            return Limit(query, limit);
        }

        public SqlQuery SearchObjects(string pattern, bool includeSystem, bool hasMaintainedFlag, int limit)
        {
            var notSystem = includeSystem ? "" : " AND NOT " + SystemSchemaCondition;
            var query = new SqlQuery
            {
                Sql = "SELECT * FROM ("
                    + " SELECT n.nspname AS \"OWNER\", " + RelKindCase("c") + " AS \"OBJECT_TYPE\", c.relname AS \"OBJECT_NAME\""
                    + " FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace"
                    + " WHERE c.relkind IN ('r', 'p', 'f', 'v', 'm', 'S') AND UPPER(c.relname) LIKE :pattern ESCAPE '\\'" + notSystem
                    + " UNION"
                    + " SELECT n.nspname, " + ProKindCase("p") + ", p.proname"
                    + " FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace"
                    + " WHERE p.prokind IN ('f', 'p') AND UPPER(p.proname) LIKE :pattern ESCAPE '\\'" + notSystem
                    + ") h ORDER BY h.\"OWNER\", h.\"OBJECT_TYPE\", h.\"OBJECT_NAME\""
            };
            Bind(query, "pattern", pattern);
            return Limit(query, limit);
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
                Sql = "SELECT n.nspname AS \"OWNER\", " + RelKindCase("c") + " AS \"OBJECT_TYPE\", c.relname AS \"OBJECT_NAME\", a.attname AS \"COLUMN_NAME\","
                    + " pg_catalog.format_type(a.atttypid, a.atttypmod) AS \"TYPE_LABEL\""
                    + " FROM pg_catalog.pg_attribute a"
                    + " JOIN pg_catalog.pg_class c ON c.oid = a.attrelid"
                    + " JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace"
                    + " WHERE c.relkind IN ('r', 'p', 'f', 'v', 'm') AND a.attnum > 0 AND NOT a.attisdropped"
                    + " AND UPPER(a.attname) LIKE :pattern ESCAPE '\\'"
                    + (includeSystem ? "" : " AND NOT " + SystemSchemaCondition)
                    + " ORDER BY n.nspname, c.relname, a.attnum"
            };
            Bind(query, "pattern", pattern);
            return Limit(query, limit);
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
            return new SqlQuery { Sql = "SELECT version()" };
        }

        /// <summary>"PostgreSQL 16.15 (Ubuntu …) on x86_64-…" → "PostgreSQL 16.15 (Ubuntu …)"(" on " 앞까지).</summary>
        public string ReadServerVersion(IDataRecord record)
        {
            var text = (record.IsDBNull(0) ? "" : Convert.ToString(record.GetValue(0), CultureInfo.InvariantCulture)).Trim();
            var on = text.IndexOf(" on ", StringComparison.Ordinal);
            return on > 0 ? text.Substring(0, on).Trim() : text;
        }

        // ---------- 도우미 ----------

        private static string RelKindCase(string alias)
        {
            return "CASE " + alias + ".relkind WHEN 'v' THEN 'VIEW' WHEN 'm' THEN 'VIEW' WHEN 'S' THEN 'SEQUENCE' ELSE 'TABLE' END";
        }

        private static string ProKindCase(string alias)
        {
            return "CASE " + alias + ".prokind WHEN 'p' THEN 'PROCEDURE' ELSE 'FUNCTION' END";
        }

        /// <summary>limit+1행(호출자가 "더 있음" 판단). 0은 제한 없음, 음수는 예외.</summary>
        public static SqlQuery Limit(SqlQuery query, int limit)
        {
            if (limit < 0)
                throw new ArgumentOutOfRangeException(nameof(limit), limit, "limit은 0(제한 없음) 이상이어야 합니다.");
            if (limit == 0)
                return query;
            query.Sql += " LIMIT :limit";
            Bind(query, "limit", limit == int.MaxValue ? limit : limit + 1);
            return query;
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
            return value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static decimal? Number(IDataRecord record, string name)
        {
            var value = Value(record, name);
            return value == null ? (decimal?)null : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        }
    }
}

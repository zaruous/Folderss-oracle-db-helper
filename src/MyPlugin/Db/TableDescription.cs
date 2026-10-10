using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MyPlugin
{
    /// <summary>F4 테이블 정보 한 벌.</summary>
    internal sealed class TableDescription
    {
        public string Owner { get; set; }
        public string Name { get; set; }
        /// <summary>TABLE 또는 VIEW.</summary>
        public string Type { get; set; }
        public string Status { get; set; }
        /// <summary>동의어로 찾았으면 그 동의어("PUBLIC.EMP" 등). 아니면 null.</summary>
        public string Via { get; set; }
        public string Comment { get; set; }
        /// <summary>통계의 행 수(ALL_TABLES.NUM_ROWS). 통계가 없거나 뷰면 null.</summary>
        public long? NumRows { get; set; }
        public DateTime? LastAnalyzed { get; set; }
        public DateTime? Created { get; set; }
        public DateTime? LastDdl { get; set; }
        /// <summary>테이블스페이스(ALL_TABLES.TABLESPACE_NAME). 정의서만 읽는다(F4는 null).</summary>
        public string Tablespace { get; set; }
        public List<DescribedColumn> Columns { get; } = new List<DescribedColumn>();
        public List<IndexDescription> Indexes { get; } = new List<IndexDescription>();
        public List<ConstraintDescription> Constraints { get; } = new List<ConstraintDescription>();
    }

    internal sealed class DescribedColumn
    {
        public ColumnInfo Column { get; set; }
        public string Comment { get; set; }
        /// <summary>기본값(ALL_TAB_COLUMNS.DATA_DEFAULT, 앞뒤 공백 뺌). 정의서만 읽는다(F4는 null).</summary>
        public string Default { get; set; }
    }

    internal sealed class IndexDescription
    {
        public string Name { get; set; }
        public bool Unique { get; set; }
        public string Type { get; set; }
        public string Status { get; set; }
        /// <summary>열 목록("A, B DESC").</summary>
        public string Columns { get; set; }
        /// <summary>열 하나씩(순서대로). 정의서만 채운다.</summary>
        public List<string> ColumnNames { get; } = new List<string>();
    }

    internal sealed class ConstraintDescription
    {
        public string Name { get; set; }
        /// <summary>P·U·R·C</summary>
        public string Type { get; set; }
        public string Status { get; set; }
        public string Columns { get; set; }
        /// <summary>외래 키가 가리키는 테이블("OWNER.TABLE"). 아니면 null.</summary>
        public string Reference { get; set; }
        /// <summary>열 하나씩(순서대로). 정의서만 채운다.</summary>
        public List<string> ColumnNames { get; } = new List<string>();
        /// <summary>외래 키가 가리키는 열(순서대로). 정의서만 채운다.</summary>
        public List<string> ReferenceColumns { get; } = new List<string>();
        /// <summary>검사 조건(ALL_CONSTRAINTS.SEARCH_CONDITION). 정의서만 읽는다.</summary>
        public string Condition { get; set; }
        /// <summary>외래 키의 삭제 규칙(NO ACTION·CASCADE·SET NULL). 정의서만 읽는다.</summary>
        public string DeleteRule { get; set; }
        /// <summary>기본 키·고유 제약이 쓰는 인덱스 이름(ALL_CONSTRAINTS.INDEX_NAME). 정의서만 읽는다.</summary>
        public string IndexName { get; set; }
    }

    /// <summary>F4 대상 후보 한 줄(ResolveTable 결과).</summary>
    internal sealed class ResolvedObject
    {
        public string Owner { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }
        public string Via { get; set; }
        /// <summary>1 지정·현재 스키마, 2 그 스키마의 동의어, 3 PUBLIC 동의어, 4 다른 스키마.</summary>
        public int Priority { get; set; }
    }

    /// <summary>F4 대상 찾기 결과: 찾은 객체 하나, 또는 못 찾음(Object null), 또는 여러 스키마에 있어 정할 수 없음(Ambiguous).</summary>
    internal sealed class TableResolution
    {
        public ResolvedObject Object { get; set; }
        public List<string> AmbiguousOwners { get; } = new List<string>();
    }

    /// <summary>
    /// F4 테이블 정보: 대상 찾기(이름·스키마·동의어)와 정보 조회 SQL·읽기(순수 로직, 테스트 대상)와 조회 순서(DescribeAsync).
    /// ALL_* 뷰만 쓴다(권한 있는 객체만 보인다). LONG 열(DATA_DEFAULT·SEARCH_CONDITION)은 읽지 않는다.
    /// </summary>
    internal static class TableInfo
    {
        private const string CurrentSchema = "SYS_CONTEXT('USERENV','CURRENT_SCHEMA')";
        private const string TableOrView = "IN ('TABLE','VIEW')";

        /// <summary>
        /// 대상 후보. 열: OWNER, OBJECT_NAME, OBJECT_TYPE, VIA, PRI(작을수록 먼저). PRI·OWNER 순.
        /// owner가 있으면 그 스키마의 테이블·뷰(1)와 그 스키마 동의어가 가리키는 테이블·뷰(2).
        /// 없으면 현재 스키마(1) → 현재 스키마 동의어(2) → PUBLIC 동의어(3) → 다른 스키마의 같은 이름(4).
        /// </summary>
        public static SqlQuery ResolveTable(string owner, string name)
        {
            var self = owner == null ? CurrentSchema : ":owner";
            var sql = "SELECT OWNER, OBJECT_NAME, OBJECT_TYPE, VIA, PRI FROM ("
                + "SELECT o.OWNER, o.OBJECT_NAME, o.OBJECT_TYPE, CAST(NULL AS VARCHAR2(300)) AS VIA, 1 AS PRI FROM ALL_OBJECTS o"
                + " WHERE o.OWNER = " + self + " AND o.OBJECT_NAME = :name AND o.OBJECT_TYPE " + TableOrView
                + " UNION ALL "
                + Synonyms(self, 2);
            if (owner == null)
            {
                sql += " UNION ALL " + Synonyms("'PUBLIC'", 3)
                    + " UNION ALL SELECT o.OWNER, o.OBJECT_NAME, o.OBJECT_TYPE, CAST(NULL AS VARCHAR2(300)), 4 FROM ALL_OBJECTS o"
                    + " WHERE o.OBJECT_NAME = :name AND o.OBJECT_TYPE " + TableOrView + " AND o.OWNER <> " + CurrentSchema;
            }
            var query = new SqlQuery { Sql = sql + ") ORDER BY PRI, OWNER" };
            if (owner != null)
                query.Parameters.Add(new KeyValuePair<string, object>("owner", owner));
            query.Parameters.Add(new KeyValuePair<string, object>("name", name));
            return query;
        }

        private static string Synonyms(string synonymOwner, int priority)
        {
            return "SELECT o.OWNER, o.OBJECT_NAME, o.OBJECT_TYPE, s.OWNER || '.' || s.SYNONYM_NAME, " + priority.ToString(CultureInfo.InvariantCulture)
                + " FROM ALL_SYNONYMS s JOIN ALL_OBJECTS o ON o.OWNER = s.TABLE_OWNER AND o.OBJECT_NAME = s.TABLE_NAME AND o.OBJECT_TYPE " + TableOrView
                + " WHERE s.OWNER = " + synonymOwner + " AND s.SYNONYM_NAME = :name AND s.DB_LINK IS NULL";
        }

        public static ResolvedObject ReadResolved(IDataRecord record)
        {
            return new ResolvedObject
            {
                Owner = Text(record, "OWNER"),
                Name = Text(record, "OBJECT_NAME"),
                Type = Text(record, "OBJECT_TYPE"),
                Via = Text(record, "VIA"),
                Priority = (int)(Number(record, "PRI") ?? 9)
            };
        }

        /// <summary>
        /// 후보 중 하나를 고른다: 가장 앞 순위. 그 순위가 "다른 스키마"(4)인데 스키마가 여럿이면 정하지 않는다(Ambiguous).
        /// 후보가 없으면 Object = null.
        /// </summary>
        public static TableResolution Resolve(IEnumerable<ResolvedObject> candidates)
        {
            var resolution = new TableResolution();
            var list = (candidates ?? Enumerable.Empty<ResolvedObject>()).Where(c => c != null).ToList();
            if (list.Count == 0)
                return resolution;
            var best = list.Min(c => c.Priority);
            var top = list.Where(c => c.Priority == best).ToList();
            var owners = top.Select(c => c.Owner).Distinct(StringComparer.Ordinal).ToList();
            if (best >= 4 && owners.Count > 1)
            {
                resolution.AmbiguousOwners.AddRange(owners.OrderBy(o => o, StringComparer.Ordinal));
                return resolution;
            }
            resolution.Object = top.OrderBy(c => c.Owner, StringComparer.Ordinal).First();
            return resolution;
        }

        /// <summary>객체 요약: OBJECT_TYPE, STATUS, CREATED, LAST_DDL_TIME, NUM_ROWS, LAST_ANALYZED, COMMENTS.</summary>
        public static SqlQuery Summary(string owner, string name)
        {
            var query = new SqlQuery
            {
                Sql = "SELECT o.OBJECT_TYPE, o.STATUS, o.CREATED, o.LAST_DDL_TIME, t.NUM_ROWS, t.LAST_ANALYZED, c.COMMENTS"
                    + " FROM ALL_OBJECTS o"
                    + " LEFT JOIN ALL_TABLES t ON t.OWNER = o.OWNER AND t.TABLE_NAME = o.OBJECT_NAME"
                    + " LEFT JOIN ALL_TAB_COMMENTS c ON c.OWNER = o.OWNER AND c.TABLE_NAME = o.OBJECT_NAME"
                    + " WHERE o.OWNER = :owner AND o.OBJECT_NAME = :name AND o.OBJECT_TYPE " + TableOrView
            };
            Bind(query, owner, name);
            return query;
        }

        /// <summary>열(OracleMetadata.Columns와 같은 열) + COMMENTS.</summary>
        public static SqlQuery Columns(string owner, string name)
        {
            var columns = OracleMetadata.Columns(owner, name);
            var sql = columns.Sql.Replace(" FROM ALL_TAB_COLUMNS c", ", cc.COMMENTS FROM ALL_TAB_COLUMNS c"
                + " LEFT JOIN ALL_COL_COMMENTS cc ON cc.OWNER = c.OWNER AND cc.TABLE_NAME = c.TABLE_NAME AND cc.COLUMN_NAME = c.COLUMN_NAME");
            var query = new SqlQuery { Sql = sql };
            query.Parameters.AddRange(columns.Parameters);
            return query;
        }

        public static DescribedColumn ReadColumn(IDataRecord record)
        {
            return new DescribedColumn { Column = OracleMetadata.ReadColumn(record), Comment = Text(record, "COMMENTS") };
        }

        /// <summary>인덱스: INDEX_NAME, UNIQUENESS, INDEX_TYPE, STATUS, COLS(열 순서대로 "A, B DESC"). 이름 순.</summary>
        public static SqlQuery Indexes(string owner, string name)
        {
            var query = new SqlQuery
            {
                Sql = "SELECT i.INDEX_NAME, i.UNIQUENESS, i.INDEX_TYPE, i.STATUS,"
                    + " LISTAGG(ic.COLUMN_NAME || CASE WHEN ic.DESCEND = 'DESC' THEN ' DESC' END, ', ') WITHIN GROUP (ORDER BY ic.COLUMN_POSITION) AS COLS"
                    + " FROM ALL_INDEXES i JOIN ALL_IND_COLUMNS ic ON ic.INDEX_OWNER = i.OWNER AND ic.INDEX_NAME = i.INDEX_NAME"
                    + " WHERE i.TABLE_OWNER = :owner AND i.TABLE_NAME = :name"
                    + " GROUP BY i.INDEX_NAME, i.UNIQUENESS, i.INDEX_TYPE, i.STATUS ORDER BY i.INDEX_NAME"
            };
            Bind(query, owner, name);
            return query;
        }

        public static IndexDescription ReadIndex(IDataRecord record)
        {
            return new IndexDescription
            {
                Name = Text(record, "INDEX_NAME"),
                Unique = string.Equals(Text(record, "UNIQUENESS"), "UNIQUE", StringComparison.OrdinalIgnoreCase),
                Type = Text(record, "INDEX_TYPE"),
                Status = Text(record, "STATUS"),
                Columns = Text(record, "COLS")
            };
        }

        /// <summary>
        /// 제약 조건: 기본 키(P)·고유(U)·외래 키(R)·검사(C, 시스템이 이름 붙인 NOT NULL 검사는 뺌 — 열 목록의 NULL 허용에 이미 보인다).
        /// 열: CONSTRAINT_NAME, CONSTRAINT_TYPE, STATUS, COLS, R_OWNER, R_TABLE. P·U·R·C 순, 이름 순.
        /// </summary>
        public static SqlQuery Constraints(string owner, string name)
        {
            var query = new SqlQuery
            {
                Sql = "SELECT c.CONSTRAINT_NAME, c.CONSTRAINT_TYPE, c.STATUS,"
                    + " (SELECT LISTAGG(cc.COLUMN_NAME, ', ') WITHIN GROUP (ORDER BY cc.POSITION) FROM ALL_CONS_COLUMNS cc"
                    + " WHERE cc.OWNER = c.OWNER AND cc.CONSTRAINT_NAME = c.CONSTRAINT_NAME AND cc.TABLE_NAME = c.TABLE_NAME) AS COLS,"
                    + " r.OWNER AS R_OWNER, r.TABLE_NAME AS R_TABLE"
                    + " FROM ALL_CONSTRAINTS c"
                    + " LEFT JOIN ALL_CONSTRAINTS r ON r.OWNER = c.R_OWNER AND r.CONSTRAINT_NAME = c.R_CONSTRAINT_NAME"
                    + " WHERE c.OWNER = :owner AND c.TABLE_NAME = :name AND c.CONSTRAINT_TYPE IN ('P','U','R','C')"
                    + " AND NOT (c.CONSTRAINT_TYPE = 'C' AND c.GENERATED = 'GENERATED NAME')"
                    + " ORDER BY DECODE(c.CONSTRAINT_TYPE, 'P', 1, 'U', 2, 'R', 3, 4), c.CONSTRAINT_NAME"
            };
            Bind(query, owner, name);
            return query;
        }

        public static ConstraintDescription ReadConstraint(IDataRecord record)
        {
            var refOwner = Text(record, "R_OWNER");
            var refTable = Text(record, "R_TABLE");
            return new ConstraintDescription
            {
                Name = Text(record, "CONSTRAINT_NAME"),
                Type = Text(record, "CONSTRAINT_TYPE"),
                Status = Text(record, "STATUS"),
                Columns = Text(record, "COLS"),
                Reference = refTable == null ? null : (refOwner == null ? refTable : refOwner + "." + refTable)
            };
        }

        /// <summary>제약 조건 종류 이름: 기본 키·고유·외래 키·검사.</summary>
        public static string ConstraintTypeLabel(string type)
        {
            switch (type)
            {
                case "P": return "기본 키";
                case "U": return "고유";
                case "R": return "외래 키";
                case "C": return "검사";
                default: return type ?? "";
            }
        }

        /// <summary>
        /// 정보 조회 전체: 대상 찾기 → 요약 → 열 → (테이블이면) 인덱스·제약 조건. 찾지 못했거나 정할 수 없으면 error에 사람이 읽을 문장, 정보는 null.
        /// </summary>
        public static async Task<TableDescription> DescribeAsync(DbSession session, string owner, string name, CancellationToken token, Action<string> error)
        {
            var candidates = await session.QueryAsync(ResolveTable(owner, name), ReadResolved, token).ConfigureAwait(true);
            var resolution = Resolve(candidates);
            var display = owner == null ? name : owner + "." + name;
            if (resolution.AmbiguousOwners.Count > 0)
            {
                error(NotUniqueMessage(display, resolution.AmbiguousOwners, name));
                return null;
            }
            if (resolution.Object == null)
            {
                error(NotFoundMessage(display));
                return null;
            }
            var target = resolution.Object;
            var info = new TableDescription { Owner = target.Owner, Name = target.Name, Type = target.Type, Via = target.Via };
            await session.QueryAsync(Summary(target.Owner, target.Name), r =>
            {
                ReadSummary(r, info);
                return true;
            }, token).ConfigureAwait(true);
            info.Columns.AddRange(await session.QueryAsync(Columns(target.Owner, target.Name), ReadColumn, token).ConfigureAwait(true));
            if (string.Equals(target.Type, "TABLE", StringComparison.Ordinal))
            {
                info.Indexes.AddRange(await session.QueryAsync(Indexes(target.Owner, target.Name), ReadIndex, token).ConfigureAwait(true));
                info.Constraints.AddRange(await session.QueryAsync(Constraints(target.Owner, target.Name), ReadConstraint, token).ConfigureAwait(true));
            }
            return info;
        }

        public static string NotFoundMessage(string display)
        {
            return "'" + display + "'은(는) 테이블·뷰가 아니거나 볼 권한이 없습니다(현재 스키마·동의어·다른 스키마를 찾아봤습니다).";
        }

        public static string NotUniqueMessage(string display, IEnumerable<string> owners, string name)
        {
            var list = owners.ToList();
            return "'" + display + "'이(가) 여러 스키마에 있습니다: " + string.Join(", ", list)
                + ". 스키마를 붙여(예: " + list.First() + "." + name + ") 다시 F4를 누르세요.";
        }

        // ---------- 읽기 도우미 ----------

        private static void Bind(SqlQuery query, string owner, string name)
        {
            query.Parameters.Add(new KeyValuePair<string, object>("owner", owner));
            query.Parameters.Add(new KeyValuePair<string, object>("name", name));
        }

        internal static string Text(IDataRecord record, string column)
        {
            var value = record[column];
            return value == null || value is DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        internal static decimal? Number(IDataRecord record, string column)
        {
            var value = record[column];
            if (value == null || value is DBNull)
                return null;
            return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        }

        internal static DateTime? Date(IDataRecord record, string column)
        {
            var value = record[column];
            if (value == null || value is DBNull)
                return null;
            return value is DateTime time ? time : Convert.ToDateTime(value, CultureInfo.InvariantCulture);
        }

        /// <summary>요약 한 줄을 info에 읽어 넣는다.</summary>
        internal static void ReadSummary(IDataRecord record, TableDescription info)
        {
            info.Status = Text(record, "STATUS");
            info.Created = Date(record, "CREATED");
            info.LastDdl = Date(record, "LAST_DDL_TIME");
            var rows = Number(record, "NUM_ROWS");
            info.NumRows = rows.HasValue ? (long)rows.Value : (long?)null;
            info.LastAnalyzed = Date(record, "LAST_ANALYZED");
            info.Comment = Text(record, "COMMENTS");
        }
    }
}

using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MyPlugin
{
    /// <summary>정의서 대상 후보 한 줄(대상 선택 창의 목록).</summary>
    internal sealed class TableSpecCandidate
    {
        public string Owner { get; set; }
        public string Name { get; set; }
        /// <summary>TABLE 또는 VIEW.</summary>
        public string Type { get; set; }
        public long? NumRows { get; set; }
        public string Comment { get; set; }
    }

    /// <summary>정의서 대상 테이블 하나(스키마·이름).</summary>
    internal struct TableKey : IEquatable<TableKey>
    {
        public TableKey(string owner, string name)
        {
            Owner = owner;
            Name = name;
        }

        public string Owner { get; }
        public string Name { get; }

        public bool Equals(TableKey other)
        {
            return string.Equals(Owner, other.Owner, StringComparison.Ordinal) && string.Equals(Name, other.Name, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return obj is TableKey && Equals((TableKey)obj);
        }

        public override int GetHashCode()
        {
            return ((Owner ?? "").GetHashCode() * 397) ^ (Name ?? "").GetHashCode();
        }

        public override string ToString()
        {
            return Owner + "." + Name;
        }
    }

    /// <summary>
    /// 테이블 정의서용 데이터 사전 조회(순수 SQL·읽기는 테스트 대상, LoadAsync는 실제 DB).
    /// F4(TableInfo)는 테이블 하나씩 여러 번 묻지만, 정의서는 스키마마다 이름을 ChunkSize개씩 묶어 4번(요약·열·인덱스·제약 조건)만 묻는다.
    /// 열 목록은 CHR(31)로 이어 받아 나눈다(이름에 ", "가 들어 있어도 섞이지 않게). LONG 열(DATA_DEFAULT·SEARCH_CONDITION)도 읽는다.
    /// ALL_* 뷰만 쓴다(권한 있는 객체만 보인다). 휴지통(BIN$…) 객체는 뺀다.
    /// </summary>
    internal static class TableSpecQueries
    {
        /// <summary>IN 목록 하나에 넣는 이름 수(Oracle 한도 1000보다 작게).</summary>
        public const int ChunkSize = 500;

        private const string ListSeparator = "CHR(31)";
        private const char ListSeparatorChar = '\u001F';

        /// <summary>
        /// 대상 후보: 스키마 하나의 테이블(과 뷰), 이름 순. 열: OWNER, OBJECT_NAME, OBJECT_TYPE, NUM_ROWS, COMMENTS.
        /// </summary>
        public static SqlQuery Candidates(string owner, bool includeViews)
        {
            var query = new SqlQuery
            {
                Sql = "SELECT o.OWNER, o.OBJECT_NAME, o.OBJECT_TYPE, t.NUM_ROWS, c.COMMENTS"
                    + " FROM ALL_OBJECTS o"
                    + " LEFT JOIN ALL_TABLES t ON t.OWNER = o.OWNER AND t.TABLE_NAME = o.OBJECT_NAME"
                    + " LEFT JOIN ALL_TAB_COMMENTS c ON c.OWNER = o.OWNER AND c.TABLE_NAME = o.OBJECT_NAME"
                    + " WHERE o.OWNER = :owner AND o.OBJECT_TYPE IN (" + (includeViews ? "'TABLE','VIEW'" : "'TABLE'") + ")"
                    + " AND o.OBJECT_NAME NOT LIKE 'BIN$%'"
                    + " ORDER BY o.OBJECT_NAME"
            };
            query.Parameters.Add(new KeyValuePair<string, object>("owner", owner));
            return query;
        }

        public static TableSpecCandidate ReadCandidate(IDataRecord record)
        {
            var rows = TableInfo.Number(record, "NUM_ROWS");
            return new TableSpecCandidate
            {
                Owner = TableInfo.Text(record, "OWNER"),
                Name = TableInfo.Text(record, "OBJECT_NAME"),
                Type = TableInfo.Text(record, "OBJECT_TYPE"),
                NumRows = rows.HasValue ? (long)rows.Value : (long?)null,
                Comment = TableInfo.Text(record, "COMMENTS")
            };
        }

        /// <summary>요약: OBJECT_NAME, OBJECT_TYPE, STATUS, CREATED, LAST_DDL_TIME, NUM_ROWS, LAST_ANALYZED, TABLESPACE_NAME, COMMENTS.</summary>
        public static SqlQuery Summaries(string owner, IReadOnlyList<string> names)
        {
            return InList(
                "SELECT o.OBJECT_NAME, o.OBJECT_TYPE, o.STATUS, o.CREATED, o.LAST_DDL_TIME, t.NUM_ROWS, t.LAST_ANALYZED, t.TABLESPACE_NAME, c.COMMENTS"
                + " FROM ALL_OBJECTS o"
                + " LEFT JOIN ALL_TABLES t ON t.OWNER = o.OWNER AND t.TABLE_NAME = o.OBJECT_NAME"
                + " LEFT JOIN ALL_TAB_COMMENTS c ON c.OWNER = o.OWNER AND c.TABLE_NAME = o.OBJECT_NAME"
                + " WHERE o.OWNER = :owner AND o.OBJECT_TYPE IN ('TABLE','VIEW') AND o.OBJECT_NAME IN ({0})"
                + " ORDER BY o.OBJECT_NAME", owner, names);
        }

        /// <summary>열: TABLE_NAME, COLUMN_NAME, DATA_TYPE, DATA_LENGTH, DATA_PRECISION, DATA_SCALE, NULLABLE, COLUMN_ID, CHAR_USED, CHAR_LENGTH, DATA_DEFAULT(LONG), COMMENTS.</summary>
        public static SqlQuery Columns(string owner, IReadOnlyList<string> names)
        {
            return InList(
                "SELECT c.TABLE_NAME, c.COLUMN_NAME, c.DATA_TYPE, c.DATA_LENGTH, c.DATA_PRECISION, c.DATA_SCALE, c.NULLABLE, c.COLUMN_ID,"
                + " c.CHAR_USED, c.CHAR_LENGTH, c.DATA_DEFAULT, cc.COMMENTS"
                + " FROM ALL_TAB_COLUMNS c"
                + " LEFT JOIN ALL_COL_COMMENTS cc ON cc.OWNER = c.OWNER AND cc.TABLE_NAME = c.TABLE_NAME AND cc.COLUMN_NAME = c.COLUMN_NAME"
                + " WHERE c.OWNER = :owner AND c.TABLE_NAME IN ({0})"
                + " ORDER BY c.TABLE_NAME, c.COLUMN_ID", owner, names);
        }

        /// <summary>인덱스: TABLE_NAME, INDEX_NAME, UNIQUENESS, INDEX_TYPE, STATUS, COLS(CHR(31)로 이음, 열 순서).</summary>
        public static SqlQuery Indexes(string owner, IReadOnlyList<string> names)
        {
            return InList(
                "SELECT i.TABLE_NAME, i.INDEX_NAME, i.UNIQUENESS, i.INDEX_TYPE, i.STATUS,"
                + " LISTAGG(ic.COLUMN_NAME || CASE WHEN ic.DESCEND = 'DESC' THEN ' DESC' END, " + ListSeparator + ") WITHIN GROUP (ORDER BY ic.COLUMN_POSITION) AS COLS"
                + " FROM ALL_INDEXES i JOIN ALL_IND_COLUMNS ic ON ic.INDEX_OWNER = i.OWNER AND ic.INDEX_NAME = i.INDEX_NAME"
                + " WHERE i.TABLE_OWNER = :owner AND i.TABLE_NAME IN ({0})"
                + " GROUP BY i.TABLE_NAME, i.INDEX_NAME, i.UNIQUENESS, i.INDEX_TYPE, i.STATUS"
                + " ORDER BY i.TABLE_NAME, i.INDEX_NAME", owner, names);
        }

        /// <summary>
        /// 제약 조건(F4와 같은 범위 — 시스템 이름의 NOT NULL 검사는 뺌): TABLE_NAME, CONSTRAINT_NAME, CONSTRAINT_TYPE, STATUS, SEARCH_CONDITION(LONG),
        /// DELETE_RULE, INDEX_NAME, COLS·R_COLS(CHR(31)로 이음, 순서대로), R_OWNER, R_TABLE. P·U·R·C 순.
        /// </summary>
        public static SqlQuery Constraints(string owner, IReadOnlyList<string> names)
        {
            return InList(
                "SELECT c.TABLE_NAME, c.CONSTRAINT_NAME, c.CONSTRAINT_TYPE, c.STATUS, c.SEARCH_CONDITION, c.DELETE_RULE, c.INDEX_NAME,"
                + " (SELECT LISTAGG(cc.COLUMN_NAME, " + ListSeparator + ") WITHIN GROUP (ORDER BY cc.POSITION) FROM ALL_CONS_COLUMNS cc"
                + " WHERE cc.OWNER = c.OWNER AND cc.CONSTRAINT_NAME = c.CONSTRAINT_NAME AND cc.TABLE_NAME = c.TABLE_NAME) AS COLS,"
                + " r.OWNER AS R_OWNER, r.TABLE_NAME AS R_TABLE,"
                + " (SELECT LISTAGG(rc.COLUMN_NAME, " + ListSeparator + ") WITHIN GROUP (ORDER BY rc.POSITION) FROM ALL_CONS_COLUMNS rc"
                + " WHERE rc.OWNER = r.OWNER AND rc.CONSTRAINT_NAME = r.CONSTRAINT_NAME AND rc.TABLE_NAME = r.TABLE_NAME) AS R_COLS"
                + " FROM ALL_CONSTRAINTS c"
                + " LEFT JOIN ALL_CONSTRAINTS r ON r.OWNER = c.R_OWNER AND r.CONSTRAINT_NAME = c.R_CONSTRAINT_NAME"
                + " WHERE c.OWNER = :owner AND c.TABLE_NAME IN ({0}) AND c.CONSTRAINT_TYPE IN ('P','U','R','C')"
                + " AND NOT (c.CONSTRAINT_TYPE = 'C' AND c.GENERATED = 'GENERATED NAME')"
                + " ORDER BY c.TABLE_NAME, DECODE(c.CONSTRAINT_TYPE, 'P', 1, 'U', 2, 'R', 3, 4), c.CONSTRAINT_NAME", owner, names);
        }

        // sql의 {0}에 :t0, :t1, … 을 넣고 owner·이름을 바인드한다
        private static SqlQuery InList(string sql, string owner, IReadOnlyList<string> names)
        {
            if (names == null || names.Count == 0 || names.Count > ChunkSize)
                throw new ArgumentException("이름은 1~" + ChunkSize + "개여야 합니다.", nameof(names));
            var binds = names.Select((n, i) => ":t" + i.ToString(CultureInfo.InvariantCulture));
            var query = new SqlQuery { Sql = sql.Replace("{0}", string.Join(", ", binds)) };
            query.Parameters.Add(new KeyValuePair<string, object>("owner", owner));
            for (var i = 0; i < names.Count; i++)
                query.Parameters.Add(new KeyValuePair<string, object>("t" + i.ToString(CultureInfo.InvariantCulture), names[i]));
            return query;
        }

        /// <summary>CHR(31)로 이은 목록을 나눈다. null이면 빈 목록.</summary>
        public static List<string> SplitList(string value)
        {
            return string.IsNullOrEmpty(value) ? new List<string>() : value.Split(ListSeparatorChar).ToList();
        }

        public static void ReadSummary(IDataRecord record, TableDescription info)
        {
            TableInfo.ReadSummary(record, info);
            info.Type = TableInfo.Text(record, "OBJECT_TYPE");
            info.Tablespace = TableInfo.Text(record, "TABLESPACE_NAME");
        }

        public static DescribedColumn ReadColumn(IDataRecord record, string owner)
        {
            var column = new ColumnInfo
            {
                Owner = owner,
                ObjectName = TableInfo.Text(record, "TABLE_NAME"),
                Name = TableInfo.Text(record, "COLUMN_NAME"),
                TypeLabel = OracleMetadata.FormatColumnType(
                    TableInfo.Text(record, "DATA_TYPE"),
                    TableInfo.Number(record, "DATA_LENGTH"),
                    TableInfo.Number(record, "DATA_PRECISION"),
                    TableInfo.Number(record, "DATA_SCALE"),
                    TableInfo.Text(record, "CHAR_USED"),
                    TableInfo.Number(record, "CHAR_LENGTH")),
                Nullable = !string.Equals(TableInfo.Text(record, "NULLABLE"), "N", StringComparison.OrdinalIgnoreCase),
                Position = (int)(TableInfo.Number(record, "COLUMN_ID") ?? 0)
            };
            var defaultValue = TableInfo.Text(record, "DATA_DEFAULT");
            return new DescribedColumn
            {
                Column = column,
                Comment = TableInfo.Text(record, "COMMENTS"),
                Default = string.IsNullOrWhiteSpace(defaultValue) ? null : defaultValue.Trim()
            };
        }

        public static IndexDescription ReadIndex(IDataRecord record)
        {
            var index = TableInfo.ReadIndex(record);
            index.ColumnNames.AddRange(SplitList(index.Columns));
            index.Columns = string.Join(", ", index.ColumnNames);
            return index;
        }

        public static ConstraintDescription ReadConstraint(IDataRecord record)
        {
            var constraint = TableInfo.ReadConstraint(record);
            constraint.ColumnNames.AddRange(SplitList(constraint.Columns));
            constraint.Columns = string.Join(", ", constraint.ColumnNames);
            constraint.ReferenceColumns.AddRange(SplitList(TableInfo.Text(record, "R_COLS")));
            var condition = TableInfo.Text(record, "SEARCH_CONDITION");
            constraint.Condition = string.IsNullOrWhiteSpace(condition) ? null : condition.Trim();
            constraint.DeleteRule = constraint.Type == "R" ? TableInfo.Text(record, "DELETE_RULE") : null;
            constraint.IndexName = TableInfo.Text(record, "INDEX_NAME");
            return constraint;
        }

        /// <summary>DB 제품·버전(표지의 DBMS / 버전): PRODUCT, VERSION. PRODUCT_COMPONENT_VERSION은 누구나 읽을 수 있다.</summary>
        public static SqlQuery ServerVersion()
        {
            return new SqlQuery { Sql = "SELECT PRODUCT, VERSION FROM PRODUCT_COMPONENT_VERSION WHERE PRODUCT LIKE 'Oracle%' ORDER BY PRODUCT" };
        }

        /// <summary>"Oracle Database 12c Enterprise Edition 12.1.0.2.0"(PRODUCT 끝 공백·" - Production" 정리). 없으면 "".</summary>
        public static string ReadServerVersion(IDataRecord record)
        {
            var product = (TableInfo.Text(record, "PRODUCT") ?? "").Trim();
            var version = (TableInfo.Text(record, "VERSION") ?? "").Trim();
            if (product.EndsWith(" - Production", StringComparison.Ordinal))
                product = product.Substring(0, product.Length - " - Production".Length).TrimEnd();
            return (product + " " + version).Trim();
        }

        /// <summary>DB 버전을 읽는다. 권한이 없거나 실패하면 ""(표지의 한 칸이라 정의서 만들기를 멈추지 않는다).</summary>
        public static async Task<string> LoadServerVersionAsync(DbSession session, CancellationToken token)
        {
            try
            {
                var rows = await session.QueryAsync(ServerVersion(), ReadServerVersion, token).ConfigureAwait(true);
                return rows.FirstOrDefault() ?? "";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return "";
            }
        }

        /// <summary>targets를 스키마별로 묶고 ChunkSize개씩 나눈다(처음 나온 스키마 순, 이름은 받은 순). 같은 대상은 한 번만.</summary>
        public static List<KeyValuePair<string, List<string>>> Chunks(IEnumerable<TableKey> targets)
        {
            var chunks = new List<KeyValuePair<string, List<string>>>();
            foreach (var group in (targets ?? Enumerable.Empty<TableKey>()).Distinct().GroupBy(t => t.Owner, StringComparer.Ordinal))
            {
                var names = group.Select(t => t.Name).ToList();
                for (var i = 0; i < names.Count; i += ChunkSize)
                    chunks.Add(new KeyValuePair<string, List<string>>(group.Key, names.Skip(i).Take(ChunkSize).ToList()));
            }
            return chunks;
        }

        /// <summary>
        /// 대상 테이블들의 정의서 데이터. 결과는 targets 순서(찾지 못한 대상 — 그 사이 지웠거나 권한이 없음 — 은 빠진다).
        /// progress에는 "SCOTT 열 읽는 중 (1/3)" 같은 문장을 보낸다.
        /// </summary>
        public static async Task<List<TableDescription>> LoadAsync(DbSession session, IReadOnlyList<TableKey> targets, IProgress<string> progress, CancellationToken token)
        {
            var found = new Dictionary<TableKey, TableDescription>();
            var chunks = Chunks(targets);
            for (var c = 0; c < chunks.Count; c++)
            {
                var owner = chunks[c].Key;
                var names = chunks[c].Value;
                var step = chunks.Count > 1 ? " (" + (c + 1).ToString(CultureInfo.InvariantCulture) + "/" + chunks.Count.ToString(CultureInfo.InvariantCulture) + ")" : "";
                Report(progress, owner + " 테이블 " + names.Count.ToString(CultureInfo.InvariantCulture) + "개 요약 읽는 중" + step);
                var tables = new Dictionary<string, TableDescription>(StringComparer.Ordinal);
                await session.QueryAsync(Summaries(owner, names), r =>
                {
                    var info = new TableDescription { Owner = owner, Name = TableInfo.Text(r, "OBJECT_NAME") };
                    ReadSummary(r, info);
                    // 같은 이름의 TABLE과 VIEW는 없지만(같은 이름 공간), 혹시 있으면 먼저 온 것
                    if (!tables.ContainsKey(info.Name))
                        tables.Add(info.Name, info);
                    return true;
                }, token).ConfigureAwait(true);

                Report(progress, owner + " 열 읽는 중" + step);
                foreach (var column in await session.QueryAsync(Columns(owner, names), r => ReadColumn(r, owner), token).ConfigureAwait(true))
                {
                    TableDescription info;
                    if (tables.TryGetValue(column.Column.ObjectName ?? "", out info))
                        info.Columns.Add(column);
                }

                Report(progress, owner + " 인덱스 읽는 중" + step);
                await session.QueryAsync(Indexes(owner, names), r =>
                {
                    TableDescription info;
                    if (tables.TryGetValue(TableInfo.Text(r, "TABLE_NAME") ?? "", out info))
                        info.Indexes.Add(ReadIndex(r));
                    return true;
                }, token).ConfigureAwait(true);

                Report(progress, owner + " 제약 조건 읽는 중" + step);
                await session.QueryAsync(Constraints(owner, names), r =>
                {
                    TableDescription info;
                    if (tables.TryGetValue(TableInfo.Text(r, "TABLE_NAME") ?? "", out info))
                        info.Constraints.Add(ReadConstraint(r));
                    return true;
                }, token).ConfigureAwait(true);

                foreach (var info in tables.Values)
                {
                    foreach (var column in info.Columns)
                        column.Column.PrimaryKey = info.Constraints.Any(k => k.Type == "P" && k.ColumnNames.Contains(column.Column.Name));
                    found[new TableKey(owner, info.Name)] = info;
                }
            }
            var result = new List<TableDescription>();
            foreach (var key in targets.Distinct())
            {
                TableDescription info;
                if (found.TryGetValue(key, out info))
                    result.Add(info);
            }
            return result;
        }

        private static void Report(IProgress<string> progress, string text)
        {
            if (progress != null)
                progress.Report(text);
        }
    }
}

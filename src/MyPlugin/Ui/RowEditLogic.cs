using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MyPlugin
{
    /// <summary>결과 그리드 행의 편집 상태.</summary>
    internal enum RowEditState
    {
        Unchanged,
        /// <summary>값을 고쳤고 아직 DB에 보내지 않음.</summary>
        Modified,
        /// <summary>삭제 표시(저장하면 DELETE).</summary>
        Deleted,
        /// <summary>[행 추가]·[복제]로 만든 새 행(저장하면 INSERT).</summary>
        Inserted
    }

    /// <summary>결과 열의 값을 어떤 .NET 값으로 바인드할지(표시 문자열 → DB 값).</summary>
    public enum ColumnValueKind
    {
        Text,
        Number,
        DateTime,
        /// <summary>LOB·RAW·시간대 있는 TIMESTAMP·INTERVAL 등: 표시 문자열로 값을 되살릴 수 없어 편집하지 않는다.</summary>
        Unsupported
    }

    /// <summary>결과 편집이 UPDATE·DELETE의 WHERE로 행을 찾는 방법.</summary>
    internal enum RowKeyKind
    {
        /// <summary>행을 찾을 키가 없음(편집할 수 없음).</summary>
        None,
        PrimaryKey,
        /// <summary>모든 열이 NOT NULL인 UNIQUE 제약·고유 인덱스.</summary>
        UniqueKey,
        /// <summary>Oracle ROWID(조회 SELECT 끝에 붙여 가져오고 그리드에는 보이지 않는다).</summary>
        RowId
    }

    /// <summary>
    /// 행을 찾을 키: 기본 키 → NOT NULL 고유 키 → (Oracle 테이블만) ROWID 순으로 고른 것(<see cref="RowEditLogic.ChooseKey"/>).
    /// </summary>
    internal sealed class RowKey
    {
        public RowKeyKind Kind { get; set; }
        /// <summary>고유 키의 제약·인덱스 이름. 기본 키·ROWID면 null.</summary>
        public string Name { get; set; }
        /// <summary>키 열 이름(키 순서). ROWID·없음이면 비어 있다.</summary>
        public string[] Columns { get; set; } = Array.Empty<string>();
        /// <summary>Kind가 None일 때 편집할 수 없는 이유.</summary>
        public string Reason { get; set; }
    }

    /// <summary>
    /// 빠른 조회(F1) 결과를 편집·저장할 대상 테이블. 어느 열로 행을 찾는지(기본 키·고유 키 열, 또는 ROWID), 열마다 값 종류를 안다.
    /// 행을 찾을 키가 없거나 결과에 키 열이 빠졌으면 CanEdit = false(DisabledReason에 이유).
    /// </summary>
    internal sealed class EditableTable
    {
        public string Owner { get; set; }
        public string Name { get; set; }
        /// <summary>이 결과를 조회한 WHERE 조건(편집 막대의 WHERE 칸). 없으면 "".</summary>
        public string Where { get; set; } = "";
        /// <summary>바인드 변수 접두: Oracle·PostgreSQL ':', SQL Server '@'.</summary>
        public string ParameterPrefix { get; set; } = ":";
        public IReadOnlyList<ResultColumn> Columns { get; set; }
        /// <summary>키(기본 키·고유 키) 열의 결과 열 위치. ROWID로 찾으면 비어 있다.</summary>
        public int[] KeyIndexes { get; set; } = Array.Empty<int>();
        public RowKeyKind KeyKind { get; set; } = RowKeyKind.PrimaryKey;
        /// <summary>고유 키의 제약·인덱스 이름(KeyKind가 UniqueKey일 때).</summary>
        public string KeyName { get; set; }
        public ColumnValueKind[] Kinds { get; set; } = Array.Empty<ColumnValueKind>();
        /// <summary>편집할 수 없는 이유. 편집할 수 있으면 null.</summary>
        public string DisabledReason { get; set; }

        public bool CanEdit
        {
            get { return DisabledReason == null && (KeyIndexes.Length > 0 || KeyKind == RowKeyKind.RowId); }
        }

        /// <summary>ROWID로 행을 찾는다: 조회 SELECT 끝에 ROWID를 붙였고, 행마다 ResultGridRow.RowId에 떼어 둔다.</summary>
        public bool UsesRowId
        {
            get { return KeyKind == RowKeyKind.RowId; }
        }

        /// <summary>편집 막대에 보일 키 설명: 기본 키면 null, "고유 키 UK_EMP_EMAIL"·"ROWID".</summary>
        public string KeyNote
        {
            get
            {
                if (KeyKind == RowKeyKind.UniqueKey)
                    return "고유 키 " + (KeyName ?? "");
                return KeyKind == RowKeyKind.RowId ? "ROWID" : null;
            }
        }

        public bool IsKey(int index)
        {
            return Array.IndexOf(KeyIndexes, index) >= 0;
        }

        public ColumnValueKind KindAt(int index)
        {
            return index >= 0 && index < Kinds.Length ? Kinds[index] : ColumnValueKind.Unsupported;
        }
    }

    /// <summary>
    /// 결과 행 편집(빠른 조회 F1)의 순수 로직: 열 형식 → 값 종류, 입력 검사·변환, UPDATE·DELETE 문(바인드 변수) 만들기.
    /// 값은 그리드의 표시 문자열(ValueFormatter.Format)이다 — 숫자는 InvariantCulture, 날짜는 "yyyy-MM-dd HH:mm:ss[.fffffff]".
    /// WHERE는 행 하나만 가리키는 키로만 쓴다: 기본 키 → NOT NULL 고유 키 → Oracle ROWID(<see cref="ChooseKey"/>).
    /// 키가 없으면 편집하지 않는다 — 같은 값의 행을 함께 바꾸지 않게.
    /// </summary>
    internal static class RowEditLogic
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static readonly string[] DateFormats =
        {
            "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd",
            "yyyy-MM-ddTHH:mm:ss.FFFFFFF", "yyyy-MM-ddTHH:mm:ss",
            "yyyy/MM/dd HH:mm:ss", "yyyy/MM/dd"
        };

        private static readonly HashSet<string> TextTypeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Oracle
            "VARCHAR2", "NVARCHAR2", "CHAR", "NCHAR", "VARCHAR",
            // PostgreSQL
            "CHARACTER VARYING", "CHARACTER", "BPCHAR", "TEXT",
            // SQL Server
            "NVARCHAR", "NTEXT",
            // .NET 형식 이름(SQLite 등)
            "STRING"
        };

        private static readonly HashSet<string> DateTypeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "DATE", "TIMESTAMP", "TIMESTAMP WITHOUT TIME ZONE", "DATETIME", "DATETIME2", "SMALLDATETIME"
        };

        // ValueFormatter가 긴 값을 자를 때 붙이는 꼬리: "…(전체 n자)", "…(전체 n바이트)"
        private static readonly Regex TruncatedSuffix = new Regex(@"…\(전체 \d+(자|바이트)\)$", RegexOptions.CultureInvariant);

        /// <summary>바인드 변수 접두: SQL Server는 '@', 그 밖(Oracle·PostgreSQL·SQLite)은 ':'.</summary>
        public static string ParameterPrefix(string providerKind)
        {
            return string.Equals(providerKind, "mssql", StringComparison.OrdinalIgnoreCase) ? "@" : ":";
        }

        /// <summary>열 머리 형식(NUMBER(7,2), VARCHAR2(10 CHAR), TIMESTAMP(6) WITH TIME ZONE …) → 값 종류.</summary>
        public static ColumnValueKind KindOf(string typeLabel)
        {
            if (string.IsNullOrWhiteSpace(typeLabel))
                return ColumnValueKind.Unsupported;
            // 괄호 안(크기·정밀도)을 떼고 나머지는 남긴다: "TIMESTAMP(6) WITH TIME ZONE" → "TIMESTAMP WITH TIME ZONE"
            var name = Regex.Replace(typeLabel, @"\s*\([^)]*\)", "").Trim();
            name = Regex.Replace(name, @"\s+", " ");
            if (TextTypeNames.Contains(name))
                return ColumnValueKind.Text;
            if (DateTypeNames.Contains(name))
                return ColumnValueKind.DateTime;
            if (ValueFormatter.IsNumericTypeName(name))
                return ColumnValueKind.Number;
            return ColumnValueKind.Unsupported;
        }

        /// <summary>그리드 값이 잘려 보이는 값("…(전체 n자)")이면 true — 그대로 저장하면 원래 값을 잃는다.</summary>
        public static bool IsTruncated(string value)
        {
            return value != null && TruncatedSuffix.IsMatch(value);
        }

        /// <summary>이 칸을 고칠 수 있는지: 형식을 지원하고 값이 잘려 보이지 않을 때.</summary>
        public static bool CanEditValue(ColumnValueKind kind, string value)
        {
            return kind != ColumnValueKind.Unsupported && !IsTruncated(value);
        }

        /// <summary>칸을 고칠 수 없는 이유(편집 창에 보임). 고칠 수 있으면 null.</summary>
        public static string EditBlockReason(ColumnValueKind kind, string value)
        {
            if (kind == ColumnValueKind.Unsupported)
                return "이 형식은 편집할 수 없습니다";
            if (IsTruncated(value))
                return "값이 잘려 보여 편집할 수 없습니다";
            return null;
        }

        /// <summary>행을 찾을 키가 없을 때 편집 막대에 보이는 이유.</summary>
        public const string NoKeyReason = "기본 키나 NOT NULL 고유 키가 없어 편집할 수 없습니다.";

        /// <summary>ROWID 키일 때 빠른 조회 SELECT 끝에 붙이는 열 이름(그리드에 보이기 전에 뗀다).</summary>
        public const string RowIdAlias = "DBH$ROWID";

        /// <summary>
        /// 행을 찾을 키를 고른다.
        /// 1) 기본 키(ColumnInfo.PrimaryKey, 열 순서).
        /// 2) 없으면 고유 키(UNIQUE 제약·고유 인덱스) 가운데 모든 열이 NOT NULL이고 바인드할 수 있는 형식인 것 — 열이 가장 적은 것, 같으면 이름순.
        ///    NULL을 허용하는 고유 키는 NULL끼리 겹칠 수 있어 뺀다. 데이터 사전 열 목록에 없는 열(식 인덱스의 숨은 열 등)이 든 키도 뺀다.
        /// 3) 그것도 없고 Oracle 보통 테이블(rowIdTable)이면 ROWID. PostgreSQL ctid는 UPDATE마다 바뀌고 SQL Server는 행 주소가 없어 쓰지 않는다.
        /// 4) 아니면 None(Reason에 이유).
        /// </summary>
        public static RowKey ChooseKey(IEnumerable<ColumnInfo> metadata, IEnumerable<UniqueKeyColumn> uniqueKeys, string providerKind, bool rowIdTable)
        {
            var columns = (metadata ?? Enumerable.Empty<ColumnInfo>()).Where(c => c != null).ToList();
            var primary = columns.Where(c => c.PrimaryKey).Select(c => c.Name ?? "").ToArray();
            if (primary.Length > 0)
                return new RowKey { Kind = RowKeyKind.PrimaryKey, Columns = primary };
            var unique = (uniqueKeys ?? Enumerable.Empty<UniqueKeyColumn>())
                .Where(k => k != null && !string.IsNullOrEmpty(k.KeyName) && !string.IsNullOrEmpty(k.ColumnName))
                .GroupBy(k => k.KeyName, StringComparer.Ordinal)
                .Select(g => new { Name = g.Key, Columns = g.OrderBy(k => k.Position).Select(k => k.ColumnName).Distinct(StringComparer.Ordinal).ToArray() })
                .Where(k => k.Columns.All(name => IsUsableKeyColumn(columns, name)))
                .OrderBy(k => k.Columns.Length)
                .ThenBy(k => k.Name, StringComparer.Ordinal)
                .FirstOrDefault();
            if (unique != null)
                return new RowKey { Kind = RowKeyKind.UniqueKey, Name = unique.Name, Columns = unique.Columns };
            if (rowIdTable && string.Equals(providerKind, "oracle", StringComparison.OrdinalIgnoreCase))
                return new RowKey { Kind = RowKeyKind.RowId };
            return new RowKey { Kind = RowKeyKind.None, Reason = NoKeyReason };
        }

        // 고유 키 열로 쓸 수 있는지: 데이터 사전에 있고 NOT NULL이며 표시 글을 값으로 되살려 바인드할 수 있는 형식
        private static bool IsUsableKeyColumn(List<ColumnInfo> columns, string name)
        {
            var column = columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));
            return column != null && !column.Nullable && KindOf(column.TypeLabel) != ColumnValueKind.Unsupported;
        }

        /// <summary>기본 키만 보고 편집 대상을 정한다(고유 키·ROWID 후보 없이).</summary>
        public static EditableTable Describe(string owner, string name, IList<ResultColumn> columns, IEnumerable<ColumnInfo> metadata, string parameterPrefix)
        {
            return Describe(owner, name, columns, metadata, parameterPrefix, null);
        }

        /// <summary>
        /// 편집 대상을 정한다: 고른 키(<see cref="ChooseKey"/>, null이면 기본 키만 보고 고름)의 열을 결과 열 위치에 맞춘다(이름 그대로, 없으면 대소문자 무시).
        /// 키가 없거나 결과에 그 열이 없으면 CanEdit = false. ROWID 키는 결과 열에 맞추지 않는다(ROWID는 Values 밖, ResultGridRow.RowId).
        /// </summary>
        public static EditableTable Describe(string owner, string name, IList<ResultColumn> columns, IEnumerable<ColumnInfo> metadata, string parameterPrefix, RowKey key)
        {
            var table = new EditableTable
            {
                Owner = owner,
                Name = name,
                ParameterPrefix = parameterPrefix ?? ":",
                Columns = columns != null ? new List<ResultColumn>(columns) : new List<ResultColumn>()
            };
            table.Kinds = table.Columns.Select(c => KindOf(c.TypeLabel)).ToArray();
            key = key ?? ChooseKey(metadata, null, null, false);
            table.KeyKind = key.Kind;
            table.KeyName = key.Name;
            if (key.Kind == RowKeyKind.None)
            {
                table.DisabledReason = key.Reason ?? NoKeyReason;
                return table;
            }
            if (key.Kind == RowKeyKind.RowId)
                return table;
            var label = key.Kind == RowKeyKind.UniqueKey ? "고유 키" : "기본 키";
            var indexes = new List<int>();
            foreach (var column in key.Columns)
            {
                var index = IndexOfColumn(table.Columns, column);
                if (index < 0)
                {
                    table.DisabledReason = "결과에 " + label + " 열 '" + column + "'이(가) 없어 편집할 수 없습니다.";
                    return table;
                }
                if (table.Kinds[index] == ColumnValueKind.Unsupported)
                {
                    table.DisabledReason = label + " 열 '" + column + "'의 형식(" + (table.Columns[index].TypeLabel ?? "") + ")으로는 행을 찾을 수 없어 편집할 수 없습니다.";
                    return table;
                }
                indexes.Add(index);
            }
            table.KeyIndexes = indexes.ToArray();
            return table;
        }

        /// <summary>편집할 수 없는 대상(데이터 사전을 읽지 못함 등).</summary>
        public static EditableTable Unavailable(string owner, string name, string reason)
        {
            return new EditableTable { Owner = owner, Name = name, DisabledReason = reason ?? "편집할 수 없습니다." };
        }

        private static int IndexOfColumn(IReadOnlyList<ResultColumn> columns, string name)
        {
            for (var i = 0; i < columns.Count; i++)
            {
                if (string.Equals(columns[i].Name, name, StringComparison.Ordinal))
                    return i;
            }
            for (var i = 0; i < columns.Count; i++)
            {
                if (string.Equals(columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        /// <summary>입력 검사. 문제가 없으면 null, 있으면 사람이 읽을 이유. null(NULL)은 언제나 통과.</summary>
        public static string Validate(ColumnValueKind kind, string text)
        {
            if (text == null)
                return null;
            switch (kind)
            {
                case ColumnValueKind.Number:
                    return IsNumber(text) ? null : "숫자가 아닙니다: " + text;
                case ColumnValueKind.DateTime:
                    return TryParseDate(text, out _) ? null : "날짜 형식이 아닙니다(yyyy-MM-dd HH:mm:ss): " + text;
                case ColumnValueKind.Unsupported:
                    return "이 형식은 편집할 수 없습니다.";
                default:
                    return null;
            }
        }

        /// <summary>표시 문자열 → 바인드 값. null은 DBNull. 숫자는 decimal(너무 크면 문자열 그대로 — DB가 변환), 날짜는 DateTime(Unspecified).</summary>
        public static object ToDbValue(ColumnValueKind kind, string text)
        {
            if (text == null)
                return DBNull.Value;
            switch (kind)
            {
                case ColumnValueKind.Number:
                    decimal number;
                    if (decimal.TryParse(text.Trim(), NumberStyles.Float, Inv, out number))
                        return number;
                    return text.Trim();
                case ColumnValueKind.DateTime:
                    DateTime date;
                    if (TryParseDate(text, out date))
                        return date;
                    return text;
                default:
                    return text;
            }
        }

        private static bool IsNumber(string text)
        {
            double d;
            return double.TryParse(text.Trim(), NumberStyles.Float, Inv, out d) && !double.IsNaN(d) && !double.IsInfinity(d);
        }

        private static bool TryParseDate(string text, out DateTime value)
        {
            return DateTime.TryParseExact((text ?? "").Trim(), DateFormats, Inv, DateTimeStyles.None, out value);
        }

        private static readonly string[] TimeFormats =
        {
            @"hh\:mm\:ss\.FFFFFFF", @"hh\:mm\:ss", @"hh\:mm", @"h\:mm\:ss", @"h\:mm"
        };

        /// <summary>날짜 + 시각 → 그리드 표시 형식. 초 미만이 있으면 소수점 뒤 끝의 0을 뺀 7자리 이내.</summary>
        public static string Compose(DateTime date, TimeSpan time)
        {
            var combined = date.Date + time;
            var format = combined.Ticks % TimeSpan.TicksPerSecond != 0 ? "yyyy-MM-dd HH:mm:ss.FFFFFFF" : "yyyy-MM-dd HH:mm:ss";
            return combined.ToString(format, Inv);
        }

        /// <summary>칸의 글에서 시각 부분. 읽을 수 없으면 00:00:00.</summary>
        public static TimeSpan TimePartOf(string text)
        {
            DateTime current;
            return TryParseDate(text, out current) ? current.TimeOfDay : TimeSpan.Zero;
        }

        /// <summary>시각 글(HH:mm, HH:mm:ss, HH:mm:ss.fff…) → TimeSpan. 24시간제, 24:00 이상은 거부.</summary>
        public static bool TryParseTime(string text, out TimeSpan time)
        {
            return TimeSpan.TryParseExact((text ?? "").Trim(), TimeFormats, Inv, TimeSpanStyles.None, out time) && time < TimeSpan.FromDays(1);
        }

        /// <summary>시각 TimeSpan → 달력 팝업의 시각 칸 글(HH:mm:ss, 초 미만이 있으면 소수점까지).</summary>
        public static string TimeText(TimeSpan time)
        {
            return time.Ticks % TimeSpan.TicksPerSecond != 0
                ? time.ToString(@"hh\:mm\:ss\.FFFFFFF", Inv)
                : time.ToString(@"hh\:mm\:ss", Inv);
        }

        /// <summary>편집 창 📅 달력: month가 든 달을 일요일부터 6주(42일)로 편다. 앞뒤 달의 날도 들어간다.</summary>
        public static DateTime[] MonthGrid(DateTime month)
        {
            var first = new DateTime(month.Year, month.Month, 1);
            var start = first.AddDays(-(int)first.DayOfWeek);
            var days = new DateTime[42];
            for (var i = 0; i < days.Length; i++)
                days[i] = start.AddDays(i);
            return days;
        }

        /// <summary>편집 창 📅: 달력이 처음 보일 날짜(칸의 글이 날짜면 그 날, 아니면 null).</summary>
        public static DateTime? DatePartOf(string text)
        {
            DateTime value;
            return TryParseDate(text, out value) ? value.Date : (DateTime?)null;
        }

        public static bool SameValues(string[] a, string[] b)
        {
            if (ReferenceEquals(a, b))
                return true;
            if (a == null || b == null || a.Length != b.Length)
                return false;
            for (var i = 0; i < a.Length; i++)
            {
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// 저장 단계 검사(UPDATE): 편집 창과 같은 기준(EditBlockReason)으로 고칠 수 없는 칸 — 편집할 수 없는 형식(LOB 등)이거나
        /// 원래 값·새 값이 잘려 보이는 칸 — 의 값이 원래 값과 다르면 저장을 거부할 이유("열 'X'(형식): …"). 문제가 없으면 null.
        /// 값이 그대로인 칸은 UpdateStatement가 SET에서 빼므로 통과한다(조용히 빼지 않는다 — 잘린 표시 글이 LOB에 들어가는 일을 막는다).
        /// </summary>
        public static string UpdateBlockReason(EditableTable table, string[] original, string[] current)
        {
            if (table == null || original == null || current == null)
                return null;
            for (var i = 0; i < table.Columns.Count && i < current.Length; i++)
            {
                var before = i < original.Length ? original[i] : null;
                if (string.Equals(before, current[i], StringComparison.Ordinal))
                    continue;
                var reason = EditBlockReason(table.KindAt(i), before) ?? EditBlockReason(table.KindAt(i), current[i]);
                if (reason != null)
                    return ColumnBlockMessage(table, i, reason);
            }
            return null;
        }

        /// <summary>
        /// 저장 단계 검사(INSERT): 넣을 값이 잘려 보이면("…(전체 n자)") 거부할 이유. 문제가 없으면 null.
        /// 편집할 수 없는 형식(LOB 등)의 열은 InsertStatement가 값과 상관없이 빼므로(DB 기본값·NULL) 여기서 막지 않는다.
        /// </summary>
        public static string InsertBlockReason(EditableTable table, string[] values)
        {
            if (table == null || values == null)
                return null;
            for (var i = 0; i < table.Columns.Count && i < values.Length; i++)
            {
                if (values[i] == null || table.KindAt(i) == ColumnValueKind.Unsupported)
                    continue;
                var reason = EditBlockReason(table.KindAt(i), values[i]);
                if (reason != null)
                    return ColumnBlockMessage(table, i, reason);
            }
            return null;
        }

        private static string ColumnBlockMessage(EditableTable table, int index, string reason)
        {
            var column = table.Columns[index];
            return "열 '" + (column.Name ?? "") + "'(" + (column.TypeLabel ?? "") + "): " + reason + " — 바뀐 값을 저장하지 않습니다. 다시 조회(F1)한 뒤 고치세요.";
        }

        /// <summary>
        /// 바뀐 열만 SET에 넣는 UPDATE 문(바인드 s0, s1 … / 키 k0, k1 …, ROWID 키면 rid). WHERE는 키의 원래 값(ROWID 키면 rowId).
        /// 바뀐 열이 없거나 ROWID 키인데 rowId가 없으면 null.
        /// 고칠 수 없는 칸(UpdateBlockReason)의 값이 바뀌었으면 InvalidOperationException(이유).
        /// </summary>
        public static SqlQuery UpdateStatement(EditableTable table, string[] original, string[] current, string rowId = null)
        {
            if (table == null || !table.CanEdit || original == null || current == null || (table.UsesRowId && rowId == null))
                return null;
            var blocked = UpdateBlockReason(table, original, current);
            if (blocked != null)
                throw new InvalidOperationException(blocked);
            var sets = new List<string>();
            var query = new SqlQuery();
            for (var i = 0; i < table.Columns.Count && i < current.Length; i++)
            {
                var before = i < original.Length ? original[i] : null;
                if (string.Equals(before, current[i], StringComparison.Ordinal))
                    continue;
                var name = "s" + sets.Count.ToString(Inv);
                sets.Add(WorkspaceLogic.QuoteIdentifier(table.Columns[i].Name) + " = " + table.ParameterPrefix + name);
                query.Parameters.Add(new KeyValuePair<string, object>(name, ToDbValue(table.KindAt(i), current[i])));
            }
            if (sets.Count == 0)
                return null;
            query.Sql = "UPDATE " + WorkspaceLogic.QualifiedName(table.Owner, table.Name) + " SET " + string.Join(", ", sets) + " WHERE " + KeyCondition(table, original, rowId, query);
            return query;
        }

        /// <summary>
        /// 새 행의 INSERT 문(바인드 i0, i1 …). 값이 NULL인 열과 편집할 수 없는 형식(LOB 등)의 열은 빼서
        /// DB 기본값·자동 번호(없으면 NULL)가 들어가게 한다. 넣을 값이 하나도 없으면 null.
        /// 잘려 보이는 값(InsertBlockReason)이 있으면 InvalidOperationException(이유).
        /// </summary>
        public static SqlQuery InsertStatement(EditableTable table, string[] values)
        {
            if (table == null || !table.CanEdit || values == null)
                return null;
            var blocked = InsertBlockReason(table, values);
            if (blocked != null)
                throw new InvalidOperationException(blocked);
            var names = new List<string>();
            var binds = new List<string>();
            var query = new SqlQuery();
            for (var i = 0; i < table.Columns.Count && i < values.Length; i++)
            {
                if (values[i] == null || table.KindAt(i) == ColumnValueKind.Unsupported)
                    continue;
                var name = "i" + binds.Count.ToString(Inv);
                names.Add(WorkspaceLogic.QuoteIdentifier(table.Columns[i].Name));
                binds.Add(table.ParameterPrefix + name);
                query.Parameters.Add(new KeyValuePair<string, object>(name, ToDbValue(table.KindAt(i), values[i])));
            }
            if (names.Count == 0)
                return null;
            query.Sql = "INSERT INTO " + WorkspaceLogic.QualifiedName(table.Owner, table.Name) + " (" + string.Join(", ", names) + ") VALUES (" + string.Join(", ", binds) + ")";
            return query;
        }

        /// <summary>
        /// [복제]: 새 행의 처음 값. 편집할 수 없는 형식(LOB 등)과 잘려 보이는 값은 원래 값을 알 수 없으므로 NULL로 비운다
        /// (그대로 두면 "…(전체 n자)"가 붙은 표시 글이 INSERT된다).
        /// </summary>
        public static string[] CopyForInsert(EditableTable table, string[] values)
        {
            var copy = new string[table != null ? table.Columns.Count : 0];
            for (var i = 0; i < copy.Length; i++)
            {
                var value = values != null && i < values.Length ? values[i] : null;
                copy[i] = value != null && CanEditValue(table.KindAt(i), value) ? value : null;
            }
            return copy;
        }

        /// <summary>
        /// 오른쪽 메뉴 [INSERT 문 만들기]: 행마다 값을 글자로 쓴 INSERT 문 한 줄(끝에 ';'). 행을 그대로 옮기는 용도라 NULL도 NULL로 쓴다.
        /// 편집할 수 없는 형식(LOB 등)과 잘려 보이는 값은 원래 값을 몰라 빼고, 줄 끝 주석에 뺀 열과 이유를 적는다. 넣을 열이 없는 행은 주석 한 줄.
        /// </summary>
        public static string InsertScript(string owner, string name, IList<ResultColumn> columns, IEnumerable<string[]> rows, string providerKind)
        {
            var text = new StringBuilder();
            var table = WorkspaceLogic.QualifiedName(owner, name);
            foreach (var values in rows ?? Enumerable.Empty<string[]>())
            {
                var names = new List<string>();
                var literals = new List<string>();
                var skipped = new List<string>();
                for (var i = 0; i < columns.Count; i++)
                {
                    var value = values != null && i < values.Length ? values[i] : null;
                    var kind = KindOf(columns[i].TypeLabel);
                    if (kind == ColumnValueKind.Unsupported)
                    {
                        skipped.Add(columns[i].Name + "(" + columns[i].TypeLabel + ")");
                        continue;
                    }
                    if (value != null && IsTruncated(value))
                    {
                        skipped.Add(columns[i].Name + "(잘린 값)");
                        continue;
                    }
                    names.Add(WorkspaceLogic.QuoteIdentifier(columns[i].Name));
                    literals.Add(SqlLiteral(kind, columns[i].TypeLabel, value, providerKind));
                }
                var note = skipped.Count > 0 ? "  -- 뺀 열: " + string.Join(", ", skipped) : "";
                if (names.Count == 0)
                    text.Append("-- 넣을 수 있는 열이 없는 행").Append(note).Append('\n');
                else
                    text.Append("INSERT INTO ").Append(table).Append(" (").Append(string.Join(", ", names)).Append(") VALUES (")
                        .Append(string.Join(", ", literals)).Append(");").Append(note).Append('\n');
            }
            return text.ToString();
        }

        /// <summary>
        /// 값 하나의 SQL 글자: NULL, 숫자는 그대로, 글은 '…'(작은따옴표 두 번, SQL Server는 N'…'),
        /// 날짜는 Oracle TO_DATE·TO_TIMESTAMP(초 미만이 있을 때), SQL Server CAST('…' AS DATETIME2)(DATETIME 열에 7자리 초 미만 글을 바로 넣으면 실패), 그 밖은 '…'.
        /// </summary>
        public static string SqlLiteral(ColumnValueKind kind, string typeLabel, string value, string providerKind)
        {
            if (value == null)
                return "NULL";
            if (kind == ColumnValueKind.Number)
                return value;
            var quoted = "'" + value.Replace("'", "''") + "'";
            var mssql = string.Equals(providerKind, "mssql", StringComparison.OrdinalIgnoreCase);
            if (kind == ColumnValueKind.DateTime)
            {
                if (string.Equals(providerKind, "oracle", StringComparison.OrdinalIgnoreCase))
                    return value.IndexOf('.') >= 0
                        ? "TO_TIMESTAMP(" + quoted + ", 'YYYY-MM-DD HH24:MI:SS.FF')"
                        : "TO_DATE(" + quoted + ", 'YYYY-MM-DD HH24:MI:SS')";
                return mssql ? "CAST(" + quoted + " AS DATETIME2)" : quoted;
            }
            return mssql ? "N" + quoted : quoted;
        }

        /// <summary>키의 원래 값(ROWID 키면 rowId)으로 행 하나를 지우는 DELETE 문. ROWID 키인데 rowId가 없으면 null.</summary>
        public static SqlQuery DeleteStatement(EditableTable table, string[] original, string rowId = null)
        {
            if (table == null || !table.CanEdit || original == null || (table.UsesRowId && rowId == null))
                return null;
            var query = new SqlQuery();
            query.Sql = "DELETE FROM " + WorkspaceLogic.QualifiedName(table.Owner, table.Name) + " WHERE " + KeyCondition(table, original, rowId, query);
            return query;
        }

        /// <summary>
        /// 이 행을 고치거나 지울 수 없는 이유: ROWID 키인데 행의 ROWID를 모를 때(저장한 새 행 — 다시 조회해야 ROWID를 안다). 할 수 있으면 null.
        /// </summary>
        public static string RowBlockReason(EditableTable table, string rowId)
        {
            if (table != null && table.UsesRowId && rowId == null)
                return "새로 넣은 행은 ROWID를 몰라 고치거나 지울 수 없습니다. 다시 조회(F1)한 뒤 하세요.";
            return null;
        }

        // WHERE: ROWID 키면 "ROWID = :rid"(값은 조회한 ROWID 글 — Oracle이 ROWID로 바꿔 비교), 아니면 키 열의 원래 값(NULL은 IS NULL).
        private static string KeyCondition(EditableTable table, string[] original, string rowId, SqlQuery query)
        {
            if (table.UsesRowId)
            {
                query.Parameters.Add(new KeyValuePair<string, object>("rid", rowId));
                return "ROWID = " + table.ParameterPrefix + "rid";
            }
            var parts = new List<string>();
            for (var k = 0; k < table.KeyIndexes.Length; k++)
            {
                var index = table.KeyIndexes[k];
                var column = WorkspaceLogic.QuoteIdentifier(table.Columns[index].Name);
                var value = index < original.Length ? original[index] : null;
                if (value == null)
                {
                    parts.Add(column + " IS NULL");
                    continue;
                }
                var name = "k" + k.ToString(Inv);
                parts.Add(column + " = " + table.ParameterPrefix + name);
                query.Parameters.Add(new KeyValuePair<string, object>(name, ToDbValue(table.KindAt(index), value)));
            }
            return string.Join(" AND ", parts);
        }

        /// <summary>메시지 탭에 남길 글: 문장 + 바인드 값("s0=…, k0=…").</summary>
        public static string Describe(SqlQuery query)
        {
            if (query == null)
                return "";
            var text = new StringBuilder(query.Sql ?? "");
            if (query.Parameters.Count > 0)
            {
                text.Append("  -- ");
                text.Append(string.Join(", ", query.Parameters.Select(p => p.Key + "=" + ValueText(p.Value))));
            }
            return text.ToString();
        }

        private static string ValueText(object value)
        {
            if (value == null || value is DBNull)
                return "NULL";
            if (value is DateTime)
                return "'" + ((DateTime)value).ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", Inv).TrimEnd('.') + "'";
            if (value is string)
                return "'" + ((string)value).Replace("'", "''") + "'";
            return Convert.ToString(value, Inv);
        }

        /// <summary>편집 막대의 요약: "추가 1 · 수정 2 · 삭제 1" (아무것도 없으면 "").</summary>
        public static string PendingSummary(int modified, int deleted, int inserted = 0)
        {
            var parts = new List<string>();
            if (inserted > 0)
                parts.Add("추가 " + inserted.ToString(Inv));
            if (modified > 0)
                parts.Add("수정 " + modified.ToString(Inv));
            if (deleted > 0)
                parts.Add("삭제 " + deleted.ToString(Inv));
            return string.Join(" · ", parts);
        }

        /// <summary>저장 뒤 상태줄·메시지: "저장: DELETE 1행 · UPDATE 2행 · INSERT 1행 (커밋 전 — [커밋]으로 확정, [롤백]으로 취소)".</summary>
        public static string SavedMessage(int updated, int deleted, int inserted = 0)
        {
            var parts = new List<string>();
            if (deleted > 0)
                parts.Add("DELETE " + deleted.ToString(Inv) + "행");
            if (updated > 0)
                parts.Add("UPDATE " + updated.ToString(Inv) + "행");
            if (inserted > 0)
                parts.Add("INSERT " + inserted.ToString(Inv) + "행");
            return "저장: " + (parts.Count == 0 ? "변경 없음" : string.Join(" · ", parts) + " (커밋 전 — [커밋]으로 확정, [롤백]으로 취소)");
        }

        /// <summary>영향 행 수가 1이 아닐 때의 오류 글. 0행이면 다른 세션에서 바뀌었을 수 있다.</summary>
        public static string AffectedMismatch(int affected)
        {
            if (affected == 0)
                return "영향 행이 0행입니다 — 다른 세션에서 바뀌었거나 이미 지워진 행입니다. 다시 조회(F1)한 뒤 고치세요.";
            return "영향 행이 " + affected.ToString(Inv) + "행입니다(1행이어야 함) — 키가 고유하지 않을 수 있습니다. [롤백]으로 되돌리고 확인하세요.";
        }
    }
}

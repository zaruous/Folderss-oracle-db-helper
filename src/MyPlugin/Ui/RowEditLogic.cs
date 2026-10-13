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

    /// <summary>
    /// 빠른 조회(F1) 결과를 편집·저장할 대상 테이블. 어느 열이 기본 키인지(WHERE에 쓸 열), 열마다 값 종류를 안다.
    /// 기본 키가 없거나 결과에 기본 키 열이 빠졌으면 CanEdit = false(DisabledReason에 이유).
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
        /// <summary>기본 키 열의 결과 열 위치. 비어 있으면 편집할 수 없다.</summary>
        public int[] KeyIndexes { get; set; } = Array.Empty<int>();
        public ColumnValueKind[] Kinds { get; set; } = Array.Empty<ColumnValueKind>();
        /// <summary>편집할 수 없는 이유. 편집할 수 있으면 null.</summary>
        public string DisabledReason { get; set; }

        public bool CanEdit
        {
            get { return DisabledReason == null && KeyIndexes.Length > 0; }
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
    /// WHERE는 기본 키 열의 원래 값으로만 쓴다(기본 키가 없으면 편집하지 않는다 — 같은 값의 행을 함께 바꾸지 않게).
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

        /// <summary>
        /// 편집 대상을 정한다: 데이터 사전의 기본 키 열(ColumnInfo.PrimaryKey)을 결과 열 위치에 맞춘다(이름 그대로, 없으면 대소문자 무시).
        /// 기본 키가 없거나 결과에 그 열이 없으면 CanEdit = false.
        /// </summary>
        public static EditableTable Describe(string owner, string name, IList<ResultColumn> columns, IEnumerable<ColumnInfo> metadata, string parameterPrefix)
        {
            var table = new EditableTable
            {
                Owner = owner,
                Name = name,
                ParameterPrefix = parameterPrefix ?? ":",
                Columns = columns != null ? new List<ResultColumn>(columns) : new List<ResultColumn>()
            };
            table.Kinds = table.Columns.Select(c => KindOf(c.TypeLabel)).ToArray();
            var keys = (metadata ?? Enumerable.Empty<ColumnInfo>()).Where(c => c != null && c.PrimaryKey).Select(c => c.Name ?? "").ToList();
            if (keys.Count == 0)
            {
                table.DisabledReason = "기본 키가 없는 테이블(또는 뷰)은 편집할 수 없습니다.";
                return table;
            }
            var indexes = new List<int>();
            foreach (var key in keys)
            {
                var index = IndexOfColumn(table.Columns, key);
                if (index < 0)
                {
                    table.DisabledReason = "결과에 기본 키 열 '" + key + "'이(가) 없어 편집할 수 없습니다.";
                    return table;
                }
                if (table.Kinds[index] == ColumnValueKind.Unsupported)
                {
                    table.DisabledReason = "기본 키 열 '" + key + "'의 형식(" + (table.Columns[index].TypeLabel ?? "") + ")으로는 행을 찾을 수 없어 편집할 수 없습니다.";
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
        /// 바뀐 열만 SET에 넣는 UPDATE 문(바인드 s0, s1 … / 키 k0, k1 …). WHERE는 기본 키의 원래 값. 바뀐 열이 없으면 null.
        /// </summary>
        public static SqlQuery UpdateStatement(EditableTable table, string[] original, string[] current)
        {
            if (table == null || !table.CanEdit || original == null || current == null)
                return null;
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
            query.Sql = "UPDATE " + WorkspaceLogic.QualifiedName(table.Owner, table.Name) + " SET " + string.Join(", ", sets) + " WHERE " + KeyCondition(table, original, query);
            return query;
        }

        /// <summary>
        /// 새 행의 INSERT 문(바인드 i0, i1 …). 값이 NULL인 열과 편집할 수 없는 형식(LOB 등)의 열은 빼서
        /// DB 기본값·자동 번호(없으면 NULL)가 들어가게 한다. 넣을 값이 하나도 없으면 null.
        /// </summary>
        public static SqlQuery InsertStatement(EditableTable table, string[] values)
        {
            if (table == null || !table.CanEdit || values == null)
                return null;
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

        /// <summary>기본 키의 원래 값으로 행 하나를 지우는 DELETE 문.</summary>
        public static SqlQuery DeleteStatement(EditableTable table, string[] original)
        {
            if (table == null || !table.CanEdit || original == null)
                return null;
            var query = new SqlQuery();
            query.Sql = "DELETE FROM " + WorkspaceLogic.QualifiedName(table.Owner, table.Name) + " WHERE " + KeyCondition(table, original, query);
            return query;
        }

        private static string KeyCondition(EditableTable table, string[] original, SqlQuery query)
        {
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
            return "영향 행이 " + affected.ToString(Inv) + "행입니다(1행이어야 함) — 기본 키가 고유하지 않을 수 있습니다. [롤백]으로 되돌리고 확인하세요.";
        }
    }
}

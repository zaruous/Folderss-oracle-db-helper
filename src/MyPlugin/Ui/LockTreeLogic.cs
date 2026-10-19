using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MyPlugin
{
    /// <summary>잠금 트리의 세션 하나.</summary>
    internal sealed class LockNode
    {
        public LockNode(string key, int order)
        {
            Key = key;
            Order = order;
        }

        /// <summary>공급자가 정한 세션 키(Oracle "INST_ID,SID" · PostgreSQL pid · SQL Server session_id).</summary>
        public string Key { get; }

        /// <summary>결과에 처음 나온 순서(같은 조건이면 이 순서로 놓는다).</summary>
        public int Order { get; }

        /// <summary>표시 열 값(<see cref="LockForest.DisplayColumns"/> 순서). 자기 행 없이 다른 세션의 BLOCKER_KEY로만 나온 세션이면 null.</summary>
        public string[] Values { get; set; }

        public double? WaitSeconds { get; set; }
        public double? IdleSeconds { get; set; }

        /// <summary>이 세션을 막는 세션들(보통 하나, PostgreSQL은 여럿일 수 있다).</summary>
        public List<LockNode> Blockers { get; } = new List<LockNode>();

        /// <summary>이 세션이 막는 세션들.</summary>
        public List<LockNode> Blocked { get; } = new List<LockNode>();

        public bool Known { get { return Values != null; } }
    }

    /// <summary>화면의 한 줄 — 트리를 위에서부터 편 순서.</summary>
    internal sealed class LockTreeRow
    {
        public LockNode Node { get; set; }

        /// <summary>이 줄이 속한 사슬의 근원(순환 묶음이면 묶음의 첫 세션). [근원 세션 종료…]의 대상.</summary>
        public LockNode Root { get; set; }

        public int Depth { get; set; }

        /// <summary>세션 칸 앞에 붙는 선 글자("● ", "├ ", "│  └ " …).</summary>
        public string Prefix { get; set; }

        /// <summary>이 세션 아래로 막힌 세션 수(자손 전체, 중복 없이).</summary>
        public int BlockedCount { get; set; }

        /// <summary>자손들이 지금 기다린 시간의 합(초) — 이 세션 때문에 쌓인 대기(DPA의 "유발 대기").</summary>
        public double InducedWaitSeconds { get; set; }

        /// <summary>여러 세션에 막혀 이미 위에 나온 세션 — 다시 펼치지 않는다.</summary>
        public bool Repeated { get; set; }

        /// <summary>근원 없이 서로 막는 순환(교착) 묶음의 줄.</summary>
        public bool InCycle { get; set; }
    }

    internal sealed class LockForest
    {
        public LockForest(IReadOnlyList<string> displayColumns, List<LockTreeRow> rows)
        {
            DisplayColumns = displayColumns;
            Rows = rows;
        }

        /// <summary>계약 열을 뺀 표시 열 이름(조회 결과 순서).</summary>
        public IReadOnlyList<string> DisplayColumns { get; }

        public List<LockTreeRow> Rows { get; }

        /// <summary>보이는 근원 수(순환 묶음 제외).</summary>
        public int RootCount { get; set; }

        /// <summary>보이는 줄 가운데 막힌 세션 수(중복 없이).</summary>
        public int BlockedCount { get; set; }

        /// <summary>보이는 세션의 가장 긴 지금 대기(초). 대기 값이 없으면 null.</summary>
        public double? LongestWaitSeconds { get; set; }

        /// <summary>최소 대기보다 짧아 숨긴 사슬 수.</summary>
        public int HiddenChains { get; set; }

        public bool HasCycle { get; set; }
    }

    /// <summary>
    /// 잠금 연쇄 트리(순수 로직 — DBMS를 모른다). 공급자 SQL의 결과(행 = 간선: SESSION_KEY → BLOCKER_KEY)를 근원 블로커가 맨 위인 숲으로 만든다.
    /// - 근원 = 아무에게도 막히지 않고 누군가를 막는 세션. 자손 수·유발 대기(자손 대기 합)를 센다(순환이 있어도 끝나게 방문 집합).
    /// - 블로커가 여럿인 세션(PostgreSQL pg_blocking_pids)은 각 부모 아래에 보이되, 두 번째부터는 <see cref="LockTreeRow.Repeated"/>로 펼치지 않는다.
    /// - 근원에서 닿지 않는 막힌 세션은 순환(교착) — 묶음으로 따로 보인다(최소 대기와 상관없이).
    /// - 최소 대기: 사슬(근원 하나의 트리) 안에서 가장 긴 대기가 이보다 짧으면 사슬을 통째로 숨긴다. 줄 단위로 자르면 가짜 근원이 생긴다.
    /// - 정렬: 근원은 막은 세션 수 → 유발 대기 → 처음 나온 순서. 자식도 같은 기준(그다음 대기 시간).
    /// </summary>
    internal static class LockTreeLogic
    {
        public const string RootMark = "● ";
        public const string CycleMark = "⟳ ";
        public const string UnknownSuffix = " (정보 없음)";
        public const string RepeatedSuffix = " (위와 같음)";

        /// <summary>최소 대기 고르기(초). 0 = 전부.</summary>
        public static readonly IReadOnlyList<int> MinWaitChoices = new[] { 0, 3, 10, 30, 60 };

        /// <summary>기본 최소 대기 3초(OEM Performance Hub Blocking Sessions의 기본값) — 아주 짧은 대기 잡음을 숨긴다.</summary>
        public const int DefaultMinWaitSeconds = 3;

        /// <summary>트리 표의 앞 열(뒤에 표시 열이 붙는다).</summary>
        public static readonly IReadOnlyList<string> TreeColumns = new[] { "세션", "막은 세션", "유발 대기(초)", "대기(초)", "유휴(초)" };

        public static LockForest Build(QueryTable table, double minWaitSeconds)
        {
            if (table == null)
                throw new ArgumentNullException(nameof(table));
            var keyIndex = ColumnIndex(table, MonitorLockTree.SessionKey);
            if (keyIndex < 0)
                throw new InvalidOperationException("잠금 트리 조회 결과에 " + MonitorLockTree.SessionKey + " 열이 없습니다.");
            var blockerIndex = ColumnIndex(table, MonitorLockTree.BlockerKey);
            var waitIndex = ColumnIndex(table, MonitorLockTree.WaitSeconds);
            var idleIndex = ColumnIndex(table, MonitorLockTree.IdleSeconds);
            var display = new List<int>();
            for (var i = 0; i < table.Columns.Count; i++)
                if (!MonitorLockTree.ContractColumns.Any(c => string.Equals(c, table.Columns[i], StringComparison.OrdinalIgnoreCase)))
                    display.Add(i);
            var displayColumns = display.Select(i => table.Columns[i]).ToList();

            var nodes = new Dictionary<string, LockNode>(StringComparer.Ordinal);
            var all = new List<LockNode>();
            Func<string, LockNode> get = key =>
            {
                LockNode found;
                if (!nodes.TryGetValue(key, out found))
                {
                    found = new LockNode(key, all.Count);
                    nodes[key] = found;
                    all.Add(found);
                }
                return found;
            };
            foreach (var row in table.Rows)
            {
                var key = Cell(row, keyIndex);
                if (key == null)
                    continue;
                var node = get(key);
                if (node.Values == null)
                {
                    node.Values = display.Select(i => i < row.Length ? row[i] : null).ToArray();
                    node.WaitSeconds = Number(Cell(row, waitIndex));
                    node.IdleSeconds = Number(Cell(row, idleIndex));
                }
                var blockerKey = Cell(row, blockerIndex);
                if (blockerKey == null || blockerKey == key)
                    continue;
                var blocker = get(blockerKey);
                if (!node.Blockers.Contains(blocker))
                {
                    node.Blockers.Add(blocker);
                    blocker.Blocked.Add(node);
                }
            }

            var roots = all.Where(n => n.Blockers.Count == 0 && n.Blocked.Count > 0).ToList();
            var reachable = new HashSet<LockNode>(roots.SelectMany(Descendants));
            reachable.UnionWith(roots);
            var cycles = all.Where(n => n.Blockers.Count > 0 && !reachable.Contains(n)).ToList();

            var rows = new List<LockTreeRow>();
            var forest = new LockForest(displayColumns, rows);
            var emitted = new HashSet<LockNode>();
            foreach (var root in roots.OrderByDescending(n => Descendants(n).Count).ThenByDescending(InducedWait).ThenBy(n => n.Order))
            {
                var longest = Descendants(root).Select(n => n.WaitSeconds ?? 0).DefaultIfEmpty(0).Max();
                if (minWaitSeconds > 0 && longest < minWaitSeconds)
                {
                    forest.HiddenChains++;
                    continue;
                }
                forest.RootCount++;
                Emit(rows, emitted, root, root, 0, RootMark, "", false);
            }
            foreach (var node in cycles)
            {
                if (emitted.Contains(node))
                    continue;
                forest.HasCycle = true;
                Emit(rows, emitted, node, node, 0, CycleMark, "", true);
            }
            var shown = new HashSet<LockNode>(rows.Select(r => r.Node));
            forest.BlockedCount = shown.Count(n => n.Blockers.Count > 0);
            var waits = shown.Where(n => n.WaitSeconds.HasValue).Select(n => n.WaitSeconds.Value).ToList();
            forest.LongestWaitSeconds = waits.Count == 0 ? (double?)null : waits.Max();
            return forest;
        }

        private static void Emit(List<LockTreeRow> rows, HashSet<LockNode> emitted, LockNode node, LockNode root, int depth, string prefix, string indent, bool inCycle)
        {
            var repeated = !emitted.Add(node);
            var below = Descendants(node);
            rows.Add(new LockTreeRow
            {
                Node = node,
                Root = root,
                Depth = depth,
                Prefix = prefix,
                BlockedCount = repeated ? 0 : below.Count,
                InducedWaitSeconds = repeated ? 0 : below.Sum(n => n.WaitSeconds ?? 0),
                Repeated = repeated,
                InCycle = inCycle
            });
            if (repeated)
                return;
            var children = node.Blocked.OrderByDescending(n => Descendants(n).Count).ThenByDescending(n => n.WaitSeconds ?? 0).ThenBy(n => n.Order).ToList();
            for (var i = 0; i < children.Count; i++)
            {
                var last = i == children.Count - 1;
                Emit(rows, emitted, children[i], root, depth + 1, indent + (last ? "└ " : "├ "), indent + (last ? "   " : "│  "), inCycle);
            }
        }

        /// <summary>이 세션 아래로 막힌 세션 전체(자기 제외, 중복 없이 — 순환이어도 끝난다).</summary>
        public static List<LockNode> Descendants(LockNode node)
        {
            var seen = new HashSet<LockNode> { node };
            var result = new List<LockNode>();
            var queue = new Queue<LockNode>(node.Blocked);
            while (queue.Count > 0)
            {
                var next = queue.Dequeue();
                if (!seen.Add(next))
                    continue;
                result.Add(next);
                foreach (var child in next.Blocked)
                    queue.Enqueue(child);
            }
            return result;
        }

        private static double InducedWait(LockNode node)
        {
            return Descendants(node).Sum(n => n.WaitSeconds ?? 0);
        }

        /// <summary>트리를 표로: 앞 열(<see cref="TreeColumns"/>) + 표시 열. 줄 순서는 <see cref="LockForest.Rows"/>와 같다.</summary>
        public static QueryTable ToTable(LockForest forest)
        {
            if (forest == null)
                throw new ArgumentNullException(nameof(forest));
            var table = new QueryTable();
            table.Columns.AddRange(TreeColumns);
            table.Columns.AddRange(forest.DisplayColumns);
            foreach (var row in forest.Rows)
            {
                var values = new string[table.Columns.Count];
                values[0] = row.Prefix + row.Node.Key + (row.Repeated ? RepeatedSuffix : row.Node.Known ? "" : UnknownSuffix);
                // 막은 세션·유발 대기는 계산한 값이라 끝 줄도 0(NULL이 아님). 위와 같은 줄만 비운다
                values[1] = row.Repeated ? null : row.BlockedCount.ToString(CultureInfo.InvariantCulture);
                values[2] = row.Repeated ? null : Seconds(row.InducedWaitSeconds);
                values[3] = row.Node.WaitSeconds.HasValue ? Seconds(row.Node.WaitSeconds.Value) : null;
                values[4] = row.Node.IdleSeconds.HasValue ? Seconds(row.Node.IdleSeconds.Value) : null;
                if (row.Node.Values != null)
                    Array.Copy(row.Node.Values, 0, values, TreeColumns.Count, Math.Min(row.Node.Values.Length, forest.DisplayColumns.Count));
                table.Rows.Add(values);
            }
            return table;
        }

        /// <summary>조치(세션 종료)에 넣을 값: 세션의 표시 열에서 action.Columns 순서대로. 정보 없는 세션이거나 열이 없으면 null.</summary>
        public static string[] ActionValues(LockForest forest, LockNode node, MonitorAction action)
        {
            if (forest == null || node == null || action == null || node.Values == null)
                return null;
            var result = new string[action.Columns.Count];
            for (var i = 0; i < result.Length; i++)
            {
                var index = IndexOf(forest.DisplayColumns, action.Columns[i]);
                if (index < 0 || index >= node.Values.Length || string.IsNullOrEmpty(node.Values[index]))
                    return null;
                result[i] = node.Values[index];
            }
            return result;
        }

        /// <summary>트리 위 요약: "근원 2 · 막힌 세션 5 · 최장 대기 4분 12초 · 짧은 사슬 1개 숨김". 없으면 "잠금 대기 없음".</summary>
        public static string SummaryText(LockForest forest, int minWaitSeconds)
        {
            if (forest == null)
                return "";
            var hidden = forest.HiddenChains > 0 ? " · " + MinWaitText(minWaitSeconds) + "보다 짧은 사슬 " + forest.HiddenChains.ToString(CultureInfo.InvariantCulture) + "개 숨김" : "";
            if (forest.Rows.Count == 0)
                return "잠금 대기 없음" + hidden;
            var text = "근원 " + forest.RootCount.ToString(CultureInfo.InvariantCulture)
                + " · 막힌 세션 " + forest.BlockedCount.ToString(CultureInfo.InvariantCulture);
            if (forest.LongestWaitSeconds.HasValue)
                text += " · 최장 대기 " + DurationText(forest.LongestWaitSeconds.Value);
            if (forest.HasCycle)
                text += " · 순환(교착) 있음";
            return text + hidden;
        }

        /// <summary>"42초" · "4분 12초" · "1시간 3분".</summary>
        public static string DurationText(double seconds)
        {
            var total = (long)Math.Max(0, Math.Round(seconds));
            if (total < 60)
                return total.ToString(CultureInfo.InvariantCulture) + "초";
            if (total < 3600)
                return (total / 60).ToString(CultureInfo.InvariantCulture) + "분 " + (total % 60).ToString(CultureInfo.InvariantCulture) + "초";
            return (total / 3600).ToString(CultureInfo.InvariantCulture) + "시간 " + (total % 3600 / 60).ToString(CultureInfo.InvariantCulture) + "분";
        }

        /// <summary>최소 대기 콤보 글: 0 → "전부", 그 밖 → "3초".</summary>
        public static string MinWaitText(int seconds)
        {
            return seconds <= 0 ? "전부" : seconds.ToString(CultureInfo.InvariantCulture) + "초";
        }

        private static string Seconds(double value)
        {
            return Math.Round(value).ToString("0", CultureInfo.InvariantCulture);
        }

        // PostgreSQL은 따옴표 없는 별칭을 소문자로 돌려준다 — 계약 열은 대소문자를 가리지 않는다
        private static int ColumnIndex(QueryTable table, string name)
        {
            return IndexOf(table.Columns, name);
        }

        private static int IndexOf(IReadOnlyList<string> columns, string name)
        {
            for (var i = 0; i < columns.Count; i++)
                if (string.Equals(columns[i], name, StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }

        private static string Cell(string[] row, int index)
        {
            if (index < 0 || index >= row.Length)
                return null;
            var value = row[index];
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static double? Number(string text)
        {
            double value;
            return text != null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : (double?)null;
        }
    }
}

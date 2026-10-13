using System;
using System.Collections.Generic;
using System.Globalization;

namespace MyPlugin
{
    /// <summary>결과 그리드에서 찾은 칸: 화면 순서의 행 위치와 결과 열 위치.</summary>
    internal readonly struct CellMatch
    {
        public CellMatch(int row, int column)
        {
            Row = row;
            Column = column;
        }

        public int Row { get; }
        public int Column { get; }
    }

    /// <summary>
    /// SQL 편집기·결과 그리드 찾기(Ctrl+F)의 순수 로직. 대소문자를 무시하고, 끝에서 처음(처음에서 끝)으로 돈다.
    /// </summary>
    internal static class FindLogic
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>글에서 찾는 말이 나오는 시작 위치(겹치지 않게, 앞에서부터). 찾는 말이 비었으면 빈 목록.</summary>
        public static List<int> FindAll(string text, string term)
        {
            var starts = new List<int>();
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(term))
                return starts;
            var at = text.IndexOf(term, 0, StringComparison.OrdinalIgnoreCase);
            while (at >= 0)
            {
                starts.Add(at);
                at = at + term.Length >= text.Length ? -1 : text.IndexOf(term, at + term.Length, StringComparison.OrdinalIgnoreCase);
            }
            return starts;
        }

        /// <summary>
        /// 편집기: position 기준 다음(forward)·이전 일치의 번호. 다음은 inclusive면 position에서 시작하는 것도 포함(입력하는 동안 제자리),
        /// 아니면 position 뒤부터. 이전은 position 앞. 없으면 반대 끝으로 돈다. 일치가 없으면 -1.
        /// </summary>
        public static int NextIndex(IList<int> starts, int position, bool forward, bool inclusive)
        {
            if (starts == null || starts.Count == 0)
                return -1;
            if (forward)
            {
                for (var i = 0; i < starts.Count; i++)
                {
                    if (inclusive ? starts[i] >= position : starts[i] > position)
                        return i;
                }
                return 0;
            }
            for (var i = starts.Count - 1; i >= 0; i--)
            {
                if (starts[i] < position)
                    return i;
            }
            return starts.Count - 1;
        }

        /// <summary>결과 그리드: 값에 찾는 말이 들어 있는 칸(행 → 열 순서). NULL 칸은 찾지 않는다.</summary>
        public static List<CellMatch> FindCells(IList<string[]> rows, string term)
        {
            var matches = new List<CellMatch>();
            if (rows == null || string.IsNullOrEmpty(term))
                return matches;
            for (var r = 0; r < rows.Count; r++)
            {
                var values = rows[r];
                if (values == null)
                    continue;
                for (var c = 0; c < values.Length; c++)
                {
                    if (values[c] != null && values[c].IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                        matches.Add(new CellMatch(r, c));
                }
            }
            return matches;
        }

        /// <summary>
        /// 결과 그리드: (row, column) 칸 기준 다음·이전 일치의 번호. 규칙은 <see cref="NextIndex"/>와 같다
        /// (column = -1이면 그 행의 첫 칸 앞).
        /// </summary>
        public static int NextCell(IList<CellMatch> matches, int row, int column, bool forward, bool inclusive)
        {
            if (matches == null || matches.Count == 0)
                return -1;
            if (forward)
            {
                for (var i = 0; i < matches.Count; i++)
                {
                    var order = Compare(matches[i], row, column);
                    if (inclusive ? order >= 0 : order > 0)
                        return i;
                }
                return 0;
            }
            for (var i = matches.Count - 1; i >= 0; i--)
            {
                if (Compare(matches[i], row, column) < 0)
                    return i;
            }
            return matches.Count - 1;
        }

        /// <summary>찾기 상자의 개수 표시: "3/12", 없으면 "결과 없음", 찾는 말이 비었으면 "".</summary>
        public static string CountText(string term, int index, int count)
        {
            if (string.IsNullOrEmpty(term))
                return "";
            if (count == 0)
                return "결과 없음";
            return (index + 1).ToString(Inv) + "/" + count.ToString(Inv);
        }

        private static int Compare(CellMatch match, int row, int column)
        {
            return match.Row != row ? match.Row.CompareTo(row) : match.Column.CompareTo(column);
        }
    }
}

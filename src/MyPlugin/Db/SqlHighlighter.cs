using System;
using System.Collections.Generic;

namespace MyPlugin
{
    /// <summary>강조 계산이 읽는 글(편집기 문서). 전체 문자열을 만들지 않고 필요한 조각만 읽는다.</summary>
    public interface ISqlTextSource
    {
        int Length { get; }

        string GetText(int start, int length);

        /// <summary>offset이 있는 줄의 시작 위치(0 ≤ offset ≤ Length).</summary>
        int LineStart(int offset);

        /// <summary>offset이 있는 줄의 다음 줄 시작(줄바꿈 다음). 마지막 줄이면 Length.</summary>
        int NextLineStart(int offset);
    }

    /// <summary>
    /// 편집기 구문 강조 구간(<see cref="SqlScript.EditorSpans"/>)을 들고 있다가 글이 바뀌면 바뀐 곳 근처만 다시 훑는다(증분).
    /// 다시 훑기는 바뀐 줄의 시작부터 — 그 줄이 여러 줄 주석·문자열·식별자 안이면 그 토큰이 시작한 줄부터 — 새로 나온 구간이 예전 구간(옮긴 위치)과
    /// 같아지는 곳까지다. 토크나이저는 토큰이 끝난 뒤 (위치, 그 줄에 내용이 있었는지)만 기억하므로 바뀐 곳 뒤에서 같은 구간이 나오면 그 뒤는 모두 같다.
    /// 글은 줄 경계에서 자른 조각으로 읽고(처음 4KB, 모자라면 늘림), 조각 끝에서 잘린 토큰(닫히지 않은 주석 등)이 있으면 더 크게 다시 읽는다.
    /// </summary>
    public sealed class SqlHighlighter
    {
        private const int FirstWindow = 4096;
        private List<SqlSpan> _spans = new List<SqlSpan>();

        public SqlHighlighter(SqlLexRules rules)
        {
            Rules = rules ?? SqlLexRules.Oracle;
        }

        public SqlLexRules Rules { get; }

        /// <summary>지금 글의 구간(앞에서 뒤로, 겹치지 않음). 칠할 때는 <see cref="SqlSpanKind.Identifier"/>를 건너뛴다.</summary>
        public IReadOnlyList<SqlSpan> Spans { get { return _spans; } }

        /// <summary>마지막 계산이 읽은 글자 수(측정용).</summary>
        public int LastScanned { get; private set; }

        /// <summary>처음부터 다시 훑는다(글을 통째로 바꿨을 때·규칙을 바꿨을 때).</summary>
        public void Reset(ISqlTextSource text)
        {
            var fresh = new List<SqlSpan>();
            LastScanned = 0;
            int resume;
            Scan(text, 0, 0, 0, fresh, null, 0, 0, out resume);
            _spans = fresh;
        }

        // 달러 따옴표 태그는 길 수 있어($본문$) 둘레를 넓게 본다. 그 밖의 닫는 표시는 두 글자 이하라 고친 곳 앞뒤 한 글자면 된다
        private const int DollarReach = 64;

        /// <summary>
        /// 여러 줄 토큰(블록 주석·문자열·따옴표 식별자) 한가운데를 고쳤고, 그 토큰을 끝내거나 이을 수 있는 것이 고친 곳 둘레(앞뒤 한 글자 — 달러 따옴표는 64자)와
        /// 지운 글에 없으면 토큰의 끝은 그대로다 — 그 구간 길이만 바꾸고 뒤 구간은 위치만 옮긴다. 10만 줄짜리 주석 안에서 쳐도 둘레만 읽는다.
        /// 토큰마다 보는 것: 블록 주석 */(중첩 규칙이면 * /), '…'·q'[…]'·N'…' 따옴표('', ]'도 따옴표가 있어야 생긴다), E'…' 따옴표·\, $…$ 달러, "…" ", […] ].
        /// 줄 주석(--)은 줄바꿈으로 끝나므로 빼고(그 줄만 다시 훑으면 된다), 지운 글(removedText)을 모르면 지우기도 보통 길로.
        /// </summary>
        private bool InsideToken(ISqlTextSource text, int offset, int removed, int inserted, string removedText)
        {
            if (removed > 0 && (removedText == null || removedText.Length != removed))
                return false;
            var spans = _spans;
            var i = FirstStartingAtOrAfter(spans, offset) - 1;
            if (i < 0)
                return false;
            var span = spans[i];
            // 여는 표시(/*·q'[·N'·$$ 앞부분)와 닫는 표시를 건드리지 않는 안쪽만
            if (span.Kind == SqlSpanKind.Keyword || offset < span.Start + 3 || offset + removed > span.End - 3)
                return false;
            var opening = text.GetText(span.Start, 2);
            string sequence = null;
            string chars;
            var reach = 1;
            if (span.Kind == SqlSpanKind.Comment)
            {
                if (opening != "/*")
                    return false;
                // 중첩이 아니면 첫 */에서 끝난다 — 안쪽을 지운 글에 */가 있을 수 없고(있었다면 거기서 끝났다) 둘레에 새 */가 생기는지만 본다
                if (Rules.NestedComments)
                    chars = "*/";
                else
                {
                    sequence = "*/";
                    chars = "";
                }
            }
            else if (span.Kind == SqlSpanKind.Identifier)
                chars = opening[0] == '[' ? "]" : "\"";
            else if (opening[0] == '$')
            {
                chars = "$";
                reach = DollarReach;
            }
            else
                chars = Rules.EscapeString && (opening[0] == 'E' || opening[0] == 'e') ? "'\\" : "'";
            if (removed > 0 && chars.Length > 0 && removedText.IndexOfAny(chars.ToCharArray()) >= 0)
                return false;
            // 둘레는 여는 표시 뒤에서(여는 따옴표·/*를 닫는 글자로 오해하지 않게)
            var from = Math.Max(span.Start + 2, offset - reach);
            var to = Math.Min(text.Length, offset + inserted + reach);
            var around = text.GetText(from, to - from);
            LastScanned = around.Length;
            if ((chars.Length > 0 && around.IndexOfAny(chars.ToCharArray()) >= 0) || (sequence != null && around.IndexOf(sequence, StringComparison.Ordinal) >= 0))
                return false;
            var delta = inserted - removed;
            spans[i] = new SqlSpan(span.Kind, span.Start, span.Length + delta);
            if (delta != 0)
            {
                for (var k = i + 1; k < spans.Count; k++)
                    spans[k] = new SqlSpan(spans[k].Kind, spans[k].Start + delta, spans[k].Length);
            }
            return true;
        }

        /// <summary>
        /// 글이 바뀐 뒤 부른다: offset에서 removed자를 지우고 inserted자를 넣었다(text는 바뀐 뒤 글). 다시 칠해야 할 범위(바뀐 뒤 위치, Start ≤ End)를 돌려준다 —
        /// 그 밖의 구간은 예전과 같다(뒤쪽은 위치만 옮김). 목록은 제자리에서 고친다(10만 줄이면 구간이 20만 개 넘어 키마다 새로 만들면 큰 객체 힙을 쓴다).
        /// </summary>
        public (int Start, int End) Update(ISqlTextSource text, int offset, int removed, int inserted, string removedText = null)
        {
            var spans = _spans;
            offset = Math.Max(0, Math.Min(offset, text.Length));
            var delta = inserted - removed;
            LastScanned = 0;
            if (InsideToken(text, offset, removed, inserted, removedText))
                return (offset, offset);
            // 다시 훑기 시작할 줄. 바뀐 곳 앞의 글은 예전과 같으므로 예전 구간으로 여러 줄 토큰 안인지 본다.
            // 그 토큰 끝(옮긴 위치)까지는 어차피 읽어야 하므로 첫 조각을 거기까지 잡는다(4KB부터 늘려 가며 같은 곳을 여러 번 읽지 않게)
            var start = text.LineStart(offset);
            var reach = offset + inserted;
            while (true)
            {
                var inside = Containing(spans, start);
                if (inside < 0)
                    break;
                start = text.LineStart(spans[inside].Start);
                reach = Math.Max(reach, Math.Min(text.Length, spans[inside].End + delta));
            }
            var keep = FirstStartingAtOrAfter(spans, start);
            var fresh = new List<SqlSpan>();
            int resume;
            var end = Scan(text, start, offset + inserted, reach, fresh, spans, FirstStartingAtOrAfter(spans, offset + removed), delta, out resume);
            // [keep, resume)를 새로 훑은 것으로 바꾸고 그 뒤 예전 구간은 위치만 옮긴다
            spans.RemoveRange(keep, resume - keep);
            spans.InsertRange(keep, fresh);
            if (delta != 0)
            {
                for (var i = keep + fresh.Count; i < spans.Count; i++)
                    spans[i] = new SqlSpan(spans[i].Kind, spans[i].Start + delta, spans[i].Length);
            }
            return (start, end);
        }

        /// <summary>
        /// start(줄 시작, 보통 상태)부터 훑어 fresh에 붙인다. old가 있으면 editEnd 뒤에서 예전 구간(old[oldFrom..], delta만큼 옮긴 위치)과 같아지는 곳에서 멈추고
        /// 그 위치를 돌려준다(resume = 그 예전 구간 번호 — 거기부터는 예전 것을 그대로 쓴다). 같아지는 곳이 없으면 글 끝까지 훑고 글 길이를 돌려준다(resume = old 개수).
        /// 첫 조각은 reach(어차피 읽어야 할 곳)까지 잡는다.
        /// </summary>
        private int Scan(ISqlTextSource text, int start, int editEnd, int reach, List<SqlSpan> fresh, List<SqlSpan> old, int oldFrom, int delta, out int resume)
        {
            var length = text.Length;
            var pos = start;
            var window = FirstWindow;
            var j = oldFrom;
            resume = old == null ? 0 : old.Count;
            while (pos < length)
            {
                var target = Math.Max(Math.Max(pos, editEnd), reach) + window;
                var end = target >= length ? length : text.NextLineStart(target);
                var chunk = text.GetText(pos, end - pos);
                LastScanned += chunk.Length;
                var last = end >= length;
                var mark = fresh.Count;
                var cut = false;
                foreach (var s in SqlScript.EditorSpans(chunk, Rules))
                {
                    // 조각은 줄 끝(줄바꿈 다음)에서 자르므로 조각 끝까지 가는 토큰은 닫히지 않은 채 잘린 것이다
                    if (!last && s.End >= chunk.Length)
                    {
                        cut = true;
                        break;
                    }
                    var span = new SqlSpan(s.Kind, s.Start + pos, s.Length);
                    if (old != null && span.Start >= editEnd)
                    {
                        while (j < old.Count && old[j].Start + delta < span.Start)
                            j++;
                        if (j < old.Count && old[j].Start + delta == span.Start && old[j].Length == span.Length && old[j].Kind == span.Kind)
                        {
                            resume = j;
                            return span.Start;
                        }
                    }
                    fresh.Add(span);
                }
                if (cut)
                {
                    // 이 조각에서 붙인 것을 버리고 같은 곳부터 더 크게 다시 읽는다
                    fresh.RemoveRange(mark, fresh.Count - mark);
                    window *= 4;
                    continue;
                }
                pos = end;
                window *= 2;
            }
            return length;
        }

        // position(줄 시작)을 품은 구간 번호(Start < position ≤ End), 없으면 -1. End == position도 넣는다: 줄 시작에서 끝나는 토큰은 글 끝까지 닫히지 않은 것뿐이고
        // (닫힌 토큰은 줄바꿈 앞에서 끝난다) 거기에 글을 붙이면 그 토큰이 이어지거나('…' 뒤 ' → '') 달라진다
        private static int Containing(List<SqlSpan> spans, int position)
        {
            var i = FirstStartingAtOrAfter(spans, position) - 1;
            return i >= 0 && spans[i].End >= position ? i : -1;
        }

        /// <summary>Start ≥ position인 첫 구간 번호(없으면 Count).</summary>
        public static int FirstStartingAtOrAfter(IReadOnlyList<SqlSpan> spans, int position)
        {
            int lo = 0, hi = spans.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (spans[mid].Start < position)
                    lo = mid + 1;
                else
                    hi = mid;
            }
            return lo;
        }

        /// <summary>End &gt; position인 첫 구간 번호(없으면 Count) — 줄을 칠할 때 그 줄에 걸친 첫 구간.</summary>
        public static int FirstEndingAfter(IReadOnlyList<SqlSpan> spans, int position)
        {
            int lo = 0, hi = spans.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (spans[mid].End <= position)
                    lo = mid + 1;
                else
                    hi = mid;
            }
            return lo;
        }
    }

    /// <summary>문자열 하나를 <see cref="ISqlTextSource"/>로(시험·한 번 계산용).</summary>
    public sealed class SqlStringSource : ISqlTextSource
    {
        private readonly string _text;

        public SqlStringSource(string text)
        {
            _text = text ?? "";
        }

        public int Length { get { return _text.Length; } }

        public string GetText(int start, int length)
        {
            return _text.Substring(start, length);
        }

        public int LineStart(int offset)
        {
            for (var i = Math.Min(offset, _text.Length) - 1; i >= 0; i--)
            {
                var c = _text[i];
                // CRLF 사이(\r 다음 \n 앞)는 아직 같은 줄
                if (c == '\n' || (c == '\r' && (i + 1 >= _text.Length || _text[i + 1] != '\n')))
                    return i + 1;
            }
            return 0;
        }

        public int NextLineStart(int offset)
        {
            for (var i = offset; i < _text.Length; i++)
            {
                var c = _text[i];
                if (c == '\n')
                    return i + 1;
                if (c == '\r')
                    return i + 1 < _text.Length && _text[i + 1] == '\n' ? i + 2 : i + 1;
            }
            return _text.Length;
        }
    }
}

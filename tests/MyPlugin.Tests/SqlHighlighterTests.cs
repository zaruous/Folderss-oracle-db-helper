using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    /// <summary>
    /// SqlHighlighter(증분 강조): 글을 고칠 때마다 바뀐 곳 근처만 다시 훑어도 처음부터 다시 훑은 것(SqlScript.EditorSpans)과 같아야 하고,
    /// 돌려준 다시 칠할 범위 밖의 구간은 예전과 같아야 한다(뒤쪽은 위치만 옮김). 무작위 고치기 수천 번으로 확인한다.
    /// </summary>
    public class SqlHighlighterTests
    {
        /// <summary>세 DBMS 문법과 여러 줄 토큰이 섞인 글(CRLF·LF 섞임).</summary>
        public const string Mixed = "-- 주석 SELECT\r\n"
            + "SELECT a, 'it''s', q'[x ' y]', \"Col\nName\" FROM t WHERE a = 1; /* 블록\r\n 여러 줄 */\n"
            + "SELECT 'line1\nline2' FROM dual;\n"
            + "BEGIN\n  NULL; -- 안\nEND;\n/\n"
            + "DO $$ BEGIN RAISE NOTICE 'x; -- y'; END $$;\n"
            + "SELECT E'it\\'s' FROM t; /* 바깥 /* 안쪽 */ 주석 */\n"
            + "SELECT TOP 1 [col -- x] FROM [t];\nGO\n"
            + "CREATE TABLE t_order (id NUMBER(10) PRIMARY KEY, name VARCHAR2(100) NOT NULL);\n";

        private static readonly string[] Pieces =
        {
            "'", "''", "/*", "*/", "--", "\n", "\r\n", "\r", "\"", "[", "]", "q'[", "]'", "$$", "$t$", "E'", "\\", " ", "x", "SELECT ", "from",
            " WHERE ", "AND", "GO\n", "/\n", ";", ".", ":", "@", "한글", "😀", "N'"
        };

        private static List<(SqlSpanKind, int, int)> Of(IEnumerable<SqlSpan> spans)
        {
            return spans.Select(s => (s.Kind, s.Start, s.Length)).ToList();
        }

        [Theory]
        [InlineData("Oracle")]
        [InlineData("PostgreSQL")]
        [InlineData("SQL Server")]
        public void RandomEdits_SameAsFullScan_AndOutsideRangeUnchanged(string name)
        {
            var rules = new[] { SqlLexRules.Oracle, SqlLexRules.Postgres, SqlLexRules.SqlServer }.Single(r => r.Name == name);
            var random = new Random(20261021);
            var text = Mixed + Mixed;
            var highlighter = new SqlHighlighter(rules);
            highlighter.Reset(new SqlStringSource(text));
            Assert.Equal(Of(SqlScript.EditorSpans(text, rules)), Of(highlighter.Spans));
            for (var step = 0; step < 4000; step++)
            {
                var offset = random.Next(text.Length + 1);
                var removed = random.Next(4) == 0 ? Math.Min(text.Length - offset, random.Next(1, 30)) : 0;
                var inserted = removed > 0 && random.Next(2) == 0 ? "" : Pieces[random.Next(Pieces.Length)];
                // 글이 너무 줄거나 늘지 않게
                if (text.Length > 3000)
                    inserted = "";
                if (text.Length < 200)
                    removed = 0;
                var before = highlighter.Spans.ToList();
                var next = text.Remove(offset, removed).Insert(offset, inserted);
                var range = highlighter.Update(new SqlStringSource(next), offset, removed, inserted.Length, text.Substring(offset, removed));
                var full = SqlScript.EditorSpans(next, rules);
                Assert.True(Of(full).SequenceEqual(Of(highlighter.Spans)),
                    "step " + step + ": '" + Escape(inserted) + "' at " + offset + " (-" + removed + "), 범위 " + range + "\n" + Escape(next) + "\n"
                    + Difference(next, full, highlighter.Spans.ToList()));
                // 범위 앞은 그대로, 범위 뒤는 위치만 옮겨 그대로
                var delta = inserted.Length - removed;
                Assert.Equal(Of(before.Where(s => s.End <= range.Start)), Of(full.Where(s => s.End <= range.Start)));
                Assert.Equal(Of(before.Where(s => s.Start + delta >= range.End && s.Start >= offset + removed).Select(s => new SqlSpan(s.Kind, s.Start + delta, s.Length))),
                    Of(full.Where(s => s.Start >= range.End)));
                Assert.True(range.Start <= Math.Min(offset, next.Length) && range.End <= next.Length);
                text = next;
            }
        }

        /// <summary>긴 여러 줄 토큰(블록 주석·문자열·q'[…]'·$$·E'…'·"…"·[…]) 안을 주로 고친다 — 토큰 안 지름길(둘레만 읽음)과 보통 길이 섞인다.</summary>
        [Theory]
        [InlineData("Oracle")]
        [InlineData("PostgreSQL")]
        [InlineData("SQL Server")]
        public void RandomEditsInsideLongTokens_SameAsFullScan(string name)
        {
            var rules = new[] { SqlLexRules.Oracle, SqlLexRules.Postgres, SqlLexRules.SqlServer }.Single(r => r.Name == name);
            var filler = string.Concat(Enumerable.Repeat("abc 한글 x SELECT\n", 20));
            var text = "SELECT 1 FROM t;\n/* " + filler + " */\nSELECT '" + filler + "' FROM t;\nSELECT q'[" + filler + "]' FROM t;\n"
                + "DO $$ " + filler + " $$;\nSELECT \"" + filler + "\" FROM t;\nSELECT [" + filler + "] FROM t;\nSELECT E'" + filler + "' FROM t;\n";
            var random = new Random(7);
            var highlighter = new SqlHighlighter(rules);
            highlighter.Reset(new SqlStringSource(text));
            var shortcuts = 0;
            for (var step = 0; step < 4000; step++)
            {
                var offset = random.Next(text.Length + 1);
                var removed = random.Next(3) == 0 ? Math.Min(text.Length - offset, random.Next(1, 6)) : 0;
                // 대부분 토큰을 끝내지 않는 글자, 가끔 구분 글자
                var inserted = random.Next(5) == 0 ? Pieces[random.Next(Pieces.Length)] : new[] { "x", " ", "가", "\n", "SELECT", "" }[random.Next(6)];
                if (text.Length > 6000)
                    inserted = "";
                var next = text.Remove(offset, removed).Insert(offset, inserted);
                highlighter.Update(new SqlStringSource(next), offset, removed, inserted.Length, text.Substring(offset, removed));
                if (highlighter.LastScanned > 0 && highlighter.LastScanned <= 2 * 64 + 6)
                    shortcuts++;
                Assert.True(Of(SqlScript.EditorSpans(next, rules)).SequenceEqual(Of(highlighter.Spans)),
                    "step " + step + ": '" + Escape(inserted) + "' at " + offset + " (-" + removed + ")\n" + Difference(next, SqlScript.EditorSpans(next, rules), highlighter.Spans.ToList()));
                text = next;
            }
            Assert.True(shortcuts > 100, "토큰 안 지름길 " + shortcuts + "번");
        }

        [Fact]
        public void TypingInsideHugeComment_ReadsOnlyAround()
        {
            var text = "/* 전부 주석\n" + string.Concat(Enumerable.Repeat("SELECT a FROM t WHERE x = 1; -- 안\n", 50000)) + "*/\nSELECT 1 FROM dual;\n";
            var highlighter = new SqlHighlighter(SqlLexRules.Oracle);
            highlighter.Reset(new SqlStringSource(text));
            var offset = text.Length / 2;
            var next = text.Insert(offset, "x");
            var range = highlighter.Update(new SqlStringSource(next), offset, 0, 1);
            Assert.True(highlighter.LastScanned < 200, "읽은 글자 " + highlighter.LastScanned);
            Assert.Equal(offset, range.Start);
            Assert.Equal(offset, range.End);
            Assert.Equal(Of(SqlScript.EditorSpans(next)), Of(highlighter.Spans));
            // 지운 글을 주면 지우기도 둘레만, 주석을 닫는 글자를 넣으면 보통 길(다시 훑음)
            var removed = next.Remove(offset, 1);
            highlighter.Update(new SqlStringSource(removed), offset, 1, 0, "x");
            Assert.True(highlighter.LastScanned < 200);
            var closed = removed.Insert(offset, "*/");
            highlighter.Update(new SqlStringSource(closed), offset, 0, 2);
            Assert.Equal(Of(SqlScript.EditorSpans(closed)), Of(highlighter.Spans));
        }

        [Fact]
        public void TypingInMiddleOfLargeText_ScansAFewKilobytes()
        {
            var unit = "SELECT a, 'b', q'[c]' FROM t WHERE x = 1; -- 주석\n/* 블록\n 주석 */\nUPDATE t SET a = 'z' WHERE id = 2;\n";
            var text = string.Concat(Enumerable.Repeat(unit, 20000));
            var highlighter = new SqlHighlighter(SqlLexRules.Oracle);
            highlighter.Reset(new SqlStringSource(text));
            var offset = text.Length / 2;
            offset = text.IndexOf("WHERE", offset, StringComparison.Ordinal);
            var next = text.Insert(offset, "x");
            highlighter.Update(new SqlStringSource(next), offset, 0, 1);
            Assert.True(highlighter.LastScanned < 20000, "읽은 글자 " + highlighter.LastScanned);
            Assert.Equal(Of(SqlScript.EditorSpans(next)), Of(highlighter.Spans));
        }

        [Fact]
        public void OpeningCommentAtTop_RecolorsUntilFirstClose_ThenConverges()
        {
            var text = "SELECT 1 FROM t;\nSELECT 2 FROM t; /* a */\nSELECT 3 FROM t;\n";
            var highlighter = new SqlHighlighter(SqlLexRules.Oracle);
            highlighter.Reset(new SqlStringSource(text));
            var next = "/*" + text;
            var range = highlighter.Update(new SqlStringSource(next), 0, 0, 2);
            // 맨 앞 /*는 첫 */에서 닫힌다 — 그 뒤 SELECT 3은 예전 그대로라 범위는 거기까지
            Assert.Equal(0, range.Start);
            Assert.Equal(next.IndexOf("SELECT 3", StringComparison.Ordinal), range.End);
            Assert.Equal(Of(SqlScript.EditorSpans(next)), Of(highlighter.Spans));
        }

        [Fact]
        public void UnclosedQuote_RunsToEnd_ThenClosingRestores()
        {
            var text = string.Concat(Enumerable.Repeat("SELECT 'a' FROM t;\n", 2000));
            var highlighter = new SqlHighlighter(SqlLexRules.Oracle);
            highlighter.Reset(new SqlStringSource(text));
            var opened = "'" + text;
            var range = highlighter.Update(new SqlStringSource(opened), 0, 0, 1);
            Assert.Equal(Of(SqlScript.EditorSpans(opened)), Of(highlighter.Spans));
            Assert.True(range.End > opened.Length / 2);
            highlighter.Update(new SqlStringSource(text), 0, 1, 0);
            Assert.Equal(Of(SqlScript.EditorSpans(text)), Of(highlighter.Spans));
        }

        [Fact]
        public void EditInsideMultiLineToken_StartsFromTokenLine()
        {
            var text = "SELECT 1 FROM t;\nSELECT 'one\ntwo\nthree' FROM t;\nSELECT 2 FROM t;\n";
            var highlighter = new SqlHighlighter(SqlLexRules.Oracle);
            highlighter.Reset(new SqlStringSource(text));
            var at = text.IndexOf("three", StringComparison.Ordinal);
            var next = text.Insert(at, "'");
            var range = highlighter.Update(new SqlStringSource(next), at, 0, 1);
            Assert.Equal(text.IndexOf("SELECT 'one", StringComparison.Ordinal), range.Start);
            Assert.Equal(Of(SqlScript.EditorSpans(next)), Of(highlighter.Spans));
        }

        [Fact]
        public void EmptyAndWholeReplace()
        {
            var highlighter = new SqlHighlighter(null);
            highlighter.Reset(new SqlStringSource(""));
            Assert.Empty(highlighter.Spans);
            highlighter.Update(new SqlStringSource(Mixed), 0, 0, Mixed.Length);
            Assert.Equal(Of(SqlScript.EditorSpans(Mixed)), Of(highlighter.Spans));
            highlighter.Update(new SqlStringSource(""), 0, Mixed.Length, 0);
            Assert.Empty(highlighter.Spans);
        }

        [Fact]
        public void StringSource_Lines()
        {
            var source = new SqlStringSource("ab\r\ncd\ne\rf");
            Assert.Equal(0, source.LineStart(0));
            Assert.Equal(0, source.LineStart(3));   // \r 다음 \n 앞은 아직 첫 줄
            Assert.Equal(4, source.LineStart(4));
            Assert.Equal(7, source.LineStart(8));
            Assert.Equal(9, source.LineStart(10));
            Assert.Equal(4, source.NextLineStart(0));
            Assert.Equal(4, source.NextLineStart(2));
            Assert.Equal(7, source.NextLineStart(5));
            Assert.Equal(9, source.NextLineStart(7));
            Assert.Equal(10, source.NextLineStart(9));
        }

        private static string Difference(string text, List<SqlSpan> expected, List<SqlSpan> actual)
        {
            var i = 0;
            while (i < expected.Count && i < actual.Count && expected[i].Kind == actual[i].Kind && expected[i].Start == actual[i].Start && expected[i].Length == actual[i].Length)
                i++;
            Func<List<SqlSpan>, string> show = list => i < list.Count
                ? list[i].Kind + " " + list[i].Start + "+" + list[i].Length + " '" + Escape(text.Substring(list[i].Start, Math.Min(list[i].Length, 40))) + "'"
                : "(없음)";
            return "첫 차이 #" + i + ": 전체 " + show(expected) + " / 증분 " + show(actual);
        }

        private static string Escape(string text)
        {
            var sb = new StringBuilder();
            foreach (var c in text)
                sb.Append(c == '\r' ? "\\r" : c == '\n' ? "\\n" : c.ToString());
            return sb.ToString();
        }
    }
}

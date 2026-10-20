using System.Collections.Generic;
using System.Linq;
using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    /// <summary>
    /// SqlScript.HighlightSpans: 구문 강조용 주석·문자열 구간(docs/sql-editor-avalonedit-plan.md 2단계).
    /// 기대값은 계획서 5절 D1(경계 사례)이다. 구간 밖(따옴표 식별자·숫자·키워드 등)은 일반 글.
    /// </summary>
    public class SqlHighlightTests
    {
        // 계획서 D1 그대로(줄 끝 LF)
        private const string D1 = "-- 줄 주석 한글\n"
            + "SELECT 1 FROM DUAL; -- 끝 주석\n"
            + "/* 블록\n"
            + "   여러 줄 -- 안의 줄 주석 기호는 주석의 일부 */\n"
            + "SELECT /*+ INDEX(t IX_T) */ * FROM T t;          -- 힌트(블록 주석으로 칠함)\n"
            + "SELECT 'a -- 문자열 안' , 'it''s', '/* 아님 */' FROM DUAL;\n"
            + "SELECT q'[ -- 아님 ' 아님 ]', q'{ } }', q'( ) )', q'< >', q'! ! !', nq'[ 한글 ]', N'유니코드' FROM DUAL;\n"
            + "SELECT \"Col -- 아님\", \"a\"\"b\" FROM DUAL;\n"
            + "SELECT 'line1\n"
            + "line2 -- 여러 줄 문자열 안' FROM DUAL;\n"
            + "SELECT 1 - -1 FROM DUAL;                           -- 빼기·음수(주석 아님)\n"
            + "SELECT 10/2 FROM DUAL;                            -- 나눗셈(주석 아님)\n"
            + "SELECT '😀 이모지' FROM DUAL; -- 서로게이트 쌍: 오프셋 어긋남 확인\n"
            + "BEGIN\n"
            + "  NULL; -- PL/SQL 안 주석\n"
            + "END;\n"
            + "/\n"
            + "SELECT '닫히지 않은 문자열 → 끝까지 문자열\n";

        private const SqlSpanKind C = SqlSpanKind.Comment;
        private const SqlSpanKind S = SqlSpanKind.String;

        // D1의 기대 구간(앞에서 뒤로). 줄 주석은 줄 끝 공백을 넣지 않는다.
        private static readonly (SqlSpanKind Kind, string Text)[] D1Expected =
        {
            (C, "-- 줄 주석 한글"),
            (C, "-- 끝 주석"),
            (C, "/* 블록\n   여러 줄 -- 안의 줄 주석 기호는 주석의 일부 */"),
            (C, "/*+ INDEX(t IX_T) */"),
            (C, "-- 힌트(블록 주석으로 칠함)"),
            (S, "'a -- 문자열 안'"),
            (S, "'it''s'"),
            (S, "'/* 아님 */'"),
            (S, "q'[ -- 아님 ' 아님 ]'"),
            (S, "q'{ } }'"),
            (S, "q'( ) )'"),
            (S, "q'< >'"),
            (S, "q'! ! !'"),
            (S, "nq'[ 한글 ]'"),
            (S, "N'유니코드'"),
            (S, "'line1\nline2 -- 여러 줄 문자열 안'"),
            (C, "-- 빼기·음수(주석 아님)"),
            (C, "-- 나눗셈(주석 아님)"),
            (S, "'😀 이모지'"),
            (C, "-- 서로게이트 쌍: 오프셋 어긋남 확인"),
            (C, "-- PL/SQL 안 주석"),
            (S, "'닫히지 않은 문자열 → 끝까지 문자열\n")
        };

        private static List<(SqlSpanKind Kind, string Text)> Spans(string script)
        {
            return SqlScript.HighlightSpans(script).Select(s => (s.Kind, script.Substring(s.Start, s.Length))).ToList();
        }

        [Fact]
        public void D1_CommentsAndStrings()
        {
            Assert.Equal(D1Expected, Spans(D1));
        }

        [Fact]
        public void D1_QuotedIdentifiersNumbersOperators_AreNotSpans()
        {
            var texts = Spans(D1).Select(s => s.Text).ToList();
            // 따옴표 식별자 안의 --, 빼기·음수, 나눗셈, '/' 줄은 주석도 문자열도 아니다
            Assert.DoesNotContain(texts, t => t.Contains("Col -- 아님"));
            Assert.DoesNotContain(texts, t => t.StartsWith("- -1") || t.StartsWith("-1"));
            Assert.DoesNotContain(texts, t => t.Contains("10/2"));
        }

        [Fact]
        public void LineEndings_CrLfAndMixed_SameSpansAtShiftedOffsets()
        {
            var crlf = D1.Replace("\n", "\r\n");
            Assert.Equal(D1Expected.Select(e => (e.Kind, e.Text.Replace("\n", "\r\n"))).ToArray(), Spans(crlf));
            // 섞임: 짝수 줄만 CRLF
            var lines = D1.Split('\n');
            var mixed = string.Join("", lines.Select((l, i) => i == lines.Length - 1 ? l : l + (i % 2 == 0 ? "\r\n" : "\n")));
            var spans = SqlScript.HighlightSpans(mixed);
            Assert.Equal(D1Expected.Length, spans.Count);
            Assert.Equal(D1Expected.Select(e => e.Kind), spans.Select(s => s.Kind));
            // 오프셋이 글 그대로의 위치(\r\n은 2글자)라 Substring이 각 구간의 원문과 같다
            Assert.Equal(D1Expected.Select(e => e.Text.Replace("\n", "")).ToArray(),
                spans.Select(s => mixed.Substring(s.Start, s.Length).Replace("\r", "").Replace("\n", "")).ToArray());
        }

        [Fact]
        public void TabIndent_SameSpans()
        {
            var tabbed = string.Join("\n", D1.Split('\n').Select(l => l.Length == 0 ? l : "\t" + l));
            var expected = D1Expected.Select(e => (e.Kind, e.Text.Replace("\n", "\n\t"))).ToArray();
            // 마지막(닫히지 않은 문자열)은 끝 줄바꿈 뒤에 탭이 붙지 않는다(빈 마지막 줄)
            expected[expected.Length - 1] = (S, "'닫히지 않은 문자열 → 끝까지 문자열\n");
            Assert.Equal(expected, Spans(tabbed));
        }

        [Fact]
        public void UnclosedBlockComment_RunsToEnd()
        {
            const string script = "/* 닫히지 않은 블록 주석 → 끝까지 주석\nSELECT 'x' FROM DUAL;\n";
            Assert.Equal(new[] { (C, script) }, Spans(script));
        }

        [Fact]
        public void Surrogates_OffsetsAreUtf16Indexes()
        {
            const string script = "SELECT '😀' AS e, 'b' FROM DUAL; -- 끝";
            var spans = SqlScript.HighlightSpans(script);
            Assert.Equal(new[] { 7, 18, 33 }, spans.Select(s => s.Start).ToArray());
            Assert.Equal(new[] { 4, 3, 4 }, spans.Select(s => s.Length).ToArray());
            Assert.All(spans, s => Assert.Equal(s.Start + s.Length, s.End));
        }

        [Fact]
        public void EmptyOrNull_NoSpans()
        {
            Assert.Empty(SqlScript.HighlightSpans(""));
            Assert.Empty(SqlScript.HighlightSpans(null));
            Assert.Empty(SqlScript.HighlightSpans("SELECT 1 FROM DUAL;\n/\n"));
        }

        [Fact]
        public void Spans_AreOrderedAndDoNotOverlap()
        {
            var spans = SqlScript.HighlightSpans(D1);
            for (var i = 1; i < spans.Count; i++)
                Assert.True(spans[i - 1].End <= spans[i].Start);
        }
    }
}

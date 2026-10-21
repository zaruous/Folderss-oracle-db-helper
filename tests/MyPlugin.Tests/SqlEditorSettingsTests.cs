using System.Collections.Generic;
using System.Linq;
using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    /// <summary>SqlEditorSettings(설정 "SQL 편집기" 탭): 기본값, 저장 형식 왕복, 깨진 값은 그 항목만 기본값, 설정 창 입력 검사.</summary>
    public class SqlEditorSettingsTests
    {
        [Fact]
        public void Defaults()
        {
            var settings = new SqlEditorSettings();
            Assert.Equal(0, settings.LineHeight);
            Assert.Equal(20000, settings.MaxColoredLine);
            Assert.Equal(new[] { SqlSpanKind.Keyword, SqlSpanKind.DataType, SqlSpanKind.String, SqlSpanKind.Comment }, settings.Styles.Keys.OrderBy(k => System.Array.IndexOf(SqlEditorSettings.StyledKinds, k)));
            Assert.All(settings.Styles.Values, s => Assert.True(s.Enabled && s.Color == null && !s.Bold && !s.Italic));
            Assert.Empty(settings.ExtraKeywords);
            Assert.True(settings.IsDefault);
            // 비었거나 깨진 저장값은 기본값
            Assert.True(SqlEditorSettings.Parse(null).IsDefault);
            Assert.True(SqlEditorSettings.Parse("").IsDefault);
            Assert.True(SqlEditorSettings.Parse("{깨짐").IsDefault);
            Assert.True(SqlEditorSettings.Parse("[1,2]").IsDefault);
        }

        [Fact]
        public void RoundTrip()
        {
            var settings = new SqlEditorSettings { LineHeight = 19, MaxColoredLine = 50000 };
            settings.Style(SqlSpanKind.Keyword).Color = 0xC678DD;
            settings.Style(SqlSpanKind.Keyword).Bold = true;
            settings.Style(SqlSpanKind.Comment).Italic = true;
            settings.Style(SqlSpanKind.DataType).Enabled = false;
            settings.ExtraKeywords.AddRange(new[] { "NVL", "DECODE" });
            settings.ExcludedKeywords.Add("LEVEL");
            var json = settings.Serialize();
            var back = SqlEditorSettings.Parse(json);
            Assert.Equal(json, back.Serialize());
            Assert.Equal(19, back.LineHeight);
            Assert.Equal(50000, back.MaxColoredLine);
            Assert.Equal(0xC678DD, back.Style(SqlSpanKind.Keyword).Color);
            Assert.True(back.Style(SqlSpanKind.Keyword).Bold);
            Assert.True(back.Style(SqlSpanKind.Comment).Italic);
            Assert.False(back.Style(SqlSpanKind.DataType).Enabled);
            Assert.Equal(new[] { "NVL", "DECODE" }, back.ExtraKeywords);
            Assert.Equal(new[] { "LEVEL" }, back.ExcludedKeywords);
            Assert.False(back.IsDefault);
            Assert.Contains("\"color\":\"#C678DD\"", json);
            // 사본은 따로 논다
            var copy = back.Clone();
            copy.Style(SqlSpanKind.Keyword).Bold = false;
            copy.ExtraKeywords.Clear();
            Assert.True(back.Style(SqlSpanKind.Keyword).Bold);
            Assert.Equal(2, back.ExtraKeywords.Count);
        }

        [Fact]
        public void Parse_OutOfRangeOrBadItems_FallBackPerItem()
        {
            var settings = SqlEditorSettings.Parse("{\"lineHeight\":200,\"maxColoredLine\":5,"
                + "\"styles\":{\"keyword\":{\"enabled\":false,\"color\":\"파랑\",\"bold\":\"yes\"},\"string\":{\"color\":\"#00ff00\"}},"
                + "\"extraKeywords\":[\"nvl\",\"한글\",\"NVL\",3,\"a b\"]}");
            Assert.Equal(0, settings.LineHeight);
            Assert.Equal(20000, settings.MaxColoredLine);
            Assert.False(settings.Style(SqlSpanKind.Keyword).Enabled);
            Assert.Null(settings.Style(SqlSpanKind.Keyword).Color);
            Assert.False(settings.Style(SqlSpanKind.Keyword).Bold);
            Assert.Equal(0x00FF00, settings.Style(SqlSpanKind.String).Color);
            Assert.True(settings.Style(SqlSpanKind.Comment).Enabled);
            Assert.Equal(new[] { "NVL" }, settings.ExtraKeywords);
        }

        [Theory]
        [InlineData("20000", 20000, true)]
        [InlineData(" 1,000 ", 1000, true)]
        [InlineData("1000000", 1000000, true)]
        [InlineData("999", 20000, false)]
        [InlineData("1000001", 20000, false)]
        [InlineData("abc", 20000, false)]
        [InlineData("", 20000, false)]
        public void ParseMaxColoredLine(string text, int expected, bool ok)
        {
            int value;
            var error = SqlEditorSettings.ParseMaxColoredLine(text, out value);
            Assert.Equal(ok, error == null);
            Assert.Equal(expected, value);
            if (!ok)
                Assert.Contains("1,000~1,000,000", error);
        }

        [Theory]
        [InlineData("", null, true)]
        [InlineData("  ", null, true)]
        [InlineData("#4F8CFF", 0x4F8CFF, true)]
        [InlineData("a65200", 0xA65200, true)]
        [InlineData("#FFF", null, false)]
        [InlineData("파랑", null, false)]
        public void ParseColor(string text, int? expected, bool ok)
        {
            int? color;
            var error = SqlEditorSettings.ParseColor(text, out color);
            Assert.Equal(ok, error == null);
            Assert.Equal(expected, color);
            Assert.Equal(expected.HasValue ? "#" + expected.Value.ToString("X6") : "", SqlEditorSettings.FormatColor(color));
        }

        [Fact]
        public void ParseWords()
        {
            List<string> words;
            Assert.Null(SqlEditorSettings.ParseWords("nvl, DECODE;\nnvl  sys_context v$session", out words));
            Assert.Equal(new[] { "NVL", "DECODE", "SYS_CONTEXT", "V$SESSION" }, words);
            Assert.Equal("NVL DECODE SYS_CONTEXT V$SESSION", SqlEditorSettings.FormatWords(words));
            var error = SqlEditorSettings.ParseWords("ok 1abc 한글 a-b", out words);
            Assert.Contains("1abc, 한글, a-b", error);
            Assert.Equal(new[] { "OK" }, words);
            Assert.Null(SqlEditorSettings.ParseWords("", out words));
            Assert.Empty(words);
        }

        [Fact]
        public void LineHeightLabels()
        {
            Assert.Equal("글꼴 기본", SqlEditorSettings.LineHeightLabel(0));
            Assert.Equal("19px (지금 SQL 탭과 같음)", SqlEditorSettings.LineHeightLabel(19));
            Assert.Equal("24px", SqlEditorSettings.LineHeightLabel(24));
            Assert.Contains(19.0, SqlEditorSettings.LineHeightChoices);
            Assert.Equal(0, SqlEditorSettings.LineHeightChoices[0]);
        }
    }
}

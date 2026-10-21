using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    /// <summary>SqlHighlightLogic.Palette: Folderss 테마 7종 색에서 정한 주석·문자열·키워드 색이 바탕 대비 4.5:1 이상이고 주석이 본문 글자와 구분된다.</summary>
    public class SqlHighlightLogicTests
    {
        // Folderss Themes/*.xaml: PanelBackground, PrimaryText, SecondaryText, Accent
        [Theory]
        [InlineData("Black", 0x0E1013, 0xF3F4F6, 0x9CA3AF, 0x4F8CFF)]
        [InlineData("Catppuccin", 0x1E1E2E, 0xCAD3F5, 0xA5ADCB, 0xC6A0F6)]
        [InlineData("Dracula", 0x282A36, 0xF8F8F2, 0xBFBFCF, 0xFF79C6)]
        [InlineData("GitHub", 0xFFFFFF, 0x1F2328, 0x656D76, 0x0969DA)]
        [InlineData("Light", 0xFFFFFF, 0x171A1F, 0x626B78, 0x2563EB)]
        [InlineData("Nord", 0x2E3440, 0xECEFF4, 0xD8DEE9, 0x88C0D0)]
        [InlineData("Solarized", 0x002B36, 0xFDF6E3, 0x93A1A1, 0x268BD2)]
        public void Palette_ReadableOnEveryTheme(string theme, int background, int text, int secondary, int accent)
        {
            var palette = SqlHighlightLogic.Palette(background, text, secondary, accent);
            Assert.True(SqlHighlightLogic.Contrast(palette.Comment, background) >= SqlHighlightLogic.MinContrast, theme + " 주석");
            Assert.True(SqlHighlightLogic.Contrast(palette.String, background) >= SqlHighlightLogic.MinContrast, theme + " 문자열");
            Assert.True(SqlHighlightLogic.Contrast(palette.Keyword, background) >= SqlHighlightLogic.MinContrast, theme + " 키워드");
            Assert.True(SqlHighlightLogic.Contrast(palette.Comment, text) >= SqlHighlightLogic.CommentSeparation, theme + " 주석과 본문 구분");
        }

        [Fact]
        public void Palette_KeepsThemeColorsWhenAlreadyGood()
        {
            // Black: 보조 글자색·강조색이 이미 기준을 넘으므로 그대로
            var palette = SqlHighlightLogic.Palette(0x0E1013, 0xF3F4F6, 0x9CA3AF, 0x4F8CFF);
            Assert.Equal(0x9CA3AF, palette.Comment);
            Assert.Equal(0x4F8CFF, palette.Keyword);
            Assert.Equal(SqlHighlightLogic.DarkString, palette.String);
            Assert.Equal(SqlHighlightLogic.LightString, SqlHighlightLogic.Palette(0xFFFFFF, 0x171A1F, 0x626B78, 0x2563EB).String);
        }

        [Fact]
        public void Contrast_KnownValues()
        {
            Assert.Equal(21.0, SqlHighlightLogic.Contrast(0x000000, 0xFFFFFF), 3);
            Assert.Equal(1.0, SqlHighlightLogic.Contrast(0x777777, 0x777777), 3);
        }
    }
}

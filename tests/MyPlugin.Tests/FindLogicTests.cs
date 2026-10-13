using System.Collections.Generic;
using System.Linq;
using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    public class FindLogicTests
    {
        [Fact]
        public void FindAll_IgnoresCaseAndDoesNotOverlap()
        {
            Assert.Equal(new[] { 0, 14 }, FindLogic.FindAll("select * from SELECTED", "select"));
            Assert.Equal(new[] { 0, 2 }, FindLogic.FindAll("aaaa", "aa"));
            Assert.Empty(FindLogic.FindAll("SELECT 1", "x"));
            Assert.Empty(FindLogic.FindAll("SELECT 1", ""));
            Assert.Empty(FindLogic.FindAll(null, "a"));
        }

        [Theory]
        // 일치 시작: 2, 10, 20
        [InlineData(0, true, false, 0)]
        [InlineData(2, true, false, 1)]   // 다음: 지금 위치에서 시작하는 일치는 건너뜀
        [InlineData(2, true, true, 0)]    // 입력 중: 제자리
        [InlineData(15, true, false, 2)]
        [InlineData(20, true, false, 0)]  // 끝 → 처음
        [InlineData(10, false, false, 0)] // 이전
        [InlineData(2, false, false, 2)]  // 처음 → 끝
        [InlineData(0, false, true, 2)]
        public void NextIndex_WrapsAround(int position, bool forward, bool inclusive, int expected)
        {
            Assert.Equal(expected, FindLogic.NextIndex(new[] { 2, 10, 20 }, position, forward, inclusive));
        }

        [Fact]
        public void NextIndex_NoMatches()
        {
            Assert.Equal(-1, FindLogic.NextIndex(new List<int>(), 0, true, true));
        }

        [Fact]
        public void FindCells_RowThenColumnOrder_SkipsNull()
        {
            var rows = new List<string[]>
            {
                new[] { "Apple", null, "pineapple" },
                new[] { "x", "y", "z" },
                new[] { null, "APPLE pie", "1" }
            };
            var matches = FindLogic.FindCells(rows, "apple");
            Assert.Equal(new[] { (0, 0), (0, 2), (2, 1) }, matches.Select(m => (m.Row, m.Column)).ToArray());
            Assert.Empty(FindLogic.FindCells(rows, ""));
        }

        [Fact]
        public void NextCell_FromFoundCellOrSelectedRow()
        {
            var matches = new List<CellMatch> { new CellMatch(0, 0), new CellMatch(0, 2), new CellMatch(2, 1) };
            Assert.Equal(1, FindLogic.NextCell(matches, 0, 0, true, false));   // 다음 칸
            Assert.Equal(1, FindLogic.NextCell(matches, 0, 2, true, true));    // 입력 중 제자리
            Assert.Equal(0, FindLogic.NextCell(matches, 2, 1, true, false));   // 끝 → 처음
            Assert.Equal(2, FindLogic.NextCell(matches, 1, -1, true, true));   // 고른 행(1)부터
            Assert.Equal(1, FindLogic.NextCell(matches, 2, 1, false, false));  // 이전
            Assert.Equal(2, FindLogic.NextCell(matches, 0, 0, false, false));  // 처음 → 끝
            Assert.Equal(-1, FindLogic.NextCell(new List<CellMatch>(), 0, -1, true, true));
        }

        [Fact]
        public void CountText()
        {
            Assert.Equal("", FindLogic.CountText("", -1, 0));
            Assert.Equal("결과 없음", FindLogic.CountText("x", -1, 0));
            Assert.Equal("3/12", FindLogic.CountText("x", 2, 12));
        }
    }
}

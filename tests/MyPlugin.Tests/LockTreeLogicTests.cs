using System;
using System.Linq;
using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    /// <summary>잠금 연쇄 트리(LockTreeLogic): 간선 결과 → 근원이 맨 위인 숲, 자손 수·유발 대기, 여러 블로커·순환·정보 없는 블로커, 최소 대기, 표·조치 값.</summary>
    public class LockTreeLogicTests
    {
        // 계약 열 + 표시 열(Oracle 트리와 같은 모양의 일부)
        private static QueryTable Table(params string[][] rows)
        {
            var table = new QueryTable();
            table.Columns.AddRange(new[] { "SESSION_KEY", "BLOCKER_KEY", "WAIT_SECONDS", "IDLE_SECONDS", "인스턴스", "SID", "SERIAL#", "사용자" });
            foreach (var row in rows)
                table.Rows.Add(row);
            return table;
        }

        private static string[] Row(string key, string blocker, string wait, string idle, string user = "APP")
        {
            var parts = key.Split(',');
            return new[] { key, blocker, wait, idle, parts[0], parts.Length > 1 ? parts[1] : null, "7" + parts.Last(), user };
        }

        private static string[] Keys(LockForest forest)
        {
            return forest.Rows.Select(r => r.Prefix + r.Node.Key).ToArray();
        }

        [Fact]
        public void Chain_RootOnTop_DescendantsAndInducedWait()
        {
            // A(1,10)가 쉬면서 B(1,20)를 막고, B가 C(1,30)를 막는다
            var forest = LockTreeLogic.Build(Table(
                Row("1,30", "1,20", "5", null),
                Row("1,20", "1,10", "40", null),
                Row("1,10", null, null, "300", "SCOTT")), 3);
            Assert.Equal(new[] { "● 1,10", "└ 1,20", "   └ 1,30" }, Keys(forest));
            var root = forest.Rows[0];
            Assert.Same(root.Node, root.Root);
            Assert.Equal(2, root.BlockedCount);
            Assert.Equal(45, root.InducedWaitSeconds);
            Assert.Equal(1, forest.Rows[1].BlockedCount);
            Assert.Equal(0, forest.Rows[2].BlockedCount);
            Assert.All(forest.Rows, r => Assert.Same(root.Node, r.Root));
            Assert.Equal(1, forest.RootCount);
            Assert.Equal(2, forest.BlockedCount);
            Assert.Equal(40, forest.LongestWaitSeconds);
            Assert.False(forest.HasCycle);
            Assert.Equal("근원 1 · 막힌 세션 2 · 최장 대기 40초", LockTreeLogic.SummaryText(forest, 3));
        }

        [Fact]
        public void Roots_SortedByBlockedCountThenInducedWait_ChildrenByDescendantsThenWait()
        {
            var forest = LockTreeLogic.Build(Table(
                Row("1,1", null, null, "10"),
                Row("1,2", "1,1", "100", null),
                Row("1,5", null, null, "10"),
                Row("1,6", "1,5", "4", null),
                Row("1,7", "1,5", "9", null),
                Row("1,8", "1,6", "3", null)), 0);
            // 근원 1,5(막은 3) → 1,1(막은 1). 1,5의 자식: 1,6(자손 1개) → 1,7(자손 없음, 대기는 더 길어도)
            Assert.Equal(new[] { "● 1,5", "├ 1,6", "│  └ 1,8", "└ 1,7", "● 1,1", "└ 1,2" }, Keys(forest));
            Assert.Equal(2, forest.RootCount);
            Assert.Equal(4, forest.BlockedCount);
        }

        [Fact]
        public void MultipleBlockers_ShownUnderEachParent_SecondTimeRepeatedNotExpanded()
        {
            // PostgreSQL: 3이 1과 2 모두에 막힘, 3은 4를 막음
            var forest = LockTreeLogic.Build(Table(
                Row("3", "1", "20", null),
                Row("3", "2", "20", null),
                Row("4", "3", "8", null),
                Row("1", null, null, "50"),
                Row("2", null, null, "50")), 0);
            Assert.Equal(new[] { "● 1", "└ 3", "   └ 4", "● 2", "└ 3" }, Keys(forest));
            Assert.True(forest.Rows[4].Repeated);
            Assert.Equal(0, forest.Rows[4].BlockedCount);
            var repeated = LockTreeLogic.ToTable(forest).Rows[4];
            Assert.Equal("└ 3" + LockTreeLogic.RepeatedSuffix, repeated[0]);
            Assert.Null(repeated[1]);
            Assert.Null(repeated[2]);
            Assert.Equal(2, forest.BlockedCount);
        }

        [Fact]
        public void Cycle_NoRoot_GroupedSeparately_AndTerminates()
        {
            var forest = LockTreeLogic.Build(Table(
                Row("1,1", "1,2", "2", null),
                Row("1,2", "1,1", "2", null)), 3);
            Assert.True(forest.HasCycle);
            Assert.Equal(0, forest.RootCount);
            Assert.Equal(new[] { LockTreeLogic.CycleMark + "1,1", "└ 1,2", "   └ 1,1" }, Keys(forest));
            Assert.True(forest.Rows[2].Repeated);
            Assert.All(forest.Rows, r => Assert.True(r.InCycle));
            // 순환은 최소 대기와 상관없이 보인다
            Assert.Contains("순환(교착) 있음", LockTreeLogic.SummaryText(forest, 3));
        }

        [Fact]
        public void UnknownBlocker_PlaceholderRoot_NoValues_NoAction()
        {
            // 막는 세션의 행이 없다(다른 인스턴스·이미 끝남)
            var forest = LockTreeLogic.Build(Table(Row("1,20", "2,99", "30", null)), 3);
            Assert.Equal(new[] { "● 2,99", "└ 1,20" }, Keys(forest));
            Assert.False(forest.Rows[0].Node.Known);
            var table = LockTreeLogic.ToTable(forest);
            Assert.Equal("● 2,99" + LockTreeLogic.UnknownSuffix, table.Rows[0][0]);
            var kill = DbProviders.Oracle.Monitor.LockTree.Actions[0];
            Assert.Null(LockTreeLogic.ActionValues(forest, forest.Rows[0].Node, kill));
            Assert.Equal(new[] { "1", "20", "720" }, LockTreeLogic.ActionValues(forest, forest.Rows[1].Node, kill));
        }

        [Fact]
        public void MinWait_HidesWholeShortChains_NotSingleRows()
        {
            var table = Table(
                Row("1,1", null, null, "5"),
                Row("1,2", "1,1", "1", null),
                Row("1,3", "1,2", "2", null),
                Row("1,7", null, null, "9"),
                Row("1,8", "1,7", "12", null));
            var forest = LockTreeLogic.Build(table, 3);
            // 1,1 사슬(가장 긴 대기 2초)은 통째로 숨김 — 1,2를 잘라 1,2가 가짜 근원이 되지 않는다
            Assert.Equal(new[] { "● 1,7", "└ 1,8" }, Keys(forest));
            Assert.Equal(1, forest.HiddenChains);
            Assert.Equal("근원 1 · 막힌 세션 1 · 최장 대기 12초 · 3초보다 짧은 사슬 1개 숨김", LockTreeLogic.SummaryText(forest, 3));
            Assert.Equal(5, LockTreeLogic.Build(table, 0).Rows.Count);
            var none = LockTreeLogic.Build(table, 30);
            Assert.Empty(none.Rows);
            Assert.Equal("잠금 대기 없음 · 30초보다 짧은 사슬 2개 숨김", LockTreeLogic.SummaryText(none, 30));
            Assert.Equal("잠금 대기 없음", LockTreeLogic.SummaryText(LockTreeLogic.Build(Table(), 3), 3));
        }

        [Fact]
        public void ContractColumns_CaseInsensitive_DuplicateAndSelfEdgesIgnored()
        {
            // PostgreSQL은 따옴표 없는 별칭을 소문자로 돌려준다
            var table = new QueryTable();
            table.Columns.AddRange(new[] { "session_key", "blocker_key", "wait_seconds", "idle_seconds", "PID" });
            table.Rows.Add(new[] { "20", "10", "6", null, "20" });
            table.Rows.Add(new[] { "20", "10", "6", null, "20" });
            table.Rows.Add(new[] { "10", "10", null, "3", "10" });
            var forest = LockTreeLogic.Build(table, 3);
            Assert.Equal(new[] { "● 10", "└ 20" }, Keys(forest));
            Assert.Equal(new[] { "PID" }, forest.DisplayColumns);
            Assert.Single(forest.Rows[0].Node.Blocked);
        }

        [Fact]
        public void MissingSessionKeyColumn_Throws()
        {
            var table = new QueryTable();
            table.Columns.Add("SID");
            Assert.Throws<InvalidOperationException>(() => LockTreeLogic.Build(table, 0));
            Assert.Throws<ArgumentNullException>(() => LockTreeLogic.Build(null, 0));
        }

        [Fact]
        public void ToTable_TreeColumnsThenDisplay_ContractHidden()
        {
            var forest = LockTreeLogic.Build(Table(
                Row("1,10", null, null, "300", "SCOTT"),
                Row("1,20", "1,10", "40", null)), 0);
            var table = LockTreeLogic.ToTable(forest);
            Assert.Equal(LockTreeLogic.TreeColumns.Concat(new[] { "인스턴스", "SID", "SERIAL#", "사용자" }), table.Columns);
            Assert.DoesNotContain(table.Columns, c => MonitorLockTree.ContractColumns.Contains(c));
            Assert.Equal(new[] { "● 1,10", "1", "40", null, "300", "1", "10", "710", "SCOTT" }, table.Rows[0]);
            Assert.Equal(new[] { "└ 1,20", "0", "0", "40", null, "1", "20", "720", "APP" }, table.Rows[1]);
            var columns = MonitorLogic.Columns(table);
            Assert.False(columns[0].IsNumeric);
            Assert.True(columns[1].IsNumeric);
        }

        [Fact]
        public void DurationAndMinWaitTexts()
        {
            Assert.Equal("0초", LockTreeLogic.DurationText(-3));
            Assert.Equal("42초", LockTreeLogic.DurationText(42.4));
            Assert.Equal("4분 12초", LockTreeLogic.DurationText(252));
            Assert.Equal("1시간 3분", LockTreeLogic.DurationText(3780));
            Assert.Equal("전부", LockTreeLogic.MinWaitText(0));
            Assert.Equal("3초", LockTreeLogic.MinWaitText(3));
            Assert.Contains(LockTreeLogic.DefaultMinWaitSeconds, LockTreeLogic.MinWaitChoices);
            Assert.Equal(3, LockTreeLogic.DefaultMinWaitSeconds);
        }
    }
}

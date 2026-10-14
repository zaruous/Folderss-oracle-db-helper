using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    public class RowEditChangesTests
    {
        private static readonly bool[] AllEditable = { true, true, true };

        [Fact]
        public void FreshRow_HasNoChanges_UntilACellDiffers()
        {
            var values = new[] { "1", "KIM", "2024-03-05" };
            var changes = new RowEditChanges(values, values, AllEditable, false);
            Assert.True(changes.HasOriginal);
            Assert.False(changes.HasChanges);
            Assert.Equal(0, changes.ChangedCount);
            Assert.False(changes.CanRevertAny);
            Assert.Null(changes.ToolTip(1));

            changes.Set(1, "LEE");
            Assert.True(changes.IsChanged(1));
            Assert.False(changes.IsChanged(0));
            Assert.True(changes.HasChanges);
            Assert.Equal("원래 값: KIM", changes.ToolTip(1));

            // 다시 원래 값으로 고치면 바뀐 칸이 아니다
            changes.Set(1, "KIM");
            Assert.False(changes.IsChanged(1));
            Assert.False(changes.HasChanges);
        }

        [Fact]
        public void ReopenedRow_ComparesWithOriginal_NotWithOpenedValues()
        {
            // 이미 고친 행(NAME KIM → LEE)을 다시 연다
            var original = new[] { "1", "KIM", "10" };
            var edited = new[] { "1", "LEE", "10" };
            var changes = new RowEditChanges(original, edited, AllEditable, false);
            Assert.True(changes.IsChanged(1));
            Assert.Equal("원래 값: KIM", changes.ToolTip(1));
            Assert.Equal(1, changes.ChangedCount);
            Assert.True(changes.CanRevert(1));
            // 창에서는 아직 아무것도 바꾸지 않았다(닫기 확인 대상 아님)
            Assert.False(changes.HasChanges);

            // 원래 값으로 다시 고치면 강조는 사라지지만 창을 연 뒤에는 바뀐 것
            changes.Set(1, "KIM");
            Assert.False(changes.IsChanged(1));
            Assert.True(changes.HasChanges);

            // ↶는 열 때 값(LEE)이 아니라 원래 값(KIM)으로
            changes.Set(1, "PARK");
            Assert.Equal("KIM", changes.Revert(1));
            Assert.Equal("KIM", changes.CurrentAt(1));
            Assert.False(changes.CanRevert(1));
        }

        [Fact]
        public void NullAndEmptyString_AreDifferent()
        {
            var original = new[] { "1", null, "" };
            var changes = new RowEditChanges(original, original, AllEditable, false);
            changes.Set(1, "");
            changes.Set(2, null);
            Assert.True(changes.IsChanged(1));
            Assert.True(changes.IsChanged(2));
            Assert.Equal("원래 값: NULL", changes.ToolTip(1));
            Assert.Equal("원래 값: (빈 문자열)", changes.ToolTip(2));

            Assert.Null(changes.Revert(1));
            Assert.Equal("", changes.Revert(2));
            Assert.False(changes.HasChanges);

            // 편집 창 칸 → 값: NULL 체크면 null, 빈 칸은 빈 문자열(새 행만 NULL)
            Assert.Null(RowEditChanges.FieldValue(true, "abc", false));
            Assert.Equal("", RowEditChanges.FieldValue(false, "", false));
            Assert.Null(RowEditChanges.FieldValue(false, "", true));
            Assert.Equal("x", RowEditChanges.FieldValue(false, "x", true));
            Assert.True(RowEditChanges.ShowsAsNull(null, false));
            Assert.False(RowEditChanges.ShowsAsNull(null, true));
            Assert.False(RowEditChanges.ShowsAsNull("", false));
        }

        [Fact]
        public void RevertAll_RestoresEditableCells_AndSkipsReadOnly()
        {
            // 3번째 열은 LOB(읽기 전용)
            var editable = new[] { true, true, false };
            var original = new[] { "1", "KIM", "(CLOB)" };
            var edited = new[] { "2", "LEE", "(CLOB)" };
            var changes = new RowEditChanges(original, edited, editable, false);
            changes.Set(0, "3");
            // 읽기 전용 칸은 값을 넣어도 무시
            changes.Set(2, "hack");
            Assert.Equal("(CLOB)", changes.CurrentAt(2));
            Assert.False(changes.CanRevert(2));
            Assert.Equal(2, changes.ChangedCount);

            Assert.Equal(new[] { 0, 1 }, changes.RevertAll());
            Assert.Equal("1", changes.CurrentAt(0));
            Assert.Equal("KIM", changes.CurrentAt(1));
            Assert.Equal(0, changes.ChangedCount);
            Assert.False(changes.CanRevertAny);
            // 다시 연 행을 모두 되돌렸으므로 창을 연 뒤에는 바뀐 것(취소하면 되돌림을 버림)
            Assert.True(changes.HasChanges);
            Assert.Empty(changes.RevertAll());
        }

        [Fact]
        public void NewRow_UsesOpenedValuesAsBaseline()
        {
            // [복제]: 원래 값이 없다 — 창을 열 때 값이 기준. 빈 문자열은 새 행에서 NULL과 같다(빈 칸 = NULL)
            var copied = new[] { "1", "", "KIM" };
            var changes = new RowEditChanges(null, copied, AllEditable, true);
            Assert.False(changes.HasOriginal);
            Assert.False(changes.HasChanges);
            Assert.Null(changes.BaselineAt(1));
            changes.Set(1, RowEditChanges.FieldValue(false, "", true));
            Assert.False(changes.IsChanged(1));

            changes.Set(0, "2");
            Assert.Equal("처음 값: 1", changes.ToolTip(0));
            Assert.True(changes.HasChanges);
            Assert.Equal(new[] { 0 }, changes.RevertAll());
            Assert.Equal("1", changes.CurrentAt(0));
            Assert.False(changes.HasChanges);

            // 새 행 모드면 original을 넘겨도 무시한다
            Assert.False(new RowEditChanges(copied, copied, AllEditable, true).HasOriginal);
        }

        [Fact]
        public void ShortValues_AndLongToolTip()
        {
            // 값 배열이 열 수보다 짧으면 모자란 칸은 NULL
            var changes = new RowEditChanges(new[] { "1" }, new[] { "1" }, AllEditable, false);
            Assert.Equal(3, changes.Count);
            Assert.Null(changes.BaselineAt(2));
            Assert.False(changes.IsChanged(2));
            Assert.False(changes.IsChanged(99));

            var described = RowEditChanges.Describe(new string('a', 600));
            Assert.Equal(501, described.Length);
            Assert.EndsWith("…", described);
        }
    }
}

using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MyPlugin
{
    /// <summary>
    /// 찾기 팝업(Ctrl+F): SQL 편집기와 결과 그리드에 하나씩. 입력하는 대로 찾고, Enter·F3로 다음, Shift+Enter·Shift+F3로 이전.
    /// 편집기는 찾은 글자를 고르고 그 줄이 보이게 스크롤, 결과는 찾은 칸의 행을 고르고 그 칸이 보이게 세로·가로로 스크롤해 강조한다.
    /// Esc로 닫으면 편집기·찾은 행으로 포커스가 돌아간다.
    /// </summary>
    internal sealed partial class SqlWorkspace
    {
        private FindBar _editorFind;
        private FindBar _resultFind;
        // 결과 찾기 강조를 둔 그리드(다른 결과로 옮겨 찾으면 이전 강조를 지운다)
        private ResultGridView _findGrid;

        private FrameworkElement BuildEditorFind()
        {
            _editorFind = new FindBar("SQL에서 찾기", "SQL에서 찾기 — 대소문자 무시. Enter·F3 다음, Shift+Enter 이전, Esc 닫기");
            _editorFind.Changed += () => Safe(() => FindInEditor(true, true));
            _editorFind.Search += forward => Safe(() => FindInEditor(forward, false));
            _editorFind.Closed += () => Safe(() =>
            {
                var editor = _active != null ? _active.Editor : null;
                if (editor != null)
                    editor.FocusEditor();
            });
            return _editorFind.View;
        }

        private FrameworkElement BuildResultFind()
        {
            _resultFind = new FindBar("결과에서 찾기", "결과에서 찾기 — 가져온 행의 값만, 대소문자 무시. Enter·F3 다음, Shift+Enter 이전, Esc 닫기");
            _resultFind.Changed += () => Safe(() => FindInResult(true, true));
            _resultFind.Search += forward => Safe(() => FindInResult(forward, false));
            _resultFind.Closed += () => Safe(() =>
            {
                var grid = _findGrid;
                _findGrid = null;
                if (grid == null)
                    return;
                grid.FocusFound();
                grid.ClearFound();
            });
            // 결과 영역 어디서든(그리드·찾기 상자) Ctrl+F 열기, F3 다음·이전
            _gridHost.PreviewKeyDown += (s, e) => Safe(() => ResultFindKey(e));
            return _resultFind.View;
        }

        /// <summary>편집기 Ctrl+F: 한 줄 안에서 고른 글이 있으면 그것을 찾는 말로.</summary>
        private void OpenEditorFind()
        {
            var editor = _active != null ? _active.Editor : null;
            if (editor == null)
                return;
            var selected = editor.SelectedText;
            _editorFind.Open(string.IsNullOrEmpty(selected) || selected.IndexOfAny(new[] { '\r', '\n' }) >= 0 ? null : selected);
            FindInEditor(true, true);
        }

        /// <summary>편집기 F3·Shift+F3: 찾는 말이 있을 때만(없으면 찾기를 연다).</summary>
        private void EditorFindAgain(bool forward)
        {
            if (string.IsNullOrEmpty(_editorFind.Term))
                OpenEditorFind();
            else
                FindInEditor(forward, false);
        }

        private void ResultFindKey(KeyEventArgs e)
        {
            var key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
            var modifiers = Keyboard.Modifiers;
            if (key == Key.F && modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                var grid = ActiveResultGrid();
                if (grid == null)
                    return;
                _resultFind.Open(null);
                FindInResult(true, true);
            }
            else if (key == Key.F3 && (modifiers & ~ModifierKeys.Shift) == 0 && !string.IsNullOrEmpty(_resultFind.Term) && ActiveResultGrid() != null)
            {
                e.Handled = true;
                if (!_resultFind.IsOpen)
                    _resultFind.Open(null);
                FindInResult((modifiers & ModifierKeys.Shift) == 0, false);
            }
        }

        private ResultGridView ActiveResultGrid()
        {
            var result = _active != null ? _active.ActiveResult : null;
            return result != null && result.Columns != null ? result.Grid : null;
        }

        /// <summary>
        /// 편집기 찾기. incremental(입력 중)이면 지금 고른 곳에서 시작하는 일치도 포함해 제자리에 머문다.
        /// 찾은 글자를 고르고(포커스는 찾기 상자에 둔 채 — 고른 글은 흐린 선택색으로 보인다) 그 위치가 보이게 스크롤한다.
        /// </summary>
        private void FindInEditor(bool forward, bool incremental)
        {
            var editor = _active != null ? _active.Editor : null;
            if (editor == null)
                return;
            var term = _editorFind.Term;
            var starts = FindLogic.FindAll(editor.Text, term);
            var index = FindLogic.NextIndex(starts, editor.SelectionStart, forward, incremental);
            _editorFind.SetCount(FindLogic.CountText(term, index, starts.Count), term.Length > 0 && starts.Count == 0);
            if (index < 0)
                return;
            editor.Select(starts[index], term.Length);
            editor.ScrollIntoView(starts[index]);
        }

        /// <summary>
        /// 결과 찾기: 지금 결과의 가져온 행(화면 순서 — 정렬했으면 정렬한 순서)에서 값에 찾는 말이 든 칸을 찾는다.
        /// 기준은 지금 찾은 칸, 없으면 고른 행(없으면 처음).
        /// </summary>
        private void FindInResult(bool forward, bool incremental)
        {
            var grid = ActiveResultGrid();
            if (grid != _findGrid && _findGrid != null)
                _findGrid.ClearFound();
            _findGrid = grid;
            var term = _resultFind.Term;
            if (grid == null)
            {
                _resultFind.SetCount("", false);
                return;
            }
            var rows = grid.RowsInViewOrder();
            var matches = FindLogic.FindCells(rows.Select(r => r.Values).ToList(), term);
            var row = 0;
            var column = -1;
            var anchor = grid.FoundRow ?? grid.SelectedRows().FirstOrDefault();
            var position = anchor == null ? -1 : rows.IndexOf(anchor);
            if (position >= 0)
            {
                row = position;
                column = anchor == grid.FoundRow ? anchor.FoundColumn : -1;
            }
            var index = FindLogic.NextCell(matches, row, column, forward, incremental);
            _resultFind.SetCount(FindLogic.CountText(term, index, matches.Count), term.Length > 0 && matches.Count == 0);
            if (index < 0)
            {
                grid.ClearFound();
                return;
            }
            grid.ShowFound(rows[matches[index].Row], matches[index].Column);
        }
    }
}

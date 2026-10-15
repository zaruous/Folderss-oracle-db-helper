using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace MyPlugin
{
    /// <summary>
    /// 결과 행 편집 창(빠른 조회 F1의 [편집]). 열마다 이름(기본 키는 🔑)·형식과 값 칸, NULL 체크. 편집할 수 없는 칸(LOB 등 형식, 잘려 보이는 값)은 읽기 전용.
    /// [확인]은 숫자·날짜 형식을 검사해 틀리면 그 칸에 머문다. 돌려주는 값은 그리드 표시 문자열(null = NULL) — 저장할 때 형식에 맞춰 바인드한다.
    /// 새 행([행 추가]·[복제], insert = true)은 NULL 칸도 바로 입력할 수 있게 비워 두고, 빈 칸은 NULL로 돌려준다(INSERT에서 빠져 DB 기본값).
    /// 변경 관리(RowEditChanges): 원래 값(original, 새 행은 창을 열 때 값)과 다른 칸은 경고색 바탕(Theme.ChangedCell)과 툴팁 "원래 값: …",
    /// 칸 오른쪽 ↶로 그 칸만, [모두 되돌리기]로 전부 되돌린다. 편집할 수 없는 칸에는 ↶를 두지 않는다.
    /// </summary>
    internal static class RowEditDialog
    {
        private const double MaxBodyHeight = 460;

        private sealed class Field
        {
            public int Index;
            public TextBox Box;
            public CheckBox Null;
            public bool Editable;
            public Button Undo;
            public string Hint;
        }

        /// <summary>
        /// 고친 값(열 순서, NULL은 null). 취소면 null. title은 창 제목 앞머리("행 편집"·"행 추가"·"행 복제").
        /// original은 비교 기준인 조회한 원래 값(ResultGridRow.OriginalValues) — 새 행이면 null(창을 열 때 값이 기준).
        /// </summary>
        public static string[] Show(Window owner, OracleConnectionProfile profile, EditableTable table, string[] values, bool insert = false, string title = "행 편집", string[] original = null)
        {
            if (table == null || values == null)
                return null;
            var window = DialogKit.Create(owner, DialogKit.TitleFor(title + " · " + WorkspaceLogic.QualifiedName(table.Owner, table.Name), profile), 620);
            window.ResizeMode = ResizeMode.CanResize;
            window.MinWidth = 480;
            var body = DialogKit.Body(window);
            // 접속 이름은 창 제목(행 편집 · 스키마.테이블 · 접속)에 있으므로 본문에는 설명을 두지 않는다
            if (insert)
                body.Children.Add(DialogKit.Hint("빈 칸·NULL인 열은 INSERT에서 빼어 DB 기본값(자동 번호 포함, 없으면 NULL)이 들어갑니다. 🔑 기본 키 값을 확인하세요.", 0));
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MaxWidth = 220 });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var fields = new List<Field>();
            // 칸을 다 만든 뒤 정한다(칸의 이벤트는 창이 뜬 뒤에만 오므로 그때는 null이 아니다)
            RowEditChanges changes = null;
            Button revertAll = null;
            Action<Field> refresh = field =>
            {
                if (changes == null)
                    return;
                // 바탕만으로는 어두운 테마에서 안 바뀐 칸과 밝기 차가 작아(1.0~1.5) 경고색 테두리도 준다
                if (changes.IsChanged(field.Index))
                {
                    field.Box.Background = Theme.ChangedCell;
                    field.Box.BorderBrush = Theme.Warning;
                }
                else
                {
                    field.Box.ClearValue(Control.BackgroundProperty);
                    field.Box.ClearValue(Control.BorderBrushProperty);
                }
                var tip = changes.ToolTip(field.Index);
                field.Box.ToolTip = tip == null ? field.Hint : field.Hint == null ? tip : tip + Environment.NewLine + field.Hint;
                if (field.Undo != null)
                    field.Undo.Visibility = changes.CanRevert(field.Index) ? Visibility.Visible : Visibility.Hidden;
                revertAll.IsEnabled = changes.CanRevertAny;
            };
            Action<Field> sync = field =>
            {
                if (changes == null)
                    return;
                changes.Set(field.Index, RowEditChanges.FieldValue(field.Null.IsChecked == true, field.Box.Text, insert));
                refresh(field);
            };
            // 되돌린 값을 칸에 다시 채운다(NULL이면 체크, 새 행은 빈 칸)
            Action<Field, string> fill = (field, value) =>
            {
                field.Null.IsChecked = RowEditChanges.ShowsAsNull(value, insert);
                field.Box.Text = value ?? "";
            };
            for (var i = 0; i < table.Columns.Count; i++)
            {
                var column = table.Columns[i];
                var value = i < values.Length ? values[i] : null;
                var kind = table.KindAt(i);
                var blocked = RowEditLogic.EditBlockReason(kind, value);
                var row = grid.RowDefinitions.Count;
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var label = new StackPanel { Margin = new Thickness(0, 6, 10, 0), VerticalAlignment = VerticalAlignment.Top };
                var name = Theme.Text((table.IsKey(i) ? "🔑 " : "") + (column.Name ?? ""));
                name.FontWeight = FontWeights.SemiBold;
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                name.ToolTip = column.Name;
                label.Children.Add(name);
                var type = Theme.Secondary((column.TypeLabel ?? "") + (blocked != null ? " · " + blocked : ""));
                type.FontFamily = Theme.Mono;
                type.FontSize = 10.5;
                type.TextWrapping = TextWrapping.Wrap;
                label.Children.Add(type);
                Grid.SetRow(label, row);
                Grid.SetColumn(label, 0);
                grid.Children.Add(label);

                var multiline = value != null && WorkspaceLogic.HasLineBreak(value);
                var box = new TextBox
                {
                    Text = value ?? "",
                    Margin = new Thickness(0, 4, 0, 0),
                    FontFamily = Theme.Mono,
                    AcceptsReturn = multiline,
                    TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
                    MaxLines = multiline ? 6 : 1,
                    VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    IsReadOnly = blocked != null,
                    IsEnabled = value != null || blocked != null || insert
                };
                if (kind == ColumnValueKind.Number)
                    box.HorizontalContentAlignment = HorizontalAlignment.Right;
                var hint = blocked ?? (kind == ColumnValueKind.DateTime ? "yyyy-MM-dd HH:mm:ss[.fffffff]" : null);
                box.ToolTip = hint;
                AutomationProperties.SetName(box, column.Name ?? "");
                FrameworkElement valueCell = box;
                DateTimeBox picker = null;
                if (kind == ColumnValueKind.DateTime && blocked == null)
                {
                    picker = new DateTimeBox(box);
                    valueCell = picker;
                }
                Grid.SetRow(valueCell, row);
                Grid.SetColumn(valueCell, 1);
                grid.Children.Add(valueCell);

                var isNull = new CheckBox
                {
                    Content = "NULL",
                    IsChecked = value == null && !insert,
                    IsEnabled = blocked == null,
                    Margin = new Thickness(8, 6, 0, 0),
                    VerticalAlignment = VerticalAlignment.Top
                };
                Theme.Foreground(isNull);
                var field = new Field { Index = i, Box = box, Null = isNull, Editable = blocked == null, Hint = hint };
                var captured = box;
                if (picker != null)
                {
                    // 팝업에서 값을 넣으면 NULL이 아니다
                    var capturedNull = isNull;
                    picker.Picked += (s, e) => capturedNull.IsChecked = false;
                }
                isNull.Checked += (s, e) => captured.IsEnabled = false;
                isNull.Unchecked += (s, e) =>
                {
                    captured.IsEnabled = true;
                    captured.Focus();
                };
                Grid.SetRow(isNull, row);
                Grid.SetColumn(isNull, 2);
                grid.Children.Add(isNull);

                if (field.Editable)
                {
                    box.TextChanged += (s, e) => sync(field);
                    isNull.Checked += (s, e) => sync(field);
                    isNull.Unchecked += (s, e) => sync(field);
                    // 읽기 전용 칸(LOB 등)은 바꿀 수 없으므로 ↶를 두지 않는다
                    var undo = ShellUi.SmallButton("↶", (insert ? "처음 값" : "원래 값") + "으로 되돌리기 — " + (column.Name ?? ""));
                    undo.FontSize = 12;
                    undo.Margin = new Thickness(6, 4, 0, 0);
                    undo.VerticalAlignment = VerticalAlignment.Top;
                    undo.Visibility = Visibility.Hidden;
                    undo.Click += (s, e) =>
                    {
                        fill(field, changes.Revert(field.Index));
                        refresh(field);
                    };
                    field.Undo = undo;
                    Grid.SetRow(undo, row);
                    Grid.SetColumn(undo, 3);
                    grid.Children.Add(undo);
                }
                fields.Add(field);
            }
            body.Children.Add(new ScrollViewer
            {
                Content = grid,
                MaxHeight = MaxBodyHeight,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            });

            var error = DialogKit.Warning("", 8);
            error.Visibility = Visibility.Collapsed;
            body.Children.Add(error);

            string[] edited = null;
            var ok = DialogKit.PrimaryButton("확인");
            ok.IsDefault = true;
            var cancel = DialogKit.CancelButton("취소", false);
            ok.Click += (s, e) =>
            {
                var result = (string[])values.Clone();
                if (result.Length < table.Columns.Count)
                    Array.Resize(ref result, table.Columns.Count);
                foreach (var field in fields)
                {
                    if (!field.Editable)
                        continue;
                    var text = TextOf(field, insert);
                    var reason = RowEditLogic.Validate(table.KindAt(field.Index), text);
                    if (reason != null)
                    {
                        error.Text = "⚠ " + (table.Columns[field.Index].Name ?? "") + ": " + reason;
                        error.Visibility = Visibility.Visible;
                        field.Box.Focus();
                        field.Box.SelectAll();
                        return;
                    }
                    result[field.Index] = text;
                }
                edited = result;
                window.DialogResult = true;
            };
            revertAll = DialogKit.PlainButton("모두 되돌리기");
            revertAll.ToolTip = insert ? "바꾼 칸을 모두 창을 열 때 값으로 되돌립니다" : "바꾼 칸을 모두 조회한 원래 값으로 되돌립니다";
            revertAll.Click += (s, e) =>
            {
                foreach (var index in changes.RevertAll())
                    fill(fields.Find(f => f.Index == index), changes.CurrentAt(index));
                fields.ForEach(refresh);
            };
            var editable = new bool[table.Columns.Count];
            foreach (var field in fields)
                editable[field.Index] = field.Editable;
            changes = new RowEditChanges(original ?? values, values, editable, insert);
            fields.ForEach(refresh);

            var footer = new DockPanel { Margin = new Thickness(0, 13, 0, 0), LastChildFill = true };
            DockPanel.SetDock(revertAll, Dock.Left);
            footer.Children.Add(revertAll);
            var buttons = DialogKit.Buttons(ok, cancel);
            buttons.Margin = new Thickness(0);
            footer.Children.Add(buttons);
            body.Children.Add(footer);
            Field first = fields.Find(f => f.Editable && f.Box.IsEnabled);
            DialogKit.FocusOnLoad(window, first != null ? (IInputElement)first.Box : ok);
            // [취소]·Esc·제목 표시줄 X·Alt+F4: 바꾼 칸이 있으면 버릴지 묻는다(기본은 [계속 편집]). [확인]으로 닫을 때는 묻지 않는다
            window.Closing += (s, e) =>
            {
                if (edited == null && changes.HasChanges
                    && !Dialogs.ConfirmDiscardRowEdit(window, profile, title))
                    e.Cancel = true;
            };
            return window.ShowDialog() == true ? edited : null;
        }

        /// <summary>칸이 돌려줄 값(NULL 체크면 null, 새 행의 빈 칸도 null). 검사는 하지 않는다.</summary>
        private static string TextOf(Field field, bool insert)
        {
            return field.Null.IsChecked == true || (insert && field.Box.Text.Length == 0) ? null : field.Box.Text;
        }
    }
}

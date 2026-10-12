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
        }

        /// <summary>고친 값(열 순서, NULL은 null). 취소면 null.</summary>
        public static string[] Show(Window owner, OracleConnectionProfile profile, EditableTable table, string[] values)
        {
            if (table == null || values == null)
                return null;
            var window = DialogKit.Create(owner, DialogKit.TitleFor("행 편집 · " + WorkspaceLogic.QualifiedName(table.Owner, table.Name), profile), 620);
            window.ResizeMode = ResizeMode.CanResize;
            window.MinWidth = 480;
            var body = DialogKit.Body(window);
            // 접속 이름은 창 제목(행 편집 · 스키마.테이블 · 접속)에 있으므로 본문에는 설명을 두지 않는다
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MaxWidth = 220 });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var fields = new List<Field>();
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
                    IsEnabled = value != null || blocked != null
                };
                if (kind == ColumnValueKind.Number)
                    box.HorizontalContentAlignment = HorizontalAlignment.Right;
                box.ToolTip = blocked ?? (kind == ColumnValueKind.DateTime ? "yyyy-MM-dd HH:mm:ss[.fffffff]" : null);
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
                    IsChecked = value == null,
                    IsEnabled = blocked == null,
                    Margin = new Thickness(8, 6, 0, 0),
                    VerticalAlignment = VerticalAlignment.Top
                };
                Theme.Foreground(isNull);
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

                fields.Add(new Field { Index = i, Box = box, Null = isNull, Editable = blocked == null });
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
                    var text = field.Null.IsChecked == true ? null : field.Box.Text;
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
            body.Children.Add(DialogKit.Buttons(ok, cancel));
            Field first = fields.Find(f => f.Editable && f.Box.IsEnabled);
            DialogKit.FocusOnLoad(window, first != null ? (IInputElement)first.Box : ok);
            return window.ShowDialog() == true ? edited : null;
        }
    }
}

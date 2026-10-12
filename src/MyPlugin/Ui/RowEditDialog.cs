using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

        /// <summary>
        /// 날짜 칸 옆 📅 버튼: 달력 팝업에서 고른 날짜를 칸의 날짜 부분에만 넣는다(시각·소수점 초는 그대로). NULL 체크가 돼 있으면 푼다.
        /// WPF 기본 Calendar는 템플릿에 색이 고정돼 테마를 입힐 수 없어, 테마 Button·글자색으로 달력을 직접 그린다(일요일부터 6주).
        /// </summary>
        private static FrameworkElement WithCalendar(TextBox box)
        {
            var panel = new DockPanel();
            var pick = new Button
            {
                Content = "📅",
                Padding = new Thickness(6, 0, 6, 0),
                Margin = new Thickness(4, 4, 0, 0),
                ToolTip = "달력에서 날짜 고르기(시각은 그대로)",
                Focusable = false
            };
            AutomationProperties.SetName(pick, "달력");
            DockPanel.SetDock(pick, Dock.Right);
            var popup = new Popup { PlacementTarget = pick, Placement = PlacementMode.Bottom, StaysOpen = false };
            pick.Click += (s, e) =>
            {
                var initial = RowEditLogic.DatePartOf(box.Text);
                popup.Child = MonthPicker(initial ?? DateTime.Today, initial, date =>
                {
                    var isNull = box.Tag as CheckBox;
                    if (isNull != null && isNull.IsChecked == true)
                        isNull.IsChecked = false;
                    box.Text = RowEditLogic.ReplaceDatePart(box.Text, date);
                    popup.IsOpen = false;
                    box.Focus();
                    box.SelectAll();
                });
                popup.IsOpen = true;
            };
            panel.Children.Add(pick);
            // 팝업을 논리 트리에 두어 테마 리소스(DynamicResource)를 창에서 물려받는다
            panel.Children.Add(popup);
            panel.Children.Add(box);
            return panel;
        }

        /// <summary>달 하나: ◀ yyyy년 M월 ▶, 요일, 날짜 버튼 42개(다른 달은 흐리게, 오늘은 강조 테두리, 고른 날은 선택색).</summary>
        private static FrameworkElement MonthPicker(DateTime month, DateTime? selected, Action<DateTime> pick)
        {
            var root = new StackPanel { Margin = new Thickness(6) };
            var border = new Border { Child = root, BorderThickness = new Thickness(1), SnapsToDevicePixels = true };
            Theme.Background(border, Theme.SurfaceBackground);
            border.SetResourceReference(Border.BorderBrushProperty, Theme.Border);

            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var previous = NavButton("◀");
            var next = NavButton("▶");
            var title = Theme.Text("");
            title.FontWeight = FontWeights.SemiBold;
            title.HorizontalAlignment = HorizontalAlignment.Center;
            title.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(previous, Dock.Left);
            DockPanel.SetDock(next, Dock.Right);
            header.Children.Add(previous);
            header.Children.Add(next);
            header.Children.Add(title);
            root.Children.Add(header);

            var days = new UniformGrid { Columns = 7 };
            foreach (var name in new[] { "일", "월", "화", "수", "목", "금", "토" })
            {
                var label = Theme.Secondary(name);
                label.HorizontalAlignment = HorizontalAlignment.Center;
                label.Margin = new Thickness(0, 0, 0, 2);
                days.Children.Add(label);
            }
            root.Children.Add(days);

            var shown = new DateTime(month.Year, month.Month, 1);
            Action render = null;
            render = () =>
            {
                title.Text = shown.ToString("yyyy년 M월", CultureInfo.InvariantCulture);
                while (days.Children.Count > 7)
                    days.Children.RemoveAt(days.Children.Count - 1);
                foreach (var day in RowEditLogic.MonthGrid(shown))
                {
                    var date = day;
                    var button = new Button
                    {
                        Content = date.Day.ToString(CultureInfo.InvariantCulture),
                        Padding = new Thickness(0),
                        Margin = new Thickness(1),
                        MinWidth = 30,
                        Height = 26,
                        FontSize = 12,
                        Focusable = false
                    };
                    if (date.Month != shown.Month)
                        Theme.Foreground(button, Theme.SecondaryText);
                    if (selected.HasValue && date == selected.Value.Date)
                        button.SetResourceReference(Control.BackgroundProperty, Theme.Selection);
                    if (date == DateTime.Today)
                    {
                        button.SetResourceReference(Control.BorderBrushProperty, Theme.Accent);
                        button.ToolTip = "오늘";
                    }
                    button.Click += (s, e) => pick(date);
                    days.Children.Add(button);
                }
            };
            previous.Click += (s, e) =>
            {
                shown = shown.AddMonths(-1);
                render();
            };
            next.Click += (s, e) =>
            {
                shown = shown.AddMonths(1);
                render();
            };
            render();
            return border;
        }

        private static Button NavButton(string text)
        {
            return new Button { Content = text, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(1), MinWidth = 26, Height = 24, FontSize = 11, Focusable = false };
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
                if (kind == ColumnValueKind.DateTime && blocked == null)
                    valueCell = WithCalendar(box);
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
                box.Tag = isNull;
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

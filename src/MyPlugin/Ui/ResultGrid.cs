using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace MyPlugin
{
    /// <summary>
    /// 결과 그리드 한 행. 값은 ValueFormatter.Format 결과(null = NULL).
    /// 빠른 조회(F1) 결과는 편집할 수 있다: 고친 값은 Values를 통째로 바꾸고(칸은 Values에 바인딩) 원래 값은 Original에 남긴다(저장의 WHERE·되돌리기).
    /// </summary>
    internal sealed class ResultGridRow : INotifyPropertyChanged
    {
        private string[] _values;
        private RowEditState _state;
        private int _foundColumn = -1;

        public ResultGridRow(int number, string[] values)
        {
            Number = number;
            _values = values ?? Array.Empty<string>();
        }

        /// <summary>1부터 세는 행 번호.</summary>
        public int Number { get; }

        /// <summary>지금 값(고쳤으면 고친 값).</summary>
        public string[] Values
        {
            get { return _values; }
            private set
            {
                _values = value ?? Array.Empty<string>();
                Raise(nameof(Values));
            }
        }

        /// <summary>조회한 원래 값. 고친 적이 없으면 Values와 같은 배열.</summary>
        public string[] Original { get; private set; }

        public RowEditState State
        {
            get { return _state; }
            private set
            {
                if (_state == value)
                    return;
                _state = value;
                Raise(nameof(State));
            }
        }

        /// <summary>고친 값을 넣는다. 원래 값과 같아지면 변경 없음으로 돌아간다(삭제 표시는 그대로).</summary>
        public void Apply(string[] values)
        {
            if (Original == null)
                Original = _values;
            Values = values;
            if (State != RowEditState.Deleted)
                State = RowEditLogic.SameValues(Original, _values) ? RowEditState.Unchanged : RowEditState.Modified;
        }

        /// <summary>삭제 표시를 하거나(true) 푼다(false). 풀면 고친 값이 남아 있으면 수정 상태로.</summary>
        public void MarkDeleted(bool deleted)
        {
            if (deleted)
                State = RowEditState.Deleted;
            else
                State = Original != null && !RowEditLogic.SameValues(Original, _values) ? RowEditState.Modified : RowEditState.Unchanged;
        }

        /// <summary>찾기(Ctrl+F)로 지금 가리키는 칸의 결과 열 위치. 없으면 -1(그 칸을 강조색 바탕으로 보인다).</summary>
        public int FoundColumn
        {
            get { return _foundColumn; }
            set
            {
                if (_foundColumn == value)
                    return;
                _foundColumn = value;
                Raise(nameof(FoundColumn));
            }
        }

        /// <summary>DB에 보낸 뒤: 지금 값이 원래 값이 된다.</summary>
        public void Saved()
        {
            Original = null;
            State = RowEditState.Unchanged;
        }

        /// <summary>WHERE에 쓸 원래 값(고친 적 없으면 지금 값).</summary>
        public string[] OriginalValues
        {
            get { return Original ?? _values; }
        }

        // INotifyPropertyChanged를 구현해 두면 WPF가 PropertyDescriptor 변경 구독(행이 해제되지 않는 누수)을 쓰지 않는다.
        public event PropertyChangedEventHandler PropertyChanged;

        private void Raise(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>
    /// 조회 결과 그리드(ListView + GridView, 가상화). 열 머리는 이름(굵게) 위·형식(작은 고정폭) 아래,
    /// NULL은 흐린 기울임꼴, 숫자 열은 오른쪽 정렬, 긴 값은 한 줄로 줄이고 툴팁으로 전체를 보인다.
    /// Ctrl+C는 선택한 행, 오른쪽 메뉴로 전체 복사. 복사(클립보드·메시지)는 만든 쪽이 copy 콜백으로 한다.
    /// 열 머리를 누르면 가져온 행을 정렬한다(오름차순 → 내림차순 → 해제, # 열은 해제). 행 번호는 원래 순서 그대로 보이고,
    /// 더 가져온 행도 정렬 위치에 들어간다(ListCollectionView.CustomSort). 복사는 화면 순서를 따른다.
    /// </summary>
    internal sealed class ResultGridView
    {
        private const int SampleRows = 50;
        private const double EditMarkerWidth = 16;
        private const string SortHint = "누르면 정렬: 오름차순 → 내림차순 → 해제 (가져온 행 안에서)";

        private readonly ListView _list;
        private readonly Action<bool> _copy;
        private readonly List<TextBlock> _titles = new List<TextBlock>();
        private readonly List<string> _names = new List<string>();
        private IList<ResultColumn> _columns;
        private ListCollectionView _rowsView;
        private GridViewColumn _numberColumn;
        private Style _headerStyle;
        private bool _stylesApplied;
        private int _sortColumn = -1;
        private bool _sortDescending;
        private bool _editMarkers;
        private ResultGridRow _found;

        /// <param name="copy">복사 요청. true = 전체(머리글 포함), false = 선택한 행.</param>
        public ResultGridView(Action<bool> copy)
        {
            _copy = copy;
            _list = new ListView
            {
                SelectionMode = SelectionMode.Extended,
                BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            Theme.Background(_list, Theme.PanelBackground);
            VirtualizingPanel.SetIsVirtualizing(_list, true);
            VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
            ScrollViewer.SetCanContentScroll(_list, true);
            ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Auto);
            ScrollViewer.SetVerticalScrollBarVisibility(_list, ScrollBarVisibility.Auto);
            AutomationProperties.SetName(_list, "조회 결과");

            _list.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy,
                (s, e) =>
                {
                    e.Handled = true;
                    _copy(false);
                },
                (s, e) =>
                {
                    e.CanExecute = _list.SelectedItems.Count > 0;
                    e.Handled = true;
                }));

            var menu = new ContextMenu();
            var copySelected = new MenuItem { Header = "선택한 행 복사", InputGestureText = "Ctrl+C" };
            copySelected.Click += (s, e) => _copy(false);
            var copyAll = new MenuItem { Header = "모두 복사 (머리글 포함)" };
            copyAll.Click += (s, e) => _copy(true);
            menu.Items.Add(copySelected);
            menu.Items.Add(copyAll);
            menu.Opened += (s, e) =>
            {
                copySelected.IsEnabled = _list.SelectedItems.Count > 0;
                copyAll.IsEnabled = _list.Items.Count > 0;
            };
            _list.ContextMenu = menu;
            _list.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(Header_Click));
        }

        public ListView View
        {
            get { return _list; }
        }

        /// <summary>정렬한 열(결과 열 위치). 정렬 안 했으면 -1.</summary>
        public int SortColumn
        {
            get { return _sortColumn; }
        }

        public bool SortDescending
        {
            get { return _sortDescending; }
        }

        /// <summary>열 머리를 눌러 정렬을 바꿨을 때.</summary>
        public event EventHandler Sorted;

        /// <summary>새 결과로 바꾼다. 이 그리드는 화면 트리에 붙어 있어야 한다(테마 스타일을 찾으려고).</summary>
        public void Show(IList<ResultColumn> columns, ObservableCollection<ResultGridRow> rows)
        {
            ApplyThemeStyles();
            _editMarkers = false;
            _found = null;
            // 열을 끌어 옮기면 복사(TSV)의 열 순서와 화면이 달라진다
            var view = new GridView { AllowsColumnReorder = false };
            if (_headerStyle != null)
                view.ColumnHeaderContainerStyle = _headerStyle;
            // 새 결과는 정렬하지 않은 원래 순서로 보인다
            _sortColumn = -1;
            _sortDescending = false;
            _columns = columns;
            _titles.Clear();
            _names.Clear();
            TextBlock numberTitle;
            var numberHeader = HeaderText("#", null, out numberTitle);
            numberHeader.ToolTip = "원래 순서(행 번호). 누르면 정렬을 해제합니다.";
            _numberColumn = new GridViewColumn
            {
                Header = numberHeader,
                CellTemplate = NumberTemplate(),
                Width = NumberWidth(rows.Count)
            };
            view.Columns.Add(_numberColumn);
            for (var i = 0; i < columns.Count; i++)
            {
                var index = i;
                var column = columns[i];
                var sample = rows.Take(SampleRows).Select(r => index < r.Values.Length ? r.Values[index] : null);
                TextBlock title;
                var header = HeaderText(column.Name, column.TypeLabel, out title);
                _titles.Add(title);
                _names.Add(column.Name ?? "");
                view.Columns.Add(new GridViewColumn
                {
                    Header = header,
                    CellTemplate = CellTemplate(index, column.IsNumeric),
                    Width = WorkspaceLogic.ColumnWidth(column.Name, column.TypeLabel, sample)
                });
            }
            _list.View = view;
            _list.ItemsSource = rows;
            _rowsView = CollectionViewSource.GetDefaultView(rows) as ListCollectionView;
            if (_rowsView != null)
                _rowsView.CustomSort = null;
            // 같은 탭에서 다시 조회하면 이전 결과의 스크롤 위치에 머물지 않게 한다
            var scroller = WorkspaceUi.FindDescendant<ScrollViewer>(_list);
            if (scroller != null)
                scroller.ScrollToHome();
        }

        /// <summary>행이 늘어 번호 자릿수가 커지면 번호 열을 넓힌다.</summary>
        public void FitRowNumbers(int rowCount)
        {
            if (_numberColumn == null)
                return;
            var width = NumberWidth(rowCount);
            if (double.IsNaN(_numberColumn.Width) || width > _numberColumn.Width)
                _numberColumn.Width = width;
        }

        /// <summary>편집할 수 있는 결과: 번호 열에 수정(✎)·삭제(✖) 표시 자리를 둔다.</summary>
        public void EnableEditMarkers(int rowCount)
        {
            _editMarkers = true;
            FitRowNumbers(rowCount);
        }

        private double NumberWidth(int rowCount)
        {
            return WorkspaceLogic.RowNumberWidth(rowCount) + (_editMarkers ? EditMarkerWidth : 0);
        }

        /// <summary>찾기로 가리키는 행(없으면 null).</summary>
        public ResultGridRow FoundRow
        {
            get { return _found; }
        }

        /// <summary>
        /// 찾기: 그 행을 고르고(하나만) 보이게 세로로 스크롤한 뒤, 그 칸이 보이게 가로로 스크롤하고 칸을 강조한다.
        /// 키보드 포커스는 옮기지 않는다(찾기 상자에서 Enter로 계속 찾게) — 닫을 때 <see cref="FocusFound"/>.
        /// </summary>
        public void ShowFound(ResultGridRow row, int column)
        {
            if (row == null)
                return;
            if (_found != null && _found != row)
                _found.FoundColumn = -1;
            _found = row;
            row.FoundColumn = column;
            _list.SelectedItem = row;
            _list.ScrollIntoView(row);
            var view = _list.View as GridView;
            var scroller = WorkspaceUi.FindDescendant<ScrollViewer>(_list);
            if (view == null || scroller == null || column < 0 || column + 1 >= view.Columns.Count)
                return;
            // 가로 스크롤은 픽셀 단위다(가상화는 세로 행 단위만): # 열부터 그 칸 앞까지의 너비가 칸의 왼쪽
            var left = 0.0;
            for (var i = 0; i <= column; i++)
                left += view.Columns[i].ActualWidth;
            var right = left + view.Columns[column + 1].ActualWidth;
            if (left < scroller.HorizontalOffset)
                scroller.ScrollToHorizontalOffset(left);
            else if (right > scroller.HorizontalOffset + scroller.ViewportWidth)
                scroller.ScrollToHorizontalOffset(Math.Min(left, right - scroller.ViewportWidth));
        }

        /// <summary>찾기 강조를 지운다.</summary>
        public void ClearFound()
        {
            if (_found != null)
                _found.FoundColumn = -1;
            _found = null;
        }

        /// <summary>찾기를 닫을 때: 찾은 행에 키보드 포커스(↑↓·Ctrl+C로 이어 쓰게). 없으면 그리드에.</summary>
        public void FocusFound()
        {
            var item = _found == null ? null : _list.ItemContainerGenerator.ContainerFromItem(_found) as ListViewItem;
            if (item != null)
                item.Focus();
            else
                _list.Focus();
        }

        public void ScrollIntoView(ResultGridRow row)
        {
            if (row != null)
                _list.ScrollIntoView(row);
        }

        /// <summary>선택한 행(화면 순서 — 정렬했으면 정렬한 순서).</summary>
        public List<ResultGridRow> SelectedRows()
        {
            var selected = _list.SelectedItems.OfType<ResultGridRow>().ToList();
            if (_sortColumn < 0 || _rowsView == null)
                return selected.OrderBy(r => r.Number).ToList();
            var order = ViewOrder();
            return selected.OrderBy(r => order.TryGetValue(r, out var position) ? position : int.MaxValue).ToList();
        }

        /// <summary>모든 행(화면 순서 — 정렬했으면 정렬한 순서).</summary>
        public List<ResultGridRow> RowsInViewOrder()
        {
            if (_rowsView == null)
                return _list.Items.OfType<ResultGridRow>().ToList();
            return _rowsView.OfType<ResultGridRow>().ToList();
        }

        private Dictionary<ResultGridRow, int> ViewOrder()
        {
            var order = new Dictionary<ResultGridRow, int>();
            var position = 0;
            foreach (var row in _rowsView.OfType<ResultGridRow>())
                order[row] = position++;
            return order;
        }

        private void Header_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var header = e.OriginalSource as GridViewColumnHeader;
                var view = _list.View as GridView;
                if (header == null || header.Column == null || header.Role == GridViewColumnHeaderRole.Padding || view == null || _rowsView == null)
                    return;
                e.Handled = true;
                var clicked = view.Columns.IndexOf(header.Column) - 1;
                if (clicked < -1)
                    return;
                int column;
                bool descending;
                if (clicked < 0)
                {
                    // # 열: 원래 순서로
                    if (_sortColumn < 0)
                        return;
                    column = -1;
                    descending = false;
                }
                else
                {
                    WorkspaceLogic.NextSort(_sortColumn, _sortDescending, clicked, out column, out descending);
                }
                _sortColumn = column;
                _sortDescending = descending;
                ApplySort();
                Sorted?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception)
            {
                // 정렬하지 못해도 결과는 그대로 보인다
            }
        }

        private void ApplySort()
        {
            var numeric = _sortColumn >= 0 && _columns != null && _sortColumn < _columns.Count && _columns[_sortColumn].IsNumeric;
            _rowsView.CustomSort = _sortColumn < 0 ? null : new RowComparer(_sortColumn, numeric, _sortDescending);
            for (var i = 0; i < _titles.Count; i++)
                _titles[i].Text = _names[i] + (i == _sortColumn ? WorkspaceLogic.SortMark(_sortDescending) : "");
            // 정렬한 결과의 처음을 보인다
            var scroller = WorkspaceUi.FindDescendant<ScrollViewer>(_list);
            if (scroller != null)
                scroller.ScrollToVerticalOffset(0);
        }

        private sealed class RowComparer : System.Collections.IComparer
        {
            private readonly int _column;
            private readonly bool _numeric;
            private readonly bool _descending;

            public RowComparer(int column, bool numeric, bool descending)
            {
                _column = column;
                _numeric = numeric;
                _descending = descending;
            }

            public int Compare(object x, object y)
            {
                var a = x as ResultGridRow;
                var b = y as ResultGridRow;
                if (a == null || b == null)
                    return a == null ? (b == null ? 0 : 1) : -1;
                return WorkspaceLogic.CompareRows(a.Values, a.Number, b.Values, b.Number, _column, _numeric, _descending);
            }
        }

        private void ApplyThemeStyles()
        {
            if (_stylesApplied)
                return;
            _stylesApplied = true;
            var itemStyle = WorkspaceUi.StretchItemStyle(_list, typeof(ListViewItem)) ?? new Style(typeof(ListViewItem));
            // 수정·삭제 표시한 행은 바탕색으로 드러낸다. 고른 행은 테마 선택색이 앞선다(뒤에 둔 트리거가 이긴다).
            var modified = new DataTrigger { Binding = new Binding(nameof(ResultGridRow.State)), Value = RowEditState.Modified };
            modified.Setters.Add(new Setter(Control.BackgroundProperty, Theme.ModifiedRow));
            var deleted = new DataTrigger { Binding = new Binding(nameof(ResultGridRow.State)), Value = RowEditState.Deleted };
            deleted.Setters.Add(new Setter(Control.BackgroundProperty, Theme.DeletedRow));
            var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
            selected.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(Theme.Selection)));
            itemStyle.Triggers.Add(modified);
            itemStyle.Triggers.Add(deleted);
            itemStyle.Triggers.Add(selected);
            _list.ItemContainerStyle = itemStyle;
            var baseHeader = _list.TryFindResource(typeof(GridViewColumnHeader)) as Style;
            if (baseHeader != null)
            {
                var header = new Style(typeof(GridViewColumnHeader), baseHeader);
                header.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
                _headerStyle = header;
            }
        }

        private static FrameworkElement HeaderText(string name, string typeLabel, out TextBlock title)
        {
            var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 1, 2, 1) };
            title = Theme.Text(name ?? "");
            title.FontWeight = FontWeights.SemiBold;
            panel.Children.Add(title);
            if (!string.IsNullOrEmpty(typeLabel))
            {
                var type = Theme.Text(typeLabel, Theme.SecondaryText);
                type.FontFamily = Theme.Mono;
                type.FontSize = 10.5;
                panel.Children.Add(type);
                panel.ToolTip = (name ?? "") + " · " + typeLabel + Environment.NewLine + SortHint;
            }
            else
            {
                panel.ToolTip = SortHint;
            }
            return panel;
        }

        private static DataTemplate NumberTemplate()
        {
            var panel = new FrameworkElementFactory(typeof(DockPanel));
            var marker = new FrameworkElementFactory(typeof(TextBlock), "marker");
            marker.SetBinding(TextBlock.TextProperty, new Binding(nameof(ResultGridRow.State)) { Converter = EditMarkerConverter.Instance, Mode = BindingMode.OneWay });
            marker.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(ResultGridRow.State)) { Converter = EditMarkerTipConverter.Instance, Mode = BindingMode.OneWay });
            marker.SetValue(DockPanel.DockProperty, Dock.Left);
            marker.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
            marker.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 3, 0));
            panel.AppendChild(marker);
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty, new Binding(nameof(ResultGridRow.Number)) { Mode = BindingMode.OneTime, StringFormat = "{0:N0}" });
            text.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Right);
            text.SetResourceReference(TextBlock.ForegroundProperty, Theme.SecondaryText);
            panel.AppendChild(text);
            var template = new DataTemplate { VisualTree = panel };
            var modified = new DataTrigger { Binding = new Binding(nameof(ResultGridRow.State)), Value = RowEditState.Modified };
            modified.Setters.Add(new Setter(TextBlock.ForegroundProperty, Theme.Warning, "marker"));
            var deleted = new DataTrigger { Binding = new Binding(nameof(ResultGridRow.State)), Value = RowEditState.Deleted };
            deleted.Setters.Add(new Setter(TextBlock.ForegroundProperty, Theme.Danger, "marker"));
            template.Triggers.Add(modified);
            template.Triggers.Add(deleted);
            template.Seal();
            return template;
        }

        private static DataTemplate CellTemplate(int index, bool numeric)
        {
            var text = new FrameworkElementFactory(typeof(TextBlock), "cell");
            // 행 객체가 아니라 Values에 바인딩한다: 편집으로 Values가 바뀌면 칸이 따라 바뀐다
            text.SetBinding(TextBlock.TextProperty, new Binding(nameof(ResultGridRow.Values)) { Converter = CellTextConverter.Instance, ConverterParameter = index, Mode = BindingMode.OneWay });
            text.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(ResultGridRow.Values)) { Converter = CellTooltipConverter.Instance, ConverterParameter = index, Mode = BindingMode.OneWay });
            text.SetValue(FrameworkElement.TagProperty, index);
            text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap);
            if (numeric)
                text.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Right);
            text.AddHandler(FrameworkElement.ToolTipOpeningEvent, new ToolTipEventHandler(OnCellToolTipOpening));

            var template = new DataTemplate { VisualTree = text };
            var isNull = new DataTrigger
            {
                Binding = new Binding(nameof(ResultGridRow.Values)) { Converter = CellIsNullConverter.Instance, ConverterParameter = index, Mode = BindingMode.OneWay },
                Value = true
            };
            isNull.Setters.Add(new Setter(TextBlock.FontStyleProperty, FontStyles.Italic, "cell"));
            isNull.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension(Theme.DisabledText), "cell"));
            template.Triggers.Add(isNull);
            var deleted = new DataTrigger { Binding = new Binding(nameof(ResultGridRow.State)), Value = RowEditState.Deleted };
            deleted.Setters.Add(new Setter(TextBlock.TextDecorationsProperty, TextDecorations.Strikethrough, "cell"));
            template.Triggers.Add(deleted);
            var found = new DataTrigger { Binding = new Binding(nameof(ResultGridRow.FoundColumn)), Value = index };
            found.Setters.Add(new Setter(TextBlock.BackgroundProperty, ShellUi.HighlightTint, "cell"));
            template.Triggers.Add(found);
            template.Seal();
            return template;
        }

        // 잘리지 않은 한 줄 값에는 툴팁을 띄우지 않는다(숫자·짧은 글자 위에서 툴팁이 계속 뜨지 않게)
        private static void OnCellToolTipOpening(object sender, ToolTipEventArgs e)
        {
            try
            {
                var text = sender as TextBlock;
                var value = text == null ? null : CellValue(text.DataContext, text.Tag);
                if (value == null)
                {
                    e.Handled = true;
                    return;
                }
                // 줄바꿈이 있는 값은 칸에 한 줄로 바꿔 보이므로 언제나 원래 값을 보인다
                if (!WorkspaceLogic.HasLineBreak(value) && !IsTrimmed(text))
                    e.Handled = true;
            }
            catch (Exception)
            {
                // 툴팁이 필요한지 판단하지 못하면 띄우지 않는다
                e.Handled = true;
            }
        }

        private static bool IsTrimmed(TextBlock text)
        {
            var formatted = new FormattedText(text.Text ?? "", CultureInfo.CurrentUICulture, text.FlowDirection,
                new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, text.Foreground,
                VisualTreeHelper.GetDpi(text).PixelsPerDip);
            return formatted.WidthIncludingTrailingWhitespace > text.ActualWidth - text.Padding.Left - text.Padding.Right + 0.5;
        }

        private static string CellValue(object row, object parameter)
        {
            var values = row as string[] ?? (row as ResultGridRow)?.Values;
            if (values == null || !(parameter is int))
                return null;
            var index = (int)parameter;
            return index >= 0 && index < values.Length ? values[index] : null;
        }

        private sealed class CellTextConverter : IValueConverter
        {
            public static readonly CellTextConverter Instance = new CellTextConverter();

            public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            {
                // 컨테이너를 재사용하는 동안 DataContext가 행이 아닐 수 있다(그때는 NULL로 보이지 않게 빈 칸)
                if (!(value is string[]) && !(value is ResultGridRow))
                    return "";
                return WorkspaceLogic.CellText(CellValue(value, parameter));
            }

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            {
                return Binding.DoNothing;
            }
        }

        private sealed class CellIsNullConverter : IValueConverter
        {
            public static readonly CellIsNullConverter Instance = new CellIsNullConverter();

            public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            {
                return (value is string[] || value is ResultGridRow) && CellValue(value, parameter) == null;
            }

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            {
                return Binding.DoNothing;
            }
        }

        private sealed class EditMarkerConverter : IValueConverter
        {
            public static readonly EditMarkerConverter Instance = new EditMarkerConverter();

            public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            {
                if (value is RowEditState)
                {
                    switch ((RowEditState)value)
                    {
                        case RowEditState.Modified: return "✎";
                        case RowEditState.Deleted: return "✖";
                    }
                }
                return "";
            }

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            {
                return Binding.DoNothing;
            }
        }

        private sealed class EditMarkerTipConverter : IValueConverter
        {
            public static readonly EditMarkerTipConverter Instance = new EditMarkerTipConverter();

            public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            {
                if (value is RowEditState)
                {
                    switch ((RowEditState)value)
                    {
                        case RowEditState.Modified: return "고친 행 — [저장]하면 UPDATE";
                        case RowEditState.Deleted: return "삭제 표시한 행 — [저장]하면 DELETE";
                    }
                }
                return null;
            }

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            {
                return Binding.DoNothing;
            }
        }

        private sealed class CellTooltipConverter : IValueConverter
        {
            public static readonly CellTooltipConverter Instance = new CellTooltipConverter();

            public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            {
                return WorkspaceLogic.TooltipText(CellValue(value, parameter));
            }

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            {
                return Binding.DoNothing;
            }
        }
    }

    /// <summary>클립보드 쓰기. 다른 프로그램이 클립보드를 잡고 있으면 COMException이 나므로 잠시 뒤 한 번 더 한다.</summary>
    internal static class ResultClipboard
    {
        /// <summary>성공하면 null, 실패하면 사람이 읽을 이유. UI 스레드(STA)에서 호출한다.</summary>
        public static async Task<string> SetTextAsync(string text)
        {
            try
            {
                Clipboard.SetText(text ?? "");
                return null;
            }
            catch (COMException)
            {
                // 다른 프로그램이 클립보드를 열고 있음 — 잠깐 기다렸다 한 번 더
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
            await Task.Delay(150);
            try
            {
                Clipboard.SetText(text ?? "");
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }
    }
}

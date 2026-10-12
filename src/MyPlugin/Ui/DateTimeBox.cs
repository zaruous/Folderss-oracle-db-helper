using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace MyPlugin
{
    /// <summary>
    /// 이 플러그인 전용 날짜·시각 입력 컨트롤: 글 칸(그리드 표시 형식 "yyyy-MM-dd HH:mm:ss[.fffffff]", 값의 원본) + 📅 버튼 + 팝업.
    /// 팝업은 달력(◀◀ 1년 · ◀ 1달 · 오늘 강조 · 고른 날 선택색), 시각 칸(HH:mm:ss[.fff]), [오늘]·[지금]·[확인].
    /// WPF 기본 Calendar·DatePicker는 템플릿에 색이 고정돼 Folderss 테마를 입힐 수 없어 테마 Button·글자색·선택색으로 직접 그린다.
    /// 값 검사는 하지 않는다(편집 창이 [확인] 때 RowEditLogic.Validate로 한다). 팝업에서 값을 넣으면 Picked를 올린다.
    /// </summary>
    internal sealed class DateTimeBox : DockPanel
    {
        private readonly Button _pick;
        private readonly Popup _popup;
        private DateTime _shown;
        private DateTime? _selected;
        private TextBlock _title;
        private UniformGrid _days;
        private TextBox _time;
        private TextBlock _error;

        /// <param name="box">값을 담는 글 칸(편집 창이 만들어 넘긴다 — 글꼴·읽기 전용·NULL 연동은 그쪽 몫).</param>
        public DateTimeBox(TextBox box)
        {
            if (box == null)
                throw new ArgumentNullException(nameof(box));
            Box = box;
            _pick = new Button
            {
                Content = "📅",
                Padding = new Thickness(6, 0, 6, 0),
                Margin = new Thickness(4, 4, 0, 0),
                ToolTip = "달력·시각에서 고르기",
                Focusable = false
            };
            AutomationProperties.SetName(_pick, "날짜·시각 고르기");
            SetDock(_pick, Dock.Right);
            _popup = new Popup { PlacementTarget = _pick, Placement = PlacementMode.Bottom, StaysOpen = false };
            _pick.Click += (s, e) => Open();
            Box.PreviewKeyDown += (s, e) =>
            {
                // Alt+↓: 키보드로도 팝업을 연다
                if (e.Key == Key.Down && (Keyboard.Modifiers & ModifierKeys.Alt) != 0 && Box.IsEnabled && !Box.IsReadOnly)
                {
                    Open();
                    e.Handled = true;
                }
            };
            Children.Add(_pick);
            // 팝업을 논리 트리에 두어 테마 리소스(DynamicResource)를 창에서 물려받는다
            Children.Add(_popup);
            Children.Add(Box);
        }

        /// <summary>값을 담는 글 칸.</summary>
        public TextBox Box { get; }

        /// <summary>팝업에서 값을 넣었음(편집 창은 NULL 체크를 푼다).</summary>
        public event EventHandler Picked;

        /// <summary>팝업을 연다: 칸의 글에서 날짜·시각을 읽어 시작 상태로 쓴다.</summary>
        public void Open()
        {
            _selected = RowEditLogic.DatePartOf(Box.Text);
            _shown = _selected ?? DateTime.Today;
            _popup.Child = BuildPicker();
            _popup.IsOpen = true;
            _time.Focus();
            _time.SelectAll();
        }

        private FrameworkElement BuildPicker()
        {
            var root = new StackPanel { Margin = new Thickness(8) };
            var border = new Border { Child = root, BorderThickness = new Thickness(1), SnapsToDevicePixels = true };
            Theme.Background(border, Theme.SurfaceBackground);
            border.SetResourceReference(Border.BorderBrushProperty, Theme.Border);

            // ◀◀ ◀ yyyy년 M월 ▶ ▶▶
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var previousYear = NavButton("◀◀", "1년 전");
            var previousMonth = NavButton("◀", "1달 전");
            var nextMonth = NavButton("▶", "1달 뒤");
            var nextYear = NavButton("▶▶", "1년 뒤");
            _title = Theme.Text("");
            _title.FontWeight = FontWeights.SemiBold;
            _title.HorizontalAlignment = HorizontalAlignment.Center;
            _title.VerticalAlignment = VerticalAlignment.Center;
            foreach (var button in new[] { previousYear, previousMonth })
            {
                SetDock(button, Dock.Left);
                header.Children.Add(button);
            }
            foreach (var button in new[] { nextYear, nextMonth })
            {
                SetDock(button, Dock.Right);
                header.Children.Add(button);
            }
            header.Children.Add(_title);
            root.Children.Add(header);
            previousYear.Click += (s, e) => Move(-12);
            previousMonth.Click += (s, e) => Move(-1);
            nextMonth.Click += (s, e) => Move(1);
            nextYear.Click += (s, e) => Move(12);

            _days = new UniformGrid { Columns = 7 };
            foreach (var name in new[] { "일", "월", "화", "수", "목", "금", "토" })
            {
                var label = Theme.Secondary(name);
                label.HorizontalAlignment = HorizontalAlignment.Center;
                label.Margin = new Thickness(0, 0, 0, 2);
                _days.Children.Add(label);
            }
            root.Children.Add(_days);

            // 시각 HH:mm:ss[.fff]  [오늘] [지금]
            var timeRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            var timeLabel = Theme.Secondary("시각");
            timeLabel.VerticalAlignment = VerticalAlignment.Center;
            timeLabel.Margin = new Thickness(2, 0, 6, 0);
            SetDock(timeLabel, Dock.Left);
            timeRow.Children.Add(timeLabel);
            var now = SmallButton("지금", "오늘 날짜와 지금 시각");
            var today = SmallButton("오늘", "오늘 날짜(시각은 그대로)");
            SetDock(now, Dock.Right);
            SetDock(today, Dock.Right);
            timeRow.Children.Add(now);
            timeRow.Children.Add(today);
            _time = new TextBox
            {
                Text = RowEditLogic.TimeText(RowEditLogic.TimePartOf(Box.Text)),
                FontFamily = Theme.Mono,
                MinWidth = 90,
                Margin = new Thickness(0, 0, 4, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "HH:mm:ss 또는 HH:mm:ss.fff"
            };
            AutomationProperties.SetName(_time, "시각");
            _time.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    Apply(_selected ?? _shown);
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    _popup.IsOpen = false;
                    e.Handled = true;
                }
            };
            timeRow.Children.Add(_time);
            root.Children.Add(timeRow);

            _error = Theme.Text("", Theme.SecondaryText);
            _error.Foreground = Theme.Danger;
            _error.FontSize = 11.5;
            _error.Margin = new Thickness(2, 4, 0, 0);
            _error.Visibility = Visibility.Collapsed;
            root.Children.Add(_error);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
            var ok = SmallButton("확인", "고른 날짜와 시각을 칸에 넣기");
            ok.SetResourceReference(Control.BorderBrushProperty, Theme.Accent);
            var cancel = SmallButton("취소", null);
            ok.Click += (s, e) => Apply(_selected ?? _shown);
            cancel.Click += (s, e) => _popup.IsOpen = false;
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);

            today.Click += (s, e) =>
            {
                _selected = DateTime.Today;
                _shown = DateTime.Today;
                RenderDays();
            };
            now.Click += (s, e) =>
            {
                var current = DateTime.Now;
                _time.Text = RowEditLogic.TimeText(new TimeSpan(current.Hour, current.Minute, current.Second));
                Apply(current.Date);
            };
            RenderDays();
            return border;
        }

        private void Move(int months)
        {
            _shown = _shown.AddMonths(months);
            RenderDays();
        }

        /// <summary>날짜 버튼 42개를 다시 그린다(다른 달은 흐리게, 오늘은 강조 테두리, 고른 날은 선택색).</summary>
        private void RenderDays()
        {
            var month = new DateTime(_shown.Year, _shown.Month, 1);
            _title.Text = month.ToString("yyyy년 M월", CultureInfo.InvariantCulture);
            while (_days.Children.Count > 7)
                _days.Children.RemoveAt(_days.Children.Count - 1);
            foreach (var day in RowEditLogic.MonthGrid(month))
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
                if (date.Month != month.Month)
                    Theme.Foreground(button, Theme.SecondaryText);
                if (_selected.HasValue && date == _selected.Value.Date)
                    button.SetResourceReference(Control.BackgroundProperty, Theme.Selection);
                if (date == DateTime.Today)
                {
                    button.SetResourceReference(Control.BorderBrushProperty, Theme.Accent);
                    button.ToolTip = "오늘";
                }
                button.Click += (s, e) => Apply(date);
                _days.Children.Add(button);
            }
        }

        /// <summary>날짜 + 시각 칸을 합쳐 글 칸에 넣고 닫는다. 시각이 틀리면 팝업에 이유를 보이고 닫지 않는다.</summary>
        private void Apply(DateTime date)
        {
            TimeSpan time;
            if (!RowEditLogic.TryParseTime(_time.Text, out time))
            {
                _error.Text = "시각 형식이 아닙니다(HH:mm:ss): " + _time.Text;
                _error.Visibility = Visibility.Visible;
                _time.Focus();
                _time.SelectAll();
                return;
            }
            Box.Text = RowEditLogic.Compose(date, time);
            _popup.IsOpen = false;
            Picked?.Invoke(this, EventArgs.Empty);
            Box.Focus();
            Box.SelectAll();
        }

        private static Button NavButton(string text, string tip)
        {
            return new Button { Content = text, Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(1), MinWidth = 26, Height = 24, FontSize = 11, Focusable = false, ToolTip = tip };
        }

        private static Button SmallButton(string text, string tip)
        {
            return new Button { Content = text, Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(2, 0, 0, 0), Height = 24, FontSize = 12, ToolTip = tip };
        }
    }
}

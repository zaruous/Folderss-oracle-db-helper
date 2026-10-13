using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace MyPlugin
{
    /// <summary>
    /// 찾기 팝업(Ctrl+F): 편집기·결과 그리드 오른쪽 위에 떠 있는 작은 막대. 찾는 말 · 개수(3/12) · [▲]·[▼] · [✕].
    /// Enter·F3 다음, Shift+Enter·Shift+F3 이전, Esc 닫기. 찾기와 포커스 이동은 만든 쪽이 Search·Closed로 한다.
    /// </summary>
    internal sealed class FindBar
    {
        private readonly TextBox _box;
        private readonly TextBlock _count;

        public FindBar(string name, string tip)
        {
            _box = new TextBox
            {
                Width = 200,
                FontFamily = Theme.Mono,
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = tip
            };
            SpellCheck.SetIsEnabled(_box, false);
            AutomationProperties.SetName(_box, name);
            _box.TextChanged += (s, e) => Changed?.Invoke();
            _box.PreviewKeyDown += Box_PreviewKeyDown;

            _count = Theme.Text("", Theme.SecondaryText);
            _count.FontSize = 11.5;
            _count.MinWidth = 56;
            _count.TextAlignment = TextAlignment.Center;
            _count.VerticalAlignment = VerticalAlignment.Center;
            _count.Margin = new Thickness(6, 0, 4, 0);

            var previous = SmallButton("▲", "이전 (Shift+Enter · Shift+F3)");
            var next = SmallButton("▼", "다음 (Enter · F3)");
            var close = SmallButton("✕", "닫기 (Esc)");
            previous.Click += (s, e) => Search?.Invoke(false);
            next.Click += (s, e) => Search?.Invoke(true);
            close.Click += (s, e) => Close();

            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 4, 4, 4) };
            panel.Children.Add(_box);
            panel.Children.Add(_count);
            panel.Children.Add(previous);
            panel.Children.Add(next);
            panel.Children.Add(close);

            View = new Border
            {
                Child = panel,
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                // 오른쪽 세로 스크롤 막대를 가리지 않게
                Margin = new Thickness(0, 4, 22, 0),
                Visibility = Visibility.Collapsed
            };
            View.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
            Theme.Background(View, Theme.SurfaceBackground);
            AutomationProperties.SetName(View, name);
            // 같은 Grid에 나중에 넣는 편집기·그리드보다 위에
            Panel.SetZIndex(View, 100);
        }

        public Border View { get; }

        public string Term
        {
            get { return _box.Text ?? ""; }
        }

        public bool IsOpen
        {
            get { return View.Visibility == Visibility.Visible; }
        }

        /// <summary>찾기: true = 다음, false = 이전.</summary>
        public event Action<bool> Search;

        /// <summary>찾는 말이 바뀜(입력하는 대로 찾기).</summary>
        public event Action Changed;

        public event Action Closed;

        /// <summary>연다. initial이 있으면 찾는 말을 그것으로 바꾼다. 찾는 말을 모두 고른 채 상자에 포커스.</summary>
        public void Open(string initial)
        {
            View.Visibility = Visibility.Visible;
            if (!string.IsNullOrEmpty(initial) && initial != _box.Text)
                _box.Text = initial;
            _box.SelectAll();
            // 방금 보이게 한 상자는 레이아웃 전이라 포커스를 받지 못할 수 있다 — 레이아웃 뒤에 한 번 더
            _box.Focus();
            _box.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (IsOpen)
                    Keyboard.Focus(_box);
            }));
        }

        public void Close()
        {
            if (!IsOpen)
                return;
            View.Visibility = Visibility.Collapsed;
            Closed?.Invoke();
        }

        /// <summary>개수 표시. none이면 위험색("결과 없음").</summary>
        public void SetCount(string text, bool none)
        {
            _count.Text = text ?? "";
            if (none)
                _count.Foreground = Theme.Danger;
            else
                _count.SetResourceReference(TextBlock.ForegroundProperty, Theme.SecondaryText);
        }

        private void Box_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
            var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            if (key == Key.Enter || key == Key.F3)
            {
                e.Handled = true;
                Search?.Invoke(!shift);
            }
            else if (key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        }

        private static Button SmallButton(string text, string tip)
        {
            return new Button
            {
                Content = text,
                Padding = new Thickness(6, 0, 6, 0),
                Margin = new Thickness(2, 0, 0, 0),
                MinWidth = 24,
                Focusable = false,
                ToolTip = tip
            };
        }
    }
}

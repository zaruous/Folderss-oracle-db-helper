using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MyPlugin
{
    /// <summary>
    /// 뷰·프로시저·함수·패키지·시퀀스 소스 창(트리 [소스 보기]·코드 객체·시퀀스 F4). 모달 아님(열어 둔 채 SQL을 쓸 수 있다), Esc로 닫음.
    /// 위: 이름·종류·DB 배지. 가운데: 읽기 전용 고정폭 소스(선택·Ctrl+C 됨). 아래: [SQL 탭에 넣기]·[복사]·[닫기].
    /// </summary>
    internal static class ObjectSourceWindow
    {
        public static Window Show(Window owner, string schema, string objectType, string name, string source, OracleConnectionProfile profile,
            Action openInTab, Action<string> copy)
        {
            var qualified = ObjectSource.DisplayName(schema, name);
            var window = new Window
            {
                Title = "소스 — " + qualified + (profile != null ? " (" + profile.Name + ")" : ""),
                Width = 820,
                Height = 600,
                MinWidth = 480,
                MinHeight = 320,
                ShowInTaskbar = false,
                ResizeMode = ResizeMode.CanResizeWithGrip
            };
            Theme.ApplyWindow(window);
            DialogKit.SetOwner(window, owner);

            var root = new DockPanel { Margin = new Thickness(14, 12, 14, 12) };
            var header = Header(qualified, objectType, profile);
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var buttons = Buttons(window, openInTab, () => copy(source));
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);

            var text = new TextBox
            {
                Text = WorkspaceLogic.NormalizeNewlines(source, Environment.NewLine),
                IsReadOnly = true,
                IsReadOnlyCaretVisible = true,
                AcceptsReturn = true,
                AcceptsTab = true,
                TextWrapping = TextWrapping.NoWrap,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                // Folderss의 암시적 TextBox 스타일은 한 줄 입력칸용(가운데 정렬)이다
                VerticalContentAlignment = VerticalAlignment.Top,
                FontFamily = Theme.Mono,
                Margin = new Thickness(0, 10, 0, 0)
            };
            SpellCheck.SetIsEnabled(text, false);
            Theme.Background(text, Theme.PanelBackground);
            Theme.Foreground(text);
            System.Windows.Automation.AutomationProperties.SetName(text, "소스");
            root.Children.Add(text);
            window.Content = root;

            window.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    window.Close();
                }
            };
            window.Loaded += (s, e) =>
            {
                text.Focus();
                text.CaretIndex = 0;
            };
            window.Show();
            return window;
        }

        private static FrameworkElement Header(string qualified, string objectType, OracleConnectionProfile profile)
        {
            var line = new WrapPanel();
            var name = Theme.Text(qualified);
            name.FontSize = 16;
            name.FontWeight = FontWeights.SemiBold;
            name.Margin = new Thickness(0, 0, 8, 0);
            line.Children.Add(name);
            var type = Theme.Text(TypeTitle(objectType), Theme.SecondaryText);
            type.VerticalAlignment = VerticalAlignment.Bottom;
            type.Margin = new Thickness(0, 0, 8, 2);
            line.Children.Add(type);
            if (profile != null)
            {
                var badge = Theme.DbBadge(profile);
                badge.VerticalAlignment = VerticalAlignment.Center;
                line.Children.Add(badge);
            }
            return line;
        }

        private static string TypeTitle(string objectType)
        {
            switch (objectType)
            {
                case "VIEW": return "뷰";
                case "PROCEDURE": return "프로시저";
                case "FUNCTION": return "함수";
                case "PACKAGE": return "패키지";
                case "SEQUENCE": return "시퀀스";
                default: return objectType ?? "";
            }
        }

        private static FrameworkElement Buttons(Window window, Action openInTab, Action copy)
        {
            var open = new Button
            {
                Content = "SQL 탭에 넣기",
                Padding = new Thickness(12, 3, 12, 3),
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = "이 DB 대상의 새 SQL 탭에 소스를 넣습니다(실행하지 않음)"
            };
            open.Click += (s, e) => openInTab();
            var copyButton = new Button { Content = "복사", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 6, 0) };
            copyButton.Click += (s, e) => copy();
            var close = new Button { Content = "닫기", Padding = new Thickness(16, 3, 16, 3), IsCancel = true };
            close.Click += (s, e) => window.Close();
            var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            panel.Children.Add(open);
            panel.Children.Add(copyButton);
            panel.Children.Add(close);
            return panel;
        }
    }
}

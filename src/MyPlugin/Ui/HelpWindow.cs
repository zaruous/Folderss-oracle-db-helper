using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace MyPlugin
{
    /// <summary>
    /// 도움말 창(모달 아님): 플러그인 폴더의 help\*.html을 WebBrowser로 보인다. 아래에 [브라우저에서 열기]·[닫기].
    /// 같은 소유자 창에 이미 열려 있으면 그 창에서 페이지만 바꾼다.
    /// </summary>
    internal sealed class HelpWindow
    {
        private static HelpWindow _current;

        private readonly Window _window;
        private readonly WebBrowser _browser;
        private readonly string _helpFolder;
        private string _page;

        private HelpWindow(Window owner, string helpFolder)
        {
            _helpFolder = helpFolder;
            _window = new Window
            {
                Width = 880,
                Height = 720,
                MinWidth = 520,
                MinHeight = 360,
                ShowInTaskbar = false,
                ResizeMode = ResizeMode.CanResizeWithGrip
            };
            Theme.ApplyWindow(_window);
            DialogKit.SetOwner(_window, owner);
            var root = new DockPanel();
            var foot = new DockPanel { Margin = new Thickness(10, 6, 10, 8) };
            var close = DialogKit.PlainButton("닫기");
            close.Click += (s, e) => _window.Close();
            var open = DialogKit.PlainButton("브라우저에서 열기");
            open.Margin = new Thickness(0, 0, 8, 0);
            open.Click += (s, e) => OpenExternal();
            var home = DialogKit.PlainButton("목차");
            home.Margin = new Thickness(0, 0, 8, 0);
            home.Click += (s, e) => Navigate(HelpLogic.Index);
            DockPanel.SetDock(close, Dock.Right);
            DockPanel.SetDock(open, Dock.Right);
            foot.Children.Add(close);
            foot.Children.Add(open);
            foot.Children.Add(home);
            DockPanel.SetDock(foot, Dock.Bottom);
            root.Children.Add(foot);
            _browser = new WebBrowser();
            AutomationProperties.SetName(_browser, "도움말");
            root.Children.Add(_browser);
            _window.Content = root;
            _window.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                    _window.Close();
            };
            _window.Closed += (s, e) =>
            {
                if (ReferenceEquals(_current, this))
                    _current = null;
                _browser.Dispose();
            };
        }

        /// <summary>도움말 창을 띄우거나(이미 있으면 앞으로) 그 페이지로 간다. helpFolder: 플러그인 폴더의 help(모르면 null — 온라인).</summary>
        public static void Show(Window owner, string helpFolder, string page)
        {
            var window = _current;
            if (window == null || !window._window.IsVisible)
            {
                window = new HelpWindow(owner, helpFolder);
                _current = window;
                window.Navigate(page);
                window._window.Show();
            }
            else
            {
                window.Navigate(page);
                window._window.Activate();
            }
        }

        private void Navigate(string page)
        {
            _page = page ?? HelpLogic.Index;
            _window.Title = HelpLogic.Title(_page);
            try
            {
                _browser.Navigate(HelpLogic.PageUri(_helpFolder, _page));
            }
            catch (Exception)
            {
                // WebBrowser(IE 구성 요소)를 쓸 수 없는 환경 — 기본 브라우저로
                OpenExternal();
            }
        }

        private void OpenExternal()
        {
            try
            {
                Process.Start(new ProcessStartInfo(HelpLogic.PageUri(_helpFolder, _page).AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception)
            {
                // 브라우저가 없으면 할 수 있는 것이 없다
            }
        }
    }
}

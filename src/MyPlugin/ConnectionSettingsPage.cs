using Folderss.Plugins;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace MyPlugin
{
    /// <summary>
    /// 설정 창의 "DB 접속" 탭: 저장된 접속 목록(읽기 전용)과 [접속 관리 열기].
    /// 편집은 접속 관리 대화상자에서만 한다(두 곳에서 편집하면 나중에 저장한 쪽이 다른 쪽 변경을 덮어쓰므로).
    /// </summary>
    public sealed class ConnectionSettingsPage : IPluginSettingsPage
    {
        private readonly IPluginManager _manager;

        public ConnectionSettingsPage(IPluginManager manager)
        {
            _manager = manager;
        }

        public string Title { get { return "DB 접속"; } }

        public FrameworkElement CreateView()
        {
            return new ConnectionList(_manager).Root;
        }

        /// <summary>접속 관리에서 [저장]할 때 이미 저장했으므로 여기서 쓸 것이 없다.</summary>
        public void Save()
        {
        }

        /// <summary>설정 창을 열 때마다 새로 만드는 목록 화면. 보이는 동안만 접속 목록 변경 알림을 받는다.</summary>
        private sealed class ConnectionList
        {
            private readonly IPluginManager _manager;
            private readonly StackPanel _rows = new StackPanel();
            private bool _subscribed;

            public ConnectionList(IPluginManager manager)
            {
                _manager = manager;

                var hint = Theme.Secondary("접속 추가·변경·삭제는 [접속 관리]에서 합니다. 접속 관리에서 [저장]하면 바로 적용되며, 이 설정 창의 [저장]·[취소]와는 관계없습니다.");
                hint.TextWrapping = TextWrapping.Wrap;
                hint.FontSize = 12;
                var open = new Button { Content = "접속 관리 열기", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
                open.Click += Open_Click;
                var frame = new Border
                {
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 6, 8, 6),
                    Margin = new Thickness(0, 8, 0, 0),
                    Child = new ScrollViewer
                    {
                        Content = _rows,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        Focusable = false
                    }
                };
                frame.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
                frame.SetResourceReference(Border.BackgroundProperty, Theme.PanelBackground);

                var root = new DockPanel { Margin = new Thickness(8) };
                DockPanel.SetDock(hint, Dock.Top);
                root.Children.Add(hint);
                DockPanel.SetDock(open, Dock.Bottom);
                root.Children.Add(open);
                root.Children.Add(frame);
                root.Loaded += Root_Loaded;
                root.Unloaded += Root_Unloaded;
                Root = root;
                Refresh();
            }

            public FrameworkElement Root { get; }

            private void Root_Loaded(object sender, RoutedEventArgs e)
            {
                if (!_subscribed)
                {
                    ConnectionRepository.Changed += Repository_Changed;
                    _subscribed = true;
                }
                Refresh(); // 안 보이는 동안 바뀌었을 수 있다
            }

            private void Root_Unloaded(object sender, RoutedEventArgs e)
            {
                Unsubscribe();
            }

            private void Repository_Changed(object sender, EventArgs e)
            {
                // 설정 창이 Unloaded 없이 사라진 경우에도 정적 이벤트에 붙어 남지 않게 한다.
                if (!Root.IsLoaded)
                {
                    Unsubscribe();
                    return;
                }
                Refresh();
            }

            private void Unsubscribe()
            {
                if (!_subscribed)
                    return;
                ConnectionRepository.Changed -= Repository_Changed;
                _subscribed = false;
            }

            private void Open_Click(object sender, RoutedEventArgs e)
            {
                var owner = Window.GetWindow(Root);
                try
                {
                    ConnectionManager.Show(owner, _manager);
                }
                catch (Exception ex)
                {
                    Dialogs.Show(owner, "DB 접속", "접속 관리를 열지 못했습니다: " + ex.Message, true);
                }
                Refresh();
            }

            private void Refresh()
            {
                _rows.Children.Clear();
                try
                {
                    List<OracleConnectionProfile> profiles;
                    try
                    {
                        profiles = ConnectionRepository.Load(_manager);
                    }
                    catch (InvalidOperationException ex)
                    {
                        // 손상된 값은 목록 대신 이유를 보인다. 접속 관리도 이 값을 덮어쓰지 않는다.
                        AddMessage(ex.Message + Environment.NewLine + "접속 관리에서도 이 값을 덮어쓰지 않습니다.", true);
                        return;
                    }
                    if (profiles.Count == 0)
                    {
                        AddMessage("등록된 접속이 없습니다. [접속 관리 열기]로 추가하세요.", false);
                        return;
                    }
                    foreach (var profile in profiles)
                        _rows.Children.Add(Row(profile));
                }
                catch (Exception ex)
                {
                    _rows.Children.Clear();
                    AddMessage("접속 목록을 표시하지 못했습니다: " + ex.Message, true);
                }
            }

            private void AddMessage(string text, bool error)
            {
                var block = error
                    ? new TextBlock { Text = text, Foreground = Theme.Danger }
                    : Theme.Text(text, Theme.SecondaryText);
                block.TextWrapping = TextWrapping.Wrap;
                _rows.Children.Add(block);
            }

            private static FrameworkElement Row(OracleConnectionProfile profile)
            {
                var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                var badge = Theme.DbBadge(profile);
                badge.Margin = new Thickness(0, 0, 8, 0);
                DockPanel.SetDock(badge, Dock.Left);
                row.Children.Add(badge);
                if (profile.ReadOnly)
                {
                    var readOnly = Theme.Secondary("읽기 전용");
                    readOnly.Margin = new Thickness(8, 0, 0, 0);
                    readOnly.VerticalAlignment = VerticalAlignment.Center;
                    DockPanel.SetDock(readOnly, Dock.Right);
                    row.Children.Add(readOnly);
                }
                var address = Theme.Text(ConnectionManagerLogic.Address(profile), Theme.SecondaryText);
                address.FontFamily = Theme.Mono;
                address.FontSize = 11.5;
                address.TextTrimming = TextTrimming.CharacterEllipsis;
                address.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(address);
                return row;
            }
        }
    }
}

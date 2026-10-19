using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace MyPlugin
{
    /// <summary>
    /// 전체 DB 상태 창(모달 아님, DB Helper 창마다 하나). 저장한 접속마다 타일 하나 — 가장 심각한 상태의 색과 그 이유 한 줄, 활성 세션과 미니 추이,
    /// 공간(최대 기준)·FRA, 백업·로그 모드, 갱신 시각. 심각한 것이 앞(고정한 것은 맨 앞), 이름순으로 바꿀 수 있다.
    /// - 타일마다 이 창 전용 세션을 열어 창이 열린 동안 유지한다(동시 <see cref="OverviewLogic.MaxConcurrent"/>개, DB마다 <see cref="OverviewLogic.TimeoutSeconds"/>초 제한).
    /// - 저장된 비밀번호가 있는 접속만 스스로 붙는다. 없으면 "비밀번호 필요" — 타일을 누를 때만 묻는다(한꺼번에 비밀번호 창이 뜨지 않게).
    /// - 공급자에 <see cref="IMonitorProvider.Overview"/>가 없으면(지금은 PostgreSQL·SQL Server) "지원 예정" 타일.
    /// - 타일을 누르면 그 DB의 성능 모니터, 오른쪽 메뉴: 잠금 트리 열기 · 맨 앞에 고정/고정 풀기 · 이 화면에서 빼기 · 지금 새로 고침.
    /// - 판정은 <see cref="OverviewLogic"/>(DBMS 중립), 지표는 공급자 SQL(긴 형식).
    /// </summary>
    internal sealed class OverviewWindow
    {
        /// <summary>창이 DB Helper에서 받는 것(접속 목록·세션 열기·모니터 열기·설정).</summary>
        internal sealed class Host
        {
            public Func<IReadOnlyList<OracleConnectionProfile>> Profiles { get; set; }
            public Func<OracleConnectionProfile, bool> HasStoredPassword { get; set; }
            /// <summary>접속 Id → 세션(저장된 비밀번호가 없으면 묻는다, 취소하면 null). 연결 오류는 예외.</summary>
            public Func<string, Task<DbSession>> Connect { get; set; }
            /// <summary>(접속 Id, 섹션 id 또는 null) → 성능 모니터 창.</summary>
            public Action<string, string> OpenMonitor { get; set; }
            /// <summary>[일일 점검…] — 일일 점검 창. null이면 버튼을 두지 않는다.</summary>
            public Action OpenChecks { get; set; }
            public Func<string, string> GetSetting { get; set; }
            public Action<string, string> SetSetting { get; set; }
            public string HelpFolder { get; set; }
        }

        private readonly Window _window;
        private readonly Host _host;
        private readonly Dictionary<string, Tile> _tiles = new Dictionary<string, Tile>(StringComparer.Ordinal);
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(OverviewLogic.MaxConcurrent);
        private readonly CancellationTokenSource _closing = new CancellationTokenSource();
        private readonly DispatcherTimer _timer = new DispatcherTimer();
        private readonly DispatcherTimer _ageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        private HashSet<string> _pinned;
        private HashSet<string> _hidden;
        private WrapPanel _panel;
        private TextBlock _summary;
        private TextBlock _empty;
        private Button _showHidden;
        private bool _bySeverity = true;
        private bool _refreshing;
        private bool _closed;

        private OverviewWindow(Window owner, Host host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _pinned = OverviewLogic.ParseIds(SafeGet(OverviewLogic.PinnedSettingKey));
            _hidden = OverviewLogic.ParseIds(SafeGet(OverviewLogic.HiddenSettingKey));
            _window = new Window
            {
                Title = OverviewLogic.Title,
                Width = 1000,
                Height = 640,
                MinWidth = 480,
                MinHeight = 320,
                ShowInTaskbar = false,
                ResizeMode = ResizeMode.CanResizeWithGrip
            };
            Theme.ApplyWindow(_window);
            DialogKit.SetOwner(_window, owner);
            _window.Content = Build();
            _window.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    _window.Close();
                }
                else if (e.Key == Key.F5)
                {
                    e.Handled = true;
                    RefreshAll();
                }
            };
            _window.Loaded += (s, e) => RefreshAll();
            _window.Closed += (s, e) =>
            {
                _closed = true;
                _timer.Stop();
                _ageTimer.Stop();
                _closing.Cancel();
                foreach (var tile in _tiles.Values)
                    DisposeQuietly(tile.Session);
                _tiles.Clear();
            };
            _timer.Tick += (s, e) => RefreshAll();
            _ageTimer.Tick += (s, e) =>
            {
                foreach (var tile in _tiles.Values)
                    tile.Updated.Text = OverviewLogic.UpdatedText(tile.UpdatedAt, DateTime.Now);
            };
            SetRefresh(OverviewLogic.DefaultRefreshSeconds);
            _ageTimer.Start();
        }

        public static OverviewWindow Show(Window owner, Host host)
        {
            var overview = new OverviewWindow(owner, host);
            overview._window.Show();
            return overview;
        }

        public event EventHandler Closed
        {
            add { _window.Closed += value; }
            remove { _window.Closed -= value; }
        }

        public void Close()
        {
            if (!_closed)
                _window.Close();
        }

        public void Activate()
        {
            if (_closed)
                return;
            if (_window.WindowState == WindowState.Minimized)
                _window.WindowState = WindowState.Normal;
            _window.Activate();
        }

        // ================= 화면 =================

        private FrameworkElement Build()
        {
            var root = new DockPanel { Margin = new Thickness(14, 12, 14, 12) };
            var header = Header();
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);
            var note = Theme.Secondary("공간은 자동 확장 최대 크기 기준(DBA_TABLESPACE_USAGE_METRICS, 영구 테이블스페이스만), FRA는 회수 가능 공간을 뺀 사용률, 잠금 대기는 3초 이상. 타일을 누르면 그 DB의 성능 모니터가 열립니다.");
            note.TextWrapping = TextWrapping.Wrap;
            note.Margin = new Thickness(0, 6, 0, 8);
            DockPanel.SetDock(note, Dock.Top);
            root.Children.Add(note);
            var host = new Grid();
            _panel = new WrapPanel { Orientation = Orientation.Horizontal };
            AutomationProperties.SetName(_panel, "DB 타일");
            var scroll = new ScrollViewer { Content = _panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            host.Children.Add(scroll);
            _empty = Theme.Text("등록한 접속이 없습니다. [접속 관리…]에서 추가하세요.", Theme.SecondaryText);
            _empty.HorizontalAlignment = HorizontalAlignment.Center;
            _empty.VerticalAlignment = VerticalAlignment.Center;
            _empty.Visibility = Visibility.Collapsed;
            host.Children.Add(_empty);
            root.Children.Add(host);
            return root;
        }

        private FrameworkElement Header()
        {
            var panel = new DockPanel();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            buttons.Children.Add(Label("정렬"));
            var sort = new ComboBox { MinWidth = 72, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            sort.Items.Add(new ComboBoxItem { Content = "심각도", Tag = true });
            sort.Items.Add(new ComboBoxItem { Content = "이름", Tag = false });
            sort.SelectedIndex = 0;
            sort.SelectionChanged += (s, e) =>
            {
                var item = sort.SelectedItem as ComboBoxItem;
                _bySeverity = item == null || (bool)item.Tag;
                Arrange();
            };
            AutomationProperties.SetName(sort, "정렬");
            buttons.Children.Add(sort);
            buttons.Children.Add(Label("자동 새로 고침"));
            var interval = new ComboBox { MinWidth = 64, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            foreach (var seconds in OverviewLogic.RefreshSeconds)
            {
                interval.Items.Add(new ComboBoxItem { Content = OverviewLogic.RefreshText(seconds), Tag = seconds });
                if (seconds == OverviewLogic.DefaultRefreshSeconds)
                    interval.SelectedIndex = interval.Items.Count - 1;
            }
            interval.SelectionChanged += (s, e) =>
            {
                var item = interval.SelectedItem as ComboBoxItem;
                SetRefresh(item == null ? 0 : (int)item.Tag);
            };
            AutomationProperties.SetName(interval, "자동 새로 고침");
            buttons.Children.Add(interval);
            var refresh = new Button { Content = "새로 고침", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 6, 0), ToolTip = "모든 타일을 다시 가져옵니다 (F5)" };
            refresh.Click += (s, e) => RefreshAll();
            buttons.Children.Add(refresh);
            if (_host.OpenChecks != null)
            {
                var checks = new Button { Content = CheckLogic.Title + "…", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 6, 0), ToolTip = "고른 DB들에 일일 점검 항목을 돌리고 엑셀 보고서로 저장합니다" };
                checks.Click += (s, e) => _host.OpenChecks();
                buttons.Children.Add(checks);
            }
            var help = new Button { Content = "도움말", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 6, 0) };
            help.Click += (s, e) => HelpWindow.Show(_window, _host.HelpFolder, HelpLogic.Monitor);
            buttons.Children.Add(help);
            var close = new Button { Content = "닫기", Padding = new Thickness(16, 3, 16, 3), IsCancel = true };
            close.Click += (s, e) => _window.Close();
            buttons.Children.Add(close);
            DockPanel.SetDock(buttons, Dock.Right);
            panel.Children.Add(buttons);

            var title = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            var name = Theme.Text(OverviewLogic.Title);
            name.FontSize = 16;
            name.FontWeight = FontWeights.SemiBold;
            name.Margin = new Thickness(0, 0, 10, 0);
            title.Children.Add(name);
            _summary = Theme.Text("", Theme.SecondaryText);
            _summary.VerticalAlignment = VerticalAlignment.Center;
            _summary.Margin = new Thickness(0, 0, 10, 0);
            title.Children.Add(_summary);
            _showHidden = new Button { Content = "숨긴 DB 다시 보이기", Padding = new Thickness(8, 1, 8, 1), Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
            _showHidden.Click += (s, e) =>
            {
                _hidden.Clear();
                SafeSet(OverviewLogic.HiddenSettingKey, "");
                RefreshAll();
            };
            title.Children.Add(_showHidden);
            panel.Children.Add(title);
            return panel;
        }

        private static TextBlock Label(string text)
        {
            var label = Theme.Text(text, Theme.SecondaryText);
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Margin = new Thickness(0, 0, 6, 0);
            return label;
        }

        private void SetRefresh(int seconds)
        {
            _timer.Stop();
            if (seconds > 0)
            {
                _timer.Interval = TimeSpan.FromSeconds(seconds);
                _timer.Start();
            }
        }

        // ================= 타일 =================

        /// <summary>접속 목록을 다시 읽어 타일을 맞춘다(새 접속은 더하고, 지운·숨긴 접속은 빼고 세션을 닫는다).</summary>
        private void SyncTiles()
        {
            IReadOnlyList<OracleConnectionProfile> profiles;
            try
            {
                profiles = _host.Profiles() ?? Array.Empty<OracleConnectionProfile>();
            }
            catch (Exception)
            {
                profiles = Array.Empty<OracleConnectionProfile>();
            }
            var wanted = profiles.Where(p => p != null && !string.IsNullOrEmpty(p.Id) && !_hidden.Contains(p.Id)).ToList();
            foreach (var id in _tiles.Keys.Where(id => wanted.All(p => p.Id != id)).ToList())
            {
                DisposeQuietly(_tiles[id].Session);
                _tiles.Remove(id);
            }
            foreach (var profile in wanted)
            {
                Tile tile;
                if (!_tiles.TryGetValue(profile.Id, out tile))
                {
                    tile = new Tile { Profile = profile, Provider = DbProviders.For(profile) };
                    BuildTile(tile);
                    _tiles[profile.Id] = tile;
                }
                else
                {
                    tile.Profile = profile;
                    tile.Provider = DbProviders.For(profile);
                }
                if (tile.Provider.Monitor.Overview == null)
                    tile.State = OverviewTileState.NotSupported;
                else if (tile.Session == null && !tile.Busy && !HasPassword(tile.Profile))
                    tile.State = OverviewTileState.NeedsPassword;
                ShowTile(tile);
            }
            _empty.Visibility = _tiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _showHidden.Visibility = _hidden.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private bool HasPassword(OracleConnectionProfile profile)
        {
            try
            {
                return _host.HasStoredPassword(profile);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>타일 순서를 다시 정하고 머리 요약을 고친다.</summary>
        private void Arrange()
        {
            if (_closed)
                return;
            var ordered = OverviewLogic.Order(_tiles.Values, t => t.State, t => t.Profile.Name, t => _pinned.Contains(t.Profile.Id), _bySeverity);
            _panel.Children.Clear();
            foreach (var tile in ordered)
                _panel.Children.Add(tile.Element);
            _summary.Text = OverviewLogic.SummaryText(_tiles.Values.Select(t => t.State), _hidden.Count);
        }

        /// <summary>모든 타일을 가져온다(지원하고, 세션이 있거나 저장된 비밀번호가 있는 것만). 가져오는 중이면 겹치지 않는다.</summary>
        private async void RefreshAll()
        {
            try
            {
                if (_closed || _refreshing)
                    return;
                _refreshing = true;
                try
                {
                    SyncTiles();
                    Arrange();
                    // 타일 하나가 끝날 때마다 순서·요약을 고친다 — 느린 DB 하나(접속 시간 제한까지)가 전체 정렬을 붙잡지 않게
                    var work = _tiles.Values
                        .Where(t => t.Provider.Monitor.Overview != null && !t.Busy && (t.Session != null || HasPassword(t.Profile)))
                        .Select(async t =>
                        {
                            await RefreshTile(t, false);
                            Arrange();
                        }).ToList();
                    await Task.WhenAll(work);
                }
                finally
                {
                    _refreshing = false;
                }
            }
            catch (Exception)
            {
                // async void — 예외를 밖으로 내보내면 Folderss가 멈춘다
            }
        }

        /// <summary>
        /// 타일 하나: 세션(없거나 끊겼으면 연다 — ask면 비밀번호를 물을 수 있다) → 지표 조회(시간 제한) → 판정.
        /// 접속 실패·권한·조회 실패(시간 초과 포함)를 나눠 타일에 보인다.
        /// </summary>
        private async Task RefreshTile(Tile tile, bool ask)
        {
            var connecting = false;
            try
            {
                await _gate.WaitAsync(_closing.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            try
            {
                if (_closed)
                    return;
                tile.Busy = true;
                if (tile.Snapshot == null)
                    tile.State = OverviewTileState.Loading;
                ShowTile(tile);
                var session = tile.Session;
                if (session == null || !session.IsOpen || session.IsBroken)
                {
                    DisposeQuietly(session);
                    tile.Session = null;
                    if (!ask && !HasPassword(tile.Profile))
                    {
                        tile.State = OverviewTileState.NeedsPassword;
                        return;
                    }
                    connecting = true;
                    session = await _host.Connect(tile.Profile.Id);
                    connecting = false;
                    if (_closed)
                    {
                        DisposeQuietly(session);
                        return;
                    }
                    if (session == null)
                    {
                        tile.State = OverviewTileState.NeedsPassword;
                        return;
                    }
                    tile.Session = session;
                }
                using (var limit = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token))
                {
                    limit.CancelAfter(TimeSpan.FromSeconds(OverviewLogic.TimeoutSeconds));
                    var table = await session.QueryTableAsync(tile.Provider.Monitor.Overview.Sql, 200, limit.Token);
                    if (_closed)
                        return;
                    tile.Snapshot = OverviewLogic.Parse(table);
                    tile.Findings = OverviewLogic.Judge(tile.Snapshot);
                    tile.State = OverviewLogic.StateOf(tile.Findings);
                    tile.Message = null;
                    tile.UpdatedAt = DateTime.Now;
                    var trend = OverviewLogic.TrendValue(tile.Snapshot);
                    if (trend.HasValue)
                    {
                        tile.Trend.Add(trend.Value);
                        while (tile.Trend.Count > OverviewLogic.TrendPoints)
                            tile.Trend.RemoveAt(0);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (_closed)
                    return;
                tile.State = OverviewTileState.QueryFailed;
                tile.Message = OverviewLogic.TimeoutText + " (" + OverviewLogic.TimeoutSeconds + "초)";
            }
            catch (Exception ex)
            {
                if (_closed)
                    return;
                var reason = DbSession.DescribeError(ex);
                if (connecting)
                {
                    tile.State = OverviewTileState.ConnectFailed;
                    tile.Message = reason;
                }
                else if (tile.Provider.IsPermissionDenied(ex))
                {
                    tile.State = OverviewTileState.PermissionDenied;
                    tile.Message = "필요 권한: " + tile.Provider.Monitor.Overview.Privilege + " (" + reason + ")";
                }
                else
                {
                    tile.State = OverviewTileState.QueryFailed;
                    tile.Message = reason;
                }
            }
            finally
            {
                tile.Busy = false;
                _gate.Release();
                if (!_closed)
                    ShowTile(tile);
            }
        }

        /// <summary>타일 화면 조각을 만든다(값은 <see cref="ShowTile"/>이 채운다).</summary>
        private void BuildTile(Tile tile)
        {
            var stack = new StackPanel { Margin = new Thickness(10, 8, 10, 8) };
            var head = new DockPanel();
            tile.Chip = new Border { CornerRadius = new CornerRadius(3), Padding = new Thickness(6, 0, 6, 1), VerticalAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(1) };
            tile.ChipText = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold };
            tile.Chip.Child = tile.ChipText;
            DockPanel.SetDock(tile.Chip, Dock.Right);
            head.Children.Add(tile.Chip);
            tile.Name = Theme.Text("");
            tile.Name.FontSize = 14;
            tile.Name.FontWeight = FontWeights.SemiBold;
            tile.Name.TextTrimming = TextTrimming.CharacterEllipsis;
            tile.Name.Margin = new Thickness(0, 0, 6, 0);
            head.Children.Add(tile.Name);
            stack.Children.Add(head);
            tile.Reason = Theme.Text("");
            tile.Reason.TextWrapping = TextWrapping.Wrap;
            tile.Reason.MaxHeight = 36;
            tile.Reason.Margin = new Thickness(0, 4, 0, 4);
            stack.Children.Add(tile.Reason);
            var activity = new DockPanel();
            tile.Spark = new Polyline { Width = 90, Height = 18, StrokeThickness = 1.5, VerticalAlignment = VerticalAlignment.Center };
            tile.Spark.SetResourceReference(Shape.StrokeProperty, Theme.Accent);
            DockPanel.SetDock(tile.Spark, Dock.Right);
            activity.Children.Add(tile.Spark);
            tile.Activity = Theme.Text("");
            activity.Children.Add(tile.Activity);
            stack.Children.Add(activity);
            tile.Space = Theme.Text("", Theme.SecondaryText);
            tile.Space.TextTrimming = TextTrimming.CharacterEllipsis;
            stack.Children.Add(tile.Space);
            tile.Backup = Theme.Text("", Theme.SecondaryText);
            tile.Backup.TextTrimming = TextTrimming.CharacterEllipsis;
            stack.Children.Add(tile.Backup);
            var foot = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            tile.Updated = Theme.Secondary("");
            DockPanel.SetDock(tile.Updated, Dock.Right);
            foot.Children.Add(tile.Updated);
            tile.Address = Theme.Secondary("");
            tile.Address.TextTrimming = TextTrimming.CharacterEllipsis;
            tile.Address.Margin = new Thickness(0, 0, 6, 0);
            foot.Children.Add(tile.Address);
            stack.Children.Add(foot);

            var body = new DockPanel();
            tile.Band = new Border { Height = 4 };
            DockPanel.SetDock(tile.Band, Dock.Top);
            body.Children.Add(tile.Band);
            body.Children.Add(stack);
            tile.Element = new Border
            {
                Width = 286,
                MinHeight = 150,
                Margin = new Thickness(0, 0, 10, 10),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Child = body,
                Cursor = Cursors.Hand,
                Focusable = true
            };
            tile.Element.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
            Theme.Background(tile.Element, Theme.PanelBackground);
            tile.Element.MouseLeftButtonUp += (s, e) => OpenTile(tile);
            tile.Element.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    OpenTile(tile);
                }
            };
            tile.Element.ContextMenu = TileMenu(tile);
        }

        private ContextMenu TileMenu(Tile tile)
        {
            var menu = new ContextMenu();
            var monitor = new MenuItem { Header = "성능 모니터 열기" };
            monitor.Click += (s, e) => _host.OpenMonitor(tile.Profile.Id, null);
            menu.Items.Add(monitor);
            var locks = new MenuItem { Header = "잠금 트리 열기" };
            locks.Click += (s, e) => _host.OpenMonitor(tile.Profile.Id, MonitorSections.Locks);
            menu.Items.Add(locks);
            var refresh = new MenuItem { Header = "지금 새로 고침" };
            refresh.Click += (s, e) => RefreshOne(tile, true);
            menu.Items.Add(refresh);
            menu.Items.Add(new Separator());
            var pin = new MenuItem();
            pin.Click += (s, e) =>
            {
                if (!_pinned.Remove(tile.Profile.Id))
                    _pinned.Add(tile.Profile.Id);
                SafeSet(OverviewLogic.PinnedSettingKey, OverviewLogic.SerializeIds(_pinned));
                ShowTile(tile);
                Arrange();
            };
            menu.Items.Add(pin);
            var hide = new MenuItem { Header = "이 화면에서 빼기" };
            hide.Click += (s, e) =>
            {
                _hidden.Add(tile.Profile.Id);
                SafeSet(OverviewLogic.HiddenSettingKey, OverviewLogic.SerializeIds(_hidden));
                SyncTiles();
                Arrange();
            };
            menu.Items.Add(hide);
            menu.Opened += (s, e) =>
            {
                var supported = tile.Provider.Monitor.Sections.Count > 0;
                monitor.IsEnabled = supported;
                locks.IsEnabled = supported;
                refresh.IsEnabled = tile.Provider.Monitor.Overview != null && !tile.Busy;
                pin.Header = _pinned.Contains(tile.Profile.Id) ? "고정 풀기" : "맨 앞에 고정";
            };
            return menu;
        }

        /// <summary>타일을 누름: 비밀번호가 필요하면 묻고 붙어서 가져오고, 아니면 그 DB의 성능 모니터를 연다.</summary>
        private void OpenTile(Tile tile)
        {
            if (_closed)
                return;
            if (tile.State == OverviewTileState.NeedsPassword)
            {
                RefreshOne(tile, true);
                return;
            }
            if (tile.Provider.Monitor.Sections.Count > 0)
                _host.OpenMonitor(tile.Profile.Id, null);
        }

        private async void RefreshOne(Tile tile, bool ask)
        {
            try
            {
                if (_closed || tile.Busy || tile.Provider.Monitor.Overview == null)
                    return;
                await RefreshTile(tile, ask);
                Arrange();
            }
            catch (Exception)
            {
                // async void
            }
        }

        /// <summary>타일 값을 화면에 채운다(상태 색·이유 줄·지표 줄·미니 추이·갱신 시각).</summary>
        private void ShowTile(Tile tile)
        {
            var profile = tile.Profile;
            tile.Name.Text = (_pinned.Contains(profile.Id) ? "📌 " : "") + profile.Name;
            tile.Name.ToolTip = profile.Name;
            tile.Address.Text = tile.Provider.DisplayName + " · " + ConnectionManagerLogic.Address(profile);
            tile.Address.ToolTip = tile.Address.Text;
            var band = Theme.ConnectionColor(profile.Color);
            if (band != null)
                tile.Band.Background = band;
            else
                tile.Band.SetResourceReference(Border.BackgroundProperty, Theme.Border);

            tile.ChipText.Text = OverviewLogic.StateText(tile.State);
            var color = StateBrush(tile.State);
            if (color != null)
            {
                tile.Chip.Background = color;
                tile.Chip.BorderBrush = color;
                tile.ChipText.Foreground = Brushes.White;
            }
            else
            {
                tile.Chip.Background = Brushes.Transparent;
                tile.Chip.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
                tile.ChipText.SetResourceReference(TextBlock.ForegroundProperty, Theme.SecondaryText);
            }

            var snapshot = tile.Snapshot;
            switch (tile.State)
            {
                case OverviewTileState.NotSupported:
                    SetReason(tile, OverviewLogic.NotSupportedText, null);
                    break;
                case OverviewTileState.NeedsPassword:
                    SetReason(tile, OverviewLogic.NeedsPasswordText, null);
                    break;
                case OverviewTileState.Loading:
                    SetReason(tile, "가져오는 중…", null);
                    break;
                case OverviewTileState.ConnectFailed:
                case OverviewTileState.QueryFailed:
                    SetReason(tile, tile.Message, Theme.Danger);
                    break;
                case OverviewTileState.PermissionDenied:
                    SetReason(tile, tile.Message, Theme.Warning);
                    break;
                default:
                    SetReason(tile, OverviewLogic.ReasonText(tile.Findings), tile.State == OverviewTileState.Critical ? Theme.Danger
                        : tile.State == OverviewTileState.Warning ? Theme.Warning : Theme.Success);
                    break;
            }
            tile.Activity.Text = snapshot == null ? "" : OverviewLogic.ActivityText(snapshot);
            tile.Space.Text = snapshot == null ? "" : OverviewLogic.SpaceText(snapshot);
            tile.Backup.Text = snapshot == null ? "" : OverviewLogic.BackupText(snapshot);
            tile.Updated.Text = OverviewLogic.UpdatedText(tile.UpdatedAt, DateTime.Now);
            tile.Spark.Points = new PointCollection(OverviewLogic.SparkPoints(tile.Trend, tile.Spark.Width, tile.Spark.Height).Select(p => new Point(p.Key, p.Value)));
            var findings = tile.Findings == null || tile.Findings.Count == 0 ? "" : string.Join(Environment.NewLine, tile.Findings.Select(f => "· " + f.Text));
            tile.Element.ToolTip = profile.Name + " — " + OverviewLogic.StateText(tile.State) + (findings.Length > 0 ? Environment.NewLine + findings : "")
                + (string.IsNullOrEmpty(tile.Message) ? "" : Environment.NewLine + tile.Message);
            AutomationProperties.SetName(tile.Element, profile.Name + " " + OverviewLogic.StateText(tile.State));
        }

        private static void SetReason(Tile tile, string text, Brush color)
        {
            tile.Reason.Text = text ?? "";
            tile.Reason.ToolTip = text;
            if (color != null)
                tile.Reason.Foreground = color;
            else
                tile.Reason.SetResourceReference(TextBlock.ForegroundProperty, Theme.SecondaryText);
        }

        /// <summary>상태 칩 색: 위험·실패는 위험색, 주의·권한은 경고색, 정상은 성공색, 그 밖은 테두리만(null).</summary>
        private static Brush StateBrush(OverviewTileState state)
        {
            switch (state)
            {
                case OverviewTileState.Critical:
                case OverviewTileState.ConnectFailed:
                case OverviewTileState.QueryFailed:
                    return Theme.Danger;
                case OverviewTileState.Warning:
                case OverviewTileState.PermissionDenied:
                    return Theme.Warning;
                case OverviewTileState.Ok:
                    return Theme.Success;
                default:
                    return null;
            }
        }

        // ================= 도움 =================

        private string SafeGet(string key)
        {
            try
            {
                return _host.GetSetting == null ? null : _host.GetSetting(key);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void SafeSet(string key, string value)
        {
            try
            {
                if (_host.SetSetting != null)
                    _host.SetSetting(key, value);
            }
            catch (Exception)
            {
                // 고정·숨김을 기억하지 못할 뿐이다(DbHelperView가 메시지 탭에 알린다)
            }
        }

        private static void DisposeQuietly(DbSession session)
        {
            if (session == null)
                return;
            Task.Run(() =>
            {
                try
                {
                    session.Dispose();
                }
                catch (Exception)
                {
                    // 스레드 풀 예외로 Folderss가 끝나지 않게
                }
            });
        }

        /// <summary>DB 하나의 타일 상태와 화면 조각.</summary>
        private sealed class Tile
        {
            public OracleConnectionProfile Profile { get; set; }
            public IDbProvider Provider { get; set; }
            public OverviewTileState State { get; set; } = OverviewTileState.Loading;
            public string Message { get; set; }
            public OverviewSnapshot Snapshot { get; set; }
            public List<OverviewFinding> Findings { get; set; }
            public DateTime? UpdatedAt { get; set; }
            public List<double> Trend { get; } = new List<double>();
            /// <summary>이 창의 전용 세션(창이 열린 동안 유지).</summary>
            public DbSession Session { get; set; }
            public bool Busy { get; set; }
            public Border Element { get; set; }
            public Border Band { get; set; }
            public Border Chip { get; set; }
            public TextBlock ChipText { get; set; }
            public TextBlock Name { get; set; }
            public TextBlock Reason { get; set; }
            public TextBlock Activity { get; set; }
            public Polyline Spark { get; set; }
            public TextBlock Space { get; set; }
            public TextBlock Backup { get; set; }
            public TextBlock Updated { get; set; }
            public TextBlock Address { get; set; }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Diagnostics;
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
    /// 성능 모니터 창(모달 아님 — 열어 둔 채 SQL을 쓸 수 있다, Esc로 닫음). DB마다 하나(DbHelperView가 같은 DB면 있는 창을 앞으로).
    /// 위: DB 배지·주소, 자동 새로 고침(끔·5초·15초·60초), [새로 고침]·[도움말]·[닫기].
    /// 가운데: 공급자(<see cref="IMonitorProvider"/>)의 섹션마다 고르는 버튼 + 설명 + 상태(갱신 시각·행 수 / 오류) + 표(결과 그리드 — 정렬·복사 됨).
    /// - 섹션은 처음 고를 때 가져오고, [새로 고침]·자동 새로 고침은 보고 있는 섹션만 다시 가져온다(무거운 DBA_FREE_SPACE 등을 매번 다 읽지 않게).
    /// - 창마다 **모니터 전용 세션**을 따로 연다(SQL 탭의 세션·트랜잭션과 무관 — 자동 새로 고침과 사용자 실행이 서로 기다리지 않는다).
    ///   처음 가져올 때 열고, 끊겼으면 다음 가져오기 때 다시 연다. 창을 닫으면 정리한다. SQL 탭이 연결돼 있지 않아도 된다.
    /// - 상태 글에 조회 소요 시간, 상세는 (상세, 키)별로 캐시해 같은 행을 다시 눌러도 다시 조회하지 않는다(섹션을 새로 가져오면 비움).
    /// - 권한 오류(<see cref="IDbProvider.IsPermissionDenied"/>)는 섹션의 필요 권한과 함께 보인다. 섹션마다 따로 실패한다.
    /// - 섹션에 <see cref="MonitorSection.Chart"/>가 있으면(테이블스페이스·데이터 파일) 표 위 대시보드에 행마다 사용률 도넛을 그린다
    ///   (80% 이상 경고색, 90% 이상 위험색, 최대 12개 — 나머지는 "외 N개는 표에서"). 코드로 그리는 Path라 차트 라이브러리는 없다.
    /// - 섹션에 <see cref="MonitorSection.Details"/>가 있으면 표 아래 분할선 밑에 상세 영역: 표의 행(또는 도넛)을 누르면 그 행의 키
    ///   (<see cref="MonitorSection.KeyColumn"/>)로 상세 조회(바인드 하나)를 실행해 보인다. 상세가 여럿이면 고르는 버튼. 이전 상세 조회는 취소한다.
    ///   <see cref="MonitorDetail.AsText"/>인 상세(SQL 전문·실행 계획)는 표 대신 고정폭 읽기 전용 글 상자.
    /// - <see cref="MonitorSection.Requirements"/>(확장 설치·서버 버전)는 가져오기 전에 점검해, 충족되지 않으면 조회하지 않고 설치 안내를 경고색으로 보인다(오류와 구분).
    ///   접속이 로컬호스트면(사용자가 직접 설치할 수 있는 서버) 설치 절차·링크(<see cref="MonitorRequirement.InstallGuide"/>)를 메시지 탭에 한 번 남긴다.
    /// - <see cref="MonitorSection.Actions"/>(세션 종료)는 표의 오른쪽 메뉴에 둔다: 고른 행의 값으로 문장을 만들고 위험한 문장 확인 대화상자
    ///   (문장·경고·확인 체크)를 거쳐 실행한 뒤 섹션을 다시 가져온다. 읽기 전용 접속이면 막는다.
    /// </summary>
    internal sealed class MonitorWindow
    {
        private readonly Window _window;
        private readonly IDbProvider _provider;
        private readonly OracleConnectionProfile _profile;
        // 모니터 전용 세션을 여는 함수(비밀번호 확인 포함). 취소하면 null. SQL 탭의 세션과 따로라 서로 기다리지 않는다.
        private readonly Func<Task<DbSession>> _connect;
        private DbSession _session;
        private Task<DbSession> _connecting;
        private readonly Action<string> _log;
        private readonly List<SectionView> _sections = new List<SectionView>();
        private readonly CancellationTokenSource _closing = new CancellationTokenSource();
        private readonly DispatcherTimer _timer = new DispatcherTimer();
        private int _current = -1;
        private bool _closed;
        // 처음 열 때 고를 섹션 id(없으면 첫 섹션)
        private string _initialSection;

        private MonitorWindow(Window owner, OracleConnectionProfile profile, IDbProvider provider, Func<Task<DbSession>> connect, string helpFolder, Action<string> log)
        {
            _profile = profile;
            _log = log ?? (text => { });
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _connect = connect ?? throw new ArgumentNullException(nameof(connect));
            _window = new Window
            {
                Title = DialogKit.TitleFor(MonitorLogic.Title, profile),
                Width = 960,
                Height = 600,
                MinWidth = 560,
                MinHeight = 340,
                ShowInTaskbar = false,
                ResizeMode = ResizeMode.CanResizeWithGrip
            };
            Theme.ApplyWindow(_window);
            DialogKit.SetOwner(_window, owner);
            _window.Content = Build(helpFolder);
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
                    Load(_current, true);
                }
            };
            _window.Loaded += (s, e) => Select(Math.Max(0, IndexOfSection(_initialSection)));
            _window.Closed += (s, e) =>
            {
                _closed = true;
                _timer.Stop();
                _closing.Cancel();
                // 모니터 전용 세션은 이 창의 것 — 닫을 때 정리한다(Dispose는 실행 중 작업을 기다리므로 스레드 풀에서)
                var session = _session;
                _session = null;
                DisposeQuietly(session);
            };
            _timer.Tick += (s, e) => Load(_current, true);
        }

        /// <summary>
        /// 창을 만들어 보인다. 공급자에 섹션이 없으면 InvalidOperationException(부르는 쪽이 먼저 확인한다).
        /// log: 메시지 탭에 남길 글(로컬호스트 접속에서 설치 절차 안내). null이면 남기지 않는다.
        /// </summary>
        public static MonitorWindow Show(Window owner, OracleConnectionProfile profile, IDbProvider provider, Func<Task<DbSession>> connect, string helpFolder,
            Action<string> log = null, string initialSection = null)
        {
            if (provider == null)
                throw new ArgumentNullException(nameof(provider));
            if (provider.Monitor.Sections.Count == 0)
                throw new InvalidOperationException(MonitorLogic.NotSupportedMessage(provider.DisplayName));
            var monitor = new MonitorWindow(owner, profile, provider, connect, helpFolder, log);
            monitor._initialSection = initialSection;
            monitor._window.Show();
            return monitor;
        }

        /// <summary>섹션 id(<see cref="MonitorSections"/>)로 그 섹션을 고른다(전체 DB 화면의 [잠금 트리 열기]). 없는 id면 그대로 둔다.</summary>
        public void SelectSection(string id)
        {
            var index = IndexOfSection(id);
            if (!_closed && index >= 0)
                Select(index);
        }

        private int IndexOfSection(string id)
        {
            if (string.IsNullOrEmpty(id))
                return -1;
            return _sections.FindIndex(v => string.Equals(v.Section.Id, id, StringComparison.Ordinal));
        }

        /// <summary>창이 닫혔을 때(DbHelperView가 DB별 창 목록에서 뺀다).</summary>
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

        private FrameworkElement Build(string helpFolder)
        {
            var root = new DockPanel { Margin = new Thickness(14, 12, 14, 12) };
            var header = Header(helpFolder);
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var strip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            var host = new Grid();
            var index = 0;
            foreach (var section in _provider.Monitor.Sections)
            {
                var view = new SectionView(section, index++);
                // 공급자가 잠금 연쇄 트리를 주면 [잠금] 섹션은 트리가 기본, [표로 보기]로 섹션 표(잠금 모드 상세)
                if (section.Id == MonitorSections.Locks && _provider.Monitor.LockTree != null)
                {
                    view.Tree = _provider.Monitor.LockTree;
                    view.TreeMode = true;
                }
                Border underline;
                view.Button = WorkspaceUi.SelectorButton(Theme.Text(section.Title), out underline);
                view.Underline = underline;
                AutomationProperties.SetName(view.Button, section.Title);
                var i = view.Index;
                view.Button.Click += (s, e) => Select(i);
                strip.Children.Add(view.Button);
                view.Panel = SectionPanel(view);
                host.Children.Add(view.Panel);
                _sections.Add(view);
            }
            var stripBorder = new Border { Child = strip, BorderThickness = new Thickness(0, 0, 0, 1) };
            stripBorder.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
            DockPanel.SetDock(stripBorder, Dock.Top);
            root.Children.Add(stripBorder);
            var hostBorder = new Border { Child = host, BorderThickness = new Thickness(1, 0, 1, 1) };
            hostBorder.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
            root.Children.Add(hostBorder);
            return root;
        }

        private FrameworkElement Header(string helpFolder)
        {
            var panel = new DockPanel();

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var label = Theme.Text("자동 새로 고침", Theme.SecondaryText);
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Margin = new Thickness(0, 0, 6, 0);
            buttons.Children.Add(label);
            var interval = new ComboBox { MinWidth = 72, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            foreach (var seconds in MonitorLogic.RefreshSeconds)
                interval.Items.Add(new ComboBoxItem { Content = MonitorLogic.RefreshText(seconds), Tag = seconds });
            interval.SelectedIndex = 0;
            interval.SelectionChanged += (s, e) =>
            {
                var item = interval.SelectedItem as ComboBoxItem;
                var seconds = item == null ? 0 : (int)item.Tag;
                _timer.Stop();
                if (seconds > 0)
                {
                    _timer.Interval = TimeSpan.FromSeconds(seconds);
                    _timer.Start();
                }
            };
            AutomationProperties.SetName(interval, "자동 새로 고침");
            buttons.Children.Add(interval);
            var refresh = new Button { Content = "새로 고침", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 6, 0), ToolTip = "보고 있는 섹션을 다시 가져옵니다 (F5)" };
            refresh.Click += (s, e) => Load(_current, true);
            buttons.Children.Add(refresh);
            var help = new Button { Content = "도움말", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 6, 0) };
            help.Click += (s, e) => HelpWindow.Show(_window, helpFolder, HelpLogic.Monitor);
            buttons.Children.Add(help);
            var close = new Button { Content = "닫기", Padding = new Thickness(16, 3, 16, 3), IsCancel = true };
            close.Click += (s, e) => _window.Close();
            buttons.Children.Add(close);
            DockPanel.SetDock(buttons, Dock.Right);
            panel.Children.Add(buttons);

            var title = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            var name = Theme.Text(MonitorLogic.Title);
            name.FontSize = 16;
            name.FontWeight = FontWeights.SemiBold;
            name.Margin = new Thickness(0, 0, 8, 0);
            title.Children.Add(name);
            var badge = Theme.DbBadge(_profile);
            badge.Margin = new Thickness(0, 0, 8, 0);
            title.Children.Add(badge);
            var address = Theme.Text(_provider.DisplayName + " · " + ConnectionManagerLogic.Address(_profile), Theme.SecondaryText);
            address.VerticalAlignment = VerticalAlignment.Center;
            title.Children.Add(address);
            panel.Children.Add(title);
            return panel;
        }

        private FrameworkElement SectionPanel(SectionView view)
        {
            var panel = new DockPanel { Visibility = Visibility.Collapsed };
            var top = new DockPanel { Margin = new Thickness(8, 6, 8, 6) };
            view.Status = Theme.Text("", Theme.SecondaryText);
            view.Status.TextWrapping = TextWrapping.Wrap;
            view.Status.MaxWidth = 420;
            view.Status.TextAlignment = TextAlignment.Right;
            view.Status.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(view.Status, Dock.Right);
            top.Children.Add(view.Status);
            view.Description = Theme.Secondary(view.TreeMode ? view.Tree.Description : view.Section.Description);
            view.Description.TextWrapping = TextWrapping.Wrap;
            view.Description.VerticalAlignment = VerticalAlignment.Center;
            view.Description.Margin = new Thickness(0, 0, 12, 0);
            top.Children.Add(view.Description);
            DockPanel.SetDock(top, Dock.Top);
            panel.Children.Add(top);
            if (view.Tree != null)
            {
                var bar = TreeBar(view);
                DockPanel.SetDock(bar, Dock.Top);
                panel.Children.Add(bar);
            }
            if (view.Section.Chart != null)
            {
                view.Dashboard = new WrapPanel { Margin = new Thickness(8, 0, 8, 6), Visibility = Visibility.Collapsed };
                AutomationProperties.SetName(view.Dashboard, view.Section.Title + " 사용률 차트");
                var dashBorder = new Border { Child = view.Dashboard, BorderThickness = new Thickness(0, 0, 0, 1) };
                dashBorder.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
                DockPanel.SetDock(dashBorder, Dock.Top);
                panel.Children.Add(dashBorder);
            }
            view.Grid = CopyableGrid(() => view.Columns, () => view.Grid);
            foreach (var action in view.Section.Actions)
            {
                var act = action;
                view.Grid.AddMenuAction(act.Title, () => !view.TreeMode, () => RunAction(view, act));
            }
            if (view.Tree != null)
            {
                foreach (var action in view.Tree.Actions)
                {
                    var act = action;
                    view.Grid.AddMenuAction(MonitorLogic.RootActionTitle(act), () => view.TreeMode, () => RunTreeAction(view, act, true));
                    view.Grid.AddMenuAction(act.Title, () => view.TreeMode, () => RunTreeAction(view, act, false));
                }
            }
            if (view.Section.Details.Count == 0)
            {
                panel.Children.Add(view.Grid.View);
                return panel;
            }
            // 표 아래 분할선, 그 아래 상세 영역(행을 누르면 채워진다)
            view.Grid.View.SelectionChanged += (s, e) =>
            {
                var row = view.Grid.SelectedRows().FirstOrDefault();
                if (row == null)
                    return;
                if (view.TreeMode)
                {
                    var treeRow = TreeRowOf(view, row);
                    if (treeRow != null)
                        ShowDetail(view, treeRow.Node.Key);
                }
                else
                {
                    ShowDetail(view, MonitorLogic.KeyOf(view.Columns, row.Values, view.Section.KeyColumn));
                }
            };
            var split = new Grid();
            split.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star), MinHeight = 80 });
            split.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            split.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star), MinHeight = 80 });
            split.Children.Add(view.Grid.View);
            var splitter = new GridSplitter
            {
                Height = 5,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                ResizeDirection = GridResizeDirection.Rows,
                ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                ShowsPreview = false,
                Focusable = false
            };
            Theme.Background(splitter, Theme.SurfaceBackground);
            Grid.SetRow(splitter, 1);
            split.Children.Add(splitter);
            var detail = DetailPanel(view);
            Grid.SetRow(detail, 2);
            split.Children.Add(detail);
            panel.Children.Add(split);
            return panel;
        }

        private static ResultGridView CopyableGrid(Func<List<ResultColumn>> columns, Func<ResultGridView> self)
        {
            return new ResultGridView(all =>
            {
                var grid = self();
                var rows = all ? grid.RowsInViewOrder() : grid.SelectedRows();
                DialogKit.TryCopy(WorkspaceLogic.ToTsv(all ? columns().Select(c => c.Name) : null, rows.Select(r => r.Values)));
            }, text => DialogKit.TryCopy(text));
        }

        // ================= 행 상세 목록 =================

        private FrameworkElement DetailPanel(SectionView view)
        {
            var panel = new DockPanel();
            var top = new DockPanel { Margin = new Thickness(8, 4, 8, 4) };
            view.DetailStatus = Theme.Text("", Theme.SecondaryText);
            view.DetailStatus.VerticalAlignment = VerticalAlignment.Center;
            view.DetailStatus.TextWrapping = TextWrapping.Wrap;
            view.DetailStatus.MaxWidth = 420;
            view.DetailStatus.TextAlignment = TextAlignment.Right;
            DockPanel.SetDock(view.DetailStatus, Dock.Right);
            top.Children.Add(view.DetailStatus);
            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            view.DetailTitle = Theme.Text(MonitorLogic.DetailHintText, Theme.SecondaryText);
            view.DetailTitle.VerticalAlignment = VerticalAlignment.Center;
            view.DetailTitle.Margin = new Thickness(0, 0, 12, 0);
            left.Children.Add(view.DetailTitle);
            if (view.Section.Details.Count > 1)
            {
                for (var i = 0; i < view.Section.Details.Count; i++)
                {
                    var index = i;
                    Border underline;
                    var button = WorkspaceUi.SelectorButton(Theme.Text(view.Section.Details[i].Title), out underline);
                    AutomationProperties.SetName(button, view.Section.Details[i].Title);
                    button.Click += (s, e) =>
                    {
                        view.DetailIndex = index;
                        SelectDetailButton(view);
                        ShowDetail(view, view.DetailKey);
                    };
                    view.DetailButtons.Add(button);
                    view.DetailUnderlines.Add(underline);
                    left.Children.Add(button);
                }
                SelectDetailButton(view);
            }
            top.Children.Add(left);
            var topBorder = new Border { Child = top, BorderThickness = new Thickness(0, 0, 0, 1) };
            topBorder.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
            Theme.Background(topBorder, Theme.SurfaceBackground);
            DockPanel.SetDock(topBorder, Dock.Top);
            panel.Children.Add(topBorder);
            view.DetailGrid = CopyableGrid(() => view.DetailColumns, () => view.DetailGrid);
            view.DetailText = new TextBox
            {
                IsReadOnly = true,
                FontFamily = Theme.Mono,
                FontSize = 12,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6, 4, 6, 4),
                Visibility = Visibility.Collapsed
            };
            Theme.Background(view.DetailText, Theme.PanelBackground);
            Theme.Foreground(view.DetailText);
            AutomationProperties.SetName(view.DetailText, "상세 글");
            var host = new Grid();
            host.Children.Add(view.DetailGrid.View);
            host.Children.Add(view.DetailText);
            panel.Children.Add(host);
            return panel;
        }

        /// <summary>상세가 글(AsText)이면 글 상자, 아니면 표를 보인다.</summary>
        private static void ShowDetailMode(SectionView view, bool asText)
        {
            view.DetailText.Visibility = asText ? Visibility.Visible : Visibility.Collapsed;
            view.DetailGrid.View.Visibility = asText ? Visibility.Collapsed : Visibility.Visible;
        }

        // ================= 행 조치(세션 종료) =================

        /// <summary>표의 오른쪽 메뉴 조치: 고른 행 → 문장 → 확인 대화상자 → 실행 → 섹션 새로 고침.</summary>
        private void RunAction(SectionView view, MonitorAction action)
        {
            var row = view.Grid.SelectedRows().FirstOrDefault();
            if (row == null)
            {
                Dialogs.Show(_window, action.Title.TrimEnd('…'), MonitorLogic.ActionNoRowText, false);
                return;
            }
            RunActionWith(view, action, action.Title, MonitorLogic.ActionValues(view.Columns, row.Values, action), MonitorLogic.ActionBadValueText);
        }

        /// <summary>트리의 오른쪽 메뉴 조치: 고른 줄의 세션(toRoot면 그 사슬의 근원)으로 문장을 만든다.</summary>
        private void RunTreeAction(SectionView view, MonitorAction action, bool toRoot)
        {
            var title = toRoot ? MonitorLogic.RootActionTitle(action) : action.Title;
            var row = view.Grid.SelectedRows().FirstOrDefault();
            var treeRow = row == null ? null : TreeRowOf(view, row);
            if (treeRow == null)
            {
                Dialogs.Show(_window, title.TrimEnd('…'), MonitorLogic.ActionNoRowText, false);
                return;
            }
            var node = toRoot ? treeRow.Root : treeRow.Node;
            RunActionWith(view, action, title, LockTreeLogic.ActionValues(view.Forest, node, action),
                node.Known ? MonitorLogic.ActionBadValueText : MonitorLogic.ActionUnknownSessionText);
        }

        /// <summary>조치 실행: 읽기 전용 차단 → 문장 → 확인 대화상자(문장·경고·확인 체크) → 모니터 세션에서 실행 → 섹션 새로 고침.</summary>
        private async void RunActionWith(SectionView view, MonitorAction action, string title, string[] values, string badValueText)
        {
            try
            {
                if (_closed)
                    return;
                if (_profile != null && _profile.ReadOnly)
                {
                    Dialogs.Show(_window, title.TrimEnd('…'), MonitorLogic.ActionReadOnlyText, true);
                    return;
                }
                var statement = action.Build(values);
                if (statement == null)
                {
                    Dialogs.Show(_window, title.TrimEnd('…'), badValueText, true);
                    return;
                }
                if (!Dialogs.ConfirmDanger(_window, _profile, new SqlStatement { Text = statement, Danger = action.Warning }))
                    return;
                if (_closed)
                    return;
                SetStatus(view, MonitorLogic.LoadingText, null);
                try
                {
                    var session = await EnsureSessionAsync();
                    if (session == null)
                    {
                        SetStatus(view, MonitorLogic.NoSessionText, Theme.Danger);
                        return;
                    }
                    await session.RunAsync((connection, transaction) =>
                    {
                        using (var command = connection.CreateCommand())
                        {
                            command.Transaction = transaction;
                            command.CommandText = statement;
                            return command.ExecuteNonQuery();
                        }
                    }, _closing.Token);
                    if (_closed)
                        return;
                    SetStatus(view, MonitorLogic.ActionDoneText(statement), null);
                    Load(view.Index, true);
                }
                catch (OperationCanceledException)
                {
                    // 창을 닫았다
                }
                catch (Exception ex)
                {
                    if (!_closed)
                        SetStatus(view, MonitorLogic.FailedText(DbSession.DescribeError(ex)), Theme.Danger);
                }
            }
            catch (Exception)
            {
                // async void — 예외를 밖으로 내보내면 Folderss가 멈춘다
            }
        }

        /// <summary>지금 보기의 상세 목록: 트리면 트리 상세(키 = SESSION_KEY), 아니면 섹션 상세. 버튼은 섹션 상세의 제목으로 만든다(같은 제목·순서).</summary>
        private static IReadOnlyList<MonitorDetail> CurrentDetails(SectionView view)
        {
            return view.TreeMode ? view.Tree.Details : view.Section.Details;
        }

        private static void SelectDetailButton(SectionView view)
        {
            for (var i = 0; i < view.DetailButtons.Count; i++)
                WorkspaceUi.SetSelected(view.DetailButtons[i], view.DetailUnderlines[i], i == view.DetailIndex);
        }

        /// <summary>키(행의 키 열 값)로 지금 고른 상세 목록을 가져온다. 진행 중인 상세 조회는 취소한다.</summary>
        private async void ShowDetail(SectionView view, string key)
        {
            try
            {
                var details = CurrentDetails(view);
                if (_closed || view.DetailGrid == null || view.DetailIndex >= details.Count)
                    return;
                var detail = details[view.DetailIndex];
                view.DetailKey = key;
                if (view.DetailWork != null)
                    view.DetailWork.Cancel();
                var work = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
                view.DetailWork = work;
                view.DetailTitle.Text = MonitorLogic.DetailTitleText(detail, key);
                view.DetailTitle.SetResourceReference(TextBlock.ForegroundProperty, Theme.PrimaryText);
                view.DetailTitle.ToolTip = detail.Description;
                ShowDetailMode(view, detail.AsText);
                if (string.IsNullOrEmpty(key))
                {
                    view.DetailColumns = new List<ResultColumn>();
                    view.DetailGrid.Show(view.DetailColumns, new ObservableCollection<ResultGridRow>());
                    view.DetailText.Text = "";
                    SetDetailStatus(view, "", null);
                    return;
                }
                SetDetailStatus(view, MonitorLogic.LoadingText, null);
                try
                {
                    // 같은 행·같은 상세를 다시 누르면 다시 조회하지 않는다(DBA_SEGMENTS 같은 무거운 조회). 섹션을 새로 고치면 비운다
                    var cacheKey = MonitorLogic.DetailCacheKey(detail, key);
                    QueryTable table;
                    var cached = view.DetailCache.TryGetValue(cacheKey, out table);
                    var watch = Stopwatch.StartNew();
                    if (!cached)
                    {
                        var session = await EnsureSessionAsync();
                        if (session == null)
                        {
                            SetDetailStatus(view, MonitorLogic.NoSessionText, Theme.Danger);
                            return;
                        }
                        table = new QueryTable();
                        var read = table;
                        var full = detail.AsText;
                        var rows = await session.QueryAsync(detail.Query(key), record => ReadRow(record, read, full), work.Token);
                        if (_closed || work.IsCancellationRequested)
                            return;
                        table.Rows.AddRange(rows);
                        view.DetailCache[cacheKey] = table;
                    }
                    if (detail.AsText)
                    {
                        view.DetailText.Text = MonitorLogic.DetailText(table);
                        view.DetailText.ScrollToHome();
                    }
                    else
                    {
                        view.DetailColumns = MonitorLogic.Columns(table);
                        var gridRows = new ObservableCollection<ResultGridRow>();
                        for (var i = 0; i < table.Rows.Count; i++)
                            gridRows.Add(new ResultGridRow(i + 1, table.Rows[i]));
                        view.DetailGrid.Show(view.DetailColumns, gridRows);
                    }
                    SetDetailStatus(view, MonitorLogic.DetailRowsText(table.Rows.Count) + (cached ? MonitorLogic.CachedSuffix : MonitorLogic.ElapsedSuffix(watch.Elapsed)), null);
                }
                catch (OperationCanceledException)
                {
                    // 다른 행을 골랐거나 창을 닫았다
                }
                catch (Exception ex)
                {
                    if (_closed || work.IsCancellationRequested)
                        return;
                    var reason = DbSession.DescribeError(ex);
                    SetDetailStatus(view, _provider.IsPermissionDenied(ex) ? MonitorLogic.PermissionDeniedText(view.Section, reason) : MonitorLogic.FailedText(reason), Theme.Danger);
                }
            }
            catch (Exception)
            {
                // async void — 예외를 밖으로 내보내면 Folderss가 멈춘다
            }
        }

        /// <summary>
        /// 상세 조회의 한 행을 글자로. 첫 행에서 열 이름을 받아 둔다(행이 없으면 열도 없다).
        /// fullText면 글자 값을 자르지 않는다(SQL 전문·실행 계획 — 표의 4,000자 제한을 받지 않게).
        /// </summary>
        private static string[] ReadRow(IDataRecord record, QueryTable table, bool fullText)
        {
            var count = record.FieldCount;
            if (table.Columns.Count == 0)
                for (var i = 0; i < count; i++)
                    table.Columns.Add(record.GetName(i));
            var values = new string[count];
            for (var i = 0; i < count; i++)
            {
                if (record.IsDBNull(i))
                    continue;
                var value = record.GetValue(i);
                var text = value as string;
                values[i] = fullText && text != null ? text : ValueFormatter.Format(value);
            }
            return values;
        }

        private static void SetDetailStatus(SectionView view, string text, Brush color)
        {
            view.DetailStatus.Text = text ?? "";
            if (color != null)
                view.DetailStatus.Foreground = color;
            else
                view.DetailStatus.SetResourceReference(TextBlock.ForegroundProperty, Theme.SecondaryText);
        }

        private void Select(int index)
        {
            if (_closed || index < 0 || index >= _sections.Count)
                return;
            _current = index;
            for (var i = 0; i < _sections.Count; i++)
            {
                _sections[i].Panel.Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
                WorkspaceUi.SetSelected(_sections[i].Button, _sections[i].Underline, i == index);
            }
            Load(index, false);
        }

        // ================= 가져오기 =================

        /// <summary>섹션 하나를 가져온다. force가 아니면 이미 가져온 섹션은 그대로 둔다. 가져오는 중이면 겹쳐 실행하지 않는다.</summary>
        private async void Load(int index, bool force)
        {
            try
            {
                if (_closed || index < 0 || index >= _sections.Count)
                    return;
                var view = _sections[index];
                if (view.Loading || (view.Loaded && !force))
                    return;
                view.Loading = true;
                // 가져오는 동안 보기를 바꾸면 이전 보기의 결과가 새 보기에 그려진다 — 끝날 때까지 전환을 막는다
                if (view.ModeButton != null)
                    view.ModeButton.IsEnabled = false;
                SetStatus(view, MonitorLogic.LoadingText, null);
                try
                {
                    var session = await EnsureSessionAsync();
                    if (session == null)
                    {
                        SetStatus(view, MonitorLogic.NoSessionText, Theme.Danger);
                        return;
                    }
                    var watch = Stopwatch.StartNew();
                    if (view.TreeMode)
                    {
                        var treeTable = await session.QueryTableAsync(view.Tree.Sql, MonitorLogic.MaxRows, _closing.Token);
                        if (_closed)
                            return;
                        view.TreeTable = treeTable;
                        ShowTree(view);
                        view.Loaded = true;
                        view.DetailCache.Clear();
                        SetStatus(view, MonitorLogic.RefreshedText(DateTime.Now, treeTable.Rows.Count) + MonitorLogic.ElapsedSuffix(watch.Elapsed), null);
                        return;
                    }
                    // 필요 조건(확장 설치·버전)을 먼저 본다 — 충족되지 않으면 조회하지 않고 설치 안내(경고색, 오류 아님)
                    foreach (var requirement in view.Section.Requirements)
                    {
                        var check = await session.QueryTableAsync(requirement.Sql, 1, _closing.Token);
                        if (_closed)
                            return;
                        if (!MonitorLogic.RequirementMet(check))
                        {
                            view.Columns = new List<ResultColumn>();
                            view.Grid.Show(view.Columns, new ObservableCollection<ResultGridRow>());
                            view.Loaded = true;
                            // 이 PC의 서버면 사용자가 직접 설치할 수 있다 — 절차·링크를 메시지 탭에 한 번만 남긴다
                            var guide = MonitorLogic.InstallGuideMessage(view.Section, requirement);
                            var logged = guide != null && _profile != null && MonitorLogic.IsLocalHost(_profile.Host);
                            if (logged && !view.GuideLogged.Contains(requirement.Sql))
                            {
                                view.GuideLogged.Add(requirement.Sql);
                                _log(guide);
                            }
                            SetStatus(view, MonitorLogic.RequirementMissingText(requirement, logged), Theme.Warning);
                            return;
                        }
                    }
                    var table = await session.QueryTableAsync(view.Section.Sql, MonitorLogic.MaxRows, _closing.Token);
                    if (_closed)
                        return;
                    view.Columns = MonitorLogic.Columns(table);
                    var rows = new ObservableCollection<ResultGridRow>();
                    for (var i = 0; i < table.Rows.Count; i++)
                        rows.Add(new ResultGridRow(i + 1, table.Rows[i]));
                    view.Grid.Show(view.Columns, rows);
                    ShowDashboard(view, table);
                    view.Loaded = true;
                    view.DetailCache.Clear();
                    SetStatus(view, MonitorLogic.RefreshedText(DateTime.Now, table.Rows.Count) + MonitorLogic.ElapsedSuffix(watch.Elapsed), null);
                }
                catch (OperationCanceledException)
                {
                    // 창을 닫았다
                }
                catch (Exception ex)
                {
                    if (_closed)
                        return;
                    var reason = DbSession.DescribeError(ex);
                    SetStatus(view, _provider.IsPermissionDenied(ex) ? MonitorLogic.PermissionDeniedText(view.Section, reason) : MonitorLogic.FailedText(reason), Theme.Danger);
                }
                finally
                {
                    view.Loading = false;
                    if (view.ModeButton != null)
                        view.ModeButton.IsEnabled = true;
                }
            }
            catch (Exception)
            {
                // async void — 예외를 밖으로 내보내면 Folderss가 멈춘다
            }
        }

        // ================= 잠금 연쇄 트리 =================

        /// <summary>[잠금] 섹션 위 막대: 왼쪽 요약("근원 2 · 막힌 세션 5 · 최장 대기 …"), 오른쪽 최소 대기 · [표로 보기]/[트리로 보기].</summary>
        private FrameworkElement TreeBar(SectionView view)
        {
            var bar = new DockPanel { Margin = new Thickness(8, 0, 8, 6) };
            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            view.MinWaitLabel = Theme.Text("최소 대기", Theme.SecondaryText);
            view.MinWaitLabel.VerticalAlignment = VerticalAlignment.Center;
            view.MinWaitLabel.Margin = new Thickness(0, 0, 6, 0);
            right.Children.Add(view.MinWaitLabel);
            view.MinWaitBox = new ComboBox { MinWidth = 64, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), ToolTip = MonitorLogic.MinWaitToolTip };
            foreach (var seconds in LockTreeLogic.MinWaitChoices)
            {
                view.MinWaitBox.Items.Add(new ComboBoxItem { Content = LockTreeLogic.MinWaitText(seconds), Tag = seconds });
                if (seconds == view.MinWaitSeconds)
                    view.MinWaitBox.SelectedIndex = view.MinWaitBox.Items.Count - 1;
            }
            view.MinWaitBox.SelectionChanged += (s, e) =>
            {
                var item = view.MinWaitBox.SelectedItem as ComboBoxItem;
                view.MinWaitSeconds = item == null ? 0 : (int)item.Tag;
                // 다시 조회하지 않고 가져온 결과로 다시 그린다
                if (view.TreeMode && view.TreeTable != null)
                    ShowTree(view);
            };
            AutomationProperties.SetName(view.MinWaitBox, "최소 대기");
            right.Children.Add(view.MinWaitBox);
            view.ModeButton = new Button { Padding = new Thickness(12, 3, 12, 3) };
            view.ModeButton.Click += (s, e) => ToggleTree(view);
            right.Children.Add(view.ModeButton);
            DockPanel.SetDock(right, Dock.Right);
            bar.Children.Add(right);
            view.TreeSummary = Theme.Text("", Theme.SecondaryText);
            view.TreeSummary.VerticalAlignment = VerticalAlignment.Center;
            view.TreeSummary.FontWeight = FontWeights.SemiBold;
            view.TreeSummary.TextWrapping = TextWrapping.Wrap;
            bar.Children.Add(view.TreeSummary);
            ApplyTreeMode(view);
            return bar;
        }

        /// <summary>보기에 맞춰 설명·버튼 글·트리 전용 조각(요약·최소 대기)을 보이거나 숨긴다.</summary>
        private static void ApplyTreeMode(SectionView view)
        {
            view.Description.Text = view.TreeMode ? view.Tree.Description : view.Section.Description;
            view.ModeButton.Content = view.TreeMode ? MonitorLogic.TableViewText : MonitorLogic.TreeViewText;
            view.ModeButton.ToolTip = view.TreeMode ? MonitorLogic.TableViewToolTip : MonitorLogic.TreeViewToolTip;
            var visibility = view.TreeMode ? Visibility.Visible : Visibility.Collapsed;
            view.TreeSummary.Visibility = visibility;
            view.MinWaitLabel.Visibility = visibility;
            view.MinWaitBox.Visibility = visibility;
        }

        /// <summary>트리 ↔ 표 전환: 표와 상세를 비우고 그 보기로 다시 가져온다(가져오는 중에는 버튼이 꺼져 있다).</summary>
        private void ToggleTree(SectionView view)
        {
            if (_closed || view.Loading)
                return;
            view.TreeMode = !view.TreeMode;
            ApplyTreeMode(view);
            view.TreeTable = null;
            view.Forest = null;
            view.Columns = new List<ResultColumn>();
            view.Grid.Show(view.Columns, new ObservableCollection<ResultGridRow>());
            view.DetailCache.Clear();
            ShowDetail(view, null);
            view.Loaded = false;
            Load(view.Index, true);
        }

        /// <summary>가져온 트리 결과를 최소 대기로 걸러 표로 그리고 요약을 고친다(근원·순환이 있으면 경고색).</summary>
        private static void ShowTree(SectionView view)
        {
            view.Forest = LockTreeLogic.Build(view.TreeTable, view.MinWaitSeconds);
            var table = LockTreeLogic.ToTable(view.Forest);
            view.Columns = MonitorLogic.Columns(table);
            var rows = new ObservableCollection<ResultGridRow>();
            for (var i = 0; i < table.Rows.Count; i++)
                rows.Add(new ResultGridRow(i + 1, table.Rows[i]));
            view.Grid.Show(view.Columns, rows);
            view.TreeSummary.Text = LockTreeLogic.SummaryText(view.Forest, view.MinWaitSeconds);
            if (view.Forest.RootCount > 0 || view.Forest.HasCycle)
                view.TreeSummary.Foreground = Theme.Warning;
            else
                view.TreeSummary.SetResourceReference(TextBlock.ForegroundProperty, Theme.SecondaryText);
        }

        /// <summary>표의 행 → 트리 줄(행 번호 = 트리 줄 순서 + 1 — 열 머리로 정렬해도 번호는 따라간다).</summary>
        private static LockTreeRow TreeRowOf(SectionView view, ResultGridRow row)
        {
            var forest = view.Forest;
            if (forest == null || row == null)
                return null;
            var index = row.Number - 1;
            return index >= 0 && index < forest.Rows.Count ? forest.Rows[index] : null;
        }

        // ================= 대시보드(사용률 도넛) =================

        private void ShowDashboard(SectionView view, QueryTable table)
        {
            if (view.Dashboard == null)
                return;
            view.Dashboard.Children.Clear();
            var slices = MonitorLogic.Usage(table, view.Section.Chart, MonitorLogic.MaxCharts);
            if (slices.Count == 0)
            {
                view.Dashboard.Visibility = Visibility.Collapsed;
                return;
            }
            foreach (var slice in slices)
            {
                var donut = Donut(slice);
                if (view.Section.Details.Count > 0)
                {
                    var key = slice.Name;
                    donut.Cursor = Cursors.Hand;
                    donut.MouseLeftButtonUp += (s, e) => ShowDetail(view, key);
                }
                view.Dashboard.Children.Add(donut);
            }
            var more = MonitorLogic.MoreChartsText(table.Rows.Count, slices.Count);
            if (more != null)
            {
                var note = Theme.Secondary(more);
                note.VerticalAlignment = VerticalAlignment.Center;
                note.Margin = new Thickness(8, 0, 0, 0);
                view.Dashboard.Children.Add(note);
            }
            view.Dashboard.Visibility = Visibility.Visible;
        }

        private const double DonutSize = 64;
        private const double DonutThickness = 9;

        /// <summary>도넛 하나: 바탕 고리(테두리색) 위에 사용 호(단계별 색), 가운데 "93%", 아래 이름과 "사용 / 전체".</summary>
        private static FrameworkElement Donut(UsageSlice slice)
        {
            var ring = new Grid { Width = DonutSize, Height = DonutSize };
            var inner = DonutSize - DonutThickness;
            var track = new Ellipse { Width = inner, Height = inner, StrokeThickness = DonutThickness, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            track.SetResourceReference(Shape.StrokeProperty, Theme.Border);
            ring.Children.Add(track);

            var level = MonitorLogic.UsageLevel(slice.Percent);
            Shape used;
            if (slice.Percent >= 99.95)
            {
                used = new Ellipse { Width = inner, Height = inner, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            }
            else
            {
                used = new Path { Data = Arc(slice.Percent) };
            }
            used.StrokeThickness = DonutThickness;
            if (level == 2)
                used.Stroke = Theme.Danger;
            else if (level == 1)
                used.Stroke = Theme.Warning;
            else
                used.SetResourceReference(Shape.StrokeProperty, Theme.Accent);
            if (slice.Percent > 0)
                ring.Children.Add(used);

            var percent = Theme.Text(MonitorLogic.PercentText(slice.Percent));
            percent.FontSize = 12;
            percent.FontWeight = FontWeights.SemiBold;
            percent.HorizontalAlignment = HorizontalAlignment.Center;
            percent.VerticalAlignment = VerticalAlignment.Center;
            if (level == 2)
                percent.Foreground = Theme.Danger;
            else if (level == 1)
                percent.Foreground = Theme.Warning;
            ring.Children.Add(percent);

            var panel = new StackPanel { Width = 104, Margin = new Thickness(0, 6, 6, 4) };
            panel.Children.Add(ring);
            var name = Theme.Text(slice.Name);
            name.FontSize = 11.5;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.TextAlignment = TextAlignment.Center;
            name.Margin = new Thickness(0, 4, 0, 0);
            name.ToolTip = slice.Name;
            panel.Children.Add(name);
            var text = Theme.Secondary(MonitorLogic.UsageText(slice));
            text.FontSize = 10.5;
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            text.TextAlignment = TextAlignment.Center;
            panel.Children.Add(text);
            panel.ToolTip = slice.Name + " — " + MonitorLogic.UsageText(slice) + " (" + MonitorLogic.PercentText(slice.Percent) + ")";
            return panel;
        }

        /// <summary>12시 방향에서 시계 방향으로 percent만큼 도는 호(고리 중심선 반지름).</summary>
        private static Geometry Arc(double percent)
        {
            var radius = (DonutSize - DonutThickness) / 2;
            var center = DonutSize / 2;
            var angle = Math.Max(0, Math.Min(99.95, percent)) / 100 * 2 * Math.PI;
            var start = new Point(center, center - radius);
            var end = new Point(center + radius * Math.Sin(angle), center - radius * Math.Cos(angle));
            var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
            figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, angle > Math.PI, SweepDirection.Clockwise, true));
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            geometry.Freeze();
            return geometry;
        }

        // ================= 모니터 전용 세션 =================

        /// <summary>
        /// 모니터 전용 세션: 없거나 끊겼으면 연다(여러 섹션이 동시에 불러도 한 번만 연다). 비밀번호를 묻다 취소하면 null.
        /// 연결 오류는 예외로 올려 부른 쪽이 그 섹션 상태에 보인다. 여는 동안 창이 닫혔으면 바로 정리하고 null.
        /// </summary>
        private async Task<DbSession> EnsureSessionAsync()
        {
            var current = _session;
            if (current != null && current.IsOpen && !current.IsBroken)
                return current;
            if (current != null)
            {
                _session = null;
                DisposeQuietly(current);
            }
            if (_closed)
                return null;
            if (_connecting == null)
                _connecting = _connect();
            var connecting = _connecting;
            try
            {
                var opened = await connecting;
                if (_closed)
                {
                    DisposeQuietly(opened);
                    return null;
                }
                if (opened != null && _session == null)
                    _session = opened;
                return _session;
            }
            finally
            {
                if (ReferenceEquals(_connecting, connecting))
                    _connecting = null;
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

        /// <summary>상태 글. color가 null이면 보조 글자색, 아니면 그 색(오류 Danger · 설치 필요 Warning).</summary>
        private static void SetStatus(SectionView view, string text, Brush color)
        {
            view.Status.Text = text ?? "";
            if (color != null)
                view.Status.Foreground = color;
            else
                view.Status.SetResourceReference(TextBlock.ForegroundProperty, Theme.SecondaryText);
        }

        /// <summary>섹션 하나의 화면 조각과 상태.</summary>
        private sealed class SectionView
        {
            public SectionView(MonitorSection section, int index)
            {
                Section = section;
                Index = index;
            }

            public MonitorSection Section { get; }
            public int Index { get; }
            public Button Button { get; set; }
            public Border Underline { get; set; }
            public FrameworkElement Panel { get; set; }
            public TextBlock Status { get; set; }
            public ResultGridView Grid { get; set; }
            /// <summary>사용률 도넛 줄(섹션에 Chart가 있을 때만). 조각이 없으면 숨긴다.</summary>
            public WrapPanel Dashboard { get; set; }
            // 행 상세 영역(섹션에 Details가 있을 때만)
            public ResultGridView DetailGrid { get; set; }
            public List<ResultColumn> DetailColumns { get; set; } = new List<ResultColumn>();
            public TextBox DetailText { get; set; }
            public TextBlock DetailTitle { get; set; }
            public TextBlock DetailStatus { get; set; }
            public List<Button> DetailButtons { get; } = new List<Button>();
            public List<Border> DetailUnderlines { get; } = new List<Border>();
            public int DetailIndex { get; set; }
            public string DetailKey { get; set; }
            public CancellationTokenSource DetailWork { get; set; }
            /// <summary>메시지 탭에 설치 안내를 이미 남긴 필요 조건(점검 SQL로 구분) — 새로 고침마다 되풀이하지 않게.</summary>
            public HashSet<string> GuideLogged { get; } = new HashSet<string>(StringComparer.Ordinal);
            /// <summary>상세 결과 캐시(상세 id + 키). 섹션을 새로 가져오면 비운다.</summary>
            public Dictionary<string, QueryTable> DetailCache { get; } = new Dictionary<string, QueryTable>(StringComparer.Ordinal);
            public List<ResultColumn> Columns { get; set; } = new List<ResultColumn>();
            public bool Loaded { get; set; }
            public bool Loading { get; set; }
            public TextBlock Description { get; set; }
            // 잠금 연쇄 트리([잠금] 섹션이고 공급자가 LockTree를 줄 때만)
            public MonitorLockTree Tree { get; set; }
            /// <summary>트리 보기면 true(기본), [표로 보기]면 false. 트리가 없는 섹션은 늘 false.</summary>
            public bool TreeMode { get; set; }
            /// <summary>마지막으로 가져온 트리 조회 결과(최소 대기를 바꾸면 다시 조회하지 않고 이것으로 다시 그린다).</summary>
            public QueryTable TreeTable { get; set; }
            /// <summary>표에 그린 트리(행 번호 - 1 = 줄 위치).</summary>
            public LockForest Forest { get; set; }
            public int MinWaitSeconds { get; set; } = LockTreeLogic.DefaultMinWaitSeconds;
            public TextBlock TreeSummary { get; set; }
            public TextBlock MinWaitLabel { get; set; }
            public ComboBox MinWaitBox { get; set; }
            public Button ModeButton { get; set; }
        }
    }
}

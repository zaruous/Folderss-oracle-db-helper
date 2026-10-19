using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Linq;
using System.Threading;
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
    /// - 세션은 그 DB의 지금 세션을 그때그때 받는다(끊었다 다시 연결해도 창을 다시 열 필요가 없다). 그 DB에서 사용자 문장이 실행 중이면 기다리지 않고 알린다.
    /// - 권한 오류(<see cref="IDbProvider.IsPermissionDenied"/>)는 섹션의 필요 권한과 함께 보인다. 섹션마다 따로 실패한다.
    /// - 섹션에 <see cref="MonitorSection.Chart"/>가 있으면(테이블스페이스·데이터 파일) 표 위 대시보드에 행마다 사용률 도넛을 그린다
    ///   (80% 이상 경고색, 90% 이상 위험색, 최대 12개 — 나머지는 "외 N개는 표에서"). 코드로 그리는 Path라 차트 라이브러리는 없다.
    /// - 섹션에 <see cref="MonitorSection.Details"/>가 있으면 표 아래 분할선 밑에 상세 영역: 표의 행(또는 도넛)을 누르면 그 행의 키
    ///   (<see cref="MonitorSection.KeyColumn"/>)로 상세 조회(바인드 하나)를 실행해 보인다. 상세가 여럿이면 고르는 버튼. 이전 상세 조회는 취소한다.
    ///   <see cref="MonitorDetail.AsText"/>인 상세(SQL 전문·실행 계획)는 표 대신 고정폭 읽기 전용 글 상자.
    /// - <see cref="MonitorSection.Actions"/>(세션 종료)는 표의 오른쪽 메뉴에 둔다: 고른 행의 값으로 문장을 만들고 위험한 문장 확인 대화상자
    ///   (문장·경고·확인 체크)를 거쳐 실행한 뒤 섹션을 다시 가져온다. 읽기 전용 접속이면 막는다.
    /// </summary>
    internal sealed class MonitorWindow
    {
        private readonly Window _window;
        private readonly IDbProvider _provider;
        private readonly OracleConnectionProfile _profile;
        private readonly Func<DbSession> _session;
        private readonly Func<bool> _isRunning;
        private readonly List<SectionView> _sections = new List<SectionView>();
        private readonly CancellationTokenSource _closing = new CancellationTokenSource();
        private readonly DispatcherTimer _timer = new DispatcherTimer();
        private int _current = -1;
        private bool _closed;

        private MonitorWindow(Window owner, OracleConnectionProfile profile, IDbProvider provider, Func<DbSession> session, Func<bool> isRunning, string helpFolder)
        {
            _profile = profile;
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _isRunning = isRunning ?? (() => false);
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
            _window.Loaded += (s, e) => Select(0);
            _window.Closed += (s, e) =>
            {
                _closed = true;
                _timer.Stop();
                _closing.Cancel();
            };
            _timer.Tick += (s, e) => Load(_current, true);
        }

        /// <summary>창을 만들어 보인다. 공급자에 섹션이 없으면 InvalidOperationException(부르는 쪽이 먼저 확인한다).</summary>
        public static MonitorWindow Show(Window owner, OracleConnectionProfile profile, IDbProvider provider, Func<DbSession> session, Func<bool> isRunning, string helpFolder)
        {
            if (provider == null)
                throw new ArgumentNullException(nameof(provider));
            if (provider.Monitor.Sections.Count == 0)
                throw new InvalidOperationException(MonitorLogic.NotSupportedMessage(provider.DisplayName));
            var monitor = new MonitorWindow(owner, profile, provider, session, isRunning, helpFolder);
            monitor._window.Show();
            return monitor;
        }

        /// <summary>창이 닫혔을 때(DbHelperView가 DB별 창 목록에서 뺀다).</summary>
        public event EventHandler Closed
        {
            add { _window.Closed += value; }
            remove { _window.Closed -= value; }
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
            var description = Theme.Secondary(view.Section.Description);
            description.TextWrapping = TextWrapping.Wrap;
            description.VerticalAlignment = VerticalAlignment.Center;
            description.Margin = new Thickness(0, 0, 12, 0);
            top.Children.Add(description);
            DockPanel.SetDock(top, Dock.Top);
            panel.Children.Add(top);
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
                view.Grid.AddMenuAction(act.Title, () => true, () => RunAction(view, act));
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
                if (row != null)
                    ShowDetail(view, MonitorLogic.KeyOf(view.Columns, row.Values, view.Section.KeyColumn));
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
        private async void RunAction(SectionView view, MonitorAction action)
        {
            try
            {
                if (_closed)
                    return;
                var row = view.Grid.SelectedRows().FirstOrDefault();
                if (row == null)
                {
                    Dialogs.Show(_window, action.Title.TrimEnd('…'), MonitorLogic.ActionNoRowText, false);
                    return;
                }
                if (_profile != null && _profile.ReadOnly)
                {
                    Dialogs.Show(_window, action.Title.TrimEnd('…'), MonitorLogic.ActionReadOnlyText, true);
                    return;
                }
                var statement = action.Build(MonitorLogic.ActionValues(view.Columns, row.Values, action));
                if (statement == null)
                {
                    Dialogs.Show(_window, action.Title.TrimEnd('…'), MonitorLogic.ActionBadValueText, true);
                    return;
                }
                var session = _session();
                if (session == null || !session.IsOpen || session.IsBroken)
                {
                    SetStatus(view, MonitorLogic.NoSessionText, true);
                    return;
                }
                if (!Dialogs.ConfirmDanger(_window, _profile, new SqlStatement { Text = statement, Danger = action.Warning }))
                    return;
                if (_closed)
                    return;
                if (_isRunning())
                {
                    SetStatus(view, MonitorLogic.BusyText(_profile != null ? _profile.Name : ""), true);
                    return;
                }
                SetStatus(view, MonitorLogic.LoadingText, false);
                try
                {
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
                    SetStatus(view, MonitorLogic.ActionDoneText(statement), false);
                    Load(view.Index, true);
                }
                catch (OperationCanceledException)
                {
                    // 창을 닫았다
                }
                catch (Exception ex)
                {
                    if (!_closed)
                        SetStatus(view, MonitorLogic.FailedText(DbSession.DescribeError(ex)), true);
                }
            }
            catch (Exception)
            {
                // async void — 예외를 밖으로 내보내면 Folderss가 멈춘다
            }
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
                if (_closed || view.DetailGrid == null || view.DetailIndex >= view.Section.Details.Count)
                    return;
                var detail = view.Section.Details[view.DetailIndex];
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
                    SetDetailStatus(view, "", false);
                    return;
                }
                var session = _session();
                if (session == null || !session.IsOpen || session.IsBroken)
                {
                    SetDetailStatus(view, MonitorLogic.NoSessionText, true);
                    return;
                }
                if (_isRunning())
                {
                    SetDetailStatus(view, MonitorLogic.BusyText(_profile != null ? _profile.Name : ""), true);
                    return;
                }
                SetDetailStatus(view, MonitorLogic.LoadingText, false);
                try
                {
                    var table = new QueryTable();
                    var full = detail.AsText;
                    var rows = await session.QueryAsync(detail.Query(key), record => ReadRow(record, table, full), work.Token);
                    if (_closed || work.IsCancellationRequested)
                        return;
                    table.Rows.AddRange(rows);
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
                    SetDetailStatus(view, MonitorLogic.DetailRowsText(table.Rows.Count), false);
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
                    SetDetailStatus(view, _provider.IsPermissionDenied(ex) ? MonitorLogic.PermissionDeniedText(view.Section, reason) : MonitorLogic.FailedText(reason), true);
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

        private static void SetDetailStatus(SectionView view, string text, bool error)
        {
            view.DetailStatus.Text = text ?? "";
            if (error)
                view.DetailStatus.Foreground = Theme.Danger;
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
                var session = _session();
                if (session == null || !session.IsOpen || session.IsBroken)
                {
                    SetStatus(view, MonitorLogic.NoSessionText, true);
                    return;
                }
                if (_isRunning())
                {
                    SetStatus(view, MonitorLogic.BusyText(_profile != null ? _profile.Name : ""), true);
                    return;
                }
                view.Loading = true;
                SetStatus(view, MonitorLogic.LoadingText, false);
                try
                {
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
                    SetStatus(view, MonitorLogic.RefreshedText(DateTime.Now, table.Rows.Count), false);
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
                    SetStatus(view, _provider.IsPermissionDenied(ex) ? MonitorLogic.PermissionDeniedText(view.Section, reason) : MonitorLogic.FailedText(reason), true);
                }
                finally
                {
                    view.Loading = false;
                }
            }
            catch (Exception)
            {
                // async void — 예외를 밖으로 내보내면 Folderss가 멈춘다
            }
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

        private static void SetStatus(SectionView view, string text, bool error)
        {
            view.Status.Text = text ?? "";
            if (error)
                view.Status.Foreground = Theme.Danger;
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
            public List<ResultColumn> Columns { get; set; } = new List<ResultColumn>();
            public bool Loaded { get; set; }
            public bool Loading { get; set; }
        }
    }
}

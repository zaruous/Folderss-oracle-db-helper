using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MyPlugin
{
    internal enum MessageKind { Info, Success, Error }

    /// <summary>SqlWorkspace가 셸(DbHelperView)에게 받는 기능. 모든 호출은 UI 스레드에서.</summary>
    internal interface IDbHost
    {
        /// <summary>현재 접속 목록(프로필 순서).</summary>
        IReadOnlyList<OracleConnectionProfile> Profiles { get; }

        /// <summary>Id로 접속을 찾는다. 없으면 null.</summary>
        OracleConnectionProfile FindProfile(string dbId);

        /// <summary>연결된 세션. 연결 안 됐으면 null.</summary>
        DbSession GetSession(string dbId);

        /// <summary>연결한다(필요하면 비밀번호를 묻는다). 연결되면 true. 이미 연결돼 있으면 바로 true.
        /// 세션이 끊긴 상태(IsBroken)면 그 세션을 버리고 다시 연결한다(이때 커밋 대기 변경은 이미 서버에서 사라졌다고 안내).</summary>
        Task<bool> ConnectAsync(string dbId);

        /// <summary>트리에서 고른 DB(새 탭의 대상). 없으면 null.</summary>
        string SelectedDbId { get; }

        /// <summary>툴바의 "가져올 행"(200/1000/5000).</summary>
        int FetchCount { get; }

        /// <summary>실행 시작·끝, 커밋 대기 변경 등으로 그 DB의 표시(툴바·트리 배지)를 새로 그려야 할 때.</summary>
        void StateChanged(string dbId);

        /// <summary>플러그인 데이터 폴더(SQL 임시 저장 위치). 쓸 수 없으면 null.</summary>
        string DataDirectory { get; }

        /// <summary>SQL 파일을 열거나 저장한 마지막 폴더(열기·저장 창의 처음 위치). 없으면 null.</summary>
        string LastSqlFolder { get; set; }

        /// <summary>최근에 열거나 저장한 SQL 파일을 맨 앞에 넣는다.</summary>
        void AddRecentSqlFile(string path);
    }

    /// <summary>
    /// SQL 탭들(탭마다 대상 DB) + 편집기 + 결과(그리드·메시지·기록) + 상태줄. PoC의 오른쪽 영역과 같은 동작.
    /// 화면은 코드로 만든다(XAML 없음). 색은 Theme 도우미로 테마 키에 연결한다.
    /// </summary>
    internal sealed partial class SqlWorkspace : Grid
    {
        private const double EditorFontSize = 13;
        // 편집기와 줄 번호 칸의 줄 높이를 고정해 맞춘다(한글처럼 대체 글꼴로 그리는 줄도 같은 높이가 된다)
        private const double EditorLineHeight = 19;
        private const string FirstTabText = "-- 커서가 있는 문장(빈 줄이나 ;로 구분)이나 선택한 부분을 Ctrl+Enter로 실행합니다.\n"
            + "-- PL/SQL 블록(BEGIN·DECLARE·CREATE PROCEDURE 등)은 끝에 / 만 있는 줄을 두세요(블록 안의 빈 줄·;로는 끝나지 않음).\n";

        private static readonly Thickness EditorPadding = new Thickness(6, 4, 6, 4);

        private readonly IDbHost _host;
        private readonly List<SqlTabState> _tabs = new List<SqlTabState>();
        // 세션이 바빠 바로 닫지 못한 커서(탭 닫기·대상 변경). 그 세션의 실행이 끝나면 다시 닫는다.
        private readonly List<KeyValuePair<DbSession, QueryCursor>> _orphanCursors = new List<KeyValuePair<DbSession, QueryCursor>>();
        private readonly DispatcherTimer _elapsedTimer;
        private readonly DispatcherTimer _stateTimer;

        private SqlTabState _active;
        private int _nextTabNumber = 1;
        private WorkspacePane _pane = WorkspacePane.Grid;
        private bool _fillingTarget;
        private int _gutterLines = -1;
        private string _stateSignature;
        private bool _stateTimerFailed;

        // SQL 임시 저장: 지금 열린 DB Helper 창들의 파일 Id(같은 프로세스의 창끼리 파일을 덮어쓰거나 넘겨받지 않게. 모두 UI 스레드지만 잠근다)
        private static readonly HashSet<string> LiveDraftOwners = new HashSet<string>(StringComparer.Ordinal);
        private readonly string _draftOwner = Guid.NewGuid().ToString("N");
        private readonly DispatcherTimer _draftTimer;
        // 넘겨받았지만 이 창 파일에 아직 저장하지 못한 파일(저장에 성공하면 지운다)
        private readonly List<string> _adoptedDrafts = new List<string>();
        private bool _draftSaveFailed;

        private Grid _strip;
        private StackPanel _tabPanel;
        private ScrollViewer _tabScroll;
        private Button _tabList;
        private Button _addTab;
        private StackPanel _rightGroup;
        private ComboBox _target;
        private TextBlock _cursorInfo;
        private Button _run;
        private TextBlock _runHint;
        private Button _cancel;

        private Grid _editorHost;
        private TextBlock _gutterText;
        private TranslateTransform _gutterShift;
        // 줄 번호 첫 글자 상자의 위쪽(TextBlock 안 좌표). 처음 배치 뒤 잰다.
        private double _gutterGlyphTop = double.NaN;

        private PaneTab _gridTab;
        private PaneTab _messagesTab;
        private PaneTab _historyTab;
        private Button _copy;
        private Grid _gridHost;
        // 여러 문장 실행의 결과 하위 탭(결과가 둘 이상일 때만 보임)과 그것을 담은 결과 칸
        private DockPanel _gridArea;
        private Border _resultStrip;
        private StackPanel _resultTabs;
        private TextBlock _gridPlaceholder;
        private ResultMessageList _messages;
        private ResultHistoryList _history;

        private TextBlock _statusText;
        private Button _statusAction;

        public SqlWorkspace(IDbHost host)
        {
            if (host == null)
                throw new ArgumentNullException(nameof(host));
            _host = host;
            Theme.Background(this, Theme.PanelBackground);

            RowDefinitions.Add(new RowDefinition { Height = new GridLength(42, GridUnitType.Star), MinHeight = 90 });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(58, GridUnitType.Star), MinHeight = 110 });

            var editorArea = new DockPanel();
            var strip = BuildTabStrip();
            DockPanel.SetDock(strip, Dock.Top);
            editorArea.Children.Add(strip);
            editorArea.Children.Add(BuildEditor());
            Children.Add(editorArea);

            var splitter = BuildSplitter();
            Grid.SetRow(splitter, 1);
            Children.Add(splitter);

            var results = new DockPanel();
            var header = BuildResultsHeader();
            DockPanel.SetDock(header, Dock.Top);
            results.Children.Add(header);
            var status = BuildStatusBar();
            DockPanel.SetDock(status, Dock.Bottom);
            results.Children.Add(status);
            results.Children.Add(BuildResultsPane());
            Grid.SetRow(results, 2);
            Children.Add(results);

            _elapsedTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
            _elapsedTimer.Tick += ElapsedTimer_Tick;
            // 셸이 연결·끊기·가져올 행 변경을 OnHostStateChanged로 알린다. 알림이 빠진 경로(커서 정리 중 끊김 등)를 위해 1초마다도 맞춘다
            _stateTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
            _stateTimer.Tick += StateTimer_Tick;
            _draftTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = SqlDraftLogic.SaveDelay };
            _draftTimer.Tick += (s, e) => Safe(() => SaveDrafts());
            Loaded += Workspace_Loaded;
            Unloaded += Workspace_Unloaded;

            lock (LiveDraftOwners)
                LiveDraftOwners.Add(_draftOwner);
            if (!RestoreDrafts())
            {
                var first = CreateTab(InitialTarget(), FirstTabText);
                first.Editor.CaretIndex = first.Editor.Text.Length;
                ActivateTab(first, false);
            }
            SelectPane(WorkspacePane.Grid);
        }

        /// <summary>지금 탭의 대상 DB Id(없으면 null). 셸의 커밋·롤백·모드 표시가 이 DB를 대상으로 한다.</summary>
        public string ActiveDbId
        {
            get { return _active != null ? _active.DbId : null; }
        }

        /// <summary>지금 탭이 바뀌었거나 그 탭의 대상 DB가 바뀌었을 때.</summary>
        public event EventHandler ActiveTabChanged;

        /// <summary>트리 메뉴 [SELECT 문 넣기]·Enter: 그 DB 대상 탭(지금 탭이 다른 DB면 그 DB의 실행 중 아닌 탭, 없으면 새 탭)으로 옮겨 맨 위에 SELECT를 넣는다.</summary>
        public void InsertSelect(string dbId, string owner, string objectName)
        {
            try
            {
                if (string.IsNullOrEmpty(objectName))
                    return;
                bool moved;
                var tab = TabForInsert(dbId, out moved);
                InsertAtTop(tab, WorkspaceLogic.SelectStatement(owner, objectName));
                AddMessage(dbId, WorkspaceLogic.SelectMadeMessage(WorkspaceLogic.QualifiedName(owner, objectName), moved, tab.Title, NameOf(dbId)), MessageKind.Info);
            }
            catch (Exception ex)
            {
                ReportUnexpected(ex);
            }
        }

        /// <summary>메시지 탭에 한 줄(시각, DB 배지, 문장). dbId가 null이면 배지 없이.</summary>
        public void AddMessage(string dbId, string text, MessageKind kind)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => AddMessage(dbId, text, kind)));
                return;
            }
            try
            {
                _messages.Add(DateTime.Now, Profile(dbId), dbId != null, text, kind);
                RenderResultsHeader();
            }
            catch (Exception)
            {
                // 메시지를 남기지 못하는 화면 오류는 다시 메시지로 알릴 수 없다 — Folderss를 멈추지 않는 것이 우선
            }
        }

        /// <summary>그 DB를 대상으로 실행·가져오기 중인 탭이 있으면 true.</summary>
        public bool IsRunning(string dbId)
        {
            return dbId != null && _tabs.Any(t => t.Running && t.RunningDbId == dbId);
        }

        public bool AnyRunning
        {
            get { return _tabs.Any(t => t.Running); }
        }

        /// <summary>모든 탭의 실행을 취소 요청한다(창 닫기).</summary>
        public void CancelAll()
        {
            foreach (var tab in _tabs)
            {
                if (tab.Running)
                    RequestCancel(tab);
            }
        }

        /// <summary>그 DB의 연결을 끊기 직전: 그 DB 탭들의 열린 커서를 닫고 상태줄을 "커서 닫힘"으로.</summary>
        public async Task OnDisconnectingAsync(string dbId)
        {
            try
            {
                // 기다리기 전에 모두 놓는다 — 닫는 동안 그 DB 탭에서 [다음 행 가져오기]를 누르지 못하게
                var closing = new List<KeyValuePair<DbSession, QueryCursor>>();
                var affected = new List<SqlTabState>();
                foreach (var tab in _tabs)
                {
                    var touched = false;
                    foreach (var result in tab.Results)
                    {
                        if (result.Cursor == null || result.ResultDbId != dbId)
                            continue;
                        if (result.HoldsOpenCursor)
                        {
                            closing.Add(new KeyValuePair<DbSession, QueryCursor>(result.CursorSession, result.Cursor));
                            touched = true;
                        }
                        result.CursorReleased = true;
                    }
                    if (touched || (tab.ResultDbId == dbId && tab.Status != null && tab.Status.ShowsCursor))
                        affected.Add(tab);
                }
                foreach (var tab in affected)
                    SetStatus(tab, FetchStatusOf(tab));
                var session = _host.GetSession(dbId);
                _orphanCursors.RemoveAll(p => p.Value.IsClosed || ReferenceEquals(p.Key, session));
                RenderStatus();
                foreach (var pair in closing)
                {
                    try
                    {
                        if (pair.Key != null && !pair.Value.IsClosed)
                            await pair.Key.CloseCursorAsync(pair.Value);
                    }
                    catch (Exception)
                    {
                        // 바쁘거나 끊긴 세션 — 연결을 닫을 때 커서도 함께 닫힌다
                    }
                }
            }
            catch (Exception ex)
            {
                ReportUnexpected(ex);
            }
        }

        /// <summary>툴바 [커밋]·[롤백] 뒤: 트랜잭션이 끝나 닫힌 FOR UPDATE 커서를 보이던 탭의 상태줄을 "커서 닫힘"으로.</summary>
        public void OnTransactionEnded(string dbId)
        {
            try
            {
                RefreshCursorStates(dbId);
                RenderStatus();
            }
            catch (Exception ex)
            {
                ReportUnexpected(ex);
            }
        }

        /// <summary>
        /// 연결·끊기·끊김·가져올 행이 바뀌었을 수 있음(셸이 알린다. 1초 타이머는 알림을 놓친 경우를 위한 것). 바뀐 것이 없으면 아무것도 안 함.
        /// </summary>
        public void OnHostStateChanged()
        {
            try
            {
                RefreshHostState();
            }
            catch (Exception ex)
            {
                ReportUnexpected(ex);
            }
        }

        /// <summary>접속 목록이 바뀜: 탭 배지·대상 목록 갱신, 삭제된 접속을 대상으로 하던 탭은 대상을 비우고 메시지를 남긴다.</summary>
        public void OnProfilesChanged()
        {
            try
            {
                var activeChanged = false;
                foreach (var tab in _tabs)
                {
                    if (DropMissingTarget(tab) && tab == _active)
                        activeChanged = true;
                }
                _stateSignature = null;
                RenderTabs();
                RenderTarget();
                RenderResults();
                RenderStatus();
                if (activeChanged)
                    RaiseActiveTabChanged();
            }
            catch (Exception ex)
            {
                ReportUnexpected(ex);
            }
        }

        public void FocusEditor()
        {
            try
            {
                FocusActiveEditor();
            }
            catch (Exception ex)
            {
                ReportUnexpected(ex);
            }
        }

        // ================= 화면 만들기 =================

        private FrameworkElement BuildTabStrip()
        {
            _strip = new Grid { MinHeight = 30 };
            Theme.Background(_strip, Theme.SurfaceBackground);
            _strip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _strip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _strip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _strip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _tabPanel = new StackPanel { Orientation = Orientation.Horizontal };
            _tabScroll = WorkspaceUi.TabScroll(_tabPanel, "SQL 탭 목록", menu => Safe(() => FillTabList(menu)), out _tabList);
            _strip.Children.Add(_tabScroll);
            Grid.SetColumn(_tabList, 1);
            _strip.Children.Add(_tabList);
            // ▾가 보이거나 숨으면 탭 줄이 쓸 수 있는 폭이 바뀐다
            _tabList.IsVisibleChanged += (s, e) => Safe(UpdateTabStripLayout);
            _tabList.SizeChanged += (s, e) => Safe(UpdateTabStripLayout);

            _addTab = new Button
            {
                Content = "＋",
                ToolTip = "새 SQL 탭 (트리에서 고른 DB 대상)",
                Padding = new Thickness(8, 1, 8, 1),
                Margin = new Thickness(3, 3, 3, 3),
                VerticalAlignment = VerticalAlignment.Center,
                Focusable = false
            };
            AutomationProperties.SetName(_addTab, "새 SQL 탭");
            _addTab.Click += (s, e) => Safe(AddTab);
            Grid.SetColumn(_addTab, 2);
            _strip.Children.Add(_addTab);

            _rightGroup = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 6, 0) };
            var targetLabel = Theme.Text("대상", Theme.SecondaryText);
            targetLabel.FontSize = 11.5;
            targetLabel.VerticalAlignment = VerticalAlignment.Center;
            targetLabel.Margin = new Thickness(0, 0, 4, 0);
            _rightGroup.Children.Add(targetLabel);

            _target = new ComboBox { Width = 140, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, ToolTip = "이 탭의 대상 DB" };
            AutomationProperties.SetName(_target, "이 탭의 대상 DB");
            _target.SelectionChanged += Target_SelectionChanged;
            _target.DropDownOpened += (s, e) => Safe(RenderTarget);
            _target.DropDownClosed += (s, e) => Safe(RenderTarget);
            _rightGroup.Children.Add(_target);

            _cursorInfo = Theme.Text("줄 1, 열 1", Theme.SecondaryText);
            _cursorInfo.FontSize = 11;
            _cursorInfo.MinWidth = 64;
            _cursorInfo.Margin = new Thickness(10, 0, 8, 0);
            _cursorInfo.VerticalAlignment = VerticalAlignment.Center;
            _rightGroup.Children.Add(_cursorInfo);

            var runLabel = new TextBlock { Text = "실행", VerticalAlignment = VerticalAlignment.Center };
            _runHint = new TextBlock { Text = "Ctrl+Enter", FontSize = 11, Opacity = 0.75, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var runContent = new StackPanel { Orientation = Orientation.Horizontal };
            runContent.Children.Add(runLabel);
            runContent.Children.Add(_runHint);
            _run = new Button
            {
                Content = runContent,
                Padding = new Thickness(10, 2, 10, 2),
                VerticalAlignment = VerticalAlignment.Center,
                // 눌러도 편집기의 캐럿·선택이 그대로 있게(이어서 Ctrl+Enter)
                Focusable = false,
                ToolTip = "커서가 있는 문장이나 선택한 부분을 실행 (Ctrl+Enter)"
            };
            AutomationProperties.SetName(_run, "실행");
            WorkspaceUi.MakePrimary(_run, runLabel, _runHint);
            _run.Click += (s, e) => RunActive();
            _rightGroup.Children.Add(_run);

            _cancel = new Button
            {
                Content = "취소",
                Padding = new Thickness(10, 2, 10, 2),
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsEnabled = false,
                Focusable = false,
                ToolTip = "실행 중인 문장 취소"
            };
            _cancel.Click += (s, e) => Safe(CancelActive);
            _rightGroup.Children.Add(_cancel);

            Grid.SetColumn(_rightGroup, 4);
            _strip.Children.Add(_rightGroup);
            _strip.SizeChanged += (s, e) => Safe(UpdateTabStripLayout);
            _rightGroup.SizeChanged += (s, e) => Safe(UpdateTabStripLayout);

            var border = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = _strip };
            border.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
            return border;
        }

        private FrameworkElement BuildEditor()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // 편집기와 같은 글꼴·크기·줄 높이여야 글자 상자를 맞췄을 때 기준선도 맞는다
            _gutterText = new TextBlock
            {
                Text = "1",
                FontFamily = Theme.Mono,
                FontSize = EditorFontSize,
                TextAlignment = TextAlignment.Right,
                LineHeight = EditorLineHeight,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight
            };
            Theme.Foreground(_gutterText, Theme.DisabledText);
            _gutterShift = new TranslateTransform();
            _gutterText.RenderTransform = _gutterShift;
            // 칸보다 긴 줄 번호를 Grid·Border에 바로 넣으면 레이아웃 잘림(로컬 좌표)이 생겨 RenderTransform으로 올려도 아래 번호가 안 보인다.
            // Canvas 자식은 잘림 없이 자기 크기로 배치되므로 Canvas에 넣고, 잘라 내기는 바깥 Border(ClipToBounds)가 한다.
            var numbers = new Canvas { HorizontalAlignment = HorizontalAlignment.Right };
            numbers.SetBinding(FrameworkElement.WidthProperty, new Binding(nameof(FrameworkElement.ActualWidth)) { Source = _gutterText });
            numbers.Children.Add(_gutterText);
            var gutter = new Border
            {
                Child = numbers,
                MinWidth = 36,
                Padding = new Thickness(6, 0, 6, 0),
                BorderThickness = new Thickness(0, 0, 1, 0),
                ClipToBounds = true
            };
            gutter.SetResourceReference(Border.BackgroundProperty, Theme.SurfaceBackground);
            gutter.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
            grid.Children.Add(gutter);

            _editorHost = new Grid();
            Grid.SetColumn(_editorHost, 1);
            _editorHost.Children.Add(BuildEditorFind());
            grid.Children.Add(_editorHost);
            return grid;
        }

        private GridSplitter BuildSplitter()
        {
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
            splitter.MouseEnter += (s, e) => Theme.Background(splitter, Theme.Accent);
            splitter.MouseLeave += (s, e) => Theme.Background(splitter, Theme.SurfaceBackground);
            return splitter;
        }

        private FrameworkElement BuildResultsHeader()
        {
            var bar = new DockPanel();
            Theme.Background(bar, Theme.SurfaceBackground);

            _copy = new Button
            {
                Content = "복사(TSV)",
                Padding = new Thickness(10, 1, 10, 1),
                Margin = new Thickness(4, 3, 6, 3),
                IsEnabled = false,
                ToolTip = "결과 전체를 머리글과 함께 TSV로 복사"
            };
            _copy.Click += CopyAll_Click;
            DockPanel.SetDock(_copy, Dock.Right);
            bar.Children.Add(_copy);

            var tabs = new StackPanel { Orientation = Orientation.Horizontal };
            _gridTab = new PaneTab("결과", true);
            _messagesTab = new PaneTab("메시지", true);
            _historyTab = new PaneTab("기록", false);
            _gridTab.Button.Click += (s, e) => Safe(() => SelectPane(WorkspacePane.Grid));
            _messagesTab.Button.Click += (s, e) => Safe(() => SelectPane(WorkspacePane.Messages));
            _historyTab.Button.Click += (s, e) => Safe(() => SelectPane(WorkspacePane.History));
            tabs.Children.Add(_gridTab.Button);
            tabs.Children.Add(_messagesTab.Button);
            tabs.Children.Add(_historyTab.Button);
            bar.Children.Add(tabs);

            var border = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = bar };
            border.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
            return border;
        }

        private FrameworkElement BuildResultsPane()
        {
            var pane = new Grid();
            Theme.Background(pane, Theme.PanelBackground);

            _gridHost = new Grid();
            _gridPlaceholder = Theme.Text("", Theme.SecondaryText);
            _gridPlaceholder.HorizontalAlignment = HorizontalAlignment.Center;
            _gridPlaceholder.VerticalAlignment = VerticalAlignment.Center;
            _gridPlaceholder.TextAlignment = TextAlignment.Center;
            _gridPlaceholder.TextWrapping = TextWrapping.Wrap;
            _gridPlaceholder.Margin = new Thickness(16);
            _gridHost.Children.Add(_gridPlaceholder);
            _gridHost.Children.Add(BuildResultFind());
            _resultTabs = new StackPanel { Orientation = Orientation.Horizontal };
            Button resultList;
            var resultScroll = WorkspaceUi.TabScroll(_resultTabs, "조회 결과 목록", menu => Safe(() => FillResultList(menu)), out resultList);
            var resultRow = new DockPanel();
            DockPanel.SetDock(resultList, Dock.Right);
            resultRow.Children.Add(resultList);
            resultRow.Children.Add(resultScroll);
            _resultStrip = new Border
            {
                Child = resultRow,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Visibility = Visibility.Collapsed
            };
            _resultStrip.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
            Theme.Background(_resultStrip, Theme.SurfaceBackground);
            AutomationProperties.SetName(_resultStrip, "조회 결과 목록");
            DockPanel.SetDock(_resultStrip, Dock.Top);
            _gridArea = new DockPanel();
            _gridArea.Children.Add(_resultStrip);
            _gridArea.Children.Add(BuildEditBar());
            _gridArea.Children.Add(_gridHost);
            pane.Children.Add(_gridArea);

            _messages = new ResultMessageList(text => CopyText(text, null));
            pane.Children.Add(_messages.View);

            _history = new ResultHistoryList();
            _history.Activated += entry => Safe(() => InsertHistory(entry));
            pane.Children.Add(_history.View);
            return pane;
        }

        private FrameworkElement BuildStatusBar()
        {
            var bar = new DockPanel();
            _statusAction = new Button
            {
                Padding = new Thickness(10, 0, 10, 0),
                Margin = new Thickness(8, 2, 6, 2),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            _statusAction.Click += StatusAction_Click;
            DockPanel.SetDock(_statusAction, Dock.Right);
            bar.Children.Add(_statusAction);

            _statusText = new TextBlock
            {
                FontSize = 11.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 3, 8, 3)
            };
            Theme.Foreground(_statusText, Theme.SecondaryText);
            bar.Children.Add(_statusText);

            var border = new Border { BorderThickness = new Thickness(0, 1, 0, 0), MinHeight = 26, Child = bar };
            border.SetResourceReference(Border.BackgroundProperty, Theme.SurfaceBackground);
            border.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
            return border;
        }

        private TextBox CreateEditor(string text)
        {
            var editor = new TextBox
            {
                AcceptsReturn = true,
                AcceptsTab = true,
                TextWrapping = TextWrapping.NoWrap,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = Theme.Mono,
                FontSize = EditorFontSize,
                BorderThickness = new Thickness(0),
                Padding = EditorPadding,
                // Folderss의 암시적 TextBox 스타일은 VerticalContentAlignment=Center다(한 줄 입력칸용). 그대로 두면 짧은 글이 편집기
                // 한가운데에 뜨고, 처음 잰 첫 줄 위치로 맞추는 줄 번호도 글이 늘면 어긋난다 — 위·왼쪽 정렬을 직접 정한다.
                VerticalContentAlignment = VerticalAlignment.Top,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                IsInactiveSelectionHighlightEnabled = true,
                Visibility = Visibility.Collapsed,
                Text = WorkspaceLogic.NormalizeNewlines(text ?? "", Environment.NewLine)
            };
            SpellCheck.SetIsEnabled(editor, false);
            editor.SetValue(TextBlock.LineHeightProperty, EditorLineHeight);
            editor.SetValue(TextBlock.LineStackingStrategyProperty, LineStackingStrategy.BlockLineHeight);
            Theme.Background(editor, Theme.PanelBackground);
            Theme.Foreground(editor);
            editor.SetResourceReference(TextBoxBase.CaretBrushProperty, Theme.PrimaryText);
            AutomationProperties.SetName(editor, "SQL 편집기");
            editor.TextChanged += Editor_TextChanged;
            // 탐색기에서 파일을 끌어다 놓으면 그 파일 탭으로 연다(이미 연 파일은 그 탭으로) — 열린 뒤에는 Ctrl+S로 그 파일에 저장
            ShellUi.EnableFileDrop(editor, files => OpenPaths(files));
            editor.SelectionChanged += Editor_SelectionChanged;
            editor.PreviewKeyDown += Editor_PreviewKeyDown;
            editor.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Editor_ScrollChanged));
            return editor;
        }

        // ================= 탭 =================

        private string InitialTarget()
        {
            var selected = _host.SelectedDbId;
            if (Profile(selected) != null)
                return selected;
            var first = Profiles().FirstOrDefault();
            return first != null ? first.Id : null;
        }

        private SqlTabState CreateTab(string dbId, string text)
        {
            var number = WorkspaceLogic.NextTabNumber(_tabs.Select(t => t.Title), _nextTabNumber);
            _nextTabNumber = number + 1;
            var tab = new SqlTabState(WorkspaceLogic.TabTitle(number), dbId, CreateEditor(text))
            {
                Status = WorkspaceLogic.ReadyStatus()
            };
            tab.InitialText = tab.Editor.Text;
            _tabs.Add(tab);
            _editorHost.Children.Add(tab.Editor);
            return tab;
        }

        /// <summary>새 탭의 대상: 트리에서 고른 DB, 없으면 지금 탭의 대상.</summary>
        private string NewTabTarget()
        {
            var selected = _host.SelectedDbId;
            var target = Profile(selected) != null ? selected : (_active != null ? _active.DbId : null);
            return Profile(target) != null ? target : null;
        }

        private void AddTab()
        {
            var target = NewTabTarget();
            var profile = Profile(target);
            var tab = CreateTab(target, WorkspaceLogic.NewTabText(profile != null ? profile.Name : null));
            tab.Editor.CaretIndex = tab.Editor.Text.Length;
            ActivateTab(tab, true);
        }

        private void ActivateTab(SqlTabState tab, bool focusEditor)
        {
            if (tab == null)
                return;
            var changed = _active != tab;
            _active = tab;
            tab.Done = false;
            foreach (var t in _tabs)
                t.Editor.Visibility = t == tab ? Visibility.Visible : Visibility.Collapsed;
            if (tab.PendingPane.HasValue)
            {
                var pane = tab.PendingPane.Value;
                tab.PendingPane = null;
                SelectPane(pane);
            }
            UpdateGutter(tab, true);
            PositionGutter(tab, tab.Editor.VerticalOffset);
            UpdateCursorInfo(tab);
            RenderTabs();
            RenderTarget();
            RenderRunState();
            RenderResults();
            RenderStatus();
            if (focusEditor)
                FocusActiveEditor();
            if (changed)
                RaiseActiveTabChanged();
        }

        /// <summary>탭을 닫는다. 닫았으면 true(실행 중·마지막 탭·저장 묻기에서 취소면 false).</summary>
        private bool CloseTab(SqlTabState tab)
        {
            if (_tabs.Count <= 1 || !_tabs.Contains(tab))
                return false;
            if (tab.Running)
            {
                AddMessage(tab.RunningDbId ?? tab.DbId, "'" + tab.Title + "' 탭은 실행 중이라 닫지 않았습니다. 먼저 취소하세요.", MessageKind.Error);
                return false;
            }
            if (!ConfirmCloseUnsaved(tab) || !_tabs.Contains(tab) || tab.Running)
                return false;
            if (ReleaseCursor(tab))
                AddMessage(tab.ResultDbId, "'" + tab.Title + "' 탭을 닫아 열린 커서를 닫았습니다.", MessageKind.Info);
            var index = _tabs.IndexOf(tab);
            _tabs.RemoveAt(index);
            // 닫은 탭은 임시 저장에서도 뺀다
            ScheduleDraftSave();
            _editorHost.Children.Remove(tab.Editor);
            DropResultGrids(tab);
            if (_active == tab)
            {
                _active = null;
                ActivateTab(_tabs[Math.Max(0, index - 1)], true);
            }
            else
            {
                RenderTabs();
            }
            return true;
        }

        private void RenderTabs()
        {
            _tabPanel.Children.Clear();
            Button activeHeader = null;
            foreach (var tab in _tabs)
            {
                var header = BuildTabHeader(tab);
                _tabPanel.Children.Add(header);
                if (tab == _active)
                    activeHeader = header;
            }
            // 탭이 많아 가로로 스크롤될 때 지금 탭이 보이게
            if (activeHeader != null)
                WorkspaceUi.BringIntoViewWhenLoaded(activeHeader);
        }

        private Button BuildTabHeader(SqlTabState tab)
        {
            var active = tab == _active;
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            if (tab.Running)
            {
                var spin = new TextBlock { Text = "◌", Foreground = Theme.Warning, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "실행 중" };
                content.Children.Add(spin);
            }
            else if (tab.Done)
            {
                var done = Theme.Text("●", Theme.Accent);
                done.FontSize = 9;
                done.Margin = new Thickness(0, 0, 5, 0);
                done.VerticalAlignment = VerticalAlignment.Center;
                done.ToolTip = "새 결과";
                content.Children.Add(done);
            }
            var title = Theme.Text(tab.Title + (tab.ShownDirty ? "*" : ""), active ? Theme.PrimaryText : Theme.SecondaryText);
            title.VerticalAlignment = VerticalAlignment.Center;
            if (active)
                title.FontWeight = FontWeights.SemiBold;
            content.Children.Add(title);
            var profile = Profile(tab.DbId);
            var badge = Theme.DbBadge(profile);
            badge.Margin = new Thickness(6, 0, 0, 0);
            content.Children.Add(badge);

            TextBlock close = null;
            if (_tabs.Count > 1)
            {
                close = Theme.Text("×", Theme.SecondaryText);
                close.FontSize = 13;
                close.Padding = new Thickness(3, 0, 3, 0);
                close.Margin = new Thickness(5, 0, 0, 0);
                close.VerticalAlignment = VerticalAlignment.Center;
                close.ToolTip = "탭 닫기";
                AutomationProperties.SetName(close, "탭 닫기");
                var glyph = close;
                glyph.MouseEnter += (s, e) =>
                {
                    Theme.Background(glyph, Theme.ControlHover);
                    Theme.Foreground(glyph, Theme.PrimaryText);
                };
                glyph.MouseLeave += (s, e) =>
                {
                    glyph.ClearValue(TextBlock.BackgroundProperty);
                    Theme.Foreground(glyph, Theme.SecondaryText);
                };
                content.Children.Add(close);
            }

            Border underline;
            var button = WorkspaceUi.SelectorButton(content, out underline);
            WorkspaceUi.SetSelected(button, underline, active);
            button.ToolTip = (tab.FilePath ?? tab.Title) + (tab.ShownDirty ? " (저장하지 않은 변경 있음)" : "") + " — 대상: " + (profile != null ? profile.Name : "없음");
            AutomationProperties.SetName(button, tab.Title);
            button.Click += (s, e) => Safe(() => ActivateTab(tab, true));
            button.ContextMenu = BuildTabMenu(tab);
            if (close != null)
            {
                var glyph = close;
                // ×는 버튼 안에 있으므로 누르기 전에 가로채 탭 전환(버튼 Click)이 일어나지 않게 한다
                button.PreviewMouseLeftButtonDown += (s, e) =>
                {
                    if (!WorkspaceUi.IsInside(e.OriginalSource, glyph))
                        return;
                    e.Handled = true;
                    Safe(() => CloseTab(tab));
                };
                button.MouseUp += (s, e) =>
                {
                    if (e.ChangedButton != MouseButton.Middle)
                        return;
                    e.Handled = true;
                    Safe(() => CloseTab(tab));
                };
            }
            return button;
        }

        private void UpdateTabStripLayout()
        {
            var width = _strip.ActualWidth;
            if (width <= 0)
                return;
            // 좁은 창(900×600보다 작게 줄였을 때)에서는 덜 중요한 표시부터 숨겨 탭 자리를 남긴다
            var hint = width < 600 ? Visibility.Collapsed : Visibility.Visible;
            if (_runHint.Visibility != hint)
                _runHint.Visibility = hint;
            var info = width < 500 ? Visibility.Collapsed : Visibility.Visible;
            if (_cursorInfo.Visibility != info)
                _cursorInfo.Visibility = info;
            var used = _rightGroup.ActualWidth + _rightGroup.Margin.Left + _rightGroup.Margin.Right
                + _addTab.ActualWidth + _addTab.Margin.Left + _addTab.Margin.Right
                + (_tabList.IsVisible ? _tabList.ActualWidth + _tabList.Margin.Left + _tabList.Margin.Right : 0);
            _tabScroll.MaxWidth = Math.Max(60, width - used);
        }

        /// <summary>▾ 탭 목록: 모든 SQL 탭(지금 탭은 굵게). 고르면 그 탭으로 옮기고 탭 줄을 그 탭까지 스크롤한다.</summary>
        private void FillTabList(ContextMenu menu)
        {
            foreach (var tab in _tabs)
            {
                var item = tab;
                var profile = Profile(item.DbId);
                var text = item.Title + (item.ShownDirty ? "*" : "") + "  —  " + (profile != null ? profile.Name : "대상 없음");
                menu.Items.Add(WorkspaceUi.PlainMenuItem(text, item == _active, () => Safe(() => ActivateTab(item, true))));
            }
        }

        /// <summary>탭 머리 오른쪽 클릭 메뉴: 닫기 · 다른 탭 모두 닫기 · 왼쪽 탭 모두 닫기 · 오른쪽 탭 모두 닫기.</summary>
        private ContextMenu BuildTabMenu(SqlTabState tab)
        {
            var menu = new ContextMenu();
            var items = new[]
            {
                Tuple.Create("닫기", TabCloseScope.This),
                Tuple.Create("다른 탭 모두 닫기", TabCloseScope.Others),
                Tuple.Create("왼쪽 탭 모두 닫기", TabCloseScope.Left),
                Tuple.Create("오른쪽 탭 모두 닫기", TabCloseScope.Right)
            };
            foreach (var entry in items)
            {
                var scope = entry.Item2;
                var item = WorkspaceUi.PlainMenuItem(entry.Item1, false, () => Safe(() => CloseTabs(tab, scope)));
                item.IsEnabled = WorkspaceLogic.TabsToClose(_tabs.Count, _tabs.IndexOf(tab), scope).Count > 0;
                menu.Items.Add(item);
            }
            return menu;
        }

        /// <summary>
        /// 메뉴로 여러 탭 닫기(앞에서 뒤로). 실행 중인 탭은 건너뛰고(메시지), 저장 묻기에서 [취소]하면 남은 탭은 닫지 않는다.
        /// 끝나면 메뉴를 연 탭으로 옮긴다.
        /// </summary>
        private void CloseTabs(SqlTabState tab, TabCloseScope scope)
        {
            var targets = WorkspaceLogic.TabsToClose(_tabs.Count, _tabs.IndexOf(tab), scope).Select(i => _tabs[i]).ToList();
            foreach (var target in targets)
            {
                if (!CloseTab(target) && !target.Running)
                    break;
            }
            if (scope != TabCloseScope.This && _tabs.Contains(tab) && _active != tab)
                ActivateTab(tab, true);
        }

        // ================= 대상 =================

        private void RenderTarget()
        {
            var tab = _active;
            _target.IsEnabled = tab != null && !tab.Running;
            var current = IsTargetListCurrent();
            if (_target.IsDropDownOpen)
            {
                // 펼친 목록을 다시 만들면 고르던 항목이 사라진다 — 글자만 맞추고 목록은 닫힌 뒤(DropDownClosed) 맞춘다
                if (current)
                    UpdateTargetTexts();
                return;
            }
            _fillingTarget = true;
            try
            {
                if (current)
                {
                    UpdateTargetTexts();
                }
                else
                {
                    _target.Items.Clear();
                    foreach (var profile in Profiles())
                        _target.Items.Add(new ComboBoxItem { Content = TargetText(profile), Tag = profile.Id });
                    if (tab == null || Profile(tab.DbId) == null)
                        _target.Items.Add(new ComboBoxItem { Content = "대상 없음", Tag = null });
                }
                var wanted = tab != null && Profile(tab.DbId) != null ? tab.DbId : null;
                _target.SelectedItem = _target.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(item => string.Equals(item.Tag as string, wanted, StringComparison.Ordinal));
            }
            finally
            {
                _fillingTarget = false;
            }
        }

        // 목록 항목(접속 Id 순서, "대상 없음" 유무)이 지금 접속 목록과 같으면 글자만 바꾸면 된다
        private bool IsTargetListCurrent()
        {
            var wanted = Profiles().Select(p => p.Id).ToList();
            var tab = _active;
            var needsNone = tab == null || Profile(tab.DbId) == null;
            if (needsNone)
                wanted.Add(null);
            if (_target.Items.Count != wanted.Count)
                return false;
            for (var i = 0; i < wanted.Count; i++)
            {
                var item = _target.Items[i] as ComboBoxItem;
                if (item == null || !string.Equals(item.Tag as string, wanted[i], StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        private void UpdateTargetTexts()
        {
            foreach (ComboBoxItem item in _target.Items)
            {
                var profile = Profile(item.Tag as string);
                var text = profile != null ? TargetText(profile) : "대상 없음";
                if (!string.Equals(item.Content as string, text, StringComparison.Ordinal))
                    item.Content = text;
            }
        }

        private string TargetText(OracleConnectionProfile profile)
        {
            return WorkspaceLogic.TargetItemText(profile.Name, _host.GetSession(profile.Id) != null, profile.ReadOnly);
        }

        private void Target_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_fillingTarget)
                return;
            Safe(() =>
            {
                var tab = _active;
                var item = _target.SelectedItem as ComboBoxItem;
                if (tab == null || item == null)
                    return;
                var dbId = item.Tag as string;
                if (tab.Running)
                {
                    // 실행 중에는 대상을 바꿀 수 없다 — 고른 항목을 되돌린다
                    RenderTarget();
                    return;
                }
                if (dbId == null || dbId == tab.DbId || Profile(dbId) == null)
                    return;
                ChangeTarget(tab, dbId);
            });
        }

        private void ChangeTarget(SqlTabState tab, string dbId)
        {
            if (ReleaseCursor(tab))
                AddMessage(tab.ResultDbId, "대상 DB를 바꿔 '" + tab.Title + "' 탭의 열린 커서를 닫았습니다.", MessageKind.Info);
            tab.DbId = dbId;
            ScheduleDraftSave();
            // 이전 대상의 연결·바쁨 안내는 더 맞지 않는다
            tab.Status = tab.Cursor != null ? FetchStatusOf(tab) : WorkspaceLogic.ReadyStatus();
            RenderTabs();
            RenderTarget();
            RenderResults();
            RenderStatus();
            AddMessage(dbId, WorkspaceLogic.TargetChangedMessage(tab.Title, NameOf(dbId)), MessageKind.Info);
            RaiseActiveTabChanged();
        }

        /// <summary>대상 접속이 삭제된 탭은 대상을 비운다(실행 중이면 끝난 뒤). 비웠으면 true.</summary>
        private bool DropMissingTarget(SqlTabState tab)
        {
            if (tab.DbId == null || tab.Running || Profile(tab.DbId) != null)
                return false;
            ReleaseCursor(tab);
            tab.DbId = null;
            if (tab.Status != null && tab.Status.Action != WorkspaceLogic.StatusAction.None && tab.Status.Action != WorkspaceLogic.StatusAction.FetchNext)
                tab.Status = WorkspaceLogic.ReadyStatus();
            AddMessage(null, WorkspaceLogic.TargetRemovedMessage(tab.Title), MessageKind.Error);
            return true;
        }

        // ================= 편집기 =================

        private void Editor_TextChanged(object sender, TextChangedEventArgs e)
        {
            Safe(() =>
            {
                // 이 처리기는 이 작업 영역의 편집기에만 붙는다 — 어느 탭이 바뀌어도 임시 저장한다
                ScheduleDraftSave();
                var changed = _tabs.FirstOrDefault(t => ReferenceEquals(t.Editor, sender));
                if (changed != null && changed.FilePath != null)
                    UpdateDirty(changed, false);
                var tab = _active;
                if (tab == null || !ReferenceEquals(sender, tab.Editor))
                    return;
                UpdateGutter(tab, false);
            });
        }

        private void Editor_SelectionChanged(object sender, RoutedEventArgs e)
        {
            Safe(() =>
            {
                var tab = _active;
                if (tab != null && ReferenceEquals(sender, tab.Editor))
                    UpdateCursorInfo(tab);
            });
        }

        private void Editor_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            Safe(() =>
            {
                var tab = _active;
                if (tab != null && ReferenceEquals(sender, tab.Editor))
                    PositionGutter(tab, e.VerticalOffset);
            });
        }

        private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // 한글 입력 중이면 Enter 등이 ImeProcessed로 온다
            var key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
            var modifiers = Keyboard.Modifiers;
            if (key == Key.F4 && modifiers == ModifierKeys.None)
            {
                // F4: 커서가 있는 이름(또는 선택한 이름)의 테이블 정보
                e.Handled = true;
                DescribeAtCaret();
                return;
            }
            if (key == Key.F3 && (modifiers & ~ModifierKeys.Shift) == 0)
            {
                // F3·Shift+F3: 찾기 다음·이전
                e.Handled = true;
                Safe(() => EditorFindAgain(modifiers == ModifierKeys.None));
                return;
            }
            if ((modifiers & ModifierKeys.Control) == 0 || (modifiers & ModifierKeys.Alt) != 0)
                return;
            var shift = (modifiers & ModifierKeys.Shift) != 0;
            if (key == Key.F && !shift)
            {
                // Ctrl+F: 찾기
                e.Handled = true;
                Safe(OpenEditorFind);
            }
            else if (key == Key.Enter && !shift)
            {
                e.Handled = true;
                RunActive();
            }
            else if ((key == Key.Oem2 || key == Key.Divide) && !shift)
            {
                // Ctrl+/ : 줄 주석 토글
                e.Handled = true;
                Safe(ToggleComment);
            }
            else if (shift && (key == Key.U || key == Key.L))
            {
                // Ctrl+Shift+U·L : 선택한 글 대·소문자
                e.Handled = true;
                Safe(() => ChangeCase(key == Key.U));
            }
        }

        // ================= SQL 임시 저장 =================

        /// <summary>
        /// 닫힌 창(또는 비정상 종료)이 남긴 임시 저장을 넘겨받아 탭으로 되살린다. 되살린 탭이 있으면 true(첫 탭 안내문을 만들지 않음).
        /// 넘겨받은 내용을 이 창 파일에 저장한 뒤 원래 파일을 지운다.
        /// 파일 탭: 저장하지 않은 변경이 있었으면 그 내용을, 없었으면 파일을 다시 읽어(그 사이 바뀌었을 수 있음) 되살린다.
        /// </summary>
        private bool RestoreDrafts()
        {
            var folder = DraftFolder();
            if (folder == null)
                return false;
            List<KeyValuePair<string, SqlDraftSet>> orphans;
            try
            {
                List<string> live;
                lock (LiveDraftOwners)
                    live = LiveDraftOwners.ToList();
                orphans = SqlDraftLogic.LoadOrphans(folder, live);
            }
            catch (Exception ex)
            {
                AddMessage(null, SqlDraftLogic.LoadFailedMessage(ex.Message), MessageKind.Error);
                return false;
            }
            if (orphans.Count == 0)
                return false;
            var merged = SqlDraftLogic.Merge(orphans.Select(p => p.Value));
            _adoptedDrafts.AddRange(orphans.Select(p => p.Key));
            if (merged.Tabs.Count == 0)
            {
                // 탭이 없는 파일뿐 — 지우기만 한다
                SqlDraftLogic.DeleteQuietly(_adoptedDrafts);
                _adoptedDrafts.Clear();
                return false;
            }
            SqlTabState active = null;
            for (var i = 0; i < merged.Tabs.Count; i++)
            {
                var tab = RestoreTab(merged.Tabs[i]);
                if (i == merged.Active)
                    active = tab;
            }
            ActivateTab(active ?? _tabs[_tabs.Count - 1], false);
            SaveDrafts();
            AddMessage(null, SqlDraftLogic.RestoredMessage(merged.Tabs.Count), MessageKind.Info);
            return true;
        }

        private SqlTabState RestoreTab(SqlDraft draft)
        {
            // 그 사이 지운 접속이면 대상 없이 되살린다
            var dbId = Profile(draft.DbId) != null ? draft.DbId : null;
            SqlFileContent fresh = null;
            if (!string.IsNullOrEmpty(draft.FilePath) && !draft.Dirty)
            {
                try
                {
                    fresh = SqlFileLogic.Read(draft.FilePath);
                }
                catch (Exception ex)
                {
                    AddMessage(null, SqlFileLogic.OpenFailedMessage(draft.FilePath, ex.Message) + " — 임시 저장한 내용으로 되살립니다.", MessageKind.Error);
                }
            }
            var tab = CreateTab(dbId, fresh != null ? fresh.Text : draft.Text);
            tab.InitialText = "";
            if (!string.IsNullOrEmpty(draft.FilePath))
            {
                SqlFileEncoding encoding;
                tab.FilePath = draft.FilePath;
                tab.FileEncoding = fresh != null ? fresh.Encoding
                    : Enum.TryParse(draft.FileEncoding, out encoding) ? encoding : SqlFileEncoding.Utf8;
                tab.FileNewline = fresh != null ? fresh.Newline : draft.Newline;
                // 다시 읽었으면 저장된 상태, 아니면(변경이 있었거나 읽지 못함) 변경됨
                tab.SavedText = fresh != null ? tab.Editor.Text : null;
                tab.ShownDirty = tab.Dirty;
            }
            tab.Editor.CaretIndex = Math.Max(0, Math.Min(draft.Caret, tab.Editor.Text.Length));
            return tab;
        }

        /// <summary>입력이 멈춘 뒤(SaveDelay) 저장하도록 미룬다.</summary>
        private void ScheduleDraftSave()
        {
            if (_draftTimer == null)
                return;
            _draftTimer.Stop();
            _draftTimer.Start();
        }

        /// <summary>
        /// 연 탭들의 SQL을 이 창의 임시 저장 파일에 쓴다(빈 탭·만들 때 글 그대로인 탭은 빼고, 파일 탭은 늘 넣는다). 성공하면 true.
        /// 실패는 연달아 알리지 않는다.
        /// </summary>
        private bool SaveDrafts()
        {
            if (_draftTimer != null)
                _draftTimer.Stop();
            var folder = DraftFolder();
            if (folder == null)
                return false;
            var set = new SqlDraftSet { SavedAtUtc = DateTime.UtcNow };
            foreach (var tab in _tabs)
            {
                var text = tab.Editor.Text;
                if (tab.FilePath == null && !SqlDraftLogic.IsWorthSaving(text, tab.InitialText))
                    continue;
                if (tab == _active)
                    set.Active = set.Tabs.Count;
                set.Tabs.Add(new SqlDraft
                {
                    DbId = tab.DbId,
                    Text = text,
                    Caret = tab.Editor.CaretIndex,
                    FilePath = tab.FilePath,
                    FileEncoding = tab.FilePath != null ? tab.FileEncoding.ToString() : null,
                    Newline = tab.FileNewline,
                    Dirty = tab.Dirty
                });
            }
            try
            {
                SqlDraftLogic.Save(folder, _draftOwner, set);
            }
            catch (Exception ex)
            {
                if (!_draftSaveFailed)
                    AddMessage(null, SqlDraftLogic.SaveFailedMessage(ex.Message), MessageKind.Error);
                _draftSaveFailed = true;
                return false;
            }
            _draftSaveFailed = false;
            if (_adoptedDrafts.Count > 0)
            {
                SqlDraftLogic.DeleteQuietly(_adoptedDrafts);
                _adoptedDrafts.Clear();
            }
            return true;
        }

        // ================= 빠른 조회(트리 F1) · 테이블 정보(F4) =================

        /// <summary>
        /// 트리 F1: 그 테이블·뷰의 앞 QuickQueryRows행을 그 DB 탭에서 바로 조회한다. 편집기 글은 바꾸지 않고(실행 기록에는 남음),
        /// 결과는 지금처럼 하나로 보인다. [다음 행 가져오기]는 툴바의 가져올 행 수로 이어 읽는다.
        /// 결과는 편집할 수 있다(편집 막대 [편집]·[삭제]·[저장]): 조회 전에 데이터 사전에서 기본 키 열을 읽어 둔다
        /// (조회 뒤에 읽으면 리더가 하나뿐인 PostgreSQL에서 결과 커서가 닫혀 [다음 행 가져오기]를 못 한다).
        /// where는 편집 막대 WHERE 칸에서 다시 조회할 때의 조건(<see cref="WorkspaceLogic.QuickQueryWhere"/>로 검사한 것).
        /// </summary>
        public async void QuickQuery(string dbId, string owner, string objectName, string where = null)
        {
            try
            {
                var profile = Profile(dbId);
                if (profile == null || string.IsNullOrEmpty(objectName))
                    return;
                bool moved;
                var tab = TabForInsert(dbId, out moved);
                var session = _host.GetSession(dbId);
                if (session == null || session.IsBroken)
                {
                    if (!await _host.ConnectAsync(dbId) || tab.Running)
                        return;
                }
                session = CheckSession(tab, profile);
                if (session == null)
                    return;
                List<ColumnInfo> metadata = null;
                string metadataError = null;
                try
                {
                    metadata = await LoadColumnsAsync(session, profile, owner, objectName);
                }
                catch (Exception ex)
                {
                    metadataError = DbSession.DescribeError(ex);
                }
                // 열 정보를 읽는 사이 다른 실행이 시작됐거나 연결 상태가 바뀌었을 수 있다
                if (tab.Running)
                    return;
                session = CheckSession(tab, profile);
                if (session == null)
                    return;
                var statement = SqlScript.Parse(WorkspaceLogic.QuickQueryStatement(owner, objectName, where));
                AddMessage(dbId, WorkspaceLogic.QuickQueryMessage(WorkspaceLogic.QualifiedName(owner, objectName), tab.Title, where), MessageKind.Info);
                await RunSingleAsync(tab, profile, session, statement, WorkspaceLogic.QuickQueryRows);
                AttachEditTarget(tab, profile, owner, objectName, statement.Text, metadata, metadataError, where);
            }
            catch (Exception ex)
            {
                ReportUnexpected(ex);
            }
        }

        /// <summary>편집기 F4: 커서가 있는 낱말(또는 선택한 이름)의 테이블 정보.</summary>
        public void DescribeAtCaret()
        {
            Safe(() =>
            {
                var tab = _active;
                if (tab == null)
                    return;
                var editor = tab.Editor;
                var name = EditorLogic.ObjectNameAt(editor.Text, editor.SelectionStart, editor.SelectionLength);
                if (name == null)
                {
                    SetStatus(tab, WorkspaceLogic.TextStatus(TableInfoText.NoNameMessage));
                    return;
                }
                if (Profile(tab.DbId) == null)
                {
                    SetStatus(tab, WorkspaceLogic.NoTargetStatus());
                    return;
                }
                DescribeObject(tab.DbId, name.Owner, name.Name);
            });
        }

        /// <summary>테이블 정보 창(트리 F4·메뉴·편집기 F4). 이름은 스키마 없이도 된다(현재 스키마·동의어·다른 스키마 순으로 찾음).</summary>
        public async void DescribeObject(string dbId, string owner, string objectName)
        {
            try
            {
                var profile = Profile(dbId);
                if (profile == null || string.IsNullOrEmpty(objectName))
                    return;
                var session = _host.GetSession(dbId);
                if (session == null || session.IsBroken)
                {
                    if (!await _host.ConnectAsync(dbId))
                        return;
                    session = _host.GetSession(dbId);
                    if (session == null)
                        return;
                }
                // 실행 중인 문장이 끝날 때까지 기다리지 않는다(세션 하나를 같이 쓴다)
                if (IsRunning(dbId))
                {
                    AddMessage(dbId, TableInfoText.BusyMessage(profile.Name), MessageKind.Error);
                    if (_active != null)
                        SetStatus(_active, WorkspaceLogic.TextStatus(TableInfoText.BusyMessage(profile.Name)));
                    return;
                }
                string problem = null;
                TableDescription info;
                // 데이터 사전 조회 몇 번이라 1초쯤 걸린다 — 그동안 상태줄에 알린다(창을 띄우면 원래대로)
                var tab = _active;
                var before = tab != null ? tab.Status : null;
                var loading = WorkspaceLogic.TextStatus(TableInfoText.LoadingMessage(WorkspaceLogic.QualifiedName(owner, objectName)));
                if (tab != null && !tab.Running)
                    SetStatus(tab, loading);
                try
                {
                    info = await TableInfo.DescribeAsync(session, owner, objectName, CancellationToken.None, text => problem = text);
                }
                catch (Exception ex)
                {
                    AddMessage(dbId, TableInfoText.FailedMessage(DbSession.DescribeError(ex)), MessageKind.Error);
                    if (tab != null && tab.Status == loading)
                        SetStatus(tab, WorkspaceLogic.TextStatus(TableInfoText.FailedMessage(DbSession.DescribeError(ex))));
                    return;
                }
                if (tab != null && tab.Status == loading)
                    SetStatus(tab, before ?? WorkspaceLogic.ReadyStatus());
                if (info == null)
                {
                    AddMessage(dbId, problem, MessageKind.Info);
                    if (_active != null)
                        SetStatus(_active, WorkspaceLogic.TextStatus(problem));
                    return;
                }
                var target = info;
                TableInfoWindow.Show(Window.GetWindow(this), info, profile,
                    () => InsertSelect(dbId, target.Owner, target.Name),
                    () => QuickQuery(dbId, target.Owner, target.Name),
                    text => CopyText(text, "표를 TSV로 복사했습니다."));
            }
            catch (Exception ex)
            {
                ReportUnexpected(ex);
            }
        }

        /// <summary>소스 창(트리 [소스 보기]·코드 객체 F4): 뷰·프로시저·함수·패키지의 소스를 읽어 보인다.</summary>
        public async void ShowSource(string dbId, string owner, string objectType, string objectName)
        {
            try
            {
                var profile = Profile(dbId);
                if (profile == null || string.IsNullOrEmpty(objectName) || !ObjectSource.Supports(objectType))
                    return;
                var session = _host.GetSession(dbId);
                if (session == null || session.IsBroken)
                {
                    if (!await _host.ConnectAsync(dbId))
                        return;
                    session = _host.GetSession(dbId);
                    if (session == null)
                        return;
                }
                // 실행 중인 문장이 끝날 때까지 기다리지 않는다(세션 하나를 같이 쓴다)
                if (IsRunning(dbId))
                {
                    AddMessage(dbId, ObjectSource.BusyMessage(profile.Name), MessageKind.Error);
                    if (_active != null)
                        SetStatus(_active, WorkspaceLogic.TextStatus(ObjectSource.BusyMessage(profile.Name)));
                    return;
                }
                var qualified = ObjectSource.DisplayName(owner, objectName);
                var tab = _active;
                var before = tab != null ? tab.Status : null;
                var loading = WorkspaceLogic.TextStatus(ObjectSource.LoadingMessage(qualified));
                if (tab != null && !tab.Running)
                    SetStatus(tab, loading);
                string source;
                try
                {
                    var meta = session.Provider.Metadata;
                    var parts = await session.QueryAsync(meta.Source(owner, objectType, objectName), ObjectSource.Read, CancellationToken.None);
                    source = meta.BuildSource(owner, objectType, objectName, parts);
                }
                catch (Exception ex)
                {
                    AddMessage(dbId, ObjectSource.FailedMessage(DbSession.DescribeError(ex)), MessageKind.Error);
                    if (tab != null && tab.Status == loading)
                        SetStatus(tab, WorkspaceLogic.TextStatus(ObjectSource.FailedMessage(DbSession.DescribeError(ex))));
                    return;
                }
                if (tab != null && tab.Status == loading)
                    SetStatus(tab, before ?? WorkspaceLogic.ReadyStatus());
                if (source == null)
                {
                    AddMessage(dbId, ObjectSource.NotFoundMessage(qualified), MessageKind.Info);
                    if (_active != null)
                        SetStatus(_active, WorkspaceLogic.TextStatus(ObjectSource.NotFoundMessage(qualified)));
                    return;
                }
                ObjectSourceWindow.Show(Window.GetWindow(this), owner, objectType, objectName, source, profile,
                    () => Safe(() => OpenTextTab(dbId, source)),
                    text => CopyText(text, "소스를 복사했습니다."));
            }
            catch (Exception ex)
            {
                ReportUnexpected(ex);
            }
        }

        /// <summary>그 DB 대상의 새 SQL 탭에 글을 넣어 연다(실행하지 않음).</summary>
        private void OpenTextTab(string dbId, string text)
        {
            var tab = CreateTab(dbId, text);
            ActivateTab(tab, true);
            Window.GetWindow(this)?.Activate();
        }

        // ================= SQL 파일·메뉴 명령 =================

        /// <summary>메뉴·아이콘이 따르는 상태가 바뀜(탭 바꿈·실행 시작과 끝·저장하지 않은 변경 표시).</summary>
        public event EventHandler CommandStateChanged;

        /// <summary>지금 탭의 편집기(편집 메뉴의 잘라내기·복사·되돌리기 대상).</summary>
        public TextBox ActiveEditor
        {
            get { return _active != null ? _active.Editor : null; }
        }

        public bool ActiveRunning
        {
            get { return _active != null && _active.Running; }
        }

        public bool ActiveCanFetch
        {
            get { return _active != null && !_active.Running && _active.HasMoreRows; }
        }

        public bool ActiveHasSelection
        {
            get { return _active != null && _active.Editor.SelectionLength > 0; }
        }

        /// <summary>새 SQL 탭(Ctrl+N).</summary>
        public void NewTab()
        {
            Safe(AddTab);
        }

        /// <summary>SQL 파일 열기(Ctrl+O): 여러 개를 고를 수 있다.</summary>
        public void OpenFileWithDialog()
        {
            Safe(() =>
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "SQL 파일 열기",
                    Filter = SqlFileLogic.DialogFilter,
                    Multiselect = true,
                    CheckFileExists = true
                };
                var folder = LastFolder();
                if (folder != null)
                    dialog.InitialDirectory = folder;
                if (dialog.ShowDialog(Window.GetWindow(this)) == true)
                    OpenPaths(dialog.FileNames);
            });
        }

        /// <summary>파일들을 연다(최근 파일 메뉴 등). 이미 연 파일은 그 탭으로 옮긴다.</summary>
        public void OpenPaths(IEnumerable<string> paths)
        {
            Safe(() =>
            {
                foreach (var path in paths ?? Enumerable.Empty<string>())
                    OpenPath(path);
            });
        }

        /// <summary>지금 탭 저장(Ctrl+S) 또는 다른 이름으로 저장(Ctrl+Shift+S). 저장했으면 true.</summary>
        public bool SaveActive(bool saveAs)
        {
            try
            {
                return _active != null && SaveTab(_active, saveAs);
            }
            catch (Exception ex)
            {
                ReportUnexpected(ex);
                return false;
            }
        }

        /// <summary>지금 탭 닫기(Ctrl+W). 저장하지 않은 변경이 있으면 묻는다.</summary>
        public void CloseActiveTab()
        {
            Safe(() =>
            {
                if (_active != null)
                    CloseTab(_active);
            });
        }

        public void RunActiveStatement()
        {
            RunActive();
        }

        public void CancelActiveRun()
        {
            Safe(CancelActive);
        }

        public async void FetchNextActive()
        {
            try
            {
                if (ActiveCanFetch)
                    await FetchNextAsync(_active);
            }
            catch (Exception ex)
            {
                ReportUnexpected(ex);
            }
        }

        /// <summary>줄 주석 토글(Ctrl+/). 되돌리기(Ctrl+Z) 한 번으로 돌아간다.</summary>
        public void ToggleComment()
        {
            var editor = ActiveEditor;
            if (editor == null || editor.IsReadOnly)
                return;
            ApplyEdit(editor, EditorLogic.ToggleLineComment(editor.Text, editor.SelectionStart, editor.SelectionLength));
            editor.Focus();
        }

        /// <summary>선택한 글을 대문자(Ctrl+Shift+U)·소문자(Ctrl+Shift+L)로.</summary>
        public void ChangeCase(bool upper)
        {
            var editor = ActiveEditor;
            if (editor == null || editor.IsReadOnly || editor.SelectionLength == 0)
                return;
            var start = editor.SelectionStart;
            var replaced = EditorLogic.ChangeCase(editor.SelectedText, upper);
            editor.SelectedText = replaced;
            editor.Select(start, replaced.Length);
            editor.Focus();
        }

        private static void ApplyEdit(TextBox editor, TextEdit edit)
        {
            if (edit.Length > 0 || edit.Replacement.Length > 0)
            {
                // SelectedText로 바꿔야 되돌리기 한 번에 돌아간다
                editor.Select(edit.Start, edit.Length);
                editor.SelectedText = edit.Replacement;
            }
            editor.Select(edit.SelectStart, edit.SelectLength);
        }

        private void OpenPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            var existing = _tabs.FirstOrDefault(t => SqlFileLogic.SamePath(t.FilePath, path));
            if (existing != null)
            {
                ActivateTab(existing, true);
                AddMessage(null, SqlFileLogic.AlreadyOpenMessage(path), MessageKind.Info);
                return;
            }
            SqlFileContent content;
            try
            {
                content = SqlFileLogic.Read(path);
            }
            catch (Exception ex)
            {
                var message = SqlFileLogic.OpenFailedMessage(path, ex.Message);
                AddMessage(null, message, MessageKind.Error);
                Dialogs.Show(Window.GetWindow(this), "파일을 열지 못했습니다", message, true);
                return;
            }
            var text = WorkspaceLogic.NormalizeNewlines(content.Text, Environment.NewLine);
            // 아무것도 쓰지 않은 지금 탭이면 그 탭에 연다(탭이 쌓이지 않게)
            var reuse = IsPristine(_active);
            var tab = reuse ? _active : CreateTab(NewTabTarget(), text);
            if (reuse)
                tab.Editor.Text = text;
            tab.FilePath = FullPath(path);
            tab.FileEncoding = content.Encoding;
            tab.FileNewline = content.Newline;
            tab.SavedText = tab.Editor.Text;
            tab.ShownDirty = false;
            tab.Editor.CaretIndex = 0;
            tab.Editor.ScrollToHome();
            ActivateTab(tab, true);
            RememberFile(tab.FilePath);
            AddMessage(tab.DbId, SqlFileLogic.OpenedMessage(tab.FilePath, content.Encoding), MessageKind.Info);
            ScheduleDraftSave();
            RaiseCommandStateChanged();
        }

        /// <summary>
        /// 탭 저장. 파일이 없거나 saveAs면 저장 창을 띄운다. 연 파일의 인코딩·줄바꿈을 그대로 쓰고(새 파일은 BOM 없는 UTF-8·CRLF),
        /// 다른 탭이 연 파일에는 덮어쓰지 않는다. 저장했으면 true.
        /// </summary>
        private bool SaveTab(SqlTabState tab, bool saveAs)
        {
            var owner = Window.GetWindow(this);
            var path = tab.FilePath;
            if (saveAs || path == null)
            {
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = path == null ? "SQL 파일 저장" : "다른 이름으로 저장",
                    Filter = SqlFileLogic.DialogFilter,
                    DefaultExt = SqlFileLogic.DefaultExtension,
                    AddExtension = true,
                    OverwritePrompt = true,
                    FileName = path != null ? Path.GetFileName(path) : SqlFileLogic.DefaultFileName(tab.Title)
                };
                var folder = path != null ? Path.GetDirectoryName(path) : LastFolder();
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                    dialog.InitialDirectory = folder;
                if (dialog.ShowDialog(owner) != true)
                    return false;
                path = dialog.FileName;
                var other = _tabs.FirstOrDefault(t => t != tab && SqlFileLogic.SamePath(t.FilePath, path));
                if (other != null)
                {
                    var busy = "'" + other.Title + "' 탭이 이미 이 파일을 열고 있어 저장하지 않았습니다. 그 탭에서 저장하거나 탭을 닫은 뒤 다시 저장하세요.";
                    AddMessage(null, busy, MessageKind.Error);
                    Dialogs.Show(owner, "저장하지 않았습니다", busy, true);
                    return false;
                }
            }
            var newline = tab.FileNewline ?? "\r\n";
            var encoding = tab.FilePath != null ? tab.FileEncoding : SqlFileEncoding.Utf8;
            SqlFileEncoding used;
            try
            {
                used = SqlFileLogic.Write(path, WorkspaceLogic.NormalizeNewlines(tab.Editor.Text, newline), encoding);
            }
            catch (Exception ex)
            {
                var message = SqlFileLogic.SaveFailedMessage(path, ex.Message);
                AddMessage(null, message, MessageKind.Error);
                Dialogs.Show(owner, "파일을 저장하지 못했습니다", message, true);
                return false;
            }
            tab.FilePath = FullPath(path);
            tab.FileEncoding = used;
            tab.FileNewline = newline;
            tab.SavedText = tab.Editor.Text;
            if (used != encoding)
                AddMessage(null, SqlFileLogic.FallbackMessage(tab.FilePath), MessageKind.Info);
            RememberFile(tab.FilePath);
            AddMessage(tab.DbId, SqlFileLogic.SavedMessage(tab.FilePath, used), MessageKind.Success);
            UpdateDirty(tab, true);
            ScheduleDraftSave();
            return true;
        }

        /// <summary>
        /// 닫기 전 확인: 저장하지 않은 변경(파일 탭)이나 저장한 적 없는 SQL(새 탭)이 있으면 [저장]·[저장 안 함]·[취소]를 묻는다.
        /// 닫아도 되면 true. 창을 닫을 때는 묻지 않는다(임시 저장이 다음에 되살린다).
        /// </summary>
        private bool ConfirmCloseUnsaved(SqlTabState tab)
        {
            var unsaved = tab.Dirty || (tab.FilePath == null && SqlDraftLogic.IsWorthSaving(tab.Editor.Text, tab.InitialText));
            if (!unsaved)
                return true;
            if (tab != _active)
                ActivateTab(tab, false);
            switch (Dialogs.AskSaveChanges(Window.GetWindow(this), tab.Title, tab.FilePath))
            {
                case SaveChoice.Save:
                    return SaveTab(tab, false);
                case SaveChoice.Discard:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>아무것도 쓰지 않은 탭(파일 없음, 만들 때 글 그대로, 실행·결과 없음): 파일을 그 탭에 연다.</summary>
        private static bool IsPristine(SqlTabState tab)
        {
            return tab != null && tab.FilePath == null && !tab.Running && tab.Columns == null
                && !SqlDraftLogic.IsWorthSaving(tab.Editor.Text, tab.InitialText);
        }

        /// <summary>파일 탭의 "저장하지 않은 변경" 표시가 바뀌었으면 탭 머리를 다시 그린다.</summary>
        private void UpdateDirty(SqlTabState tab, bool force)
        {
            var dirty = tab.Dirty;
            if (!force && dirty == tab.ShownDirty)
                return;
            tab.ShownDirty = dirty;
            RenderTabs();
            RaiseCommandStateChanged();
        }

        private void RememberFile(string path)
        {
            try
            {
                _host.AddRecentSqlFile(path);
                _host.LastSqlFolder = Path.GetDirectoryName(path);
            }
            catch (Exception)
            {
                // 최근 파일을 기억하지 못해도 열기·저장은 끝났다
            }
        }

        private string LastFolder()
        {
            try
            {
                var folder = _host.LastSqlFolder;
                return !string.IsNullOrEmpty(folder) && Directory.Exists(folder) ? folder : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string FullPath(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return path;
            }
        }

        private void RaiseCommandStateChanged()
        {
            var handler = CommandStateChanged;
            if (handler != null)
                handler(this, EventArgs.Empty);
        }

        private string DraftFolder()
        {
            try
            {
                var directory = _host.DataDirectory;
                return string.IsNullOrEmpty(directory) ? null : SqlDraftLogic.FolderOf(directory);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void UpdateGutter(SqlTabState tab, bool force)
        {
            var lines = WorkspaceLogic.CountLines(tab.Editor.Text);
            if (!force && lines == _gutterLines)
                return;
            _gutterLines = lines;
            _gutterText.Text = WorkspaceLogic.LineNumbers(lines);
        }

        /// <summary>
        /// 줄 번호를 편집기 첫 줄 위치와 세로 스크롤에 맞춘다. 편집기의 첫 글자 상자(캐럿 사각형)와 줄 번호의 첫 글자 상자를 맞춘다 —
        /// 글자 상자는 줄 상자(LineHeight)보다 아래에 있어서, 줄 번호의 줄 상자 위쪽을 편집기 글자 상자에 맞추면 그만큼(약 4px) 아래로 밀린다.
        /// 두 쪽 모두 처음 배치 뒤에 잰다. 재기 전에는 줄 상자 위쪽끼리(편집기 여백) 맞춘다.
        /// </summary>
        private void PositionGutter(SqlTabState tab, double verticalOffset)
        {
            var editor = tab.Editor;
            if (double.IsNaN(tab.FirstLineTop) && editor.IsLoaded)
            {
                var rect = editor.GetRectFromCharacterIndex(0);
                if (!rect.IsEmpty)
                    tab.FirstLineTop = rect.Top + editor.VerticalOffset;
            }
            if (double.IsNaN(_gutterGlyphTop) && _gutterText.IsLoaded && _gutterText.IsArrangeValid)
            {
                var run = _gutterText.Inlines.FirstInline as Run;
                var rect = run != null ? run.ContentStart.GetCharacterRect(LogicalDirection.Forward) : Rect.Empty;
                if (!rect.IsEmpty)
                    _gutterGlyphTop = rect.Top;
            }
            var top = double.IsNaN(tab.FirstLineTop) || double.IsNaN(_gutterGlyphTop)
                ? editor.Padding.Top + editor.BorderThickness.Top
                : tab.FirstLineTop - _gutterGlyphTop;
            _gutterShift.Y = top - verticalOffset;
        }

        private void UpdateCursorInfo(SqlTabState tab)
        {
            var editor = tab.Editor;
            _cursorInfo.Text = WorkspaceLogic.CursorInfo(editor.Text, editor.CaretIndex, editor.SelectionStart, editor.SelectionLength);
        }

        /// <param name="afterInput">트리 더블클릭처럼 지금 입력 처리가 끝난 뒤 그쪽이 포커스를 다시 가져갈 수 있으면 true.</param>
        private void FocusActiveEditor(bool afterInput = false)
        {
            var editor = _active != null ? _active.Editor : null;
            if (editor == null)
                return;
            if (!afterInput && editor.IsVisible && editor.Focus())
                return;
            // 창을 막 열었거나 탭을 막 바꿔 아직 배치 전이면 배치가 끝난 뒤 옮긴다
            Dispatcher.BeginInvoke(afterInput ? DispatcherPriority.Input : DispatcherPriority.Loaded, new Action(() =>
            {
                try
                {
                    if (_active != null && _active.Editor == editor)
                        editor.Focus();
                }
                catch (Exception)
                {
                    // 그 사이 창이 닫혔으면 할 일이 없다
                }
            }));
        }

        /// <summary>트리·기록에서 넣을 탭: 지금 탭이 그 DB 대상이고 실행 중이 아니면 그 탭, 아니면 그 DB의 실행 중 아닌 탭, 없으면 새 탭.</summary>
        private SqlTabState TabForInsert(string dbId, out bool moved)
        {
            moved = false;
            var active = _active;
            if (active != null && active.DbId == dbId && !active.Running)
                return active;
            moved = true;
            var tab = _tabs.FirstOrDefault(t => t.DbId == dbId && !t.Running) ?? CreateTab(dbId, "");
            ActivateTab(tab, false);
            return tab;
        }

        /// <summary>편집기 맨 위에 넣고 캐럿을 처음으로. 되돌리기(Ctrl+Z)로 뺄 수 있게 편집으로 넣는다.</summary>
        private void InsertAtTop(SqlTabState tab, string text)
        {
            var editor = tab.Editor;
            editor.Select(0, 0);
            editor.SelectedText = WorkspaceLogic.NormalizeNewlines(text, Environment.NewLine);
            editor.Select(0, 0);
            editor.ScrollToHome();
            if (tab == _active)
            {
                UpdateGutter(tab, false);
                UpdateCursorInfo(tab);
            }
            FocusActiveEditor(true);
        }

        private void InsertHistory(HistoryEntry entry)
        {
            // 접속이 삭제된 기록은 대상 없는 탭으로
            var dbId = Profile(entry.DbId) != null ? entry.DbId : null;
            bool moved;
            var tab = TabForInsert(dbId, out moved);
            InsertAtTop(tab, WorkspaceLogic.HistoryInsertText(entry.Sql));
            if (moved)
                AddMessage(dbId, WorkspaceLogic.HistoryInsertedMessage(entry.Sql, tab.Title, NameOf(dbId)), MessageKind.Info);
        }

        // ================= 실행 =================

        /// <summary>실행 버튼·Ctrl+Enter. async void 처리기이므로 예외가 밖으로 나가지 않게 전체를 감싼다.</summary>
        private async void RunActive(bool wholeScript = false)
        {
            try
            {
                var tab = _active;
                if (tab != null)
                    await RunAsync(tab, wholeScript);
            }
            catch (Exception ex)
            {
                ReportUnexpected(ex);
            }
        }

        /// <summary>
        /// 실행. 문장 하나(커서 위치, 또는 문장 하나인 선택 영역)는 지금처럼 결과 하나로,
        /// 문장이 둘 이상(선택 영역·스크립트 실행 F5)이면 차례로 실행해 조회마다 결과 하위 탭을 만든다.
        /// </summary>
        private async Task RunAsync(SqlTabState tab, bool wholeScript = false)
        {
            if (tab.Running)
                return;
            var profile = Profile(tab.DbId);
            if (profile == null)
            {
                SetStatus(tab, WorkspaceLogic.NoTargetStatus());
                return;
            }
            var dbId = profile.Id;
            var session = CheckSession(tab, profile);
            if (session == null)
                return;

            var statements = StatementsToRun(tab.Editor, wholeScript);
            if (statements.Count == 0)
            {
                AddMessage(dbId, "실행할 문장이 없습니다.", MessageKind.Error);
                SetStatus(tab, WorkspaceLogic.NoStatementStatus());
                return;
            }
            if (statements.Count > 1)
                await RunScriptAsync(tab, profile, session, statements);
            else
                await RunSingleAsync(tab, profile, session, statements[0], FetchCount());
        }

        /// <summary>문장 하나 실행(확인 창 → 실행 → 결과 하나).</summary>
        private async Task RunSingleAsync(SqlTabState tab, OracleConnectionProfile profile, DbSession session, SqlStatement statement, int fetchCount)
        {
            var dbId = profile.Id;
            if (profile.ReadOnly && !WorkspaceLogic.AllowedOnReadOnly(statement))
            {
                AddMessage(dbId, WorkspaceLogic.ReadOnlyBlockedMessage(statement.Text), MessageKind.Error);
                SetStatus(tab, WorkspaceLogic.ReadOnlyStatus());
                ShowPane(tab, WorkspacePane.Messages);
                return;
            }
            var owner = Window.GetWindow(this);
            if (statement.Danger != null && !Dialogs.ConfirmDanger(owner, profile, statement))
            {
                AddMessage(dbId, "위험한 문장 실행을 취소했습니다.", MessageKind.Info);
                return;
            }
            if (statement.Kind == SqlKind.Ddl && session.HasPendingChanges && !Dialogs.ConfirmDdlWithPending(owner, profile, session.PendingText))
            {
                AddMessage(dbId, "DDL 실행을 취소했습니다.", MessageKind.Info);
                return;
            }
            // 확인 창을 띄운 사이 다른 실행이 끝나거나 연결 상태가 바뀌었을 수 있다
            if (tab.Running || tab.DbId != dbId)
                return;
            session = CheckSession(tab, profile);
            if (session == null)
                return;

            BeginRun(tab, session, dbId, false, 0);
            var watch = tab.RunWatch;
            var token = tab.RunCancel.Token;
            var sent = false;
            try
            {
                await ClosePreviousCursorAsync(tab);
                sent = true;
                var result = await session.ExecuteAsync(statement, fetchCount, token);
                var elapsed = watch.Elapsed;
                AddHistory(dbId, statement.Text, elapsed);
                ApplyResult(tab, session, dbId, statement, result, elapsed);
            }
            catch (Exception ex)
            {
                OnRunFailed(tab, session, profile, statement, ex, watch.Elapsed, sent && !(ex is SessionBusyException));
            }
            finally
            {
                EndRun(tab);
            }
        }

        /// <summary>실행 전 세션 확인. 쓸 수 없으면 상태줄에 이유(연결·다시 연결 버튼)를 보이고 null.</summary>
        private DbSession CheckSession(SqlTabState tab, OracleConnectionProfile profile)
        {
            var session = _host.GetSession(profile.Id);
            if (session == null)
            {
                SetStatus(tab, WorkspaceLogic.NotConnectedStatus(profile.Name));
                return null;
            }
            if (session.IsBroken)
            {
                SetStatus(tab, WorkspaceLogic.BrokenStatus(profile.Name));
                return null;
            }
            // 같은 DB의 다른 탭이 세션을 쓰는 중(DB마다 세션 1개) — 확인 창을 띄우기 전에 알린다
            if (IsRunningOther(profile.Id, tab))
            {
                SetStatus(tab, WorkspaceLogic.BusyStatus(profile.Name));
                return null;
            }
            return session;
        }

        /// <summary>
        /// 실행할 문장들. 스크립트 실행(F5)은 편집기 전체, 선택 영역은 그 안의 문장들(문장 하나면 지금처럼 선택 전체를 한 문장으로),
        /// 선택이 없으면 커서가 있는 문장 하나. 여러 문장은 <see cref="SqlScript.SplitForRun"/>으로 나눈다(문장 안의 빈 줄 허용).
        /// </summary>
        private static List<SqlStatement> StatementsToRun(TextBox editor, bool wholeScript)
        {
            if (wholeScript)
                return SqlScript.SplitForRun(editor.Text);
            if (editor.SelectionLength > 0)
            {
                var parts = SqlScript.SplitForRun(editor.SelectedText);
                if (parts.Count > 1)
                    return parts;
                var one = SqlScript.Parse(editor.SelectedText);
                return one == null ? new List<SqlStatement>() : new List<SqlStatement> { one };
            }
            var atCaret = SqlScript.AtCaret(editor.Text, editor.CaretIndex);
            return atCaret == null ? new List<SqlStatement>() : new List<SqlStatement> { atCaret };
        }

        /// <summary>
        /// 여러 문장을 차례로 실행한다. 조회마다 결과 하위 탭(최대 MaxResultSets개)을 만들고, 오류가 나면 그 문장에서 멈춘다.
        /// 읽기 전용이면 막히는 문장이 하나라도 있으면 아무것도 실행하지 않는다. 위험한 문장은 시작 전에 하나씩 확인받는다(중간에 묻지 않게).
        /// </summary>
        private async Task RunScriptAsync(SqlTabState tab, OracleConnectionProfile profile, DbSession session, List<SqlStatement> statements)
        {
            var dbId = profile.Id;
            var total = statements.Count;
            if (profile.ReadOnly)
            {
                var blocked = statements.FirstOrDefault(s => !WorkspaceLogic.AllowedOnReadOnly(s));
                if (blocked != null)
                {
                    AddMessage(dbId, WorkspaceLogic.ReadOnlyBlockedMessage(blocked.Text) + " 스크립트를 실행하지 않았습니다.", MessageKind.Error);
                    SetStatus(tab, WorkspaceLogic.ReadOnlyStatus());
                    ShowPane(tab, WorkspacePane.Messages);
                    return;
                }
            }
            var owner = Window.GetWindow(this);
            foreach (var dangerous in statements.Where(s => s.Danger != null))
            {
                if (!Dialogs.ConfirmDanger(owner, profile, dangerous))
                {
                    AddMessage(dbId, "위험한 문장 실행을 취소해 스크립트를 실행하지 않았습니다.", MessageKind.Info);
                    return;
                }
            }
            if (statements.Any(s => s.Kind == SqlKind.Ddl) && session.HasPendingChanges && !Dialogs.ConfirmDdlWithPending(owner, profile, session.PendingText))
            {
                AddMessage(dbId, "DDL이 있는 스크립트 실행을 취소했습니다.", MessageKind.Info);
                return;
            }
            // 확인 창을 띄운 사이 다른 실행이 끝나거나 연결 상태가 바뀌었을 수 있다
            if (tab.Running || tab.DbId != dbId)
                return;
            session = CheckSession(tab, profile);
            if (session == null)
                return;

            var fetchCount = FetchCount();
            BeginRun(tab, session, dbId, false, 0);
            tab.ScriptTotal = total;
            tab.ScriptIndex = 0;
            var watch = tab.RunWatch;
            var token = tab.RunCancel.Token;
            var executed = 0;
            var queries = 0;
            var failedAt = 0;
            var cancelled = false;
            var current = statements[0];
            try
            {
                await ClosePreviousCursorAsync(tab);
                DropResultGrids(tab);
                tab.ClearResults();
                if (tab == _active)
                    RenderResults();
                for (var i = 0; i < total; i++)
                {
                    current = statements[i];
                    tab.ScriptIndex = i + 1;
                    if (tab == _active)
                        RenderStatus();
                    if (token.IsCancellationRequested)
                    {
                        cancelled = true;
                        break;
                    }
                    var started = watch.Elapsed;
                    ExecuteResult result;
                    try
                    {
                        result = await session.ExecuteAsync(current, fetchCount, token);
                    }
                    catch (Exception ex) when (!(ex is SessionBusyException))
                    {
                        AddHistory(dbId, current.Text, watch.Elapsed - started);
                        if (DbSession.IsCancellation(ex))
                        {
                            cancelled = true;
                            AddMessage(dbId, WorkspaceLogic.ScriptStep(i + 1, total) + "실행을 취소했습니다.", MessageKind.Error);
                        }
                        else
                        {
                            failedAt = i + 1;
                            AddMessage(dbId, WorkspaceLogic.ScriptStep(i + 1, total) + WorkspaceLogic.ErrorMessage(DbSession.DescribeError(ex), current.Text), MessageKind.Error);
                        }
                        break;
                    }
                    var elapsed = watch.Elapsed - started;
                    executed++;
                    AddHistory(dbId, current.Text, elapsed);
                    var warned = !string.IsNullOrEmpty(result.Warning);
                    AddMessage(dbId, WorkspaceLogic.ScriptStep(i + 1, total) + WorkspaceLogic.ExecutedMessage(result.Summary, elapsed, current.Text),
                        warned ? MessageKind.Error : MessageKind.Success);
                    if (warned)
                    {
                        AddMessage(dbId, result.Warning, MessageKind.Error);
                        foreach (var detail in result.WarningDetails)
                            AddMessage(dbId, detail, MessageKind.Error);
                    }
                    if (result.Kind != SqlKind.Query || result.Cursor == null)
                        continue;
                    queries++;
                    if (tab.Results.Count < WorkspaceLogic.MaxResultSets)
                    {
                        tab.AddResult(session, dbId, result, current).LastElapsed = elapsed;
                        if (tab == _active)
                            RenderResultStrip();
                    }
                    else
                    {
                        // 그리드로 남기지 않는 결과의 커서는 바로 닫는다(서버의 열린 커서 수를 아끼려고)
                        try
                        {
                            await session.CloseCursorAsync(result.Cursor);
                        }
                        catch (Exception)
                        {
                            // 닫지 못한 커서는 세션을 닫을 때 함께 닫힌다
                        }
                        AddMessage(dbId, WorkspaceLogic.TooManyResultsMessage(i + 1), MessageKind.Info);
                    }
                }

                if (tab.Results.Count > 0)
                {
                    tab.ActiveResult = tab.Results[0];
                    ShowGrid(tab);
                }
                var stopped = failedAt > 0 || cancelled;
                AddMessage(dbId, WorkspaceLogic.ScriptSummary(executed, total, queries, failedAt, cancelled, watch.Elapsed), stopped ? MessageKind.Error : MessageKind.Info);
                if (tab.Results.Count > 0)
                    SetStatus(tab, FetchStatusOf(tab, WorkspaceLogic.ScriptLead(executed, total, tab.Results.Count, failedAt, cancelled)));
                else
                    SetStatus(tab, WorkspaceLogic.TextStatus(WorkspaceLogic.ScriptSummary(executed, total, queries, failedAt, cancelled, watch.Elapsed)));
                ShowPane(tab, stopped || tab.Results.Count == 0 ? WorkspacePane.Messages : WorkspacePane.Grid);
                if (tab != _active)
                    tab.Done = true;
                RefreshCursorStates(dbId);
            }
            catch (Exception ex)
            {
                // 이전 커서를 닫으려는데 같은 DB가 바쁨(SessionBusyException) 등 — 문장 하나 실행과 같이 알린다
                OnRunFailed(tab, session, profile, current, ex, watch.Elapsed, false);
            }
            finally
            {
                tab.ScriptTotal = 0;
                tab.ScriptIndex = 0;
                EndRun(tab);
            }
        }

        /// <summary>스크립트 실행(F5): 편집기의 모든 문장.</summary>
        public void RunActiveScript()
        {
            RunActive(true);
        }

        /// <summary>탭의 결과 그리드를 모두 화면에서 뗀다(결과를 바꾸거나 탭을 닫을 때).</summary>
        private void DropResultGrids(SqlTabState tab)
        {
            foreach (var result in tab.Results)
            {
                if (result.Grid != null)
                    _gridHost.Children.Remove(result.Grid.View);
                result.Grid = null;
            }
        }

        /// <summary>결과 하위 탭 막대: 지금 탭의 결과가 둘 이상일 때만 보인다(결과 하나면 지금처럼 막대 없이).</summary>
        private void RenderResultStrip()
        {
            var tab = _active;
            _resultTabs.Children.Clear();
            var show = tab != null && tab.Results.Count > 1;
            _resultStrip.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show)
                return;
            foreach (var result in tab.Results)
            {
                var item = result;
                var selected = item == tab.ActiveResult;
                var label = Theme.Text(WorkspaceLogic.ResultTabTitle(item.Number, item.Sql, item.Rows.Count, item.HasMoreRows),
                    selected ? Theme.PrimaryText : Theme.SecondaryText);
                if (selected)
                    label.FontWeight = FontWeights.SemiBold;
                Border underline;
                var button = WorkspaceUi.SelectorButton(label, out underline);
                WorkspaceUi.SetSelected(button, underline, selected);
                button.ToolTip = WorkspaceLogic.FirstLine(item.Sql, 200);
                AutomationProperties.SetName(button, label.Text);
                button.Click += (s, e) => Safe(() => SelectResult(tab, item));
                _resultTabs.Children.Add(button);
                if (selected)
                    WorkspaceUi.BringIntoViewWhenLoaded(button);
            }
        }

        /// <summary>▾ 결과 목록: 지금 탭의 결과 하위 탭 모두(고른 것은 굵게).</summary>
        private void FillResultList(ContextMenu menu)
        {
            var tab = _active;
            if (tab == null)
                return;
            foreach (var result in tab.Results)
            {
                var item = result;
                var text = WorkspaceLogic.ResultTabTitle(item.Number, item.Sql, item.Rows.Count, item.HasMoreRows);
                menu.Items.Add(WorkspaceUi.PlainMenuItem(text, item == tab.ActiveResult, () => Safe(() => SelectResult(tab, item))));
            }
        }

        /// <summary>결과 하위 탭을 골랐다: 그 결과의 그리드·상태줄([다음 행 가져오기]도 그 결과)을 보인다.</summary>
        private void SelectResult(SqlTabState tab, ResultSetState result)
        {
            if (tab != _active || !tab.Results.Contains(result) || tab.ActiveResult == result)
                return;
            tab.ActiveResult = result;
            if (result.Grid == null)
                ShowGrid(tab);
            if (!tab.Running)
                SetStatus(tab, FetchStatusOf(tab));
            RenderResults();
            RenderStatus();
            SelectPane(WorkspacePane.Grid);
        }


        /// <summary>이 탭의 이전 커서를 닫는다(같은 연결의 다른 커서는 세션이 그대로 둔다). 세션이 바쁘면 SessionBusyException.</summary>
        private async Task ClosePreviousCursorAsync(SqlTabState tab)
        {
            var hadMore = false;
            string dbId = null;
            foreach (var result in tab.Results.ToList())
            {
                if (!result.HoldsOpenCursor)
                    continue;
                try
                {
                    await result.CursorSession.CloseCursorAsync(result.Cursor);
                }
                catch (SessionBusyException)
                {
                    throw;
                }
                catch (Exception)
                {
                    // 끊긴 세션 등 — 커서는 쓸 수 없게 됐고 실제 문제는 이어지는 실행이 알린다
                }
                result.CursorReleased = true;
                hadMore |= result.CursorHadMore;
                dbId = result.ResultDbId;
            }
            if (hadMore)
                AddMessage(dbId, "다른 문장을 실행해 '" + tab.Title + "' 탭의 이전 커서를 닫았습니다.", MessageKind.Info);
        }

        private void ApplyResult(SqlTabState tab, DbSession session, string dbId, SqlStatement statement, ExecuteResult result, TimeSpan elapsed)
        {
            var background = tab != _active;
            // 실행은 됐어도 경고(컴파일 오류 ORA-24344 등)는 오류로 보인다 — 예외가 없어 성공처럼 보이기 쉽다
            var warned = !string.IsNullOrEmpty(result.Warning);
            AddMessage(dbId, WorkspaceLogic.ExecutedMessage(result.Summary, elapsed, statement.Text), warned ? MessageKind.Error : MessageKind.Success);
            if (warned)
            {
                AddMessage(dbId, result.Warning, MessageKind.Error);
                foreach (var detail in result.WarningDetails)
                    AddMessage(dbId, detail, MessageKind.Error);
            }
            if (result.Kind == SqlKind.Query && result.Cursor != null)
            {
                DropResultGrids(tab);
                tab.SetResult(session, dbId, result, statement).LastElapsed = elapsed;
                ShowGrid(tab);
                SetStatus(tab, FetchStatusOf(tab));
                ShowPane(tab, WorkspacePane.Grid);
            }
            else
            {
                SetStatus(tab, WorkspaceLogic.ExecutedStatus(statement, result, elapsed));
                ShowPane(tab, WorkspacePane.Messages);
            }
            if (background)
                tab.Done = true;
            // COMMIT·ROLLBACK·DDL로 트랜잭션이 끝나면 같은 DB 다른 탭의 FOR UPDATE 커서가 닫힌다
            RefreshCursorStates(dbId);
        }

        private void OnRunFailed(SqlTabState tab, DbSession session, OracleConnectionProfile profile, SqlStatement statement, Exception ex, TimeSpan elapsed, bool sent)
        {
            var dbId = profile.Id;
            if (ex is SessionBusyException)
            {
                // 같은 DB에서 바쁜 동안 다시 눌러도 메시지가 쌓이지 않게 상태줄에만 알린다
                SetStatus(tab, IsRunningOther(dbId, tab) ? WorkspaceLogic.BusyStatus(profile.Name) : WorkspaceLogic.BusyOtherStatus(profile.Name));
                return;
            }
            if (sent)
                AddHistory(dbId, statement.Text, elapsed);
            if (DbSession.IsCancellation(ex))
            {
                AddMessage(dbId, "실행을 취소했습니다.", MessageKind.Error);
                SetStatus(tab, WorkspaceLogic.TextStatus("취소됨"));
            }
            else
            {
                AddMessage(dbId, WorkspaceLogic.ErrorMessage(DbSession.DescribeError(ex), statement.Text), MessageKind.Error);
                SetStatus(tab, session.IsBroken ? WorkspaceLogic.BrokenStatus(profile.Name) : WorkspaceLogic.TextStatus("오류 — 메시지 탭을 보세요."));
                ShowPane(tab, WorkspacePane.Messages);
            }
            if (tab != _active)
                tab.Done = true;
            RefreshCursorStates(dbId);
        }

        // ================= 다음 행 =================

        private async Task FetchNextAsync(SqlTabState tab)
        {
            if (tab.Running || tab.Cursor == null || tab.CursorSession == null)
                return;
            var cursor = tab.Cursor;
            var session = tab.CursorSession;
            var dbId = tab.ResultDbId;
            var set = tab.ActiveResult;
            if (!tab.HasMoreRows)
            {
                SetStatus(tab, FetchStatusOf(tab));
                if (tab.CursorClosedEarly)
                    AddMessage(dbId, "커서가 이미 닫혀 더 가져올 수 없습니다. 다시 실행하세요.", MessageKind.Info);
                return;
            }
            var name = NameOf(dbId);
            if (IsRunningOther(dbId, tab))
            {
                SetStatus(tab, WorkspaceLogic.BusyFetchStatus(name, true));
                return;
            }
            var count = FetchCount();
            var from = set.Rows.Count + 1;
            BeginRun(tab, session, dbId, true, count);
            var watch = tab.RunWatch;
            var token = tab.RunCancel.Token;
            try
            {
                var rows = await session.FetchAsync(cursor, count, token);
                var elapsed = watch.Elapsed;
                var first = set.AppendRows(rows);
                set.LastElapsed = elapsed;
                if (set.Grid != null)
                    set.Grid.FitRowNumbers(set.Rows.Count);
                AddMessage(dbId, WorkspaceLogic.FetchedMessage(rows.Count, from, set.Rows.Count, elapsed, !set.HasMoreRows), MessageKind.Success);
                if (tab.ActiveResult == set)
                    SetStatus(tab, FetchStatusOf(tab));
                if (tab == _active)
                {
                    RenderResultsHeader();
                    if (first != null && set.Grid != null && tab.ActiveResult == set)
                        set.Grid.ScrollIntoView(first);
                }
                else
                {
                    tab.Done = true;
                    tab.PendingPane = WorkspacePane.Grid;
                }
            }
            catch (Exception ex)
            {
                if (ex is SessionBusyException)
                {
                    SetStatus(tab, WorkspaceLogic.BusyFetchStatus(name, IsRunningOther(dbId, tab)));
                }
                else if (DbSession.IsCancellation(ex))
                {
                    AddMessage(dbId, "실행을 취소했습니다.", MessageKind.Error);
                    SetStatus(tab, FetchStatusOf(tab, "취소됨"));
                }
                else
                {
                    AddMessage(dbId, WorkspaceLogic.FetchErrorMessage(DbSession.DescribeError(ex)), MessageKind.Error);
                    SetStatus(tab, session.IsBroken ? WorkspaceLogic.BrokenStatus(name) : FetchStatusOf(tab, "오류 — 메시지 탭을 보세요."));
                    ShowPane(tab, WorkspacePane.Messages);
                }
                if (tab != _active)
                    tab.Done = true;
            }
            finally
            {
                EndRun(tab);
            }
        }

        private void BeginRun(SqlTabState tab, DbSession session, string dbId, bool fetching, int fetchCount)
        {
            tab.Running = true;
            tab.Fetching = fetching;
            tab.FetchingCount = fetchCount;
            tab.CancelRequested = false;
            tab.RunningSession = session;
            tab.RunningDbId = dbId;
            tab.RunWatch = Stopwatch.StartNew();
            tab.RunCancel = new CancellationTokenSource();
            tab.Done = false;
            RenderTabs();
            RenderRunState();
            RenderTarget();
            RenderStatus();
            if (!_elapsedTimer.IsEnabled)
                _elapsedTimer.Start();
            NotifyStateChanged(dbId);
        }

        private void EndRun(SqlTabState tab)
        {
            var dbId = tab.RunningDbId;
            var session = tab.RunningSession;
            tab.Running = false;
            tab.Fetching = false;
            tab.CancelRequested = false;
            tab.RunningSession = null;
            tab.RunningDbId = null;
            tab.RunCancel = null;
            if (tab.RunWatch != null)
                tab.RunWatch.Stop();
            if (!AnyRunning)
                _elapsedTimer.Stop();
            // 실행하는 동안 접속이 삭제됐으면 이제 대상을 비운다
            var targetDropped = DropMissingTarget(tab) && tab == _active;
            RenderTabs();
            RenderRunState();
            RenderTarget();
            RenderResults();
            RenderStatus();
            FlushOrphanCursors(session);
            NotifyStateChanged(dbId);
            if (targetDropped)
                RaiseActiveTabChanged();
        }

        private void RequestCancel(SqlTabState tab)
        {
            var cancel = tab.RunCancel;
            if (cancel == null)
                return;
            tab.CancelRequested = true;
            if (tab == _active)
                RenderStatus();
            // 취소 콜백(DbCommand.Cancel)은 서버에 중단 신호를 보내는 네트워크 호출이라 UI 스레드를 막지 않게 작업 스레드에서 한다
            Task.Run(() =>
            {
                try
                {
                    cancel.Cancel();
                }
                catch (Exception)
                {
                    // 막 끝났거나 끊긴 명령 — 실행 쪽이 결과(취소·오류)로 알린다
                }
            });
        }

        private void CancelActive()
        {
            var tab = _active;
            if (tab != null && tab.Running)
                RequestCancel(tab);
        }

        // ================= 결과 =================

        private void ShowGrid(SqlTabState tab)
        {
            if (tab.Grid == null)
            {
                var owner = tab;
                tab.Grid = new ResultGridView(all => CopyRows(owner, all), text => CopyText(text, null));
                tab.Grid.Sorted += (s, e) => Safe(() => OnGridSorted(owner));
                // 그리드는 결과마다 하나 — 메뉴는 이 결과가 빠른 조회(F1, 테이블 하나)일 때만
                var result = tab.ActiveResult;
                tab.Grid.AddMenuAction("INSERT 문 만들기 (선택한 행 → 클립보드)", () => result.Edit != null, () => Safe(() => CopyInsertScript(result)));
                tab.Grid.View.Visibility = Visibility.Collapsed;
                _gridHost.Children.Add(tab.Grid.View);
            }
            tab.Grid.Show(tab.Columns, tab.Rows);
            if (tab == _active)
                RenderResults();
        }

        /// <summary>열 머리로 정렬함: 더 가져올 행이 남았으면 "가져온 행 안에서만"임을 결과마다 한 번 알린다.</summary>
        private void OnGridSorted(SqlTabState tab)
        {
            if (tab.Grid == null || tab.Grid.SortColumn < 0 || !tab.HasMoreRows || tab.SortNoticeShown)
                return;
            tab.SortNoticeShown = true;
            AddMessage(tab.ResultDbId, WorkspaceLogic.SortPartialMessage(tab.Rows != null ? tab.Rows.Count : 0), MessageKind.Info);
        }

        private void RenderResults()
        {
            var tab = _active;
            foreach (var t in _tabs)
            {
                foreach (var result in t.Results)
                {
                    if (result.Grid != null)
                        result.Grid.View.Visibility = t == tab && result == t.ActiveResult ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            RenderResultStrip();
            RenderEditBar();
            var hasGrid = tab != null && tab.Columns != null && tab.Grid != null;
            _gridPlaceholder.Visibility = hasGrid ? Visibility.Collapsed : Visibility.Visible;
            if (!hasGrid)
            {
                var profile = tab != null ? Profile(tab.DbId) : null;
                _gridPlaceholder.Text = profile != null ? profile.Name + "에서 실행한 결과가 여기에 표시됩니다." : "실행 결과가 여기에 표시됩니다.";
            }
            RenderResultsHeader();
        }

        private void RenderResultsHeader()
        {
            var tab = _active;
            var rows = tab != null && tab.Rows != null ? tab.Rows.Count : 0;
            _gridTab.SetCount(rows);
            _messagesTab.SetCount(_messages.Count);
            _gridTab.SetSelected(_pane == WorkspacePane.Grid);
            _messagesTab.SetSelected(_pane == WorkspacePane.Messages);
            _historyTab.SetSelected(_pane == WorkspacePane.History);
            _copy.IsEnabled = rows > 0;
        }

        private void SelectPane(WorkspacePane pane)
        {
            _pane = pane;
            _gridArea.Visibility = pane == WorkspacePane.Grid ? Visibility.Visible : Visibility.Collapsed;
            _messages.View.Visibility = pane == WorkspacePane.Messages ? Visibility.Visible : Visibility.Collapsed;
            _history.View.Visibility = pane == WorkspacePane.History ? Visibility.Visible : Visibility.Collapsed;
            RenderResultsHeader();
        }

        /// <summary>지금 탭이면 그 칸을 바로 보이고, 뒤의 탭이면 그 탭으로 옮길 때 보인다.</summary>
        private void ShowPane(SqlTabState tab, WorkspacePane pane)
        {
            if (tab == _active)
                SelectPane(pane);
            else
                tab.PendingPane = pane;
        }

        private void AddHistory(string dbId, string sql, TimeSpan elapsed)
        {
            _history.Add(new HistoryEntry { Time = DateTime.Now, DbId = dbId, Sql = sql, Elapsed = elapsed }, Profile(dbId));
        }

        private async void CopyAll_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var tab = _active;
                if (tab != null)
                    await CopyRowsAsync(tab, true);
            }
            catch (Exception ex)
            {
                ReportUnexpected(ex);
            }
        }

        private async void CopyRows(SqlTabState tab, bool all)
        {
            try
            {
                await CopyRowsAsync(tab, all);
            }
            catch (Exception ex)
            {
                ReportUnexpected(ex);
            }
        }

        private async Task CopyRowsAsync(SqlTabState tab, bool all)
        {
            if (tab.Rows == null || tab.Rows.Count == 0 || tab.Columns == null)
                return;
            if (all)
            {
                // 화면 순서(열 머리로 정렬했으면 정렬한 순서)
                var rows = tab.Grid != null ? tab.Grid.RowsInViewOrder() : tab.Rows.ToList();
                var tsv = WorkspaceLogic.ToTsv(tab.Columns.Select(c => c.Name), rows.Select(r => r.Values));
                await CopyTextAsync(tsv, WorkspaceLogic.CopiedMessage(rows.Count), tab.ResultDbId);
            }
            else if (tab.Grid != null)
            {
                var rows = tab.Grid.SelectedRows();
                if (rows.Count > 0)
                    await CopyTextAsync(WorkspaceLogic.ToTsv(null, rows.Select(r => r.Values)), null, tab.ResultDbId);
            }
        }

        private async void CopyText(string text, string doneMessage)
        {
            try
            {
                await CopyTextAsync(text, doneMessage, null);
            }
            catch (Exception ex)
            {
                ReportUnexpected(ex);
            }
        }

        private async Task CopyTextAsync(string text, string doneMessage, string dbId)
        {
            var error = await ResultClipboard.SetTextAsync(text);
            if (error != null)
                AddMessage(null, "클립보드에 복사하지 못했습니다: " + error, MessageKind.Error);
            else if (doneMessage != null)
                AddMessage(dbId, doneMessage, MessageKind.Info);
        }

        // ================= 상태줄 =================

        private void SetStatus(SqlTabState tab, WorkspaceLogic.StatusInfo status)
        {
            tab.Status = status;
            if (tab == _active)
                RenderStatus();
        }

        private WorkspaceLogic.StatusInfo FetchStatusOf(SqlTabState tab, string lead = null)
        {
            var rows = tab.Rows != null ? tab.Rows.Count : 0;
            return WorkspaceLogic.FetchStatus(rows, tab.LastElapsed, tab.HasMoreRows, tab.CursorClosedEarly, tab.StrippedSemicolon, lead);
        }

        private static WorkspaceLogic.StatusInfo RunningStatusOf(SqlTabState tab)
        {
            var elapsed = tab.RunWatch != null ? tab.RunWatch.Elapsed : TimeSpan.Zero;
            return tab.Fetching
                ? WorkspaceLogic.FetchingStatus(tab.FetchingCount, elapsed, tab.CancelRequested)
                : WorkspaceLogic.RunningStatus(elapsed, tab.CancelRequested,
                    tab.ScriptTotal > 1 ? tab.ScriptIndex + "/" + tab.ScriptTotal : null);
        }

        private void RenderStatus()
        {
            var tab = _active;
            var status = tab == null ? WorkspaceLogic.ReadyStatus() : tab.Running ? RunningStatusOf(tab) : tab.Status ?? WorkspaceLogic.ReadyStatus();
            _statusText.Inlines.Clear();
            foreach (var segment in status.Segments)
            {
                if (segment.NewPart)
                    _statusText.Inlines.Add(new Run(" "));
                var run = new Run(segment.Text);
                if (segment.Strong)
                {
                    run.FontWeight = FontWeights.SemiBold;
                    run.SetResourceReference(TextElement.ForegroundProperty, Theme.PrimaryText);
                }
                _statusText.Inlines.Add(run);
            }
            _statusText.ToolTip = status.PlainText;

            var action = status.Action;
            // 다른 안내(바쁨·읽기 전용 등)를 보이는 동안에도 열린 커서의 다음 행 버튼은 남긴다
            if (action == WorkspaceLogic.StatusAction.None && tab != null && !tab.Running && tab.HasMoreRows)
                action = WorkspaceLogic.StatusAction.FetchNext;
            _statusAction.Tag = action;
            switch (action)
            {
                case WorkspaceLogic.StatusAction.None:
                    _statusAction.Visibility = Visibility.Collapsed;
                    break;
                case WorkspaceLogic.StatusAction.FetchNext:
                    var more = tab != null && tab.HasMoreRows;
                    _statusAction.Content = WorkspaceLogic.FetchButtonText(more, FetchCount());
                    _statusAction.IsEnabled = more && !tab.Running;
                    _statusAction.Visibility = Visibility.Visible;
                    break;
                default:
                    _statusAction.Content = status.ActionText;
                    _statusAction.IsEnabled = tab != null && !tab.Running && !tab.Connecting;
                    _statusAction.Visibility = Visibility.Visible;
                    break;
            }
        }

        private void RenderRunState()
        {
            var tab = _active;
            var running = tab != null && tab.Running;
            _run.IsEnabled = tab != null && !running;
            _cancel.IsEnabled = running;
            _target.IsEnabled = tab != null && !running;
            RenderEditBar();
            RaiseCommandStateChanged();
        }

        private async void StatusAction_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var tab = _active;
                if (tab == null || tab.Running || !(_statusAction.Tag is WorkspaceLogic.StatusAction))
                    return;
                switch ((WorkspaceLogic.StatusAction)_statusAction.Tag)
                {
                    case WorkspaceLogic.StatusAction.Connect:
                        await ConnectFromStatusAsync(tab, false);
                        break;
                    case WorkspaceLogic.StatusAction.Reconnect:
                        await ConnectFromStatusAsync(tab, true);
                        break;
                    case WorkspaceLogic.StatusAction.FetchNext:
                        await FetchNextAsync(tab);
                        break;
                }
            }
            catch (Exception ex)
            {
                ReportUnexpected(ex);
            }
        }

        private async Task ConnectFromStatusAsync(SqlTabState tab, bool reconnect)
        {
            var dbId = tab.DbId;
            if (Profile(dbId) == null || tab.Connecting)
                return;
            tab.Connecting = true;
            RenderStatus();
            var connected = false;
            try
            {
                connected = await _host.ConnectAsync(dbId);
            }
            catch (Exception ex)
            {
                AddMessage(dbId, "연결하지 못했습니다: " + DbSession.DescribeError(ex), MessageKind.Error);
            }
            finally
            {
                tab.Connecting = false;
            }
            // 다시 연결하면 끊긴 세션의 커서는 모두 닫혔다
            RefreshCursorStates(dbId);
            if (connected && tab.DbId == dbId && !tab.Running)
                SetStatus(tab, WorkspaceLogic.ConnectedStatus(reconnect));
            _stateSignature = null;
            RenderTarget();
            RenderStatus();
        }

        /// <summary>트랜잭션 끝·다시 연결 등으로 밖에서 닫힌 커서를 보이던 탭의 상태줄을 "커서 닫힘"으로 맞춘다.</summary>
        private void RefreshCursorStates(string dbId)
        {
            foreach (var tab in _tabs)
            {
                if (tab.Running || tab.Cursor == null || tab.ResultDbId != dbId || tab.Status == null || !tab.Status.ShowsCursor)
                    continue;
                if (tab.CursorHadMore && !tab.HasMoreRows)
                    SetStatus(tab, FetchStatusOf(tab));
            }
        }

        // ================= 타이머 =================

        private void ElapsedTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                var tab = _active;
                if (tab != null && tab.Running)
                    RenderStatus();
                if (!AnyRunning)
                    _elapsedTimer.Stop();
            }
            catch (Exception ex)
            {
                _elapsedTimer.Stop();
                ReportUnexpected(ex);
            }
        }

        private void StateTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                RefreshHostState();
            }
            catch (Exception ex)
            {
                // 타이머를 멈추면 대상 목록의 "(연결 안 됨)"이 다시는 맞춰지지 않는다 — 계속 돌리되 같은 오류를 매초 쌓지 않는다
                if (!_stateTimerFailed)
                    ReportUnexpected(ex);
                _stateTimerFailed = true;
            }
        }

        private void RefreshHostState()
        {
            var signature = StateSignature();
            if (signature == _stateSignature)
                return;
            _stateSignature = signature;
            // 트리·툴바에서 연결했으면 [연결] 안내는 더 맞지 않는다
            foreach (var tab in _tabs)
            {
                var status = tab.Status;
                if (tab.Running || status == null || tab.DbId == null)
                    continue;
                var session = _host.GetSession(tab.DbId);
                var usable = session != null && !session.IsBroken;
                if (usable && (status.Action == WorkspaceLogic.StatusAction.Connect || status.Action == WorkspaceLogic.StatusAction.Reconnect))
                    SetStatus(tab, WorkspaceLogic.ConnectedStatus(status.Action == WorkspaceLogic.StatusAction.Reconnect));
            }
            // 연결하거나 끊으면 탭 배지 색이 열린 세션의 값과 저장된 값 사이에서 바뀐다
            RenderTabs();
            RenderTarget();
            RenderStatus();
            RenderEditBar();
        }

        // 대상 목록·상태줄이 의존하는 바깥 상태: 접속마다 연결 여부·끊김, 가져올 행 수
        private string StateSignature()
        {
            var sb = new StringBuilder();
            foreach (var profile in Profiles())
            {
                var session = _host.GetSession(profile.Id);
                sb.Append(profile.Id).Append(session == null ? '-' : session.IsBroken ? 'b' : 'c').Append(';');
            }
            sb.Append(FetchCount());
            return sb.ToString();
        }

        private void Workspace_Loaded(object sender, RoutedEventArgs e)
        {
            Safe(() =>
            {
                lock (LiveDraftOwners)
                    LiveDraftOwners.Add(_draftOwner);
                _stateTimer.Start();
                if (AnyRunning)
                    _elapsedTimer.Start();
                if (_active != null)
                    PositionGutter(_active, _active.Editor.VerticalOffset);
            });
        }

        private void Workspace_Unloaded(object sender, RoutedEventArgs e)
        {
            // 멈추지 않으면 Dispatcher가 타이머를 통해 닫힌 창의 화면을 계속 붙잡는다
            _stateTimer.Stop();
            _elapsedTimer.Stop();
            // 창을 닫음: 마지막 내용을 저장하고, 다음에 여는 창이 넘겨받을 수 있게 열린 창 목록에서 뺀다
            Safe(() => SaveDrafts());
            lock (LiveDraftOwners)
                LiveDraftOwners.Remove(_draftOwner);
        }

        // ================= 커서 정리 =================

        /// <summary>탭이 쥔 열린 커서를 놓고 닫는다(세션이 바쁘면 나중에). 놓은 커서가 있었으면 true.</summary>
        private bool ReleaseCursor(SqlTabState tab)
        {
            var open = tab.Results.Where(r => r.HoldsOpenCursor).ToList();
            if (open.Count == 0)
                return false;
            foreach (var result in open)
                result.CursorReleased = true;
            foreach (var group in open.GroupBy(r => r.CursorSession))
                CloseCursorsQuietly(group.Key, group.Select(r => r.Cursor).ToList());
            return true;
        }

        private void FlushOrphanCursors(DbSession session)
        {
            _orphanCursors.RemoveAll(p => p.Value.IsClosed);
            if (session == null || _orphanCursors.Count == 0)
                return;
            var cursors = _orphanCursors.Where(p => ReferenceEquals(p.Key, session)).Select(p => p.Value).ToList();
            if (cursors.Count == 0)
                return;
            _orphanCursors.RemoveAll(p => ReferenceEquals(p.Key, session));
            CloseCursorsQuietly(session, cursors);
        }

        /// <summary>커서들을 차례로 닫는다. async void이므로 예외가 밖으로 나가지 않게 전체를 감싼다.</summary>
        private async void CloseCursorsQuietly(DbSession session, List<QueryCursor> cursors)
        {
            try
            {
                for (var i = 0; i < cursors.Count; i++)
                {
                    var cursor = cursors[i];
                    if (session == null || cursor == null || cursor.IsClosed)
                        continue;
                    try
                    {
                        await session.CloseCursorAsync(cursor);
                    }
                    catch (SessionBusyException)
                    {
                        // 같은 DB에서 다른 실행 중 — 그 실행이 끝나면(EndRun) 다시 닫는다
                        for (var j = i; j < cursors.Count; j++)
                            _orphanCursors.Add(new KeyValuePair<DbSession, QueryCursor>(session, cursors[j]));
                        return;
                    }
                    catch (Exception)
                    {
                        // 닫히거나 끊긴 세션 — 서버 쪽 커서도 연결과 함께 정리된다
                    }
                }
            }
            catch (Exception ex)
            {
                ReportUnexpected(ex);
            }
        }

        // ================= 도우미 =================

        private IReadOnlyList<OracleConnectionProfile> Profiles()
        {
            return _host.Profiles ?? (IReadOnlyList<OracleConnectionProfile>)Array.Empty<OracleConnectionProfile>();
        }

        private OracleConnectionProfile Profile(string dbId)
        {
            return string.IsNullOrEmpty(dbId) ? null : _host.FindProfile(dbId);
        }

        private string NameOf(string dbId)
        {
            var profile = Profile(dbId);
            return profile != null ? profile.Name : "(없음)";
        }

        private int FetchCount()
        {
            return Math.Max(1, _host.FetchCount);
        }

        private bool IsRunningOther(string dbId, SqlTabState except)
        {
            return dbId != null && _tabs.Any(t => t != except && t.Running && t.RunningDbId == dbId);
        }

        private void NotifyStateChanged(string dbId)
        {
            if (dbId == null)
                return;
            try
            {
                _host.StateChanged(dbId);
            }
            catch (Exception ex)
            {
                ReportUnexpected(ex);
            }
        }

        private void RaiseActiveTabChanged()
        {
            try
            {
                var handler = ActiveTabChanged;
                if (handler != null)
                    handler(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                ReportUnexpected(ex);
            }
        }

        private void Safe(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                ReportUnexpected(ex);
            }
        }

        private void ReportUnexpected(Exception ex)
        {
            try
            {
                AddMessage(null, "처리 중 오류가 발생했습니다: " + DbSession.DescribeError(ex), MessageKind.Error);
            }
            catch (Exception)
            {
                // 알릴 길이 없는 오류 — Folderss를 멈추지 않는 것이 우선
            }
        }

        /// <summary>결과 머리의 버튼 하나(결과·메시지·기록)와 개수 표시.</summary>
        private sealed class PaneTab
        {
            private readonly Border _underline;
            private readonly TextBlock _label;
            private readonly TextBlock _count;

            public PaneTab(string title, bool counted)
            {
                var content = new StackPanel { Orientation = Orientation.Horizontal };
                _label = Theme.Text(title, Theme.SecondaryText);
                _label.VerticalAlignment = VerticalAlignment.Center;
                content.Children.Add(_label);
                if (counted)
                {
                    _count = new TextBlock { Text = "0" };
                    content.Children.Add(WorkspaceUi.CountPill(_count));
                }
                Button = WorkspaceUi.SelectorButton(content, out _underline);
                AutomationProperties.SetName(Button, title);
            }

            public Button Button { get; }

            public void SetCount(int count)
            {
                if (_count != null)
                    _count.Text = WorkspaceLogic.Count(count);
            }

            public void SetSelected(bool selected)
            {
                WorkspaceUi.SetSelected(Button, _underline, selected);
                Theme.Foreground(_label, selected ? Theme.PrimaryText : Theme.SecondaryText);
            }
        }
    }
}

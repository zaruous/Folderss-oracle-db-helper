using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace MyPlugin
{
    /// <summary>DbTreePanel이 셸(DbHelperView)에게 받는 기능. 모든 호출은 UI 스레드에서.</summary>
    internal interface ITreeHost
    {
        /// <summary>접속 목록(프로필 순서). 트리 맨 위 DB 행들. 연결 중인 접속은 연결할 때의 값.</summary>
        IReadOnlyList<OracleConnectionProfile> Profiles { get; }

        /// <summary>연결 중인 접속의 저장 값이 연결할 때와 달라졌음(다시 연결하면 적용).</summary>
        bool ProfileChanged(string dbId);

        /// <summary>접속 목록을 읽지 못한 이유(트리에 보임). 없으면 null.</summary>
        string ProfilesError { get; }

        DbSession GetSession(string dbId);

        bool IsConnecting(string dbId);

        /// <summary>그 DB를 대상으로 실행 중인 탭이 있음(트리 배지).</summary>
        bool IsRunning(string dbId);

        /// <summary>접속 사용자의 스키마(연결할 때 읽음). 모르면 null.</summary>
        string MySchemaOf(string dbId);

        /// <summary>ALL_USERS.ORACLE_MAINTAINED가 있는 DB인지(null = 아직 모름 — 있다고 보고 시도한다).</summary>
        bool? HasOracleMaintained(string dbId);

        void SetHasOracleMaintained(string dbId, bool value);

        /// <summary>그 DB의 메타데이터 조회에 쓸 토큰. 연결을 끊거나 창을 닫으면 취소된다.</summary>
        CancellationToken LoadToken(string dbId);

        Task<bool> ConnectAsync(string dbId);

        /// <summary>트리 메뉴 [다시 연결]: 연결돼 있으면 끊고(커밋 대기는 묻는다) 저장된 접속 정보로 다시 연결한다.</summary>
        void Reconnect(string dbId);

        /// <summary>트리 메뉴 [연결 끊기]: 툴바 [끊기]와 같다.</summary>
        void Disconnect(string dbId);

        /// <summary>트리 F1: 테이블·뷰의 앞 100행을 그 DB 탭에서 바로 조회(편집기 글은 그대로).</summary>
        void QuickQuery(string dbId, string owner, string objectName);

        /// <summary>트리 F4: 테이블·뷰 정보 창.</summary>
        void DescribeObject(string dbId, string owner, string objectName);

        /// <summary>트리 조회 중 세션이 끊긴 것을 알았을 때 등 툴바·배지를 새로 그려야 할 때.</summary>
        void StateChanged(string dbId);

        /// <summary>트리에서 고른 DB가 바뀌었을 수 있음(툴바의 DB 표시·연결·끊기 대상).</summary>
        void TreeSelectionChanged(string dbId);

        void InsertSelect(string dbId, string owner, string objectName);

        /// <summary>트리 메뉴·도구 메뉴 [테이블 정의서 만들기…]: 대상 선택 창(필요하면 연결한다).</summary>
        void CreateTableSpec(TableSpecRequest request);

        /// <summary>도구 메뉴 [GCM 정의서 만들기…]: 그 DB의 사용자 SQL 창(필요하면 연결한다).</summary>
        void CreateCodeSpec(string dbId);

        /// <summary>메시지 탭에 오류 한 줄.</summary>
        void ReportError(string dbId, string text);
    }

    /// <summary>
    /// 왼쪽 DB·스키마 트리(PoC 왼쪽 영역). 평면 ListBox에 TreeRowsBuilder가 만든 행을 깊이만큼 들여 그린다(가상화·재사용).
    /// - 다시 그릴 때 같은 키의 행은 그 자리에서 내용만 바꿔 선택·포커스·스크롤 위치를 지킨다. 고르기만 할 때는 다시 그리지 않는다
    ///   (두 번 누르기 사이에 다시 그리면 두 번째 누름이 다른 요소로 가서 더블클릭이 깨진다).
    /// - 펼칠 때 불러오기(Load 행)는 DbSession.QueryAsync로 하고, 결과는 DbTreeData 캐시에 넣는다.
    /// - 검색은 연결된 DB마다 서버에서 한다(입력이 멈춘 뒤 300ms, 이전 검색은 취소).
    /// </summary>
    internal sealed class DbTreePanel : Border
    {
        private const int SearchDelayMilliseconds = 300;
        // 한 번에 이만큼 넘게 바뀌면 하나씩 넣고 빼는 대신 목록을 통째로 바꾼다(큰 "더 보기" 등)
        private const int ResetThreshold = 400;

        private readonly ITreeHost _host;
        private readonly Dictionary<string, DbTreeData> _data = new Dictionary<string, DbTreeData>(StringComparer.Ordinal);
        private readonly TreeState _state = new TreeState();
        private readonly EntryCollection _entries = new EntryCollection();
        private readonly HashSet<string> _searchWaiting = new HashSet<string>(StringComparer.Ordinal);
        private readonly DispatcherTimer _searchTimer;

        private TextBox _search;
        private TextBlock _placeholder;
        private FrameworkElement _clear;
        private ComboBox _scope;
        private CheckBox _showSystem;
        private FrameworkElement _searchBar;
        private TextBlock _searchCount;
        private TextBlock _searchPosition;
        private Button _previous;
        private Button _next;
        private TreeList _list;
        private TextBlock _overlay;
        private TextBlock _footer;

        private TreeState _searchState = new TreeState();
        private Dictionary<string, TreeSearchResult> _results = new Dictionary<string, TreeSearchResult>(StringComparer.Ordinal);
        private List<string> _hits = new List<string>();
        private CancellationTokenSource _searchCts;
        private int _searchGeneration;
        private int _gotoAfterSearch;
        // null이면 일반 모드. 검색 모드에서는 지금 결과의 검색어
        private string _appliedTerm;
        private string _currentHit;

        // 고른 행. 접혀서 안 보여도 기억한다(다시 펼치면 그대로 고른 상태)
        private string _selectedKey;
        private TreeRow _selectedRow;
        // 오른쪽 누름으로 고른 메뉴 대상 행(메뉴를 열 때 쓰고 비운다)
        private TreeRow _menuRow;
        // Shift+누름으로 여러 줄을 고를 때의 기준 행
        private string _anchorKey;

        private bool _inRebuild;
        private bool _rebuildAgain;
        private bool _rebuildQueued;
        private bool _settingText;
        private bool _shutdown;

        public DbTreePanel(ITreeHost host)
        {
            if (host == null)
                throw new ArgumentNullException(nameof(host));
            _host = host;
            BorderThickness = new Thickness(0, 0, 1, 0);
            SetResourceReference(BorderBrushProperty, Theme.Border);
            Theme.Background(this, Theme.PanelBackground);

            var root = new DockPanel();
            var head = BuildHead();
            DockPanel.SetDock(head, Dock.Top);
            root.Children.Add(head);
            var foot = BuildFooter();
            DockPanel.SetDock(foot, Dock.Bottom);
            root.Children.Add(foot);
            root.Children.Add(BuildList());
            Child = root;

            _searchTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = TimeSpan.FromMilliseconds(SearchDelayMilliseconds) };
            _searchTimer.Tick += SearchTimer_Tick;
        }

        /// <summary>DB가 연결됨: 그 DB와 내 스키마를 펼치고, 검색 중이면 그 DB도 검색한다.</summary>
        public void OnConnected(string dbId, string mySchema)
        {
            if (_shutdown || string.IsNullOrEmpty(dbId))
                return;
            _state.Expanded[TreeKeys.Db(dbId)] = true;
            if (!string.IsNullOrEmpty(mySchema))
                _state.Expanded[TreeKeys.Schema(dbId, mySchema)] = true;
            if (_appliedTerm != null && _searchCts != null)
                SearchDb(dbId, _searchGeneration);
            Rebuild();
        }

        /// <summary>
        /// 연결을 끊었거나 끊긴 세션을 버림: 그 DB의 캐시·펼침 상태·검색 결과를 지운다(접힘).
        /// 고른 행이 그 DB 안에 있었으면 DB 행을 고른다(툴바의 다시 연결 대상이 그대로 남게).
        /// </summary>
        public void ResetDb(string dbId)
        {
            if (string.IsNullOrEmpty(dbId))
                return;
            DbTreeData old;
            if (_data.TryGetValue(dbId, out old))
                _data[dbId] = new DbTreeData { DbId = dbId, Name = old.Name, Detail = old.Detail };
            TreePanelLogic.ForgetDb(_state, dbId);
            TreePanelLogic.ForgetDb(_searchState, dbId);
            _results.Remove(dbId);
            _searchWaiting.Remove(dbId);
            if (_selectedKey != null && TreeKeys.DbIdOf(_selectedKey) == dbId)
            {
                _selectedKey = TreeKeys.Db(dbId);
                _selectedRow = null;
            }
            Rebuild();
        }

        /// <summary>접속 목록이 바뀜: 없어진 DB의 캐시·상태를 지운다. 고른 DB가 없어졌으면 첫 DB를 고른다.</summary>
        public void OnProfilesChanged()
        {
            var ids = new HashSet<string>(Profiles().Select(p => p.Id), StringComparer.Ordinal);
            foreach (var id in _data.Keys.Where(k => !ids.Contains(k)).ToList())
            {
                _data.Remove(id);
                TreePanelLogic.ForgetDb(_state, id);
                TreePanelLogic.ForgetDb(_searchState, id);
                _results.Remove(id);
                _searchWaiting.Remove(id);
            }
            var selectedDb = TreeKeys.DbIdOf(_selectedKey);
            if (selectedDb == null || !ids.Contains(selectedDb))
                SelectDb(Profiles().Select(p => p.Id).FirstOrDefault());
            Rebuild();
            _host.TreeSelectionChanged(TreeKeys.DbIdOf(_selectedKey));
        }

        /// <summary>DB 행을 고른다(처음 열 때·고른 DB가 지워졌을 때). null이면 고른 것 없음.</summary>
        public void SelectDb(string dbId)
        {
            _selectedKey = string.IsNullOrEmpty(dbId) ? null : TreeKeys.Db(dbId);
            _selectedRow = null;
            RestoreSelection();
        }

        /// <summary>창을 닫음: 검색·타이머를 멈추고 더는 그리지 않는다(불러오기는 셸이 토큰으로 취소한다).</summary>
        public void Shutdown()
        {
            _shutdown = true;
            _searchTimer.Stop();
            CancelSearchQueries();
        }

        /// <summary>다음 틈에 한 번 다시 그린다(불러오기 완료·상태 변경이 몰려도 한 번).</summary>
        public void RequestRebuild()
        {
            if (_rebuildQueued || _shutdown)
                return;
            _rebuildQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                _rebuildQueued = false;
                try
                {
                    Rebuild();
                }
                catch (Exception ex)
                {
                    Report(ex, null);
                }
            }));
        }

        /// <summary>캐시·펼침 상태로 행을 다시 만들어 목록에 맞춘다. 그리는 중에 다시 불리면 끝난 뒤 한 번 더 그린다.</summary>
        public void Rebuild()
        {
            if (_shutdown)
                return;
            if (_inRebuild)
            {
                _rebuildAgain = true;
                return;
            }
            _inRebuild = true;
            try
            {
                for (var pass = 0; pass < 3; pass++)
                {
                    _rebuildAgain = false;
                    RebuildCore();
                    if (!_rebuildAgain || _shutdown)
                        break;
                }
            }
            finally
            {
                _inRebuild = false;
            }
        }

        // ================= 화면 =================

        private FrameworkElement BuildHead()
        {
            var panel = new StackPanel { Margin = new Thickness(6) };

            var box = new Grid();
            _search = new TextBox { Padding = new Thickness(17, 1, 18, 1), VerticalContentAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(_search, "트리 검색 (Enter: 다음 일치, Esc: 지우기)");
            _search.TextChanged += Search_TextChanged;
            _search.PreviewKeyDown += Search_PreviewKeyDown;
            box.Children.Add(_search);
            var magnifier = Theme.Text("⌕", Theme.SecondaryText);
            magnifier.FontSize = 12;
            magnifier.IsHitTestVisible = false;
            magnifier.VerticalAlignment = VerticalAlignment.Center;
            magnifier.Margin = new Thickness(6, 0, 0, 1);
            box.Children.Add(magnifier);
            _placeholder = Theme.Text("트리 검색", Theme.SecondaryText);
            _placeholder.IsHitTestVisible = false;
            _placeholder.VerticalAlignment = VerticalAlignment.Center;
            _placeholder.Margin = new Thickness(21, 0, 0, 0);
            box.Children.Add(_placeholder);
            var clearText = Theme.Text("✕", Theme.SecondaryText);
            clearText.FontSize = 11;
            clearText.VerticalAlignment = VerticalAlignment.Center;
            var clear = new Border
            {
                Child = clearText,
                Background = Brushes.Transparent,
                Padding = new Thickness(4, 0, 4, 0),
                Margin = new Thickness(0, 0, 2, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                Cursor = Cursors.Hand,
                ToolTip = "검색 지우기 (Esc)",
                Visibility = Visibility.Collapsed
            };
            clear.MouseLeftButtonDown += Clear_MouseLeftButtonDown;
            _clear = clear;
            box.Children.Add(clear);
            panel.Children.Add(box);

            var options = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            _showSystem = ShellUi.ThemedCheckBox("내장");
            _showSystem.FontSize = 11.5;
            _showSystem.Margin = new Thickness(6, 0, 0, 0);
            _showSystem.ToolTip = "SYS, SYSTEM 등 Oracle 내장 스키마(ALL_USERS.ORACLE_MAINTAINED = 'Y')";
            _showSystem.Checked += ShowSystem_Changed;
            _showSystem.Unchecked += ShowSystem_Changed;
            DockPanel.SetDock(_showSystem, Dock.Right);
            options.Children.Add(_showSystem);
            _scope = new ComboBox { FontSize = 11.5, ToolTip = "열 검색은 ALL_TAB_COLUMNS를 조회해 느릴 수 있습니다" };
            AutomationProperties.SetName(_scope, "검색 대상");
            _scope.Items.Add(new ComboBoxItem { Content = "스키마·객체" });
            _scope.Items.Add(new ComboBoxItem { Content = "스키마·객체·열" });
            _scope.SelectedIndex = 0;
            _scope.SelectionChanged += Scope_SelectionChanged;
            options.Children.Add(_scope);
            panel.Children.Add(options);

            var bar = new DockPanel { Margin = new Thickness(0, 5, 0, 0), Visibility = Visibility.Collapsed };
            _next = ShellUi.SmallButton("▼", "다음 일치 (Enter)");
            _next.Click += Next_Click;
            DockPanel.SetDock(_next, Dock.Right);
            bar.Children.Add(_next);
            _previous = ShellUi.SmallButton("▲", "이전 일치 (Shift+Enter)");
            _previous.Click += Previous_Click;
            _previous.Margin = new Thickness(4, 0, 2, 0);
            DockPanel.SetDock(_previous, Dock.Right);
            bar.Children.Add(_previous);
            _searchPosition = Theme.Secondary("");
            _searchPosition.FontSize = 11;
            _searchPosition.VerticalAlignment = VerticalAlignment.Center;
            _searchPosition.Margin = new Thickness(6, 0, 0, 0);
            DockPanel.SetDock(_searchPosition, Dock.Right);
            bar.Children.Add(_searchPosition);
            _searchCount = Theme.Secondary("");
            _searchCount.FontSize = 11;
            _searchCount.VerticalAlignment = VerticalAlignment.Center;
            _searchCount.TextTrimming = TextTrimming.CharacterEllipsis;
            bar.Children.Add(_searchCount);
            _searchBar = bar;
            panel.Children.Add(bar);

            var head = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = panel };
            head.SetResourceReference(BorderBrushProperty, Theme.Border);
            return head;
        }

        private FrameworkElement BuildFooter()
        {
            _footer = Theme.Secondary("—");
            _footer.FontSize = 11;
            _footer.TextTrimming = TextTrimming.CharacterEllipsis;
            var foot = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(8, 4, 8, 4), Child = _footer };
            foot.SetResourceReference(BorderBrushProperty, Theme.Border);
            return foot;
        }

        private FrameworkElement BuildList()
        {
            _list = new TreeList
            {
                ItemsSource = _entries,
                ItemTemplate = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(TreeRowView)) },
                // 여러 줄 고르기(Ctrl·Shift+누름, Shift+화살표): 테이블 정의서 대상
                SelectionMode = SelectionMode.Extended,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0, 3, 0, 3)
            };
            // 암시 스타일은 형식이 정확히 같아야 붙는다 — 하위 클래스에도 Folderss의 ListBox 스타일을 잇는다
            _list.SetResourceReference(StyleProperty, typeof(ListBox));
            // 스타일이 패널을 바꿔도 가상화가 꺼지지 않게 패널을 직접 준다
            _list.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
            Theme.Background(_list, Theme.PanelBackground);
            AutomationProperties.SetName(_list, "DB·스키마 트리");
            VirtualizingPanel.SetIsVirtualizing(_list, true);
            VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
            ScrollViewer.SetCanContentScroll(_list, true);
            _list.PreviewMouseLeftButtonDown += List_PreviewMouseLeftButtonDown;
            _list.PreviewMouseRightButtonDown += List_PreviewMouseRightButtonDown;
            _list.PreviewKeyDown += List_PreviewKeyDown;
            _list.SelectionChanged += List_SelectionChanged;
            // 오른쪽 메뉴: 항목은 열 때마다 그 행의 DB 상태로 만든다(ContextMenu가 있어야 ContextMenuOpening이 온다)
            _list.ContextMenu = new ContextMenu();
            _list.ContextMenuOpening += List_ContextMenuOpening;

            _overlay = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(14, 30, 14, 0),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            var area = new Grid();
            area.Children.Add(_list);
            area.Children.Add(_overlay);
            return area;
        }

        // ================= 다시 그리기 =================

        private void RebuildCore()
        {
            var dbs = CurrentDbs();
            var showSystem = _showSystem.IsChecked == true;
            var searching = _appliedTerm != null;
            var connected = dbs.Count(d => d.Connected);
            List<TreeRow> rows;
            if (searching)
            {
                rows = TreeRowsBuilder.BuildSearch(dbs, _results, _appliedTerm, _searchState, showSystem);
                var offline = TreePanelLogic.OfflineNote(dbs.Count - connected);
                if (offline != null)
                    rows.Add(new TreeRow { Key = TreePanelLogic.OfflineNoteKey, Kind = TreeRowKind.Note, Depth = 0, Text = offline });
                _hits = TreePanelLogic.HitKeys(rows);
                if (_currentHit != null && !_hits.Contains(_currentHit))
                    _currentHit = null;
            }
            else
            {
                rows = TreeRowsBuilder.Build(dbs, _state, showSystem);
                _hits = new List<string>();
                _currentHit = null;
            }

            var profiles = new Dictionary<string, OracleConnectionProfile>(StringComparer.Ordinal);
            foreach (var profile in Profiles())
            {
                if (!profiles.ContainsKey(profile.Id))
                    profiles.Add(profile.Id, profile);
            }
            var next = new List<TreeEntry>(rows.Count);
            foreach (var row in rows)
                next.Add(MakeEntry(row, profiles, searching));

            var hadFocus = _list.IsKeyboardFocusWithin;
            if (Apply(next) && hadFocus)
                FocusSelectedLater();
            RestoreSelection();
            UpdateChrome(dbs, rows, connected, searching, showSystem);
            foreach (var load in TreeLoaderLogic.PendingLoads(rows, DataOf))
                StartLoad(load, false);
        }

        /// <summary>프로필 순서의 DB 캐시. 연결 상태는 셸에서 새로 읽는다.</summary>
        private List<DbTreeData> CurrentDbs()
        {
            var dbs = new List<DbTreeData>();
            foreach (var profile in Profiles())
            {
                DbTreeData db;
                if (!_data.TryGetValue(profile.Id, out db))
                {
                    db = new DbTreeData { DbId = profile.Id };
                    _data.Add(profile.Id, db);
                }
                else if (dbs.Contains(db))
                {
                    continue;
                }
                db.Name = profile.Name;
                db.Detail = ShellLogic.Address(profile);
                db.Connected = _host.GetSession(profile.Id) != null;
                db.Connecting = _host.IsConnecting(profile.Id);
                db.MySchema = _host.MySchemaOf(profile.Id);
                dbs.Add(db);
            }
            return dbs;
        }

        private TreeEntry MakeEntry(TreeRow row, Dictionary<string, OracleConnectionProfile> profiles, bool searching)
        {
            var badges = new RowBadges();
            Brush stripe = null;
            if (row.Kind == TreeRowKind.Database && row.DbId != null)
            {
                OracleConnectionProfile profile;
                profiles.TryGetValue(row.DbId, out profile);
                var session = _host.GetSession(row.DbId);
                badges.Connected = session != null;
                badges.Broken = session != null && session.IsBroken;
                badges.Connecting = _host.IsConnecting(row.DbId);
                badges.Running = _host.IsRunning(row.DbId);
                badges.Pending = session == null ? null : session.PendingText;
                badges.ReadOnly = profile != null && profile.ReadOnly;
                badges.Color = profile == null ? null : profile.Color;
                badges.ProfileChanged = session != null && _host.ProfileChanged(row.DbId);
                stripe = Theme.ConnectionColor(badges.Color);
            }
            badges.Current = searching && _currentHit != null && row.Key == _currentHit;
            if (row.IsLoadMore && row.Load != null)
            {
                var db = DataOf(row.DbId);
                badges.Loading = db != null && db.Loading.Contains(row.Load.Key);
            }
            return new TreeEntry(row, badges, stripe);
        }

        /// <summary>새 행 목록을 목록에 맞춘다. 같은 키의 앞뒤 행은 그 자리에서 내용만 바꾼다. 목록을 통째로 바꿨으면 true.</summary>
        private bool Apply(List<TreeEntry> next)
        {
            int prefix, suffix;
            TreePanelLogic.Align(_entries.Select(e => e.Row.Key).ToList(), next.Select(e => e.Row.Key).ToList(), out prefix, out suffix);
            for (var i = 0; i < prefix; i++)
                UpdateInPlace(_entries[i], next[i]);
            for (var i = 0; i < suffix; i++)
                UpdateInPlace(_entries[_entries.Count - 1 - i], next[next.Count - 1 - i]);
            var removeCount = _entries.Count - prefix - suffix;
            var insertCount = next.Count - prefix - suffix;
            if (removeCount == 0 && insertCount == 0)
                return false;
            if (removeCount + insertCount > ResetThreshold)
            {
                var merged = new List<TreeEntry>(next.Count);
                merged.AddRange(_entries.Take(prefix));
                merged.AddRange(next.Skip(prefix).Take(insertCount));
                merged.AddRange(_entries.Skip(_entries.Count - suffix));
                _entries.ReplaceAll(merged);
                return true;
            }
            for (var i = removeCount - 1; i >= 0; i--)
                _entries.RemoveAt(prefix + i);
            for (var i = 0; i < insertCount; i++)
                _entries.Insert(prefix + i, next[prefix + i]);
            return false;
        }

        private static void UpdateInPlace(TreeEntry current, TreeEntry fresh)
        {
            if (!current.Update(fresh))
                return;
            var view = current.View;
            if (view != null && ReferenceEquals(view.DataContext, current))
                view.Render();
        }

        /// <summary>고른 키의 행이 보이면 그 행을 고른다(행을 새로 만들었거나 다시 펼쳤을 때).</summary>
        private void RestoreSelection()
        {
            if (_selectedKey == null)
                return;
            var entry = Find(_selectedKey);
            // 여러 줄을 골라 둔 상태면 그대로 둔다(고른 키의 행이 그 안에 있으면)
            if (entry != null && !_list.SelectedItems.Contains(entry))
                _list.SelectedItem = entry;
        }

        private void UpdateChrome(List<DbTreeData> dbs, List<TreeRow> rows, int connected, bool searching, bool showSystem)
        {
            string overlay = null;
            var overlayIsError = false;
            if (dbs.Count == 0)
            {
                overlayIsError = !string.IsNullOrEmpty(_host.ProfilesError);
                overlay = overlayIsError ? _host.ProfilesError : "등록된 DB가 없습니다.\n[접속 관리…]에서 추가하세요.";
            }
            else if (searching && rows.All(r => r.Key == TreePanelLogic.OfflineNoteKey))
            {
                overlay = _searchWaiting.Count > 0 ? "검색 중…" : TreePanelLogic.NoMatchText(_appliedTerm, ColumnsScope, showSystem);
            }
            _overlay.Text = overlay ?? "";
            _overlay.Visibility = overlay == null ? Visibility.Collapsed : Visibility.Visible;
            if (overlayIsError)
                _overlay.Foreground = Theme.Danger;
            else
                Theme.Foreground(_overlay, Theme.SecondaryText);

            _searchBar.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
            if (searching)
            {
                _searchCount.Text = TreePanelLogic.SearchSummary(rows, ColumnsScope);
                _searchPosition.Text = TreePanelLogic.Position(_hits.IndexOf(_currentHit), _hits.Count);
                _previous.IsEnabled = _hits.Count > 0;
                _next.IsEnabled = _hits.Count > 0;
            }

            if (dbs.Count == 0)
                _footer.Text = "—";
            else if (searching)
                _footer.Text = TreePanelLogic.SearchFooter(connected, _searchWaiting.Count);
            else if (_searchTimer.IsEnabled)
                _footer.Text = "검색 중…";
            else
                _footer.Text = TreePanelLogic.NormalFooter(dbs.Count, connected, TreePanelLogic.HidesSystemSchemas(dbs, showSystem));
        }

        // ================= 펼치기·고르기 =================

        private TreeState ActiveState
        {
            get { return _appliedTerm != null ? _searchState : _state; }
        }

        private void SetExpanded(TreeRow row, bool expand)
        {
            if (row == null || !row.Expandable)
                return;
            // 연결 안 된(또는 끊긴) DB를 펼치면 연결한다(연결되면 셸이 OnConnected로 펼친다)
            if (expand && row.Kind == TreeRowKind.Database && NeedsConnect(row.DbId))
            {
                ConnectAndExpand(row.DbId);
                return;
            }
            ActiveState.Expanded[row.Key] = expand;
            if (expand)
            {
                // 다시 펼치면 이전 불러오기 오류를 지우고 다시 불러온다
                var db = DataOf(row.DbId);
                if (db != null)
                    db.Errors.Remove(row.Key);
            }
            Rebuild();
        }

        private void Toggle(TreeRow row)
        {
            if (row != null)
                SetExpanded(row, !row.Expanded);
        }

        // 세션이 없거나 끊겼으면 연결해야 한다(ConnectAsync가 끊긴 세션을 버리고 다시 연결한다)
        private bool NeedsConnect(string dbId)
        {
            var session = _host.GetSession(dbId);
            return session == null || session.IsBroken;
        }

        private async void ConnectAndExpand(string dbId)
        {
            try
            {
                // 마우스 누름 처리기 안에서 비밀번호 창(모달)을 띄우지 않게 처리기가 끝난 뒤 연결한다
                await Task.Yield();
                if (_shutdown)
                    return;
                var connected = await _host.ConnectAsync(dbId);
                if (!connected || _shutdown)
                    return;
                if (_appliedTerm == null)
                    _state.Expanded[TreeKeys.Db(dbId)] = true;
                Rebuild();
            }
            catch (Exception ex)
            {
                Report(ex, dbId);
            }
        }

        /// <summary>두 번 누름: 연결 안 된(또는 끊긴) DB는 연결하고 펼친다. 테이블·뷰는 아무것도 하지 않는다(SELECT 넣기는 오른쪽 메뉴·Enter).</summary>
        private void DoubleClick(TreeRow row)
        {
            if (row.Kind == TreeRowKind.Database && NeedsConnect(row.DbId))
                ConnectAndExpand(row.DbId);
        }

        /// <summary>Enter: 테이블·뷰는 SELECT 넣기, "더 보기"는 불러오기, 연결 안 된 DB는 연결, 그 밖에 펼칠 수 있으면 펼치기·접기.</summary>
        private void Activate(TreeRow row)
        {
            if (row.Kind == TreeRowKind.Object && TreeGroups.HasColumns(row.ObjectType))
                _host.InsertSelect(row.DbId, row.Owner, row.ObjectName);
            else if (row.Kind == TreeRowKind.Note && row.IsLoadMore && row.Load != null)
                StartLoad(row.Load, true);
            else if (row.Kind == TreeRowKind.Database && NeedsConnect(row.DbId))
                ConnectAndExpand(row.DbId);
            else if (row.Expandable)
                Toggle(row);
        }

        private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                // 지금 행 = 새로 고른 행 중 마지막. Ctrl+누름으로 지금 행을 풀었으면 남은 것 중 마지막.
                // 고른 행이 접혀 사라졌을 뿐이면 고른 키는 그대로 둔다. 안내 행은 고르는 대상이 아니다.
                var entry = e.AddedItems.OfType<TreeEntry>().LastOrDefault(IsSelectable);
                if (entry == null && _list.SelectedItems.Count > 0)
                {
                    var current = Find(_selectedKey);
                    if (current != null && !_list.SelectedItems.Contains(current))
                        entry = _list.SelectedItems.OfType<TreeEntry>().LastOrDefault(IsSelectable);
                }
                if (entry == null)
                    return;
                _selectedKey = entry.Row.Key;
                _selectedRow = entry.Row;
                _host.TreeSelectionChanged(entry.Row.DbId);
            }
            catch (Exception ex)
            {
                Report(ex, null);
            }
        }

        private void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                var source = e.OriginalSource as DependencyObject;
                var container = ShellUi.FindAncestor<ListBoxItem>(source, _list);
                if (container == null)
                    return; // 스크롤 막대 등 — 목록이 처리한다
                var entry = _list.ItemContainerGenerator.ItemFromContainer(container) as TreeEntry ?? container.DataContext as TreeEntry;
                if (entry == null || entry.Row == null)
                    return;
                // 고르기·펼치기를 직접 한다(목록의 끌어서 고르기·포커스 이동과 섞이지 않게)
                e.Handled = true;
                var row = entry.Row;
                if (row.Kind == TreeRowKind.Note)
                {
                    if (row.IsLoadMore && row.Load != null && e.ClickCount == 1)
                        StartLoad(row.Load, true);
                    return;
                }
                // Ctrl+누름: 그 행을 고르거나 풂, Shift+누름: 기준 행부터 그 행까지 — 펼치기·두 번 누르기는 하지 않는다
                var modifiers = Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift);
                if (modifiers != ModifierKeys.None && e.ClickCount == 1)
                {
                    if (modifiers == ModifierKeys.Control)
                    {
                        if (_list.SelectedItems.Contains(entry))
                            _list.SelectedItems.Remove(entry);
                        else
                            _list.SelectedItems.Add(entry);
                        _anchorKey = row.Key;
                    }
                    else
                    {
                        SelectRange(entry);
                    }
                    container.Focus();
                    return;
                }
                _list.SelectedItem = entry;
                _anchorKey = row.Key;
                container.Focus();
                var view = ShellUi.FindAncestor<TreeRowView>(source, container);
                if (row.Expandable && view != null && view.IsOnTwisty(source))
                {
                    Toggle(row);
                    return;
                }
                if (e.ClickCount >= 2)
                {
                    DoubleClick(row);
                    return;
                }
                if ((row.Kind == TreeRowKind.Schema || row.Kind == TreeRowKind.Group) && row.Expandable)
                    Toggle(row);
            }
            catch (Exception ex)
            {
                Report(ex, null);
            }
        }

        /// <summary>오른쪽 누름: 누른 행을 고르고 메뉴 대상으로 기억한다(빈 곳·안내 행이면 메뉴를 띄우지 않음).</summary>
        private void List_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                _menuRow = null;
                var container = ShellUi.FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject, _list);
                if (container == null)
                    return;
                var entry = _list.ItemContainerGenerator.ItemFromContainer(container) as TreeEntry ?? container.DataContext as TreeEntry;
                if (entry == null || entry.Row == null || entry.Row.Kind == TreeRowKind.Note)
                    return;
                // 여러 줄을 골라 둔 곳 안을 누르면 고른 것을 그대로 둔다(메뉴가 그 행들에 적용)
                if (!_list.SelectedItems.Contains(entry))
                {
                    _list.SelectedItem = entry;
                    _anchorKey = entry.Row.Key;
                }
                container.Focus();
                _menuRow = entry.Row;
            }
            catch (Exception ex)
            {
                Report(ex, null);
            }
        }

        /// <summary>메뉴 열기: 마우스로 열면 누른 행, 키보드(Shift+F10·메뉴 키)로 열면 고른 행의 DB 메뉴를 만든다.</summary>
        private void List_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            try
            {
                var byKeyboard = e.CursorLeft < 0 && e.CursorTop < 0;
                var selected = CurrentEntry();
                var row = byKeyboard ? (selected != null ? selected.Row : null) : _menuRow;
                _menuRow = null;
                if (row == null || row.Kind == TreeRowKind.Note || string.IsNullOrEmpty(row.DbId) || _shutdown)
                {
                    e.Handled = true;
                    return;
                }
                FillMenu(_list.ContextMenu, row);
                if (_list.ContextMenu.Items.Count == 0)
                    e.Handled = true;
            }
            catch (Exception ex)
            {
                e.Handled = true;
                Report(ex, null);
            }
        }

        /// <summary>그 행의 DB 메뉴: DB 이름(머리), 연결·다시 연결·연결 끊기, 테이블·뷰면 SELECT 넣기.</summary>
        private void FillMenu(ContextMenu menu, TreeRow row)
        {
            menu.Items.Clear();
            var dbId = row.DbId;
            var profile = _host.Profiles.FirstOrDefault(p => p.Id == dbId);
            if (profile == null)
                return;
            menu.Items.Add(new MenuItem { Header = profile.Name, IsEnabled = false });
            menu.Items.Add(new Separator());
            var state = TreePanelLogic.ConnectionMenu(_host.GetSession(dbId) != null, _host.IsConnecting(dbId));
            if (state.Connecting)
            {
                menu.Items.Add(new MenuItem { Header = "연결하는 중…", IsEnabled = false });
            }
            else
            {
                menu.Items.Add(MenuAction("연결", state.CanConnect, () => ConnectAndExpand(dbId)));
                menu.Items.Add(MenuAction("다시 연결", state.CanReconnect, () => _host.Reconnect(dbId),
                    "연결을 끊고 저장된 접속 정보로 다시 연결합니다(커밋 대기 변경은 묻습니다)"));
                menu.Items.Add(MenuAction("연결 끊기", state.CanDisconnect, () => _host.Disconnect(dbId)));
            }
            if (row.Kind == TreeRowKind.Database && !state.Connecting)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(MenuAction(CodeSpecLogic.Title + "…", true, () => _host.CreateCodeSpec(dbId),
                    "이 DB('" + profile.Name + "')에서 공통 코드 조회 SQL을 실행해 정의서를 만듭니다"));
            }
            if (row.Kind == TreeRowKind.Object && TreeGroups.HasColumns(row.ObjectType))
            {
                var owner = row.Owner;
                var name = row.ObjectName;
                menu.Items.Add(new Separator());
                var quick = MenuAction("빠른 조회 (앞 " + WorkspaceLogic.QuickQueryRows + "행)", true, () => _host.QuickQuery(dbId, owner, name));
                quick.InputGestureText = "F1";
                menu.Items.Add(quick);
                var describe = MenuAction("테이블 정보", true, () => _host.DescribeObject(dbId, owner, name));
                describe.InputGestureText = "F4";
                menu.Items.Add(describe);
                menu.Items.Add(MenuAction("SELECT 문 넣기", true, () => _host.InsertSelect(dbId, owner, name)));
            }
            // 테이블 정의서: 누른 행이 고른 행들 안에 있으면 고른 행 전부(같은 DB), 아니면 누른 행
            var targets = TableSpecRows(dbId, row);
            var specText = TableSpecLogic.MenuText(row, targets);
            if (specText != null && !state.Connecting)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(MenuAction(specText, true, () => _host.CreateTableSpec(TableSpecRequest(dbId, targets))));
            }
        }

        /// <summary>정의서 대상 행: row가 고른 행들 안에 있으면 고른 행 중 같은 DB 것들, 아니면 row 하나.</summary>
        private List<TreeRow> TableSpecRows(string dbId, TreeRow row)
        {
            var selected = _list.SelectedItems.OfType<TreeEntry>().Select(e => e.Row).Where(r => r != null && r.DbId == dbId).ToList();
            return selected.Any(r => r.Key == row.Key) ? selected : new List<TreeRow> { row };
        }

        private TableSpecRequest TableSpecRequest(string dbId, IEnumerable<TreeRow> rows)
        {
            var db = DataOf(dbId);
            return TableSpecLogic.Request(dbId, rows, _host.MySchemaOf(dbId), db == null ? null : db.Schemas, _showSystem.IsChecked == true);
        }

        /// <summary>지금 고른 행의 DB id(접속 목록에 있는 것). 고른 DB가 없으면 null.</summary>
        public string SelectedDbId()
        {
            var current = CurrentEntry();
            var dbId = current != null && current.Row != null ? current.Row.DbId : TreeKeys.DbIdOf(_selectedKey);
            return string.IsNullOrEmpty(dbId) || !Profiles().Any(p => p.Id == dbId) ? null : dbId;
        }

        /// <summary>도구 메뉴 [테이블 정의서 만들기…]: 지금 고른 행들(지금 행의 DB)로 대상 선택 창을 연다. 고른 DB가 없으면 false.</summary>
        public bool CreateTableSpecFromSelection()
        {
            var dbId = SelectedDbId();
            if (dbId == null)
                return false;
            var rows = _list.SelectedItems.OfType<TreeEntry>().Select(e => e.Row).Where(r => r != null && r.DbId == dbId).ToList();
            _host.CreateTableSpec(TableSpecRequest(dbId, rows));
            return true;
        }

        // 메뉴가 닫힌 뒤 실행한다(확인·비밀번호 창이 메뉴 위에 뜨지 않게)
        private MenuItem MenuAction(string header, bool enabled, Action action, string tooltip = null)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            if (tooltip != null)
                item.ToolTip = tooltip;
            item.Click += (s, e) => Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                try
                {
                    if (!_shutdown)
                        action();
                }
                catch (Exception ex)
                {
                    Report(ex, null);
                }
            }));
            return item;
        }

        private void List_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            try
            {
                if (e.Key == Key.Escape)
                {
                    if (_appliedTerm != null || _search.Text.Length > 0)
                    {
                        e.Handled = true;
                        ClearSearch();
                    }
                    return;
                }
                var entry = CurrentEntry();
                // F1 빠른 조회(앞 100행) · F4 테이블 정보: 테이블·뷰 행에서
                if ((e.Key == Key.F1 || e.Key == Key.F4) && Keyboard.Modifiers == ModifierKeys.None)
                {
                    var target = entry == null ? null : entry.Row;
                    if (target != null && target.Kind == TreeRowKind.Object && TreeGroups.HasColumns(target.ObjectType))
                    {
                        e.Handled = true;
                        if (e.Key == Key.F1)
                            _host.QuickQuery(target.DbId, target.Owner, target.ObjectName);
                        else
                            _host.DescribeObject(target.DbId, target.Owner, target.ObjectName);
                    }
                    return;
                }
                if (entry == null || entry.Row == null || (e.Key != Key.Enter && e.Key != Key.Right && e.Key != Key.Left))
                    return;
                if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0)
                    return;
                e.Handled = true;
                var row = entry.Row;
                if (e.Key == Key.Enter)
                {
                    Activate(row);
                }
                else if (e.Key == Key.Right)
                {
                    var disconnectedDb = row.Kind == TreeRowKind.Database && NeedsConnect(row.DbId);
                    if (row.Expandable && (!row.Expanded || disconnectedDb))
                        SetExpanded(row, true);
                    else if (row.Expanded)
                        SelectFirstChild(entry);
                }
                else if (row.Expandable && row.Expanded)
                {
                    SetExpanded(row, false);
                }
                else
                {
                    var parent = Find(TreePanelLogic.ParentKey(row));
                    if (parent != null)
                        SelectAndFocus(parent);
                }
            }
            catch (Exception ex)
            {
                Report(ex, null);
            }
        }

        /// <summary>지금 행: 고른 키의 행이 골라져 있으면 그 행, 아니면 목록이 고른 첫 행.</summary>
        private TreeEntry CurrentEntry()
        {
            var entry = Find(_selectedKey);
            return entry != null && _list.SelectedItems.Contains(entry) ? entry : _list.SelectedItem as TreeEntry;
        }

        private static bool IsSelectable(TreeEntry entry)
        {
            return entry != null && entry.Row != null && entry.Row.Kind != TreeRowKind.Note;
        }

        /// <summary>Shift+누름: 기준 행(없으면 지금 행)부터 entry까지 안내 행을 뺀 행들을 고른다.</summary>
        private void SelectRange(TreeEntry entry)
        {
            var anchor = Find(_anchorKey) ?? CurrentEntry() ?? entry;
            var from = _entries.IndexOf(anchor);
            var to = _entries.IndexOf(entry);
            if (from < 0)
                from = to;
            var range = new List<TreeEntry>();
            for (var i = Math.Min(from, to); i <= Math.Max(from, to); i++)
            {
                if (IsSelectable(_entries[i]))
                    range.Add(_entries[i]);
            }
            _list.SelectRange(range, entry);
        }

        private void SelectFirstChild(TreeEntry entry)
        {
            var index = _entries.IndexOf(entry);
            if (index < 0 || index + 1 >= _entries.Count)
                return;
            var child = _entries[index + 1];
            if (child.Row.Depth > entry.Row.Depth && child.Row.Kind != TreeRowKind.Note)
                SelectAndFocus(child);
        }

        private void SelectAndFocus(TreeEntry entry)
        {
            _list.SelectedItem = entry;
            _list.ScrollIntoView(entry);
            FocusEntryLater(entry);
        }

        private void FocusSelectedLater()
        {
            var entry = _list.SelectedItem as TreeEntry;
            if (entry != null)
                FocusEntryLater(entry);
        }

        // 목록을 통째로 바꾸거나 스크롤한 직후에는 항목 컨테이너가 아직 없다 — 그려진 뒤 포커스를 옮긴다
        private void FocusEntryLater(TreeEntry entry)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                try
                {
                    if (_shutdown)
                        return;
                    var container = _list.ItemContainerGenerator.ContainerFromItem(entry) as ListBoxItem;
                    if (container != null)
                        container.Focus();
                }
                catch (Exception ex)
                {
                    Report(ex, null);
                }
            }));
        }

        private void FocusTree()
        {
            var entry = _list.SelectedItem as TreeEntry ?? _entries.FirstOrDefault(e => e.Row.Kind != TreeRowKind.Note);
            if (entry != null)
                SelectAndFocus(entry);
        }

        // ================= 불러오기 =================

        /// <summary>
        /// Load 행 하나를 실행한다(이미 불러오는 중이면 건너뜀). 결과·오류는 캐시에 넣고 다시 그린다.
        /// 그 사이 연결을 끊었거나 다시 연결했으면(캐시·세션이 바뀜) 결과를 버린다.
        /// </summary>
        private async void StartLoad(TreeLoadRequest load, bool loadMore)
        {
            DbTreeData db = null;
            DbSession session = null;
            var started = false;
            try
            {
                if (_shutdown || load == null || string.IsNullOrEmpty(load.Key))
                    return;
                db = DataOf(load.DbId);
                session = _host.GetSession(load.DbId);
                if (db == null || session == null || !db.Loading.Add(load.Key))
                    return;
                started = true;
                if (loadMore)
                    RequestRebuild(); // "더 보기"에 불러오는 중 표시
                var token = _host.LoadToken(load.DbId);
                var meta = session.Provider.Metadata;
                switch (load.Kind)
                {
                    case TreeLoadKind.Schemas:
                    {
                        var schemas = await QueryWithOmAsync<SchemaInfo>(session, load.DbId, om => meta.Schemas(om),
                            om => r => meta.ReadSchema(r, om), meta.HasMaintainedFlagFallback, token);
                        if (IsCurrent(db, session))
                            db.Schemas = schemas;
                        break;
                    }
                    case TreeLoadKind.GroupCounts:
                    {
                        var counts = await session.QueryAsync(meta.GroupCounts(load.Owner), r => TreeLoaderLogic.ReadGroupCount(r), token);
                        if (IsCurrent(db, session))
                            db.GroupCounts[load.Key] = TreeLoaderLogic.GroupCounts(counts);
                        break;
                    }
                    case TreeLoadKind.Objects:
                    {
                        var objects = await session.QueryAsync(meta.Objects(load.Owner, load.Group, load.Limit), r => meta.ReadObject(r), token);
                        if (IsCurrent(db, session))
                            db.Objects[load.Key] = TreeLoaderLogic.Page(objects, load.Limit);
                        break;
                    }
                    case TreeLoadKind.Columns:
                    {
                        var columns = await session.QueryAsync(meta.Columns(load.Owner, load.ObjectName), r => meta.ReadColumn(r), token);
                        if (IsCurrent(db, session))
                            db.Columns[load.Key] = columns;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                if (started && IsCurrent(db, session))
                    db.Errors[load.Key] = DbSession.DescribeError(ex);
                if (started && session != null && session.IsBroken && !_shutdown)
                    NotifyHostState(load.DbId);
            }
            finally
            {
                if (started)
                {
                    db.Loading.Remove(load.Key);
                    RequestRebuild();
                }
            }
        }

        /// <summary>
        /// ORACLE_MAINTAINED를 쓰는 조회: 모르면 있다고 보고 실행하고, ORA-00904(11g)면 그 DB는 없다고 기억한 뒤 대체 쿼리로 다시 한다.
        /// usesOm이 false면(그 조회가 열을 쓰지 않음) 알고 있는 값으로 한 번만 실행한다.
        /// </summary>
        private async Task<List<T>> QueryWithOmAsync<T>(DbSession session, string dbId, Func<bool, SqlQuery> query, Func<bool, Func<IDataRecord, T>> read,
            bool usesOm, CancellationToken token)
        {
            var known = _host.HasOracleMaintained(dbId);
            var om = known ?? true;
            if (!usesOm || !om)
                return await session.QueryAsync(query(om), read(om), token);
            try
            {
                var rows = await session.QueryAsync(query(true), read(true), token);
                if (known == null && ReferenceEquals(_host.GetSession(dbId), session))
                    _host.SetHasOracleMaintained(dbId, true);
                return rows;
            }
            catch (Exception ex) when (!token.IsCancellationRequested && OracleMetadata.IsInvalidIdentifier(ex))
            {
                // 11g: ALL_USERS에 ORACLE_MAINTAINED가 없다 — 아래에서 알려진 내장 스키마 목록으로 다시 묻는다
            }
            if (ReferenceEquals(_host.GetSession(dbId), session))
                _host.SetHasOracleMaintained(dbId, false);
            return await session.QueryAsync(query(false), read(false), token);
        }

        private bool IsCurrent(DbTreeData db, DbSession session)
        {
            DbTreeData current;
            return !_shutdown && db != null && session != null && _data.TryGetValue(db.DbId, out current) && ReferenceEquals(current, db)
                && ReferenceEquals(_host.GetSession(db.DbId), session);
        }

        private void NotifyHostState(string dbId)
        {
            try
            {
                _host.StateChanged(dbId);
            }
            catch (Exception ex)
            {
                Report(ex, dbId);
            }
        }

        // ================= 검색 =================

        private bool ColumnsScope
        {
            get { return _scope != null && _scope.SelectedIndex == 1; }
        }

        private void Search_TextChanged(object sender, TextChangedEventArgs e)
        {
            try
            {
                var hasText = _search.Text.Length > 0;
                _placeholder.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
                _clear.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
                if (_settingText)
                    return;
                _searchTimer.Stop();
                if (_search.Text.Trim().Length == 0)
                {
                    if (_appliedTerm != null)
                        ClearSearch();
                    else
                        Rebuild();
                    return;
                }
                _footer.Text = "검색 중…";
                _searchTimer.Start();
            }
            catch (Exception ex)
            {
                Report(ex, null);
            }
        }

        private void SearchTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                _searchTimer.Stop();
                var term = _search.Text.Trim();
                if (term.Length > 0 && !_shutdown)
                    StartSearch(term, true);
            }
            catch (Exception ex)
            {
                Report(ex, null);
            }
        }

        private void Search_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            try
            {
                switch (e.Key)
                {
                    case Key.Enter:
                        e.Handled = true;
                        OnSearchEnter((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1);
                        break;
                    case Key.Escape:
                        if (_search.Text.Length > 0 || _appliedTerm != null)
                        {
                            e.Handled = true;
                            ClearSearch();
                        }
                        break;
                    case Key.Down:
                        if (_entries.Count > 0)
                        {
                            e.Handled = true;
                            FocusTree();
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                Report(ex, null);
            }
        }

        private void OnSearchEnter(int step)
        {
            var term = _search.Text.Trim();
            if (term.Length == 0)
                return;
            // 입력이 멈추기를 기다리는 중이면 바로 검색하고, 결과가 오면 첫(또는 마지막) 일치로 간다
            if (_searchTimer.IsEnabled || _appliedTerm != term)
            {
                _gotoAfterSearch = step;
                StartSearch(term, true);
                return;
            }
            if (_hits.Count == 0 && _searchWaiting.Count > 0)
            {
                _gotoAfterSearch = step;
                return;
            }
            GotoHit(step);
        }

        private void Clear_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                e.Handled = true;
                ClearSearch();
                _search.Focus();
            }
            catch (Exception ex)
            {
                Report(ex, null);
            }
        }

        private void Next_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                GotoHit(1);
            }
            catch (Exception ex)
            {
                Report(ex, null);
            }
        }

        private void Previous_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                GotoHit(-1);
            }
            catch (Exception ex)
            {
                Report(ex, null);
            }
        }

        private void Scope_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (_appliedTerm == null)
                    return;
                _gotoAfterSearch = 0;
                StartSearch(_appliedTerm, true);
            }
            catch (Exception ex)
            {
                Report(ex, null);
            }
        }

        private void ShowSystem_Changed(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_appliedTerm == null)
                {
                    Rebuild();
                    return;
                }
                _gotoAfterSearch = 0;
                StartSearch(_appliedTerm, false);
            }
            catch (Exception ex)
            {
                Report(ex, null);
            }
        }

        /// <summary>검색을 새로 시작한다(이전 검색은 취소하고 결과는 버림). 연결된 DB마다 따로 검색해 오는 대로 보인다.</summary>
        private void StartSearch(string term, bool resetState)
        {
            _searchTimer.Stop();
            CancelSearchQueries();
            _appliedTerm = term;
            if (resetState)
                _searchState = new TreeState();
            _results = new Dictionary<string, TreeSearchResult>(StringComparer.Ordinal);
            _currentHit = null;
            _searchCts = new CancellationTokenSource();
            var generation = _searchGeneration;
            foreach (var profile in Profiles())
            {
                if (_host.GetSession(profile.Id) != null)
                    SearchDb(profile.Id, generation);
            }
            Rebuild();
        }

        private void CancelSearchQueries()
        {
            _searchGeneration++;
            _searchWaiting.Clear();
            var cts = _searchCts;
            _searchCts = null;
            if (cts != null)
                ShellUi.CancelInBackground(cts);
        }

        private async void SearchDb(string dbId, int generation)
        {
            DbSession session = null;
            CancellationTokenSource cts = null;
            TreeSearchResult result = null;
            CancellationTokenSource linked = null;
            try
            {
                session = _host.GetSession(dbId);
                cts = _searchCts;
                var term = _appliedTerm;
                if (session == null || cts == null || term == null || _shutdown || _searchWaiting.Contains(dbId))
                    return;
                _searchWaiting.Add(dbId);
                var includeSystem = _showSystem.IsChecked == true;
                var columns = ColumnsScope;
                linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, _host.LoadToken(dbId));
                result = await SearchOneAsync(session, dbId, SqlScript.ContainsPattern(term), includeSystem, columns, linked.Token);
            }
            catch (Exception ex)
            {
                // 새 검색·끊기로 취소된 검색은 버린다
                if (cts != null && !cts.IsCancellationRequested && !DbSession.IsCancellation(ex))
                    result = new TreeSearchResult { Error = DbSession.DescribeError(ex) };
            }
            finally
            {
                if (linked != null)
                    linked.Dispose();
            }
            try
            {
                if (session == null || cts == null || _shutdown || generation != _searchGeneration)
                    return;
                _searchWaiting.Remove(dbId);
                if (result != null && ReferenceEquals(_host.GetSession(dbId), session))
                    _results[dbId] = result;
                if (session.IsBroken)
                    NotifyHostState(dbId);
                Rebuild();
                if (_gotoAfterSearch != 0 && (_hits.Count > 0 || _searchWaiting.Count == 0))
                {
                    var step = _gotoAfterSearch;
                    _gotoAfterSearch = 0;
                    GotoHit(step);
                }
            }
            catch (Exception ex)
            {
                Report(ex, dbId);
            }
        }

        /// <summary>DB 하나에서 스키마·객체(·열) 이름을 찾는다. 하나가 실패해도 나머지는 찾고 첫 오류를 결과에 남긴다.</summary>
        private async Task<TreeSearchResult> SearchOneAsync(DbSession session, string dbId, string pattern, bool includeSystem, bool columns, CancellationToken token)
        {
            var limit = OracleMetadata.SearchLimit;
            var meta = session.Provider.Metadata;
            var fallback = meta.HasMaintainedFlagFallback;
            string error = null;
            List<SchemaInfo> schemas = null;
            List<SearchHit> objects = null;
            List<SearchHit> columnHits = null;
            try
            {
                schemas = await QueryWithOmAsync<SchemaInfo>(session, dbId, om => meta.SearchSchemas(pattern, includeSystem, om, limit),
                    om => r => meta.ReadSchema(r, om), fallback, token);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                error = DbSession.DescribeError(ex);
            }
            try
            {
                objects = await QueryWithOmAsync<SearchHit>(session, dbId, om => meta.SearchObjects(pattern, includeSystem, om, limit),
                    om => r => meta.ReadObjectHit(r), fallback && !includeSystem, token);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                error = error ?? DbSession.DescribeError(ex);
            }
            if (columns)
            {
                try
                {
                    columnHits = await QueryWithOmAsync<SearchHit>(session, dbId, om => meta.SearchColumns(pattern, includeSystem, om, limit),
                        om => r => meta.ReadColumnHit(r), fallback && !includeSystem, token);
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    error = error ?? DbSession.DescribeError(ex);
                }
            }
            return TreeLoaderLogic.SearchResult(schemas, objects, columnHits, limit, error);
        }

        private void GotoHit(int step)
        {
            if (_hits.Count == 0)
                return;
            var index = TreePanelLogic.Step(_hits.IndexOf(_currentHit), step, _hits.Count);
            if (index < 0)
                return;
            _currentHit = _hits[index];
            _selectedKey = _currentHit;
            Rebuild();
            var entry = Find(_currentHit);
            if (entry != null)
                _list.ScrollIntoView(entry);
        }

        /// <summary>검색을 지운다(Esc·✕·빈 검색어). 검색 중 고른 행은 원래 트리에서도 보이도록 상위를 펼친다.</summary>
        private void ClearSearch()
        {
            _searchTimer.Stop();
            var wasSearching = _appliedTerm != null;
            CancelSearchQueries();
            if (wasSearching && _selectedKey != null)
                TreePanelLogic.ExpandAncestors(_state, _selectedKey, _selectedRow == null ? null : _selectedRow.ObjectType);
            _appliedTerm = null;
            _searchState = new TreeState();
            _results = new Dictionary<string, TreeSearchResult>(StringComparer.Ordinal);
            _hits = new List<string>();
            _currentHit = null;
            _gotoAfterSearch = 0;
            SetSearchText("");
            Rebuild();
            var entry = _selectedKey == null ? null : Find(_selectedKey);
            if (entry != null)
                _list.ScrollIntoView(entry);
        }

        private void SetSearchText(string text)
        {
            _settingText = true;
            try
            {
                _search.Text = text;
            }
            finally
            {
                _settingText = false;
            }
        }

        // ================= 도우미 =================

        private IEnumerable<OracleConnectionProfile> Profiles()
        {
            var profiles = _host.Profiles;
            if (profiles == null)
                return Enumerable.Empty<OracleConnectionProfile>();
            return profiles.Where(p => p != null && !string.IsNullOrEmpty(p.Id));
        }

        private DbTreeData DataOf(string dbId)
        {
            DbTreeData db;
            return dbId != null && _data.TryGetValue(dbId, out db) ? db : null;
        }

        private TreeEntry Find(string key)
        {
            if (key == null)
                return null;
            foreach (var entry in _entries)
            {
                if (entry.Row.Key == key)
                    return entry;
            }
            return null;
        }

        private void Report(Exception ex, string dbId)
        {
            try
            {
                _host.ReportError(dbId, "트리 처리 중 오류: " + DbSession.DescribeError(ex));
            }
            catch (Exception)
            {
                // 오류를 알리는 것마저 실패하면 더 할 수 있는 것이 없다 — Folderss를 멈추지 않는 것이 우선
            }
        }

        /// <summary>항목 컨테이너를 줄에 맞춘다: 내용을 가로로 채우고(오른쪽 개수 표시), 안내 행은 키보드로 고르지 않게.</summary>
        private sealed class TreeList : ListBox
        {
            /// <summary>items만 고른다(바뀜 알림 한 번). last를 맨 끝에 넣어 지금 행이 되게 한다.</summary>
            public void SelectRange(List<TreeEntry> items, TreeEntry last)
            {
                var ordered = items.Where(i => !ReferenceEquals(i, last)).ToList();
                if (items.Contains(last))
                    ordered.Add(last);
                SetSelectedItems(ordered);
            }

            protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
            {
                base.PrepareContainerForItemOverride(element, item);
                var container = element as ListBoxItem;
                if (container == null)
                    return;
                container.Padding = new Thickness(0);
                container.MinHeight = 0;
                container.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                container.VerticalContentAlignment = VerticalAlignment.Center;
                var entry = item as TreeEntry;
                container.Focusable = entry == null || entry.Row == null || entry.Row.Kind != TreeRowKind.Note || entry.Row.IsLoadMore;
            }
        }

        /// <summary>목록을 통째로 바꿀 수 있는 ObservableCollection(바뀜 알림 한 번).</summary>
        private sealed class EntryCollection : ObservableCollection<TreeEntry>
        {
            public void ReplaceAll(IEnumerable<TreeEntry> items)
            {
                CheckReentrancy();
                Items.Clear();
                foreach (var item in items)
                    Items.Add(item);
                OnPropertyChanged(new PropertyChangedEventArgs("Count"));
                OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
                OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            }
        }
    }

    /// <summary>트리 목록의 항목: 행과 그 행의 화면 상태. 같은 키의 행을 새로 만들면 Update로 내용만 바꾼다(선택·포커스 유지).</summary>
    internal sealed class TreeEntry
    {
        public TreeEntry(TreeRow row, RowBadges badges, Brush stripe)
        {
            Row = row;
            Badges = badges;
            Stripe = stripe;
            Signature = TreePanelLogic.Signature(row, badges);
        }

        public TreeRow Row { get; private set; }

        public RowBadges Badges { get; private set; }

        /// <summary>DB 행 왼쪽의 접속 색 띠. 없으면 null.</summary>
        public Brush Stripe { get; private set; }

        public string Signature { get; private set; }

        /// <summary>지금 이 항목을 그리는 뷰(목록이 뷰를 재사용하면 바뀐다).</summary>
        public TreeRowView View { get; set; }

        /// <summary>같은 키의 새 항목 내용으로 바꾼다. 보이는 모양이 바뀌었으면 true.</summary>
        public bool Update(TreeEntry fresh)
        {
            Row = fresh.Row;
            Badges = fresh.Badges;
            Stripe = fresh.Stripe;
            if (Signature == fresh.Signature)
                return false;
            Signature = fresh.Signature;
            return true;
        }

        /// <summary>화면 읽기 프로그램과 목록의 글자 찾기가 쓰는 이름.</summary>
        public override string ToString()
        {
            return Row == null ? "" : Row.Text ?? "";
        }
    }

    /// <summary>
    /// 트리 한 줄(DataTemplate이 만드는 뷰). DataContext(TreeEntry)가 바뀌거나 같은 항목의 내용이 바뀌면 다시 그린다.
    /// 왼쪽부터 들여쓰기, ▶/▼(누르면 펼치기·접기), 종류 아이콘, 이름(검색어 강조), 보조 글자·배지, 오른쪽 끝 개수.
    /// </summary>
    internal sealed class TreeRowView : Border
    {
        private const double IndentBase = 6;
        private const double IndentStep = 14;

        private TreeEntry _entry;
        private FrameworkElement _twisty;

        public TreeRowView()
        {
            Background = Brushes.Transparent;
            SnapsToDevicePixels = true;
            DataContextChanged += OnDataContextChanged;
        }

        /// <summary>누른 곳(source)이 ▶/▼ 영역인지.</summary>
        public bool IsOnTwisty(object source)
        {
            var twisty = _twisty;
            var node = source as DependencyObject;
            for (var depth = 0; twisty != null && node != null && depth < 32; depth++)
            {
                if (ReferenceEquals(node, twisty))
                    return true;
                if (ReferenceEquals(node, this))
                    return false;
                node = ShellUi.ParentOf(node);
            }
            return false;
        }

        public void Render()
        {
            try
            {
                RenderCore();
            }
            catch (Exception)
            {
                // 한 줄을 그리지 못해도 목록 전체(와 Folderss)는 계속 동작해야 한다 — 빈 줄로 둔다
                Child = null;
                _twisty = null;
            }
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            var old = e.OldValue as TreeEntry;
            if (old != null && ReferenceEquals(old.View, this))
                old.View = null;
            _entry = e.NewValue as TreeEntry;
            if (_entry != null)
                _entry.View = this;
            Render();
        }

        private void RenderCore()
        {
            Child = null;
            ToolTip = null;
            _twisty = null;
            var entry = _entry;
            if (entry == null || entry.Row == null)
                return;
            var row = entry.Row;
            var badges = entry.Badges ?? new RowBadges();

            // 왼쪽 띠: 검색의 지금 일치는 강조색, DB 행은 접속 색 표시
            double stripe = 0;
            if (badges.Current)
            {
                stripe = 2;
                SetResourceReference(BorderBrushProperty, Theme.Accent);
            }
            else if (entry.Stripe != null)
            {
                stripe = 3;
                BorderBrush = entry.Stripe;
            }
            else
            {
                ClearValue(BorderBrushProperty);
            }
            BorderThickness = new Thickness(stripe, 0, 0, 0);
            Padding = new Thickness(Math.Max(0, IndentBase + row.Depth * IndentStep - stripe), 1, 6, 1);

            var line = new DockPanel { LastChildFill = true };
            _twisty = Twisty(row);
            DockPanel.SetDock(_twisty, Dock.Left);
            line.Children.Add(_twisty);
            if (row.Kind == TreeRowKind.Note)
            {
                line.Children.Add(NoteText(row, badges));
                ToolTip = row.Text;
                Child = line;
                return;
            }
            var icon = Icon(row, badges);
            DockPanel.SetDock(icon, Dock.Left);
            line.Children.Add(icon);
            var meta = Meta(row);
            if (meta != null)
            {
                DockPanel.SetDock(meta, Dock.Right);
                line.Children.Add(meta);
            }
            line.Children.Add(Body(row, badges));
            Child = line;
        }

        private static FrameworkElement Twisty(TreeRow row)
        {
            var glyph = Theme.Text(row.Expandable && row.Kind != TreeRowKind.Note ? (row.Expanded ? "▼" : "▶") : "", Theme.SecondaryText);
            glyph.FontSize = 8.5;
            glyph.HorizontalAlignment = HorizontalAlignment.Center;
            glyph.VerticalAlignment = VerticalAlignment.Center;
            // 투명 바탕: 글자 밖(줄 높이 전체)을 눌러도 펼치기
            return new Border { Width = 14, Background = Brushes.Transparent, Child = glyph };
        }

        private static TextBlock Icon(TreeRow row, RowBadges badges)
        {
            var icon = new TextBlock
            {
                Width = 15,
                TextAlignment = TextAlignment.Center,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 3, 0)
            };
            var key = Theme.SecondaryText;
            Brush brush = null;
            switch (row.Kind)
            {
                case TreeRowKind.Database:
                    icon.Text = badges.Connected ? "●" : "○";
                    if (badges.Broken)
                        brush = Theme.Danger;
                    else if (badges.Connected)
                        brush = Theme.Success;
                    else
                        key = Theme.DisabledText;
                    break;
                case TreeRowKind.Schema:
                    icon.Text = "◫";
                    key = Theme.PrimaryText;
                    break;
                case TreeRowKind.Group:
                    icon.Text = "▦";
                    break;
                case TreeRowKind.Object:
                    icon.Text = ObjectGlyph(row.ObjectType);
                    icon.FontFamily = Theme.Mono;
                    if (row.ObjectType == TreeGroups.Table)
                        key = Theme.Accent;
                    break;
                default:
                    icon.Text = "·";
                    break;
            }
            if (brush != null)
                icon.Foreground = brush;
            else
                Theme.Foreground(icon, key);
            return icon;
        }

        private static string ObjectGlyph(string objectType)
        {
            switch (objectType)
            {
                case "TABLE": return "T";
                case "VIEW": return "V";
                case "SEQUENCE": return "S";
                default: return "P";
            }
        }

        private static FrameworkElement Body(TreeRow row, RowBadges badges)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            panel.Children.Add(NameText(row));
            switch (row.Kind)
            {
                case TreeRowKind.Database:
                    if (!string.IsNullOrEmpty(row.Detail))
                        panel.Children.Add(Detail(row.Detail, 10.5));
                    if (badges.Connecting)
                    {
                        panel.Children.Add(FillBadge("연결 중…", Theme.Warning, ShellUi.WarningTint));
                        break;
                    }
                    if (badges.Broken)
                        panel.Children.Add(FillBadge("연결 끊김", Theme.Danger, ShellUi.DangerTint));
                    if (badges.ProfileChanged)
                    {
                        // 보이는 주소·색은 열린 세션의 것이다. 저장한 새 값은 다시 연결할 때 적용된다
                        var changed = OutlineBadge("변경됨", Theme.SecondaryText, Theme.Border);
                        changed.ToolTip = ShellLogic.ProfileChangedNote;
                        panel.Children.Add(changed);
                    }
                    if (badges.ReadOnly)
                        panel.Children.Add(OutlineBadge("읽기 전용", Theme.SecondaryText, Theme.Border));
                    if (!string.IsNullOrEmpty(badges.Pending))
                        panel.Children.Add(FillBadge(badges.Pending, Theme.Danger, ShellUi.DangerTint));
                    if (badges.Running)
                        panel.Children.Add(FillBadge("실행 중", Theme.Warning, ShellUi.WarningTint));
                    break;
                case TreeRowKind.Schema:
                    if (row.IsMySchema)
                        panel.Children.Add(OutlineBadge("내 스키마", Theme.Accent, Theme.Accent));
                    if (row.IsSystemSchema)
                        panel.Children.Add(OutlineBadge("내장", Theme.SecondaryText, Theme.Border));
                    break;
                case TreeRowKind.Column:
                    if (!string.IsNullOrEmpty(row.Detail))
                        panel.Children.Add(Detail(row.Detail, 11));
                    if (row.IsPrimaryKey)
                    {
                        var pk = new TextBlock { Text = "PK", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Theme.Warning, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) };
                        panel.Children.Add(pk);
                    }
                    break;
            }
            return panel;
        }

        private static TextBlock NameText(TreeRow row)
        {
            var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            AddHighlighted(name.Inlines, row.Text ?? "", row.Highlights);
            if (row.Kind == TreeRowKind.Database || row.IsHit)
                name.FontWeight = FontWeights.SemiBold;
            Theme.Foreground(name, row.IsDim ? Theme.SecondaryText : Theme.PrimaryText);
            return name;
        }

        /// <summary>검색어 위치(spans)만 강조 바탕으로. 겹치거나 범위를 벗어난 위치는 건너뛴다.</summary>
        private static void AddHighlighted(InlineCollection inlines, string text, IEnumerable<TextSpan> spans)
        {
            var position = 0;
            foreach (var span in (spans ?? Enumerable.Empty<TextSpan>()).OrderBy(s => s.Start))
            {
                if (span.Length <= 0 || span.Start < position || span.Start + span.Length > text.Length)
                    continue;
                if (span.Start > position)
                    inlines.Add(new Run(text.Substring(position, span.Start - position)));
                inlines.Add(new Run(text.Substring(span.Start, span.Length)) { Background = ShellUi.HighlightTint });
                position = span.Start + span.Length;
            }
            if (position < text.Length || inlines.Count == 0)
                inlines.Add(new Run(text.Substring(position)));
        }

        private static TextBlock Detail(string text, double fontSize)
        {
            var detail = new TextBlock
            {
                Text = text,
                FontFamily = Theme.Mono,
                FontSize = fontSize,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };
            return Theme.Foreground(detail, Theme.SecondaryText);
        }

        /// <summary>오른쪽 끝 표시: 묶음은 개수("일치 / 전체"), 객체는 보조 글자(≈행 수 · FUNCTION · INVALID — INVALID는 위험색).</summary>
        private static FrameworkElement Meta(TreeRow row)
        {
            string text = null;
            if (row.Kind == TreeRowKind.Group)
                text = TreePanelLogic.GroupMeta(row);
            else if (row.Kind == TreeRowKind.Object)
                text = row.Detail;
            if (string.IsNullOrEmpty(text))
                return null;
            var meta = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            Theme.Foreground(meta, Theme.SecondaryText);
            var parts = text.Split(new[] { " · " }, StringSplitOptions.None);
            for (var i = 0; i < parts.Length; i++)
            {
                if (i > 0)
                    meta.Inlines.Add(new Run(" · "));
                var run = new Run(parts[i]);
                if (parts[i] == "INVALID")
                    run.Foreground = Theme.Danger;
                meta.Inlines.Add(run);
            }
            return meta;
        }

        private static TextBlock NoteText(TreeRow row, RowBadges badges)
        {
            var note = new TextBlock
            {
                Text = (row.Text ?? "") + (row.IsLoadMore && badges.Loading ? " · 불러오는 중…" : ""),
                FontStyle = FontStyles.Italic,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (row.IsError)
            {
                note.Foreground = Theme.Danger;
            }
            else if (row.IsLoadMore)
            {
                Theme.Foreground(note, Theme.Accent);
                note.Cursor = Cursors.Hand;
            }
            else
            {
                Theme.Foreground(note, Theme.SecondaryText);
            }
            return note;
        }

        private static Border OutlineBadge(string text, string textKey, string borderKey)
        {
            var label = new TextBlock { Text = text, FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
            Theme.Foreground(label, textKey);
            if (textKey == Theme.Accent)
                label.FontWeight = FontWeights.SemiBold;
            var badge = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 0, 4, 0),
                Margin = new Thickness(5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = label
            };
            badge.SetResourceReference(BorderBrushProperty, borderKey);
            return badge;
        }

        private static Border FillBadge(string text, Brush foreground, Brush background)
        {
            var label = new TextBlock { Text = text, FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = foreground, VerticalAlignment = VerticalAlignment.Center };
            return new Border
            {
                Background = background,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 0, 4, 0),
                Margin = new Thickness(5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = label
            };
        }
    }

    /// <summary>셸·트리 화면 조각: 테마 색으로 그린 체크 상자·작은 버튼·주 동작 버튼, 반투명 의미 색, 시각 트리 도우미.</summary>
    internal static class ShellUi
    {
        /// <summary>
        /// 요소에 파일 드래그 드롭을 받는다(탐색기에서 끌어다 놓기). 파일이 아닌 드래그(글자 등)는 요소의 기본 동작에 맡긴다.
        /// TextBox는 자기 드래그 처리가 있어 Preview 이벤트로 먼저 가로챈다. onFiles에는 실제로 있는 파일만(순서대로, 중복 없이).
        /// </summary>
        public static void EnableFileDrop(UIElement element, Action<IReadOnlyList<string>> onFiles)
        {
            if (element == null || onFiles == null)
                return;
            element.AllowDrop = true;
            element.PreviewDragOver += (s, e) =>
            {
                if (!e.Data.GetDataPresent(DataFormats.FileDrop))
                    return;
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            };
            element.PreviewDrop += (s, e) =>
            {
                if (!e.Data.GetDataPresent(DataFormats.FileDrop))
                    return;
                e.Handled = true;
                var files = SqlFileLogic.DroppedFiles(e.Data.GetData(DataFormats.FileDrop) as string[]);
                if (files.Count > 0)
                    onFiles(files);
            };
        }

        /// <summary>검색어 강조 바탕(경고색 30%).</summary>
        public static readonly Brush HighlightTint = Tint(Theme.Warning, 0x4D);

        /// <summary>위험 배지·커밋 대기 표시 바탕(위험색 반투명).</summary>
        public static readonly Brush DangerTint = Tint(Theme.Danger, 0x2E);

        /// <summary>진행 중 배지 바탕(경고색 반투명).</summary>
        public static readonly Brush WarningTint = Tint(Theme.Warning, 0x2E);

        private static readonly Geometry CheckMark = FrozenGeometry("M 0,4 L 3.5,7.5 L 10,0");

        private static ControlTemplate _checkTemplate;
        private static ControlTemplate _primaryTemplate;

        /// <summary>
        /// 테마 색 체크 상자. 기본 CheckBox 템플릿은 체크 표시 색이 고정이라 어두운 테마에서 바탕을 바꾸면 표시가 안 보인다 — 템플릿을 직접 준다.
        /// </summary>
        public static CheckBox ThemedCheckBox(string text)
        {
            var check = new CheckBox { Content = text, Template = CheckTemplate(), VerticalAlignment = VerticalAlignment.Center };
            return Theme.Foreground(check, Theme.SecondaryText);
        }

        /// <summary>검색 막대의 ▲▼ 같은 작은 버튼(Folderss 버튼 모양).</summary>
        public static Button SmallButton(string text, string tooltip)
        {
            var button = new Button
            {
                Content = text,
                FontSize = 9,
                Padding = new Thickness(5, 0, 5, 0),
                MinWidth = 0,
                MinHeight = 0,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = tooltip
            };
            AutomationProperties.SetName(button, tooltip);
            return button;
        }

        /// <summary>
        /// 주 동작 버튼(강조색 바탕). Folderss 버튼 템플릿은 마우스를 올리면 바탕을 테마 색으로 덮으므로 템플릿을 직접 준다.
        /// 글자는 켜짐이면 PanelBackground(강조색 위에서 두 테마 모두 읽힘), 꺼짐이면 흐린 글자색.
        /// </summary>
        public static Button PrimaryButton(string text)
        {
            var label = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold };
            var button = new Button { Content = label, Template = PrimaryTemplate(), Padding = new Thickness(10, 2, 10, 2) };
            ApplyPrimaryLabel(button, label);
            button.IsEnabledChanged += (s, e) => ApplyPrimaryLabel(button, label);
            AutomationProperties.SetName(button, text);
            return button;
        }

        /// <summary>source부터 위로 올라가며 T를 찾는다(stopAt에 닿으면 멈춤). Run 같은 ContentElement도 따라 올라간다.</summary>
        public static T FindAncestor<T>(DependencyObject source, DependencyObject stopAt) where T : DependencyObject
        {
            var node = source;
            for (var depth = 0; node != null && depth < 64; depth++)
            {
                var found = node as T;
                if (found != null)
                    return found;
                if (ReferenceEquals(node, stopAt))
                    return null;
                node = ParentOf(node);
            }
            return null;
        }

        public static DependencyObject ParentOf(DependencyObject node)
        {
            if (node is Visual || node is Visual3D)
                return VisualTreeHelper.GetParent(node);
            return LogicalTreeHelper.GetParent(node);
        }

        /// <summary>
        /// 취소를 스레드 풀에서 요청한다. 취소 콜백이 실행 중인 OracleCommand.Cancel()을 부르는데, 네트워크가 느리면 그 호출이 UI를 멈출 수 있다.
        /// </summary>
        public static void CancelInBackground(CancellationTokenSource cts)
        {
            if (cts == null)
                return;
            Task.Run(() =>
            {
                try
                {
                    cts.Cancel();
                }
                catch (Exception)
                {
                    // 취소 콜백 실패(이미 끝난 명령 등)는 취소할 것이 없다는 뜻이다
                }
            });
        }

        private static void ApplyPrimaryLabel(Button button, TextBlock label)
        {
            Theme.Foreground(label, button.IsEnabled ? Theme.PanelBackground : Theme.DisabledText);
        }

        private static ControlTemplate CheckTemplate()
        {
            // 봉인한 템플릿은 여러 체크 상자가 함께 쓴다
            if (_checkTemplate != null)
                return _checkTemplate;
            var root = new FrameworkElementFactory(typeof(StackPanel));
            root.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            root.SetValue(Panel.BackgroundProperty, Brushes.Transparent);
            var box = new FrameworkElementFactory(typeof(Border), "box");
            box.SetValue(FrameworkElement.WidthProperty, 13.0);
            box.SetValue(FrameworkElement.HeightProperty, 13.0);
            box.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            box.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            box.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
            box.SetResourceReference(Border.BackgroundProperty, Theme.ControlBackground);
            box.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
            var mark = new FrameworkElementFactory(typeof(Path), "mark");
            mark.SetValue(Path.DataProperty, CheckMark);
            mark.SetValue(Shape.StretchProperty, Stretch.Uniform);
            mark.SetValue(Shape.StrokeThicknessProperty, 1.6);
            mark.SetResourceReference(Shape.StrokeProperty, Theme.Accent);
            mark.SetValue(FrameworkElement.MarginProperty, new Thickness(2));
            mark.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            box.AppendChild(mark);
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 0, 0, 0));
            presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            root.AppendChild(box);
            root.AppendChild(presenter);

            var template = new ControlTemplate(typeof(CheckBox)) { VisualTree = root };
            var isChecked = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
            isChecked.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "mark"));
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension(Theme.Accent), "box"));
            var focused = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
            focused.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension(Theme.Accent), "box"));
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.5));
            template.Triggers.Add(isChecked);
            template.Triggers.Add(hover);
            template.Triggers.Add(focused);
            template.Triggers.Add(disabled);
            template.Seal();
            _checkTemplate = template;
            return template;
        }

        private static ControlTemplate PrimaryTemplate()
        {
            if (_primaryTemplate != null)
                return _primaryTemplate;
            var border = new FrameworkElementFactory(typeof(Border), "border");
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            border.SetResourceReference(Border.BackgroundProperty, Theme.Accent);
            border.SetResourceReference(Border.BorderBrushProperty, Theme.Accent);
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);

            var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension(Theme.AccentHover), "border"));
            hover.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension(Theme.AccentHover), "border"));
            var focused = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
            focused.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension(Theme.PrimaryText), "border"));
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension(Theme.ControlBackground), "border"));
            disabled.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension(Theme.Border), "border"));
            template.Triggers.Add(hover);
            template.Triggers.Add(focused);
            template.Triggers.Add(disabled);
            template.Seal();
            _primaryTemplate = template;
            return template;
        }

        private static Brush Tint(Brush source, byte alpha)
        {
            var solid = source as SolidColorBrush;
            if (solid == null)
                return Brushes.Transparent;
            var brush = new SolidColorBrush(Color.FromArgb(alpha, solid.Color.R, solid.Color.G, solid.Color.B));
            brush.Freeze();
            return brush;
        }

        private static Geometry FrozenGeometry(string data)
        {
            var geometry = Geometry.Parse(data);
            geometry.Freeze();
            return geometry;
        }
    }
}

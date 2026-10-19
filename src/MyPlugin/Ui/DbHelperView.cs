using Folderss.Plugins;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MyPlugin
{
    /// <summary>
    /// DB Helper 팝업 화면(PoC의 창 전체): 위 툴바, 왼쪽 DB·스키마 트리, 오른쪽 SQL 작업 영역.
    /// DB(접속)마다 세션을 하나 열고(같은 DB의 탭은 트랜잭션을 공유), 창을 닫을 때 커밋 대기 변경을 DB마다 묻는다.
    /// </summary>
    internal sealed class DbHelperView : Grid, IDbHost, ITreeHost, IShellCommands
    {
        private const string DisconnectAction = "연결을 끊기";
        private const string CloseAction = "창을 닫기";

        // 창을 닫을 때 취소한 실행이 멈추기를 기다리는 시간, 롤백으로 닫는 세션을 기다리는 시간(커밋은 끝까지 기다린다)
        private static readonly TimeSpan RunningStopWait = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan RollbackCloseWait = TimeSpan.FromSeconds(3);

        private readonly IPluginManager _manager;
        private readonly Dictionary<string, DbState> _dbs = new Dictionary<string, DbState>(StringComparer.Ordinal);
        // 열려 있는 성능 모니터 창(DB마다 하나). 닫히면 뺀다.
        private readonly Dictionary<string, MonitorWindow> _monitors = new Dictionary<string, MonitorWindow>(StringComparer.Ordinal);
        // 저장된 목록에서 빠졌지만 아직 연결이 살아 있어 목록에 남겨 둔 접속(끊으면 뺀다)
        private readonly HashSet<string> _orphans = new HashSet<string>(StringComparer.Ordinal);
        private readonly DbTreePanel _tree;
        private readonly SqlWorkspace _workspace;
        private readonly ShellMenu _shellMenu;

        private List<OracleConnectionProfile> _profiles = new List<OracleConnectionProfile>();
        private string _profilesError;
        private string _selectedDbId;

        private Button _connect;
        private Button _disconnect;
        private Button _commit;
        private Button _rollback;
        private TextBlock _selectedLabel;
        private Run _selectedText;
        private Border _modePill;
        private TextBlock _modeText;
        private FrameworkElement _modeDot;
        private ComboBox _fetchCount;

        private Window _window;
        private bool _staticSubscribed;
        private bool _focusedOnce;
        private bool _closingFlow;
        private bool _closeConfirmed;
        private bool _closeFlowFailed;
        private bool _closed;

        public DbHelperView(IPluginManager manager)
        {
            if (manager == null)
                throw new ArgumentNullException(nameof(manager));
            _manager = manager;
            Theme.Background(this, Theme.WindowBackground);
            LoadProfiles();
            _selectedDbId = _profiles.Select(p => p.Id).FirstOrDefault();

            // 맨 위 메뉴 막대·아이콘(미니 패널) → 툴바 → 트리·작업 영역
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _shellMenu = new ShellMenu(this);
            Children.Add(_shellMenu.View);
            var toolbar = BuildToolbar();
            Grid.SetRow(toolbar, 1);
            Children.Add(toolbar);

            // 작업 영역은 생성자에서 Profiles·SelectedDbId를 읽으므로 접속 목록·툴바를 먼저 준비한다
            _tree = new DbTreePanel(this);
            _workspace = new SqlWorkspace(this);
            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230), MinWidth = 160 });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 320 });
            body.Children.Add(_tree);
            var splitter = BuildSplitter();
            Grid.SetColumn(splitter, 1);
            body.Children.Add(splitter);
            Grid.SetColumn(_workspace, 2);
            body.Children.Add(_workspace);
            Grid.SetRow(body, 2);
            Children.Add(body);

            _workspace.ActiveTabChanged += Workspace_ActiveTabChanged;
            _workspace.CommandStateChanged += (s, e) => RefreshShellMenu();
            // 창 전체 단축키(트리·결과에 포커스가 있어도): Ctrl+N·O·S·Shift+S·W
            PreviewKeyDown += View_PreviewKeyDown;
            _tree.SelectDb(_selectedDbId);
            _tree.Rebuild();
            RefreshToolbar();
            Loaded += View_Loaded;
            Unloaded += View_Unloaded;
        }

        // ================= IDbHost · ITreeHost =================

        /// <summary>접속 목록. 연결 중인 접속은 연결할 때의 사본(<see cref="FindProfile"/>).</summary>
        public IReadOnlyList<OracleConnectionProfile> Profiles
        {
            get { return _profiles.Select(SessionOrSaved).ToList(); }
        }

        public string ProfilesError
        {
            get { return _profilesError; }
        }

        /// <summary>
        /// 접속 정보. 연결 중이면 연결할 때의 사본이다 — 저장 값을 바꿔도 열린 세션은 원래 DB에 붙어 있으므로 주소·이름·색·확인 창·읽기 전용 검사는
        /// 다시 연결할 때까지 사본을 따른다(읽기 전용을 켠 것만 바로 적용). 저장된 값은 <see cref="SavedProfile"/>.
        /// </summary>
        public OracleConnectionProfile FindProfile(string dbId)
        {
            return SessionOrSaved(SavedProfile(dbId));
        }

        /// <summary>연결 중인 접속의 저장 값이 연결할 때와 달라졌으면 true(다시 연결하면 적용).</summary>
        public bool ProfileChanged(string dbId)
        {
            var state = StateOf(dbId);
            return state != null && state.Session != null && ShellLogic.ProfileChanged(SavedProfile(dbId), state.Profile);
        }

        /// <summary>연결된 세션. 닫는 중인 세션은 없는 것으로 본다(닫히는 세션에 새 문장을 실행하지 않게).</summary>
        public DbSession GetSession(string dbId)
        {
            var state = StateOf(dbId);
            return state == null || state.Closing ? null : state.Session;
        }

        public string SelectedDbId
        {
            get { return FindProfile(_selectedDbId) == null ? null : _selectedDbId; }
        }

        public int FetchCount
        {
            get { return ShellLogic.ParseFetchCount(_fetchCount == null ? null : _fetchCount.SelectedItem); }
        }

        public void StateChanged(string dbId)
        {
            try
            {
                if (_closed)
                    return;
                RefreshToolbar();
                _tree.RequestRebuild();
                // 연결·끊기·끊김을 작업 영역의 대상 목록·상태줄에도 바로 알린다(바뀐 것이 없으면 작업 영역이 아무것도 안 함)
                if (_workspace != null)
                    _workspace.OnHostStateChanged();
            }
            catch (Exception ex)
            {
                ReportUnexpected(ex, dbId);
            }
        }

        /// <summary>
        /// 연결한다. 이미 연결돼 있으면 바로 true, 같은 DB를 연결하는 중이면 그 작업을 돌려준다(두 번 연결하지 않음).
        /// 끊긴 세션은 버리고 다시 연결한다. 비밀번호를 묻다 취소하면 false. 오류는 메시지로 알리고 false(예외를 내지 않음).
        /// </summary>
        public Task<bool> ConnectAsync(string dbId)
        {
            try
            {
                if (FindProfile(dbId) == null || _closed || _closingFlow)
                    return Task.FromResult(false);
                var state = EnsureState(dbId);
                if (state.Connecting != null)
                    return state.Connecting;
                if (state.Busy)
                    return Task.FromResult(false);
                if (state.Session != null && !state.Session.IsBroken)
                    return Task.FromResult(true);
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                state.Connecting = completion.Task;
                RefreshToolbar();
                RunConnect(dbId, state, completion);
                return completion.Task;
            }
            catch (Exception ex)
            {
                ReportUnexpected(ex, dbId);
                return Task.FromResult(false);
            }
        }

        public bool IsConnecting(string dbId)
        {
            var state = StateOf(dbId);
            return state != null && state.Opening;
        }

        public bool IsRunning(string dbId)
        {
            try
            {
                return _workspace != null && !string.IsNullOrEmpty(dbId) && _workspace.IsRunning(dbId);
            }
            catch (Exception)
            {
                // 표시용 질문이다 — 작업 영역이 답하지 못하면 실행 중이 아닌 것으로 본다
                return false;
            }
        }

        public string LastSqlFolder
        {
            get { return SafeSetting(SqlFileLogic.FolderSettingKey); }
            set { SaveSetting(SqlFileLogic.FolderSettingKey, value); }
        }

        public void AddRecentSqlFile(string path)
        {
            SaveSetting(SqlFileLogic.RecentSettingKey, SqlFileLogic.SerializeRecent(SqlFileLogic.AddRecent(RecentFiles, path)));
        }

        private string SafeSetting(string key)
        {
            try
            {
                return _manager.GetSetting(key);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void SaveSetting(string key, string value)
        {
            try
            {
                _manager.SetSetting(key, value);
            }
            catch (Exception ex)
            {
                // 최근 파일·폴더를 기억하지 못할 뿐이다
                AddMessage(null, "설정을 저장하지 못했습니다: " + ex.Message, MessageKind.Error);
            }
        }

        // ================= IShellCommands (메뉴 막대·아이콘) =================

        public IReadOnlyList<string> RecentFiles
        {
            get { return SqlFileLogic.ParseRecent(SafeSetting(SqlFileLogic.RecentSettingKey)); }
        }

        public TextBox ActiveEditor
        {
            get { return _workspace != null ? _workspace.ActiveEditor : null; }
        }

        public void NewTab()
        {
            _workspace.NewTab();
        }

        public void OpenFile()
        {
            _workspace.OpenFileWithDialog();
        }

        public void OpenRecent(string path)
        {
            if (!System.IO.File.Exists(path))
            {
                // 옮겼거나 지운 파일은 목록에서 뺀다
                SaveSetting(SqlFileLogic.RecentSettingKey, SqlFileLogic.SerializeRecent(RecentFiles.Where(p => !SqlFileLogic.SamePath(p, path))));
                Dialogs.Show(OwnerWindow(), "파일이 없습니다", "파일을 찾을 수 없어 최근 파일 목록에서 뺐습니다." + Environment.NewLine + path, true);
                return;
            }
            _workspace.OpenPaths(new[] { path });
        }

        public void ClearRecent()
        {
            SaveSetting(SqlFileLogic.RecentSettingKey, null);
        }

        public void Save()
        {
            _workspace.SaveActive(false);
        }

        public void SaveAs()
        {
            _workspace.SaveActive(true);
        }

        public void CloseTab()
        {
            _workspace.CloseActiveTab();
        }

        public void ManageConnections()
        {
            Manage_Click(this, new RoutedEventArgs());
        }

        void IShellCommands.CreateTableSpec()
        {
            if (!_tree.CreateTableSpecFromSelection())
                Dialogs.Show(OwnerWindow(), TableSpecLogic.Title, "트리에서 DB·스키마·테이블을 고른 뒤 다시 누르세요(Ctrl·Shift+누름으로 여러 개).", false);
        }

        void IShellCommands.CreateCodeSpec()
        {
            var dbId = _tree.SelectedDbId();
            if (dbId == null)
                Dialogs.Show(OwnerWindow(), CodeSpecLogic.Title, "트리에서 대상 DB(또는 그 안의 객체)를 고른 뒤 다시 누르세요.", false);
            else
                CreateCodeSpec(dbId);
        }

        void IShellCommands.ShowMonitor()
        {
            var dbId = _tree.SelectedDbId();
            if (dbId == null)
                Dialogs.Show(OwnerWindow(), MonitorLogic.Title, MonitorLogic.PickDbMessage, false);
            else
                ShowMonitor(dbId);
        }

        public void ShowShortcuts()
        {
            Dialogs.Show(OwnerWindow(), "단축키", ShellLogic.ShortcutsText, false);
        }

        public void ShowHelp()
        {
            HelpWindow.Show(OwnerWindow(), HelpFolder, HelpLogic.Index);
        }

        /// <summary>플러그인 폴더의 help(도움말 HTML). 폴더를 모르면 null — 온라인 사본을 연다.</summary>
        private string HelpFolder
        {
            get
            {
                try
                {
                    return HelpLogic.Folder(_manager.PluginDirectory);
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        public void ToggleComment()
        {
            _workspace.ToggleComment();
        }

        public void ChangeCase(bool upper)
        {
            _workspace.ChangeCase(upper);
        }

        public void Run()
        {
            _workspace.RunActiveStatement();
        }

        public void RunScript()
        {
            _workspace.RunActiveScript();
        }

        public void Describe()
        {
            _workspace.DescribeAtCaret();
        }

        public void QuickQuery(string dbId, string owner, string objectName)
        {
            _workspace.QuickQuery(dbId, owner, objectName);
        }

        public void DescribeObject(string dbId, string owner, string objectName)
        {
            if (!SupportsTableInfo(dbId, "테이블 정보"))
                return;
            _workspace.DescribeObject(dbId, owner, objectName);
        }

        public void ShowSource(string dbId, string owner, string objectType, string objectName)
        {
            _workspace.ShowSource(dbId, owner, objectType, objectName);
        }

        /// <summary>테이블 정보(F4)·테이블 정의서는 아직 Oracle 데이터 사전(ALL_*)으로만 읽는다. 다른 공급자면 알리고 false.</summary>
        private bool SupportsTableInfo(string dbId, string feature)
        {
            var profile = FindProfile(dbId);
            var provider = DbProviders.For(profile);
            if (provider.Metadata.SupportsTableInfo)
                return true;
            Dialogs.Show(OwnerWindow(), feature, provider.DisplayName + " 접속에서는 " + feature + "을(를) 아직 지원하지 않습니다(Oracle 데이터 사전 기반). SQL 실행·트리·GCM 정의서는 쓸 수 있습니다.", false);
            return false;
        }

        public void Cancel()
        {
            _workspace.CancelActiveRun();
        }

        public void FetchNext()
        {
            _workspace.FetchNextActive();
        }

        public void Commit()
        {
            Commit_Click(this, new RoutedEventArgs());
        }

        public void Rollback()
        {
            Rollback_Click(this, new RoutedEventArgs());
        }

        public void Connect()
        {
            Connect_Click(this, new RoutedEventArgs());
        }

        void IShellCommands.Reconnect()
        {
            Reconnect(_selectedDbId);
        }

        void IShellCommands.Disconnect()
        {
            Disconnect(_selectedDbId);
        }

        public ShellCommandState CommandState()
        {
            var state = new ShellCommandState();
            if (_workspace == null || _connect == null)
                return state;
            var open = !_closingFlow && !_closed;
            state.CanRun = open && _workspace.ActiveEditor != null && !_workspace.ActiveRunning;
            state.CanCancel = open && _workspace.ActiveRunning;
            state.CanFetch = open && _workspace.ActiveCanFetch;
            state.HasSelection = _workspace.ActiveHasSelection;
            state.CanCommit = _commit.IsEnabled;
            state.CanRollback = _rollback.IsEnabled;
            state.CanConnect = _connect.IsEnabled;
            state.CanDisconnect = _disconnect.IsEnabled;
            var selected = FindProfile(_selectedDbId);
            state.DbName = selected != null ? selected.Name : null;
            var selectedState = selected == null ? null : StateOf(selected.Id);
            state.CanReconnect = open && selectedState != null && selectedState.Session != null
                && selectedState.Connecting == null && !selectedState.Busy;
            return state;
        }

        /// <summary>창 전체 단축키. 편집기 안의 단축키(Ctrl+Enter·/·Shift+U·Shift+L)는 SqlWorkspace가 처리한다.</summary>
        private void View_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            try
            {
                if (_closed || _closingFlow)
                    return;
                var key = e.Key == System.Windows.Input.Key.ImeProcessed ? e.ImeProcessedKey
                    : e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
                var modifiers = System.Windows.Input.Keyboard.Modifiers;
                var command = ShellLogic.WindowShortcut(key.ToString(),
                    (modifiers & System.Windows.Input.ModifierKeys.Control) != 0,
                    (modifiers & System.Windows.Input.ModifierKeys.Shift) != 0,
                    (modifiers & System.Windows.Input.ModifierKeys.Alt) != 0);
                if (command == ShellShortcut.None)
                    return;
                e.Handled = true;
                switch (command)
                {
                    case ShellShortcut.NewTab:
                        NewTab();
                        break;
                    case ShellShortcut.Open:
                        OpenFile();
                        break;
                    case ShellShortcut.Save:
                        Save();
                        break;
                    case ShellShortcut.SaveAs:
                        SaveAs();
                        break;
                    case ShellShortcut.CloseTab:
                        CloseTab();
                        break;
                    case ShellShortcut.RunScript:
                        RunScript();
                        break;
                }
            }
            catch (Exception ex)
            {
                ReportUnexpected(ex, null);
            }
        }

        public string DataDirectory
        {
            get
            {
                try
                {
                    return _manager.DataDirectory;
                }
                catch (Exception)
                {
                    // 폴더를 만들 수 없는 등 — 임시 저장만 못 할 뿐 화면은 계속 쓴다
                    return null;
                }
            }
        }

        /// <summary>트리 메뉴 [연결 끊기]: 툴바 [끊기]와 같다(실행 중이면 막고, 커밋 대기는 묻는다).</summary>
        public async void Disconnect(string dbId)
        {
            try
            {
                await DisconnectAsync(dbId);
            }
            catch (Exception ex)
            {
                ReportUnexpected(ex, dbId);
            }
        }

        /// <summary>트리 메뉴 [다시 연결].</summary>
        public async void Reconnect(string dbId)
        {
            try
            {
                await ReconnectAsync(dbId);
            }
            catch (Exception ex)
            {
                ReportUnexpected(ex, dbId);
            }
        }

        public string MySchemaOf(string dbId)
        {
            var state = StateOf(dbId);
            return state == null || state.Session == null ? null : state.MySchema;
        }

        public bool? HasOracleMaintained(string dbId)
        {
            var state = StateOf(dbId);
            return state == null ? null : state.HasOracleMaintained;
        }

        public void SetHasOracleMaintained(string dbId, bool value)
        {
            var state = StateOf(dbId);
            if (state != null && state.Session != null)
                state.HasOracleMaintained = value;
        }

        public CancellationToken LoadToken(string dbId)
        {
            var state = StateOf(dbId);
            if (state == null || state.Session == null || state.Closing)
                return new CancellationToken(true);
            return state.Loads.Token;
        }

        public void TreeSelectionChanged(string dbId)
        {
            try
            {
                _selectedDbId = FindProfile(dbId) == null ? null : dbId;
                RefreshToolbar();
            }
            catch (Exception ex)
            {
                ReportUnexpected(ex, dbId);
            }
        }

        public void InsertSelect(string dbId, string owner, string objectName)
        {
            try
            {
                _workspace.InsertSelect(dbId, owner, objectName);
            }
            catch (Exception ex)
            {
                ReportUnexpected(ex, dbId);
            }
        }

        public void ReportError(string dbId, string text)
        {
            AddMessage(dbId, text, MessageKind.Error);
        }

        /// <summary>테이블 정의서 대상 선택 창. 연결 안 됐으면 연결하고, 그 DB에서 실행 중이면 알리고 열지 않는다(세션 하나를 같이 쓴다).</summary>
        public async void CreateTableSpec(TableSpecRequest request)
        {
            try
            {
                if (request == null || _closed)
                    return;
                var dbId = request.DbId;
                var profile = FindProfile(dbId);
                if (profile == null || !SupportsTableInfo(dbId, TableSpecLogic.Title))
                    return;
                var session = GetSession(dbId);
                if (session == null || session.IsBroken)
                {
                    if (!await ConnectAsync(dbId))
                        return;
                    session = GetSession(dbId);
                    if (session == null)
                        return;
                }
                // 이제 막 연결했으면 내 스키마를 여기서 안다
                if (request.MySchema == null)
                    request.MySchema = MySchemaOf(dbId);
                if (request.InitialOwner == null)
                    request.InitialOwner = request.MySchema;
                if (IsRunning(dbId))
                {
                    Dialogs.Show(OwnerWindow(), TableSpecLogic.Title, TableSpecLogic.BusyText(profile.Name), true);
                    return;
                }
                TableSpecDialog.Show(OwnerWindow(), profile, session, request, () => IsRunning(dbId),
                    () => SafeSetting(TableSpecLogic.SettingKey), value => SaveSetting(TableSpecLogic.SettingKey, value),
                    TableSpecTemplates.UserFolder(DataDirectory), HelpFolder);
            }
            catch (Exception ex)
            {
                ReportUnexpected(ex, request == null ? null : request.DbId);
            }
        }

        /// <summary>GCM 정의서 창. 연결 안 됐으면 연결하고, 그 DB에서 실행 중이면 알리고 열지 않는다(세션 하나를 같이 쓴다).</summary>
        public void CreateCodeSpec(string dbId)
        {
            CreateCodeSpec(dbId, null);
        }

        /// <summary>carrySql: 창 안에서 대상 DB를 바꿔 넘어올 때 가져온 편집기 SQL(없으면 null).</summary>
        private async void CreateCodeSpec(string dbId, string carrySql)
        {
            try
            {
                if (string.IsNullOrEmpty(dbId) || _closed)
                    return;
                var profile = FindProfile(dbId);
                if (profile == null)
                    return;
                var session = GetSession(dbId);
                if (session == null || session.IsBroken)
                {
                    if (!await ConnectAsync(dbId))
                        return;
                    session = GetSession(dbId);
                    if (session == null)
                        return;
                }
                if (IsRunning(dbId))
                {
                    Dialogs.Show(OwnerWindow(), CodeSpecLogic.Title, TableSpecLogic.BusyText(profile.Name), true);
                    return;
                }
                var next = CodeSpecDialog.Show(OwnerWindow(), profile, session, () => IsRunning(dbId),
                    () => SafeSetting(CodeSpecLogic.SettingKey), value => SaveSetting(CodeSpecLogic.SettingKey, value),
                    CodeSpecTemplates.UserFolder(DataDirectory), CodeSpecLogic.SqlFolder(DataDirectory), Profiles, carrySql, HelpFolder);
                // 콤보로 다른 접속을 골랐다: 그 DB로 다시 연다(연결이 안 돼 있으면 비밀번호를 묻고 연결)
                if (next != null && !_closed)
                    CreateCodeSpec(next.DbId, next.Sql);
            }
            catch (Exception ex)
            {
                ReportUnexpected(ex, dbId);
            }
        }

        /// <summary>
        /// 성능 모니터 창(DB마다 하나). 연결이 없으면 연결한다(비밀번호를 물을 수 있음). 창은 그 DB의 지금 세션을 그때그때 받으므로
        /// 다시 연결해도 그대로 쓴다. 공급자에 모니터 섹션이 없으면(Generic) 알리고 열지 않는다.
        /// </summary>
        public async void ShowMonitor(string dbId)
        {
            try
            {
                if (string.IsNullOrEmpty(dbId) || _closed)
                    return;
                var profile = FindProfile(dbId);
                if (profile == null)
                    return;
                var provider = DbProviders.For(profile);
                if (provider.Monitor.Sections.Count == 0)
                {
                    Dialogs.Show(OwnerWindow(), MonitorLogic.Title, MonitorLogic.NotSupportedMessage(provider.DisplayName), false);
                    return;
                }
                MonitorWindow existing;
                if (_monitors.TryGetValue(dbId, out existing))
                {
                    existing.Activate();
                    return;
                }
                var session = GetSession(dbId);
                if (session == null || session.IsBroken)
                {
                    if (!await ConnectAsync(dbId) || GetSession(dbId) == null)
                        return;
                }
                // 연결하는 동안 다른 경로로 열렸거나 창이 닫혔을 수 있다
                if (_closed || _monitors.ContainsKey(dbId))
                    return;
                var window = MonitorWindow.Show(OwnerWindow(), FindProfile(dbId) ?? profile, provider, () => GetSession(dbId), () => IsRunning(dbId), HelpFolder);
                _monitors[dbId] = window;
                window.Closed += (s, e) => _monitors.Remove(dbId);
            }
            catch (Exception ex)
            {
                ReportUnexpected(ex, dbId);
            }
        }

        // ================= 화면 =================

        private FrameworkElement BuildToolbar()
        {
            var manage = ToolbarButton("접속 관리…", "접속 추가·변경·삭제");
            manage.Click += Manage_Click;

            _selectedLabel = new TextBlock
            {
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 300,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            var prefix = new Run("DB: ");
            prefix.SetResourceReference(TextElement.ForegroundProperty, Theme.SecondaryText);
            _selectedText = new Run { FontWeight = FontWeights.SemiBold };
            _selectedText.SetResourceReference(TextElement.ForegroundProperty, Theme.PrimaryText);
            _selectedLabel.Inlines.Add(prefix);
            _selectedLabel.Inlines.Add(_selectedText);

            _connect = ShellUi.PrimaryButton("연결");
            _connect.Margin = new Thickness(0, 0, 4, 0);
            _connect.VerticalAlignment = VerticalAlignment.Center;
            _connect.ToolTip = "트리에서 고른 DB에 연결";
            _connect.Click += Connect_Click;
            _disconnect = ToolbarButton("끊기", "트리에서 고른 DB의 연결을 끊습니다");
            _disconnect.Click += Disconnect_Click;

            _modeDot = new Ellipse { Width = 7, Height = 7, Fill = Theme.Danger, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            DockPanel.SetDock(_modeDot, Dock.Left);
            _modeText = new TextBlock { FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var pillContent = new DockPanel();
            pillContent.Children.Add(_modeDot);
            pillContent.Children.Add(_modeText);
            _modePill = new Border
            {
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(9, 1, 9, 1),
                MaxWidth = 220,
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "현재 SQL 탭의 대상 DB",
                Child = pillContent
            };

            _commit = ToolbarButton("커밋", "현재 SQL 탭 대상 DB의 변경을 반영합니다(같은 DB의 모든 탭에 적용)");
            _commit.Click += Commit_Click;
            _rollback = ToolbarButton("롤백", "현재 SQL 탭 대상 DB의 변경을 버립니다(같은 DB의 모든 탭에 적용)");
            _rollback.Click += Rollback_Click;

            var fetchLabel = Theme.Text("가져올 행", Theme.SecondaryText);
            fetchLabel.VerticalAlignment = VerticalAlignment.Center;
            fetchLabel.Margin = new Thickness(0, 0, 4, 0);
            _fetchCount = new ComboBox { Width = 72, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(_fetchCount, "가져올 행");
            foreach (var count in ShellLogic.FetchCounts)
                _fetchCount.Items.Add(count);
            _fetchCount.SelectedIndex = 0;
            // 상태줄의 [다음 n행 가져오기] 글자를 바로 맞춘다(OnHostStateChanged는 예외를 내지 않는다)
            _fetchCount.SelectionChanged += (s, e) =>
            {
                if (_workspace != null && !_closed)
                    _workspace.OnHostStateChanged();
            };
            var fetch = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            fetch.Children.Add(fetchLabel);
            fetch.Children.Add(_fetchCount);

            var spacer = new Border();
            var row = new ToolbarRow { Shrink = _selectedLabel, Spacer = spacer };
            row.Children.Add(manage);
            row.Children.Add(ToolbarSeparator());
            row.Children.Add(_selectedLabel);
            row.Children.Add(_connect);
            row.Children.Add(_disconnect);
            row.Children.Add(ToolbarSeparator());
            row.Children.Add(_modePill);
            row.Children.Add(_commit);
            row.Children.Add(_rollback);
            row.Children.Add(spacer);
            row.Children.Add(fetch);

            var bar = new Border { Child = row, Padding = new Thickness(8, 5, 8, 5), BorderThickness = new Thickness(0, 0, 0, 1) };
            bar.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
            Theme.Background(bar, Theme.SurfaceBackground);
            return bar;
        }

        private static Button ToolbarButton(string text, string tooltip)
        {
            var button = new Button
            {
                Content = text,
                ToolTip = tooltip,
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetName(button, text);
            return button;
        }

        private static FrameworkElement ToolbarSeparator()
        {
            var line = new Border { Width = 1, Height = 18, Margin = new Thickness(2, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            line.SetResourceReference(Border.BackgroundProperty, Theme.Border);
            return line;
        }

        /// <summary>트리와 작업 영역 사이 분할선: 평소에는 보이지 않고 마우스를 올리면 강조색(PoC).</summary>
        private static GridSplitter BuildSplitter()
        {
            var style = new Style(typeof(GridSplitter));
            // 로컬 값은 트리거보다 우선하므로 바탕은 스타일 Setter로 둔다
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(Theme.Accent)));
            style.Triggers.Add(hover);
            var splitter = new GridSplitter
            {
                Width = 5,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                ResizeDirection = GridResizeDirection.Columns,
                ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                ShowsPreview = false,
                Focusable = false,
                Style = style
            };
            AutomationProperties.SetName(splitter, "트리 너비 조절");
            return splitter;
        }

        private void RefreshToolbar()
        {
            if (_connect == null)
                return;
            var open = !_closingFlow && !_closed;
            var selected = FindProfile(_selectedDbId);
            var selectedState = selected == null ? null : StateOf(selected.Id);
            var text = ShellLogic.SelectedDbText(selected);
            _selectedText.Text = text;
            _selectedLabel.ToolTip = "트리에서 고른 DB: " + text + (selected != null && ProfileChanged(selected.Id) ? " (" + ShellLogic.ProfileChangedNote + ")" : "");
            var session = selectedState == null ? null : selectedState.Session;
            var connecting = selectedState != null && selectedState.Connecting != null;
            var busy = selectedState != null && selectedState.Busy;
            _connect.IsEnabled = open && selected != null && !connecting && !busy && (session == null || session.IsBroken);
            _disconnect.IsEnabled = open && session != null && !connecting && !busy;

            var activeId = ActiveDbId();
            var active = FindProfile(activeId);
            var activeState = active == null ? null : StateOf(activeId);
            var activeSession = activeState == null ? null : activeState.Session;
            bool danger;
            _modeText.Text = ShellLogic.ModeText(active, activeSession == null ? null : activeSession.PendingText, out danger);
            SetPillStyle(danger);
            var canEnd = open && activeSession != null && activeSession.HasPendingChanges && !activeSession.IsBroken && !activeState.Busy && !IsRunning(activeId);
            _commit.IsEnabled = canEnd;
            _rollback.IsEnabled = canEnd;
            RefreshShellMenu();
        }

        private void RefreshShellMenu()
        {
            try
            {
                if (_shellMenu != null && !_closed)
                    _shellMenu.Refresh();
            }
            catch (Exception ex)
            {
                ReportUnexpected(ex, null);
            }
        }

        private void SetPillStyle(bool danger)
        {
            _modeDot.Visibility = danger ? Visibility.Visible : Visibility.Collapsed;
            if (danger)
            {
                _modeText.Foreground = Theme.Danger;
                _modePill.BorderBrush = Theme.Danger;
                _modePill.Background = ShellUi.DangerTint;
            }
            else
            {
                Theme.Foreground(_modeText, Theme.SecondaryText);
                _modePill.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
                _modePill.ClearValue(Border.BackgroundProperty);
            }
        }

        // ================= 툴바 동작 =================

        private void Manage_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 저장하면 ConnectionRepository.Changed로 목록을 다시 읽는다(다른 DB Helper 창도 함께)
                ConnectionManager.Show(OwnerWindow(), _manager);
            }
            catch (Exception ex)
            {
                ReportUnexpected(ex, null);
            }
        }

        private async void Connect_Click(object sender, RoutedEventArgs e)
        {
            var dbId = _selectedDbId;
            try
            {
                if (FindProfile(dbId) != null)
                    await ConnectAsync(dbId);
            }
            catch (Exception ex)
            {
                ReportUnexpected(ex, dbId);
            }
        }

        private async void Disconnect_Click(object sender, RoutedEventArgs e)
        {
            var dbId = _selectedDbId;
            try
            {
                await DisconnectAsync(dbId);
            }
            catch (Exception ex)
            {
                ReportUnexpected(ex, dbId);
            }
        }

        private async void Commit_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await EndTransactionAsync(true);
            }
            catch (Exception ex)
            {
                ReportUnexpected(ex, ActiveDbId());
            }
        }

        private async void Rollback_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await EndTransactionAsync(false);
            }
            catch (Exception ex)
            {
                ReportUnexpected(ex, ActiveDbId());
            }
        }

        private void Workspace_ActiveTabChanged(object sender, EventArgs e)
        {
            try
            {
                RefreshToolbar();
            }
            catch (Exception ex)
            {
                ReportUnexpected(ex, null);
            }
        }

        // ================= 연결 =================

        private async void RunConnect(string dbId, DbState state, TaskCompletionSource<bool> completion)
        {
            var connected = false;
            try
            {
                connected = await ConnectCoreAsync(dbId, state);
            }
            catch (Exception ex)
            {
                ReportUnexpected(ex, dbId);
            }
            finally
            {
                state.Opening = false;
                if (ReferenceEquals(state.Connecting, completion.Task))
                    state.Connecting = null;
                completion.TrySetResult(connected);
                StateChanged(dbId);
            }
        }

        private async Task<bool> ConnectCoreAsync(string dbId, DbState state)
        {
            // 연결은 언제나 저장된 값으로 한다(끊긴 세션의 사본이 아니라)
            var profile = SavedProfile(dbId);
            if (profile == null)
                return false;

            // 끊긴 세션: 버리고 다시 연결한다(그 세션의 커밋 대기 변경은 서버에서 이미 사라졌다)
            var broken = state.Session;
            if (broken != null)
            {
                var lostPending = broken.PendingText;
                try
                {
                    await _workspace.OnDisconnectingAsync(dbId);
                }
                catch (Exception ex)
                {
                    ReportUnexpected(ex, dbId);
                }
                ReleaseSession(dbId, state, broken);
                DisposeInBackground(broken);
                _tree.ResetDb(dbId);
                AddMessage(dbId, ShellLogic.ReconnectMessage(lostPending), MessageKind.Info);
                // 커서를 닫는 동안 창 닫기가 시작됐을 수 있다(닫기 흐름은 이 연결을 모른다)
                if (_closed || _closingFlow)
                    return false;
            }

            string password = null;
            string reason = null;
            try
            {
                password = ConnectionRepository.StoredPassword(profile);
            }
            catch (Exception ex)
            {
                // 다른 PC·다른 Windows 사용자가 저장한 비밀번호 등 — 이번 연결에만 쓸 비밀번호를 묻는다
                reason = ex.Message;
            }
            if (string.IsNullOrEmpty(password))
            {
                password = PasswordPrompt.Ask(OwnerWindow(), profile, reason ?? ShellLogic.NoStoredPasswordReason);
                if (string.IsNullOrEmpty(password) || _closed || _closingFlow)
                    return false;
                // 묻는 동안 접속 목록이 바뀌었을 수 있다
                profile = SavedProfile(dbId);
                if (profile == null)
                    return false;
            }

            DbSession session = null;
            try
            {
                var provider = DbProviders.For(profile);
                var connectionString = OracleConnectionStore.BuildConnectionString(profile, password, ShellLogic.ConnectTimeoutSeconds);
                password = null;
                var used = ShellLogic.SessionCopy(profile);
                state.Opening = true;
                StateChanged(dbId);
                session = DbSession.Create(provider, connectionString);
                await session.OpenAsync();
                var schemas = await session.QueryAsync(provider.Metadata.CurrentSchema(),
                    r => r.IsDBNull(0) ? null : Convert.ToString(r.GetValue(0), CultureInfo.InvariantCulture));
                var mySchema = schemas.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(mySchema))
                    mySchema = (profile.UserId ?? "").Trim().ToUpperInvariant();
                // 닫기 흐름이 시작된 뒤 붙은 세션은 그 흐름의 계획에 없다 — 남기지 않고 닫는다(finally)
                if (_closed || _closingFlow)
                    return false;
                var saved = SavedProfile(dbId);
                if (saved == null)
                {
                    AddMessage(null, ShellLogic.ProfileRemovedWhileConnecting, MessageKind.Info);
                    return false;
                }
                // 연결하는 동안 읽기 전용을 켰으면 바로 적용한다
                ShellLogic.ApplySavedToSession(used, saved);
                state.Session = session;
                state.Profile = used;
                state.MySchema = mySchema;
                state.HasOracleMaintained = null;
                state.Closing = false;
                state.Loads = new CancellationTokenSource();
                session = null; // 이제 상태가 세션을 맡는다
                state.Opening = false;
                MarkConnected(dbId);
                _tree.OnConnected(dbId, mySchema);
                AddMessage(dbId, ShellLogic.ConnectedMessage(FindProfile(dbId)), MessageKind.Success);
                return true;
            }
            catch (Exception ex)
            {
                if (!_closed)
                    AddMessage(dbId, ShellLogic.ConnectFailedPrefix + ConnectionManagerLogic.DescribeConnectError(ex), MessageKind.Error);
                return false;
            }
            finally
            {
                state.Opening = false;
                if (session != null)
                    DisposeInBackground(session);
            }
        }

        /// <summary>[끊기]: 실행 중이면 막고, 커밋 대기가 있으면 커밋·롤백을 물은 뒤 커서를 닫고 세션을 닫는다.</summary>
        private async Task DisconnectAsync(string dbId)
        {
            var profile = FindProfile(dbId);
            var state = StateOf(dbId);
            var session = state == null ? null : state.Session;
            if (profile == null || session == null || state.Busy || state.Connecting != null || _closingFlow || _closed)
                return;
            if (IsRunning(dbId))
            {
                AddMessage(dbId, ShellLogic.RunningBlocksDisconnect, MessageKind.Error);
                return;
            }
            state.Busy = true;
            RefreshToolbar();
            try
            {
                var commit = false;
                var pending = session.PendingText;
                var broken = session.IsBroken;
                if (session.HasPendingChanges && !broken)
                {
                    var choice = Dialogs.AskPending(OwnerWindow(), profile, pending, DisconnectAction);
                    if (choice == PendingChoice.Cancel || _closed || !ReferenceEquals(state.Session, session))
                        return;
                    commit = choice == PendingChoice.Commit;
                }
                try
                {
                    await _workspace.OnDisconnectingAsync(dbId);
                }
                catch (Exception ex)
                {
                    ReportUnexpected(ex, dbId);
                }
                state.Closing = true;
                ShellUi.CancelInBackground(state.Loads);
                string failure = null;
                try
                {
                    await session.CloseAsync(commit);
                }
                catch (Exception ex)
                {
                    failure = DbSession.DescribeError(ex);
                }
                ReleaseSession(dbId, state, session);
                if (_closed)
                    return;
                _tree.ResetDb(dbId);
                if (failure != null)
                    AddMessage(dbId, ShellLogic.CloseFailedMessage(commit, failure), MessageKind.Error);
                else if (broken && !string.IsNullOrEmpty(pending))
                    AddMessage(dbId, ShellLogic.BrokenDisconnectMessage(pending), MessageKind.Info);
                AddMessage(dbId, ShellLogic.Disconnected, MessageKind.Info);
                DropOrphan(dbId);
            }
            finally
            {
                FinishBusy(dbId, state);
            }
        }

        /// <summary>
        /// 다시 연결: 연결돼 있으면 [끊기]와 같이 끊고(실행 중이면 막고, 커밋 대기는 묻는다) 저장된 접속 정보로 연결한다
        /// (바뀐 접속 정보·비밀번호가 이때 적용된다). 끊긴 세션은 ConnectAsync가 버리고 다시 연결한다.
        /// 끊기를 취소했거나 끊지 못했으면 연결하지 않는다.
        /// </summary>
        private async Task ReconnectAsync(string dbId)
        {
            if (FindProfile(dbId) == null || _closed || _closingFlow)
                return;
            var state = StateOf(dbId);
            var session = state == null ? null : state.Session;
            if (session != null && !session.IsBroken)
            {
                await DisconnectAsync(dbId);
                state = StateOf(dbId);
                if (state != null && state.Session != null)
                    return;
            }
            if (_closed || _closingFlow)
                return;
            await ConnectAsync(dbId);
        }

        /// <summary>툴바 [커밋]·[롤백]: 지금 SQL 탭 대상 DB의 세션 전체(같은 DB의 모든 탭)에 적용한다.</summary>
        private async Task EndTransactionAsync(bool commit)
        {
            var dbId = ActiveDbId();
            var state = StateOf(dbId);
            var session = state == null ? null : state.Session;
            if (session == null || state.Busy || _closingFlow || _closed)
                return;
            state.Busy = true;
            RefreshToolbar();
            try
            {
                if (commit)
                    await session.CommitAsync();
                else
                    await session.RollbackAsync();
                AddMessage(dbId, commit ? ShellLogic.CommitDone : ShellLogic.RollbackDone, MessageKind.Success);
            }
            catch (SessionBusyException)
            {
                AddMessage(dbId, ShellLogic.BusyMessage(commit ? "커밋" : "롤백"), MessageKind.Error);
            }
            catch (Exception ex)
            {
                AddMessage(dbId, (commit ? "커밋하지 못했습니다: " : "롤백하지 못했습니다: ") + DbSession.DescribeError(ex), MessageKind.Error);
            }
            finally
            {
                FinishBusy(dbId, state);
            }
            // 트랜잭션이 끝나(실패했어도 서버가 끝냈으면) 닫힌 FOR UPDATE 커서를 보이던 탭을 맞춘다
            if (!_closed)
                _workspace.OnTransactionEnded(dbId);
        }

        /// <summary>끊기·커밋·롤백이 끝남. 그 사이 창이 닫혔으면 남은 세션을 정리한다.</summary>
        private void FinishBusy(string dbId, DbState state)
        {
            state.Busy = false;
            if (_closed)
            {
                var session = state.Session;
                if (session != null)
                {
                    ReleaseSession(dbId, state, session);
                    DisposeInBackground(session);
                }
                return;
            }
            StateChanged(dbId);
        }

        /// <summary>세션을 이 창의 상태에서 떼어 낸다(트리 조회 취소, 연결 수 세기). 이미 다른 세션이면 아무것도 안 함.</summary>
        private void ReleaseSession(string dbId, DbState state, DbSession session)
        {
            if (state == null || session == null || !ReferenceEquals(state.Session, session))
                return;
            state.Session = null;
            state.Profile = null;
            state.MySchema = null;
            state.HasOracleMaintained = null;
            state.Closing = false;
            var loads = state.Loads;
            state.Loads = new CancellationTokenSource();
            ShellUi.CancelInBackground(loads);
            MarkDisconnected(dbId);
        }

        // ================= 접속 목록 =================

        private void LoadProfiles()
        {
            try
            {
                _profiles = ShellLogic.MergeProfiles(ConnectionRepository.Load(_manager), null, null, null);
                _profilesError = null;
            }
            catch (Exception ex)
            {
                _profiles = new List<OracleConnectionProfile>();
                _profilesError = ex.Message;
            }
        }

        private void Repository_Changed(object sender, EventArgs e)
        {
            try
            {
                if (_closed)
                    return;
                if (!Dispatcher.CheckAccess())
                {
                    Dispatcher.BeginInvoke(new Action(() => Repository_Changed(sender, e)));
                    return;
                }
                ReloadProfiles();
            }
            catch (Exception ex)
            {
                ReportUnexpected(ex, null);
            }
        }

        /// <summary>
        /// 저장된 접속 목록을 다시 읽는다. 세션은 그대로 둔다(바뀐 접속 정보는 다시 연결할 때 적용).
        /// 연결 중인 접속은 접속 관리에서 지울 수 없지만, 그래도 빠졌으면 끊을 수 있게 목록에 남긴다.
        /// </summary>
        private void ReloadProfiles()
        {
            List<OracleConnectionProfile> loaded;
            try
            {
                loaded = ConnectionRepository.Load(_manager);
            }
            catch (Exception ex)
            {
                AddMessage(null, "접속 목록을 다시 읽지 못했습니다: " + ex.Message, MessageKind.Error);
                return;
            }
            _profiles = ShellLogic.MergeProfiles(loaded, _profiles, HasLiveSession, _orphans);
            _profilesError = null;
            foreach (var pair in _dbs)
            {
                if (pair.Value.Session != null)
                    ShellLogic.ApplySavedToSession(pair.Value.Profile, SavedProfile(pair.Key));
            }
            foreach (var id in _dbs.Keys.Where(k => SavedProfile(k) == null).ToList())
            {
                var state = _dbs[id];
                if (state.Session == null && state.Connecting == null && !state.Busy)
                    _dbs.Remove(id);
            }
            AfterProfilesChanged();
        }

        /// <summary>목록에서 빠졌지만 연결이 살아 있어 남겨 둔 접속을, 끊은 뒤 목록에서 뺀다.</summary>
        private void DropOrphan(string dbId)
        {
            if (!_orphans.Remove(dbId))
                return;
            _profiles = _profiles.Where(p => p.Id != dbId).ToList();
            _dbs.Remove(dbId);
            AfterProfilesChanged();
        }

        private void AfterProfilesChanged()
        {
            if (FindProfile(_selectedDbId) == null)
                _selectedDbId = _profiles.Select(p => p.Id).FirstOrDefault();
            _tree.OnProfilesChanged();
            try
            {
                _workspace.OnProfilesChanged();
            }
            catch (Exception ex)
            {
                ReportUnexpected(ex, null);
            }
            RefreshToolbar();
        }

        // ================= 창 수명 =================

        private void View_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_closed)
                    return;
                if (_window == null)
                {
                    var window = Window.GetWindow(this);
                    if (window != null)
                    {
                        _window = window;
                        window.Closing += Window_Closing;
                        window.Closed += Window_Closed;
                    }
                }
                if (!_staticSubscribed)
                {
                    _staticSubscribed = true;
                    ConnectionRepository.Changed += Repository_Changed;
                    var app = Application.Current;
                    if (app != null)
                        app.Exit += App_Exit;
                }
                if (!_focusedOnce)
                {
                    _focusedOnce = true;
                    _workspace.FocusEditor();
                }
            }
            catch (Exception ex)
            {
                ReportUnexpected(ex, null);
            }
        }

        private void View_Unloaded(object sender, RoutedEventArgs e)
        {
            // 창에 붙어 있으면 창의 Closed에서 정리한다. 창 없이 떨어져 나간 경우만 여기서 정리한다.
            if (_window == null)
                Cleanup();
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            try
            {
                if (_closeConfirmed || _closed || _closeFlowFailed)
                    return;
                if (_closingFlow)
                {
                    e.Cancel = true;
                    return;
                }
                if (!_dbs.Values.Any(s => s.Session != null || s.Busy))
                    return; // 연결이 없으면 바로 닫는다
                e.Cancel = true;
                _closingFlow = true;
                IsEnabled = false;
                RefreshToolbar();
                // Closing 안에서는 Close()를 다시 부를 수 없다 — 이벤트가 끝난 뒤 닫기 흐름(묻기·세션 닫기)을 돈다
                Dispatcher.BeginInvoke(new Action(RunCloseFlow));
            }
            catch (Exception)
            {
                // 닫기 확인을 하지 못하면 그대로 닫는다(Closed에서 세션을 정리하고, 커밋하지 않은 변경은 롤백된다)
                e.Cancel = false;
                _closeFlowFailed = true;
            }
        }

        /// <summary>
        /// 창 닫기: 실행을 취소하고, 커밋 대기가 있는 DB마다 커밋·롤백을 묻는다(하나라도 [돌아가기]면 아무것도 하지 않고 닫기를 그만둔다).
        /// 그다음 세션을 모두 닫고 창을 닫는다. 커밋을 고른 세션은 끝까지 기다리고, 커밋이 실패하면 알린 뒤 닫는다.
        /// </summary>
        private async void RunCloseFlow()
        {
            try
            {
                if (_closed)
                    return;
                try
                {
                    _workspace.CancelAll();
                }
                catch (Exception ex)
                {
                    ReportUnexpected(ex, null);
                }
                // 취소한 실행이 끝나야 커밋 대기 여부가 정확하다(취소 직전에 끝난 DML 등). 끊기·커밋 중인 DB도 끝나기를 기다린다.
                await WaitUntil(() => !AnyWorkspaceRunning() && !_dbs.Values.Any(s => s.Busy), RunningStopWait);
                if (_closed)
                    return;

                var plan = new List<ClosePlan>();
                foreach (var dbId in StateIdsInProfileOrder())
                {
                    var state = _dbs[dbId];
                    var session = state.Session;
                    if (session == null || state.Busy)
                        continue;
                    var commit = false;
                    var profile = FindProfile(dbId);
                    // 세션이 살아 있는 접속은 목록에 남겨 두므로 profile이 없는 경우는 없다(있다면 묻지 않고 롤백)
                    if (profile != null && session.HasPendingChanges && !session.IsBroken)
                    {
                        var choice = Dialogs.AskPending(OwnerWindow(), profile, session.PendingText, CloseAction);
                        if (_closed)
                            return;
                        if (choice == PendingChoice.Cancel)
                        {
                            AbortClose();
                            return;
                        }
                        commit = choice == PendingChoice.Commit;
                    }
                    plan.Add(new ClosePlan { DbId = dbId, State = state, Session = session, Commit = commit });
                }

                var commits = new List<Task<string>>();
                var rollbacks = new List<Task<string>>();
                foreach (var item in plan)
                    (item.Commit ? commits : rollbacks).Add(CloseForWindowAsync(item));
                var commitErrors = await Task.WhenAll(commits);
                // 롤백으로 닫는 세션은 오래 걸리면(네트워크 끊김 등) 기다리지 않는다 — 서버가 변경을 버린다
                await Task.WhenAny(Task.WhenAll(rollbacks), Task.Delay(RollbackCloseWait));
                if (_closed)
                    return;

                var failures = new List<string>();
                var committed = plan.Where(p => p.Commit).ToList();
                for (var i = 0; i < committed.Count; i++)
                {
                    if (commitErrors[i] != null)
                        failures.Add(DisplayName(committed[i].DbId) + ": " + commitErrors[i]);
                }
                if (failures.Count > 0)
                {
                    Dialogs.Show(OwnerWindow(), "커밋하지 못했습니다",
                        "다음 DB의 변경은 저장되지 않았습니다(연결은 닫았습니다).\n" + string.Join("\n", failures), true);
                    if (_closed)
                        return;
                }
                _closeConfirmed = true;
                var window = _window;
                if (window != null)
                    window.Close();
                // 다른 처리기가 닫기를 막았으면(Closed가 오지 않음) 화면을 다시 쓸 수 있게 돌려놓는다 — 세션은 이미 닫혔다
                if (!_closed)
                {
                    _closeConfirmed = false;
                    AbortClose();
                }
            }
            catch (Exception ex)
            {
                _closeFlowFailed = true;
                try
                {
                    AbortClose();
                }
                catch (Exception)
                {
                    // 화면을 되돌리지 못해도 다음 닫기는 묻지 않고 닫힌다(_closeFlowFailed)
                }
                AddMessage(null, ShellLogic.CloseFlowFailedMessage(DbSession.DescribeError(ex)), MessageKind.Error);
            }
        }

        /// <summary>창을 닫으며 세션 하나를 닫는다. 실패 문장(없으면 null)을 돌려주고 예외를 내지 않는다.</summary>
        private async Task<string> CloseForWindowAsync(ClosePlan item)
        {
            var state = item.State;
            state.Busy = true;
            state.Closing = true;
            ShellUi.CancelInBackground(state.Loads);
            try
            {
                await item.Session.CloseAsync(item.Commit);
                return null;
            }
            catch (Exception ex)
            {
                return DbSession.DescribeError(ex);
            }
            finally
            {
                state.Busy = false;
                ReleaseSession(item.DbId, state, item.Session);
            }
        }

        private void AbortClose()
        {
            _closingFlow = false;
            if (_closed)
                return;
            IsEnabled = true;
            // 닫기 흐름이 이미 닫은 세션이 있을 수 있다 — 툴바·트리·작업 영역을 모두 맞춘다
            StateChanged(null);
            try
            {
                _workspace.FocusEditor();
            }
            catch (Exception ex)
            {
                ReportUnexpected(ex, null);
            }
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            Cleanup();
        }

        /// <summary>창이 닫힘(또는 창 없이 떨어져 나감): 정적 이벤트 구독을 풀고 남은 세션을 정리한다(묻지 않음 — 롤백).</summary>
        private void Cleanup()
        {
            if (_closed)
                return;
            _closed = true;
            try
            {
                if (_window != null)
                {
                    _window.Closing -= Window_Closing;
                    _window.Closed -= Window_Closed;
                }
                UnsubscribeStatic();
                _tree.Shutdown();
            }
            catch (Exception)
            {
                // 창이 닫히는 중이라 알릴 곳이 없다 — 세션 정리는 계속한다
            }
            try
            {
                _workspace.CancelAll();
            }
            catch (Exception)
            {
                // 위와 같음
            }
            foreach (var pair in _dbs.ToList())
            {
                try
                {
                    var state = pair.Value;
                    var session = state.Session;
                    // 끊기·커밋 중인 세션은 그 작업이 끝나면 정리한다(커밋 도중에 연결을 닫지 않게)
                    if (session == null || state.Busy)
                        continue;
                    ReleaseSession(pair.Key, state, session);
                    DisposeInBackground(session);
                }
                catch (Exception)
                {
                    // 창이 닫히는 중 — 나머지 세션 정리는 계속한다
                }
            }
        }

        private void App_Exit(object sender, ExitEventArgs e)
        {
            // 앱 종료: 물을 수 없으므로 남은 세션을 바로 정리한다(커밋하지 않은 변경은 롤백된다).
            // 끊기·커밋 중인 세션도 정리한다 — Dispose는 진행 중인 작업을 최대 2초 기다리므로 그 사이 커밋이 끝날 수 있다. 예외를 내지 않는다.
            foreach (var pair in _dbs.ToList())
            {
                try
                {
                    var state = pair.Value;
                    var session = state.Session;
                    if (session == null)
                        continue;
                    ReleaseSession(pair.Key, state, session);
                    session.Dispose();
                }
                catch (Exception)
                {
                    // 종료 중 — 프로세스가 끝나면 서버가 남은 세션을 정리한다
                }
            }
            try
            {
                UnsubscribeStatic();
            }
            catch (Exception)
            {
                // 위와 같음
            }
        }

        private void UnsubscribeStatic()
        {
            if (!_staticSubscribed)
                return;
            _staticSubscribed = false;
            ConnectionRepository.Changed -= Repository_Changed;
            var app = Application.Current;
            if (app != null && app.Dispatcher.CheckAccess())
                app.Exit -= App_Exit;
        }

        // ================= 도우미 =================

        private DbState StateOf(string dbId)
        {
            DbState state;
            return dbId != null && _dbs.TryGetValue(dbId, out state) ? state : null;
        }

        /// <summary>저장된 접속 정보(목록에서 빠졌지만 연결이 살아 있는 접속은 이전 값). 없으면 null.</summary>
        private OracleConnectionProfile SavedProfile(string dbId)
        {
            if (string.IsNullOrEmpty(dbId))
                return null;
            foreach (var profile in _profiles)
            {
                if (profile.Id == dbId)
                    return profile;
            }
            return null;
        }

        private OracleConnectionProfile SessionOrSaved(OracleConnectionProfile saved)
        {
            if (saved == null)
                return null;
            var state = StateOf(saved.Id);
            return state != null && state.Session != null && state.Profile != null ? state.Profile : saved;
        }

        /// <summary>닫는 중이어도 이 창이 세션을 쥐고 있으면 true(접속 목록에서 빼지 않는다).</summary>
        private bool HasLiveSession(string dbId)
        {
            var state = StateOf(dbId);
            return state != null && state.Session != null;
        }

        private DbState EnsureState(string dbId)
        {
            DbState state;
            if (!_dbs.TryGetValue(dbId, out state))
            {
                state = new DbState();
                _dbs.Add(dbId, state);
            }
            return state;
        }

        /// <summary>세션이 있는 DB를 접속 목록 순서로(목록에 없는 것은 뒤에).</summary>
        private List<string> StateIdsInProfileOrder()
        {
            var ids = _profiles.Select(p => p.Id).Where(id => _dbs.ContainsKey(id)).ToList();
            ids.AddRange(_dbs.Keys.Where(id => !ids.Contains(id)));
            return ids;
        }

        private string ActiveDbId()
        {
            try
            {
                return _workspace == null ? null : _workspace.ActiveDbId;
            }
            catch (Exception)
            {
                // 표시용 질문이다 — 답하지 못하면 대상 없음으로 본다
                return null;
            }
        }

        private bool AnyWorkspaceRunning()
        {
            try
            {
                return _workspace != null && _workspace.AnyRunning;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private string DisplayName(string dbId)
        {
            var profile = FindProfile(dbId);
            return profile == null ? "(삭제된 접속)" : profile.Name;
        }

        private Window OwnerWindow()
        {
            return _window ?? Window.GetWindow(this);
        }

        private void AddMessage(string dbId, string text, MessageKind kind)
        {
            try
            {
                if (_workspace != null && !_closed)
                    _workspace.AddMessage(FindProfile(dbId) == null ? null : dbId, text, kind);
            }
            catch (Exception)
            {
                // 메시지를 남기지 못해도 Folderss를 멈추지 않는 것이 우선이다
            }
        }

        private void ReportUnexpected(Exception ex, string dbId)
        {
            AddMessage(dbId, "예상하지 못한 오류: " + DbSession.DescribeError(ex), MessageKind.Error);
        }

        private void MarkConnected(string dbId)
        {
            try
            {
                ConnectionRepository.MarkConnected(dbId);
            }
            catch (Exception ex)
            {
                ReportUnexpected(ex, dbId);
            }
        }

        private void MarkDisconnected(string dbId)
        {
            try
            {
                ConnectionRepository.MarkDisconnected(dbId);
            }
            catch (Exception ex)
            {
                ReportUnexpected(ex, dbId);
            }
        }

        private static void DisposeInBackground(DbSession session)
        {
            // Dispose는 실행 중인 작업을 최대 2초 기다린다 — UI 스레드를 막지 않게 스레드 풀에서
            Task.Run(() =>
            {
                try
                {
                    session.Dispose();
                }
                catch (Exception)
                {
                    // Dispose는 예외를 내지 않게 만들어져 있다 — 그래도 스레드 풀 예외로 Folderss가 끝나지 않게 막는다
                }
            });
        }

        private static async Task WaitUntil(Func<bool> done, TimeSpan limit)
        {
            var watch = Stopwatch.StartNew();
            while (!done() && watch.Elapsed < limit)
                await Task.Delay(50);
        }

        /// <summary>DB 하나의 연결 상태.</summary>
        private sealed class DbState
        {
            public DbSession Session;
            /// <summary>연결할 때 쓴 접속 정보의 사본(세션이 있는 동안 표시·확인에 쓴다).</summary>
            public OracleConnectionProfile Profile;
            /// <summary>접속 사용자의 스키마(대문자).</summary>
            public string MySchema;
            /// <summary>ALL_USERS.ORACLE_MAINTAINED가 있는지(null = 모름). 세션마다 새로 알아낸다.</summary>
            public bool? HasOracleMaintained;
            /// <summary>진행 중인 연결(같은 DB를 동시에 두 번 연결하지 않게 같은 작업을 돌려준다).</summary>
            public Task<bool> Connecting;
            /// <summary>비밀번호를 받은 뒤 실제로 연결하는 중(트리의 "연결 중…").</summary>
            public bool Opening;
            /// <summary>끊기·커밋·롤백·닫기 중(툴바 버튼을 막는다).</summary>
            public bool Busy;
            /// <summary>세션을 닫는 중 — 새 트리 조회를 시작하지 않는다.</summary>
            public bool Closing;
            /// <summary>이 세션의 트리 조회 취소(끊거나 닫을 때 취소하고 새것으로 바꾼다).</summary>
            public CancellationTokenSource Loads = new CancellationTokenSource();
        }

        private sealed class ClosePlan
        {
            public string DbId;
            public DbState State;
            public DbSession Session;
            public bool Commit;
        }

        /// <summary>
        /// 툴바 한 줄: 자식을 왼쪽부터 놓는다. 자리가 모자라면 Shrink(고른 DB 표시)만 줄이고(말줄임), Spacer 뒤의 자식은 오른쪽 끝에 붙인다.
        /// </summary>
        private sealed class ToolbarRow : Panel
        {
            public UIElement Shrink { get; set; }

            public UIElement Spacer { get; set; }

            protected override Size MeasureOverride(Size availableSize)
            {
                var unbounded = new Size(double.PositiveInfinity, availableSize.Height);
                double used = 0;
                double height = 0;
                foreach (UIElement child in InternalChildren)
                {
                    if (child == null || ReferenceEquals(child, Shrink) || ReferenceEquals(child, Spacer))
                        continue;
                    child.Measure(unbounded);
                    used += child.DesiredSize.Width;
                    height = Math.Max(height, child.DesiredSize.Height);
                }
                if (Shrink != null)
                {
                    var room = double.IsPositiveInfinity(availableSize.Width) ? double.PositiveInfinity : Math.Max(0, availableSize.Width - used);
                    Shrink.Measure(new Size(room, availableSize.Height));
                    used += Shrink.DesiredSize.Width;
                    height = Math.Max(height, Shrink.DesiredSize.Height);
                }
                if (Spacer != null)
                    Spacer.Measure(new Size(0, availableSize.Height));
                return new Size(double.IsPositiveInfinity(availableSize.Width) ? used : Math.Min(used, availableSize.Width), height);
            }

            protected override Size ArrangeOverride(Size finalSize)
            {
                double used = 0;
                foreach (UIElement child in InternalChildren)
                {
                    if (child != null && !ReferenceEquals(child, Spacer))
                        used += child.DesiredSize.Width;
                }
                var extra = Math.Max(0, finalSize.Width - used);
                double x = 0;
                foreach (UIElement child in InternalChildren)
                {
                    if (child == null)
                        continue;
                    var width = ReferenceEquals(child, Spacer) ? extra : child.DesiredSize.Width;
                    child.Arrange(new Rect(x, 0, width, finalSize.Height));
                    x += width;
                }
                return finalSize;
            }
        }
    }
}

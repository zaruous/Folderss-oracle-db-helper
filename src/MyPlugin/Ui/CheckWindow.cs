using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace MyPlugin
{
    /// <summary>
    /// 일일 점검 창(모달 아님, DB Helper 창마다 하나). 고른 DB마다 전용 세션을 열어 점검 묶음(<see cref="IMonitorProvider.CheckSet"/>)을 차례로 돌리고,
    /// 항목마다 상태 칩·값·직전 대비를 보인다. 항목을 고르면 아래에 판정 기준·조치·발견 행(최대 100행).
    /// - 동시에 <see cref="OverviewLogic.MaxConcurrent"/>개 DB. 저장된 비밀번호가 없는 DB는 한 번에 하나씩 묻는다.
    /// - 기본 항목 + 사용자 항목·켬끔·임계값은 history.db(<see cref="MonitorStore"/>)에서. 결과(상태·값·행 수)는 설정이 켜져 있으면 기록하고, 보관 기간이 지난 것은 지운다.
    ///   history.db를 열 수 없으면 기본 항목만 돌리고 기록하지 않는다(창 아래에 이유).
    /// - [엑셀로 저장…]은 기본 양식 또는 내 양식(.xlsx)으로 저장만 한다(저장 뒤 [열기]로 연다).
    /// - 판정·비교·요약은 <see cref="CheckLogic"/>(DBMS 중립), 실행은 <see cref="CheckRunner"/>.
    /// </summary>
    internal sealed class CheckWindow
    {
        /// <summary>창이 DB Helper에서 받는 것.</summary>
        internal sealed class Host
        {
            public Func<IReadOnlyList<OracleConnectionProfile>> Profiles { get; set; }
            public Func<OracleConnectionProfile, bool> HasStoredPassword { get; set; }
            /// <summary>접속 Id → 세션(저장된 비밀번호가 없으면 묻는다, 취소하면 null). 연결 오류는 예외.</summary>
            public Func<string, Task<DbSession>> Connect { get; set; }
            public Func<string, string> GetSetting { get; set; }
            public Action<string, string> SetSetting { get; set; }
            public string DataDirectory { get; set; }
            public string PluginDirectory { get; set; }
            public string HelpFolder { get; set; }
        }

        private readonly Window _window;
        private readonly Host _host;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(OverviewLogic.MaxConcurrent);
        private readonly SemaphoreSlim _promptGate = new SemaphoreSlim(1);
        private readonly List<DbRun> _runs = new List<DbRun>();
        private readonly Dictionary<string, CheckBox> _dbChecks = new Dictionary<string, CheckBox>(StringComparer.Ordinal);
        private readonly string _appVersion;
        private MonitorStore _store;
        private string _storeError;
        private CancellationTokenSource _work;
        private bool _closed;

        private StackPanel _dbList;
        private StackPanel _results;
        private TextBlock _summary;
        private TextBlock _empty;
        private TextBlock _storeText;
        private TextBlock _saveText;
        private Button _run;
        private Button _cancel;
        private Button _save;
        private Button _openSaved;
        private string _lastOutput;

        // 상세
        private string _selectedKey;
        private TextBlock _detailEmpty;
        private FrameworkElement _detailBody;
        private TextBlock _detailTitle;
        private Border _detailChip;
        private TextBlock _detailChipText;
        private TextBlock _detailFacts;
        private TextBlock _detailNote;
        private TextBox _detailSql;
        private TextBlock _findingsText;
        private ResultGridView _findings;
        private List<ResultColumn> _findingColumns = new List<ResultColumn>();

        private CheckWindow(Window owner, Host host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _appVersion = CheckLogic.PluginVersion(host.PluginDirectory);
            _window = new Window
            {
                Title = CheckLogic.Title,
                Width = 1100,
                Height = 720,
                MinWidth = 640,
                MinHeight = 420,
                ShowInTaskbar = false,
                ResizeMode = ResizeMode.CanResizeWithGrip
            };
            Theme.ApplyWindow(_window);
            DialogKit.SetOwner(_window, owner);
            OpenStore();
            _window.Content = Build();
            _window.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape && _work == null)
                {
                    e.Handled = true;
                    _window.Close();
                }
                else if (e.Key == Key.F5)
                {
                    e.Handled = true;
                    Run();
                }
            };
            _window.Closed += (s, e) =>
            {
                _closed = true;
                if (_work != null)
                    _work.Cancel();
            };
            FillDbList();
            ShowSummary();
            ShowStoreStatus();
            ShowDetail(null);
        }

        public static CheckWindow Show(Window owner, Host host)
        {
            var window = new CheckWindow(owner, host);
            window._window.Show();
            return window;
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

        // ================= 저장소 =================

        private void OpenStore()
        {
            try
            {
                if (string.IsNullOrEmpty(_host.DataDirectory))
                    throw new InvalidOperationException("플러그인 데이터 폴더를 알 수 없습니다.");
                _store = MonitorStore.Open(_host.DataDirectory);
                _storeError = null;
            }
            catch (Exception ex)
            {
                _store = null;
                _storeError = ex.Message;
            }
        }

        private bool Recording
        {
            get { return CheckLogic.IsRecording(SafeGet(CheckLogic.RecordSettingKey)); }
        }

        private void ShowStoreStatus()
        {
            if (_store == null)
            {
                _storeText.Text = "이력 파일을 열 수 없어 사용자 항목·직전 대비 없이 기본 항목만 돌리고 기록하지 않습니다: " + _storeError;
                _storeText.Foreground = Theme.Warning;
                return;
            }
            bool valid;
            var days = CheckLogic.RetentionDays(SafeGet(CheckLogic.RetentionSettingKey), out valid);
            _storeText.Text = _store.ReadOnly
                ? "이력 파일이 더 새 버전 DB Helper의 것이라 읽기만 합니다(기록 안 함): " + _store.FilePath
                : Recording ? "결과 기록 켬 · " + days.ToString(CultureInfo.InvariantCulture) + "일 보관 · " + _store.FilePath
                : "결과 기록 꺼짐(설정 > 성능 모니터) · " + _store.FilePath;
            if (_store.ReadOnly)
                _storeText.Foreground = Theme.Warning;
            else
                _storeText.SetResourceReference(TextBlock.ForegroundProperty, Theme.SecondaryText);
            _storeText.ToolTip = _store.FilePath;
        }

        // ================= 화면 =================

        private FrameworkElement Build()
        {
            var root = new DockPanel { Margin = new Thickness(14, 12, 14, 10) };
            var header = Header();
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var foot = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            _openSaved = new Button { Content = "열기", Padding = new Thickness(10, 1, 10, 1), Margin = new Thickness(6, 0, 0, 0), Visibility = Visibility.Collapsed };
            _openSaved.Click += (s, e) => OpenPath(_lastOutput);
            DockPanel.SetDock(_openSaved, Dock.Right);
            foot.Children.Add(_openSaved);
            _saveText = Theme.Secondary("");
            _saveText.TextTrimming = TextTrimming.CharacterEllipsis;
            _saveText.VerticalAlignment = VerticalAlignment.Center;
            _saveText.MaxWidth = 520;
            DockPanel.SetDock(_saveText, Dock.Right);
            foot.Children.Add(_saveText);
            _storeText = Theme.Secondary("");
            _storeText.TextTrimming = TextTrimming.CharacterEllipsis;
            _storeText.VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetAutomationId(_storeText, "check-store");
            foot.Children.Add(_storeText);
            DockPanel.SetDock(foot, Dock.Bottom);
            root.Children.Add(foot);

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var targets = TargetsPanel();
            Grid.SetColumn(targets, 0);
            body.Children.Add(targets);

            var right = new Grid();
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star), MinHeight = 120 });
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star), MinHeight = 120 });
            var list = ResultsPanel();
            Grid.SetRow(list, 0);
            right.Children.Add(list);
            var splitter = new GridSplitter { Height = 6, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent, ResizeDirection = GridResizeDirection.Rows };
            Grid.SetRow(splitter, 1);
            right.Children.Add(splitter);
            var detail = DetailPanel();
            Grid.SetRow(detail, 2);
            right.Children.Add(detail);
            Grid.SetColumn(right, 2);
            body.Children.Add(right);
            root.Children.Add(body);
            return root;
        }

        private FrameworkElement Header()
        {
            var panel = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _run = DialogKit.PrimaryButton("점검 실행");
            _run.Padding = new Thickness(12, 3, 12, 3);
            _run.Margin = new Thickness(0, 0, 6, 0);
            _run.ToolTip = "고른 DB를 점검합니다 (F5)";
            _run.Click += (s, e) => Run();
            buttons.Children.Add(_run);
            _cancel = new Button { Content = "취소", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 6, 0), IsEnabled = false };
            _cancel.Click += (s, e) =>
            {
                if (_work != null)
                    _work.Cancel();
            };
            buttons.Children.Add(_cancel);
            _save = new Button { Content = "엑셀로 저장…", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 0, 0), IsEnabled = false };
            _save.Click += (s, e) => SaveReport();
            buttons.Children.Add(_save);
            var template = new Button { Content = "양식 ▾", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(2, 0, 6, 0), ToolTip = "보고서 양식(기본 양식 · 내 양식)" };
            template.ContextMenu = TemplateMenu();
            template.Click += (s, e) =>
            {
                template.ContextMenu.PlacementTarget = template;
                template.ContextMenu.IsOpen = true;
            };
            AutomationProperties.SetName(template, "양식");
            buttons.Children.Add(template);
            var items = new Button { Content = "항목 관리…", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 6, 0), ToolTip = "기본 항목 켬·끔과 임계값, 사용자 점검 항목 추가·고치기·내보내기" };
            items.Click += (s, e) => ManageItems();
            buttons.Children.Add(items);
            var help = new Button { Content = "도움말", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 6, 0) };
            help.Click += (s, e) => HelpWindow.Show(_window, _host.HelpFolder, HelpLogic.Monitor);
            buttons.Children.Add(help);
            var close = new Button { Content = "닫기", Padding = new Thickness(16, 3, 16, 3) };
            close.Click += (s, e) => _window.Close();
            buttons.Children.Add(close);
            DockPanel.SetDock(buttons, Dock.Right);
            panel.Children.Add(buttons);

            var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var name = Theme.Text(CheckLogic.Title);
            name.FontSize = 16;
            name.FontWeight = FontWeights.SemiBold;
            name.Margin = new Thickness(0, 0, 10, 0);
            title.Children.Add(name);
            _summary = Theme.Text("", Theme.SecondaryText);
            _summary.VerticalAlignment = VerticalAlignment.Center;
            _summary.TextTrimming = TextTrimming.CharacterEllipsis;
            AutomationProperties.SetAutomationId(_summary, "check-summary");
            title.Children.Add(_summary);
            panel.Children.Add(title);
            return panel;
        }

        private FrameworkElement TargetsPanel()
        {
            var panel = new DockPanel();
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var none = LinkButton("없음");
            none.Click += (s, e) => SetAllDbs(false);
            DockPanel.SetDock(none, Dock.Right);
            head.Children.Add(none);
            var all = LinkButton("모두");
            all.Click += (s, e) => SetAllDbs(true);
            DockPanel.SetDock(all, Dock.Right);
            head.Children.Add(all);
            var label = Theme.Text("대상 DB", Theme.SecondaryText);
            label.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(label);
            DockPanel.SetDock(head, Dock.Top);
            panel.Children.Add(head);
            var note = Theme.Secondary("지금은 Oracle만 점검합니다. 계정에 SELECT_CATALOG_ROLE(또는 V$·DBA_ 뷰 조회 권한)이 있어야 대부분의 항목이 돕니다.");
            note.TextWrapping = TextWrapping.Wrap;
            note.Margin = new Thickness(0, 8, 0, 0);
            DockPanel.SetDock(note, Dock.Bottom);
            panel.Children.Add(note);
            _dbList = new StackPanel();
            AutomationProperties.SetName(_dbList, "대상 DB");
            var frame = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 6, 8, 6),
                Child = new ScrollViewer { Content = _dbList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false }
            };
            frame.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
            Theme.Background(frame, Theme.PanelBackground);
            panel.Children.Add(frame);
            return panel;
        }

        private static Button LinkButton(string text)
        {
            var button = new Button { Content = text, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(4, 0, 0, 0), FontSize = 11.5 };
            return button;
        }

        private FrameworkElement ResultsPanel()
        {
            var host = new Grid();
            _results = new StackPanel();
            AutomationProperties.SetName(_results, "점검 결과");
            var frame = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Child = new ScrollViewer { Content = _results, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false }
            };
            frame.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
            Theme.Background(frame, Theme.PanelBackground);
            host.Children.Add(frame);
            _empty = Theme.Text("왼쪽에서 대상 DB를 고르고 [점검 실행]을 누르세요.", Theme.SecondaryText);
            _empty.HorizontalAlignment = HorizontalAlignment.Center;
            _empty.VerticalAlignment = VerticalAlignment.Center;
            host.Children.Add(_empty);
            return host;
        }

        private FrameworkElement DetailPanel()
        {
            var host = new Grid();
            var frame = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 8, 10, 8) };
            frame.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
            Theme.Background(frame, Theme.PanelBackground);

            var body = new DockPanel();
            var head = new DockPanel();
            _detailChip = new Border { CornerRadius = new CornerRadius(3), Padding = new Thickness(6, 0, 6, 1), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            _detailChipText = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold };
            _detailChip.Child = _detailChipText;
            DockPanel.SetDock(_detailChip, Dock.Left);
            head.Children.Add(_detailChip);
            var copySql = new Button { Content = "SQL 복사", Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(6, 0, 0, 0) };
            copySql.Click += (s, e) => DialogKit.TryCopy(_detailSql.Text);
            DockPanel.SetDock(copySql, Dock.Right);
            head.Children.Add(copySql);
            var showSql = new Button { Content = "SQL 보기", Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(6, 0, 0, 0) };
            showSql.Click += (s, e) => _detailSql.Visibility = _detailSql.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            DockPanel.SetDock(showSql, Dock.Right);
            head.Children.Add(showSql);
            _detailTitle = Theme.Text("");
            _detailTitle.FontWeight = FontWeights.SemiBold;
            _detailTitle.TextTrimming = TextTrimming.CharacterEllipsis;
            _detailTitle.VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetAutomationId(_detailTitle, "check-detail-title");
            head.Children.Add(_detailTitle);
            DockPanel.SetDock(head, Dock.Top);
            body.Children.Add(head);

            _detailFacts = Theme.Text("", Theme.SecondaryText);
            _detailFacts.TextWrapping = TextWrapping.Wrap;
            _detailFacts.Margin = new Thickness(0, 6, 0, 0);
            AutomationProperties.SetAutomationId(_detailFacts, "check-detail-facts");
            DockPanel.SetDock(_detailFacts, Dock.Top);
            body.Children.Add(_detailFacts);
            _detailNote = Theme.Text("");
            _detailNote.TextWrapping = TextWrapping.Wrap;
            _detailNote.Margin = new Thickness(0, 4, 0, 0);
            AutomationProperties.SetAutomationId(_detailNote, "check-detail-note");
            DockPanel.SetDock(_detailNote, Dock.Top);
            body.Children.Add(_detailNote);
            _detailSql = DialogKit.SqlBox("", 6);
            _detailSql.MaxHeight = 120;
            _detailSql.Visibility = Visibility.Collapsed;
            AutomationProperties.SetName(_detailSql, "점검 SQL");
            DockPanel.SetDock(_detailSql, Dock.Top);
            body.Children.Add(_detailSql);
            _findingsText = Theme.Secondary("");
            _findingsText.Margin = new Thickness(0, 6, 0, 2);
            AutomationProperties.SetAutomationId(_findingsText, "check-findings-text");
            DockPanel.SetDock(_findingsText, Dock.Top);
            body.Children.Add(_findingsText);
            ResultGridView grid = null;
            grid = new ResultGridView(all =>
            {
                var rows = all ? grid.RowsInViewOrder() : grid.SelectedRows();
                DialogKit.TryCopy(WorkspaceLogic.ToTsv(all ? _findingColumns.Select(c => c.Name) : null, rows.Select(r => r.Values)));
            }, text => DialogKit.TryCopy(text));
            _findings = grid;
            AutomationProperties.SetName(_findings.View, "발견 행");
            body.Children.Add(_findings.View);
            _detailBody = body;
            frame.Child = body;
            host.Children.Add(frame);
            _detailEmpty = Theme.Text("항목을 고르면 판정 기준·조치와 발견 행이 여기에 보입니다.", Theme.SecondaryText);
            _detailEmpty.HorizontalAlignment = HorizontalAlignment.Center;
            _detailEmpty.VerticalAlignment = VerticalAlignment.Center;
            host.Children.Add(_detailEmpty);
            return host;
        }

        private ContextMenu TemplateMenu()
        {
            var menu = new ContextMenu();
            var builtIn = new MenuItem { Header = "기본 양식", IsCheckable = true };
            builtIn.Click += (s, e) => SafeSet(CheckLogic.TemplateSettingKey, "");
            menu.Items.Add(builtIn);
            var mine = new MenuItem { IsCheckable = true };
            menu.Items.Add(mine);
            var pick = new MenuItem { Header = "내 양식 고르기…" };
            pick.Click += (s, e) => PickTemplate();
            menu.Items.Add(pick);
            menu.Items.Add(new Separator());
            var export = new MenuItem { Header = "기본 양식을 파일로 저장(고쳐 쓰기)…" };
            export.Click += (s, e) => ExportTemplate();
            menu.Items.Add(export);
            menu.Opened += (s, e) =>
            {
                var path = CustomTemplate();
                builtIn.IsChecked = path == null;
                mine.Visibility = path == null ? Visibility.Collapsed : Visibility.Visible;
                mine.IsChecked = path != null;
                mine.Header = path == null ? "" : "내 양식: " + Path.GetFileName(path).Replace("_", "__");
            };
            return menu;
        }

        // ================= 대상 DB =================

        private void FillDbList()
        {
            _dbList.Children.Clear();
            _dbChecks.Clear();
            IReadOnlyList<OracleConnectionProfile> profiles;
            try
            {
                profiles = _host.Profiles() ?? Array.Empty<OracleConnectionProfile>();
            }
            catch (Exception)
            {
                profiles = Array.Empty<OracleConnectionProfile>();
            }
            var saved = SafeGet(CheckLogic.DbsSettingKey);
            var chosen = OverviewLogic.ParseIds(saved);
            foreach (var profile in profiles.Where(p => p != null && !string.IsNullOrEmpty(p.Id)))
            {
                var provider = DbProviders.For(profile);
                var supported = provider.Monitor.CheckSet != null;
                var content = new StackPanel();
                var name = Theme.Text(profile.Name + (supported ? "" : " (지원 예정)"));
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                content.Children.Add(name);
                var address = Theme.Secondary(provider.DisplayName + " · " + ConnectionManagerLogic.Address(profile));
                address.TextTrimming = TextTrimming.CharacterEllipsis;
                content.Children.Add(address);
                var check = Theme.Foreground(new CheckBox
                {
                    Content = content,
                    IsEnabled = supported,
                    Margin = new Thickness(0, 2, 0, 4),
                    VerticalContentAlignment = VerticalAlignment.Top,
                    ToolTip = supported ? profile.Name : provider.DisplayName + "의 점검 항목은 아직 없습니다."
                });
                check.IsChecked = supported && (saved == null ? HasPassword(profile) : chosen.Contains(profile.Id));
                check.Tag = profile;
                AutomationProperties.SetName(check, profile.Name);
                AutomationProperties.SetAutomationId(check, "check-db:" + profile.Name);
                _dbChecks[profile.Id] = check;
                _dbList.Children.Add(check);
            }
            if (_dbChecks.Count == 0)
                _dbList.Children.Add(Theme.Secondary("등록한 접속이 없습니다."));
        }

        private void SetAllDbs(bool value)
        {
            foreach (var check in _dbChecks.Values.Where(c => c.IsEnabled))
                check.IsChecked = value;
        }

        private List<OracleConnectionProfile> SelectedProfiles()
        {
            return _dbChecks.Values.Where(c => c.IsEnabled && c.IsChecked == true).Select(c => (OracleConnectionProfile)c.Tag).ToList();
        }

        private bool HasPassword(OracleConnectionProfile profile)
        {
            try
            {
                return _host.HasStoredPassword != null && _host.HasStoredPassword(profile);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ================= 실행 =================

        private async void Run()
        {
            try
            {
                if (_closed || _work != null)
                    return;
                var targets = SelectedProfiles();
                if (targets.Count == 0)
                {
                    Dialogs.Show(_window, CheckLogic.Title, "점검할 DB를 왼쪽에서 고르세요.", false);
                    return;
                }
                SafeSet(CheckLogic.DbsSettingKey, OverviewLogic.SerializeIds(targets.Select(p => p.Id)));
                _runs.Clear();
                _results.Children.Clear();
                _selectedKey = null;
                ShowDetail(null);
                _saveText.Text = "";
                _openSaved.Visibility = Visibility.Collapsed;
                foreach (var profile in targets)
                {
                    var run = new DbRun { Profile = profile, Provider = DbProviders.For(profile) };
                    run.Report.Name = profile.Name;
                    run.Report.Address = ConnectionManagerLogic.Address(profile);
                    run.Block = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
                    _runs.Add(run);
                    _results.Children.Add(run.Block);
                    RenderDb(run);
                }
                _empty.Visibility = Visibility.Collapsed;
                _work = new CancellationTokenSource();
                SetBusy(true);
                ShowSummary();
                try
                {
                    var token = _work.Token;
                    await Task.WhenAll(_runs.Select(r => RunDb(r, token)).ToList());
                    PurgeOld();
                }
                finally
                {
                    _work.Dispose();
                    _work = null;
                    if (!_closed)
                    {
                        SetBusy(false);
                        ShowSummary();
                        ShowStoreStatus();
                    }
                }
            }
            catch (Exception ex)
            {
                // async void — 예외를 밖으로 내보내면 Folderss가 멈춘다
                if (!_closed)
                    _saveText.Text = "점검을 끝내지 못했습니다: " + ex.Message;
            }
        }

        /// <summary>
        /// DB 하나: 세션 → 서버 식별(DB 키) → 사용자 항목·덮어쓰기·직전 결과 → 항목 실행(하나 끝날 때마다 화면) → 직전 대비 → 기록.
        /// 접속 실패·취소는 그 DB 머리에 보인다. 예외를 밖으로 내지 않는다.
        /// </summary>
        private async Task RunDb(DbRun run, CancellationToken token)
        {
            DbSession session = null;
            try
            {
                await _gate.WaitAsync(token);
            }
            catch (OperationCanceledException)
            {
                run.Report.Error = "취소함";
                run.Done = true;
                RenderDb(run);
                return;
            }
            try
            {
                run.Phase = "연결 중…";
                RenderDb(run);
                var ask = !HasPassword(run.Profile);
                try
                {
                    if (ask)
                        await _promptGate.WaitAsync(token);
                    try
                    {
                        session = await _host.Connect(run.Profile.Id);
                    }
                    finally
                    {
                        if (ask)
                            _promptGate.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    run.Report.Error = "취소함";
                    return;
                }
                catch (Exception ex)
                {
                    run.Report.Error = DbSession.DescribeError(ex);
                    return;
                }
                if (session == null)
                {
                    run.Report.Error = "비밀번호를 입력하지 않아 점검하지 않았습니다.";
                    return;
                }
                if (_closed || token.IsCancellationRequested)
                {
                    run.Report.Error = "취소함";
                    return;
                }
                run.Phase = "점검 중…";
                RenderDb(run);
                var set = run.Provider.Monitor.CheckSet;
                var kind = run.Provider.Kind;
                string identity = null;
                string label = "";
                try
                {
                    var table = await session.QueryTableAsync(set.IdentitySql, 1, token);
                    var columns = table.Columns.Select(c => c.ToUpperInvariant()).ToList();
                    if (table.Rows.Count > 0)
                    {
                        var id = columns.IndexOf(MonitorCheckSet.Identity);
                        var name = columns.IndexOf(MonitorCheckSet.Label);
                        identity = id >= 0 ? table.Rows[0][id] : null;
                        label = name >= 0 ? table.Rows[0][name] ?? "" : "";
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    // 서버 식별을 못 읽으면(권한) 접속 주소로 구분한다 — 같은 DB를 다른 이름으로 등록하면 이력이 나뉜다
                }
                if (string.IsNullOrEmpty(identity))
                    identity = "address:" + ConnectionManagerLogic.Address(run.Profile);
                run.Report.Label = label;
                run.Report.Version = await TableSpecQueries.LoadServerVersionAsync(session, token);
                var dbKey = MonitorStore.DbKey(kind, identity);

                var userItems = new List<UserCheckItem>();
                IReadOnlyDictionary<string, CheckOverride> overrides = new Dictionary<string, CheckOverride>();
                Dictionary<string, StoredResult> previous = null;
                if (_store != null)
                {
                    try
                    {
                        userItems = _store.LoadUserItems();
                        overrides = _store.LoadOverrides(dbKey);
                        var last = _store.LastRunId(dbKey);
                        if (last.HasValue)
                        {
                            previous = _store.Results(last.Value);
                            var at = _store.RunStartedAt(last.Value);
                            run.Report.PreviousAt = at.HasValue ? at.Value.ToLocalTime() : (DateTime?)null;
                        }
                    }
                    catch (Exception ex)
                    {
                        StoreFailed(ex);
                    }
                }
                var plan = CheckLogic.Plan(set, kind, userItems, overrides);
                run.Pending = plan.Active.ToList();
                run.Started = DateTime.Now;
                run.Report.StartedAt = run.Started;
                RenderDb(run);
                var startedUtc = DateTime.UtcNow;
                var watch = Stopwatch.StartNew();
                var outcomes = await CheckRunner.RunAsync(session, plan.Active, outcome =>
                {
                    CheckLogic.Compare(outcome, Previous(previous, outcome.Check.Id));
                    run.Outcomes.Add(outcome);
                    run.Pending.Remove(outcome.Check);
                    if (!_closed)
                        RenderDb(run);
                }, token);
                watch.Stop();
                foreach (var invalid in plan.Invalid)
                {
                    CheckLogic.Compare(invalid, Previous(previous, invalid.Check.Id));
                    run.Outcomes.Add(invalid);
                }
                foreach (var disabled in plan.Disabled)
                    run.Outcomes.Add(CheckLogic.Skipped(disabled, "disabled", "꺼 둔 항목입니다([항목 관리…]에서 켤 수 있습니다)."));
                run.Report.Elapsed = watch.Elapsed;
                run.Report.Outcomes = run.Outcomes.ToList();
                if (_store != null && !_store.ReadOnly && Recording)
                {
                    try
                    {
                        _store.TouchDb(kind, identity, string.IsNullOrEmpty(label) ? run.Profile.Name : label, DateTime.UtcNow);
                        _store.SaveRun(dbKey, startedUtc, (long)watch.Elapsed.TotalMilliseconds, _appVersion,
                            outcomes.Concat(plan.Invalid).Select(CheckLogic.ToStored));
                    }
                    catch (Exception ex)
                    {
                        StoreFailed(ex);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                run.Report.Error = run.Outcomes.Count > 0 ? "취소함(앞 " + run.Outcomes.Count.ToString(CultureInfo.InvariantCulture) + "개 항목만 돌림)" : "취소함";
                run.Report.Outcomes = run.Outcomes.ToList();
            }
            catch (Exception ex)
            {
                run.Report.Error = DbSession.DescribeError(ex);
                run.Report.Outcomes = run.Outcomes.ToList();
            }
            finally
            {
                _gate.Release();
                DisposeQuietly(session);
                run.Done = true;
                run.Pending.Clear();
                if (!_closed)
                {
                    RenderDb(run);
                    ShowSummary();
                }
            }
        }

        private static StoredResult Previous(Dictionary<string, StoredResult> previous, string id)
        {
            StoredResult value;
            return previous != null && previous.TryGetValue(id, out value) ? value : null;
        }

        private void PurgeOld()
        {
            if (_store == null || _store.ReadOnly || !Recording)
                return;
            try
            {
                bool valid;
                var days = CheckLogic.RetentionDays(SafeGet(CheckLogic.RetentionSettingKey), out valid);
                _store.Purge(DateTime.UtcNow.AddDays(-days));
            }
            catch (Exception ex)
            {
                StoreFailed(ex);
            }
        }

        private void StoreFailed(Exception ex)
        {
            if (_closed)
                return;
            _storeText.Text = "이력 파일을 쓰지 못했습니다(점검은 계속): " + ex.Message;
            _storeText.Foreground = Theme.Warning;
        }

        private void SetBusy(bool busy)
        {
            _run.IsEnabled = !busy;
            _cancel.IsEnabled = busy;
            _save.IsEnabled = !busy && _runs.Any(r => r.Done);
            foreach (var check in _dbChecks.Values)
                check.IsHitTestVisible = !busy;
        }

        private void ShowSummary()
        {
            if (_runs.Count == 0)
            {
                _summary.Text = CheckLogic.RunSummaryText(0, 0, Enumerable.Empty<CheckOutcome>());
                return;
            }
            var running = _runs.Count(r => !r.Done);
            var text = CheckLogic.RunSummaryText(_runs.Count, _runs.Count(r => r.Done && !string.IsNullOrEmpty(r.Report.Error) && r.Outcomes.Count == 0),
                _runs.SelectMany(r => r.Outcomes));
            _summary.Text = running > 0 ? "점검 중… (" + (_runs.Count - running).ToString(CultureInfo.InvariantCulture) + "/" + _runs.Count.ToString(CultureInfo.InvariantCulture) + " DB) · " + text : text;
        }

        // ================= 결과 목록 =================

        /// <summary>DB 하나의 결과 묶음을 다시 그린다(머리 + 분류별 항목 줄, 아직 돌지 않은 항목은 "대기").</summary>
        private void RenderDb(DbRun run)
        {
            if (_closed)
                return;
            var block = run.Block;
            block.Children.Clear();
            var head = new DockPanel { Margin = new Thickness(10, 8, 10, 4) };
            var badge = Theme.DbBadge(run.Profile);
            badge.Margin = new Thickness(0, 0, 8, 0);
            badge.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(badge, Dock.Left);
            head.Children.Add(badge);
            var state = Theme.Text(DbStateText(run), string.IsNullOrEmpty(run.Report.Error) ? Theme.SecondaryText : Theme.PrimaryText);
            if (!string.IsNullOrEmpty(run.Report.Error))
                state.Foreground = Theme.Danger;
            state.TextWrapping = TextWrapping.Wrap;
            state.VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetAutomationId(state, "check-db-state:" + run.Profile.Name);
            head.Children.Add(state);
            block.Children.Add(head);

            var rows = CheckLogic.Ordered(run.Outcomes.Concat(run.Pending.Select(p => new CheckOutcome { Check = p, Status = CheckStatus.Info, Reason = PendingReason })));
            string category = null;
            foreach (var outcome in rows)
            {
                if (outcome.Check.Category != category)
                {
                    category = outcome.Check.Category;
                    var label = Theme.Secondary(category);
                    label.Margin = new Thickness(14, 6, 0, 2);
                    block.Children.Add(label);
                }
                block.Children.Add(Row(run, outcome));
            }
            if (!string.IsNullOrEmpty(_selectedKey) && _selectedKey.StartsWith(run.Profile.Id + "\n", StringComparison.Ordinal))
                ShowDetail(FindSelected());
        }

        private const string PendingReason = "pending";

        private static bool IsPending(CheckOutcome outcome)
        {
            return outcome.Reason == PendingReason;
        }

        private string DbStateText(DbRun run)
        {
            var name = run.Profile.Name + (string.IsNullOrEmpty(run.Report.Label) ? "" : " — " + run.Report.Label);
            if (!run.Done)
                return name + " · " + (run.Phase ?? "대기") + (run.Outcomes.Count > 0 || run.Pending.Count > 0
                    ? " " + run.Outcomes.Count.ToString(CultureInfo.InvariantCulture) + "/" + (run.Outcomes.Count + run.Pending.Count).ToString(CultureInfo.InvariantCulture) : "");
            if (!string.IsNullOrEmpty(run.Report.Error) && run.Outcomes.Count == 0)
                return name + " · 점검 못 함: " + run.Report.Error;
            var text = name + " · " + CheckLogic.SummaryText(run.Outcomes) + " · " + run.Report.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "초";
            if (run.Report.PreviousAt.HasValue)
                text += " · 직전 " + run.Report.PreviousAt.Value.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(run.Report.Error))
                text += " · " + run.Report.Error;
            return text;
        }

        private FrameworkElement Row(DbRun run, CheckOutcome outcome)
        {
            var key = run.Profile.Id + "\n" + outcome.Check.Id;
            var grid = new Grid { Margin = new Thickness(14, 0, 10, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            var pending = IsPending(outcome);
            var chip = Chip(pending ? (run.Done ? "—" : "대기") : CheckLogic.StatusText(outcome.Status), pending ? (CheckStatus?)null : outcome.Status);
            chip.HorizontalAlignment = HorizontalAlignment.Left;
            chip.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(chip);
            var title = Theme.Text(outcome.Check.Title + (outcome.Check.IsUser ? " (사용자)" : ""), pending ? Theme.SecondaryText : Theme.PrimaryText);
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            title.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(title, 1);
            grid.Children.Add(title);
            var value = Theme.Text(pending ? "" : ValueText(outcome), Theme.SecondaryText);
            value.TextTrimming = TextTrimming.CharacterEllipsis;
            value.VerticalAlignment = VerticalAlignment.Center;
            value.ToolTip = pending ? null : (outcome.Message ?? ValueText(outcome));
            Grid.SetColumn(value, 2);
            grid.Children.Add(value);
            var trend = Theme.Text(pending ? "" : CheckLogic.TrendText(outcome.Trend), Theme.SecondaryText);
            if (outcome.Trend == CheckTrend.Worse)
                trend.Foreground = Theme.Danger;
            else if (outcome.Trend == CheckTrend.Better)
                trend.Foreground = Theme.Success;
            trend.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(trend, 3);
            grid.Children.Add(trend);
            var elapsed = Theme.Secondary(pending ? "" : outcome.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "초");
            elapsed.HorizontalAlignment = HorizontalAlignment.Right;
            elapsed.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(elapsed, 4);
            grid.Children.Add(elapsed);

            var row = new Border { Child = grid, Padding = new Thickness(0, 3, 0, 3), Focusable = !pending, Cursor = pending ? null : Cursors.Hand, Background = Brushes.Transparent };
            if (key == _selectedKey)
                row.SetResourceReference(Border.BackgroundProperty, Theme.Selection);
            AutomationProperties.SetAutomationId(row, "check-row:" + run.Profile.Name + ":" + outcome.Check.Id);
            AutomationProperties.SetName(row, outcome.Check.Title + " " + (pending ? "대기" : CheckLogic.StatusText(outcome.Status)));
            AutomationProperties.SetHelpText(row, pending ? "" : ValueText(outcome));
            if (!pending)
            {
                row.MouseLeftButtonUp += (s, e) => Select(key);
                row.KeyDown += (s, e) =>
                {
                    if (e.Key == Key.Enter || e.Key == Key.Space)
                    {
                        e.Handled = true;
                        Select(key);
                    }
                };
            }
            return row;
        }

        private static string ValueText(CheckOutcome outcome)
        {
            if (outcome.Status == CheckStatus.Skipped || outcome.Status == CheckStatus.Error)
                return outcome.Message ?? outcome.ValueText ?? "";
            return outcome.ValueText ?? "";
        }

        private static Border Chip(string text, CheckStatus? status)
        {
            var label = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold };
            var chip = new Border { CornerRadius = new CornerRadius(3), Padding = new Thickness(6, 0, 6, 1), BorderThickness = new Thickness(1), Child = label, MinWidth = 44 };
            label.HorizontalAlignment = HorizontalAlignment.Center;
            Paint(chip, label, status);
            return chip;
        }

        /// <summary>칩 색: 위험·오류 위험색, 주의 경고색, 정상 성공색, 정보 강조색, 건너뜀·대기는 테두리만.</summary>
        private static void Paint(Border chip, TextBlock label, CheckStatus? status)
        {
            Brush color = null;
            switch (status)
            {
                case CheckStatus.Critical:
                case CheckStatus.Error:
                    color = Theme.Danger;
                    break;
                case CheckStatus.Warning:
                    color = Theme.Warning;
                    break;
                case CheckStatus.Ok:
                    color = Theme.Success;
                    break;
            }
            if (color != null)
            {
                chip.Background = color;
                chip.BorderBrush = color;
                label.Foreground = Brushes.White;
            }
            else if (status == CheckStatus.Info)
            {
                chip.Background = Brushes.Transparent;
                chip.SetResourceReference(Border.BorderBrushProperty, Theme.Accent);
                label.SetResourceReference(TextBlock.ForegroundProperty, Theme.Accent);
            }
            else
            {
                chip.Background = Brushes.Transparent;
                chip.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
                label.SetResourceReference(TextBlock.ForegroundProperty, Theme.SecondaryText);
            }
        }

        // ================= 상세 =================

        private void Select(string key)
        {
            _selectedKey = key;
            foreach (var run in _runs)
                RenderDb(run);
            ShowDetail(FindSelected());
        }

        private Tuple<DbRun, CheckOutcome> FindSelected()
        {
            if (string.IsNullOrEmpty(_selectedKey))
                return null;
            foreach (var run in _runs)
            {
                foreach (var outcome in run.Outcomes)
                {
                    if (run.Profile.Id + "\n" + outcome.Check.Id == _selectedKey)
                        return Tuple.Create(run, outcome);
                }
            }
            return null;
        }

        private void ShowDetail(Tuple<DbRun, CheckOutcome> selected)
        {
            if (selected == null)
            {
                _detailBody.Visibility = Visibility.Collapsed;
                _detailEmpty.Visibility = Visibility.Visible;
                _findingColumns = new List<ResultColumn>();
                _findings.Show(_findingColumns, new ObservableCollection<ResultGridRow>());
                return;
            }
            _detailBody.Visibility = Visibility.Visible;
            _detailEmpty.Visibility = Visibility.Collapsed;
            var run = selected.Item1;
            var outcome = selected.Item2;
            var check = outcome.Check;
            _detailChipText.Text = CheckLogic.StatusText(outcome.Status);
            Paint(_detailChip, _detailChipText, outcome.Status);
            _detailTitle.Text = run.Profile.Name + " · " + check.Category + " · " + check.Title;
            var facts = new List<string>();
            if (!string.IsNullOrEmpty(outcome.ValueText) && outcome.Status != CheckStatus.Skipped && outcome.Status != CheckStatus.Error)
                facts.Add("값: " + outcome.ValueText);
            facts.Add("판정: " + CheckLogic.RuleText(check.Rule));
            if (outcome.Previous != null)
                facts.Add("직전: " + CheckLogic.StatusText(CheckLogic.ParseStatus(outcome.Previous.Status))
                    + (outcome.Previous.Value.HasValue ? " (" + CheckLogic.NumberText(outcome.Previous.Value.Value) + ")" : "") + " → " + CheckLogic.TrendText(outcome.Trend));
            else if (outcome.Trend == CheckTrend.New)
                facts.Add("직전 기록 없음");
            if (!string.IsNullOrWhiteSpace(check.Privilege))
                facts.Add("필요 권한: " + check.Privilege);
            facts.Add("걸린 시간 " + outcome.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "초");
            facts.Add("id " + check.Id);
            _detailFacts.Text = string.Join(" · ", facts);
            var note = CheckReportModel.Note(outcome);
            _detailNote.Text = note;
            _detailNote.Visibility = string.IsNullOrEmpty(note) ? Visibility.Collapsed : Visibility.Visible;
            if (outcome.Status == CheckStatus.Error)
                _detailNote.Foreground = Theme.Danger;
            else if (outcome.Status == CheckStatus.Skipped)
                _detailNote.SetResourceReference(TextBlock.ForegroundProperty, Theme.SecondaryText);
            else
                _detailNote.SetResourceReference(TextBlock.ForegroundProperty, Theme.PrimaryText);
            _detailSql.Text = check.Sql;
            var shown = new QueryTable();
            shown.Columns.AddRange(outcome.Columns);
            shown.Rows.AddRange(outcome.Rows);
            _findingColumns = MonitorLogic.Columns(shown);
            var rows = new ObservableCollection<ResultGridRow>();
            for (var i = 0; i < outcome.Rows.Count; i++)
                rows.Add(new ResultGridRow(i + 1, outcome.Rows[i]));
            _findings.Show(_findingColumns, rows);
            _findingsText.Text = outcome.Status == CheckStatus.Skipped || outcome.Status == CheckStatus.Error ? "결과 행 없음"
                : outcome.RowCount == 0 ? "결과 행 없음"
                : "결과 " + CheckLogic.CountText(outcome.RowCount) + (outcome.RowCount > outcome.Rows.Count ? " (앞 " + outcome.Rows.Count.ToString(CultureInfo.InvariantCulture) + "행만)" : "");
        }

        // ================= 엑셀 =================

        private string CustomTemplate()
        {
            var path = SafeGet(CheckLogic.TemplateSettingKey);
            return string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        }

        private async void SaveReport()
        {
            try
            {
                var done = _runs.Where(r => r.Done).ToList();
                if (done.Count == 0 || _work != null)
                    return;
                byte[] template;
                var custom = CustomTemplate();
                if (custom != null)
                {
                    if (!File.Exists(custom))
                    {
                        Dialogs.Show(_window, CheckLogic.Title, "내 양식 파일이 없습니다: " + custom + Environment.NewLine + "[양식 ▾]에서 다시 고르거나 기본 양식으로 바꾸세요.", true);
                        return;
                    }
                    template = File.ReadAllBytes(custom);
                }
                else
                {
                    template = CheckReportTemplate.Create();
                }
                var dialog = new SaveFileDialog
                {
                    Title = "일일 점검 보고서 저장",
                    Filter = "Excel 통합 문서 (*.xlsx)|*.xlsx",
                    DefaultExt = ".xlsx",
                    AddExtension = true,
                    OverwritePrompt = true,
                    FileName = CheckLogic.ReportFileName(DateTime.Now)
                };
                var folder = SafeGet(CheckLogic.FolderSettingKey);
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                    dialog.InitialDirectory = folder;
                if (dialog.ShowDialog(_window) != true)
                    return;
                var path = dialog.FileName;
                SafeSet(CheckLogic.FolderSettingKey, Path.GetDirectoryName(path));
                var context = CheckReportModel.Context(done.Select(r => r.Report).ToList(), null, Environment.UserName);
                _save.IsEnabled = false;
                _saveText.Text = "엑셀 만드는 중…";
                try
                {
                    await Task.Run(() =>
                    {
                        using (var output = new MemoryStream())
                        {
                            ExcelTemplate.Render(template, context, output);
                            File.WriteAllBytes(path, output.ToArray());
                        }
                    });
                }
                finally
                {
                    if (!_closed)
                        _save.IsEnabled = _work == null && _runs.Any(r => r.Done);
                }
                if (_closed)
                    return;
                _lastOutput = path;
                _saveText.Text = "저장했습니다: " + path;
                _saveText.ToolTip = path;
                _openSaved.Visibility = Visibility.Visible;
            }
            catch (TemplateException ex)
            {
                _saveText.Text = "";
                Dialogs.Show(_window, CheckLogic.Title, "양식에 문법 오류가 있습니다: " + ex.Message, true);
            }
            catch (Exception ex)
            {
                _saveText.Text = "";
                Dialogs.Show(_window, CheckLogic.Title, "엑셀로 저장하지 못했습니다: " + ex.Message, true);
            }
        }

        private void PickTemplate()
        {
            try
            {
                var dialog = new OpenFileDialog { Title = "보고서 양식 고르기", Filter = "Excel 통합 문서 (*.xlsx)|*.xlsx", CheckFileExists = true };
                if (dialog.ShowDialog(_window) == true)
                    SafeSet(CheckLogic.TemplateSettingKey, dialog.FileName);
            }
            catch (Exception ex)
            {
                Dialogs.Show(_window, CheckLogic.Title, ex.Message, true);
            }
        }

        private void ExportTemplate()
        {
            try
            {
                var dialog = new SaveFileDialog
                {
                    Title = "기본 양식 저장",
                    Filter = "Excel 통합 문서 (*.xlsx)|*.xlsx",
                    DefaultExt = ".xlsx",
                    AddExtension = true,
                    OverwritePrompt = true,
                    FileName = CheckReportTemplate.FileName
                };
                if (dialog.ShowDialog(_window) != true)
                    return;
                CheckReportTemplate.Save(dialog.FileName);
                _lastOutput = dialog.FileName;
                _saveText.Text = "기본 양식을 저장했습니다(고친 뒤 [양식 ▾ > 내 양식 고르기…]): " + dialog.FileName;
                _openSaved.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                Dialogs.Show(_window, CheckLogic.Title, "양식을 저장하지 못했습니다: " + ex.Message, true);
            }
        }

        private void OpenPath(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Dialogs.Show(_window, CheckLogic.Title, "파일을 열지 못했습니다: " + ex.Message, true);
            }
        }

        // ================= 항목 관리 =================

        private void ManageItems()
        {
            if (_store == null)
            {
                Dialogs.Show(_window, CheckLogic.Title, "이력 파일(history.db)을 열 수 없어 항목을 관리할 수 없습니다: " + _storeError, true);
                return;
            }
            if (_store.ReadOnly)
            {
                Dialogs.Show(_window, CheckLogic.Title, "이력 파일이 더 새 버전 DB Helper의 것이라 항목을 고칠 수 없습니다: " + _store.FilePath, true);
                return;
            }
            CheckItemsDialog.Show(_window, new CheckItemsDialog.Host
            {
                Store = _store,
                Profiles = _host.Profiles,
                Connect = _host.Connect,
                HelpFolder = _host.HelpFolder
            });
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
                // 고른 DB·양식을 기억하지 못할 뿐이다
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

        /// <summary>DB 하나의 점검 상태와 화면 조각.</summary>
        private sealed class DbRun
        {
            public OracleConnectionProfile Profile { get; set; }
            public IDbProvider Provider { get; set; }
            public CheckReportDb Report { get; } = new CheckReportDb();
            public List<CheckOutcome> Outcomes { get; } = new List<CheckOutcome>();
            public List<MonitorCheck> Pending { get; set; } = new List<MonitorCheck>();
            public string Phase { get; set; }
            public DateTime Started { get; set; }
            public bool Done { get; set; }
            public StackPanel Block { get; set; }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace MyPlugin
{
    /// <summary>
    /// GCM(공통 코드) 정의서 만들기 창(모달 — 만드는 동안 같은 DB 세션으로 다른 문장을 실행하지 않게).
    /// 왼쪽: SQL — [저장한 SQL] 목록(플러그인 데이터 폴더\code-sql의 *.sql, 고르면 편집기에 넣음) · [저장…] · [SQL 폴더] · [LLM에게 SQL 요청],
    /// 편집기(사용자 SQL — 결과 열의 AS 별칭이 계약), [열 확인](ROWNUM 한 줄로 실행해 별칭 검사), 별칭 계약 표(검사 뒤 상태 표시), 그룹 정규식.
    /// 오른쪽: 문서 정보, 출력 형식(엑셀 양식 · JSON · CSV), 양식(엑셀일 때 — 기본 양식 + 사용자 양식 폴더 templates\gcm) 또는 템플릿 파일, [LLM 지침 복사]·[지침 보기].
    /// 아래: 상태, [만들기…](저장 위치를 물은 뒤 조회 → 별칭 검사 → 그룹 묶기 → 출력), 다 만들면 [파일 열기]·[폴더 열기].
    /// [만들기…] 때의 입력값은 플러그인 설정(CodeSpecLogic.SettingKey)에 저장하고 창을 다시 열면 그 값으로 채운다. SQL 파일은 접속마다 기억한다.
    /// </summary>
    internal sealed class CodeSpecDialog
    {
        private readonly OracleConnectionProfile _profile;
        private readonly DbSession _session;
        private readonly Func<bool> _isRunning;
        private readonly Action<string> _saveSettings;
        private readonly CodeSpecSettings _settings;
        private readonly string _templateFolder;
        private readonly string _sqlFolder;
        private readonly IReadOnlyList<OracleConnectionProfile> _profiles;
        private readonly string _initialSqlOverride;
        private readonly string _helpFolder;
        private readonly Window _window;
        private ComboBox _target;
        // 사용자가 대상 DB를 바꿨다: 창을 닫은 뒤 이 DB로 다시 연다(지금 편집기의 SQL을 가지고)
        private CodeSpecSwitch _switch;
        private readonly CancellationTokenSource _closing = new CancellationTokenSource();

        private ComboBox _sqlFiles;
        private TextBox _sql;
        private TextBlock _checkText;
        private ListView _aliasList;
        private List<AliasItem> _aliasItems;
        private TextBox _groupPattern;
        private CheckBox _groupSearchName;
        private TextBlock _patternError;
        private TextBox _title;
        private TextBox _project;
        private TextBox _system;
        private TextBox _author;
        private TextBox _version;
        private TextBox _date;
        private TextBox _description;
        private RadioButton _outputExcel;
        private RadioButton _outputJson;
        private RadioButton _outputCsv;
        private FrameworkElement _templatePanel;
        private RadioButton _defaultTemplate;
        private ComboBox _templateBox;
        private TextBlock _templateHint;
        private Button _openTemplate;
        private List<SpecTemplate> _templates;
        private bool _refreshingTemplates;
        private bool _refreshingSqlFiles;
        private RadioButton _fileTemplate;
        private TextBox _templatePath;
        private TextBlock _status;
        private ProgressBar _progress;
        private Button _create;
        private Button _check;
        private Button _openFile;
        private Button _openFolder;
        private FrameworkElement _inputs;

        private CancellationTokenSource _work;
        private string _lastOutput;
        private string _folder;
        // 지금 편집기에 든 SQL의 파일(저장한 SQL을 골랐거나 저장했을 때). 없으면 null
        private string _sqlFile;
        private bool _closed;

        private CodeSpecDialog(Window owner, OracleConnectionProfile profile, DbSession session, Func<bool> isRunning,
            Func<string> loadSettings, Action<string> saveSettings, string templateFolder, string sqlFolder,
            IReadOnlyList<OracleConnectionProfile> profiles, string initialSqlOverride, string helpFolder)
        {
            _helpFolder = helpFolder;
            _profiles = profiles ?? new List<OracleConnectionProfile>();
            _initialSqlOverride = initialSqlOverride;
            _profile = profile;
            _session = session;
            _isRunning = isRunning ?? (() => false);
            _saveSettings = saveSettings;
            _settings = CodeSpecLogic.ParseSettings(loadSettings == null ? null : loadSettings());
            _templateFolder = templateFolder;
            _sqlFolder = sqlFolder;
            _folder = _settings == null ? null : _settings.Folder;
            _window = new Window
            {
                Title = DialogKit.TitleFor(CodeSpecLogic.Title, profile),
                Width = 1120,
                Height = 860,
                MinWidth = 820,
                MinHeight = 520,
                ShowInTaskbar = false,
                ResizeMode = ResizeMode.CanResizeWithGrip
            };
            Theme.ApplyWindow(_window);
            DialogKit.SetOwner(_window, owner);
            _window.Content = Build();
            _window.Loaded += (s, e) => _sql.Focus();
            _window.Closing += (s, e) =>
            {
                _closed = true;
                _closing.Cancel();
                if (_work != null)
                    _work.Cancel();
            };
            _window.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape && _work == null)
                {
                    e.Handled = true;
                    _window.Close();
                }
            };
        }

        /// <summary>
        /// 창을 띄운다(모달). isRunning: 그 DB에서 다른 문장을 실행 중인지. loadSettings·saveSettings: 지난번 입력값(CodeSpecLogic.SettingKey).
        /// templateFolder: 사용자 GCM 양식 폴더, sqlFolder: 저장한 SQL 폴더(둘 다 모르면 null). profiles: 대상 DB 콤보에 보일 접속 목록.
        /// initialSqlOverride: 다른 DB에서 넘어오며 가져온 편집기 SQL(없으면 null — 설정·파일·본보기 순).
        /// 사용자가 창 안에서 대상 DB를 바꾸면 창을 닫고 그 요청(DB id·편집기 SQL)을 돌려준다. 아니면 null.
        /// </summary>
        public static CodeSpecSwitch Show(Window owner, OracleConnectionProfile profile, DbSession session, Func<bool> isRunning,
            Func<string> loadSettings, Action<string> saveSettings, string templateFolder, string sqlFolder,
            IReadOnlyList<OracleConnectionProfile> profiles = null, string initialSqlOverride = null, string helpFolder = null)
        {
            var dialog = new CodeSpecDialog(owner, profile, session, isRunning, loadSettings, saveSettings, templateFolder, sqlFolder, profiles, initialSqlOverride, helpFolder);
            dialog._window.ShowDialog();
            return dialog._switch;
        }

        private string DbId
        {
            get { return _profile == null ? null : _profile.Id; }
        }

        // ================= 만들기 =================

        private FrameworkElement Build()
        {
            var root = new DockPanel { Margin = new Thickness(14, 12, 14, 12) };

            var head = new DockPanel();
            var help = SmallButton("도움말", "별칭 계약·이름/값 짝 열·출력 형식 설명(도움말 창)");
            help.Click += (s, e) => HelpWindow.Show(_window, _helpFolder, HelpLogic.CodeSpec);
            help.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(help, Dock.Right);
            head.Children.Add(help);
            head.Children.Add(BuildTarget());
            DockPanel.SetDock(head, Dock.Top);
            root.Children.Add(head);

            var foot = BuildFooter();
            DockPanel.SetDock(foot, Dock.Bottom);
            root.Children.Add(foot);

            // 왼쪽(SQL) | 분할선 | 오른쪽(문서·출력·양식) — 분할선을 끌어 너비를 조절한다
            var body = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 420 });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300), MinWidth = 250 });
            body.Children.Add(BuildSql());
            var columnSplitter = Splitter(GridResizeDirection.Columns, "SQL 영역 너비 조절");
            Grid.SetColumn(columnSplitter, 1);
            body.Children.Add(columnSplitter);
            var options = BuildOptions();
            Grid.SetColumn(options, 2);
            body.Children.Add(options);
            _inputs = body;
            root.Children.Add(body);
            return root;
        }

        // 맨 위: "대상 DB" 배지 + 주소 + [다른 접속 콤보]. 콤보로 바꾸면 창을 닫고 그 DB로 다시 연다(필요하면 연결)
        private FrameworkElement BuildTarget()
        {
            var line = new DockPanel();
            var label = Label("대상 DB");
            label.FontWeight = FontWeights.SemiBold;
            label.Margin = new Thickness(0, 0, 8, 0);
            line.Children.Add(label);
            var items = _profiles.Where(p => p != null && !string.IsNullOrEmpty(p.Id)).ToList();
            if (_profile != null && !items.Any(p => p.Id == _profile.Id))
                items.Insert(0, _profile);
            _target = new ComboBox { MinWidth = 220, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(_target, "대상 DB");
            _target.ItemsSource = items.Select(p => new TargetItem(p)).ToList();
            _target.SelectedItem = _target.Items.OfType<TargetItem>().FirstOrDefault(t => _profile != null && t.Profile.Id == _profile.Id);
            _target.ToolTip = "정의서를 만들 접속입니다. 다른 접속을 고르면 이 창을 닫고 그 DB로 다시 엽니다(편집기의 SQL은 가져갑니다)";
            _target.SelectionChanged += (s, e) =>
            {
                var chosen = _target.SelectedItem as TargetItem;
                if (chosen == null || _profile == null || chosen.Profile.Id == _profile.Id || _work != null)
                    return;
                _switch = new CodeSpecSwitch { DbId = chosen.Profile.Id, Sql = _sql == null ? null : _sql.Text };
                _window.Close();
            };
            DockPanel.SetDock(label, Dock.Left);
            DockPanel.SetDock(_target, Dock.Left);
            line.Children.Add(_target);
            var address = DialogKit.Target(_profile, ConnectionManagerLogic.Address(_profile) + " · " + DbProviders.DisplayNameOf(_profile));
            address.VerticalAlignment = VerticalAlignment.Center;
            line.Children.Add(address);
            return line;
        }

        private FrameworkElement BuildSql()
        {
            // 위: 저장한 SQL 줄 / 편집기 / 분할선(높이 조절) / 아래: 열 확인·별칭 표·그룹 정규식
            var panel = new Grid();
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star), MinHeight = 100 });
            panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
            panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star), MinHeight = 130 });

            var top = new DockPanel();
            var label = Label("저장한 SQL");
            top.Children.Add(label);
            var sqlButtons = new StackPanel { Orientation = Orientation.Horizontal };
            var save = SmallButton("저장…", "편집기의 SQL을 SQL 폴더에 .sql 파일로 저장합니다(접속마다 마지막에 쓴 파일을 기억)");
            save.Click += (s, e) => SaveSql();
            var folder = SmallButton("SQL 폴더", "저장한 SQL 폴더를 탐색기로 엽니다. 여기에 넣은 .sql이 목록에 보입니다");
            folder.Click += (s, e) => OpenFolder(EnsureFolder(_sqlFolder, "SQL"));
            var llmSql = SmallButton("LLM에게 SQL 요청", "별칭 계약이 든 요청 글을 복사합니다 — 우리 GCM 테이블 구조를 덧붙여 LLM에게 보내면 맞는 SQL을 써 줍니다");
            llmSql.Click += (s, e) =>
            {
                if (DialogKit.TryCopy(CodeSpecLogic.LlmSqlPrompt(DbProviders.DisplayNameOf(_profile))))
                    SetStatus("LLM에게 붙여 넣을 SQL 요청을 복사했습니다. 끝에 테이블 구조를 적어 보내세요.", false);
                else
                    SetStatus("클립보드를 쓸 수 없습니다. 잠시 뒤 다시 누르세요.", true);
            };
            var sample = SmallButton("본보기 SQL", "플러그인의 본보기 SQL(범용 코드 테이블 꼴)을 편집기에 넣습니다 — 테이블·열 이름과 WHERE를 환경에 맞게 바꾸세요");
            sample.Click += (s, e) =>
            {
                _sql.Text = CodeSpecLogic.DefaultSql;
                _sqlFile = null;
                RefreshSqlFiles(null);
                SetStatus("본보기 SQL을 넣었습니다. 테이블·열 이름과 WHERE(공장 등)를 환경에 맞게 바꾼 뒤 [열 확인]을 누르세요.", false);
            };
            foreach (var button in new[] { sample, save, folder, llmSql })
                sqlButtons.Children.Add(button);
            DockPanel.SetDock(sqlButtons, Dock.Right);
            top.Children.Add(sqlButtons);
            _sqlFiles = new ComboBox { Margin = new Thickness(8, 0, 0, 0) };
            AutomationProperties.SetName(_sqlFiles, "저장한 SQL");
            _sqlFiles.DropDownOpened += (s, e) => RefreshSqlFiles(_sqlFile);
            _sqlFiles.SelectionChanged += (s, e) =>
            {
                if (_refreshingSqlFiles)
                    return;
                var item = _sqlFiles.SelectedItem as SqlFileItem;
                if (item != null && item.Path != null)
                    LoadSqlFile(item.Path);
            };
            top.Children.Add(_sqlFiles);
            Grid.SetRow(top, 0);
            panel.Children.Add(top);

            // 아래쪽: 열 확인(위) · 그룹 정규식(아래) · 별칭 표(남는 높이)
            var bottom = new DockPanel();
            var checkLine = new DockPanel();
            _check = SmallButton("열 확인", "SQL을 한 행만 실행해 결과 열이 별칭 계약과 맞는지 봅니다(큰 조회도 빠릅니다)");
            _check.Margin = new Thickness(0);
            _check.Click += (s, e) => Check();
            checkLine.Children.Add(_check);
            _checkText = Theme.Text("", Theme.SecondaryText);
            _checkText.Margin = new Thickness(10, 0, 0, 0);
            _checkText.VerticalAlignment = VerticalAlignment.Center;
            _checkText.TextTrimming = TextTrimming.CharacterEllipsis;
            checkLine.Children.Add(_checkText);
            DockPanel.SetDock(checkLine, Dock.Top);
            bottom.Children.Add(checkLine);

            var patternLine = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            patternLine.Children.Add(Label("그룹 정규식"));
            _groupSearchName = Check("그룹명·설명도 찾기", _settings != null && _settings.GroupPatternSearchName);
            _groupSearchName.Margin = new Thickness(10, 0, 0, 0);
            DockPanel.SetDock(_groupSearchName, Dock.Right);
            patternLine.Children.Add(_groupSearchName);
            _patternError = new TextBlock { Foreground = Theme.Danger, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(_patternError, Dock.Right);
            patternLine.Children.Add(_patternError);
            _groupPattern = new TextBox { Margin = new Thickness(8, 0, 0, 0), FontFamily = Theme.Mono, Text = _settings == null ? "" : _settings.GroupPattern ?? "",
                ToolTip = "정의서에 넣을 그룹을 TABLE_NAME으로 거릅니다(대소문자 무시). 예: ^ORDER_  ·  STATUS$  — 비우면 모두" };
            AutomationProperties.SetName(_groupPattern, "그룹 정규식");
            _groupPattern.TextChanged += (s, e) => ValidatePattern();
            patternLine.Children.Add(_groupPattern);
            DockPanel.SetDock(patternLine, Dock.Bottom);
            bottom.Children.Add(patternLine);
            _aliasList = BuildAliasList();
            _aliasList.Margin = new Thickness(0, 6, 0, 0);
            bottom.Children.Add(_aliasList);
            Grid.SetRow(bottom, 3);
            panel.Children.Add(bottom);

            var rowSplitter = Splitter(GridResizeDirection.Rows, "SQL 편집기 높이 조절");
            Grid.SetRow(rowSplitter, 2);
            panel.Children.Add(rowSplitter);

            string initialFile;
            var initialSql = CodeSpecLogic.InitialSql(_settings, DbId, File.ReadAllText, out initialFile);
            _sqlFile = initialFile;
            if (_initialSqlOverride != null)
            {
                // 다른 DB에서 넘어온 편집기 글이 우선. 그 DB의 파일 기억은 쓰지 않는다
                initialSql = _initialSqlOverride;
                _sqlFile = null;
            }
            _sql = new TextBox
            {
                Text = initialSql,
                AcceptsReturn = true,
                AcceptsTab = true,
                FontFamily = Theme.Mono,
                FontSize = 12.5,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalContentAlignment = VerticalAlignment.Top,
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 8, 0, 0)
            };
            AutomationProperties.SetName(_sql, "SQL");
            _sql.TextChanged += (s, e) => ResetCheck();
            // .sql 파일을 끌어다 놓으면 편집기에 넣고 그 파일을 기억한다([저장…]이 그 파일을 먼저 보인다)
            ShellUi.EnableFileDrop(_sql, files =>
            {
                LoadSqlFile(files[0]);
                RefreshSqlFiles(_sqlFile);
            });
            Grid.SetRow(_sql, 1);
            panel.Children.Add(_sql);
            RefreshSqlFiles(_sqlFile);
            ValidatePattern();
            return panel;
        }

        private ListView BuildAliasList()
        {
            var view = new GridView();
            view.Columns.Add(new GridViewColumn { Header = "별칭", Width = 120, DisplayMemberBinding = new Binding("Name") });
            view.Columns.Add(new GridViewColumn { Header = "필수", Width = 48, DisplayMemberBinding = new Binding("RequiredText") });
            view.Columns.Add(new GridViewColumn { Header = "뜻", Width = 420, DisplayMemberBinding = new Binding("Description") });
            view.Columns.Add(new GridViewColumn { Header = "상태", Width = 90, DisplayMemberBinding = new Binding("State") });
            var list = new ListView { View = view, SelectionMode = SelectionMode.Single, Focusable = false };
            AutomationProperties.SetName(list, "별칭 계약");
            _aliasItems = CodeSpecContract.Aliases.Select(a => new AliasItem(a)).ToList();
            list.ItemsSource = _aliasItems;
            return list;
        }

        private FrameworkElement BuildOptions()
        {
            var document = CodeSpecLogic.DocumentFrom(_settings, Environment.UserName, DateTime.Today);
            var lastTemplatePath = _settings == null ? null : _settings.TemplatePath;
            var output = CodeSpecLogic.OutputFrom(_settings == null ? null : _settings.Output);
            var panel = new StackPanel();
            panel.Children.Add(Section("문서 (표지)"));
            var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _title = AddField(grid, "문서 제목", document.Title);
            _project = AddField(grid, "프로젝트", document.Project);
            _system = AddField(grid, "시스템/모듈", document.System);
            _author = AddField(grid, "작성자", document.Author);
            _version = AddField(grid, "버전", document.Version);
            _date = AddField(grid, "작성일", document.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            _description = AddField(grid, "설명", document.Description);
            _description.AcceptsReturn = true;
            _description.TextWrapping = TextWrapping.Wrap;
            _description.Height = 54;
            _description.VerticalContentAlignment = VerticalAlignment.Top;
            panel.Children.Add(grid);

            panel.Children.Add(Section("출력 형식", 16));
            var outputs = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
            _outputExcel = Radio("output", CodeSpecLogic.OutputLabel(CodeSpecOutput.Excel), output == CodeSpecOutput.Excel);
            _outputJson = Radio("output", CodeSpecLogic.OutputLabel(CodeSpecOutput.Json), output == CodeSpecOutput.Json);
            _outputCsv = Radio("output", CodeSpecLogic.OutputLabel(CodeSpecOutput.Csv), output == CodeSpecOutput.Csv);
            _outputJson.ToolTip = "doc · db · source · groups[].codes[] 구조의 JSON(다른 도구·LLM에 넘기기 좋음)";
            _outputCsv.ToolTip = "코드마다 한 줄, 계약 별칭 + 추가 속성 열(UTF-8 BOM — 엑셀에서 바로 열림)";
            foreach (var radio in new[] { _outputExcel, _outputJson, _outputCsv })
            {
                radio.Margin = new Thickness(0, 4, 14, 0);
                radio.Checked += (s, e) => UpdateOutput();
                outputs.Children.Add(radio);
            }
            panel.Children.Add(outputs);

            var templatePanel = new StackPanel();
            templatePanel.Children.Add(Section("템플릿 (엑셀)", 12));
            _defaultTemplate = Radio("template", "양식 (기본 양식 · 내 양식)", lastTemplatePath == null);
            _fileTemplate = Radio("template", "템플릿 파일", lastTemplatePath != null);
            templatePanel.Children.Add(_defaultTemplate);
            _templateBox = new ComboBox { Margin = new Thickness(20, 4, 0, 0) };
            AutomationProperties.SetName(_templateBox, "양식");
            _templateBox.SelectionChanged += (s, e) =>
            {
                if (!_refreshingTemplates)
                    _defaultTemplate.IsChecked = true;
                UpdateTemplateHint();
            };
            _templateBox.DropDownOpened += (s, e) => RefreshTemplates(SelectedTemplate.Id);
            templatePanel.Children.Add(_templateBox);
            _templateHint = DialogKit.Hint("", 3);
            _templateHint.Margin = new Thickness(20, 3, 0, 0);
            templatePanel.Children.Add(_templateHint);
            var templateButtons = new WrapPanel { Margin = new Thickness(14, 4, 0, 0) };
            var copy = SmallButton("복사해서 고치기", "고른 양식을 GCM 양식 폴더에 새 파일로 복사하고 엑셀에서 엽니다. 고쳐 저장하면 목록에 '내 양식'으로 보입니다");
            copy.Click += (s, e) => CopyTemplate();
            _openTemplate = SmallButton("엑셀에서 열기", "고른 내 양식을 엑셀에서 엽니다(기본 양식은 [복사해서 고치기]로 먼저 꺼내세요)");
            _openTemplate.Click += (s, e) => OpenTemplate();
            var folder = SmallButton("양식 폴더", "GCM 양식 폴더(templates\\gcm)를 탐색기로 엽니다. 여기에 넣은 .xlsx가 목록에 '내 양식'으로 보입니다");
            folder.Click += (s, e) => OpenFolder(EnsureFolder(_templateFolder, "양식"));
            foreach (var button in new[] { copy, _openTemplate, folder })
            {
                button.Margin = new Thickness(6, 0, 0, 4);
                templateButtons.Children.Add(button);
            }
            templatePanel.Children.Add(templateButtons);
            RefreshTemplates(_settings == null ? null : _settings.TemplateId);
            templatePanel.Children.Add(_fileTemplate);
            var fileLine = new DockPanel { Margin = new Thickness(20, 4, 0, 0) };
            var browse = SmallButton("찾아보기…", "템플릿 .xlsx 파일을 고릅니다");
            browse.Margin = new Thickness(6, 0, 0, 0);
            browse.Click += (s, e) => BrowseTemplate();
            DockPanel.SetDock(browse, Dock.Right);
            fileLine.Children.Add(browse);
            _templatePath = new TextBox { IsReadOnly = true, Text = lastTemplatePath ?? "", ToolTip = lastTemplatePath };
            AutomationProperties.SetName(_templatePath, "템플릿 파일");
            fileLine.Children.Add(_templatePath);
            templatePanel.Children.Add(fileLine);
            var llmButtons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            var llm = SmallButton("LLM 지침 복사", "LLM(Claude Code 등)에게 붙여 넣을 요청을 복사합니다 — GCM 양식 작성 지침 주소와 양식 폴더가 들어 있습니다");
            llm.Margin = new Thickness(0, 0, 6, 0);
            llm.Click += (s, e) => CopyLlmPrompt();
            var guide = SmallButton("지침 보기", CodeSpecTemplates.GuideUrl);
            guide.Margin = new Thickness(0);
            guide.Click += (s, e) => OpenUrl(CodeSpecTemplates.GuideUrl);
            llmButtons.Children.Add(llm);
            llmButtons.Children.Add(guide);
            templatePanel.Children.Add(llmButtons);
            _templatePanel = templatePanel;
            panel.Children.Add(templatePanel);
            UpdateOutput();

            return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        }

        private FrameworkElement BuildFooter()
        {
            var foot = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
            _create = FooterButton("만들기…", true);
            _create.IsDefault = false;
            _create.Margin = new Thickness(8, 0, 0, 0);
            _create.Click += (s, e) =>
            {
                if (_work != null)
                    _work.Cancel();
                else
                    Create();
            };
            _openFile = FooterButton("파일 열기", false);
            _openFolder = FooterButton("폴더 열기", false);
            _openFile.Visibility = _openFolder.Visibility = Visibility.Collapsed;
            _openFile.Click += (s, e) => Open(_lastOutput, false);
            _openFolder.Click += (s, e) => Open(_lastOutput, true);
            foreach (var button in new[] { _create, _openFolder, _openFile })
            {
                if (button != _create)
                    button.Margin = new Thickness(8, 0, 0, 0);
                DockPanel.SetDock(button, Dock.Right);
                foot.Children.Add(button);
            }
            _progress = new ProgressBar { Width = 90, Height = 6, IsIndeterminate = false, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            _progress.SetResourceReference(Control.ForegroundProperty, Theme.Accent);
            _progress.SetResourceReference(Control.BackgroundProperty, Theme.ControlBackground);
            DockPanel.SetDock(_progress, Dock.Left);
            foot.Children.Add(_progress);
            _status = Theme.Text("", Theme.SecondaryText);
            _status.TextWrapping = TextWrapping.Wrap;
            _status.VerticalAlignment = VerticalAlignment.Center;
            foot.Children.Add(_status);
            return foot;
        }

        // ================= 출력 형식·양식 =================

        private CodeSpecOutput SelectedOutput
        {
            get
            {
                if (_outputJson != null && _outputJson.IsChecked == true)
                    return CodeSpecOutput.Json;
                if (_outputCsv != null && _outputCsv.IsChecked == true)
                    return CodeSpecOutput.Csv;
                return CodeSpecOutput.Excel;
            }
        }

        private void UpdateOutput()
        {
            if (_templatePanel != null)
                _templatePanel.IsEnabled = SelectedOutput == CodeSpecOutput.Excel;
        }

        private SpecTemplate SelectedTemplate
        {
            get { return _templateBox.SelectedItem as SpecTemplate ?? CodeSpecTemplates.All[0]; }
        }

        private void UpdateTemplateHint()
        {
            if (_templateHint != null)
                _templateHint.Text = SelectedTemplate.Description;
            if (_openTemplate != null)
                _openTemplate.IsEnabled = !SelectedTemplate.IsBuiltIn;
        }

        private void RefreshTemplates(string selectId)
        {
            _refreshingTemplates = true;
            try
            {
                _templates = CodeSpecTemplates.List(_templateFolder);
                _templateBox.ItemsSource = _templates;
                _templateBox.SelectedItem = CodeSpecTemplates.Find(_templates, selectId);
            }
            finally
            {
                _refreshingTemplates = false;
            }
            UpdateTemplateHint();
        }

        private void BrowseTemplate()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "템플릿 파일 고르기",
                Filter = "Excel 통합 문서 (*.xlsx)|*.xlsx",
                CheckFileExists = true
            };
            if (!string.IsNullOrEmpty(_templatePath.Text))
                dialog.InitialDirectory = Path.GetDirectoryName(_templatePath.Text);
            else if (!string.IsNullOrEmpty(_templateFolder) && Directory.Exists(_templateFolder))
                dialog.InitialDirectory = _templateFolder;
            if (dialog.ShowDialog(_window) != true)
                return;
            _templatePath.Text = dialog.FileName;
            _templatePath.ToolTip = dialog.FileName;
            _fileTemplate.IsChecked = true;
        }

        private void CopyTemplate()
        {
            var folder = EnsureFolder(_templateFolder, "양식");
            if (folder == null)
                return;
            var source = SelectedTemplate;
            try
            {
                var name = TableSpecTemplates.CopyName(folder, source.FileName);
                var path = Path.Combine(folder, name);
                File.WriteAllBytes(path, source.Create());
                RefreshTemplates(TableSpecTemplates.UserPrefix + name);
                _defaultTemplate.IsChecked = true;
                SetStatus("'" + source.Name + "'을(를) 내 양식으로 복사했습니다: " + path + " — 엑셀에서 고쳐 저장한 뒤 [만들기…]를 누르세요.", false);
                Open(path, false);
            }
            catch (Exception ex)
            {
                SetStatus("양식을 복사하지 못했습니다: " + ex.Message, true);
            }
        }

        private void OpenTemplate()
        {
            var template = SelectedTemplate;
            if (template.IsBuiltIn)
            {
                SetStatus("기본 양식은 직접 열 수 없습니다. [복사해서 고치기]로 내 양식을 만든 뒤 여세요.", false);
                return;
            }
            Open(template.Path, false);
        }

        private void CopyLlmPrompt()
        {
            var template = SelectedTemplate;
            var text = CodeSpecLogic.LlmPrompt(CodeSpecTemplates.GuideUrl, CodeSpecTemplates.GuideMarkdownUrl, _templateFolder, template.IsBuiltIn ? null : template.Path);
            if (DialogKit.TryCopy(text))
                SetStatus("LLM에게 붙여 넣을 요청을 복사했습니다. 끝에 원하는 모양을 적어 보내면 GCM 양식 폴더에 새 양식을 만듭니다(목록을 펼치면 보임).", false);
            else
                SetStatus("클립보드를 쓸 수 없습니다. 잠시 뒤 다시 누르세요.", true);
        }

        // ================= 저장한 SQL =================

        private void RefreshSqlFiles(string selectPath)
        {
            _refreshingSqlFiles = true;
            try
            {
                var items = new List<SqlFileItem> { new SqlFileItem(null) };
                items.AddRange(CodeSpecLogic.ListSqlFiles(_sqlFolder).Select(p => new SqlFileItem(p)));
                _sqlFiles.ItemsSource = items;
                _sqlFiles.SelectedItem = items.FirstOrDefault(i => i.Path != null && SqlFileLogic.SamePath(i.Path, selectPath)) ?? items[0];
            }
            finally
            {
                _refreshingSqlFiles = false;
            }
        }

        private void LoadSqlFile(string path)
        {
            try
            {
                _sql.Text = File.ReadAllText(path);
                _sqlFile = path;
                SetStatus("'" + Path.GetFileName(path) + "'을(를) 불러왔습니다.", false);
            }
            catch (Exception ex)
            {
                SetStatus("SQL 파일을 읽지 못했습니다: " + ex.Message, true);
            }
        }

        private void SaveSql()
        {
            var folder = EnsureFolder(_sqlFolder, "SQL");
            if (folder == null)
                return;
            var save = new Microsoft.Win32.SaveFileDialog
            {
                Title = "SQL 저장",
                Filter = "SQL 파일 (*.sql)|*.sql",
                InitialDirectory = _sqlFile != null ? Path.GetDirectoryName(_sqlFile) : folder,
                FileName = _sqlFile != null ? Path.GetFileName(_sqlFile) : "GCM_" + (_profile == null ? "" : _profile.Name) + ".sql",
                OverwritePrompt = true
            };
            if (save.ShowDialog(_window) != true)
                return;
            try
            {
                File.WriteAllText(save.FileName, _sql.Text, new UTF8Encoding(false));
                _sqlFile = save.FileName;
                RefreshSqlFiles(_sqlFile);
                SetStatus("SQL을 저장했습니다: " + save.FileName, false);
            }
            catch (Exception ex)
            {
                SetStatus("SQL을 저장하지 못했습니다: " + ex.Message, true);
            }
        }

        // ================= 열 확인 =================

        private void ResetCheck()
        {
            if (_aliasItems == null)
                return;
            foreach (var item in _aliasItems)
                item.State = "";
            if (_checkText != null)
                _checkText.Text = "";
        }

        private async void Check()
        {
            if (_work != null)
                return;
            if (string.IsNullOrWhiteSpace(_sql.Text))
            {
                SetStatus("SQL을 쓰세요.", true);
                return;
            }
            if (_isRunning())
            {
                SetStatus(TableSpecLogic.BusyText(_profile == null ? "" : _profile.Name), true);
                return;
            }
            _work = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                SetStatus("열을 확인하는 중…", false);
                var check = await CodeSpecQueries.ProbeAsync(_session, _sql.Text, _work.Token);
                if (_closed)
                    return;
                ShowCheck(check);
                SetStatus(check.Ok ? "별칭이 맞습니다. [만들기…]를 누르세요." : "별칭이 빠졌습니다. SELECT 목록에 AS 별칭을 붙이세요.", !check.Ok);
            }
            catch (OperationCanceledException)
            {
                if (!_closed)
                    SetStatus("취소했습니다.", false);
            }
            catch (Exception ex)
            {
                if (!_closed)
                    SetStatus("SQL을 실행하지 못했습니다: " + DbSession.DescribeError(ex), true);
            }
            finally
            {
                _work.Dispose();
                _work = null;
                if (!_closed)
                    SetBusy(false);
            }
        }

        private void ShowCheck(CodeSpecCheck check)
        {
            foreach (var item in _aliasItems)
                item.State = check.Has(item.Name) ? "✓ 있음" : item.Required ? "✕ 빠짐" : "– 없음";
            _checkText.Text = CodeSpecLogic.CheckText(check);
            _checkText.ToolTip = _checkText.Text;
            if (check.Ok)
                Theme.Foreground(_checkText, Theme.SecondaryText);
            else
                _checkText.Foreground = Theme.Danger;
        }

        private void ValidatePattern()
        {
            if (_groupPattern == null || _patternError == null)
                return;
            string error;
            TableSpecLogic.Pattern(_groupPattern.Text, true, out error);
            _patternError.Text = error == null ? "" : "정규식 오류";
            _patternError.ToolTip = error;
            if (error == null)
                _groupPattern.SetResourceReference(Control.BorderBrushProperty, Theme.Border);
            else
                _groupPattern.BorderBrush = Theme.Danger;
        }

        // ================= 정의서 만들기 =================

        private async void Create()
        {
            if (_work != null)
                return;
            try
            {
                if (string.IsNullOrWhiteSpace(_sql.Text))
                {
                    SetStatus("SQL을 쓰세요.", true);
                    _sql.Focus();
                    return;
                }
                var date = TableSpecLogic.ParseDate(_date.Text);
                if (!date.HasValue)
                {
                    SetStatus("작성일은 2026-10-10 꼴로 쓰세요.", true);
                    _date.Focus();
                    return;
                }
                string patternError;
                var pattern = TableSpecLogic.Pattern(_groupPattern.Text, true, out patternError);
                if (patternError != null)
                {
                    SetStatus(patternError, true);
                    _groupPattern.Focus();
                    return;
                }
                var output = SelectedOutput;
                byte[] template = null;
                string templatePath = null;
                if (output == CodeSpecOutput.Excel)
                {
                    if (_fileTemplate.IsChecked == true)
                    {
                        templatePath = _templatePath.Text;
                        if (string.IsNullOrWhiteSpace(templatePath))
                        {
                            SetStatus("[찾아보기…]로 템플릿 파일을 고르거나 기본 양식을 쓰세요.", true);
                            return;
                        }
                        template = TableSpecTemplates.ReadShared(templatePath);
                    }
                    else
                    {
                        template = SelectedTemplate.Create();
                    }
                }
                var document = new TableSpecDocument
                {
                    Title = _title.Text,
                    Project = _project.Text,
                    System = _system.Text,
                    Author = _author.Text,
                    Version = _version.Text,
                    Date = date.Value,
                    Description = _description.Text
                };
                var save = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "GCM 정의서 저장",
                    Filter = CodeSpecLogic.SaveFilter(output),
                    FileName = CodeSpecLogic.DefaultFileName(document.Title, _profile == null ? null : _profile.Name, output, document.Date),
                    OverwritePrompt = true
                };
                if (_folder != null)
                    save.InitialDirectory = _folder;
                if (save.ShowDialog(_window) != true)
                    return;
                if (_isRunning())
                {
                    SetStatus(TableSpecLogic.BusyText(_profile == null ? "" : _profile.Name), true);
                    return;
                }
                _folder = Path.GetDirectoryName(save.FileName);
                SaveSettings(document, templatePath, output);
                await Run(document, output, template, pattern, save.FileName);
            }
            catch (Exception ex)
            {
                SetStatus("정의서를 만들지 못했습니다: " + Describe(ex), true);
            }
        }

        private async Task Run(TableSpecDocument document, CodeSpecOutput output, byte[] template, System.Text.RegularExpressions.Regex pattern, string path)
        {
            _work = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                SetStatus("SQL을 실행하는 중…", false);
                var sql = _sql.Text;
                var table = await CodeSpecQueries.LoadAsync(_session, sql, _work.Token);
                var check = CodeSpecContract.Check(table.Columns);
                ShowCheck(check);
                if (!check.Ok)
                {
                    SetStatus(CodeSpecLogic.CheckText(check), true);
                    return;
                }
                var version = await TableSpecQueries.LoadServerVersionAsync(_session, _work.Token);
                _work.Token.ThrowIfCancellationRequested();
                var data = CodeSpecModel.Build(table, _sqlFile == null ? "" : Path.GetFileName(_sqlFile), CodeSpecQueries.Clean(sql));
                var total = data.Groups.Count;
                if (pattern != null)
                {
                    var kept = CodeSpecModel.Filter(data.Groups, pattern, _groupSearchName.IsChecked == true);
                    data.Groups.Clear();
                    data.Groups.AddRange(kept);
                }
                // 0행·0그룹이면 빈 정의서가 되므로 만들지 않고 이유를 알린다(공장 코드가 다른 환경에서 WHERE가 아무 행도 못 고른 경우 등)
                var empty = CodeSpecLogic.EmptyText(table.Rows.Count, data.Groups.Count, pattern != null);
                if (empty != null)
                {
                    SetStatus(empty, true);
                    return;
                }
                SetStatus(CodeSpecLogic.OutputLabel(output) + " 만드는 중… (" + CodeSpecLogic.FilterText(data.Groups.Count, total, data.CodeCount, pattern != null) + ")", false);
                var profile = _profile;
                await Task.Run(() =>
                {
                    switch (output)
                    {
                        case CodeSpecOutput.Json:
                            File.WriteAllText(path, CodeSpecModel.Json(document, profile, data, version), new UTF8Encoding(false));
                            break;
                        case CodeSpecOutput.Csv:
                            File.WriteAllBytes(path, CodeSpecModel.CsvBytes(data));
                            break;
                        default:
                            using (var stream = new MemoryStream())
                            {
                                ExcelTemplate.Render(template, CodeSpecModel.Context(document, profile, data, version), stream);
                                File.WriteAllBytes(path, stream.ToArray());
                            }
                            break;
                    }
                }, _work.Token);
                if (_closed)
                    return;
                _lastOutput = path;
                SetStatus(CodeSpecLogic.DoneText(data.Groups.Count, data.CodeCount, output, path), false);
                _openFile.Visibility = _openFolder.Visibility = Visibility.Visible;
            }
            catch (OperationCanceledException)
            {
                if (!_closed)
                    SetStatus("취소했습니다.", false);
            }
            catch (Exception ex)
            {
                if (!_closed)
                    SetStatus(ex is TemplateException ? "템플릿 오류 — " + ex.Message : "정의서를 만들지 못했습니다: " + Describe(ex), true);
            }
            finally
            {
                _work.Dispose();
                _work = null;
                if (!_closed)
                    SetBusy(false);
            }
        }

        private void SaveSettings(TableSpecDocument document, string templatePath, CodeSpecOutput output)
        {
            if (_saveSettings == null)
                return;
            try
            {
                var settings = CodeSpecLogic.SettingsFrom(_settings, document, SelectedTemplate.Id, templatePath, _folder, output, _sql.Text, DbId, _sqlFile,
                    _groupPattern.Text, _groupSearchName.IsChecked == true);
                _saveSettings(CodeSpecLogic.SerializeSettings(settings));
            }
            catch (Exception)
            {
                // 설정 파일을 쓸 수 없는 경우 — 입력값을 기억하지 못할 뿐이다
            }
        }

        private static string Describe(Exception ex)
        {
            if (ex is IOException || ex is UnauthorizedAccessException)
                return ex.Message + " (엑셀 등에서 열어 둔 파일이면 닫은 뒤 다시 하세요)";
            return DbSession.DescribeError(ex);
        }

        private void SetBusy(bool busy)
        {
            _inputs.IsEnabled = !busy;
            _create.Content = busy ? "취소" : "만들기…";
            _progress.IsIndeterminate = busy;
            _progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (busy)
                _openFile.Visibility = _openFolder.Visibility = Visibility.Collapsed;
        }

        private void SetStatus(string text, bool error)
        {
            if (_closed)
                return;
            _status.Text = text ?? "";
            if (error)
                _status.Foreground = Theme.Danger;
            else
                Theme.Foreground(_status, Theme.SecondaryText);
        }

        // 폴더를 만들어 돌려준다. 데이터 폴더를 모르거나 만들 수 없으면 상태에 알리고 null
        private string EnsureFolder(string folder, string what)
        {
            if (string.IsNullOrEmpty(folder))
            {
                SetStatus("플러그인 데이터 폴더를 알 수 없어 " + what + " 폴더를 쓸 수 없습니다.", true);
                return null;
            }
            try
            {
                Directory.CreateDirectory(folder);
                return folder;
            }
            catch (Exception ex)
            {
                SetStatus(what + " 폴더를 만들지 못했습니다: " + ex.Message, true);
                return null;
            }
        }

        private void OpenFolder(string folder)
        {
            if (folder == null)
                return;
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                SetStatus("폴더를 열지 못했습니다: " + ex.Message, true);
            }
        }

        private void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                SetStatus("열지 못했습니다: " + ex.Message, true);
            }
        }

        private void Open(string path, bool folder)
        {
            try
            {
                if (string.IsNullOrEmpty(path))
                    return;
                if (folder)
                    Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
                else
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                SetStatus("열지 못했습니다: " + ex.Message, true);
            }
        }

        // ================= 모양 도우미 =================

        // 분할선: 평소에는 바탕색, 마우스를 올리면 강조색(SqlWorkspace와 같은 모양). 끌어서 이웃 칸의 크기를 바꾼다
        private static GridSplitter Splitter(GridResizeDirection direction, string name)
        {
            var splitter = new GridSplitter
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                ResizeDirection = direction,
                ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                ShowsPreview = false,
                Focusable = false
            };
            if (direction == GridResizeDirection.Columns)
                splitter.Width = 5;
            else
                splitter.Height = 5;
            Theme.Background(splitter, Theme.SurfaceBackground);
            splitter.MouseEnter += (s, e) => Theme.Background(splitter, Theme.Accent);
            splitter.MouseLeave += (s, e) => Theme.Background(splitter, Theme.SurfaceBackground);
            AutomationProperties.SetName(splitter, name);
            return splitter;
        }

        private static Button FooterButton(string text, bool primary)
        {
            var button = new Button
            {
                Content = text,
                MinWidth = 96,
                Height = 32,
                Padding = new Thickness(14, 0, 14, 0),
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            if (primary)
            {
                button.FontWeight = FontWeights.SemiBold;
                button.SetResourceReference(Control.BorderBrushProperty, Theme.Accent);
            }
            AutomationProperties.SetName(button, text);
            return button;
        }

        private static TextBlock Label(string text)
        {
            var label = Theme.Text(text, Theme.SecondaryText);
            label.VerticalAlignment = VerticalAlignment.Center;
            return label;
        }

        private static TextBlock Section(string text, double top = 0)
        {
            var block = Theme.Text(text);
            block.FontWeight = FontWeights.SemiBold;
            block.Margin = new Thickness(0, top, 0, 0);
            return block;
        }

        private static CheckBox Check(string text, bool isChecked)
        {
            var check = Theme.Foreground(new CheckBox { Content = text, IsChecked = isChecked, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0) });
            AutomationProperties.SetName(check, text);
            return check;
        }

        private static RadioButton Radio(string group, string text, bool isChecked)
        {
            var radio = Theme.Foreground(new RadioButton { Content = text, GroupName = "codeSpec_" + group, IsChecked = isChecked, Margin = new Thickness(0, 6, 0, 0) });
            AutomationProperties.SetName(radio, text);
            return radio;
        }

        private static Button SmallButton(string text, string tooltip)
        {
            var button = new Button { Content = text, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(6, 0, 0, 0) };
            if (tooltip != null)
                button.ToolTip = tooltip;
            return button;
        }

        private static TextBox AddField(Grid grid, string label, string value)
        {
            var row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var text = Label(label);
            text.Margin = new Thickness(0, 6, 0, 0);
            Grid.SetRow(text, row);
            grid.Children.Add(text);
            var box = new TextBox { Text = value ?? "", Margin = new Thickness(0, 6, 0, 0) };
            AutomationProperties.SetName(box, label);
            Grid.SetRow(box, row);
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);
            return box;
        }

        /// <summary>대상 DB 콤보 한 줄: "이름 — user@host:port/service (Oracle)".</summary>
        private sealed class TargetItem
        {
            public TargetItem(OracleConnectionProfile profile)
            {
                Profile = profile;
            }

            public OracleConnectionProfile Profile { get; }

            public override string ToString()
            {
                return ConnectionManagerLogic.DisplayName(Profile) + " — " + ConnectionManagerLogic.Address(Profile) + " (" + DbProviders.DisplayNameOf(Profile) + ")";
            }
        }

        /// <summary>[저장한 SQL] 목록 한 줄. Path가 null이면 "(편집기의 SQL)".</summary>
        private sealed class SqlFileItem
        {
            public SqlFileItem(string path)
            {
                Path = path;
            }

            public string Path { get; }

            public override string ToString()
            {
                return Path == null ? "(편집기의 SQL — 저장하려면 [저장…])" : CodeSpecLogic.SqlFileLabel(Path);
            }
        }

        /// <summary>별칭 계약 표 한 줄(검사 뒤 상태가 바뀐다).</summary>
        private sealed class AliasItem : System.ComponentModel.INotifyPropertyChanged
        {
            private string _state = "";

            public AliasItem(CodeAlias alias)
            {
                Name = alias.Name;
                Required = alias.Required;
                Description = alias.Description;
            }

            public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

            public string Name { get; }
            public bool Required { get; }
            public string RequiredText { get { return Required ? "필수" : ""; } }
            public string Description { get; }

            public string State
            {
                get { return _state; }
                set
                {
                    if (_state == value)
                        return;
                    _state = value;
                    if (PropertyChanged != null)
                        PropertyChanged(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(State)));
                }
            }
        }
    }
}

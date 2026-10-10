using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;

namespace MyPlugin
{
    /// <summary>
    /// 테이블 정의서 만들기 창(모달 — 만드는 동안 같은 DB 세션으로 다른 문장을 실행하지 않게).
    /// 왼쪽: 대상 고르기 — 스키마, [뷰 포함], 정규식(대소문자 무시·주석도 찾기), 체크 목록(Ctrl·Shift로 여러 줄 고른 뒤 Space로 체크),
    /// [일치 모두 체크]·[일치 모두 해제], [체크한 것만 보기](모든 스키마). 스키마를 바꿔도 다른 스키마의 체크는 남는다.
    /// 오른쪽: 문서 정보(표지에 쓰는 제목·프로젝트·시스템/모듈·작성자·버전·작성일·설명),
    /// 템플릿 — [양식] 목록(기본 양식 + 사용자 양식 폴더의 "내 양식", 펼칠 때마다 폴더를 다시 읽음) · [복사해서 고치기] · [엑셀에서 열기] · [양식 폴더],
    /// 또는 [템플릿 파일], 그리고 [LLM 지침 복사]·[지침 보기](LLM에게 새 양식을 만들게 할 때).
    /// 아래: 상태, [만들기…](저장 위치를 물은 뒤 조회 → 엑셀 — 만드는 동안은 [취소]), 다 만들면 [파일 열기]·[폴더 열기]. 세 버튼은 같은 크기.
    /// 닫기 버튼은 두지 않는다 — 제목 표시줄 ✕ 또는 Esc(만드는 중이 아닐 때)로 닫는다.
    /// [만들기…] 때의 문서 정보·고른 양식·템플릿 파일·저장 폴더는 플러그인 설정(TableSpecLogic.SettingKey)에 저장하고,
    /// 창을 다시 열면(Folderss를 다시 켠 뒤에도) 그 값으로 채운다.
    /// </summary>
    internal sealed class TableSpecDialog
    {
        private readonly OracleConnectionProfile _profile;
        private readonly DbSession _session;
        private readonly TableSpecRequest _request;
        private readonly Func<bool> _isRunning;
        private readonly Action<string> _saveSettings;
        // 지난번 [만들기…] 때의 입력값(없으면 null — 기본값으로 연다)
        private readonly TableSpecSettings _settings;
        private readonly Window _window;
        private readonly CancellationTokenSource _closing = new CancellationTokenSource();
        private readonly Dictionary<string, List<TableSpecItem>> _byOwner = new Dictionary<string, List<TableSpecItem>>(StringComparer.Ordinal);
        private readonly Dictionary<string, Task> _loading = new Dictionary<string, Task>(StringComparer.Ordinal);
        private readonly HashSet<TableKey> _checked = new HashSet<TableKey>();

        private ComboBox _schema;
        private CheckBox _includeViews;
        private TextBox _pattern;
        private CheckBox _ignoreCase;
        private CheckBox _searchComment;
        private CheckBox _onlyChecked;
        private TextBlock _matchText;
        private TextBlock _patternError;
        private TextBlock _selectionText;
        private Button _checkMatches;
        private Button _uncheckMatches;
        private ListView _list;
        private TextBox _title;
        private TextBox _project;
        private TextBox _system;
        private TextBox _author;
        private TextBox _version;
        private TextBox _date;
        private TextBox _description;
        private RadioButton _defaultTemplate;
        private ComboBox _templateBox;
        private TextBlock _templateHint;
        private Button _openTemplate;
        // 사용자 양식 폴더(플러그인 데이터 폴더\templates). 모르면 null — 기본 양식만 쓴다
        private readonly string _templateFolder;
        private List<SpecTemplate> _templates;
        // 목록을 다시 읽는 중(고른 양식이 바뀌어도 [양식] 라디오를 건드리지 않게)
        private bool _refreshingTemplates;
        private RadioButton _fileTemplate;
        private TextBox _templatePath;
        private TextBlock _status;
        private ProgressBar _progress;
        private Button _create;
        private Button _openFile;
        private Button _openFolder;
        private FrameworkElement _inputs;

        private CancellationTokenSource _work;
        private string _lastOutput;
        // 저장 대화상자를 처음 열 폴더(지난번 저장 폴더, 이 창에서 저장하면 그 폴더)
        private string _folder;
        private bool _closed;
        // 여러 항목을 한꺼번에 체크하는 중(끝난 뒤 한 번만 센다)
        private bool _bulk;

        private TableSpecDialog(Window owner, OracleConnectionProfile profile, DbSession session, TableSpecRequest request, Func<bool> isRunning,
            Func<string> loadSettings, Action<string> saveSettings, string templateFolder)
        {
            _templateFolder = templateFolder;
            _profile = profile;
            _session = session;
            _request = request;
            _isRunning = isRunning ?? (() => false);
            _saveSettings = saveSettings;
            _settings = TableSpecLogic.ParseSettings(loadSettings == null ? null : loadSettings());
            _folder = _settings == null ? null : _settings.Folder;
            _window = new Window
            {
                Title = DialogKit.TitleFor(TableSpecLogic.Title, profile),
                Width = 1080,
                // Folderss 테마는 입력칸이 높다(29px) — 오른쪽 문서·템플릿 칸이 스크롤 없이 들어가는 높이
                Height = 840,
                MinWidth = 780,
                MinHeight = 480,
                ShowInTaskbar = false,
                ResizeMode = ResizeMode.CanResizeWithGrip
            };
            Theme.ApplyWindow(_window);
            DialogKit.SetOwner(_window, owner);
            _window.Content = Build();
            _window.Loaded += Window_Loaded;
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
        /// 창을 띄운다(모달). isRunning: 그 DB에서 다른 문장을 실행 중인지(만들기 전에 확인).
        /// loadSettings·saveSettings: 지난번 입력값(TableSpecLogic.SettingKey의 글)을 읽고 쓴다. templateFolder: 사용자 양식 폴더(모르면 null).
        /// </summary>
        public static void Show(Window owner, OracleConnectionProfile profile, DbSession session, TableSpecRequest request, Func<bool> isRunning,
            Func<string> loadSettings, Action<string> saveSettings, string templateFolder)
        {
            new TableSpecDialog(owner, profile, session, request ?? new TableSpecRequest(), isRunning, loadSettings, saveSettings, templateFolder)._window.ShowDialog();
        }

        // ================= 만들기 =================

        private FrameworkElement Build()
        {
            var root = new DockPanel { Margin = new Thickness(14, 12, 14, 12) };

            var head = new StackPanel();
            head.Children.Add(DialogKit.Target(_profile, ConnectionManagerLogic.Address(_profile)));
            head.Children.Add(DialogKit.Hint("트리에서 고른 테이블은 체크되어 있습니다. 스키마를 바꿔 가며 정규식이나 여러 줄 선택으로 대상을 체크하고, 문서 정보를 채운 뒤 [만들기…]를 누르세요.", 6));
            DockPanel.SetDock(head, Dock.Top);
            root.Children.Add(head);

            var foot = BuildFooter();
            DockPanel.SetDock(foot, Dock.Bottom);
            root.Children.Add(foot);

            var body = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(290) });
            var targets = BuildTargets();
            body.Children.Add(targets);
            var options = BuildOptions();
            Grid.SetColumn(options, 2);
            body.Children.Add(options);
            _inputs = body;
            root.Children.Add(body);
            return root;
        }

        private FrameworkElement BuildTargets()
        {
            var panel = new DockPanel();

            var top = new StackPanel();
            var line1 = new DockPanel();
            _selectionText = Theme.Text("", Theme.Accent);
            _selectionText.FontWeight = FontWeights.SemiBold;
            _selectionText.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(_selectionText, Dock.Right);
            line1.Children.Add(_selectionText);
            var schemaLabel = Label("스키마");
            line1.Children.Add(schemaLabel);
            _schema = new ComboBox { Width = 200, Margin = new Thickness(8, 0, 12, 0), IsEditable = false };
            AutomationProperties.SetName(_schema, "스키마");
            _schema.SelectionChanged += Schema_SelectionChanged;
            line1.Children.Add(_schema);
            _includeViews = Check("뷰 포함", _request.IncludeViews);
            _includeViews.Checked += (s, e) => RefreshView();
            _includeViews.Unchecked += (s, e) => RefreshView();
            line1.Children.Add(_includeViews);
            top.Children.Add(line1);

            var line2 = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var patternLabel = Label("정규식");
            line2.Children.Add(patternLabel);
            _searchComment = Check("주석도 찾기", false);
            _searchComment.Margin = new Thickness(10, 0, 0, 0);
            _ignoreCase = Check("대소문자 무시", true);
            _ignoreCase.Margin = new Thickness(10, 0, 0, 0);
            DockPanel.SetDock(_searchComment, Dock.Right);
            DockPanel.SetDock(_ignoreCase, Dock.Right);
            line2.Children.Add(_searchComment);
            line2.Children.Add(_ignoreCase);
            _pattern = new TextBox { Margin = new Thickness(8, 0, 0, 0), FontFamily = Theme.Mono, ToolTip = "예: ^TB_  ·  _(M|D)$  ·  ^(ORDER|ITEM)  — 비우면 모두" };
            AutomationProperties.SetName(_pattern, "정규식");
            _pattern.TextChanged += (s, e) => RefreshView();
            _ignoreCase.Checked += (s, e) => RefreshView();
            _ignoreCase.Unchecked += (s, e) => RefreshView();
            _searchComment.Checked += (s, e) => RefreshView();
            _searchComment.Unchecked += (s, e) => RefreshView();
            line2.Children.Add(_pattern);
            top.Children.Add(line2);

            var line3 = new DockPanel { Margin = new Thickness(0, 8, 0, 6) };
            _checkMatches = SmallButton("일치 모두 체크", "지금 목록에 보이는 항목을 모두 체크합니다");
            _uncheckMatches = SmallButton("일치 모두 해제", "지금 목록에 보이는 항목의 체크를 모두 풉니다");
            _checkMatches.Click += (s, e) => SetChecked(CurrentItems(), true);
            _uncheckMatches.Click += (s, e) => SetChecked(CurrentItems(), false);
            _onlyChecked = Check("체크한 것만 보기 (모든 스키마)", false);
            _onlyChecked.Margin = new Thickness(12, 0, 0, 0);
            _onlyChecked.Checked += (s, e) => RefreshView();
            _onlyChecked.Unchecked += (s, e) => RefreshView();
            foreach (var element in new FrameworkElement[] { _onlyChecked, _uncheckMatches, _checkMatches })
            {
                DockPanel.SetDock(element, Dock.Right);
                line3.Children.Add(element);
            }
            var matchLine = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _matchText = Theme.Text("", Theme.SecondaryText);
            matchLine.Children.Add(_matchText);
            _patternError = new TextBlock { Foreground = Theme.Danger, Margin = new Thickness(10, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            matchLine.Children.Add(_patternError);
            line3.Children.Add(matchLine);
            top.Children.Add(line3);
            DockPanel.SetDock(top, Dock.Top);
            panel.Children.Add(top);

            var hint = DialogKit.Hint("Ctrl·Shift로 여러 줄을 고른 뒤 Space로 체크·해제 · 두 번 누르면 체크·해제 · Ctrl+A 모두 고르기", 6);
            DockPanel.SetDock(hint, Dock.Bottom);
            panel.Children.Add(hint);

            _list = BuildList();
            panel.Children.Add(_list);
            return panel;
        }

        private ListView BuildList()
        {
            var view = new GridView();
            var check = new FrameworkElementFactory(typeof(CheckBox));
            check.SetBinding(ToggleButton.IsCheckedProperty, new Binding("IsChecked") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            check.SetValue(UIElement.FocusableProperty, false);
            check.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            view.Columns.Add(new GridViewColumn { Width = 34, CellTemplate = new DataTemplate { VisualTree = check } });
            view.Columns.Add(new GridViewColumn { Header = "스키마", Width = 84, DisplayMemberBinding = new Binding("Owner") });
            view.Columns.Add(new GridViewColumn { Header = "이름", Width = 200, DisplayMemberBinding = new Binding("Name") });
            view.Columns.Add(new GridViewColumn { Header = "종류", Width = 48, DisplayMemberBinding = new Binding("TypeLabel") });
            var rows = new FrameworkElementFactory(typeof(TextBlock));
            rows.SetBinding(TextBlock.TextProperty, new Binding("RowsText"));
            rows.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Right);
            rows.SetValue(FrameworkElement.WidthProperty, 72.0);
            view.Columns.Add(new GridViewColumn { Header = "행 수(통계)", Width = 88, CellTemplate = new DataTemplate { VisualTree = rows } });
            view.Columns.Add(new GridViewColumn { Header = "주석", Width = 260, DisplayMemberBinding = new Binding("Comment") });

            var list = new ListView { View = view, SelectionMode = SelectionMode.Extended };
            AutomationProperties.SetName(list, "대상 테이블");
            VirtualizingPanel.SetIsVirtualizing(list, true);
            VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
            list.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None)
                {
                    e.Handled = true;
                    var selected = list.SelectedItems.OfType<TableSpecItem>().ToList();
                    if (selected.Count > 0)
                        SetChecked(selected, selected.Any(i => !i.IsChecked));
                }
            };
            list.MouseDoubleClick += (s, e) =>
            {
                var source = e.OriginalSource as DependencyObject;
                if (ShellUi.FindAncestor<CheckBox>(source, list) != null)
                    return;
                var container = ShellUi.FindAncestor<ListViewItem>(source, list);
                var item = container == null ? null : container.DataContext as TableSpecItem;
                if (item != null)
                    SetChecked(new[] { item }, !item.IsChecked);
            };
            return list;
        }

        private FrameworkElement BuildOptions()
        {
            var document = TableSpecLogic.DocumentFrom(_settings, Environment.UserName, DateTime.Today);
            var lastTemplatePath = _settings == null ? null : _settings.TemplatePath;
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

            panel.Children.Add(Section("템플릿 (엑셀)", 16));
            _defaultTemplate = Radio("양식 (기본 양식 · 내 양식)", lastTemplatePath == null);
            _fileTemplate = Radio("템플릿 파일", lastTemplatePath != null);
            panel.Children.Add(_defaultTemplate);
            // 기본 양식 + 사용자 양식 폴더의 .xlsx. 펼칠 때마다 폴더를 다시 읽는다(엑셀·LLM이 새로 만든 양식이 바로 보이게)
            _templateBox = new ComboBox { Margin = new Thickness(20, 4, 0, 0) };
            AutomationProperties.SetName(_templateBox, "양식");
            _templateBox.SelectionChanged += (s, e) =>
            {
                if (!_refreshingTemplates)
                    _defaultTemplate.IsChecked = true;
                UpdateTemplateHint();
            };
            _templateBox.DropDownOpened += (s, e) => RefreshTemplates(SelectedTemplate.Id);
            panel.Children.Add(_templateBox);
            _templateHint = DialogKit.Hint("", 3);
            _templateHint.Margin = new Thickness(20, 3, 0, 0);
            panel.Children.Add(_templateHint);
            var templateButtons = new WrapPanel { Margin = new Thickness(14, 4, 0, 0) };
            var copy = SmallButton("복사해서 고치기", "고른 양식을 양식 폴더에 새 파일로 복사하고 엑셀에서 엽니다. 고쳐 저장하면 목록에 '내 양식'으로 보입니다");
            copy.Click += (s, e) => CopyTemplate();
            _openTemplate = SmallButton("엑셀에서 열기", "고른 내 양식을 엑셀에서 엽니다(기본 양식은 [복사해서 고치기]로 먼저 꺼내세요)");
            _openTemplate.Click += (s, e) => OpenTemplate();
            var folder = SmallButton("양식 폴더", "사용자 양식 폴더를 탐색기로 엽니다. 여기에 넣은 .xlsx가 목록에 '내 양식'으로 보입니다");
            folder.Click += (s, e) => OpenTemplateFolder();
            foreach (var button in new[] { copy, _openTemplate, folder })
            {
                button.Margin = new Thickness(6, 0, 0, 4);
                templateButtons.Children.Add(button);
            }
            panel.Children.Add(templateButtons);
            RefreshTemplates(_settings == null ? null : _settings.TemplateId);
            panel.Children.Add(_fileTemplate);
            var fileLine = new DockPanel { Margin = new Thickness(20, 4, 0, 0) };
            var browse = SmallButton("찾아보기…", "템플릿 .xlsx 파일을 고릅니다");
            browse.Margin = new Thickness(6, 0, 0, 0);
            browse.Click += (s, e) => BrowseTemplate();
            DockPanel.SetDock(browse, Dock.Right);
            fileLine.Children.Add(browse);
            _templatePath = new TextBox { IsReadOnly = true, Text = lastTemplatePath ?? "", ToolTip = lastTemplatePath };
            AutomationProperties.SetName(_templatePath, "템플릿 파일");
            fileLine.Children.Add(_templatePath);
            panel.Children.Add(fileLine);
            var llmButtons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            var llm = SmallButton("LLM 지침 복사", "LLM(Claude Code 등)에게 붙여 넣을 요청을 복사합니다 — 양식 작성 지침 주소와 양식 폴더가 들어 있습니다");
            llm.Margin = new Thickness(0, 0, 6, 0);
            llm.Click += (s, e) => CopyLlmPrompt();
            var guide = SmallButton("지침 보기", TableSpecTemplates.GuideUrl);
            guide.Margin = new Thickness(0);
            guide.Click += (s, e) => OpenUrl(TableSpecTemplates.GuideUrl);
            llmButtons.Children.Add(llm);
            llmButtons.Children.Add(guide);
            panel.Children.Add(llmButtons);
            panel.Children.Add(DialogKit.Hint("셀에 ${table.name} 같은 변수를 쓰고 #foreach … #end 줄로 되풀이합니다. 문법·변수는 양식의 '_변수 설명' 시트와 [지침 보기]에.", 6));

            return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        }

        private SpecTemplate SelectedTemplate
        {
            get { return _templateBox.SelectedItem as SpecTemplate ?? TableSpecTemplates.All[0]; }
        }

        private void UpdateTemplateHint()
        {
            if (_templateHint != null)
                _templateHint.Text = SelectedTemplate.Description;
            if (_openTemplate != null)
                _openTemplate.IsEnabled = !SelectedTemplate.IsBuiltIn;
        }

        // 양식 목록을 다시 읽고 id의 양식을 고른다(없으면 맨 앞). [양식] 라디오는 그대로 둔다
        private void RefreshTemplates(string selectId)
        {
            _refreshingTemplates = true;
            try
            {
                _templates = TableSpecTemplates.List(_templateFolder);
                _templateBox.ItemsSource = _templates;
                _templateBox.SelectedItem = TableSpecTemplates.Find(_templates, selectId);
            }
            finally
            {
                _refreshingTemplates = false;
            }
            UpdateTemplateHint();
        }

        private FrameworkElement BuildFooter()
        {
            var foot = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
            // 오른쪽부터 [만들기…] [폴더 열기] [파일 열기] — 모두 같은 크기
            _create = FooterButton("만들기…", true);
            _create.IsDefault = true;
            _create.Margin = new Thickness(8, 0, 0, 0);
            _create.Click += (s, e) =>
            {
                // 만드는 동안은 [취소]
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

        // ================= 대상 불러오기 =================

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _pattern.Focus();
                var schemas = _request.Schemas;
                if (schemas == null)
                {
                    SetStatus("스키마 목록을 불러오는 중…", false);
                    schemas = await LoadSchemas();
                }
                var ordered = TableSpecLogic.OrderSchemas(schemas.Concat(_request.OwnersToLoad), _request.MySchema);
                foreach (var owner in _request.OwnersToLoad)
                    await EnsureLoaded(owner);
                if (_closed)
                    return;
                _schema.ItemsSource = ordered;
                var initial = _request.InitialOwner ?? _request.MySchema;
                _schema.SelectedItem = initial != null && ordered.Contains(initial) ? initial : ordered.FirstOrDefault();
                SetStatus("", false);
                RefreshView();
            }
            catch (Exception ex)
            {
                if (!_closed && !(ex is OperationCanceledException))
                    SetStatus("대상 목록을 불러오지 못했습니다: " + DbSession.DescribeError(ex), true);
            }
        }

        // 트리가 스키마를 아직 불러오지 않았을 때: ALL_USERS(ORACLE_MAINTAINED가 없는 11g면 알려진 내장 스키마 목록으로 거른다)
        private async Task<List<string>> LoadSchemas()
        {
            List<SchemaInfo> list;
            try
            {
                list = await _session.QueryAsync(OracleMetadata.Schemas(true), r => OracleMetadata.ReadSchema(r, true), _closing.Token);
            }
            catch (Exception ex) when (OracleMetadata.IsInvalidIdentifier(ex))
            {
                list = await _session.QueryAsync(OracleMetadata.Schemas(false), r => OracleMetadata.ReadSchema(r, false), _closing.Token);
            }
            return list.Where(s => !s.OracleMaintained || s.Name == _request.MySchema).Select(s => s.Name).ToList();
        }

        private Task EnsureLoaded(string owner)
        {
            if (string.IsNullOrEmpty(owner) || _byOwner.ContainsKey(owner))
                return Task.CompletedTask;
            Task task;
            if (!_loading.TryGetValue(owner, out task))
            {
                task = Load(owner);
                _loading[owner] = task;
            }
            return task;
        }

        private async Task Load(string owner)
        {
            try
            {
                SetStatus(owner + " 테이블 목록을 불러오는 중…", false);
                var candidates = await _session.QueryAsync(TableSpecQueries.Candidates(owner, true), TableSpecQueries.ReadCandidate, _closing.Token);
                var tablesAll = _request.CheckAllTables.Contains(owner);
                var viewsAll = _request.CheckAllViews.Contains(owner);
                var items = new List<TableSpecItem>(candidates.Count);
                foreach (var candidate in candidates)
                {
                    var key = new TableKey(candidate.Owner, candidate.Name);
                    var isView = candidate.Type == "VIEW";
                    if ((isView ? viewsAll : tablesAll) || _request.Checked.Contains(key))
                        _checked.Add(key);
                    items.Add(new TableSpecItem(candidate, _checked.Contains(key), OnItemChecked));
                }
                _byOwner[owner] = items;
                SetStatus("", false);
            }
            finally
            {
                _loading.Remove(owner);
            }
        }

        private async void Schema_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                var owner = _schema.SelectedItem as string;
                if (owner == null)
                    return;
                if (!_byOwner.ContainsKey(owner))
                {
                    _list.ItemsSource = null;
                    await EnsureLoaded(owner);
                }
                if (!_closed && Equals(_schema.SelectedItem, owner))
                    RefreshView();
            }
            catch (Exception ex)
            {
                if (!_closed && !(ex is OperationCanceledException))
                    SetStatus("테이블 목록을 불러오지 못했습니다: " + DbSession.DescribeError(ex), true);
            }
        }

        // ================= 목록·체크 =================

        private void RefreshView()
        {
            if (_list == null || _matchText == null)
                return;
            string error;
            var pattern = TableSpecLogic.Pattern(_pattern.Text, _ignoreCase.IsChecked == true, out error);
            var includeViews = _includeViews.IsChecked == true;
            IEnumerable<TableSpecItem> source;
            if (_onlyChecked.IsChecked == true)
            {
                source = _byOwner.OrderBy(p => p.Key, StringComparer.Ordinal).SelectMany(p => p.Value).Where(i => i.IsChecked);
            }
            else
            {
                List<TableSpecItem> items;
                var owner = _schema.SelectedItem as string;
                source = owner != null && _byOwner.TryGetValue(owner, out items) ? items : Enumerable.Empty<TableSpecItem>();
            }
            var all = source.Where(i => includeViews || !i.IsView).ToList();
            var shown = error != null ? all : all.Where(i => TableSpecLogic.Matches(pattern, i.Candidate, _searchComment.IsChecked == true)).ToList();
            _list.ItemsSource = shown;
            _matchText.Text = TableSpecLogic.MatchText(shown.Count, all.Count, pattern != null);
            // .NET 정규식 오류 문장은 길다(영문) — 짧게 보이고 자세한 것은 풍선 도움말로
            _patternError.Text = error == null ? "" : "정규식 오류";
            _patternError.ToolTip = error;
            if (error == null)
                _pattern.SetResourceReference(Control.BorderBrushProperty, Theme.Border);
            else
                _pattern.BorderBrush = Theme.Danger;
            // 정규식이 틀렸을 때 "일치 모두 체크"가 전체를 체크하지 않게
            _checkMatches.IsEnabled = _uncheckMatches.IsEnabled = error == null && shown.Count > 0 && _work == null;
            UpdateSelection();
        }

        private List<TableSpecItem> CurrentItems()
        {
            return _list.ItemsSource == null ? new List<TableSpecItem>() : _list.ItemsSource.OfType<TableSpecItem>().ToList();
        }

        private void SetChecked(IEnumerable<TableSpecItem> items, bool value)
        {
            _bulk = true;
            try
            {
                foreach (var item in items)
                    item.IsChecked = value;
            }
            finally
            {
                _bulk = false;
            }
            UpdateSelection();
        }

        private void OnItemChecked(TableSpecItem item)
        {
            if (item.IsChecked)
                _checked.Add(item.Key);
            else
                _checked.Remove(item.Key);
            if (!_bulk)
                UpdateSelection();
        }

        /// <summary>만들 대상: 체크한 항목(뷰는 [뷰 포함]일 때만), 스키마·이름 순.</summary>
        private List<TableKey> Targets()
        {
            var includeViews = _includeViews.IsChecked == true;
            return TableSpecLogic.Order(_byOwner.Values.SelectMany(l => l).Where(i => i.IsChecked && (includeViews || !i.IsView)).Select(i => i.Key));
        }

        private void UpdateSelection()
        {
            if (_selectionText != null)
                _selectionText.Text = TableSpecLogic.SelectionText(Targets());
        }

        // ================= 템플릿 =================

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

        // 고른 양식을 양식 폴더에 새 파일로 복사하고(겹치면 " (2)") 목록에서 고른 뒤 엑셀에서 연다
        private void CopyTemplate()
        {
            var folder = EnsureTemplateFolder();
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

        private void OpenTemplateFolder()
        {
            var folder = EnsureTemplateFolder();
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

        // 양식 폴더를 만들어 돌려준다. 데이터 폴더를 모르거나 만들 수 없으면 상태에 알리고 null
        private string EnsureTemplateFolder()
        {
            if (string.IsNullOrEmpty(_templateFolder))
            {
                SetStatus("플러그인 데이터 폴더를 알 수 없어 양식 폴더를 쓸 수 없습니다. [템플릿 파일]로 파일을 고르세요.", true);
                return null;
            }
            try
            {
                Directory.CreateDirectory(_templateFolder);
                return _templateFolder;
            }
            catch (Exception ex)
            {
                SetStatus("양식 폴더를 만들지 못했습니다: " + ex.Message, true);
                return null;
            }
        }

        // LLM에게 붙여 넣을 요청(지침 주소·양식 폴더·바탕 양식)을 클립보드에 넣는다
        private void CopyLlmPrompt()
        {
            var template = SelectedTemplate;
            var text = TableSpecLogic.LlmPrompt(TableSpecTemplates.GuideUrl, TableSpecTemplates.GuideMarkdownUrl, _templateFolder, template.IsBuiltIn ? null : template.Path);
            if (DialogKit.TryCopy(text))
                SetStatus("LLM에게 붙여 넣을 요청을 복사했습니다. 끝에 원하는 모양을 적어 보내면 양식 폴더에 새 양식을 만듭니다(목록을 펼치면 보임).", false);
            else
                SetStatus("클립보드를 쓸 수 없습니다. 잠시 뒤 다시 누르세요.", true);
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

        // ================= 정의서 만들기 =================

        private async void Create()
        {
            if (_work != null)
                return;
            try
            {
                var targets = Targets();
                if (targets.Count == 0)
                {
                    SetStatus("대상 테이블을 하나 이상 체크하세요.", true);
                    return;
                }
                var date = TableSpecLogic.ParseDate(_date.Text);
                if (!date.HasValue)
                {
                    SetStatus("작성일은 2026-10-10 꼴로 쓰세요.", true);
                    _date.Focus();
                    return;
                }
                byte[] template;
                string templatePath = null;
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
                    Title = "테이블 정의서 저장",
                    Filter = "Excel 통합 문서 (*.xlsx)|*.xlsx",
                    FileName = TableSpecLogic.DefaultFileName(document.Title, _profile == null ? null : _profile.Name, targets.Select(t => t.Owner), document.Date),
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
                SaveSettings(document, templatePath);
                await Run(targets, document, template, save.FileName);
            }
            catch (Exception ex)
            {
                SetStatus("정의서를 만들지 못했습니다: " + Describe(ex), true);
            }
        }

        private async Task Run(List<TableKey> targets, TableSpecDocument document, byte[] template, string path)
        {
            _work = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                var progress = new Progress<string>(text => SetStatus(text + "…", false));
                var tables = await TableSpecQueries.LoadAsync(_session, targets, progress, _work.Token);
                var version = await TableSpecQueries.LoadServerVersionAsync(_session, _work.Token);
                _work.Token.ThrowIfCancellationRequested();
                SetStatus("엑셀 만드는 중… (테이블 " + tables.Count.ToString("N0", CultureInfo.InvariantCulture) + "개)", false);
                var context = TableSpecModel.Context(document, _profile, tables, version);
                await Task.Run(() =>
                {
                    using (var output = new MemoryStream())
                    {
                        ExcelTemplate.Render(template, context, output);
                        File.WriteAllBytes(path, output.ToArray());
                    }
                }, _work.Token);
                if (_closed)
                    return;
                _lastOutput = path;
                SetStatus(TableSpecLogic.DoneText(tables.Count, targets.Count - tables.Count, path), false);
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

        // [만들기…] 때의 입력값을 저장한다. 저장하지 못해도 정의서는 만든다(다음에 기본값으로 열릴 뿐)
        private void SaveSettings(TableSpecDocument document, string templatePath)
        {
            if (_saveSettings == null)
                return;
            try
            {
                _saveSettings(TableSpecLogic.SerializeSettings(TableSpecLogic.SettingsFrom(document, SelectedTemplate.Id, templatePath, _folder)));
            }
            catch (Exception)
            {
                // 설정 파일을 쓸 수 없는 경우 — 입력값을 기억하지 못할 뿐이다
            }
        }

        private static string Describe(Exception ex)
        {
            if (ex is IOException || ex is UnauthorizedAccessException)
                return ex.Message + " (엑셀에서 열어 둔 파일이면 닫은 뒤 다시 하세요)";
            return DbSession.DescribeError(ex);
        }

        private void SetBusy(bool busy)
        {
            _inputs.IsEnabled = !busy;
            _create.Content = busy ? "취소" : "만들기…";
            // 만드는 중 Enter로 취소되지 않게
            _create.IsDefault = !busy;
            _progress.IsIndeterminate = busy;
            _progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (busy)
                _openFile.Visibility = _openFolder.Visibility = Visibility.Collapsed;
            else
                RefreshView();
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

        // 아래 줄 버튼: 크기를 똑같이(최소 너비 96, 높이 32). primary는 강조색 테두리·굵은 글자(DialogKit.PrimaryButton과 같은 모양)
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

        private static RadioButton Radio(string text, bool isChecked)
        {
            var radio = Theme.Foreground(new RadioButton { Content = text, GroupName = "tableSpecTemplate", IsChecked = isChecked, Margin = new Thickness(0, 6, 0, 0) });
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
    }

    /// <summary>대상 목록 한 줄(체크 상태는 창의 _checked와 함께 바뀐다).</summary>
    internal sealed class TableSpecItem : INotifyPropertyChanged
    {
        private readonly Action<TableSpecItem> _changed;
        private bool _isChecked;

        public TableSpecItem(TableSpecCandidate candidate, bool isChecked, Action<TableSpecItem> changed)
        {
            Candidate = candidate;
            Key = new TableKey(candidate.Owner, candidate.Name);
            _isChecked = isChecked;
            _changed = changed;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public TableSpecCandidate Candidate { get; }
        public TableKey Key { get; }
        public string Owner { get { return Candidate.Owner; } }
        public string Name { get { return Candidate.Name; } }
        public bool IsView { get { return Candidate.Type == "VIEW"; } }
        public string TypeLabel { get { return IsView ? "뷰" : "테이블"; } }
        public string Comment { get { return Candidate.Comment; } }
        public string RowsText { get { return Candidate.NumRows.HasValue ? Candidate.NumRows.Value.ToString("N0", CultureInfo.InvariantCulture) : ""; } }

        public bool IsChecked
        {
            get { return _isChecked; }
            set
            {
                if (_isChecked == value)
                    return;
                _isChecked = value;
                if (PropertyChanged != null)
                    PropertyChanged(this, new PropertyChangedEventArgs(nameof(IsChecked)));
                if (_changed != null)
                    _changed(this);
            }
        }
    }
}

using Folderss.Plugins;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace MyPlugin
{
    /// <summary>접속 관리 대화상자(추가·복제·삭제·편집·접속 테스트). PoC의 "접속 관리" 팝업과 같은 동작.</summary>
    internal static class ConnectionManager
    {
        /// <summary>
        /// 모달로 열고, 저장했으면 true. 연결 중인 접속(ConnectionRepository.IsConnectedAnywhere)은 삭제할 수 없고 "다시 연결할 때 적용" 안내를 보인다.
        /// 저장된 값을 읽지 못하면 안내만 하고 false(덮어쓰지 않음).
        /// </summary>
        public static bool Show(Window owner, IPluginManager manager)
        {
            if (manager == null)
                throw new ArgumentNullException(nameof(manager));
            List<OracleConnectionProfile> profiles;
            try
            {
                profiles = ConnectionRepository.Load(manager);
            }
            catch (Exception ex)
            {
                Dialogs.Show(owner, "접속 관리", ex.Message + Environment.NewLine + Environment.NewLine
                    + "저장된 값을 덮어쓰지 않도록 접속 관리를 열지 않았습니다.", true);
                return false;
            }

            var editor = new Editor(owner, manager, profiles);
            var saved = editor.Run();
            if (editor.NotifyError != null)
                Dialogs.Show(owner, "접속 관리", editor.NotifyError, true);
            return saved;
        }

        private enum TestState
        {
            Idle,
            Busy,
            Succeeded,
            Failed
        }

        /// <summary>색 표시 선택지(콤보 항목). 바인딩하므로 public 속성.</summary>
        private sealed class ColorChoice
        {
            public ColorChoice(string value)
            {
                Value = value;
                Title = Theme.ConnectionColorTitle(value);
                Swatch = Theme.ConnectionColor(value);
            }

            public string Value { get; }
            public string Title { get; }
            public Brush Swatch { get; }

            public override string ToString()
            {
                return Title;
            }
        }

        /// <summary>왼쪽 목록의 항목 하나와 그 접속의 접속 테스트 결과.</summary>
        private sealed class Item
        {
            private readonly Border _frame;
            private readonly TextBlock _dot;
            private readonly Ellipse _swatch;
            private readonly TextBlock _name;
            private readonly Border _readOnlyTag;
            private readonly Border _liveTag;
            private readonly TextBlock _address;

            public Item(ConnectionDraft draft)
            {
                Draft = draft;
                _dot = new TextBlock { Text = "●", FontSize = 10, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "저장하지 않은 변경" };
                _dot.SetResourceReference(TextBlock.ForegroundProperty, Theme.Accent);
                _swatch = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
                _name = Theme.Text("");
                _name.FontWeight = FontWeights.SemiBold;
                _name.TextTrimming = TextTrimming.CharacterEllipsis;
                _name.VerticalAlignment = VerticalAlignment.Center;
                _readOnlyTag = Tag("읽기 전용", null);
                _liveTag = Tag("연결 중", Theme.Success);

                var line = new Grid();
                for (var i = 0; i < 5; i++)
                    line.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
                AddAt(line, _dot, 0);
                AddAt(line, _swatch, 1);
                AddAt(line, _name, 2);
                AddAt(line, _readOnlyTag, 3);
                AddAt(line, _liveTag, 4);

                _address = Theme.Text("", Theme.SecondaryText);
                _address.FontFamily = Theme.Mono;
                _address.FontSize = 10.5;
                _address.TextTrimming = TextTrimming.CharacterEllipsis;

                var lines = new StackPanel();
                lines.Children.Add(line);
                lines.Children.Add(_address);
                _frame = new Border
                {
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    BorderBrush = Brushes.Transparent,
                    Padding = new Thickness(5, 1, 0, 1),
                    Child = lines
                };
                Container = new ListBoxItem { Content = _frame, Tag = this, Padding = new Thickness(0, 3, 4, 3) };
            }

            public ConnectionDraft Draft { get; }

            public ListBoxItem Container { get; }

            public TestState Test { get; private set; }

            public string TestText { get; private set; }

            public void SetTest(TestState state, string text)
            {
                Test = state;
                TestText = text;
            }

            public void ClearTest()
            {
                SetTest(TestState.Idle, null);
            }

            public void Update(bool changed, bool invalid, bool live)
            {
                var profile = Draft.Profile;
                _dot.Visibility = changed ? Visibility.Visible : Visibility.Collapsed;
                var color = Theme.ConnectionColor(profile.Color);
                _swatch.Fill = color;
                _swatch.Visibility = color == null ? Visibility.Collapsed : Visibility.Visible;
                _name.Text = ConnectionManagerLogic.DisplayName(profile);
                _readOnlyTag.Visibility = profile.ReadOnly ? Visibility.Visible : Visibility.Collapsed;
                _liveTag.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
                _address.Text = ConnectionManagerLogic.Address(profile);
                _frame.BorderBrush = invalid ? Theme.Danger : Brushes.Transparent;
                _frame.ToolTip = invalid ? "저장할 수 없는 값이 있습니다" : null;
                AutomationProperties.SetName(Container, _name.Text + (changed ? ", 저장하지 않은 변경" : "") + (invalid ? ", 오류" : "") + (live ? ", 연결 중" : ""));
            }

            // 작은 꼬리표. brush가 null이면 테마의 흐린 글자·테두리.
            private static Border Tag(string text, Brush brush)
            {
                var block = new TextBlock { Text = text, FontSize = 10, FontWeight = FontWeights.SemiBold };
                var tag = new Border
                {
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(4, 0, 4, 0),
                    Margin = new Thickness(4, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = block
                };
                if (brush == null)
                {
                    block.SetResourceReference(TextBlock.ForegroundProperty, Theme.SecondaryText);
                    tag.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
                }
                else
                {
                    block.Foreground = brush;
                    tag.BorderBrush = brush;
                }
                return tag;
            }

            private static void AddAt(Grid grid, UIElement element, int column)
            {
                Grid.SetColumn(element, column);
                grid.Children.Add(element);
            }
        }

        /// <summary>대화상자 하나. 편집은 사본(ConnectionDraft)에 하고 [저장]할 때만 플러그인 설정에 쓴다(닫으면 버림).</summary>
        private sealed class Editor
        {
            private const double Gap = 8;
            private const double NoteGap = 4;

            private readonly IPluginManager _manager;
            private readonly List<OracleConnectionProfile> _originals;
            private readonly List<Item> _items = new List<Item>();
            private readonly Window _window;

            private ListBox _list;
            private TextBlock _listEmpty;
            private Button _add, _duplicate, _delete;
            private ScrollViewer _form;
            private TextBlock _formEmpty;
            private TextBox _name, _host, _port, _service, _user;
            private ComboBox _provider;
            private TextBlock _serviceLabel;
            private PasswordBox _password;
            private TextBlock _passwordPlaceholder;
            private Run _passwordState;
            private Hyperlink _clearPassword;
            private CheckBox _readOnly;
            private ComboBox _color;
            private TextBlock _liveNote;
            private Button _test;
            private TextBlock _testResult;
            private StackPanel _errorList;
            private ScrollViewer _errorScroll;
            private Border _discardBar;
            private Button _keepEditing;
            private TextBlock _dirtyText;
            private Button _save, _cancel;

            private List<string> _errors = new List<string>();
            private HashSet<ConnectionDraft> _invalid = new HashSet<ConnectionDraft>();
            private bool _validating;      // 저장이 검증에 걸린 뒤로는 고칠 때마다 다시 검사해 표시를 맞춘다
            private bool _filling;         // 폼을 채우는 중(입력 이벤트를 편집으로 보지 않음)
            private bool _confirmDiscard;  // "버리고 닫을까요?"를 보이는 중
            private bool _allowClose;
            private bool _closed;
            private bool _saved;
            private int _testRun;          // 늘리면 진행 중인 접속 테스트의 결과를 버린다
            private Item _testing;

            public Editor(Window owner, IPluginManager manager, List<OracleConnectionProfile> profiles)
            {
                _manager = manager;
                _originals = profiles;

                _window = new Window
                {
                    Title = "접속 관리",
                    Width = 720,
                    Height = 480,
                    MinWidth = 640,
                    MinHeight = 420,
                    ResizeMode = ResizeMode.CanResize,
                    ShowInTaskbar = false
                };
                Theme.ApplyWindow(_window);
                DialogKit.SetOwner(_window, owner);
                _window.Content = Build();
                _window.Closing += Window_Closing;
                _window.Closed += (s, e) =>
                {
                    _closed = true;
                    _testRun++;
                };
                _window.KeyDown += Window_KeyDown;
                _window.Loaded += (s, e) =>
                {
                    if (Selected != null)
                        _name.Focus();
                    else
                        _add.Focus();
                };

                foreach (var profile in profiles)
                    AddItem(new ConnectionDraft(ConnectionManagerLogic.Clone(profile)), _items.Count);
                if (_items.Count > 0)
                    _list.SelectedIndex = 0;
                Fill(Selected);
                Refresh();
            }

            /// <summary>저장 후 Changed 처리기 일부가 실패했으면 그 안내(저장 자체는 됨).</summary>
            public string NotifyError { get; private set; }

            public bool Run()
            {
                _window.ShowDialog();
                return _saved;
            }

            private Item Selected
            {
                get
                {
                    var container = _list.SelectedItem as ListBoxItem;
                    return container == null ? null : container.Tag as Item;
                }
            }

            private bool IsDirty
            {
                get { return ConnectionManagerLogic.IsDirty(_originals, _items.Select(i => i.Draft).ToList()); }
            }

            // ---- 화면 만들기 ----

            private FrameworkElement Build()
            {
                var body = new Grid();
                body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
                body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
                body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                body.Children.Add(BuildList());
                var formHost = new Grid();
                _form = new ScrollViewer
                {
                    Content = BuildForm(),
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Padding = new Thickness(0, 0, 6, 0),
                    Focusable = false
                };
                _formEmpty = Theme.Text("왼쪽에서 접속을 고르거나 추가하세요.", Theme.SecondaryText);
                _formEmpty.TextWrapping = TextWrapping.Wrap;
                _formEmpty.HorizontalAlignment = HorizontalAlignment.Center;
                _formEmpty.VerticalAlignment = VerticalAlignment.Center;
                formHost.Children.Add(_form);
                formHost.Children.Add(_formEmpty);
                Grid.SetColumn(formHost, 2);
                body.Children.Add(formHost);

                var root = new DockPanel { Margin = new Thickness(12) };
                var bar = BuildBottomBar();
                DockPanel.SetDock(bar, Dock.Bottom);
                root.Children.Add(bar);
                BuildDiscardBar();
                DockPanel.SetDock(_discardBar, Dock.Bottom);
                root.Children.Add(_discardBar);
                _errorList = new StackPanel();
                _errorScroll = new ScrollViewer
                {
                    Content = _errorList,
                    MaxHeight = 90,
                    Margin = new Thickness(0, 8, 0, 0),
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Visibility = Visibility.Collapsed,
                    Focusable = false
                };
                DockPanel.SetDock(_errorScroll, Dock.Bottom);
                root.Children.Add(_errorScroll);
                root.Children.Add(body);
                return root;
            }

            private FrameworkElement BuildList()
            {
                _list = new ListBox { BorderThickness = new Thickness(0) };
                ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
                AutomationProperties.SetName(_list, "접속 목록");
                _list.SelectionChanged += List_SelectionChanged;

                _listEmpty = Theme.Text("접속이 없습니다.\n[추가]를 누르세요.", Theme.SecondaryText);
                _listEmpty.TextAlignment = TextAlignment.Center;
                _listEmpty.HorizontalAlignment = HorizontalAlignment.Center;
                _listEmpty.VerticalAlignment = VerticalAlignment.Center;
                _listEmpty.IsHitTestVisible = false;

                _add = ListButton("추가");
                _duplicate = ListButton("복제");
                _delete = ListButton("삭제");
                ToolTipService.SetShowOnDisabled(_delete, true);
                _add.Click += Add_Click;
                _duplicate.Click += Duplicate_Click;
                _delete.Click += Delete_Click;
                var buttons = new UniformGrid { Columns = 3, Margin = new Thickness(3) };
                buttons.Children.Add(_add);
                buttons.Children.Add(_duplicate);
                buttons.Children.Add(_delete);
                var buttonBar = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = buttons };
                buttonBar.SetResourceReference(Border.BorderBrushProperty, Theme.Border);

                var items = new Grid();
                items.Children.Add(_list);
                items.Children.Add(_listEmpty);
                var dock = new DockPanel();
                DockPanel.SetDock(buttonBar, Dock.Bottom);
                dock.Children.Add(buttonBar);
                dock.Children.Add(items);

                var frame = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Child = dock };
                frame.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
                frame.SetResourceReference(Border.BackgroundProperty, Theme.PanelBackground);
                return frame;
            }

            private FrameworkElement BuildForm()
            {
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });

                _name = Field("이름");
                _host = Field("호스트");
                _port = Field("포트");
                _service = Field("서비스명");
                _user = Field("사용자");
                _name.TextChanged += (s, e) => Edit(p => p.Name = _name.Text.Trim(), false);
                _host.TextChanged += (s, e) => Edit(p => p.Host = _host.Text.Trim(), true);
                _port.TextChanged += (s, e) => Edit(p => p.Port = ConnectionManagerLogic.ParsePort(_port.Text), true);
                _service.TextChanged += (s, e) => Edit(p => p.ServiceName = _service.Text.Trim(), true);
                _user.TextChanged += (s, e) => Edit(p => p.UserId = _user.Text.Trim(), true);

                var row = NewRow(grid);
                Place(grid, Label("이름"), row, 0, 1, 0);
                Place(grid, _name, row, 1, 3, 0);

                // DBMS 공급자: 바꾸면 서비스명/데이터베이스 라벨이 바뀌고, 포트가 이전 공급자의 기본값이었으면 새 기본값으로
                _provider = new ComboBox { ItemsSource = DbProviders.All, DisplayMemberPath = "DisplayName" };
                AutomationProperties.SetName(_provider, "DBMS");
                _provider.SelectionChanged += (s, e) =>
                {
                    var chosen = _provider.SelectedItem as IDbProvider;
                    if (chosen == null)
                        return;
                    UpdateProviderLabels(chosen);
                    if (_filling)
                        return;
                    var previous = DbProviders.For(Selected == null ? null : Selected.Draft.Profile);
                    Edit(p => p.Provider = chosen.Kind, true);
                    if (previous != chosen && ConnectionManagerLogic.ParsePort(_port.Text) == previous.DefaultPort)
                        SetText(_port, chosen.DefaultPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
                };
                row = NewRow(grid);
                Place(grid, Label("DBMS"), row, 0, 1, Gap);
                Place(grid, _provider, row, 1, 3, Gap);

                row = NewRow(grid);
                Place(grid, Label("호스트"), row, 0, 1, Gap);
                Place(grid, _host, row, 1, 1, Gap);
                var portLabel = Label("포트");
                portLabel.HorizontalAlignment = HorizontalAlignment.Right;
                portLabel.Margin = new Thickness(0, 0, 8, 0);
                Place(grid, portLabel, row, 2, 1, Gap);
                Place(grid, _port, row, 3, 1, Gap);

                row = NewRow(grid);
                _serviceLabel = Label("서비스명");
                Place(grid, _serviceLabel, row, 0, 1, Gap);
                Place(grid, _service, row, 1, 3, Gap);

                row = NewRow(grid);
                Place(grid, Label("사용자"), row, 0, 1, Gap);
                Place(grid, _user, row, 1, 3, Gap);

                row = NewRow(grid);
                Place(grid, Label("비밀번호"), row, 0, 1, Gap);
                Place(grid, BuildPasswordInput(), row, 1, 3, Gap);

                row = NewRow(grid);
                Place(grid, BuildPasswordNote(), row, 1, 3, NoteGap);

                _readOnly = DialogKit.Check("읽기 전용 (SELECT·WITH만 실행)");
                _readOnly.Checked += (s, e) => Edit(p => p.ReadOnly = true, false);
                _readOnly.Unchecked += (s, e) => Edit(p => p.ReadOnly = false, false);
                row = NewRow(grid);
                Place(grid, _readOnly, row, 1, 3, Gap);

                _color = new ComboBox
                {
                    ItemsSource = OracleConnectionStore.Colors.Select(c => new ColorChoice(c)).ToList(),
                    ItemTemplate = ColorTemplate()
                };
                AutomationProperties.SetName(_color, "색 표시");
                _color.SelectionChanged += (s, e) =>
                {
                    var choice = _color.SelectedItem as ColorChoice;
                    if (choice != null)
                        Edit(p => p.Color = choice.Value, false);
                };
                row = NewRow(grid);
                Place(grid, Label("색 표시"), row, 0, 1, Gap);
                Place(grid, _color, row, 1, 3, Gap);

                row = NewRow(grid);
                Place(grid, Note("탭·트리·확인 창의 DB 배지에 이 색을 씁니다. 운영 DB는 빨강을 권장합니다."), row, 1, 3, NoteGap);

                _liveNote = Note("지금 연결 중인 접속입니다. 바꾼 내용은 다시 연결할 때 적용됩니다(읽기 전용을 켜는 것은 바로 적용).");
                _liveNote.Foreground = Theme.Warning;
                row = NewRow(grid);
                Place(grid, _liveNote, row, 1, 3, Gap);

                row = NewRow(grid);
                Place(grid, BuildTestRow(), row, 1, 3, Gap);
                return grid;
            }

            private FrameworkElement BuildPasswordInput()
            {
                _password = DialogKit.PasswordInput();
                AutomationProperties.SetName(_password, "비밀번호");
                _password.PasswordChanged += Password_Changed;
                // PasswordBox에는 자리 표시 글자가 없어 비어 있을 때만 위에 흐린 글자를 겹쳐 보인다.
                _passwordPlaceholder = Theme.Text("", Theme.DisabledText);
                _passwordPlaceholder.IsHitTestVisible = false;
                _passwordPlaceholder.Margin = new Thickness(10, 0, 10, 0);
                _passwordPlaceholder.VerticalAlignment = VerticalAlignment.Center;
                _passwordPlaceholder.TextTrimming = TextTrimming.CharacterEllipsis;
                var host = new Grid();
                host.Children.Add(_password);
                host.Children.Add(_passwordPlaceholder);
                return host;
            }

            private FrameworkElement BuildPasswordNote()
            {
                var note = Note("");
                _passwordState = new Run();
                _clearPassword = new Hyperlink(new Run("저장된 비밀번호 지우기"));
                _clearPassword.Click += ClearPassword_Click;
                note.Inlines.Add(_passwordState);
                note.Inlines.Add(new Run(" "));
                note.Inlines.Add(_clearPassword);
                return note;
            }

            private FrameworkElement BuildTestRow()
            {
                _test = new Button { Content = "접속 테스트", Margin = new Thickness(0), VerticalAlignment = VerticalAlignment.Top };
                _test.Click += Test_Click;
                _testResult = Note("");
                _testResult.Margin = new Thickness(10, 0, 0, 0);
                _testResult.VerticalAlignment = VerticalAlignment.Center;
                var row = new DockPanel();
                DockPanel.SetDock(_test, Dock.Left);
                row.Children.Add(_test);
                row.Children.Add(_testResult);
                return row;
            }

            private void BuildDiscardBar()
            {
                var discard = new Button { Content = "버리고 닫기", MinWidth = 80 };
                discard.Click += (s, e) => CloseWith(false);
                _keepEditing = new Button { Content = "계속 편집", MinWidth = 80 };
                _keepEditing.Click += (s, e) => HideDiscard();
                var text = Theme.Text("저장하지 않은 변경이 있습니다. 버리고 닫을까요?");
                text.TextWrapping = TextWrapping.Wrap;
                text.VerticalAlignment = VerticalAlignment.Center;
                var row = new DockPanel();
                DockPanel.SetDock(_keepEditing, Dock.Right);
                DockPanel.SetDock(discard, Dock.Right);
                row.Children.Add(_keepEditing);
                row.Children.Add(discard);
                row.Children.Add(text);
                _discardBar = new Border
                {
                    BorderThickness = new Thickness(1),
                    BorderBrush = Theme.Warning,
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(10, 3, 4, 3),
                    Margin = new Thickness(0, 8, 0, 0),
                    Visibility = Visibility.Collapsed,
                    Child = row
                };
                _discardBar.SetResourceReference(Border.BackgroundProperty, Theme.SurfaceBackground);
            }

            private FrameworkElement BuildBottomBar()
            {
                _save = DialogKit.PrimaryButton("저장");
                _save.Click += Save_Click;
                _cancel = DialogKit.PlainButton("닫기");
                _cancel.Click += (s, e) => RequestClose();
                _dirtyText = Theme.Text("");
                _dirtyText.FontSize = 11.5;
                _dirtyText.VerticalAlignment = VerticalAlignment.Center;
                _dirtyText.TextTrimming = TextTrimming.CharacterEllipsis;
                var bar = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
                DockPanel.SetDock(_cancel, Dock.Right);
                DockPanel.SetDock(_save, Dock.Right);
                bar.Children.Add(_cancel);
                bar.Children.Add(_save);
                bar.Children.Add(_dirtyText);
                return bar;
            }

            private static DataTemplate ColorTemplate()
            {
                var panel = new FrameworkElementFactory(typeof(StackPanel));
                panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
                var swatch = new FrameworkElementFactory(typeof(Border));
                swatch.SetValue(FrameworkElement.WidthProperty, 12.0);
                swatch.SetValue(FrameworkElement.HeightProperty, 12.0);
                swatch.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));
                swatch.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
                swatch.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
                swatch.SetValue(Border.BorderThicknessProperty, new Thickness(1));
                swatch.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
                swatch.SetBinding(Border.BackgroundProperty, new Binding("Swatch"));
                var text = new FrameworkElementFactory(typeof(TextBlock));
                text.SetBinding(TextBlock.TextProperty, new Binding("Title"));
                text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
                panel.AppendChild(swatch);
                panel.AppendChild(text);
                var template = new DataTemplate { VisualTree = panel };
                template.Seal();
                return template;
            }

            private static TextBox Field(string name)
            {
                var box = new TextBox();
                AutomationProperties.SetName(box, name);
                return box;
            }

            private static TextBlock Label(string text)
            {
                var label = Theme.Text(text, Theme.SecondaryText);
                label.VerticalAlignment = VerticalAlignment.Center;
                return label;
            }

            private static TextBlock Note(string text)
            {
                var note = Theme.Secondary(text);
                note.TextWrapping = TextWrapping.Wrap;
                return note;
            }

            private static Button ListButton(string text)
            {
                return new Button { Content = text, Padding = new Thickness(4, 4, 4, 4), Margin = new Thickness(3) };
            }

            private static int NewRow(Grid grid)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                return grid.RowDefinitions.Count - 1;
            }

            private static void Place(Grid grid, FrameworkElement element, int row, int column, int span, double top)
            {
                var margin = element.Margin;
                element.Margin = new Thickness(margin.Left, top, margin.Right, margin.Bottom);
                Grid.SetRow(element, row);
                Grid.SetColumn(element, column);
                Grid.SetColumnSpan(element, span);
                grid.Children.Add(element);
            }

            // ---- 목록 ----

            private Item AddItem(ConnectionDraft draft, int index)
            {
                var item = new Item(draft);
                _items.Insert(index, item);
                _list.Items.Insert(index, item.Container);
                return item;
            }

            private void Add_Click(object sender, RoutedEventArgs e)
            {
                var profile = new OracleConnectionProfile
                {
                    Id = OracleConnectionStore.NewId(),
                    Name = ConnectionManagerLogic.NameForNew(_items.Select(i => i.Draft.Profile.Name)),
                    Provider = DbProviders.Default.Kind,
                    Host = "",
                    Port = DbProviders.Default.DefaultPort,
                    ServiceName = "",
                    UserId = "",
                    Color = ""
                };
                var item = AddItem(new ConnectionDraft(profile), _items.Count);
                Select(item);
                FocusLater(_host, false);
            }

            private void Duplicate_Click(object sender, RoutedEventArgs e)
            {
                var source = Selected;
                if (source == null)
                    return;
                // 비밀번호는 복제하지 않는다(DPAPI 값을 그대로 옮기지 않고 새로 입력하게 함).
                var copy = ConnectionManagerLogic.Clone(source.Draft.Profile);
                copy.Id = OracleConnectionStore.NewId();
                copy.Name = ConnectionManagerLogic.NameForCopy(source.Draft.Profile.Name, _items.Select(i => i.Draft.Profile.Name));
                copy.ProtectedPassword = null;
                var item = AddItem(new ConnectionDraft(copy), _items.IndexOf(source) + 1);
                Select(item);
                FocusLater(_name, true);
            }

            private void Delete_Click(object sender, RoutedEventArgs e)
            {
                var item = Selected;
                if (item == null || IsLive(item))
                    return;
                var index = _items.IndexOf(item);
                _items.RemoveAt(index);
                _list.Items.Remove(item.Container);
                if (_items.Count > 0)
                {
                    var next = _items[Math.Min(index, _items.Count - 1)];
                    Select(next);
                    FocusLater(next.Container, false);
                }
                else
                {
                    Fill(null);
                    FocusLater(_add, false);
                }
                Refresh();
            }

            private void Select(Item item)
            {
                _list.SelectedItem = item.Container;
                _list.ScrollIntoView(item.Container);
            }

            private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
            {
                InvalidateTest();
                HideDiscard();
                Fill(Selected);
                Refresh();
            }

            // ---- 폼 ----

            private void Fill(Item item)
            {
                _filling = true;
                try
                {
                    var visible = item != null;
                    _form.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                    _formEmpty.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
                    if (!visible)
                        return;
                    var p = item.Draft.Profile;
                    SetText(_name, p.Name);
                    var provider = DbProviders.For(p);
                    _provider.SelectedItem = provider;
                    UpdateProviderLabels(provider);
                    SetText(_host, p.Host);
                    SetText(_port, p.Port > 0 ? p.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) : "");
                    SetText(_service, p.ServiceName);
                    SetText(_user, p.UserId);
                    _password.Password = item.Draft.NewPassword;
                    _readOnly.IsChecked = p.ReadOnly;
                    _color.SelectedItem = _color.Items.Cast<ColorChoice>().FirstOrDefault(c => c.Value == (p.Color ?? "")) ?? _color.Items[0];
                }
                finally
                {
                    _filling = false;
                }
            }

            private static void SetText(TextBox box, string text)
            {
                box.Text = text ?? "";
                // 코드로 넣은 값도 실행 취소 기록에 남아 Ctrl+Z가 앞서 고른 접속의 값을 되살리므로 기록을 비운다(끄면 비워짐).
                box.IsUndoEnabled = false;
                box.IsUndoEnabled = true;
            }

            /// <summary>입력 칸의 값을 지금 접속에 반영한다. 연결에 쓰는 값이면 진행 중인 접속 테스트 결과를 버린다.</summary>
            private void Edit(Action<OracleConnectionProfile> apply, bool affectsConnection)
            {
                var item = Selected;
                if (_filling || item == null)
                    return;
                apply(item.Draft.Profile);
                Edited(item, affectsConnection);
            }

            private void Edited(Item item, bool affectsConnection)
            {
                if (affectsConnection)
                {
                    InvalidateTest();
                    item.ClearTest();
                }
                HideDiscard();
                Refresh();
            }

            private void Password_Changed(object sender, RoutedEventArgs e)
            {
                var item = Selected;
                if (_filling || item == null)
                {
                    RefreshPasswordPlaceholder(item);
                    return;
                }
                item.Draft.NewPassword = _password.Password;
                if (item.Draft.NewPassword.Length > 0)
                    item.Draft.ClearPassword = false;
                Edited(item, true);
            }

            private void ClearPassword_Click(object sender, RoutedEventArgs e)
            {
                var item = Selected;
                if (item == null || !item.Draft.HasStoredPassword)
                    return;
                item.Draft.ClearPassword = true;
                item.Draft.NewPassword = "";
                _filling = true;
                try
                {
                    _password.Password = "";
                }
                finally
                {
                    _filling = false;
                }
                Edited(item, true);
            }

            // ---- 표시 갱신 ----

            private void Refresh()
            {
                var drafts = _items.Select(i => i.Draft).ToList();
                if (_validating)
                {
                    var profiles = drafts.Select(d => d.Profile).ToList();
                    _errors = OracleConnectionStore.Validate(profiles);
                    _invalid = new HashSet<ConnectionDraft>(ConnectionManagerLogic.InvalidIndexes(profiles).Select(i => drafts[i]));
                }
                foreach (var item in _items)
                    item.Update(ConnectionManagerLogic.IsChanged(item.Draft, _originals), _invalid.Contains(item.Draft), IsLive(item));
                RefreshErrors();

                var dirty = ConnectionManagerLogic.IsDirty(_originals, drafts);
                _dirtyText.Text = dirty ? "저장하지 않은 변경이 있습니다" : "변경 없음";
                if (dirty)
                    _dirtyText.Foreground = Theme.Warning;
                else
                    _dirtyText.SetResourceReference(TextBlock.ForegroundProperty, Theme.SecondaryText);
                _save.IsEnabled = dirty;
                _cancel.Content = dirty ? "취소" : "닫기";
                if (!dirty)
                    HideDiscard();

                var selected = Selected;
                var live = selected != null && IsLive(selected);
                _listEmpty.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                _duplicate.IsEnabled = selected != null;
                _delete.IsEnabled = selected != null && !live;
                _delete.ToolTip = live ? "연결 중인 접속은 끊은 뒤 삭제할 수 있습니다" : null;
                if (selected == null)
                    return;

                var draft = selected.Draft;
                _passwordState.Text = ConnectionManagerLogic.PasswordState(draft);
                _clearPassword.IsEnabled = draft.HasStoredPassword;
                // 로컬 색이 Hyperlink 기본 스타일(마우스를 올리면 빨강, 비활성이면 회색)보다 우선하므로 상태별로 직접 바꾼다.
                _clearPassword.SetResourceReference(TextElement.ForegroundProperty, draft.HasStoredPassword ? Theme.Accent : Theme.DisabledText);
                RefreshPasswordPlaceholder(selected);
                _liveNote.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
                RefreshTest(selected);
            }

            private void RefreshPasswordPlaceholder(Item item)
            {
                if (item == null)
                    return;
                _passwordPlaceholder.Text = ConnectionManagerLogic.PasswordPlaceholder(item.Draft);
                _passwordPlaceholder.Visibility = _password.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            private void RefreshTest(Item item)
            {
                _test.IsEnabled = _testing != item;
                switch (item.Test)
                {
                    case TestState.Busy:
                        _testResult.Foreground = Theme.Warning;
                        break;
                    case TestState.Succeeded:
                        _testResult.Foreground = Theme.Success;
                        break;
                    case TestState.Failed:
                        _testResult.Foreground = Theme.Danger;
                        break;
                    default:
                        _testResult.SetResourceReference(TextBlock.ForegroundProperty, Theme.SecondaryText);
                        break;
                }
                _testResult.Text = item.TestText ?? ConnectionManagerLogic.TestIdleText;
            }

            private void RefreshErrors()
            {
                _errorList.Children.Clear();
                foreach (var error in _errors)
                {
                    _errorList.Children.Add(new TextBlock
                    {
                        Text = "• " + error,
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 11.5,
                        Foreground = Theme.Danger
                    });
                }
                _errorScroll.Visibility = _errors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            private void ShowErrors(IEnumerable<string> errors)
            {
                _validating = false;
                _invalid = new HashSet<ConnectionDraft>();
                _errors = errors.ToList();
                Refresh();
            }

            private static bool IsLive(Item item)
            {
                return ConnectionRepository.IsConnectedAnywhere(item.Draft.Profile.Id);
            }

            private void FocusLater(IInputElement element, bool selectAll)
            {
                // 폼이 방금 보이게 됐으면 배치가 끝난 뒤에야 포커스를 받을 수 있다.
                _window.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    if (_closed)
                        return;
                    element.Focus();
                    var box = element as TextBox;
                    if (selectAll && box != null)
                        box.SelectAll();
                }));
            }

            // ---- 접속 테스트 ----

            private void InvalidateTest()
            {
                _testRun++;
                if (_testing != null)
                {
                    _testing.ClearTest();
                    _testing = null;
                }
            }

            // async void 이벤트 처리기: 예외가 밖으로 나가면 Folderss가 종료되므로 전부 잡아 결과 칸에 보인다.
            private async void Test_Click(object sender, RoutedEventArgs e)
            {
                var item = Selected;
                if (item == null || _testing == item)
                    return;
                try
                {
                    var draft = item.Draft;
                    string password = null;
                    string passwordError = null;
                    if (draft.NewPassword.Length > 0)
                    {
                        password = draft.NewPassword;
                    }
                    else if (draft.HasStoredPassword)
                    {
                        try
                        {
                            password = ConnectionRepository.StoredPassword(draft.Profile);
                        }
                        catch (InvalidOperationException ex)
                        {
                            passwordError = ex.Message;
                        }
                    }
                    var inputError = ConnectionManagerLogic.TestInputError(draft.Profile, password != null || passwordError != null);
                    if (passwordError != null)
                        inputError = inputError == null ? passwordError : inputError + " " + passwordError;
                    if (inputError != null)
                    {
                        InvalidateTest();
                        item.SetTest(TestState.Failed, inputError);
                        RefreshTest(item);
                        return;
                    }

                    // 이름은 테스트에 쓰지 않지만 연결 문자열을 만들 때 검증하므로 비어 있으면 채운다.
                    // 사본을 넘겨 백그라운드 스레드가 편집 중인 값을 읽지 않게 한다.
                    var probe = ConnectionManagerLogic.Clone(draft.Profile);
                    if (string.IsNullOrWhiteSpace(probe.Name))
                        probe.Name = "test";

                    InvalidateTest();
                    var run = _testRun;
                    _testing = item;
                    item.SetTest(TestState.Busy, "접속하는 중…");
                    RefreshTest(item);

                    var outcome = await Task.Run(() => Probe(probe, password));

                    if (_closed || run != _testRun)
                        return; // 닫았거나, 다른 접속을 골랐거나, 값을 바꿈
                    _testing = null;
                    item.SetTest(outcome.Item1 ? TestState.Succeeded : TestState.Failed, outcome.Item2);
                    if (Selected == item)
                        RefreshTest(item);
                }
                catch (Exception ex)
                {
                    if (_closed)
                        return;
                    if (_testing == item)
                        _testing = null;
                    item.SetTest(TestState.Failed, "접속 실패: " + DbSession.DescribeError(ex));
                    if (Selected == item)
                        RefreshTest(item);
                }
            }

            private void UpdateProviderLabels(IDbProvider provider)
            {
                if (_serviceLabel != null)
                    _serviceLabel.Text = provider.ServiceLabel;
                if (_service != null)
                    AutomationProperties.SetName(_service, provider.ServiceLabel);
            }

            /// <summary>
            /// 백그라운드 스레드에서 연결을 열어 보고 닫는다. 예외를 밖으로 내지 않는다.
            /// 연결 문자열도 여기서 만든다(드라이버를 처음 불러오는 일까지 UI 스레드 밖에서 하도록). 공급자는 프로필의 provider.
            /// </summary>
            private static Tuple<bool, string> Probe(OracleConnectionProfile profile, string password)
            {
                var watch = Stopwatch.StartNew();
                try
                {
                    var provider = DbProviders.For(profile);
                    var connectionString = OracleConnectionStore.BuildConnectionString(profile, password, ConnectionManagerLogic.TestTimeoutSeconds);
                    // 첫 연결이 접속 테스트일 수 있다 — Oracle은 연결을 연 뒤에는 취소 방식을 바꿀 수 없다(ORA-50099)
                    provider.ConfigureProcess();
                    string version;
                    using (var connection = provider.CreateConnection(connectionString))
                    {
                        connection.Open();
                        version = provider.ServerVersionOf(connection);
                    }
                    return Tuple.Create(true, ConnectionManagerLogic.TestSuccess(version, watch.Elapsed, provider.DisplayName));
                }
                catch (Exception ex)
                {
                    return Tuple.Create(false, "접속 실패: " + ConnectionManagerLogic.DescribeConnectError(ex));
                }
            }

            // ---- 저장·닫기 ----

            private void Save_Click(object sender, RoutedEventArgs e)
            {
                try
                {
                    Save();
                }
                catch (Exception ex)
                {
                    ShowErrors(new[] { "저장하지 못했습니다: " + ex.Message });
                }
            }

            private void Save()
            {
                var drafts = _items.Select(i => i.Draft).ToList();
                var profiles = drafts.Select(d => d.Profile).ToList();
                if (OracleConnectionStore.Validate(profiles).Count > 0)
                {
                    _validating = true;
                    Refresh();
                    var firstBad = _items.FirstOrDefault(i => _invalid.Contains(i.Draft));
                    if (firstBad != null && firstBad != Selected)
                        Select(firstBad);
                    return;
                }

                // 대화상자를 연 뒤에 연결된 접속은 삭제 버튼이 이미 눌렸을 수 있어 저장할 때 다시 본다.
                var removedLive = ConnectionManagerLogic.RemovedProfiles(_originals, drafts)
                    .Where(p => ConnectionRepository.IsConnectedAnywhere(p.Id)).ToList();
                if (removedLive.Count > 0)
                {
                    ShowErrors(removedLive.Select(p => "'" + ConnectionManagerLogic.DisplayName(p) + "': 지금 연결 중이라 삭제할 수 없습니다. 연결을 끊은 뒤 삭제하세요."));
                    return;
                }

                List<OracleConnectionProfile> toSave;
                try
                {
                    toSave = ConnectionManagerLogic.BuildProfilesToSave(drafts, OracleConnectionStore.ProtectPassword);
                }
                catch (Exception ex)
                {
                    ShowErrors(new[] { "비밀번호를 암호화하지 못했습니다: " + ex.Message });
                    return;
                }

                try
                {
                    ConnectionRepository.Save(_manager, toSave);
                }
                catch (ConnectionRepository.NotifyFailedException ex)
                {
                    NotifyError = ex.Message; // 저장은 됐다
                }
                catch (Exception ex)
                {
                    ShowErrors(new[] { "저장하지 못했습니다: " + ex.Message });
                    return;
                }
                _saved = true;
                CloseWith(true);
            }

            private void RequestClose()
            {
                if (!_confirmDiscard && IsDirty)
                {
                    ShowDiscard();
                    return;
                }
                CloseWith(false);
            }

            private void CloseWith(bool result)
            {
                if (_closed)
                    return;
                _allowClose = true;
                _window.DialogResult = result;
            }

            private void ShowDiscard()
            {
                _confirmDiscard = true;
                _discardBar.Visibility = Visibility.Visible;
                FocusLater(_keepEditing, false);
            }

            private void HideDiscard()
            {
                _confirmDiscard = false;
                if (_discardBar != null)
                    _discardBar.Visibility = Visibility.Collapsed;
            }

            private void Window_Closing(object sender, CancelEventArgs e)
            {
                // 제목 표시줄의 X·Alt+F4: 바꾼 내용이 있으면 한 번은 묻는다(다시 닫으면 버림).
                if (_allowClose || _confirmDiscard || !IsDirty)
                    return;
                e.Cancel = true;
                ShowDiscard();
            }

            private void Window_KeyDown(object sender, KeyEventArgs e)
            {
                if (e.Key != Key.Escape)
                    return;
                e.Handled = true;
                RequestClose();
            }
        }
    }
}

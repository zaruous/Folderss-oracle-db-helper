using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace MyPlugin
{
    /// <summary>메뉴 막대·아이콘 막대가 부르는 기능(DbHelperView가 구현). 모든 호출은 UI 스레드에서.</summary>
    internal interface IShellCommands
    {
        void NewTab();
        void OpenFile();
        void OpenRecent(string path);
        void ClearRecent();
        IReadOnlyList<string> RecentFiles { get; }
        void Save();
        void SaveAs();
        void CloseTab();
        void ManageConnections();
        void ShowShortcuts();

        /// <summary>도구 > 테이블 정의서 만들기: 트리에서 고른 행들로 대상 선택 창.</summary>
        void CreateTableSpec();

        /// <summary>편집 메뉴(잘라내기·복사·되돌리기 등)의 대상. 없으면 null.</summary>
        TextBox ActiveEditor { get; }
        void ToggleComment();
        void ChangeCase(bool upper);

        void Run();
        void RunScript();

        /// <summary>편집기에서 커서가 있는 이름의 테이블 정보(F4).</summary>
        void Describe();
        void Cancel();
        void FetchNext();
        void Commit();
        void Rollback();

        /// <summary>트리에서 고른 DB에 연결·다시 연결·끊기.</summary>
        void Connect();
        void Reconnect();
        void Disconnect();

        ShellCommandState CommandState();
    }

    /// <summary>메뉴·아이콘을 켤지 끌지.</summary>
    internal sealed class ShellCommandState
    {
        public bool CanRun { get; set; }
        public bool CanCancel { get; set; }
        public bool CanFetch { get; set; }
        public bool CanCommit { get; set; }
        public bool CanRollback { get; set; }
        public bool CanConnect { get; set; }
        public bool CanReconnect { get; set; }
        public bool CanDisconnect { get; set; }
        public bool HasSelection { get; set; }
        /// <summary>트리에서 고른 DB 이름(연결 메뉴 대상). 없으면 null.</summary>
        public string DbName { get; set; }
    }

    /// <summary>
    /// 창 맨 위: 메뉴 막대(파일·편집·SQL·연결·도움말)와 그 아래 줄의 작은 아이콘 막대(미니 패널).
    /// - 단축키 자체는 DbHelperView(창 전체: Ctrl+N·O·S·Shift+S·W)와 편집기(Ctrl+Enter·/·Shift+U·Shift+L)가 처리한다. 메뉴는 글자로 보일 뿐이다.
    /// - Folderss의 MenuItem 스타일은 색만 바꾸고 템플릿은 WPF 기본이라 하위 메뉴 창이 밝은 회색으로 뜬다 — 메뉴 막대 안에서만 쓰는 템플릿을 테마 키로 직접 준다.
    /// - 메뉴 항목은 열 때마다 켬·끔과 이름(연결 대상 DB, 최근 파일)을 맞춘다. 아이콘은 Refresh로 맞춘다.
    /// </summary>
    internal sealed class ShellMenu
    {
        // Segoe Fluent Icons(Windows 11)·Segoe MDL2 Assets(Windows 10) 글리프
        public const string IconNew = "";
        public const string IconOpen = "";
        public const string IconSave = "";
        public const string IconSaveAs = "";
        public const string IconClose = "";
        public const string IconRun = "";
        public const string IconCancel = "";
        public const string IconFetch = "";
        public const string IconCommit = "";
        public const string IconRollback = "";
        public const string IconConnect = "";
        public const string IconReconnect = "";
        public const string IconDisconnect = "";
        public const string IconManage = "";



        private readonly IShellCommands _commands;
        private readonly List<KeyValuePair<Button, Func<ShellCommandState, bool>>> _icons = new List<KeyValuePair<Button, Func<ShellCommandState, bool>>>();

        public ShellMenu(IShellCommands commands)
        {
            _commands = commands ?? throw new ArgumentNullException(nameof(commands));
            var menu = new Menu { VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(2, 0, 2, 0) };
            menu.Resources.MergedDictionaries.Add(Resources());
            Theme.Background(menu, Theme.SurfaceBackground);
            Theme.Foreground(menu);
            menu.SetResourceReference(Control.FontFamilyProperty, Theme.AppFont);
            AutomationProperties.SetName(menu, "DB Helper 메뉴");
            menu.Items.Add(FileMenu());
            menu.Items.Add(EditMenu());
            menu.Items.Add(SqlMenu());
            menu.Items.Add(ConnectionMenu());
            menu.Items.Add(ToolsMenu());
            menu.Items.Add(HelpMenu());
            DockPanel.SetDock(menu, Dock.Left);

            var icons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            icons.Resources.MergedDictionaries.Add(Resources());
            AutomationProperties.SetName(icons, "빠른 실행 아이콘");
            icons.Children.Add(Icon(IconNew, "새 SQL 탭 (Ctrl+N)", null, s => true, _commands.NewTab));
            icons.Children.Add(Icon(IconOpen, "SQL 파일 열기 (Ctrl+O)", null, s => true, _commands.OpenFile));
            icons.Children.Add(Icon(IconSave, "저장 (Ctrl+S)", null, s => true, _commands.Save));
            icons.Children.Add(Divider());
            icons.Children.Add(Icon(IconRun, "실행 (Ctrl+Enter)", Theme.Success, s => s.CanRun, _commands.Run));
            icons.Children.Add(Icon(IconCancel, "실행 취소", Theme.Danger, s => s.CanCancel, _commands.Cancel));
            icons.Children.Add(Divider());
            icons.Children.Add(Icon(IconCommit, "커밋 (지금 탭 대상 DB)", null, s => s.CanCommit, _commands.Commit));
            icons.Children.Add(Icon(IconRollback, "롤백 (지금 탭 대상 DB)", null, s => s.CanRollback, _commands.Rollback));
            icons.Children.Add(Divider());
            icons.Children.Add(Icon(IconConnect, "연결 (트리에서 고른 DB)", null, s => s.CanConnect, _commands.Connect));
            icons.Children.Add(Icon(IconReconnect, "다시 연결 (트리에서 고른 DB)", null, s => s.CanReconnect, _commands.Reconnect));
            icons.Children.Add(Icon(IconDisconnect, "연결 끊기 (트리에서 고른 DB)", null, s => s.CanDisconnect, _commands.Disconnect));

            // 메뉴 막대(첫 줄) 아래에 아이콘 막대(둘째 줄)
            var menuBar = new Border { Child = menu, Padding = new Thickness(4, 1, 8, 1), BorderThickness = new Thickness(0, 0, 0, 1) };
            menuBar.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
            Theme.Background(menuBar, Theme.SurfaceBackground);
            icons.Margin = new Thickness(0);
            var iconBar = new Border { Child = icons, Padding = new Thickness(6, 2, 8, 2), BorderThickness = new Thickness(0, 0, 0, 1) };
            iconBar.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
            Theme.Background(iconBar, Theme.SurfaceBackground);
            var bars = new StackPanel();
            bars.Children.Add(menuBar);
            bars.Children.Add(iconBar);
            View = bars;
        }

        public FrameworkElement View { get; }

        /// <summary>아이콘의 켬·끔을 지금 상태로 맞춘다(실행 시작·끝, 연결 바뀜, 탭 바꿈 때).</summary>
        public void Refresh()
        {
            var state = SafeState();
            foreach (var pair in _icons)
                pair.Key.IsEnabled = pair.Value(state);
        }

        // ================= 메뉴 =================

        private MenuItem FileMenu()
        {
            var top = Top("파일(_F)");
            top.Items.Add(Item("새 SQL 탭(_N)", "Ctrl+N", IconNew, _commands.NewTab));
            top.Items.Add(Item("열기(_O)…", "Ctrl+O", IconOpen, _commands.OpenFile));
            var recent = new MenuItem { Header = "최근 파일(_R)" };
            recent.Items.Add(new MenuItem { Header = "(없음)", IsEnabled = false }); // 하위 메뉴 화살표가 보이게 자리를 둔다
            recent.SubmenuOpened += (s, e) =>
            {
                if (ReferenceEquals(e.OriginalSource, recent))
                    FillRecent(recent);
            };
            top.Items.Add(recent);
            top.Items.Add(new Separator());
            top.Items.Add(Item("저장(_S)", "Ctrl+S", IconSave, _commands.Save));
            top.Items.Add(Item("다른 이름으로 저장(_A)…", "Ctrl+Shift+S", IconSaveAs, _commands.SaveAs));
            top.Items.Add(new Separator());
            top.Items.Add(Item("탭 닫기(_C)", "Ctrl+W", IconClose, _commands.CloseTab));
            top.Items.Add(new Separator());
            top.Items.Add(Item("접속 관리(_M)…", null, IconManage, _commands.ManageConnections));
            return top;
        }

        private void FillRecent(MenuItem recent)
        {
            recent.Items.Clear();
            IReadOnlyList<string> files;
            try
            {
                files = _commands.RecentFiles ?? new List<string>();
            }
            catch (Exception)
            {
                files = new List<string>();
            }
            if (files.Count == 0)
            {
                recent.Items.Add(new MenuItem { Header = "(없음)", IsEnabled = false });
                return;
            }
            for (var i = 0; i < files.Count; i++)
            {
                var path = files[i];
                var item = Item(SqlFileLogic.RecentMenuText(i, path), null, null, () => _commands.OpenRecent(path));
                item.ToolTip = path;
                recent.Items.Add(item);
            }
            recent.Items.Add(new Separator());
            recent.Items.Add(Item("목록 지우기", null, null, _commands.ClearRecent));
        }

        private MenuItem EditMenu()
        {
            var top = Top("편집(_E)");
            var undo = Command("실행 취소(_U)", ApplicationCommands.Undo);
            var redo = Command("다시 실행(_R)", ApplicationCommands.Redo);
            var cut = Command("잘라내기(_T)", ApplicationCommands.Cut);
            var copy = Command("복사(_C)", ApplicationCommands.Copy);
            var paste = Command("붙여넣기(_P)", ApplicationCommands.Paste);
            var all = Command("모두 선택(_A)", ApplicationCommands.SelectAll);
            var comment = Item("줄 주석 토글(_M)", "Ctrl+/", null, _commands.ToggleComment);
            var upper = Item("대문자로(_I)", "Ctrl+Shift+U", null, () => _commands.ChangeCase(true));
            var lower = Item("소문자로(_L)", "Ctrl+Shift+L", null, () => _commands.ChangeCase(false));
            foreach (var item in new object[] { undo, redo, new Separator(), cut, copy, paste, all, new Separator(), comment, upper, lower })
                top.Items.Add(item);
            top.SubmenuOpened += (s, e) =>
            {
                if (!ReferenceEquals(e.OriginalSource, top))
                    return;
                // 명령 대상은 지금 탭의 편집기(트리·결과에 포커스가 있어도)
                var editor = SafeEditor();
                foreach (var item in new[] { undo, redo, cut, copy, paste, all })
                    item.CommandTarget = editor;
                var state = SafeState();
                comment.IsEnabled = editor != null;
                upper.IsEnabled = lower.IsEnabled = editor != null && state.HasSelection;
            };
            return top;
        }

        private MenuItem SqlMenu()
        {
            var top = Top("SQL(_S)");
            var run = Item("실행(_X)", "Ctrl+Enter", IconRun, _commands.Run);
            run.ToolTip = "커서가 있는 문장(선택 영역에 문장이 여럿이면 모두)을 실행합니다";
            var script = Item("스크립트 실행(_A)", "F5", null, _commands.RunScript);
            script.ToolTip = "편집기의 모든 문장을 차례로 실행하고 조회마다 결과 탭을 만듭니다(오류가 나면 멈춤)";
            var cancel = Item("실행 취소(_C)", null, IconCancel, _commands.Cancel);
            var fetch = Item("다음 행 가져오기(_N)", null, IconFetch, _commands.FetchNext);
            var commit = Item("커밋(_O)", null, IconCommit, _commands.Commit);
            var rollback = Item("롤백(_R)", null, IconRollback, _commands.Rollback);
            var describe = Item("테이블 정보(_I)…", "F4", null, _commands.Describe);
            describe.ToolTip = "편집기에서 커서가 있는 이름(또는 선택한 스키마.이름)의 열·인덱스·제약 조건을 봅니다";
            foreach (var item in new object[] { run, script, cancel, fetch, new Separator(), describe, new Separator(), commit, rollback })
                top.Items.Add(item);
            top.SubmenuOpened += (s, e) =>
            {
                if (!ReferenceEquals(e.OriginalSource, top))
                    return;
                var state = SafeState();
                run.IsEnabled = state.CanRun;
                script.IsEnabled = state.CanRun;
                cancel.IsEnabled = state.CanCancel;
                fetch.IsEnabled = state.CanFetch;
                commit.IsEnabled = state.CanCommit;
                rollback.IsEnabled = state.CanRollback;
            };
            return top;
        }

        private MenuItem ConnectionMenu()
        {
            var top = Top("연결(_C)");
            var target = new MenuItem { IsEnabled = false };
            var connect = Item("연결(_C)", null, IconConnect, _commands.Connect);
            var reconnect = Item("다시 연결(_R)", null, IconReconnect, _commands.Reconnect);
            reconnect.ToolTip = "연결을 끊고 저장된 접속 정보로 다시 연결합니다(커밋 대기 변경은 묻습니다)";
            var disconnect = Item("연결 끊기(_D)", null, IconDisconnect, _commands.Disconnect);
            foreach (var item in new object[] { target, new Separator(), connect, reconnect, disconnect, new Separator(), Item("접속 관리(_M)…", null, IconManage, _commands.ManageConnections) })
                top.Items.Add(item);
            top.SubmenuOpened += (s, e) =>
            {
                if (!ReferenceEquals(e.OriginalSource, top))
                    return;
                var state = SafeState();
                target.Header = state.DbName != null ? "대상: " + state.DbName.Replace("_", "__") + " (트리에서 고른 DB)" : "트리에서 DB를 고르세요";
                connect.IsEnabled = state.CanConnect;
                reconnect.IsEnabled = state.CanReconnect;
                disconnect.IsEnabled = state.CanDisconnect;
            };
            return top;
        }

        private MenuItem ToolsMenu()
        {
            var top = Top("도구(_T)");
            var spec = Item("테이블 정의서 만들기(_D)…", null, null, _commands.CreateTableSpec);
            spec.ToolTip = "트리에서 고른 테이블(Ctrl·Shift+누름으로 여러 개)·스키마·DB로 엑셀 테이블 정의서를 만듭니다";
            top.Items.Add(spec);
            return top;
        }

        private MenuItem HelpMenu()
        {
            var top = Top("도움말(_H)");
            top.Items.Add(Item("단축키(_K)…", null, null, _commands.ShowShortcuts));
            return top;
        }

        // ================= 만들기 도우미 =================

        private static MenuItem Top(string header)
        {
            var item = new MenuItem { Header = header };
            AutomationProperties.SetName(item, header.Replace("_", ""));
            return item;
        }

        private MenuItem Item(string header, string gesture, string icon, Action action)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture ?? "", Tag = icon };
            item.Click += (s, e) => Run(action);
            return item;
        }

        private static MenuItem Command(string header, RoutedUICommand command)
        {
            // 단축키 글자는 명령의 기본 입력(Ctrl+Z 등)에서 WPF가 채운다
            return new MenuItem { Header = header, Command = command };
        }

        private Button Icon(string glyph, string tooltip, Brush color, Func<ShellCommandState, bool> enabled, Action action)
        {
            var button = new Button { Content = glyph, ToolTip = tooltip, Margin = new Thickness(1, 0, 1, 0) };
            button.SetResourceReference(FrameworkElement.StyleProperty, "ShellIconButton");
            if (color != null)
                button.Foreground = color;
            ToolTipService.SetShowOnDisabled(button, true);
            AutomationProperties.SetName(button, tooltip);
            button.Click += (s, e) => Run(action);
            _icons.Add(new KeyValuePair<Button, Func<ShellCommandState, bool>>(button, enabled));
            return button;
        }

        private static FrameworkElement Divider()
        {
            var line = new Border { Width = 1, Height = 16, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            line.SetResourceReference(Border.BackgroundProperty, Theme.Border);
            DockPanel.SetDock(line, Dock.Left);
            return line;
        }

        private static void Run(Action action)
        {
            try
            {
                action();
            }
            catch (Exception)
            {
                // 명령 쪽(DbHelperView·SqlWorkspace)이 오류를 메시지로 알린다 — 메뉴는 Folderss를 멈추지 않는 것만 지킨다
            }
        }

        private ShellCommandState SafeState()
        {
            try
            {
                return _commands.CommandState() ?? new ShellCommandState();
            }
            catch (Exception)
            {
                return new ShellCommandState();
            }
        }

        private TextBox SafeEditor()
        {
            try
            {
                return _commands.ActiveEditor;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>메뉴 막대 안에서만 쓰는 테마 템플릿(MenuItem·구분선·아이콘 버튼). 붙이는 곳마다 새로 만든다(사전 하나를 여러 곳에 병합하지 않게).</summary>
        private static ResourceDictionary Resources()
        {
            return (ResourceDictionary)XamlReader.Parse(ResourcesXaml);
        }

        private const string ResourcesXaml = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Style x:Key='{x:Static MenuItem.SeparatorStyleKey}' TargetType='Separator'>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Separator'>
          <Border Height='1' Margin='8,3' Background='{DynamicResource BorderBrush}'/>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType='MenuItem'>
    <Setter Property='Foreground' Value='{DynamicResource PrimaryText}'/>
    <Setter Property='FontFamily' Value='{DynamicResource AppFontFamily}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='MenuItem'>
          <Border x:Name='Bd' Background='Transparent' Padding='8,4,10,4' SnapsToDevicePixels='True'>
            <Grid>
              <Grid.ColumnDefinitions>
                <ColumnDefinition Width='Auto' SharedSizeGroup='MenuIcon'/>
                <ColumnDefinition Width='*'/>
                <ColumnDefinition Width='Auto' SharedSizeGroup='MenuGesture'/>
                <ColumnDefinition Width='Auto'/>
              </Grid.ColumnDefinitions>
              <TextBlock x:Name='Glyph' Grid.Column='0' Width='16' Margin='0,0,8,0' VerticalAlignment='Center' TextAlignment='Center'
                         FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' FontSize='12'
                         Text='{Binding Tag, RelativeSource={RelativeSource TemplatedParent}}'
                         Foreground='{DynamicResource SecondaryText}'/>
              <ContentPresenter x:Name='HeaderHost' Grid.Column='1' ContentSource='Header' RecognizesAccessKey='True' VerticalAlignment='Center'/>
              <TextBlock x:Name='Gesture' Grid.Column='2' Margin='28,0,0,0' VerticalAlignment='Center'
                         Text='{TemplateBinding InputGestureText}' Foreground='{DynamicResource SecondaryText}'/>
              <TextBlock x:Name='Arrow' Grid.Column='3' Margin='10,0,0,0' Text='›' VerticalAlignment='Center' Visibility='Collapsed'/>
              <Popup x:Name='PART_Popup' Placement='Right' HorizontalOffset='10' VerticalOffset='-5'
                     IsOpen='{Binding IsSubmenuOpen, RelativeSource={RelativeSource TemplatedParent}}'
                     AllowsTransparency='True' Focusable='False' PopupAnimation='None'>
                <Border Background='{DynamicResource SurfaceBackground}' BorderBrush='{DynamicResource BorderBrush}' BorderThickness='1' Padding='0,3' MinWidth='180'>
                  <ItemsPresenter KeyboardNavigation.DirectionalNavigation='Cycle' KeyboardNavigation.TabNavigation='Cycle' Grid.IsSharedSizeScope='True'/>
                </Border>
              </Popup>
            </Grid>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='Role' Value='TopLevelHeader'>
              <Setter TargetName='Bd' Property='Padding' Value='9,3,9,3'/>
              <Setter TargetName='Glyph' Property='Visibility' Value='Collapsed'/>
              <Setter TargetName='Gesture' Property='Visibility' Value='Collapsed'/>
              <Setter TargetName='PART_Popup' Property='Placement' Value='Bottom'/>
              <Setter TargetName='PART_Popup' Property='HorizontalOffset' Value='0'/>
              <Setter TargetName='PART_Popup' Property='VerticalOffset' Value='1'/>
            </Trigger>
            <Trigger Property='Role' Value='TopLevelItem'>
              <Setter TargetName='Bd' Property='Padding' Value='9,3,9,3'/>
              <Setter TargetName='Glyph' Property='Visibility' Value='Collapsed'/>
              <Setter TargetName='Gesture' Property='Visibility' Value='Collapsed'/>
            </Trigger>
            <Trigger Property='Role' Value='SubmenuHeader'>
              <Setter TargetName='Arrow' Property='Visibility' Value='Visible'/>
              <Setter TargetName='Gesture' Property='Visibility' Value='Collapsed'/>
            </Trigger>
            <Trigger Property='IsHighlighted' Value='True'>
              <Setter TargetName='Bd' Property='Background' Value='{DynamicResource ControlHoverBrush}'/>
            </Trigger>
            <Trigger Property='IsSubmenuOpen' Value='True'>
              <Setter TargetName='Bd' Property='Background' Value='{DynamicResource ControlHoverBrush}'/>
            </Trigger>
            <Trigger Property='IsEnabled' Value='False'>
              <Setter Property='Foreground' Value='{DynamicResource DisabledTextBrush}'/>
              <Setter TargetName='Glyph' Property='Opacity' Value='0.45'/>
              <Setter TargetName='Gesture' Property='Opacity' Value='0.6'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style x:Key='ShellIconButton' TargetType='Button'>
    <Setter Property='Foreground' Value='{DynamicResource PrimaryText}'/>
    <Setter Property='Focusable' Value='False'/>
    <Setter Property='Width' Value='28'/>
    <Setter Property='Height' Value='26'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Border x:Name='Bd' Background='Transparent' CornerRadius='3' SnapsToDevicePixels='True'>
            <TextBlock x:Name='Glyph' Text='{Binding Content, RelativeSource={RelativeSource TemplatedParent}}'
                       FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' FontSize='14'
                       HorizontalAlignment='Center' VerticalAlignment='Center' Foreground='{TemplateBinding Foreground}'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='Bd' Property='Background' Value='{DynamicResource ControlHoverBrush}'/>
            </Trigger>
            <Trigger Property='IsPressed' Value='True'>
              <Setter TargetName='Bd' Property='Background' Value='{DynamicResource ControlPressedBrush}'/>
            </Trigger>
            <Trigger Property='IsEnabled' Value='False'>
              <Setter TargetName='Glyph' Property='Opacity' Value='0.3'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
</ResourceDictionary>";
    }
}

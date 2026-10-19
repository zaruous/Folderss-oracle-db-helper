using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace MyPlugin
{
    internal enum PendingChoice
    {
        Commit,
        Rollback,
        /// <summary>돌아가기(끊기·닫기를 하지 않음)</summary>
        Cancel
    }

    internal enum SaveChoice
    {
        Save,
        /// <summary>저장하지 않고 닫기(변경을 버림)</summary>
        Discard,
        /// <summary>돌아가기(닫지 않음)</summary>
        Cancel
    }

    /// <summary>
    /// 확인 대화상자들. 모두 Theme.ApplyWindow로 테마를 맞춘 모달 창(Owner = 넘겨받은 창, 가운데 정렬, 크기 조절 없음, 작업 표시줄에 안 보임).
    /// 되돌릴 수 없는 선택은 경고 문구 + 확인 체크를 거쳐야 실행 버튼이 켜진다(Folderss 원칙). 기본값은 가장 안전한 선택.
    /// </summary>
    internal static class Dialogs
    {
        /// <summary>
        /// 위험한 문장 실행 확인. 제목 "위험한 문장 실행", 본문: DB 배지 + 접속 표시(user@host:port/service) + "에서 이 문장을 실행할까요?",
        /// 문장 텍스트(고정폭, 스크롤, 최대 높이 제한), 경고(statement.Danger, 위험색), 체크 "대상 DB와 영향을 확인했고 실행합니다", [실행](체크 전 비활성) [취소].
        /// 실행이면 true.
        /// </summary>
        public static bool ConfirmDanger(Window owner, OracleConnectionProfile profile, SqlStatement statement)
        {
            var window = DialogKit.Create(owner, DialogKit.TitleFor("위험한 문장 실행", profile), 470);
            var body = DialogKit.Body(window);
            body.Children.Add(DialogKit.Target(profile, profile == null
                ? "이 문장을 실행할까요?"
                : ConnectionManagerLogic.Address(profile) + "에서 이 문장을 실행할까요?"));
            body.Children.Add(DialogKit.SqlBox(statement == null ? "" : statement.Text, 10));
            if (statement != null && !string.IsNullOrWhiteSpace(statement.Danger))
                body.Children.Add(DialogKit.Warning("⚠ " + statement.Danger.Trim(), 10));
            var check = DialogKit.Check("대상 DB와 영향을 확인했고 실행합니다");
            body.Children.Add(check);

            var run = DialogKit.PrimaryButton("실행");
            var cancel = DialogKit.CancelButton("취소", true);
            DialogKit.EnableWhenChecked(run, check);
            run.Click += (s, e) =>
            {
                if (check.IsChecked == true)
                    window.DialogResult = true;
            };
            body.Children.Add(DialogKit.Buttons(run, cancel));
            DialogKit.FocusOnLoad(window, check);
            return window.ShowDialog() == true;
        }

        /// <summary>
        /// 커밋 대기 변경이 있는데 DDL을 실행할 때. "DDL을 실행하면 커밋하지 않은 변경(pendingText)도 함께 커밋되어 되돌릴 수 없습니다." + 확인 체크.
        /// 실행이면 true.
        /// </summary>
        public static bool ConfirmDdlWithPending(Window owner, OracleConnectionProfile profile, string pendingText)
        {
            var window = DialogKit.Create(owner, DialogKit.TitleFor("DDL 실행", profile), 470);
            var body = DialogKit.Body(window);
            body.Children.Add(DialogKit.Target(profile, ConnectionManagerLogic.Address(profile)));
            var pending = string.IsNullOrWhiteSpace(pendingText) ? "커밋하지 않은 변경" : "커밋하지 않은 변경(" + pendingText.Trim() + ")";
            body.Children.Add(DialogKit.Warning("⚠ DDL을 실행하면 " + pending + "도 함께 커밋되어 되돌릴 수 없습니다.", 10));
            body.Children.Add(DialogKit.Hint("변경을 버리려면 [취소]한 뒤 [롤백]하세요.", 6));
            var check = DialogKit.Check("함께 커밋되는 것을 확인했고 실행합니다");
            body.Children.Add(check);

            var run = DialogKit.PrimaryButton("실행");
            var cancel = DialogKit.CancelButton("취소", true);
            DialogKit.EnableWhenChecked(run, check);
            run.Click += (s, e) =>
            {
                if (check.IsChecked == true)
                    window.DialogResult = true;
            };
            body.Children.Add(DialogKit.Buttons(run, cancel));
            DialogKit.FocusOnLoad(window, check);
            return window.ShowDialog() == true;
        }

        /// <summary>
        /// 결과 편집 저장 확인. 제목 "결과 편집 저장 · 접속", 본문: "table에 DELETE m행 · UPDATE n행 · INSERT k행을 보낼까요?", 실행할 문장들(고정폭, 스크롤),
        /// 삭제가 있으면 경고 + 확인 체크(체크 전 [실행] 비활성), 안내 "커밋 전까지 [롤백]으로 되돌릴 수 있습니다". 실행이면 true.
        /// </summary>
        public static bool ConfirmRowEdits(Window owner, OracleConnectionProfile profile, string table, string statements, int updates, int deletes, int inserts)
        {
            var window = DialogKit.Create(owner, DialogKit.TitleFor("결과 편집 저장", profile), 560);
            var body = DialogKit.Body(window);
            var what = new System.Collections.Generic.List<string>();
            if (deletes > 0)
                what.Add("DELETE " + deletes.ToString(System.Globalization.CultureInfo.InvariantCulture) + "행");
            if (updates > 0)
                what.Add("UPDATE " + updates.ToString(System.Globalization.CultureInfo.InvariantCulture) + "행");
            if (inserts > 0)
                what.Add("INSERT " + inserts.ToString(System.Globalization.CultureInfo.InvariantCulture) + "행");
            // 접속 이름은 창 제목(결과 편집 저장 · 접속)에 있으므로 본문에는 배지 없이 질문만 둔다(행 편집 창과 같게)
            body.Children.Add(DialogKit.Text((table ?? "") + "에 " + string.Join(" · ", what) + "을(를) 보낼까요?", 0));
            var box = DialogKit.SqlBox(statements ?? "", 10);
            box.MaxHeight = 240;
            box.TextWrapping = TextWrapping.NoWrap;
            box.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            body.Children.Add(box);
            body.Children.Add(DialogKit.Hint("행마다 문장 하나를 DELETE → UPDATE → INSERT 순으로 실행합니다(DELETE·UPDATE는 기본 키로 행을 찾음). 커밋 전까지 [롤백]으로 되돌릴 수 있고, 오류가 나면 그 행에서 멈춥니다.", 8));
            CheckBox check = null;
            if (deletes > 0)
            {
                body.Children.Add(DialogKit.Warning("⚠ 삭제 표시한 행은 DELETE됩니다. 커밋하면 되돌릴 수 없습니다.", 8));
                check = DialogKit.Check("대상 DB와 삭제할 행을 확인했고 실행합니다");
                body.Children.Add(check);
            }

            var run = DialogKit.PrimaryButton("실행");
            var cancel = DialogKit.CancelButton("취소", true);
            if (check != null)
                DialogKit.EnableWhenChecked(run, check);
            run.Click += (s, e) =>
            {
                if (check == null || check.IsChecked == true)
                    window.DialogResult = true;
            };
            body.Children.Add(DialogKit.Buttons(run, cancel));
            DialogKit.FocusOnLoad(window, check != null ? (IInputElement)check : cancel);
            return window.ShowDialog() == true;
        }

        /// <summary>
        /// 편집 막대 [다시 조회] 전: 저장하지 않은 수정·삭제 표시를 버릴지. 기본 버튼은 [취소](Enter를 잘못 눌러도 버리지 않게). 버리면 true.
        /// </summary>
        public static bool ConfirmDiscardEdits(Window owner, OracleConnectionProfile profile, string table, string pendingText)
        {
            var window = DialogKit.Create(owner, DialogKit.TitleFor("다시 조회", profile), 440);
            var body = DialogKit.Body(window);
            body.Children.Add(DialogKit.Text((table ?? "") + "에 저장하지 않은 변경(" + (pendingText ?? "") + ")이 있습니다. 버리고 다시 조회할까요?", 0));
            body.Children.Add(DialogKit.Hint("버리지 않으려면 [취소]한 뒤 [저장]하세요.", 8));
            var discard = DialogKit.PrimaryButton("버리고 조회");
            var cancel = DialogKit.CancelButton("취소", true);
            discard.Click += (s, e) => window.DialogResult = true;
            body.Children.Add(DialogKit.Buttons(discard, cancel));
            DialogKit.FocusOnLoad(window, cancel);
            return window.ShowDialog() == true;
        }

        /// <summary>
        /// 행 편집 창을 [취소]·Esc·X로 닫을 때 바꾼 칸이 있으면: 버리고 닫을지. title은 편집 창 제목 앞머리("행 편집"·"행 추가"…).
        /// 기본 버튼(Enter·Esc)은 [계속 편집] — 잘못 눌러도 고친 값을 버리지 않게. 버리면 true.
        /// </summary>
        public static bool ConfirmDiscardRowEdit(Window owner, OracleConnectionProfile profile, string title)
        {
            var window = DialogKit.Create(owner, DialogKit.TitleFor(string.IsNullOrWhiteSpace(title) ? "행 편집" : title, profile), 400);
            var body = DialogKit.Body(window);
            body.Children.Add(DialogKit.Text("바꾼 칸이 있습니다. 버리고 닫을까요?", 0));
            body.Children.Add(DialogKit.Hint("고친 값을 남기려면 [계속 편집]한 뒤 [확인]하세요.", 8));
            var discard = DialogKit.PrimaryButton("버리고 닫기");
            var keep = DialogKit.CancelButton("계속 편집", true);
            keep.Margin = new Thickness(8, 0, 0, 0);
            discard.Click += (s, e) => window.DialogResult = true;
            body.Children.Add(DialogKit.Buttons(discard, keep));
            DialogKit.FocusOnLoad(window, keep);
            return window.ShowDialog() == true;
        }

        /// <summary>
        /// 연결을 끊거나 창을 닫기 전 커밋 대기 변경 처리. action은 "연결을 끊기" / "창을 닫기" 같은 동사구.
        /// 라디오: 롤백(기본, "변경을 버립니다") / 커밋("변경을 DB에 반영합니다"), 버튼 [확인] [돌아가기].
        /// </summary>
        public static PendingChoice AskPending(Window owner, OracleConnectionProfile profile, string pendingText, string action)
        {
            var window = DialogKit.Create(owner, DialogKit.TitleFor("커밋하지 않은 변경", profile), 470);
            var body = DialogKit.Body(window);
            body.Children.Add(DialogKit.Target(profile, ConnectionManagerLogic.Address(profile)));
            var pending = string.IsNullOrWhiteSpace(pendingText) ? "" : "(" + pendingText.Trim() + ")";
            var question = string.IsNullOrWhiteSpace(action) ? "어떻게 할까요?" : action.Trim() + " 전에 어떻게 할까요?";
            body.Children.Add(DialogKit.Text("커밋하지 않은 변경이 있습니다" + pending + ". " + question, 10));
            var rollback = DialogKit.Radio("pending", "롤백", "변경을 버립니다. (기본)", true);
            var commit = DialogKit.Radio("pending", "커밋", "변경을 DB에 반영합니다.", false);
            body.Children.Add(rollback);
            body.Children.Add(commit);

            // 기본 버튼은 아무것도 하지 않는 [돌아가기]: Enter를 잘못 눌러도 변경을 버리거나 반영하지 않게
            var ok = DialogKit.PrimaryButton("확인");
            var back = DialogKit.CancelButton("돌아가기", true);
            ok.Click += (s, e) => window.DialogResult = true;
            body.Children.Add(DialogKit.Buttons(ok, back));
            DialogKit.FocusOnLoad(window, rollback);
            if (window.ShowDialog() != true)
                return PendingChoice.Cancel;
            return commit.IsChecked == true ? PendingChoice.Commit : PendingChoice.Rollback;
        }

        /// <summary>
        /// 탭을 닫기 전 저장 확인: "'orders.sql'에 저장하지 않은 변경이 있습니다(경로). 저장할까요?" [저장] [저장 안 함] [취소].
        /// 기본 버튼(Enter·Esc)은 [취소] — 잘못 눌러도 변경을 버리지 않게.
        /// </summary>
        public static SaveChoice AskSaveChanges(Window owner, string title, string filePath)
        {
            var window = DialogKit.Create(owner, "저장하지 않은 SQL", 440);
            var body = DialogKit.Body(window);
            var what = filePath != null
                ? "'" + title + "'에 저장하지 않은 변경이 있습니다." + Environment.NewLine + filePath
                : "'" + title + "' 탭의 SQL을 파일로 저장한 적이 없습니다.";
            body.Children.Add(DialogKit.Text(what + Environment.NewLine + Environment.NewLine + "탭을 닫기 전에 저장할까요?", 0));
            var result = SaveChoice.Cancel;
            var save = DialogKit.PrimaryButton(filePath != null ? "저장" : "저장…");
            var discard = DialogKit.PlainButton("저장 안 함");
            discard.Margin = new Thickness(8, 0, 0, 0);
            var cancel = DialogKit.CancelButton("취소", true);
            cancel.Margin = new Thickness(8, 0, 0, 0);
            save.Click += (s, e) =>
            {
                result = SaveChoice.Save;
                window.DialogResult = true;
            };
            discard.Click += (s, e) =>
            {
                result = SaveChoice.Discard;
                window.DialogResult = true;
            };
            body.Children.Add(DialogKit.Buttons(save, discard, cancel));
            DialogKit.FocusOnLoad(window, cancel);
            return window.ShowDialog() == true ? result : SaveChoice.Cancel;
        }

        /// <summary>
        /// 예/아니요 확인(기본은 [취소]). danger면 경고색 문구와 확인 체크를 켜야 [yes]가 눌린다(되돌릴 수 없는 일 — 이력 지우기 등).
        /// </summary>
        public static bool Confirm(Window owner, string title, string message, string yes, bool danger)
        {
            var window = DialogKit.Create(owner, string.IsNullOrWhiteSpace(title) ? "DB Helper" : title, 440);
            var body = DialogKit.Body(window);
            var text = new TextBlock { Text = message ?? "", TextWrapping = TextWrapping.Wrap };
            if (danger)
                text.Foreground = Theme.Danger;
            else
                Theme.Foreground(text);
            body.Children.Add(text);
            var ok = danger ? DialogKit.PlainButton(yes) : DialogKit.PrimaryButton(yes);
            var cancel = DialogKit.CancelButton("취소", true);
            if (danger)
            {
                var check = DialogKit.Check("되돌릴 수 없는 것을 알고 진행합니다");
                check.Margin = new Thickness(0, 10, 0, 0);
                body.Children.Add(check);
                DialogKit.EnableWhenChecked(ok, check);
            }
            var result = false;
            ok.Click += (s, e) =>
            {
                result = true;
                window.DialogResult = true;
            };
            body.Children.Add(DialogKit.Buttons(ok, cancel));
            DialogKit.FocusOnLoad(window, cancel);
            window.ShowDialog();
            return result;
        }

        /// <summary>단순 안내(확인 버튼 하나). error면 위험색 아이콘·문구.</summary>
        public static void Show(Window owner, string title, string message, bool error)
        {
            var window = DialogKit.Create(owner, string.IsNullOrWhiteSpace(title) ? "DB Helper" : title, 440);
            var body = DialogKit.Body(window);
            var row = new DockPanel();
            var icon = DialogKit.Icon(error);
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            var text = new TextBlock { Text = message ?? "", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            if (error)
                text.Foreground = Theme.Danger;
            else
                Theme.Foreground(text);
            row.Children.Add(new ScrollViewer
            {
                Content = text,
                MaxHeight = 360,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false
            });
            body.Children.Add(row);

            var ok = DialogKit.CancelButton("확인", true);
            body.Children.Add(DialogKit.Buttons(ok));
            // 오류 문구(ORA-번호 등)를 그대로 옮겨 적을 수 있게 Ctrl+C로 복사한다(Windows 메시지 상자와 같음).
            window.KeyDown += (s, e) =>
            {
                if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
                {
                    DialogKit.TryCopy(window.Title + Environment.NewLine + (message ?? ""));
                    e.Handled = true;
                }
            };
            DialogKit.FocusOnLoad(window, ok);
            window.ShowDialog();
        }
    }

    /// <summary>비밀번호 입력(저장 안 함). 이번 연결에만 쓴다.</summary>
    internal static class PasswordPrompt
    {
        /// <summary>
        /// 제목 "비밀번호 입력 · {이름}", 접속 표시, 이유(reason) 한 줄, PasswordBox, [연결](빈 값이면 비활성) [취소]. Enter로 연결.
        /// 입력한 비밀번호, 취소면 null.
        /// </summary>
        public static string Ask(Window owner, OracleConnectionProfile profile, string reason)
        {
            var window = DialogKit.Create(owner, DialogKit.TitleFor("비밀번호 입력", profile), 420);
            var body = DialogKit.Body(window);
            body.Children.Add(DialogKit.Target(profile, ConnectionManagerLogic.Address(profile)));
            body.Children.Add(DialogKit.Hint(string.IsNullOrWhiteSpace(reason) ? "입력한 비밀번호는 저장하지 않고 이번 연결에만 씁니다." : reason.Trim(), 8));
            var box = DialogKit.PasswordInput();
            box.Margin = new Thickness(0, 10, 0, 0);
            AutomationProperties.SetName(box, "비밀번호");
            body.Children.Add(box);
            var capsLock = DialogKit.Hint("Caps Lock이 켜져 있습니다.", 4);
            capsLock.Foreground = Theme.Warning;
            capsLock.Visibility = Visibility.Collapsed;
            body.Children.Add(capsLock);

            var connect = DialogKit.PrimaryButton("연결");
            connect.IsDefault = true;
            connect.IsEnabled = false;
            var cancel = DialogKit.CancelButton("취소", false);
            box.PasswordChanged += (s, e) => connect.IsEnabled = box.Password.Length > 0;
            connect.Click += (s, e) =>
            {
                if (box.Password.Length > 0)
                    window.DialogResult = true;
            };
            Action updateCapsLock = () => capsLock.Visibility = Keyboard.IsKeyToggled(Key.CapsLock) ? Visibility.Visible : Visibility.Collapsed;
            box.KeyUp += (s, e) => updateCapsLock();
            box.GotKeyboardFocus += (s, e) => updateCapsLock();
            body.Children.Add(DialogKit.Buttons(connect, cancel));
            DialogKit.FocusOnLoad(window, box);

            var ok = window.ShowDialog() == true;
            var password = ok ? box.Password : null;
            box.Clear();
            return string.IsNullOrEmpty(password) ? null : password;
        }
    }

    /// <summary>플러그인 대화상자의 공통 틀(Folderss GitDialogBase와 같은 모양). XAML 없이 코드로 만든다.</summary>
    internal static class DialogKit
    {
        /// <summary>내용 높이에 맞추고 크기 조절은 막은 모달용 창.</summary>
        public static Window Create(Window owner, string title, double width)
        {
            var window = new Window
            {
                Title = title,
                Width = width,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false
            };
            Theme.ApplyWindow(window);
            SetOwner(window, owner);
            return window;
        }

        /// <summary>Owner를 지정하고 그 가운데에 띄운다. 쓸 수 있는 창이 없으면 화면 가운데.</summary>
        public static void SetOwner(Window window, Window owner)
        {
            var target = owner ?? ActiveWindow();
            // 한 번도 보이지 않은(핸들이 없는) 창이나 다른 스레드의 창은 Owner로 지정할 수 없다(InvalidOperationException).
            if (target != null && target != window && target.CheckAccess() && new WindowInteropHelper(target).Handle != IntPtr.Zero)
            {
                window.Owner = target;
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
            {
                window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
        }

        /// <summary>"제목 · 접속 이름"(이름이 없으면 제목만).</summary>
        public static string TitleFor(string title, OracleConnectionProfile profile)
        {
            return profile == null || string.IsNullOrWhiteSpace(profile.Name) ? title : title + " · " + profile.Name.Trim();
        }

        public static StackPanel Body(Window window)
        {
            var body = new StackPanel { Margin = new Thickness(16) };
            window.Content = body;
            return body;
        }

        public static TextBlock Text(string text, double top)
        {
            return Theme.Foreground(new TextBlock { Text = text ?? "", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0) });
        }

        public static TextBlock Hint(string text, double top)
        {
            var block = Theme.Secondary(text ?? "");
            block.TextWrapping = TextWrapping.Wrap;
            block.FontSize = 12;
            block.Margin = new Thickness(0, top, 0, 0);
            return block;
        }

        public static TextBlock Warning(string text, double top)
        {
            return new TextBlock { Text = text ?? "", TextWrapping = TextWrapping.Wrap, Foreground = Theme.Danger, Margin = new Thickness(0, top, 0, 0) };
        }

        /// <summary>DB 배지 + 글(접속 표시나 질문). 글이 길면 배지 옆에서 줄을 바꾼다.</summary>
        public static FrameworkElement Target(OracleConnectionProfile profile, string text)
        {
            var row = new DockPanel();
            var badge = Theme.DbBadge(profile);
            badge.VerticalAlignment = VerticalAlignment.Top;
            badge.Margin = new Thickness(0, 1, 8, 0);
            DockPanel.SetDock(badge, Dock.Left);
            row.Children.Add(badge);
            row.Children.Add(Theme.Foreground(new TextBlock { Text = text ?? "", TextWrapping = TextWrapping.Wrap }));
            return row;
        }

        /// <summary>읽기 전용 문장 보기(고정폭, 줄 바꿈, 높이 제한 + 세로 스크롤). 선택해 복사할 수 있다.</summary>
        public static TextBox SqlBox(string sql, double top)
        {
            var box = new TextBox
            {
                Text = sql ?? "",
                IsReadOnly = true,
                FontFamily = Theme.Mono,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalContentAlignment = VerticalAlignment.Top,
                MaxHeight = 160,
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, top, 0, 0)
            };
            box.SetResourceReference(Control.BackgroundProperty, Theme.SurfaceBackground);
            AutomationProperties.SetName(box, "실행할 문장");
            return box;
        }

        /// <summary>CheckBox는 Folderss 테마 스타일이 없어 글자색만 연결한다(상자는 기본 모양이어야 체크 표시가 보인다).</summary>
        public static CheckBox Check(string text)
        {
            return Theme.Foreground(new CheckBox { Content = text, Margin = new Thickness(0, 12, 0, 0) });
        }

        public static RadioButton Radio(string group, string title, string hint, bool isChecked)
        {
            var content = new StackPanel();
            content.Children.Add(Theme.Text(title));
            var hintText = Theme.Secondary(hint);
            hintText.TextWrapping = TextWrapping.Wrap;
            content.Children.Add(hintText);
            var radio = new RadioButton
            {
                GroupName = group,
                Content = content,
                IsChecked = isChecked,
                VerticalContentAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 10, 0, 0)
            };
            AutomationProperties.SetName(radio, title);
            return Theme.Foreground(radio);
        }

        /// <summary>주 동작 버튼: Folderss 버튼 모양에 강조색 테두리·굵은 글자(배경을 바꾸면 마우스를 올렸을 때 글자가 안 보인다).</summary>
        public static Button PrimaryButton(string text)
        {
            var button = new Button { Content = text, MinWidth = 80, FontWeight = FontWeights.SemiBold };
            button.SetResourceReference(Control.BorderBrushProperty, Theme.Accent);
            return button;
        }

        public static Button PlainButton(string text)
        {
            return new Button { Content = text, MinWidth = 80 };
        }

        /// <summary>Esc로 눌리는 버튼(누르면 DialogResult = false로 닫힘). isDefault면 Enter도 이 버튼.</summary>
        public static Button CancelButton(string text, bool isDefault)
        {
            var button = PlainButton(text);
            button.IsCancel = true;
            button.IsDefault = isDefault;
            return button;
        }

        public static FrameworkElement Buttons(params Button[] buttons)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 13, 0, 0) };
            foreach (var button in buttons)
                panel.Children.Add(button);
            return panel;
        }

        public static void EnableWhenChecked(Button button, CheckBox check)
        {
            button.IsEnabled = check.IsChecked == true;
            check.Checked += (s, e) => button.IsEnabled = true;
            check.Unchecked += (s, e) => button.IsEnabled = false;
        }

        /// <summary>PasswordBox는 Folderss 테마 스타일이 없어 TextBox와 같은 색·여백을 직접 연결한다.</summary>
        public static PasswordBox PasswordInput()
        {
            var box = new PasswordBox
            {
                Padding = new Thickness(8, 5, 8, 5),
                BorderThickness = new Thickness(1),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            box.SetResourceReference(Control.BackgroundProperty, Theme.ControlBackground);
            box.SetResourceReference(Control.ForegroundProperty, Theme.PrimaryText);
            box.SetResourceReference(Control.BorderBrushProperty, Theme.Border);
            box.SetResourceReference(PasswordBox.CaretBrushProperty, Theme.PrimaryText);
            box.SetResourceReference(PasswordBox.SelectionBrushProperty, Theme.Selection);
            return box;
        }

        /// <summary>안내 아이콘(원 안의 i, 오류면 위험색 원 안의 !).</summary>
        public static FrameworkElement Icon(bool error)
        {
            var glyph = new TextBlock
            {
                Text = error ? "!" : "i",
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var circle = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Child = glyph
            };
            if (error)
                circle.Background = Theme.Danger;
            else
                circle.SetResourceReference(Border.BackgroundProperty, Theme.Accent);
            return circle;
        }

        public static void FocusOnLoad(Window window, IInputElement element)
        {
            window.Loaded += (s, e) => element.Focus();
        }

        /// <summary>클립보드에 쓴다. 다른 프로그램이 클립보드를 잡고 있으면 실패할 수 있어 성공 여부만 돌려준다.</summary>
        public static bool TryCopy(string text)
        {
            try
            {
                Clipboard.SetText(text ?? "");
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static Window ActiveWindow()
        {
            var app = Application.Current;
            if (app == null || !app.CheckAccess())
                return null;
            foreach (Window window in app.Windows)
            {
                if (window.IsActive)
                    return window;
            }
            return app.MainWindow;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace MyPlugin
{
    /// <summary>
    /// 일일 점검 [항목 관리…](모달). 바꾼 내용은 그때그때 history.db에 저장한다(창의 [닫기]는 저장과 무관).
    /// - 왼쪽 기본 항목: 켬·끔, 임계값 규칙이면 주의·위험 문턱(비우거나 기본값과 같으면 덮어쓰기를 지운다). 모든 DB에 적용(전체 '').
    /// - 오른쪽 사용자 항목: 추가·고치기(<see cref="EditItem"/> — SELECT 한 문장, 판정 규칙, [시험 실행]으로 결과 열을 골라 쓴다)·지우기, 켬·끔,
    ///   JSON 내보내기·가져오기(같은 id가 있으면 덮어쓸지 묻는다).
    /// </summary>
    internal static class CheckItemsDialog
    {
        internal sealed class Host
        {
            public MonitorStore Store { get; set; }
            public Func<IReadOnlyList<OracleConnectionProfile>> Profiles { get; set; }
            public Func<string, Task<DbSession>> Connect { get; set; }
            public string HelpFolder { get; set; }
        }

        public const string Title = "점검 항목 관리";

        public static void Show(Window owner, Host host)
        {
            var window = new Window
            {
                Title = Title,
                Width = 1040,
                Height = 640,
                MinWidth = 720,
                MinHeight = 400,
                ShowInTaskbar = false,
                ResizeMode = ResizeMode.CanResizeWithGrip
            };
            Theme.ApplyWindow(window);
            DialogKit.SetOwner(window, owner);
            var view = new View(window, host);
            window.Content = view.Build();
            window.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    window.Close();
                }
            };
            view.Reload();
            window.ShowDialog();
        }

        /// <summary>점검을 지원하는 DBMS(점검 묶음이 있는 공급자).</summary>
        public static List<IDbProvider> Kinds()
        {
            return DbProviders.All.Where(p => p.Monitor.CheckSet != null).ToList();
        }

        private sealed class View
        {
            private readonly Window _window;
            private readonly Host _host;
            private StackPanel _builtIn;
            private ListBox _userList;
            private TextBlock _status;
            private Dictionary<string, CheckOverride> _overrides = new Dictionary<string, CheckOverride>(StringComparer.Ordinal);
            private List<UserCheckItem> _items = new List<UserCheckItem>();

            public View(Window window, Host host)
            {
                _window = window;
                _host = host;
            }

            public FrameworkElement Build()
            {
                var root = new DockPanel { Margin = new Thickness(14, 12, 14, 10) };
                var hint = Theme.Secondary("바꾼 내용은 바로 저장됩니다. 켬·끔과 임계값은 모든 DB에 적용됩니다. 저장 위치: " + _host.Store.FilePath);
                hint.TextWrapping = TextWrapping.Wrap;
                hint.Margin = new Thickness(0, 0, 0, 8);
                DockPanel.SetDock(hint, Dock.Top);
                root.Children.Add(hint);

                var foot = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
                var close = DialogKit.CancelButton("닫기", true);
                DockPanel.SetDock(close, Dock.Right);
                foot.Children.Add(close);
                var help = DialogKit.PlainButton("도움말");
                help.Margin = new Thickness(0, 0, 6, 0);
                help.Click += (s, e) => HelpWindow.Show(_window, _host.HelpFolder, HelpLogic.Monitor);
                DockPanel.SetDock(help, Dock.Right);
                foot.Children.Add(help);
                _status = Theme.Secondary("");
                _status.VerticalAlignment = VerticalAlignment.Center;
                _status.TextWrapping = TextWrapping.Wrap;
                AutomationProperties.SetAutomationId(_status, "check-items-status");
                foot.Children.Add(_status);
                DockPanel.SetDock(foot, Dock.Bottom);
                root.Children.Add(foot);

                var body = new Grid();
                body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
                body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var left = Section("기본 항목", out _builtIn);
                Grid.SetColumn(left, 0);
                body.Children.Add(left);
                var right = UserPanel();
                Grid.SetColumn(right, 2);
                body.Children.Add(right);
                root.Children.Add(body);
                return root;
            }

            private static FrameworkElement Section(string title, out StackPanel content)
            {
                var panel = new DockPanel();
                var label = Theme.Text(title);
                label.FontWeight = FontWeights.SemiBold;
                label.Margin = new Thickness(0, 0, 0, 4);
                DockPanel.SetDock(label, Dock.Top);
                panel.Children.Add(label);
                content = new StackPanel();
                AutomationProperties.SetName(content, title);
                var frame = new Border
                {
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 6, 8, 6),
                    Child = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false }
                };
                frame.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
                Theme.Background(frame, Theme.PanelBackground);
                panel.Children.Add(frame);
                return panel;
            }

            private FrameworkElement UserPanel()
            {
                var panel = new DockPanel();
                var label = Theme.Text("사용자 항목");
                label.FontWeight = FontWeights.SemiBold;
                label.Margin = new Thickness(0, 0, 0, 4);
                DockPanel.SetDock(label, Dock.Top);
                panel.Children.Add(label);

                var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
                var add = Small("추가…");
                add.Click += (s, e) => Edit(null);
                buttons.Children.Add(add);
                var edit = Small("고치기…");
                edit.Click += (s, e) => Edit(SelectedItem());
                buttons.Children.Add(edit);
                var delete = Small("지우기");
                delete.Click += (s, e) => Delete(SelectedItem());
                buttons.Children.Add(delete);
                var export = Small("내보내기…");
                export.Click += (s, e) => Export();
                buttons.Children.Add(export);
                var import = Small("가져오기…");
                import.Click += (s, e) => Import();
                buttons.Children.Add(import);
                DockPanel.SetDock(buttons, Dock.Bottom);
                panel.Children.Add(buttons);

                _userList = new ListBox { BorderThickness = new Thickness(1), HorizontalContentAlignment = HorizontalAlignment.Stretch };
                _userList.SetResourceReference(Control.BorderBrushProperty, Theme.Border);
                Theme.Background(_userList, Theme.PanelBackground);
                _userList.MouseDoubleClick += (s, e) =>
                {
                    if (SelectedItem() != null)
                        Edit(SelectedItem());
                };
                _userList.SelectionChanged += (s, e) =>
                {
                    edit.IsEnabled = delete.IsEnabled = SelectedItem() != null;
                };
                edit.IsEnabled = delete.IsEnabled = false;
                AutomationProperties.SetName(_userList, "사용자 항목");
                panel.Children.Add(_userList);
                return panel;
            }

            private static Button Small(string text)
            {
                return new Button { Content = text, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 6, 4) };
            }

            private UserCheckItem SelectedItem()
            {
                var item = _userList.SelectedItem as FrameworkElement;
                return item == null ? null : item.Tag as UserCheckItem;
            }

            public void Reload()
            {
                try
                {
                    _overrides = _host.Store.LoadOverrides(null);
                    _items = _host.Store.LoadUserItems();
                }
                catch (Exception ex)
                {
                    SetStatus("history.db를 읽지 못했습니다: " + ex.Message, true);
                }
                FillBuiltIn();
                FillUser();
            }

            private void SetStatus(string text, bool error)
            {
                _status.Text = text ?? "";
                if (error)
                    _status.Foreground = Theme.Danger;
                else
                    _status.SetResourceReference(TextBlock.ForegroundProperty, Theme.SecondaryText);
            }

            // ---------- 기본 항목 ----------

            private void FillBuiltIn()
            {
                _builtIn.Children.Clear();
                foreach (var provider in Kinds())
                {
                    var kind = Theme.Text(provider.DisplayName, Theme.SecondaryText);
                    kind.Margin = new Thickness(0, 0, 0, 4);
                    _builtIn.Children.Add(kind);
                    string category = null;
                    foreach (var check in provider.Monitor.CheckSet.Items.OrderBy(c => CheckLogic.CategoryOrder(c.Category)))
                    {
                        if (check.Category != category)
                        {
                            category = check.Category;
                            var label = Theme.Secondary(category);
                            label.Margin = new Thickness(0, 6, 0, 2);
                            _builtIn.Children.Add(label);
                        }
                        _builtIn.Children.Add(BuiltInRow(check));
                    }
                }
            }

            private FrameworkElement BuiltInRow(MonitorCheck check)
            {
                CheckOverride current;
                _overrides.TryGetValue(check.Id, out current);
                var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
                if (check.Rule.Kind == CheckRuleKind.Threshold)
                {
                    var limits = new StackPanel { Orientation = Orientation.Horizontal };
                    var warn = Limit(check, current == null ? null : current.Warning, check.Rule.Warning, true);
                    var crit = Limit(check, current == null ? null : current.Critical, check.Rule.Critical, false);
                    limits.Children.Add(Theme.Secondary("주의 "));
                    limits.Children.Add(warn);
                    limits.Children.Add(Theme.Secondary(" 위험 "));
                    limits.Children.Add(crit);
                    foreach (TextBlock text in limits.Children.OfType<TextBlock>())
                        text.VerticalAlignment = VerticalAlignment.Center;
                    DockPanel.SetDock(limits, Dock.Right);
                    row.Children.Add(limits);
                }
                var enabled = Theme.Foreground(new CheckBox
                {
                    // 글을 TextBlock으로 — 문자열이면 '_'가 단축 밑줄로 먹힌다(DBMS_JOB → DBMSJOB)
                    Content = Theme.Text(check.Title),
                    IsChecked = current == null || current.Enabled != false,
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = CheckLogic.RuleText(check.Rule) + (string.IsNullOrEmpty(check.Advice) ? "" : Environment.NewLine + check.Advice)
                });
                AutomationProperties.SetAutomationId(enabled, "check-item-enabled:" + check.Id);
                AutomationProperties.SetName(enabled, check.Title);
                enabled.Click += (s, e) => SaveEnabled(check.Id, enabled.IsChecked == true);
                row.Children.Add(enabled);
                return row;
            }

            /// <summary>임계값 칸: 덮어쓰기가 없으면 기본값을 흐리게 보인다. Enter·포커스를 떠날 때 저장(기본값과 같거나 비우면 덮어쓰기를 지운다).</summary>
            private TextBox Limit(MonitorCheck check, double? value, double? fallback, bool warning)
            {
                var box = new TextBox { Width = 56, Padding = new Thickness(4, 1, 4, 1), HorizontalContentAlignment = HorizontalAlignment.Right };
                box.Text = value.HasValue ? CheckLogic.NumberText(value.Value) : fallback.HasValue ? CheckLogic.NumberText(fallback.Value) : "";
                if (!value.HasValue)
                    box.SetResourceReference(Control.ForegroundProperty, Theme.SecondaryText);
                box.ToolTip = (warning ? "주의" : "위험") + " 문턱 — 기본 " + (fallback.HasValue ? CheckLogic.NumberText(fallback.Value) : "없음") + " (" + check.Rule.Column + ")";
                AutomationProperties.SetAutomationId(box, "check-item-" + (warning ? "warn" : "crit") + ":" + check.Id);
                AutomationProperties.SetName(box, check.Title + (warning ? " 주의" : " 위험"));
                var last = box.Text;
                Action commit = () =>
                {
                    if (box.Text == last)
                        return;
                    double parsed;
                    var text = box.Text.Trim();
                    double? next = null;
                    if (text.Length > 0)
                    {
                        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                        {
                            SetStatus("'" + check.Title + "'의 " + (warning ? "주의" : "위험") + " 문턱은 숫자여야 합니다: " + text, true);
                            return;
                        }
                        next = parsed;
                    }
                    if (next.HasValue && fallback.HasValue && Math.Abs(next.Value - fallback.Value) < 1e-9)
                        next = null;
                    if (SaveLimit(check.Id, warning, next))
                    {
                        last = box.Text = next.HasValue ? CheckLogic.NumberText(next.Value) : fallback.HasValue ? CheckLogic.NumberText(fallback.Value) : "";
                        if (next.HasValue)
                            box.SetResourceReference(Control.ForegroundProperty, Theme.PrimaryText);
                        else
                            box.SetResourceReference(Control.ForegroundProperty, Theme.SecondaryText);
                    }
                };
                box.LostKeyboardFocus += (s, e) => commit();
                box.KeyDown += (s, e) =>
                {
                    if (e.Key == Key.Enter)
                    {
                        e.Handled = true;
                        commit();
                    }
                };
                return box;
            }

            private CheckOverride Current(string id)
            {
                CheckOverride current;
                if (!_overrides.TryGetValue(id, out current))
                    _overrides[id] = current = new CheckOverride { ItemId = id };
                return current;
            }

            private void SaveEnabled(string id, bool enabled)
            {
                var current = Current(id);
                var before = current.Enabled;
                current.Enabled = enabled ? (bool?)null : false;
                if (!Save(current))
                    current.Enabled = before;
                else
                    SetStatus((enabled ? "켰습니다: " : "껐습니다: ") + id, false);
            }

            private bool SaveLimit(string id, bool warning, double? value)
            {
                var current = Current(id);
                var before = warning ? current.Warning : current.Critical;
                if (warning)
                    current.Warning = value;
                else
                    current.Critical = value;
                if (Save(current))
                {
                    SetStatus("임계값을 저장했습니다: " + id + (value.HasValue ? " " + (warning ? "주의 " : "위험 ") + CheckLogic.NumberText(value.Value) : " (기본값)"), false);
                    return true;
                }
                if (warning)
                    current.Warning = before;
                else
                    current.Critical = before;
                return false;
            }

            private bool Save(CheckOverride value)
            {
                try
                {
                    _host.Store.SaveOverride(null, value);
                    return true;
                }
                catch (Exception ex)
                {
                    SetStatus("저장하지 못했습니다: " + ex.Message, true);
                    return false;
                }
            }

            // ---------- 사용자 항목 ----------

            private void FillUser()
            {
                var selected = SelectedItem();
                _userList.Items.Clear();
                foreach (var item in _items)
                {
                    CheckOverride current;
                    _overrides.TryGetValue(item.Id, out current);
                    var row = new DockPanel { Tag = item, Margin = new Thickness(2, 3, 2, 3) };
                    var enabled = Theme.Foreground(new CheckBox { IsChecked = current == null || current.Enabled != false, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 6, 0), ToolTip = "켬·끔" });
                    var id = item.Id;
                    enabled.Click += (s, e) => SaveEnabled(id, enabled.IsChecked == true);
                    AutomationProperties.SetAutomationId(enabled, "check-item-enabled:" + item.Id);
                    AutomationProperties.SetName(enabled, item.Title + " 켬");
                    DockPanel.SetDock(enabled, Dock.Left);
                    row.Children.Add(enabled);
                    var text = new StackPanel();
                    text.Children.Add(Theme.Text(item.Title));
                    string rule;
                    try
                    {
                        rule = CheckLogic.RuleText(CheckLogic.RuleFromJson(item.Rule));
                    }
                    catch (FormatException ex)
                    {
                        rule = "규칙 오류: " + ex.Message;
                    }
                    var provider = DbProviders.For(item.Kind);
                    var sub = Theme.Secondary(provider.DisplayName + " · " + item.Category + " · " + rule);
                    sub.TextTrimming = TextTrimming.CharacterEllipsis;
                    text.Children.Add(sub);
                    row.Children.Add(text);
                    AutomationProperties.SetName(row, item.Title);
                    var listItem = new ListBoxItem { Content = row, Tag = item };
                    AutomationProperties.SetAutomationId(listItem, "check-user-item:" + item.Title);
                    AutomationProperties.SetName(listItem, item.Title);
                    _userList.Items.Add(listItem);
                    if (selected != null && selected.Id == item.Id)
                        _userList.SelectedItem = listItem;
                }
                if (_items.Count == 0)
                    _userList.Items.Add(new ListBoxItem { Content = Theme.Secondary("사용자 항목이 없습니다. [추가…]로 SELECT 한 문장과 판정 규칙을 넣으세요."), IsEnabled = false });
            }

            private void Edit(UserCheckItem item)
            {
                if (Kinds().Count == 0)
                    return;
                var saved = EditItem(_window, _host, item);
                if (saved == null)
                    return;
                SetStatus("저장했습니다: " + saved.Title, false);
                Reload();
                foreach (ListBoxItem listItem in _userList.Items)
                {
                    var tagged = listItem.Tag as UserCheckItem;
                    if (tagged != null && tagged.Id == saved.Id)
                        _userList.SelectedItem = listItem;
                }
            }

            private void Delete(UserCheckItem item)
            {
                if (item == null)
                    return;
                if (!Dialogs.Confirm(_window, Title, "'" + item.Title + "' 항목을 지울까요? 켬·끔·임계값 설정도 함께 지워집니다. 지난 점검 기록은 남습니다.", "지우기", false))
                    return;
                try
                {
                    _host.Store.DeleteUserItem(item.Id);
                    SetStatus("지웠습니다: " + item.Title, false);
                }
                catch (Exception ex)
                {
                    SetStatus("지우지 못했습니다: " + ex.Message, true);
                }
                Reload();
            }

            private void Export()
            {
                if (_items.Count == 0)
                {
                    SetStatus("내보낼 사용자 항목이 없습니다.", true);
                    return;
                }
                try
                {
                    var dialog = new SaveFileDialog
                    {
                        Title = "점검 항목 내보내기",
                        Filter = "JSON (*.json)|*.json",
                        DefaultExt = ".json",
                        AddExtension = true,
                        OverwritePrompt = true,
                        FileName = "점검 항목.json"
                    };
                    if (dialog.ShowDialog(_window) != true)
                        return;
                    File.WriteAllText(dialog.FileName, CheckLogic.ExportItems(_items), new UTF8Encoding(false));
                    SetStatus(_items.Count.ToString(CultureInfo.InvariantCulture) + "개를 내보냈습니다: " + dialog.FileName, false);
                }
                catch (Exception ex)
                {
                    SetStatus("내보내지 못했습니다: " + ex.Message, true);
                }
            }

            private void Import()
            {
                try
                {
                    var dialog = new OpenFileDialog { Title = "점검 항목 가져오기", Filter = "JSON (*.json)|*.json", CheckFileExists = true };
                    if (dialog.ShowDialog(_window) != true)
                        return;
                    ImportFile(dialog.FileName);
                }
                catch (Exception ex)
                {
                    SetStatus("가져오지 못했습니다: " + ex.Message, true);
                }
            }

            private void ImportFile(string path)
            {
                List<UserCheckItem> incoming;
                try
                {
                    incoming = CheckLogic.ImportItems(File.ReadAllText(path));
                }
                catch (FormatException ex)
                {
                    Dialogs.Show(_window, Title, "가져올 수 없는 파일입니다: " + ex.Message, true);
                    return;
                }
                var known = Kinds().Select(p => p.Kind).ToList();
                var unsupported = incoming.Where(i => !known.Contains((i.Kind ?? "").ToLowerInvariant())).ToList();
                var existing = incoming.Where(i => _items.Any(x => x.Id == i.Id)).ToList();
                if (existing.Count > 0 && !Dialogs.Confirm(_window, Title,
                    "같은 id의 항목 " + existing.Count.ToString(CultureInfo.InvariantCulture) + "개를 덮어씁니다: " + string.Join(", ", existing.Select(i => i.Title).Take(5))
                    + (existing.Count > 5 ? " …" : "") + Environment.NewLine + "계속할까요?", "덮어쓰기", false))
                    return;
                var now = DateTime.UtcNow;
                foreach (var item in incoming)
                    _host.Store.SaveUserItem(item, now);
                SetStatus(incoming.Count.ToString(CultureInfo.InvariantCulture) + "개를 가져왔습니다" + (existing.Count > 0 ? "(" + existing.Count.ToString(CultureInfo.InvariantCulture) + "개 덮어씀)" : "")
                    + (unsupported.Count > 0 ? " — 점검을 지원하지 않는 DBMS 항목 " + unsupported.Count.ToString(CultureInfo.InvariantCulture) + "개는 돌지 않습니다" : "") + ".", false);
                Reload();
            }
        }

        // ================= 사용자 항목 편집 =================

        private sealed class RuleChoice
        {
            public RuleChoice(string title, CheckRuleKind kind, CheckLevel level)
            {
                Text = title;
                Kind = kind;
                Level = level;
            }

            public string Text { get; }
            public CheckRuleKind Kind { get; }
            public CheckLevel Level { get; }

            public override string ToString()
            {
                return Text;
            }
        }

        private static readonly RuleChoice[] RuleChoices =
        {
            new RuleChoice("행이 있으면 주의", CheckRuleKind.AnyRow, CheckLevel.Warning),
            new RuleChoice("행이 있으면 위험", CheckRuleKind.AnyRow, CheckLevel.Critical),
            new RuleChoice("수 열의 최댓값으로 (임계값)", CheckRuleKind.Threshold, CheckLevel.Ok),
            new RuleChoice("판정 열의 OK·WARN·CRIT", CheckRuleKind.ByLevel, CheckLevel.Ok),
            new RuleChoice("정보 (판정하지 않음)", CheckRuleKind.Info, CheckLevel.Ok)
        };

        private static readonly string[] MissingChoices = { "정상", "주의", "위험" };

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

        /// <summary>편집 칸으로 규칙을 만든다. 만들 수 없으면 null과 사람이 읽을 이유(숫자가 아닌 문턱은 여기서 보지 않는다 — 비운 것으로 본다).</summary>
        private static CheckRule BuildRule(RuleChoice choice, string column, string warnText, string critText, CheckLevel missing, out string problem)
        {
            problem = null;
            if ((choice.Kind == CheckRuleKind.Threshold || choice.Kind == CheckRuleKind.ByLevel) && string.IsNullOrEmpty(column))
            {
                problem = "판정 열을 고르세요. [시험 실행]을 하면 결과 열이 목록에 들어옵니다.";
                return null;
            }
            try
            {
                switch (choice.Kind)
                {
                    case CheckRuleKind.AnyRow:
                        return CheckRule.AnyRow(choice.Level);
                    case CheckRuleKind.Threshold:
                        double w, c;
                        return CheckRule.Threshold(column,
                            double.TryParse((warnText ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out w) ? w : (double?)null,
                            double.TryParse((critText ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out c) ? c : (double?)null,
                            missing);
                    case CheckRuleKind.ByLevel:
                        return CheckRule.ByLevel(column);
                    default:
                        return CheckRule.Info();
                }
            }
            catch (ArgumentException ex)
            {
                problem = ex.Message;
                return null;
            }
        }

        /// <summary>사용자 항목 추가·고치기. 저장하면 그 항목, 취소하면 null.</summary>
        private static UserCheckItem EditItem(Window owner, Host host, UserCheckItem source)
        {
            var item = source == null
                ? new UserCheckItem { Id = CheckLogic.NewUserId(), Kind = Kinds()[0].Kind, Category = "사용자", Rule = CheckLogic.RuleToJson(CheckRule.AnyRow(CheckLevel.Warning)) }
                : new UserCheckItem { Id = source.Id, Kind = source.Kind, Category = source.Category, Title = source.Title, Sql = source.Sql, Rule = source.Rule, Advice = source.Advice };
            CheckRule rule;
            try
            {
                rule = CheckLogic.RuleFromJson(item.Rule);
            }
            catch (FormatException)
            {
                rule = CheckRule.AnyRow(CheckLevel.Warning);
            }

            var window = new Window
            {
                Title = source == null ? "점검 항목 추가" : "점검 항목 고치기",
                Width = 760,
                Height = 700,
                MinWidth = 560,
                MinHeight = 520,
                ShowInTaskbar = false,
                ResizeMode = ResizeMode.CanResizeWithGrip
            };
            Theme.ApplyWindow(window);
            DialogKit.SetOwner(window, owner);
            var root = new DockPanel { Margin = new Thickness(16) };
            window.Content = root;

            var form = new Grid();
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var row = 0;
            Func<string, FrameworkElement, FrameworkElement> add = (label, control) =>
            {
                form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var text = Theme.Text(label, Theme.SecondaryText);
                text.VerticalAlignment = VerticalAlignment.Center;
                text.Margin = new Thickness(0, 4, 8, 4);
                Grid.SetRow(text, row);
                form.Children.Add(text);
                control.Margin = new Thickness(0, 4, 0, 4);
                Grid.SetRow(control, row);
                Grid.SetColumn(control, 1);
                form.Children.Add(control);
                row++;
                return control;
            };

            var title = new TextBox { Text = item.Title ?? "" };
            AutomationProperties.SetName(title, "제목");
            AutomationProperties.SetAutomationId(title, "check-edit-title");
            add("제목", title);

            var kinds = Kinds();
            var kind = new ComboBox { MinWidth = 160, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var provider in kinds)
                kind.Items.Add(new ComboBoxItem { Content = provider.DisplayName, Tag = provider });
            kind.SelectedIndex = Math.Max(0, kinds.FindIndex(p => string.Equals(p.Kind, item.Kind, StringComparison.OrdinalIgnoreCase)));
            AutomationProperties.SetName(kind, "DBMS");
            add("DBMS", kind);

            // 고르기만 하는 콤보(Folderss 콤보 스타일은 편집 칸이 없다). 가져온 항목의 낯선 분류도 목록에 넣는다
            var category = new ComboBox { MinWidth = 160, HorizontalAlignment = HorizontalAlignment.Left };
            var categories = MonitorCheckSet.Categories.Concat(new[] { "사용자" }).ToList();
            if (!string.IsNullOrWhiteSpace(item.Category) && !categories.Contains(item.Category))
                categories.Add(item.Category);
            foreach (var name in categories)
                category.Items.Add(name);
            category.SelectedItem = string.IsNullOrWhiteSpace(item.Category) ? "사용자" : item.Category;
            AutomationProperties.SetName(category, "분류");
            add("분류", category);

            var sql = new TextBox
            {
                Text = item.Sql ?? "",
                AcceptsReturn = true,
                AcceptsTab = false,
                FontFamily = Theme.Mono,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Height = 150,
                VerticalContentAlignment = VerticalAlignment.Top
            };
            AutomationProperties.SetName(sql, "점검 SQL");
            AutomationProperties.SetAutomationId(sql, "check-edit-sql");
            add("SQL", sql);

            var ruleRow = new WrapPanel();
            var ruleBox = new ComboBox { MinWidth = 220, Margin = new Thickness(0, 0, 8, 0) };
            foreach (var choice in RuleChoices)
                ruleBox.Items.Add(choice);
            ruleBox.SelectedItem = RuleChoices.FirstOrDefault(c => c.Kind == rule.Kind && (c.Kind != CheckRuleKind.AnyRow || c.Level == rule.Level)) ?? RuleChoices[0];
            AutomationProperties.SetName(ruleBox, "판정");
            AutomationProperties.SetAutomationId(ruleBox, "check-edit-rule");
            ruleRow.Children.Add(ruleBox);
            var columnLabel = Theme.Text("열 ", Theme.SecondaryText);
            columnLabel.VerticalAlignment = VerticalAlignment.Center;
            ruleRow.Children.Add(columnLabel);
            // 판정 열은 손으로 치지 않고 [시험 실행]이 받은 결과 열에서 고른다(고치는 항목은 지금 열을 미리 넣어 둔다)
            var column = new ComboBox { MinWidth = 140, Margin = new Thickness(0, 0, 8, 0) };
            if (!string.IsNullOrEmpty(rule.Column))
            {
                column.Items.Add(rule.Column);
                column.SelectedIndex = 0;
            }
            column.ToolTip = "[시험 실행]을 하면 결과 열에서 고를 수 있습니다";
            Func<string> columnText = () => column.SelectedItem as string ?? "";
            AutomationProperties.SetName(column, "판정 열");
            AutomationProperties.SetAutomationId(column, "check-edit-column");
            ruleRow.Children.Add(column);
            add("판정", ruleRow);

            var limits = new WrapPanel();
            Func<string, TextBlock> small = text =>
            {
                var block = Theme.Text(text, Theme.SecondaryText);
                block.VerticalAlignment = VerticalAlignment.Center;
                return block;
            };
            limits.Children.Add(small("주의 ≥ "));
            var warn = new TextBox { Width = 70, Text = rule.Warning.HasValue ? CheckLogic.NumberText(rule.Warning.Value) : "" };
            AutomationProperties.SetName(warn, "주의 문턱");
            AutomationProperties.SetAutomationId(warn, "check-edit-warn");
            limits.Children.Add(warn);
            limits.Children.Add(small("   위험 ≥ "));
            var crit = new TextBox { Width = 70, Text = rule.Critical.HasValue ? CheckLogic.NumberText(rule.Critical.Value) : "" };
            AutomationProperties.SetName(crit, "위험 문턱");
            AutomationProperties.SetAutomationId(crit, "check-edit-crit");
            limits.Children.Add(crit);
            limits.Children.Add(small("   값이 없으면 "));
            var missing = new ComboBox { MinWidth = 70 };
            foreach (var text in MissingChoices)
                missing.Items.Add(text);
            missing.SelectedIndex = rule.MissingLevel == CheckLevel.Critical ? 2 : rule.MissingLevel == CheckLevel.Warning ? 1 : 0;
            AutomationProperties.SetName(missing, "값이 없으면");
            limits.Children.Add(missing);
            var limitsRow = add("문턱", limits);

            var advice = new TextBox { Text = item.Advice ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 52, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            AutomationProperties.SetName(advice, "조치 안내");
            add("조치 안내", advice);

            Action syncRule = () =>
            {
                var choice = ruleBox.SelectedItem as RuleChoice ?? RuleChoices[0];
                var needsColumn = choice.Kind == CheckRuleKind.Threshold || choice.Kind == CheckRuleKind.ByLevel;
                columnLabel.Visibility = column.Visibility = needsColumn ? Visibility.Visible : Visibility.Collapsed;
                limitsRow.Visibility = choice.Kind == CheckRuleKind.Threshold ? Visibility.Visible : Visibility.Collapsed;
                ((UIElement)form.Children[form.Children.IndexOf(limitsRow) - 1]).Visibility = limitsRow.Visibility;
            };
            ruleBox.SelectionChanged += (s, e) => syncRule();
            syncRule();

            // 시험 실행
            var testRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var testDb = new ComboBox { MinWidth = 200, Margin = new Thickness(0, 0, 6, 0) };
            AutomationProperties.SetName(testDb, "시험할 DB");
            AutomationProperties.SetAutomationId(testDb, "check-edit-testdb");
            var test = DialogKit.PlainButton("시험 실행");
            AutomationProperties.SetAutomationId(test, "check-edit-test");
            var testStatus = Theme.Text("", Theme.SecondaryText);
            testStatus.TextWrapping = TextWrapping.Wrap;
            testStatus.VerticalAlignment = VerticalAlignment.Center;
            testStatus.Margin = new Thickness(8, 0, 0, 0);
            AutomationProperties.SetAutomationId(testStatus, "check-edit-test-result");
            DockPanel.SetDock(testDb, Dock.Left);
            DockPanel.SetDock(test, Dock.Left);
            testRow.Children.Add(testDb);
            testRow.Children.Add(test);
            testRow.Children.Add(testStatus);
            Action fillDbs = () =>
            {
                var provider = (kind.SelectedItem as ComboBoxItem)?.Tag as IDbProvider;
                var previous = (testDb.SelectedItem as ComboBoxItem)?.Tag as OracleConnectionProfile;
                testDb.Items.Clear();
                IReadOnlyList<OracleConnectionProfile> profiles;
                try
                {
                    profiles = host.Profiles == null ? Array.Empty<OracleConnectionProfile>() : host.Profiles() ?? Array.Empty<OracleConnectionProfile>();
                }
                catch (Exception)
                {
                    profiles = Array.Empty<OracleConnectionProfile>();
                }
                foreach (var profile in profiles.Where(p => p != null && provider != null && string.Equals(DbProviders.For(p).Kind, provider.Kind, StringComparison.OrdinalIgnoreCase)))
                {
                    var option = new ComboBoxItem { Content = profile.Name, Tag = profile };
                    testDb.Items.Add(option);
                    if (previous != null && previous.Id == profile.Id)
                        testDb.SelectedItem = option;
                }
                if (testDb.SelectedIndex < 0 && testDb.Items.Count > 0)
                    testDb.SelectedIndex = 0;
                test.IsEnabled = testDb.Items.Count > 0;
            };
            kind.SelectionChanged += (s, e) => fillDbs();
            fillDbs();

            var errors = DialogKit.Warning("", 6);
            errors.Visibility = Visibility.Collapsed;
            AutomationProperties.SetAutomationId(errors, "check-edit-errors");

            var gridHost = new ResultGridView(all => { }, text => DialogKit.TryCopy(text));
            gridHost.View.MinHeight = 80;
            AutomationProperties.SetName(gridHost.View, "시험 결과 행");

            string ruleProblem = null;
            Func<UserCheckItem> current = () =>
            {
                var built = BuildRule(ruleBox.SelectedItem as RuleChoice ?? RuleChoices[0], columnText(), warn.Text, crit.Text,
                    missing.SelectedIndex == 2 ? CheckLevel.Critical : missing.SelectedIndex == 1 ? CheckLevel.Warning : CheckLevel.Ok, out ruleProblem);
                return new UserCheckItem
                {
                    Id = item.Id,
                    Kind = ((kind.SelectedItem as ComboBoxItem)?.Tag as IDbProvider ?? kinds[0]).Kind,
                    Category = category.SelectedItem as string ?? "사용자",
                    Title = title.Text.Trim(),
                    Sql = sql.Text.Trim(),
                    Rule = built == null ? null : CheckLogic.RuleToJson(built),
                    Advice = advice.Text.Trim()
                };
            };

            // 규칙·숫자 칸의 글자 검사(RuleToJson은 형식만 맞추므로 숫자가 아닌 문턱은 여기서 잡는다)
            Func<UserCheckItem, List<string>> validate = candidate =>
            {
                var problems = CheckLogic.Validate(candidate);
                if (ruleProblem != null)
                {
                    problems.RemoveAll(p => p == CheckLogic.EmptyRuleText);
                    problems.Add(ruleProblem);
                }
                var choice = ruleBox.SelectedItem as RuleChoice ?? RuleChoices[0];
                if (choice.Kind == CheckRuleKind.Threshold)
                {
                    double unused;
                    foreach (var pair in new[] { Tuple.Create("주의", warn.Text.Trim()), Tuple.Create("위험", crit.Text.Trim()) })
                    {
                        if (pair.Item2.Length > 0 && !double.TryParse(pair.Item2, NumberStyles.Float, CultureInfo.InvariantCulture, out unused))
                            problems.Add(pair.Item1 + " 문턱은 숫자여야 합니다: " + pair.Item2);
                    }
                }
                return problems;
            };

            Action<List<string>> showErrors = problems =>
            {
                errors.Text = string.Join(Environment.NewLine, problems.Select(p => "· " + p));
                errors.Visibility = problems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            };

            CancellationTokenSource running = null;
            test.Click += async (s, e) =>
            {
                try
                {
                    var candidate = current();
                    var problems = validate(candidate);
                    showErrors(problems);
                    if (problems.Count > 0 || running != null)
                        return;
                    var profile = (testDb.SelectedItem as ComboBoxItem)?.Tag as OracleConnectionProfile;
                    if (profile == null)
                        return;
                    running = new CancellationTokenSource();
                    test.IsEnabled = false;
                    testStatus.SetResourceReference(TextBlock.ForegroundProperty, Theme.SecondaryText);
                    testStatus.Text = "연결 중…";
                    DbSession session = null;
                    try
                    {
                        session = await host.Connect(profile.Id);
                        if (session == null)
                        {
                            testStatus.Text = "비밀번호를 입력하지 않아 시험하지 않았습니다.";
                            return;
                        }
                        testStatus.Text = "실행 중…";
                        var check = CheckLogic.ToCheck(candidate);
                        var outcomes = await CheckRunner.RunAsync(session, new[] { check }, null, running.Token);
                        var outcome = outcomes[0];
                        testStatus.Text = CheckLogic.StatusText(outcome.Status) + " — " + (outcome.Message ?? outcome.ValueText ?? "")
                            + (outcome.RowCount > 0 ? " · 결과 " + CheckLogic.CountText(outcome.RowCount) : "")
                            + " · " + outcome.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "초";
                        if (outcome.Status == CheckStatus.Critical || outcome.Status == CheckStatus.Error)
                            testStatus.Foreground = Theme.Danger;
                        else if (outcome.Status == CheckStatus.Warning || outcome.Status == CheckStatus.Skipped)
                            testStatus.Foreground = Theme.Warning;
                        else
                            testStatus.Foreground = Theme.Success;
                        // 결과 열을 판정 열 후보로
                        var keep = columnText();
                        column.Items.Clear();
                        foreach (var name in outcome.Columns)
                            column.Items.Add(name);
                        column.SelectedItem = outcome.Columns.FirstOrDefault(c => string.Equals(c, keep, StringComparison.OrdinalIgnoreCase));
                        var table = new QueryTable();
                        table.Columns.AddRange(outcome.Columns);
                        table.Rows.AddRange(outcome.Rows);
                        var rows = new ObservableCollection<ResultGridRow>();
                        for (var i = 0; i < outcome.Rows.Count; i++)
                            rows.Add(new ResultGridRow(i + 1, outcome.Rows[i]));
                        gridHost.Show(MonitorLogic.Columns(table), rows);
                    }
                    catch (Exception ex)
                    {
                        testStatus.Foreground = Theme.Danger;
                        testStatus.Text = "시험하지 못했습니다: " + DbSession.DescribeError(ex);
                    }
                    finally
                    {
                        running.Dispose();
                        running = null;
                        test.IsEnabled = testDb.Items.Count > 0;
                        DisposeQuietly(session);
                    }
                }
                catch (Exception)
                {
                    // async void
                }
            };
            window.Closed += (s, e) =>
            {
                if (running != null)
                    running.Cancel();
            };

            UserCheckItem result = null;
            var save = DialogKit.PrimaryButton("저장");
            AutomationProperties.SetAutomationId(save, "check-edit-save");
            var cancel = DialogKit.CancelButton("취소", false);
            save.Click += (s, e) =>
            {
                var candidate = current();
                var problems = validate(candidate);
                showErrors(problems);
                if (problems.Count > 0)
                    return;
                try
                {
                    host.Store.SaveUserItem(candidate, DateTime.UtcNow);
                    result = candidate;
                    window.DialogResult = true;
                }
                catch (Exception ex)
                {
                    showErrors(new List<string> { "저장하지 못했습니다: " + ex.Message });
                }
            };

            var bottom = new StackPanel();
            bottom.Children.Add(errors);
            bottom.Children.Add(DialogKit.Buttons(save, cancel));
            DockPanel.SetDock(bottom, Dock.Bottom);
            root.Children.Add(bottom);
            var hint = DialogKit.Hint("SELECT(WITH) 한 문장만 됩니다. 결과 행이 '발견 사항'이 되고, 임계값은 고른 열의 최댓값으로, 판정 열은 행마다 OK·WARN·CRIT로 판정합니다. 바인드 변수는 쓸 수 없습니다.", 0);
            hint.Margin = new Thickness(0, 0, 0, 6);
            DockPanel.SetDock(hint, Dock.Top);
            root.Children.Add(hint);
            DockPanel.SetDock(form, Dock.Top);
            root.Children.Add(form);
            DockPanel.SetDock(testRow, Dock.Top);
            root.Children.Add(testRow);
            var gridFrame = new Border { BorderThickness = new Thickness(1), Margin = new Thickness(0, 6, 0, 0), Child = gridHost.View };
            gridFrame.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
            root.Children.Add(gridFrame);
            DialogKit.FocusOnLoad(window, title);
            window.ShowDialog();
            return result;
        }
    }
}

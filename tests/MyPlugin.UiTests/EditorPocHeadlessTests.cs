using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyPlugin.UiTests.Headless;
using Xunit;
using Xunit.Abstractions;

namespace MyPlugin.UiTests
{
    /// <summary>
    /// SQL 편집기 AvalonEdit 전환 1단계 PoC 실측(docs/sql-editor-avalonedit-plan.md 4절)을 헤드리스로: 도구 > AvalonEdit PoC 창에서
    /// A 플러그인 로드 · B 성능(지금 SQL 탭과 같은 설정의 TextBox와 같은 데이터로 비교) · D 키 처리(TextBox와 비교) · E 테마 7종을 잰다.
    /// C(한글 IME)는 사람이 확인한다. 막는 문제(A 로드·그리기·색칠 실패)만 실패로 보고, 수치는 계획서 7절 표 줄로 shots/poc-measure.md에 남긴다.
    /// 숨은 데스크톱은 소프트웨어 렌더링이라 시간은 GPU로 그리는 실제 Folderss보다 느린 쪽(상한)이다.
    /// </summary>
    public class EditorPocHeadlessTests
    {
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(120);
        private static readonly string[] AllThemes = { "Black", "Catppuccin", "Dracula", "GitHub", "Light", "Nord", "Solarized" };
        private const string EditorType = "ICSharpCode.AvalonEdit.TextEditor";
        private readonly ITestOutputHelper _out;

        public EditorPocHeadlessTests(ITestOutputHelper output)
        {
            _out = output;
        }

        // ================= A. 플러그인 로드 =================

        [UiFact]
        public async Task A_LoadsInPluginContext_DrawsAndColors()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var (main, poc, editor) = await OpenPoc(ui, plugin);
            try
            {
                var assembly = ui.Run(() => editor.GetType().Assembly);
                Assert.Same(plugin.Context, AssemblyLoadContext.GetLoadContext(assembly));
                Assert.StartsWith(plugin.PluginDirectory, assembly.Location, StringComparison.OrdinalIgnoreCase);
                // 기본 스타일(AvalonEdit Themes/generic.xaml, pack URI)을 못 찾으면 템플릿이 없어 아무것도 그려지지 않는다
                Assert.NotNull(ui.Run(() => ((Control)editor).Template));

                ui.Run(() => Ui.Click(Ui.Button(poc, "D1 경계 사례")));
                await ui.WaitUntil(() => MeasureText(poc).StartsWith("D1:", StringComparison.Ordinal), Wait, "D1 넣기 측정");
                await Idle(ui);
                var lines = ui.Run(() => { dynamic ed = editor; return (int)ed.TextArea.TextView.VisualLines.Count; });
                Assert.True(lines > 10, "그려진 줄이 " + lines + "개뿐입니다.");
                // 첫 줄(-- 줄 주석 한글)은 전부 주석색, 둘째 줄 앞(SELECT)은 주석색이 아니다
                var colors = ui.Run(() =>
                {
                    dynamic ed = editor;
                    var comment = (Brush)Field(Colorizer(editor), "Comment");
                    var first = new List<object>();
                    foreach (var element in ed.TextArea.TextView.VisualLines[0].Elements)
                        first.Add(element.TextRunProperties.ForegroundBrush);
                    object second = ed.TextArea.TextView.VisualLines[1].Elements[0].TextRunProperties.ForegroundBrush;
                    return (first.All(b => ReferenceEquals(b, comment)), ReferenceEquals(second, comment), first.Count);
                });
                Assert.True(colors.Item1, "첫 줄 주석이 주석색으로 칠해지지 않았습니다(요소 " + colors.Item3 + "개).");
                Assert.False(colors.Item2, "둘째 줄 SELECT가 주석색입니다.");
                var load = ui.Run(() => MeasureText(poc).Split('\n')[0]);
                _out.WriteLine("캡처: " + ui.Run(() => Ui.Snap(poc, plugin.ShotFolder, "poc-d1")));

                // PoC 창의 자체 측정(버튼 → 창 아래 글)도 도는지
                ui.Run(() => Ui.Click(Ui.Button(poc, "D3-1 1만 줄")));
                await ui.WaitUntil(() => MeasureText(poc).StartsWith("D3-1", StringComparison.Ordinal), Wait, "D3-1 넣기 측정");
                var d3 = ui.Run(() => MeasureText(poc).Split('\n')[0]);

                Report(plugin, "A 플러그인 로드",
                    "PluginLoadContext에서 " + assembly.GetName().Name + " " + assembly.GetName().Version + " 로드(" + Path.GetFileName(Path.GetDirectoryName(assembly.Location)) + "\\…), "
                    + "기본 템플릿 적용, 그려진 줄 " + lines + "개, 주석 색칠 확인, UI 예외 0", "✓");
                Report(plugin, "A PoC 창 자체 측정", load + " / " + d3, "");
            }
            finally
            {
                ui.Run(() => { poc.Close(); main.Close(); });
            }
            Assert.Empty(ui.TakeErrors());
        }

        // ================= B. 성능 =================

        [UiFact]
        public async Task B_Performance_AgainstTextBox()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var (main, poc, editor) = await OpenPoc(ui, plugin);
            // 셋을 같은 크기 창에서 같은 데이터로: PoC 편집기(글이 바뀔 때마다 전체 다시 칠함), 강조 없는 AvalonEdit(편집기 자체 비용), 지금 SQL 탭 TextBox
            var bareWindow = ui.Run(() => BareEditorWindow(editor));
            var bare = ui.Run(() => (FrameworkElement)bareWindow.Content);
            var baseline = ui.Run(() => BaselineWindow(editor));
            var box = ui.Run(() => (TextBox)baseline.Content);
            var targets = new[] { AvalonTarget("AvalonEdit+강조", editor), AvalonTarget("AvalonEdit(강조 없음)", bare), TextBoxTarget(box) };
            try
            {
                foreach (var size in new[] { ("D3-1", 10000), ("D3-2", 50000), ("D3-3", 100000) })
                {
                    var text = PocD3(plugin, size.Item2);
                    var mb = (text.Length * 2 / 1024.0 / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + "MB";
                    var load = targets.ToDictionary(t => t, t => new List<double>());
                    for (var i = 0; i < 3; i++)
                    {
                        foreach (var t in targets)
                        {
                            ui.Run(() => t.SetText(""));
                            await Idle(ui);
                            load[t].Add(await TimeUntilIdle(ui, () => t.SetText(text)));
                        }
                    }
                    Report(plugin, "B 넣기→첫 화면 " + size.Item1 + " (" + size.Item2.ToString("N0", CultureInfo.InvariantCulture) + "줄 · " + mb + ", 3회 중앙값)",
                        string.Join(" · ", targets.Select(t => t.Name + " " + Ms(Median(load[t])))),
                        Judge(Median(load[targets[0]]) <= Median(load[targets[2]]) * 1.2 || Median(load[targets[0]]) < 200));

                    // 키 입력: 가운데·끝에서 한 글자씩 10번, 입력부터 (다시 칠하고) 그려서 한가해질 때까지
                    foreach (var atEnd in new[] { false, true })
                    {
                        var typing = targets.ToDictionary(t => t, t => new List<double>());
                        foreach (var t in targets)
                        {
                            ui.Run(() => t.CaretAt(atEnd));
                            await Idle(ui);
                            var before = ui.Run(t.Length);
                            for (var i = 0; i < 10; i++)
                                typing[t].Add(await TimeUntilIdle(ui, () => TypeText(t.Input(), "x")));
                            Assert.Equal(10, ui.Run(t.Length) - before);
                        }
                        Report(plugin, "B 키 입력 지연 " + size.Item1 + " " + (atEnd ? "끝" : "가운데") + " (10회 중앙값 · 최대)",
                            string.Join(" / ", targets.Select(t => t.Name + " " + Ms(Median(typing[t])) + " · " + Ms(typing[t].Max()))),
                            Judge(Median(typing[targets[0]]) <= 16));
                    }

                    // 강조 구간 계산 단독(순수 함수, 5회 중앙값) — Linux 참고값과 같은 잣대
                    var spans = new List<double>();
                    var highlight = HighlightSpans(plugin);
                    for (var i = 0; i < 5; i++)
                    {
                        var watch = Stopwatch.StartNew();
                        highlight(text);
                        spans.Add(watch.Elapsed.TotalMilliseconds);
                    }
                    Report(plugin, "B 강조 구간 계산 단독 " + size.Item1 + " (5회 중앙값)", Ms(Median(spans)), Judge(Median(spans) <= 16));
                }

                // D4-2: 10만 줄 맨 앞에 /* 넣기 → 문서 전체가 주석, 빼기 → 원래대로 (각 3회)
                var big = PocD3(plugin, 100000);
                foreach (var t in targets)
                    ui.Run(() => t.SetText(big));
                await Idle(ui);
                var insert = new List<double>();
                var remove = new List<double>();
                for (var i = 0; i < 3; i++)
                {
                    insert.Add(await TimeUntilIdle(ui, () => { dynamic ed = editor; ed.Document.Insert(0, "/*"); }));
                    remove.Add(await TimeUntilIdle(ui, () => { dynamic ed = editor; ed.Document.Remove(0, 2); }));
                }
                Report(plugin, "B 전체 다시 칠하기 D4-2 (D3-3 맨 앞 /* 넣기·빼기, 3회 중앙값)", "넣기 " + Ms(Median(insert)) + " · 빼기 " + Ms(Median(remove)),
                    Judge(Math.Max(Median(insert), Median(remove)) <= 1000));

                // 스크롤: 10만 줄 끝으로 한 번에(3회), 가운데부터 한 화면씩 30번
                var scrolls = new List<string>();
                var pocPage = 0.0;
                foreach (var t in targets)
                {
                    var end = new List<double>();
                    for (var i = 0; i < 3; i++)
                    {
                        ui.Run(t.ScrollToHome);
                        await Idle(ui);
                        end.Add(await TimeUntilIdle(ui, t.ScrollToEnd));
                    }
                    var page = await PageScroll(ui, t.Scroll, t.ScrollTo);
                    if (t == targets[0])
                        pocPage = Median(page);
                    scrolls.Add(t.Name + " 끝으로 " + Ms(Median(end)) + " · 한 화면 " + Ms(Median(page)) + " (최대 " + Ms(page.Max()) + ")");
                }
                Report(plugin, "B 스크롤 D3-3 (끝으로 3회 중앙값, 한 화면씩 30회 중앙값)", string.Join(" / ", scrolls), Judge(pocPage <= 16));

                // D5: 탭 20개(1천 줄씩, 한 번씩 보이기) 메모리
                foreach (var t in targets)
                    ui.Run(() => t.SetText(""));
                var tab = PocD3(plugin, 1000);
                var editorType = editor.GetType();
                Func<FrameworkElement> newAvalon = () =>
                {
                    var ed = NewBareEditor(editorType, ((Control)editor).FontFamily);
                    ((dynamic)ed).Document.Text = tab;
                    return ed;
                };
                Func<FrameworkElement> newTextBox = () =>
                {
                    var t = NewBaselineTextBox(((Control)editor).FontFamily);
                    t.Text = tab;
                    return t;
                };
                // 처음 한 번은 글꼴·서식 캐시가 잡히고 앞 단계의 큰 글이 늦게 풀려 잡음이 크다 — 한 번씩 버리고 잰다
                await MemoryPerTab(ui, newTextBox);
                await MemoryPerTab(ui, newAvalon);
                var tbMemory = await MemoryPerTab(ui, newTextBox);
                var aeMemory = await MemoryPerTab(ui, newAvalon);
                Report(plugin, "B 탭 20개 메모리 (1천 줄씩, 한 번씩 보이기, 관리 힙 ÷ 20: 열 때 늘어난 양 / 탭을 다 닫은 뒤 줄어든 양)",
                    "AvalonEdit 탭당 " + Mb(aeMemory.opened) + " / " + Mb(aeMemory.released) + " · TextBox 탭당 " + Mb(tbMemory.opened) + " / " + Mb(tbMemory.released),
                    Judge(Math.Max(aeMemory.opened, aeMemory.released) < 5 * 1024 * 1024));
            }
            finally
            {
                ui.Run(() => { baseline.Close(); bareWindow.Close(); poc.Close(); main.Close(); });
            }
            Assert.Empty(ui.TakeErrors());
        }

        /// <summary>성능 비교 대상 하나(동작은 UI 스레드에서 부른다).</summary>
        private sealed class Target
        {
            public string Name;
            public Action<string> SetText;      // 글을 넣고 맨 위로
            public Func<int> Length;
            public Action<bool> CaretAt;        // true면 끝, false면 가운데 줄 앞 — 그 줄이 보이게
            public Func<UIElement> Input;       // 글자 입력을 받는 요소
            public Action ScrollToHome;
            public Action ScrollToEnd;
            public Func<(double offset, double viewport, double extent)> Scroll;
            public Action<double> ScrollTo;
        }

        private static Target AvalonTarget(string name, FrameworkElement editor)
        {
            dynamic ed = editor;
            return new Target
            {
                Name = name,
                SetText = text => { ed.Document.Text = text; ed.ScrollToHome(); },
                Length = () => (int)ed.Document.TextLength,
                CaretAt = atEnd =>
                {
                    int offset = atEnd ? ed.Document.TextLength : ed.Document.GetLineByNumber(Math.Max(1, (int)ed.Document.LineCount / 2)).Offset;
                    ed.TextArea.Caret.Offset = offset;
                    ed.ScrollToLine(ed.Document.GetLineByOffset(offset).LineNumber);
                },
                Input = () => (UIElement)ed.TextArea,
                ScrollToHome = () => ed.ScrollToHome(),
                ScrollToEnd = () => ed.ScrollToEnd(),
                Scroll = () => ((double)ed.VerticalOffset, (double)ed.ViewportHeight, (double)ed.ExtentHeight),
                ScrollTo = y => ed.ScrollToVerticalOffset(y)
            };
        }

        private static Target TextBoxTarget(TextBox box)
        {
            return new Target
            {
                Name = "TextBox",
                SetText = text => { box.Text = text; box.ScrollToHome(); },
                Length = () => box.Text.Length,
                CaretAt = atEnd =>
                {
                    box.CaretIndex = atEnd ? box.Text.Length : box.GetCharacterIndexFromLineIndex(Math.Max(0, box.LineCount / 2));
                    box.ScrollToLine(box.GetLineIndexFromCharacterIndex(box.CaretIndex));
                },
                Input = () => box,
                ScrollToHome = box.ScrollToHome,
                ScrollToEnd = box.ScrollToEnd,
                Scroll = () => (box.VerticalOffset, box.ViewportHeight, box.ExtentHeight),
                ScrollTo = box.ScrollToVerticalOffset
            };
        }

        // ================= D. 키 =================

        [UiFact]
        public async Task D_Keys_AgainstTextBox()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var (main, poc, editor) = await OpenPoc(ui, plugin);
            var baseline = ui.Run(() => BaselineWindow(editor));
            var box = ui.Run(() => (TextBox)baseline.Content);
            try
            {
                await Idle(ui);
                // 수정 키 흉내가 되는지: 이 UI 스레드의 키 상태(SetKeyboardState — 다른 창·사용자 입력과 무관)를 바꿔 Keyboard.Modifiers가 따라오는지
                var modifiers = ui.Run(() =>
                {
                    var seen = ModifierKeys.None;
                    WithModifiers(ModifierKeys.Control | ModifierKeys.Shift, () => seen = Keyboard.Modifiers);
                    return seen;
                });
                Assert.Equal(ModifierKeys.Control | ModifierKeys.Shift, modifiers);

                // SQL 탭 편집기 키(F4·F3·Ctrl+F …)는 SqlWorkspace가 편집기의 PreviewKeyDown에서 먼저 잡는다. AvalonEdit을 품은 컨트롤에서도
                // 그 처리기에 먼저 오는지(편집기가 터널 단계에서 먹지 않는지) 본다
                KeyCase active = null;
                var reachedBox = new List<string>();
                var reachedEditor = new List<string>();
                ui.Run(() =>
                {
                    box.PreviewKeyDown += (s, e) => { if (!e.Handled && active != null && active.SqlTab && e.Key == active.Key) reachedBox.Add(active.Name); };
                    ((UIElement)editor).PreviewKeyDown += (s, e) => { if (!e.Handled && active != null && active.SqlTab && e.Key == active.Key) reachedEditor.Add(active.Name); };
                });

                const string start = "SELECT a,\r\n  b FROM t;\r\n-- c\r\n";
                var cases = new List<KeyCase>
                {
                    // SQL 탭 편집기 키
                    new KeyCase("F4", Key.F4, ModifierKeys.None, 9, 0, sqlTab: true),
                    new KeyCase("F3", Key.F3, ModifierKeys.None, 9, 0, sqlTab: true),
                    new KeyCase("Shift+F3", Key.F3, ModifierKeys.Shift, 9, 0, sqlTab: true),
                    new KeyCase("Ctrl+F", Key.F, ModifierKeys.Control, 9, 0, sqlTab: true),
                    new KeyCase("Ctrl+Enter", Key.Enter, ModifierKeys.Control, 9, 0, sqlTab: true),
                    new KeyCase("Ctrl+/", Key.Oem2, ModifierKeys.Control, 9, 0, sqlTab: true),
                    new KeyCase("Ctrl+Shift+U", Key.U, ModifierKeys.Control | ModifierKeys.Shift, 0, 6, sqlTab: true),
                    new KeyCase("Ctrl+Shift+L", Key.L, ModifierKeys.Control | ModifierKeys.Shift, 0, 6, sqlTab: true),
                    // 기본 편집 키
                    new KeyCase("Tab (선택 없음)", Key.Tab, ModifierKeys.None, 7, 0),
                    new KeyCase("Tab (두 줄 선택)", Key.Tab, ModifierKeys.None, 0, 20),
                    new KeyCase("Shift+Tab (두 줄 선택)", Key.Tab, ModifierKeys.Shift, 0, 20),
                    new KeyCase("Shift+Tab (들여쓴 줄 캐럿)", Key.Tab, ModifierKeys.Shift, 13, 0),
                    new KeyCase("Ctrl+Z (x 입력 뒤)", Key.Z, ModifierKeys.Control, 9, 0, typeFirst: "x"),
                    new KeyCase("Ctrl+Y (x 입력·되돌리기 뒤)", Key.Y, ModifierKeys.Control, 9, 0, typeFirst: "x", undoFirst: true),
                    new KeyCase("Ctrl+A", Key.A, ModifierKeys.Control, 9, 0),
                    new KeyCase("Ctrl+D", Key.D, ModifierKeys.Control, 13, 0),
                    new KeyCase("Ctrl+I", Key.I, ModifierKeys.Control, 13, 0),
                    new KeyCase("Insert 뒤 Z 입력", Key.Insert, ModifierKeys.None, 7, 0, typeAfter: "Z"),
                    new KeyCase("Ctrl+Backspace", Key.Back, ModifierKeys.Control, 22, 0),
                    new KeyCase("Ctrl+Delete", Key.Delete, ModifierKeys.Control, 13, 0),
                    new KeyCase("Home (들여쓴 줄 끝)", Key.Home, ModifierKeys.None, 22, 0),
                    new KeyCase("Home 두 번 (들여쓴 줄 끝)", Key.Home, ModifierKeys.None, 22, 0, pressTwice: true),
                    new KeyCase("End", Key.End, ModifierKeys.None, 0, 0),
                    new KeyCase("Ctrl+Home", Key.Home, ModifierKeys.Control, 22, 0),
                    new KeyCase("Ctrl+End", Key.End, ModifierKeys.Control, 0, 0),
                    new KeyCase("Enter (들여쓴 줄 끝)", Key.Enter, ModifierKeys.None, 22, 0),
                };

                var rows = new List<string>();
                var differences = 0;
                foreach (var c in cases)
                {
                    active = c;
                    var boxResult = ui.Run(() => RunKey(box, box, c, start));
                    var editorResult = ui.Run(() => { dynamic ed = editor; return RunKey((UIElement)ed.TextArea, editor, c, start); });
                    active = null;
                    var same = boxResult == editorResult;
                    if (!same && !c.SqlTab)
                        differences++;
                    // SQL 탭 키는 SqlWorkspace 처리기가 먼저 처리하므로 편집기 자체 동작(이 표의 값)은 참고용
                    rows.Add("| " + c.Name + " | " + boxResult + " | " + editorResult + " | " + (c.SqlTab ? "SQL 탭이 먼저 처리(참고)" : same ? "같음" : "**다름**") + " |");
                }
                var sqlTabKeys = cases.Where(c => c.SqlTab).Select(c => c.Name).ToList();
                var lostToEditor = sqlTabKeys.Where(k => !reachedEditor.Contains(k)).ToList();
                Assert.Equal(sqlTabKeys, reachedBox);
                var cutCopyWholeLine = ui.Run(() => { dynamic ed = editor; return (bool)ed.Options.CutCopyWholeLine; });

                var table = "| 키 | TextBox (지금 SQL 탭 설정) | AvalonEdit | 비교 |\n|---|---|---|---|\n" + string.Join("\n", rows);
                File.WriteAllText(Path.Combine(plugin.ShotFolder, "poc-keys.md"), table + "\n");
                foreach (var row in rows)
                    _out.WriteLine("[키] " + row);
                Report(plugin, "D SQL 탭 편집기 키가 편집기 PreviewKeyDown 처리기에 먼저 오는지 (F4·F3·Shift+F3·Ctrl+F·Ctrl+Enter·Ctrl+/·Ctrl+Shift+U·L)",
                    lostToEditor.Count == 0 ? sqlTabKeys.Count + "개 모두 옴(편집기가 터널 단계에서 먹지 않음)" : "편집기가 먼저 먹음: " + string.Join(", ", lostToEditor), Judge(lostToEditor.Count == 0));
                Report(plugin, "D 기본 편집 키 동작 비교 (" + cases.Count(c => !c.SqlTab) + "가지, 표: shots/poc-keys.md)",
                    "다른 것 " + differences + "가지. Ctrl+X/C/V는 시험이 클립보드를 건드리지 않게 빼고 설정만 읽음: CutCopyWholeLine=" + cutCopyWholeLine
                    + (cutCopyWholeLine ? "(선택 없이 Ctrl+C·X면 줄 전체 — TextBox는 아무것도 안 함)" : ""), "키마다 결정 필요");
                Report(plugin, "D Folderss 본체 전역 단축키", "해당 없음 — 플러그인은 본체가 띄운 별도 창(PluginManager.ShowPluginWindow)이라 본체 Window_PreviewKeyDown을 지나지 않음. "
                    + "플러그인 창 단축키(Ctrl+N·O·S·Shift+S·W, F5)는 DbHelperView의 PreviewKeyDown(터널)이라 편집기보다 먼저 받음", "✓ (코드 확인)");
            }
            finally
            {
                ui.Run(() => { baseline.Close(); poc.Close(); main.Close(); });
            }
            Assert.Empty(ui.TakeErrors());
        }

        // ================= E. 테마 =================

        [UiFact]
        public async Task E_Themes_Seven()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var (main, poc, editor) = await OpenPoc(ui, plugin);
            try
            {
                var d1 = PocD1(plugin);
                ui.Run(() => SetEditorText(editor, d1));
                await Idle(ui);
                var rows = new List<string>();
                var stale = new List<string>();
                var low = 0;
                foreach (var theme in AllThemes)
                {
                    ui.Run(() => UiThread.ApplyTheme(theme));
                    await Idle(ui);
                    // 테마만 바꾸고 글은 그대로일 때 바로 따라오는지(바탕은 리소스 참조, 주석색은 다시 계산할 때만 읽는다)
                    var follows = ui.Run(() =>
                    {
                        var panel = Resource("PanelBackground");
                        var secondary = Resource("SecondaryText");
                        var comment = ColorOf((Brush)Field(Colorizer(editor), "Comment"));
                        return (ColorOf(((Control)editor).Background) == panel, comment == secondary);
                    });
                    if (!follows.Item2)
                        stale.Add(theme);

                    // 다시 넣어 새 테마 색으로 칠하고 둘째 줄을 선택해 캡처
                    ui.Run(() =>
                    {
                        SetEditorText(editor, "");
                        SetEditorText(editor, d1);
                        dynamic ed = editor;
                        ed.Select(ed.Document.GetLineByNumber(2).Offset, ed.Document.GetLineByNumber(2).Length);
                    });
                    await Idle(ui);
                    var measured = ui.Run(() =>
                    {
                        dynamic ed = editor;
                        var background = Resource("PanelBackground");
                        var colorizer = Colorizer(editor);
                        var selection = Over(ColorOf((Brush)ed.TextArea.SelectionBrush), background);
                        var selectionPixels = CountPixels((FrameworkElement)ed.TextArea, selection);
                        return new[]
                        {
                            Contrast(Resource("PrimaryText"), background),
                            Contrast(ColorOf((Brush)Field(colorizer, "Comment")), background),
                            Contrast(ColorOf((Brush)Field(colorizer, "String")), background),
                            Contrast(ColorOf((Brush)ed.LineNumbersForeground), background),
                            Contrast(Resource("PrimaryText"), selection),
                            selectionPixels,
                            (double)ed.TextArea.TextView.DefaultLineHeight
                        };
                    });
                    _out.WriteLine("캡처: " + ui.Run(() => Ui.Snap(poc, plugin.ShotFolder, "poc-theme-" + theme)));
                    low += measured.Take(5).Count(v => v < 4.5);
                    rows.Add("| " + theme + " | " + Ratio(measured[0]) + " | " + Ratio(measured[1]) + " | " + Ratio(measured[2]) + " | " + Ratio(measured[3]) + " | "
                        + Ratio(measured[4]) + " | " + (measured[5] > 0 ? "보임" : "**안 보임**") + " | " + (follows.Item1 ? "즉시" : "**안 바뀜**") + " / " + (follows.Item2 ? "즉시" : "**다시 칠할 때**") + " |");
                }
                var table = "| 테마 | 글자 | 주석 | 문자열 | 줄 번호 | 선택 위 글자 | 포커스 없는 선택 | 테마 변경: 바탕 / 주석색 |\n|---|---|---|---|---|---|---|---|\n" + string.Join("\n", rows);
                File.WriteAllText(Path.Combine(plugin.ShotFolder, "poc-themes.md"), "바탕(PanelBackground) 대비 명암비(WCAG, 목표 4.5:1)\n\n" + table + "\n");
                foreach (var row in rows)
                    _out.WriteLine("[테마] " + row);

                // 줄 간격·글꼴 크기: 지금 TextBox는 글꼴 13 · 줄 높이 19 고정(SqlWorkspace.EditorLineHeight)
                var lineHeight = ui.Run(() =>
                {
                    dynamic ed = editor;
                    var at13 = (double)ed.TextArea.TextView.DefaultLineHeight;
                    ed.FontSize = 16.0;
                    return at13;
                });
                await Idle(ui);
                var at16 = ui.Run(() => { dynamic ed = editor; var h = (double)ed.TextArea.TextView.DefaultLineHeight; ed.FontSize = 13.0; return h; });
                var textBoxLine = (double)plugin.Assembly.GetType("MyPlugin.SqlWorkspace", true).GetField("EditorLineHeight", BindingFlags.NonPublic | BindingFlags.Static).GetRawConstantValue();

                Report(plugin, "E 테마 7종 명암비·선택·즉시 반영 (표: shots/poc-themes.md, 캡처 poc-theme-*.png)",
                    "4.5:1 미만 칸 " + low + "개. 테마만 바꿨을 때 주석색이 바로 안 바뀌는 테마 " + stale.Count + "개(" + string.Join("·", stale) + ") — PoC는 다시 계산할 때만 색을 읽음",
                    low == 0 && stale.Count == 0 ? "✓" : "4단계에서 고칠 것");
                Report(plugin, "E 줄 간격·글꼴 크기", "AvalonEdit 기본 줄 높이 글꼴 13: " + Px(lineHeight) + " · 16: " + Px(at16) + " / TextBox 지금 19px 고정(" + Px(textBoxLine) + ")",
                    "3단계에서 맞출지 결정");
            }
            finally
            {
                ui.Run(() => { UiThread.ApplyTheme("Black"); poc.Close(); main.Close(); });
            }
            Assert.Empty(ui.TakeErrors());
        }

        // ================= 키 흉내 =================

        private sealed class KeyCase
        {
            public KeyCase(string name, Key key, ModifierKeys modifiers, int selectionStart, int selectionLength,
                bool sqlTab = false, string typeFirst = null, bool undoFirst = false, string typeAfter = null, bool pressTwice = false)
            {
                Name = name;
                Key = key;
                Modifiers = modifiers;
                SelectionStart = selectionStart;
                SelectionLength = selectionLength;
                SqlTab = sqlTab;
                TypeFirst = typeFirst;
                UndoFirst = undoFirst;
                TypeAfter = typeAfter;
                PressTwice = pressTwice;
            }

            public string Name { get; }
            public Key Key { get; }
            public ModifierKeys Modifiers { get; }
            public int SelectionStart { get; }
            public int SelectionLength { get; }
            public bool SqlTab { get; }
            public string TypeFirst { get; }
            public bool UndoFirst { get; }
            public string TypeAfter { get; }
            public bool PressTwice { get; }
        }

        /// <summary>
        /// 글·선택을 맞추고 키를 낸 뒤 "처리 여부 · 글 · 선택"을 한 줄로. target은 키를 받는 요소(TextBox 또는 TextArea),
        /// owner는 글·선택을 가진 쪽(TextBox 또는 TextEditor). UI 스레드에서 부른다.
        /// </summary>
        private static string RunKey(UIElement target, object owner, KeyCase c, string start)
        {
            var box = owner as TextBox;
            dynamic ed = box == null ? owner : null;
            if (box != null)
            {
                box.Text = start;
                box.IsUndoEnabled = false;
                box.IsUndoEnabled = true;
                box.Select(c.SelectionStart, c.SelectionLength);
            }
            else
            {
                ed.Document.Text = start;
                ed.Document.UndoStack.ClearAll();
                ed.Select(c.SelectionStart, c.SelectionLength);
            }
            // WPF TextBox의 Tab·Enter 처리기는 키보드 포커스가 없으면 아무것도 안 한다(TextEditorTyping: UiScope.IsKeyboardFocused) — 사람이 칠 때처럼 포커스를 준다
            Window.GetWindow(target).Activate();
            Keyboard.Focus(target);
            if (c.TypeFirst != null)
                TypeText(target, c.TypeFirst);
            if (c.UndoFirst)
                Press(target, Key.Z, ModifierKeys.Control);
            var handled = Press(target, c.Key, c.Modifiers);
            if (c.PressTwice)
                handled = Press(target, c.Key, c.Modifiers) && handled;
            if (c.TypeAfter != null)
                TypeText(target, c.TypeAfter);
            // 편집기가 디스패처에 미뤄 둔 처리까지 끝난 뒤에 읽는다(같은 스레드 Invoke는 그 우선순위까지 큐를 돌린다)
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(() => { }));
            string text = box != null ? box.Text : ed.Document.Text;
            int selectionStart = box != null ? box.SelectionStart : ed.SelectionStart;
            int selectionLength = box != null ? box.SelectionLength : ed.SelectionLength;
            int caret = box != null ? box.CaretIndex : ed.CaretOffset;
            var result = (handled ? "처리" : "통과") + (target.IsKeyboardFocused ? "" : "(포커스 없음)") + " · ";
            result += text == start ? "글 그대로" : "글 `" + Escape(text) + "`";
            result += selectionLength > 0 ? " · 선택 " + selectionStart + "+" + selectionLength : " · 캐럿 " + caret;
            return result;
        }

        private static string Escape(string text)
        {
            return text.Replace("\r\n", "⏎").Replace("\t", "⇥").Replace(" ", "·").Replace("|", "\\|");
        }

        /// <summary>PreviewKeyDown(터널) → 처리 안 됐으면 KeyDown(버블)을 그 요소에 낸다. 수정 키는 이 스레드의 키 상태로 흉내 낸다.</summary>
        private static bool Press(UIElement target, Key key, ModifierKeys modifiers)
        {
            var handled = false;
            WithModifiers(modifiers, () =>
            {
                var source = PresentationSource.FromVisual(target);
                var preview = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                target.RaiseEvent(preview);
                handled = preview.Handled;
                if (!handled)
                {
                    var down = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent };
                    target.RaiseEvent(down);
                    handled = down.Handled;
                }
            });
            return handled;
        }

        /// <summary>글자 입력(TextInput) — 키보드 장치를 거치지 않고 그 요소에 낸다.</summary>
        private static void TypeText(UIElement target, string text)
        {
            target.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, new TextComposition(InputManager.Current, target, text))
            {
                RoutedEvent = TextCompositionManager.TextInputEvent
            });
        }

        [DllImport("user32.dll")]
        private static extern bool GetKeyboardState(byte[] state);

        [DllImport("user32.dll")]
        private static extern bool SetKeyboardState(byte[] state);

        /// <summary>
        /// 이 스레드의 키 상태 표에서 Ctrl·Shift·Alt를 누른 것으로 두고 press를 부른 뒤 되돌린다. 키 입력을 보내는 것이 아니라(SendInput 아님)
        /// 이 스레드가 읽는 상태만 바꾸므로 다른 창·사용자 입력에는 영향이 없다. WPF Keyboard.Modifiers는 GetKeyState로 이 표를 읽는다.
        /// </summary>
        private static void WithModifiers(ModifierKeys modifiers, Action press)
        {
            var saved = new byte[256];
            GetKeyboardState(saved);
            var state = (byte[])saved.Clone();
            if ((modifiers & ModifierKeys.Control) != 0)
                state[0x11] = state[0xA2] = 0x80;
            if ((modifiers & ModifierKeys.Shift) != 0)
                state[0x10] = state[0xA0] = 0x80;
            if ((modifiers & ModifierKeys.Alt) != 0)
                state[0x12] = state[0xA4] = 0x80;
            SetKeyboardState(state);
            try
            {
                press();
            }
            finally
            {
                SetKeyboardState(saved);
            }
        }

        // ================= 창·편집기 =================

        private async Task<(Window main, Window poc, FrameworkElement editor)> OpenPoc(UiThread ui, PluginUnderTest plugin)
        {
            var main = ui.Run(() => plugin.OpenView());
            ui.Run(() => Ui.Menu(main, "도구", "AvalonEdit PoC"));
            var poc = await ui.WaitFor(() => Ui.Window("AvalonEdit PoC"), Wait, "AvalonEdit PoC 창");
            var editor = await ui.WaitFor(() => Ui.Find<FrameworkElement>(poc, e => e.GetType().FullName == EditorType), Wait, "AvalonEdit 편집기");
            await Idle(ui);
            return (main, poc, editor);
        }

        /// <summary>창 아래 실측 글(넣기→첫 화면 · 구간 계산 · 안내).</summary>
        private static string MeasureText(Window poc)
        {
            return Ui.Texts(poc).FirstOrDefault(t => t.Contains("16ms를 넘으면")) ?? "";
        }

        private static void SetEditorText(FrameworkElement editor, string text)
        {
            dynamic ed = editor;
            ed.Document.Text = text;
        }

        /// <summary>지금 SQL 탭 편집기(SqlWorkspace.CreateEditor)와 같은 설정의 TextBox — 비교 기준.</summary>
        private static TextBox NewBaselineTextBox(FontFamily font)
        {
            var box = new TextBox
            {
                AcceptsReturn = true,
                AcceptsTab = true,
                TextWrapping = TextWrapping.NoWrap,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = font,
                FontSize = 13,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6, 4, 6, 4),
                VerticalContentAlignment = VerticalAlignment.Top,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                IsInactiveSelectionHighlightEnabled = true
            };
            SpellCheck.SetIsEnabled(box, false);
            box.SetValue(TextBlock.LineHeightProperty, 19.0);
            box.SetValue(TextBlock.LineStackingStrategyProperty, LineStackingStrategy.BlockLineHeight);
            box.SetResourceReference(Control.BackgroundProperty, "PanelBackground");
            box.SetResourceReference(Control.ForegroundProperty, "PrimaryText");
            return box;
        }

        /// <summary>PoC 창과 같은 크기의 창에 비교용 TextBox 하나.</summary>
        private static Window BaselineWindow(FrameworkElement editor)
        {
            var window = new Window
            {
                Title = "TextBox 기준",
                Width = 1000,
                Height = 720,
                Left = 0,
                Top = 0,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Content = NewBaselineTextBox(((Control)editor).FontFamily)
            };
            window.Show();
            return window;
        }

        /// <summary>강조 없는 AvalonEdit(PoC와 같은 줄 번호·글꼴) — 편집기 자체 비용을 따로 보려고.</summary>
        private static FrameworkElement NewBareEditor(Type editorType, FontFamily font)
        {
            dynamic ed = Activator.CreateInstance(editorType);
            ed.ShowLineNumbers = true;
            ed.FontFamily = font;
            ed.FontSize = 13.0;
            ed.Options.EnableHyperlinks = false;
            ed.Options.EnableEmailHyperlinks = false;
            var element = (FrameworkElement)ed;
            element.SetResourceReference(Control.BackgroundProperty, "PanelBackground");
            element.SetResourceReference(Control.ForegroundProperty, "PrimaryText");
            return element;
        }

        private static Window BareEditorWindow(FrameworkElement editor)
        {
            var window = new Window
            {
                Title = "AvalonEdit 강조 없음",
                Width = 1000,
                Height = 720,
                Left = 0,
                Top = 0,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Content = NewBareEditor(editor.GetType(), ((Control)editor).FontFamily)
            };
            window.Show();
            return window;
        }

        // ================= 측정 =================

        private static Task Idle(UiThread ui)
        {
            return TimeUntilIdle(ui, () => { });
        }

        /// <summary>
        /// UI 스레드에서 work를 하고 화면을 다 그려 한가해질 때까지(ApplicationIdle) 걸린 시간(ms) — PoC 창 "넣기→첫 화면"과 같은 잣대.
        /// </summary>
        private static Task<double> TimeUntilIdle(UiThread ui, Action work)
        {
            var done = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
            ui.Post(() =>
            {
                var watch = Stopwatch.StartNew();
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    done.SetException(ex);
                    return;
                }
                ui.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => done.SetResult(watch.Elapsed.TotalMilliseconds)));
            });
            return done.Task;
        }

        /// <summary>가운데부터 한 화면씩 아래로 30번, 한 번마다 한가해질 때까지.</summary>
        private static async Task<List<double>> PageScroll(UiThread ui, Func<(double offset, double viewport, double extent)> read, Action<double> scrollTo)
        {
            var (_, viewport, extent) = ui.Run(read);
            var y = extent / 2;
            ui.Run(() => scrollTo(y));
            await Idle(ui);
            var times = new List<double>();
            for (var i = 0; i < 30; i++)
            {
                y += viewport;
                var target = y;
                times.Add(await TimeUntilIdle(ui, () => scrollTo(target)));
            }
            return times;
        }

        /// <summary>
        /// 탭 20개처럼: 한 창에 편집기 20개를 겹쳐 두고 하나씩 보이게(지금 SQL 탭과 같은 구조) 한 뒤 관리 힙 증가 ÷ 20, 편집기를 모두 뺀 뒤(탭 닫기) 줄어든 양 ÷ 20.
        /// 전용 바이트(PrivateMemorySize64)는 GC가 세그먼트를 바로 돌려주지 않아 이 크기에서는 잡음에 묻힌다.
        /// </summary>
        private static async Task<(double opened, double released)> MemoryPerTab(UiThread ui, Func<FrameworkElement> create)
        {
            await Idle(ui);
            var before = ManagedBytes();
            var window = ui.Run(() =>
            {
                var grid = new Grid();
                for (var i = 0; i < 20; i++)
                {
                    var element = create();
                    element.Visibility = Visibility.Collapsed;
                    grid.Children.Add(element);
                }
                var w = new Window { Title = "탭 20개", Width = 1000, Height = 720, ShowActivated = false, ShowInTaskbar = false, Content = grid };
                w.Show();
                return w;
            });
            for (var i = 0; i < 20; i++)
            {
                var index = i;
                await TimeUntilIdle(ui, () =>
                {
                    var grid = (Grid)window.Content;
                    for (var j = 0; j < grid.Children.Count; j++)
                        grid.Children[j].Visibility = j == index ? Visibility.Visible : Visibility.Collapsed;
                });
            }
            var after = ManagedBytes();
            // 탭 닫기처럼 창은 둔 채 편집기만 뺀다(SqlWorkspace는 닫은 탭의 편집기를 _editorHost에서 뺀다)
            ui.Run(() => ((Grid)window.Content).Children.Clear());
            await Idle(ui);
            await Task.Delay(2000);
            await Idle(ui);
            var closed = ManagedBytes();
            ui.Run(() => window.Close());
            await Idle(ui);
            return ((after - before) / 20.0, (after - closed) / 20.0);
        }

        private static long ManagedBytes()
        {
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            return GC.GetTotalMemory(true);
        }

        private static double Median(List<double> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            return sorted[sorted.Count / 2];
        }

        // ================= 플러그인 안 (리플렉션) =================

        private static object Colorizer(FrameworkElement editor)
        {
            dynamic ed = editor;
            foreach (object transformer in ed.TextArea.TextView.LineTransformers)
            {
                if (transformer.GetType().Name == "SpanColorizer")
                    return transformer;
            }
            throw new InvalidOperationException("PoC 색칠기(SpanColorizer)가 없습니다.");
        }

        private static object Field(object target, string name)
        {
            return target.GetType().GetField(name).GetValue(target);
        }

        private static Type PocData(PluginUnderTest plugin)
        {
            return plugin.Assembly.GetType("MyPlugin.AvalonEditPocWindow+PocData", true);
        }

        /// <summary>PoC 창의 D3 생성기(계획서 5절 구성 — 버튼과 같은 글).</summary>
        private static string PocD3(PluginUnderTest plugin, int lines)
        {
            return (string)PocData(plugin).GetMethod("D3").Invoke(null, new object[] { lines });
        }

        private static string PocD1(PluginUnderTest plugin)
        {
            return (string)PocData(plugin).GetField("D1").GetValue(null);
        }

        /// <summary>SqlScript.HighlightSpans(text, Oracle 규칙).</summary>
        private static Action<string> HighlightSpans(PluginUnderTest plugin)
        {
            var oracle = plugin.Assembly.GetType("MyPlugin.SqlLexRules", true).GetField("Oracle").GetValue(null);
            var method = plugin.Assembly.GetType("MyPlugin.SqlScript", true).GetMethod("HighlightSpans");
            return text => method.Invoke(null, new[] { text, oracle });
        }

        // ================= 색 =================

        private static Color Resource(string key)
        {
            return ColorOf((Brush)Application.Current.FindResource(key));
        }

        private static Color ColorOf(Brush brush)
        {
            var solid = brush as SolidColorBrush;
            if (solid == null)
                throw new InvalidOperationException("단색 붓이 아닙니다: " + (brush == null ? "null" : brush.GetType().Name));
            return solid.Color;
        }

        /// <summary>반투명 색을 바탕 위에 얹은 색.</summary>
        private static Color Over(Color top, Color background)
        {
            var a = top.A / 255.0;
            return System.Windows.Media.Color.FromRgb((byte)Math.Round(top.R * a + background.R * (1 - a)), (byte)Math.Round(top.G * a + background.G * (1 - a)),
                (byte)Math.Round(top.B * a + background.B * (1 - a)));
        }

        /// <summary>WCAG 2 명암비.</summary>
        private static double Contrast(Color a, Color b)
        {
            var la = Luminance(a);
            var lb = Luminance(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }

        private static double Luminance(Color c)
        {
            Func<byte, double> channel = v =>
            {
                var s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            };
            return 0.2126 * channel(c.R) + 0.7152 * channel(c.G) + 0.0722 * channel(c.B);
        }

        /// <summary>요소를 그려 이 색(±2)인 화소 수 — 선택 영역이 그려졌는지.</summary>
        private static double CountPixels(FrameworkElement element, Color color)
        {
            var width = Math.Max(1, (int)element.ActualWidth);
            var height = Math.Max(1, (int)element.ActualHeight);
            // 자식 요소를 바로 Render하면 부모 안 위치만큼 밀려 그려진다 — Ui.Snap처럼 VisualBrush로 (0,0)에
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
                dc.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, width, height));
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var pixels = new byte[width * height * 4];
            bitmap.CopyPixels(pixels, width * 4, 0);
            var count = 0;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                if (Math.Abs(pixels[i] - color.B) <= 2 && Math.Abs(pixels[i + 1] - color.G) <= 2 && Math.Abs(pixels[i + 2] - color.R) <= 2)
                    count++;
            }
            return count;
        }

        // ================= 기록 =================

        private static string Ms(double value)
        {
            return value.ToString("F1", CultureInfo.InvariantCulture) + "ms";
        }

        private static string Px(double value)
        {
            return value.ToString("0.#", CultureInfo.InvariantCulture) + "px";
        }

        private static string Mb(double bytes)
        {
            return (bytes / 1024 / 1024).ToString("F2", CultureInfo.InvariantCulture) + "MB";
        }

        private static string Ratio(double value)
        {
            return value.ToString("F1", CultureInfo.InvariantCulture) + (value < 4.5 ? " ✗" : "");
        }

        private static string Judge(bool ok)
        {
            return ok ? "✓" : "✗ 기준 넘음";
        }

        /// <summary>계획서 7절 표 한 줄을 shots/poc-measure.md에 덧붙이고 시험 출력에도 남긴다.</summary>
        private void Report(PluginUnderTest plugin, string item, string result, string judgement)
        {
            var row = "| " + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " | Claude (헤드리스) | " + MeasureEnvironment() + " | " + item + " | " + result + " | " + judgement + " |";
            File.AppendAllText(Path.Combine(plugin.ShotFolder, "poc-measure.md"), row + "\n");
            _out.WriteLine("[실측] " + row);
        }

        private static string _environment;

        private static string MeasureEnvironment()
        {
            if (_environment == null)
            {
                var cpu = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", "") as string;
                _environment = "Windows " + System.Environment.OSVersion.Version.Build + " · " + (cpu ?? "").Trim() + " " + System.Environment.ProcessorCount + "스레드 · .NET "
                    + System.Environment.Version + " · AvalonEdit 6.3.0.90 · 숨은 데스크톱(소프트웨어 렌더링)";
            }
            return _environment;
        }
    }
}

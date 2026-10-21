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
                // 첫 줄(-- 줄 주석 한글)은 주석색, 둘째 줄 SELECT는 키워드색, 'a -- 문자열 안'은 문자열색
                var d1 = PocD1(plugin);
                var colors = ui.Run(() =>
                {
                    var palette = Palette(plugin);
                    return (ForegroundAt(editor, 0) == palette.Comment, ForegroundAt(editor, d1.IndexOf("SELECT", StringComparison.Ordinal)) == palette.Keyword,
                        ForegroundAt(editor, d1.IndexOf("'a --", StringComparison.Ordinal)) == palette.String);
                });
                Assert.True(colors.Item1, "첫 줄 주석이 주석색이 아닙니다.");
                Assert.True(colors.Item2, "둘째 줄 SELECT가 키워드색이 아닙니다.");
                Assert.True(colors.Item3, "문자열이 문자열색이 아닙니다.");
                var load = ui.Run(() => MeasureText(poc).Split('\n')[0]);
                _out.WriteLine("캡처: " + ui.Run(() => Ui.Snap(poc, plugin.ShotFolder, "poc-d1")));

                // PoC 창의 자체 측정(버튼 → 창 아래 글)도 도는지
                ui.Run(() => Ui.Click(Ui.Button(poc, "D3-1 1만 줄")));
                await ui.WaitUntil(() => MeasureText(poc).StartsWith("D3-1", StringComparison.Ordinal), Wait, "D3-1 넣기 측정");
                var d3 = ui.Run(() => MeasureText(poc).Split('\n')[0]);

                Report(plugin, "A 플러그인 로드",
                    "PluginLoadContext에서 " + assembly.GetName().Name + " " + assembly.GetName().Version + " 로드(" + Path.GetFileName(Path.GetDirectoryName(assembly.Location)) + "\\…), "
                    + "기본 템플릿 적용, 그려진 줄 " + lines + "개, 주석·문자열·키워드 색칠 확인, UI 예외 0", "✓");
                Report(plugin, "A PoC 창 자체 측정", load + " / " + d3, "");
            }
            finally
            {
                ui.Run(() => { poc.Close(); main.Close(); });
            }
            Assert.Empty(ui.TakeErrors());
        }

        // ================= 키워드 색 (DBMS별) =================

        /// <summary>확인할 곳: needle의 nth번째 위치 첫 글자가 kind 색인지("Plain"이면 보통 글자색).</summary>
        private sealed class ColorCheck
        {
            public ColorCheck(string needle, string kind, int nth = 0, int skip = 0)
            {
                Needle = needle;
                Kind = kind;
                Nth = nth;
                Skip = skip;
            }

            public string Needle { get; }
            public string Kind { get; }
            public int Nth { get; }
            /// <summary>needle 안에서 확인할 글자 위치(예: "t.date"의 date → 2).</summary>
            public int Skip { get; }
        }

        [UiFact]
        public async Task K_Keywords_ColoredByRules()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var (main, poc, editor) = await OpenPoc(ui, plugin);
            const string oracle = "-- 주문 조회 (SELECT는 주석 안)\n"
                + "SELECT o.order_id, o.status AS \"Select\", t.date, :from\n"
                + "  FROM orders o\n"
                + "  JOIN customers c ON c.id = o.customer_id\n"
                + " WHERE o.status = 'SELECT FROM'\n"
                + "   AND o.amount BETWEEN 10 AND 100\n"
                + "   AND c.grade IS NOT NULL\n"
                + " GROUP BY o.order_id\n"
                + " ORDER BY o.order_id DESC;\n"
                + "CREATE TABLE t_order (\n"
                + "  id   NUMBER(10) PRIMARY KEY,\n"
                + "  name VARCHAR2(100) NOT NULL\n"
                + ");\n"
                + "SELECT TOP 1 x FROM a MINUS SELECT x FROM b;\n";
            const string postgres = "SELECT id FROM t WHERE name ILIKE 'a%' ORDER BY id LIMIT 10;\n"
                + "INSERT INTO t (id) VALUES (1) RETURNING id;\n"
                + "CREATE FUNCTION f() RETURNS int LANGUAGE plpgsql AS $$ BEGIN RETURN 1; END $$;\n"
                + "SELECT x FROM a MINUS SELECT x FROM b;\n";
            const string sqlServer = "SELECT TOP 10 [select], @from FROM dbo.t WHERE id = @id;\n"
                + "GO\n"
                + "DECLARE @x NVARCHAR(10) = N'from';\n"
                + "CREATE TABLE s (id INT IDENTITY(1,1) PRIMARY KEY, at DATETIME2);\n";
            var cases = new[]
            {
                ("Oracle", oracle, new[]
                {
                    new ColorCheck("-- 주문", "Comment"), new ColorCheck("SELECT o.", "Keyword"), new ColorCheck("AS \"", "Keyword"), new ColorCheck("\"Select\"", "Plain", skip: 1),
                    new ColorCheck("t.date", "Plain", skip: 2), new ColorCheck(":from", "Plain", skip: 1), new ColorCheck("FROM orders", "Keyword"), new ColorCheck("orders o", "Plain"),
                    new ColorCheck("JOIN", "Keyword"), new ColorCheck("ON c", "Keyword"), new ColorCheck("WHERE", "Keyword"), new ColorCheck("'SELECT FROM'", "String", skip: 1),
                    new ColorCheck("AND o.amount", "Keyword"), new ColorCheck("BETWEEN", "Keyword"), new ColorCheck("IS NOT", "Keyword"), new ColorCheck("NOT NULL", "Keyword"),
                    new ColorCheck("NULL\n", "Keyword"), new ColorCheck("GROUP", "Keyword"), new ColorCheck("ORDER", "Keyword"), new ColorCheck("DESC", "Keyword"),
                    new ColorCheck("CREATE", "Keyword"), new ColorCheck("TABLE", "Keyword"), new ColorCheck("NUMBER", "Keyword"), new ColorCheck("PRIMARY", "Keyword"),
                    new ColorCheck("KEY", "Keyword"), new ColorCheck("VARCHAR2", "Keyword"), new ColorCheck("t_order", "Plain"), new ColorCheck("MINUS", "Keyword"),
                    new ColorCheck("TOP", "Plain")
                }),
                ("PostgreSQL", postgres, new[]
                {
                    new ColorCheck("ILIKE", "Keyword"), new ColorCheck("LIMIT", "Keyword"), new ColorCheck("RETURNING", "Keyword"), new ColorCheck("LANGUAGE", "Keyword"),
                    new ColorCheck("plpgsql", "Plain"), new ColorCheck("BEGIN", "String"), new ColorCheck("'a%'", "String"), new ColorCheck("MINUS", "Plain")
                }),
                ("SQL Server", sqlServer, new[]
                {
                    new ColorCheck("TOP", "Keyword"), new ColorCheck("[select]", "Plain", skip: 1), new ColorCheck("@from", "Plain", skip: 1), new ColorCheck("FROM dbo", "Keyword"),
                    new ColorCheck("GO", "Keyword"), new ColorCheck("DECLARE", "Keyword"), new ColorCheck("NVARCHAR", "Keyword"), new ColorCheck("N'from'", "String"),
                    new ColorCheck("IDENTITY", "Keyword"), new ColorCheck("DATETIME2", "Keyword")
                })
            };
            try
            {
                var rows = new List<string>();
                var wrong = new List<string>();
                foreach (var (rules, text, checks) in cases)
                {
                    ui.Run(() => Ui.Choose(Ui.ById<ComboBox>(poc, "PocRules"), rules));
                    ui.Run(() => SetEditorText(editor, text));
                    await Idle(ui);
                    var results = ui.Run(() =>
                    {
                        var palette = Palette(plugin);
                        var plain = ColorOf(((Control)editor).Foreground);
                        return checks.Select(c =>
                        {
                            var at = Nth(text, c.Needle, c.Nth) + c.Skip;
                            var color = ForegroundAt(editor, at);
                            var expected = c.Kind == "Comment" ? palette.Comment : c.Kind == "String" ? palette.String : c.Kind == "Keyword" ? palette.Keyword : plain;
                            return (c, color == expected);
                        }).ToList();
                    });
                    foreach (var (c, ok) in results)
                    {
                        if (!ok)
                            wrong.Add(rules + " '" + c.Needle + "' → " + c.Kind + " 아님");
                    }
                    _out.WriteLine("캡처: " + ui.Run(() => Ui.Snap(poc, plugin.ShotFolder, "poc-keywords-" + rules.Replace(" ", ""))));
                    rows.Add(rules + " " + results.Count(r => r.Item2) + "/" + results.Count);
                }
                // 밝은 테마에서도 한 장
                ui.Run(() => { UiThread.ApplyTheme("Light"); Ui.Choose(Ui.ById<ComboBox>(poc, "PocRules"), "Oracle"); SetEditorText(editor, oracle); });
                await Idle(ui);
                _out.WriteLine("캡처: " + ui.Run(() => Ui.Snap(poc, plugin.ShotFolder, "poc-keywords-Oracle-Light")));
                Report(plugin, "키워드 색 (SELECT·FROM·JOIN·WHERE 조건·GROUP/ORDER BY·CREATE TABLE·자료형, DBMS별 목록, 문자열·주석·식별자·t.date·:from·@from·[select]는 아님)",
                    string.Join(" · ", rows) + (wrong.Count == 0 ? "" : " — 틀림: " + string.Join(", ", wrong)), Judge(wrong.Count == 0));
                Assert.Empty(wrong);
            }
            finally
            {
                ui.Run(() => { UiThread.ApplyTheme("Black"); poc.Close(); main.Close(); });
            }
            Assert.Empty(ui.TakeErrors());
        }

        // ================= B. 성능 =================

        [UiFact]
        public async Task B_Performance_IncrementalFullTextBox()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var (main, poc, editor) = await OpenPoc(ui, plugin);
            // 같은 크기 창에서 같은 데이터로: 강조를 붙인 AvalonEdit(SqlEditorHighlighting — 4단계가 쓸 것, PoC 창의 측정 글 갱신 없이; 증분 / 전체 다시 계산),
            // 강조 없는 AvalonEdit(편집기 자체 비용), 지금 SQL 탭 TextBox
            var litWindow = ui.Run(() => BareEditorWindow(editor));
            var lit = ui.Run(() => (FrameworkElement)litWindow.Content);
            dynamic highlighting = ui.Run(() => AttachHighlighting(plugin, lit));
            var bareWindow = ui.Run(() => BareEditorWindow(editor));
            var bare = ui.Run(() => (FrameworkElement)bareWindow.Content);
            var baseline = ui.Run(() => BaselineWindow(editor));
            var box = ui.Run(() => (TextBox)baseline.Content);
            var incremental = AvalonTarget("증분", lit, () => highlighting.Incremental = true);
            var full = AvalonTarget("전체", lit, () => highlighting.Incremental = false);
            var targets = new[] { incremental, full, AvalonTarget("강조 없음", bare, null), TextBoxTarget(box) };
            var datasets = new[]
            {
                ("D2 실무형 1만 줄", PocD2(plugin, 10000)),
                ("D3-1 1만 줄", PocD3(plugin, 10000)),
                ("D3-2 5만 줄", PocD3(plugin, 50000)),
                ("D3-3 10만 줄", PocD3(plugin, 100000)),
                ("D4-3 전부 주석 10만 줄", PocD43(plugin, 100000)),
                ("D4-1 한 줄 1MB", PocD41(plugin, 1 << 20))
            };
            var editorSpans = EditorSpans(plugin);
            try
            {
                foreach (var (name, text) in datasets)
                {
                    var size = name + " (" + Lines(text).ToString("N0", CultureInfo.InvariantCulture) + "줄 · " + (text.Length * 2 / 1024.0 / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + "MB)";
                    var load = targets.ToDictionary(t => t, t => new List<double>());
                    for (var i = 0; i < 3; i++)
                    {
                        foreach (var t in targets)
                        {
                            ui.Run(() => { t.Select?.Invoke(); t.SetText(""); });
                            await Idle(ui);
                            load[t].Add(await TimeUntilIdle(ui, () => t.SetText(text)));
                        }
                    }
                    Report(plugin, "B 넣기→첫 화면 " + size + " (3회 중앙값)", string.Join(" · ", targets.Select(t => t.Name + " " + Ms(Median(load[t])))),
                        Judge(Median(load[incremental]) <= Median(load[targets[3]]) * 1.2 || Median(load[incremental]) < 200));

                    // 키 입력: 가운데·끝에서 한 글자씩 10번, 입력부터 (다시 칠하고) 그려서 한가해질 때까지
                    foreach (var atEnd in new[] { false, true })
                    {
                        var typing = targets.ToDictionary(t => t, t => new List<double>());
                        foreach (var t in targets)
                        {
                            ui.Run(() => { t.Select?.Invoke(); t.SetText(text); });
                            await Idle(ui);
                            ui.Run(() => t.CaretAt(atEnd));
                            await Idle(ui);
                            var before = ui.Run(t.Length);
                            for (var i = 0; i < 10; i++)
                                typing[t].Add(await TimeUntilIdle(ui, () => TypeText(t.Input(), "x")));
                            Assert.Equal(10, ui.Run(t.Length) - before);
                        }
                        Report(plugin, "B 키 입력 " + name + " " + (atEnd ? "끝" : "가운데") + " (10회 중앙값 · 최대)",
                            string.Join(" / ", targets.Select(t => t.Name + " " + Ms(Median(typing[t])) + " · " + Ms(typing[t].Max()))),
                            Judge(Median(typing[incremental]) <= 16));
                    }

                    // 강조 구간 계산 단독(주석·문자열·키워드, 순수 함수, 5회 중앙값)
                    var alone = new List<double>();
                    var count = 0;
                    for (var i = 0; i < 5; i++)
                    {
                        var watch = Stopwatch.StartNew();
                        count = editorSpans(text);
                        alone.Add(watch.Elapsed.TotalMilliseconds);
                    }
                    Report(plugin, "B 강조 구간 전체 계산 단독 " + name + " (5회 중앙값)", Ms(Median(alone)) + " · 구간 " + count.ToString("N0", CultureInfo.InvariantCulture) + "개", "참고");
                }

                // 문서 뒤쪽 색이 뒤집히는 고치기(10만 줄): 맨 앞 /* 넣기·빼기(D4-2 — 첫 */까지), 가운데 SELECT 줄 앞 ' 넣기·빼기(뒤 문자열 짝이 어긋남)
                var big = PocD3(plugin, 100000);
                var middle = big.IndexOf("\r\nSELECT a, b, c", big.Length / 2, StringComparison.Ordinal) + 2;
                foreach (var t in new[] { incremental, full })
                {
                    ui.Run(() => { t.Select(); t.SetText(big); });
                    await Idle(ui);
                    var comment = new List<double>();
                    var quote = new List<double>();
                    var scanned = new List<string>();
                    for (var i = 0; i < 3; i++)
                    {
                        comment.Add(await TimeUntilIdle(ui, () => { dynamic ed = lit; ed.Document.Insert(0, "/*"); }));
                        scanned.Add(ui.Run(() => "/* 넣기 " + Scanned(highlighting)));
                        comment.Add(await TimeUntilIdle(ui, () => { dynamic ed = lit; ed.Document.Remove(0, 2); }));
                        quote.Add(await TimeUntilIdle(ui, () => { dynamic ed = lit; ed.Document.Insert(middle, "'"); }));
                        scanned.Add(ui.Run(() => "' 넣기 " + Scanned(highlighting)));
                        quote.Add(await TimeUntilIdle(ui, () => { dynamic ed = lit; ed.Document.Remove(middle, 1); }));
                    }
                    Report(plugin, "B 색이 뒤집히는 고치기 D3-3 " + t.Name + " (넣기·빼기 3회씩 중앙값)",
                        "맨 앞 /*: " + Ms(Median(comment)) + " · 가운데 ': " + Ms(Median(quote)) + " (" + scanned[0] + " · " + scanned[1] + ")",
                        Judge(Math.Max(Median(comment), Median(quote)) <= 1000));
                }

                // 스크롤: 10만 줄 끝으로 한 번에(3회), 가운데부터 한 화면씩 30번
                var scrolls = new List<string>();
                var pocPage = 0.0;
                foreach (var t in new[] { incremental, targets[2], targets[3] })
                {
                    ui.Run(() => { t.Select?.Invoke(); t.SetText(big); });
                    await Idle(ui);
                    var end = new List<double>();
                    for (var i = 0; i < 3; i++)
                    {
                        ui.Run(t.ScrollToHome);
                        await Idle(ui);
                        end.Add(await TimeUntilIdle(ui, t.ScrollToEnd));
                    }
                    var page = await PageScroll(ui, t.Scroll, t.ScrollTo);
                    if (t == incremental)
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
                ui.Run(() => { baseline.Close(); bareWindow.Close(); litWindow.Close(); poc.Close(); main.Close(); });
            }
            Assert.Empty(ui.TakeErrors());
        }

        /// <summary>SqlEditorHighlighting.Attach(편집기, Oracle 규칙) — UI 스레드에서 부른다.</summary>
        private static object AttachHighlighting(PluginUnderTest plugin, FrameworkElement editor)
        {
            var oracle = plugin.Assembly.GetType("MyPlugin.SqlLexRules", true).GetField("Oracle").GetValue(null);
            return plugin.Assembly.GetType("MyPlugin.SqlEditorHighlighting", true).GetMethod("Attach").Invoke(null, new[] { editor, oracle, null });
        }

        /// <summary>마지막 강조 계산: 시간 · 읽은 글자 · 다시 칠한 범위. UI 스레드에서 부른다.</summary>
        private static string Scanned(dynamic highlighting)
        {
            var last = highlighting.Last;
            return Ms((double)last.Milliseconds) + "·읽음 " + ((int)last.Scanned).ToString("N0", CultureInfo.InvariantCulture) + "자·칠함 "
                + ((int)last.Range).ToString("N0", CultureInfo.InvariantCulture) + "자";
        }

        /// <summary>성능 비교 대상 하나(동작은 UI 스레드에서 부른다).</summary>
        private sealed class Target
        {
            public string Name;
            public Action Select;               // 이 대상으로 재기 전에(PoC 강조 방식 고르기). 없으면 null
            public Action<string> SetText;      // 글을 넣고 맨 위로
            public Func<int> Length;
            public Action<bool> CaretAt;        // true면 끝, false면 가운데(가운데 줄 앞, 한 줄이면 그 줄 가운데) — 그 곳이 보이게
            public Func<UIElement> Input;       // 글자 입력을 받는 요소
            public Action ScrollToHome;
            public Action ScrollToEnd;
            public Func<(double offset, double viewport, double extent)> Scroll;
            public Action<double> ScrollTo;
        }

        private static Target AvalonTarget(string name, FrameworkElement editor, Action select)
        {
            dynamic ed = editor;
            return new Target
            {
                Name = name,
                Select = select,
                SetText = text => { ed.Document.Text = text; ed.ScrollToHome(); },
                Length = () => (int)ed.Document.TextLength,
                CaretAt = atEnd =>
                {
                    int lines = ed.Document.LineCount;
                    int offset = atEnd ? ed.Document.TextLength
                        : lines <= 2 ? ed.Document.TextLength / 2 : ed.Document.GetLineByNumber(lines / 2).Offset;
                    ed.TextArea.Caret.Offset = offset;
                    ed.TextArea.Caret.BringCaretToView();
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
                    box.CaretIndex = atEnd ? box.Text.Length
                        : box.LineCount <= 2 ? box.Text.Length / 2 : box.GetCharacterIndexFromLineIndex(box.LineCount / 2);
                    var rect = box.GetRectFromCharacterIndex(box.CaretIndex);
                    box.ScrollToLine(box.GetLineIndexFromCharacterIndex(box.CaretIndex));
                    if (!rect.IsEmpty)
                        box.ScrollToHorizontalOffset(Math.Max(0, box.HorizontalOffset + rect.X - box.ViewportWidth / 2));
                },
                Input = () => box,
                ScrollToHome = box.ScrollToHome,
                ScrollToEnd = box.ScrollToEnd,
                Scroll = () => (box.VerticalOffset, box.ViewportHeight, box.ExtentHeight),
                ScrollTo = box.ScrollToVerticalOffset
            };
        }

        private static int Lines(string text)
        {
            var count = 1;
            foreach (var c in text)
            {
                if (c == '\n')
                    count++;
            }
            return text.EndsWith("\n", StringComparison.Ordinal) ? count - 1 : count;
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
                    // 키 결정(계획서 8절): Ctrl+D는 끄고 Insert는 겹쳐쓰기 전환 — 지금 TextBox와 같아야 한다(SqlEditorSetup.ApplyBehavior)
                    if (c.Name == "Ctrl+D" || c.Name.StartsWith("Insert", StringComparison.Ordinal))
                        Assert.True(same, c.Name + ": TextBox " + boxResult + " / AvalonEdit " + editorResult);
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
                    // 테마만 바꾸고 글은 그대로일 때 바로 따라오는지: 바탕은 리소스 참조, 강조색은 바탕이 바뀌면 다시 칠한다 — 그려진 줄의 색으로 본다
                    var follows = ui.Run(() =>
                    {
                        var palette = Palette(plugin);
                        return (ColorOf(((Control)editor).Background) == Resource("PanelBackground"), ForegroundAt(editor, 0) == palette.Comment
                            && ForegroundAt(editor, d1.IndexOf("SELECT", StringComparison.Ordinal)) == palette.Keyword);
                    });
                    if (!follows.Item2)
                        stale.Add(theme);

                    // 둘째 줄을 선택해 캡처
                    ui.Run(() =>
                    {
                        dynamic ed = editor;
                        ed.Select(ed.Document.GetLineByNumber(2).Offset, ed.Document.GetLineByNumber(2).Length);
                    });
                    await Idle(ui);
                    var measured = ui.Run(() =>
                    {
                        dynamic ed = editor;
                        var background = Resource("PanelBackground");
                        var text = Resource("PrimaryText");
                        var comment = ForegroundAt(editor, 0).Value;
                        var selection = Over(ColorOf((Brush)ed.TextArea.SelectionBrush), background);
                        var selectionPixels = CountPixels((FrameworkElement)ed.TextArea, selection);
                        return new[]
                        {
                            Contrast(text, background),
                            Contrast(comment, background),
                            Contrast(ForegroundAt(editor, d1.IndexOf("'a --", StringComparison.Ordinal)).Value, background),
                            Contrast(ForegroundAt(editor, d1.IndexOf("SELECT", StringComparison.Ordinal)).Value, background),
                            Contrast(ColorOf((Brush)ed.LineNumbersForeground), background),
                            Contrast(text, selection),
                            Contrast(comment, text),
                            selectionPixels
                        };
                    });
                    _out.WriteLine("캡처: " + ui.Run(() => Ui.Snap(poc, plugin.ShotFolder, "poc-theme-" + theme)));
                    low += measured.Take(6).Count(v => v < 4.5);
                    rows.Add("| " + theme + " | " + Ratio(measured[0]) + " | " + Ratio(measured[1]) + " | " + Ratio(measured[2]) + " | " + Ratio(measured[3]) + " | "
                        + Ratio(measured[4]) + " | " + Ratio(measured[5]) + " | " + measured[6].ToString("F1", CultureInfo.InvariantCulture) + (measured[6] < 2 ? " ✗" : "") + " | "
                        + (measured[7] > 0 ? "보임" : "**안 보임**") + " | " + (follows.Item1 ? "즉시" : "**안 바뀜**") + " / " + (follows.Item2 ? "즉시" : "**다시 칠할 때**") + " |");
                }
                var table = "| 테마 | 글자 | 주석 | 문자열 | 키워드 | 줄 번호 | 선택 위 글자 | 주석:본문 구분 | 포커스 없는 선택 | 테마 변경: 바탕 / 강조색 |\n|---|---|---|---|---|---|---|---|---|---|\n" + string.Join("\n", rows);
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
                    "4.5:1 미만 칸 " + low + "개(글자·주석·문자열·키워드·줄 번호·선택 위 글자). 테마만 바꿨을 때 강조색이 바로 안 바뀌는 테마 " + stale.Count + "개"
                    + (stale.Count == 0 ? "" : "(" + string.Join("·", stale) + ")"),
                    Judge(low == 0 && stale.Count == 0));
                Report(plugin, "E 줄 간격·글꼴 크기", "AvalonEdit 기본 줄 높이 글꼴 13: " + Px(lineHeight) + " · 16: " + Px(at16) + " / TextBox 지금 19px 고정(" + Px(textBoxLine) + ")",
                    "3단계에서 맞출지 결정");
            }
            finally
            {
                ui.Run(() => { UiThread.ApplyTheme("Black"); poc.Close(); main.Close(); });
            }
            Assert.Empty(ui.TakeErrors());
        }

        // ================= 설정 "SQL 편집기" 탭 =================

        /// <summary>
        /// 설정 "SQL 편집기" 탭: 칸을 바꾸면 미리 보기가 바로 따르고, [저장]하면 열린 PoC 창이 따른다 —
        /// 줄 간격 19px, 키워드 색·굵게, 주석 끔, 더 칠할 낱말(NVL)·칠하지 않을 낱말(DATE), 긴 줄 기준(5,000자). 잘못 적은 값은 저장 실패, [기본값으로 되돌리기].
        /// </summary>
        [UiFact]
        public async Task S_SettingsPage_AppliesToEditor()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var page = plugin.Manager.SettingsPages.Single(p => p.Title == "SQL 편집기");
            var (main, poc, editor) = await OpenPoc(ui, plugin);
            var longLine = "SELECT " + string.Join(", ", Enumerable.Range(1, 900).Select(i => "c" + i)) + " FROM t;";
            var text = "-- 주석 줄\nSELECT NVL(a, 1), b FROM t WHERE d > DATE '2026-01-01';\n" + longLine + "\n";
            var host = ui.Run(() =>
            {
                var window = new Window { Title = "설정", Content = page.CreateView(), Width = 820, Height = 1150, ShowActivated = false, ShowInTaskbar = false };
                window.SetResourceReference(Window.BackgroundProperty, "WindowBackground");
                window.Show();
                return window;
            });
            try
            {
                ui.Run(() => SetEditorText(editor, text));
                await Idle(ui);
                var at = new Func<string, int>(needle => Nth(text, needle, 0));
                var before = ui.Run(() =>
                {
                    dynamic ed = editor;
                    var palette = Palette(plugin);
                    var plain = ColorOf(((Control)editor).Foreground);
                    return ((double)ed.TextArea.TextView.DefaultLineHeight, ForegroundAt(editor, 0) == palette.Comment, ForegroundAt(editor, at("NVL")) == plain,
                        ForegroundAt(editor, at("DATE")) == palette.Keyword, ForegroundAt(editor, text.IndexOf(longLine, StringComparison.Ordinal)) == palette.Keyword);
                });
                Assert.Equal((15.0, true, true, true, true), before);
                _out.WriteLine("캡처: " + ui.Run(() => Ui.Snap(host, plugin.ShotFolder, "settings-1-default")));

                // 칸 바꾸기 → 미리 보기
                ui.Run(() =>
                {
                    Ui.Choose(Ui.ById<ComboBox>(host, "sqleditor-line-height"), "19px (지금 SQL 탭과 같음)");
                    Ui.ById<TextBox>(host, "sqleditor-max-line").Text = "5000";
                    Ui.ById<TextBox>(host, "sqleditor-style-keyword-color").Text = "#FF00FF";
                    Ui.ClickToggle(Ui.ById<CheckBox>(host, "sqleditor-style-keyword-bold"));
                    Ui.ClickToggle(Ui.ById<CheckBox>(host, "sqleditor-style-comment-enabled"));
                    Ui.ById<TextBox>(host, "sqleditor-extra-keywords").Text = "nvl";
                    Ui.ById<TextBox>(host, "sqleditor-excluded-keywords").Text = "DATE";
                });
                await Idle(ui);
                var preview = ui.Run(() =>
                {
                    var view = Ui.ById<FrameworkElement>(host, "sqleditor-preview");
                    dynamic pv = view;
                    string previewText = pv.Document.Text;
                    return ((double)pv.TextArea.TextView.DefaultLineHeight, ForegroundAt(view, previewText.IndexOf("SELECT", StringComparison.Ordinal)),
                        Ui.ById<TextBlock>(host, "sqleditor-style-keyword-contrast").Text, Ui.ById<TextBlock>(host, "sqleditor-error").Text);
                });
                Assert.Equal(19.0, preview.Item1);
                Assert.Equal(System.Windows.Media.Color.FromRgb(0xFF, 0x00, 0xFF), preview.Item2);
                Assert.EndsWith(":1", preview.Item3);
                Assert.Equal("", preview.Item4);
                _out.WriteLine("캡처: " + ui.Run(() => Ui.Snap(host, plugin.ShotFolder, "settings-2-changed")));

                // 잘못 적은 값은 저장 실패(설정 창이 알린다)
                ui.Run(() =>
                {
                    Ui.ById<TextBox>(host, "sqleditor-max-line").Text = "12";
                    var error = Assert.Throws<InvalidOperationException>(() => page.Save());
                    Assert.Contains("1,000~1,000,000", error.Message);
                    Assert.Contains("1,000~1,000,000", Ui.ById<TextBlock>(host, "sqleditor-error").Text);
                    Ui.ById<TextBox>(host, "sqleditor-max-line").Text = "5000";
                    page.Save();
                });
                Assert.Contains("\"maxColoredLine\":5000", plugin.Manager.GetSetting("sqlEditor.settings"));
                await Idle(ui);

                // 열린 PoC 창이 바로 따른다
                var magenta = System.Windows.Media.Color.FromRgb(0xFF, 0x00, 0xFF);
                var after = ui.Run(() =>
                {
                    dynamic ed = editor;
                    var plain = ColorOf(((Control)editor).Foreground);
                    return ((double)ed.TextArea.TextView.DefaultLineHeight, ForegroundAt(editor, 0) == plain, ForegroundAt(editor, at("SELECT")) == magenta,
                        WeightAt(editor, at("SELECT")) == FontWeights.Bold, ForegroundAt(editor, at("NVL")) == magenta, ForegroundAt(editor, at("DATE")) == plain,
                        ForegroundAt(editor, text.IndexOf(longLine, StringComparison.Ordinal)) == plain, MeasureText(poc));
                });
                _out.WriteLine(after.Item8);
                Assert.Equal((19.0, true, true, true, true, true, true), (after.Item1, after.Item2, after.Item3, after.Item4, after.Item5, after.Item6, after.Item7));
                Assert.Contains("줄 간격 19px", after.Item8);
                Assert.Contains("5,000자 넘는 줄은 칠하지 않음", after.Item8);
                _out.WriteLine("캡처: " + ui.Run(() => Ui.Snap(poc, plugin.ShotFolder, "settings-3-poc-after-save")));

                // 기본값으로 되돌리기 → 저장 → 처음 모습
                ui.Run(() =>
                {
                    Ui.Click(Ui.ById<Button>(host, "sqleditor-reset"));
                });
                await Idle(ui);
                ui.Run(() =>
                {
                    Assert.Equal("20000", Ui.ById<TextBox>(host, "sqleditor-max-line").Text);
                    Assert.Equal("", Ui.ById<TextBox>(host, "sqleditor-style-keyword-color").Text);
                    page.Save();
                });
                await Idle(ui);
                var reset = ui.Run(() =>
                {
                    dynamic ed = editor;
                    var palette = Palette(plugin);
                    return ((double)ed.TextArea.TextView.DefaultLineHeight, ForegroundAt(editor, 0) == palette.Comment, ForegroundAt(editor, at("SELECT")) == palette.Keyword);
                });
                Assert.Equal((15.0, true, true), reset);
                Assert.True(ui.Run(() => SqlSettingsIsDefault(plugin)));
                Report(plugin, "설정 \"SQL 편집기\" 탭",
                    "줄 간격 15→19px, 키워드 색·굵게, 주석 끔, 더 칠할 NVL·칠하지 않을 DATE, 긴 줄 기준 5,000자 — 미리 보기는 바로, [저장]하면 열린 PoC 창이 바로 따름. 범위 밖 값은 저장 실패, 기본값으로 되돌리기",
                    "✓");
            }
            finally
            {
                ui.Run(() =>
                {
                    // 다른 시험이 기본 설정을 보도록
                    plugin.Manager.SetSetting("sqlEditor.settings", null);
                    host.Close();
                    poc.Close();
                    main.Close();
                });
            }
            Assert.Empty(ui.TakeErrors());
        }

        private static bool SqlSettingsIsDefault(PluginUnderTest plugin)
        {
            var type = plugin.Assembly.GetType("MyPlugin.SqlEditorSettings", true);
            dynamic settings = type.GetMethod("Parse").Invoke(null, new object[] { plugin.Manager.GetSetting("sqlEditor.settings") });
            return (bool)settings.IsDefault;
        }

        /// <summary>그 위치 글자의 굵기(그 줄의 VisualLine 요소에서). UI 스레드에서 부른다.</summary>
        private static FontWeight WeightAt(FrameworkElement editor, int offset)
        {
            dynamic ed = editor;
            var line = ed.Document.GetLineByOffset(offset);
            var visual = ed.TextArea.TextView.GetOrConstructVisualLine(line);
            int relative = offset - (int)visual.FirstDocumentLine.Offset;
            foreach (var element in visual.Elements)
            {
                int start = element.RelativeTextOffset;
                int length = element.DocumentLength;
                if (relative >= start && relative < start + length)
                    return ((Typeface)element.TextRunProperties.Typeface).Weight;
            }
            return FontWeights.Normal;
        }

        // ================= 기능 둘러보기 캡처 (보고서용) =================

        /// <summary>
        /// 플러그인 단독 호스트에서 사람이 쓰는 순서대로 화면을 띄워 캡처한다(shots/tour-*.png, 보고서용): DB Helper 창 → 도구 메뉴 →
        /// AvalonEdit PoC 창의 데이터·DBMS 규칙·강조 방식·테마별 모습. 각 장면에서 그려진 색이 기대대로인지도 몇 군데 확인한다.
        /// </summary>
        [UiFact]
        public async Task T_FeatureTour_Captures()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var main = ui.Run(() => plugin.OpenView());
            Window poc = null;
            var shots = new List<string>();
            Action<string> snap = name => shots.Add(ui.Run(() => Ui.Snap(poc, plugin.ShotFolder, name)));
            try
            {
                await Idle(ui);
                shots.Add(ui.Run(() => Ui.Snap(main, plugin.ShotFolder, "tour-01-main")));
                // 도구 메뉴를 펼친 모습(팝업은 창 밖이라 팝업 내용만 따로)
                var menuShot = ui.Run(() =>
                {
                    var tools = Ui.Find<Menu>(main).Items.OfType<MenuItem>().First(m => Ui.HeaderText(m).StartsWith("도구", StringComparison.Ordinal));
                    tools.IsSubmenuOpen = true;
                    return tools;
                });
                await Idle(ui);
                var menuPath = ui.Run(() =>
                {
                    var popup = menuShot.Template.FindName("PART_Popup", menuShot) as System.Windows.Controls.Primitives.Popup;
                    var child = popup == null ? null : popup.Child as FrameworkElement;
                    // 숨은 데스크톱에서는 팝업 창이 배치되지 않을 수 있어 직접 잰다
                    if (child != null && child.ActualWidth < 1)
                    {
                        child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        child.Arrange(new Rect(child.DesiredSize));
                    }
                    var path = child == null || child.DesiredSize.Width < 1 ? null : Ui.SnapElement(child, (Brush)Application.Current.FindResource("SurfaceBackground"), plugin.ShotFolder, "tour-02-tools-menu");
                    menuShot.IsSubmenuOpen = false;
                    return path;
                });
                if (menuPath != null)
                    shots.Add(menuPath);

                ui.Run(() => Ui.Menu(main, "도구", "AvalonEdit PoC"));
                poc = await ui.WaitFor(() => Ui.Window("AvalonEdit PoC"), Wait, "AvalonEdit PoC 창");
                var editor = await ui.WaitFor(() => Ui.Find<FrameworkElement>(poc, e => e.GetType().FullName == EditorType), Wait, "AvalonEdit 편집기");
                await Idle(ui);

                // D1 경계 사례: 규칙 셋으로 같은 글이 다르게 칠해진다
                ui.Run(() => Ui.Click(Ui.Button(poc, "D1 경계 사례")));
                await ui.WaitUntil(() => MeasureText(poc).StartsWith("D1:", StringComparison.Ordinal), Wait, "D1");
                await Idle(ui);
                snap("tour-03-d1-oracle");
                foreach (var rules in new[] { "PostgreSQL", "SQL Server" })
                {
                    ui.Run(() => Ui.Choose(Ui.ById<ComboBox>(poc, "PocRules"), rules));
                    await Idle(ui);
                    snap("tour-04-d1-" + rules.Replace(" ", "").ToLowerInvariant());
                }

                // D2 실무형(조회·조건·조인·집계, 테이블 정의, DML, PL/SQL 배치) — Oracle, 어두운·밝은 테마
                ui.Run(() => Ui.Choose(Ui.ById<ComboBox>(poc, "PocRules"), "Oracle"));
                ui.Run(() => Ui.Click(Ui.Button(poc, "D2 실무형 1천 줄")));
                await ui.WaitUntil(() => MeasureText(poc).StartsWith("D2", StringComparison.Ordinal), Wait, "D2");
                await Idle(ui);
                snap("tour-05-d2-black");
                var d2Ok = ui.Run(() =>
                {
                    var palette = Palette(plugin);
                    dynamic ed = editor;
                    string text = ed.Document.Text;
                    return ForegroundAt(editor, text.IndexOf("SELECT", StringComparison.Ordinal)) == palette.Keyword
                        && ForegroundAt(editor, text.IndexOf("-- =====", StringComparison.Ordinal)) == palette.Comment
                        && ForegroundAt(editor, text.IndexOf("'PAID'", StringComparison.Ordinal)) == palette.String;
                });
                Assert.True(d2Ok, "D2 첫 화면의 키워드·주석·문자열 색이 기대와 다릅니다.");
                foreach (var theme in AllThemes.Where(t => t != "Black"))
                {
                    ui.Run(() => UiThread.ApplyTheme(theme));
                    await Idle(ui);
                    snap("tour-06-d2-" + theme.ToLowerInvariant());
                }
                ui.Run(() => UiThread.ApplyTheme("Black"));
                await Idle(ui);

                // 10만 줄에서 가운데에 몇 글자 쳐 본 뒤 창 아래 측정 글: 증분 / 전체 다시 계산
                ui.Run(() => Ui.Click(Ui.Button(poc, "D3-3 10만 줄")));
                await ui.WaitUntil(() => MeasureText(poc).StartsWith("D3-3", StringComparison.Ordinal), Wait, "D3-3");
                foreach (var mode in new[] { "증분 강조", "전체 다시 계산(비교용)" })
                {
                    ui.Run(() =>
                    {
                        Ui.Choose(Ui.ById<ComboBox>(poc, "PocMode"), mode);
                        dynamic ed = editor;
                        var line = ed.Document.GetLineByNumber((int)ed.Document.LineCount / 2);
                        ed.TextArea.Caret.Offset = (int)line.Offset;
                        ed.ScrollToLine((int)line.LineNumber);
                    });
                    await Idle(ui);
                    foreach (var word in new[] { "SELECT", " ", "x", " ", "FROM", " ", "t", ";", "\n" })
                    {
                        await TimeUntilIdle(ui, () => { dynamic ed = editor; TypeText((UIElement)ed.TextArea, word); });
                    }
                    snap(mode.StartsWith("증분", StringComparison.Ordinal) ? "tour-07-incremental" : "tour-08-full");
                }
                ui.Run(() => Ui.Choose(Ui.ById<ComboBox>(poc, "PocMode"), "증분 강조"));

                // D4-2: 맨 앞에 /* 를 넣으면 첫 */까지 주석이 된다
                ui.Run(() => Ui.Click(Ui.Button(poc, "D4-2 맨 앞 /* 넣기·빼기")));
                await Idle(ui);
                ui.Run(() => { dynamic ed = editor; ed.ScrollToHome(); });
                await Idle(ui);
                snap("tour-09-d42-leading-comment");
                ui.Run(() => Ui.Click(Ui.Button(poc, "D4-2 맨 앞 /* 넣기·빼기")));
                await Idle(ui);

                // 설정 "SQL 편집기" 탭: 기본 모습, 고친 모습(미리 보기만 — 저장하지 않음)
                var page = plugin.Manager.SettingsPages.Single(p => p.Title == "SQL 편집기");
                var settingsWindow = ui.Run(() =>
                {
                    var window = new Window { Title = "설정", Content = page.CreateView(), Width = 820, Height = 1150, ShowActivated = false, ShowInTaskbar = false };
                    window.SetResourceReference(Window.BackgroundProperty, "WindowBackground");
                    window.Show();
                    return window;
                });
                await Idle(ui);
                shots.Add(ui.Run(() => Ui.Snap(settingsWindow, plugin.ShotFolder, "tour-10-settings")));
                ui.Run(() =>
                {
                    Ui.Choose(Ui.ById<ComboBox>(settingsWindow, "sqleditor-line-height"), "19px (지금 SQL 탭과 같음)");
                    Ui.ClickToggle(Ui.ById<CheckBox>(settingsWindow, "sqleditor-style-keyword-bold"));
                    Ui.ById<TextBox>(settingsWindow, "sqleditor-style-datatype-color").Text = "#C678DD";
                    Ui.ClickToggle(Ui.ById<CheckBox>(settingsWindow, "sqleditor-style-comment-italic"));
                    Ui.ById<TextBox>(settingsWindow, "sqleditor-extra-keywords").Text = "NVL";
                });
                await Idle(ui);
                shots.Add(ui.Run(() => Ui.Snap(settingsWindow, plugin.ShotFolder, "tour-11-settings-custom")));
                ui.Run(() => settingsWindow.Close());

                File.WriteAllLines(Path.Combine(plugin.ShotFolder, "tour-shots.txt"), shots.Select(Path.GetFileName));
                foreach (var shot in shots)
                    _out.WriteLine("캡처: " + shot);
            }
            finally
            {
                ui.Run(() =>
                {
                    UiThread.ApplyTheme("Black");
                    if (poc != null)
                        poc.Close();
                    main.Close();
                });
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

        private static Type PocData(PluginUnderTest plugin)
        {
            return plugin.Assembly.GetType("MyPlugin.AvalonEditPocWindow+PocData", true);
        }

        private static string PocData(PluginUnderTest plugin, string method, int value)
        {
            return (string)PocData(plugin).GetMethod(method).Invoke(null, new object[] { value });
        }

        /// <summary>PoC 창의 시험 데이터 생성기(계획서 5절 구성 — 버튼과 같은 글): D2 실무형, D3 크기별, D4-1 한 줄, D4-3 전부 주석.</summary>
        private static string PocD2(PluginUnderTest plugin, int lines) { return PocData(plugin, "D2", lines); }

        private static string PocD3(PluginUnderTest plugin, int lines) { return PocData(plugin, "D3", lines); }

        private static string PocD41(PluginUnderTest plugin, int chars) { return PocData(plugin, "D41", chars); }

        private static string PocD43(PluginUnderTest plugin, int lines) { return PocData(plugin, "D43", lines); }

        private static string PocD1(PluginUnderTest plugin)
        {
            return (string)PocData(plugin).GetField("D1").GetValue(null);
        }

        /// <summary>SqlScript.EditorSpans(text, Oracle 규칙) — 구간 수를 돌려준다.</summary>
        private static Func<string, int> EditorSpans(PluginUnderTest plugin)
        {
            var oracle = plugin.Assembly.GetType("MyPlugin.SqlLexRules", true).GetField("Oracle").GetValue(null);
            var method = plugin.Assembly.GetType("MyPlugin.SqlScript", true).GetMethod("EditorSpans");
            return text => ((System.Collections.ICollection)method.Invoke(null, new[] { text, oracle, (object)0 })).Count;
        }

        /// <summary>지금 테마에서 PoC가 쓸 강조색(SqlHighlightLogic.Palette — 플러그인과 같은 계산). UI 스레드에서 부른다.</summary>
        private static (Color Comment, Color String, Color Keyword) Palette(PluginUnderTest plugin)
        {
            var method = plugin.Assembly.GetType("MyPlugin.SqlHighlightLogic", true).GetMethod("Palette");
            var result = method.Invoke(null, new object[] { Rgb(Resource("PanelBackground")), Rgb(Resource("PrimaryText")), Rgb(Resource("SecondaryText")), Rgb(Resource("AccentBrush")) });
            Func<string, Color> item = name =>
            {
                var rgb = (int)result.GetType().GetField(name).GetValue(result);
                return System.Windows.Media.Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            };
            return (item("Item1"), item("Item2"), item("Item3"));
        }

        private static int Rgb(Color color)
        {
            return (color.R << 16) | (color.G << 8) | color.B;
        }

        /// <summary>편집기가 그 위치 글자를 그리는 색(그 줄의 VisualLine 요소에서). UI 스레드에서 부른다.</summary>
        private static Color? ForegroundAt(FrameworkElement editor, int offset)
        {
            dynamic ed = editor;
            var line = ed.Document.GetLineByOffset(offset);
            var visual = ed.TextArea.TextView.GetOrConstructVisualLine(line);
            int relative = offset - (int)visual.FirstDocumentLine.Offset;
            foreach (var element in visual.Elements)
            {
                int start = element.RelativeTextOffset;
                int length = element.DocumentLength;
                if (relative >= start && relative < start + length)
                    return ((SolidColorBrush)element.TextRunProperties.ForegroundBrush).Color;
            }
            return null;
        }

        private static int Nth(string text, string needle, int nth)
        {
            var at = -1;
            for (var i = 0; i <= nth; i++)
            {
                at = text.IndexOf(needle, at + 1, StringComparison.Ordinal);
                if (at < 0)
                    throw new InvalidOperationException("글에 '" + needle + "'가 " + (nth + 1) + "번 없습니다.");
            }
            return at;
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

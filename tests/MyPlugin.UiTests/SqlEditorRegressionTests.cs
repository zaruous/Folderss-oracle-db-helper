using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MyPlugin.UiTests.Headless;
using Xunit;

namespace MyPlugin.UiTests
{
    /// <summary>
    /// SQL 탭 편집기 교체(3단계) 회귀 확인표(docs/sql-editor-avalonedit-plan.md 6절)를 헤드리스로: 탭별 상태 유지, 되돌리기 한 번, 찾기·스크롤,
    /// 파일 열기·저장(줄 끝·인코딩), 임시 저장 되살리기, 편집 메뉴·위치 표시, 실제 Oracle 실행(Ctrl+Enter·선택 영역·F5·F4 — FMON_UI_ORACLE이 있을 때).
    /// 키는 사람이 누르는 것처럼 편집기 입력 칸에 낸다(SQL 탭·DB Helper 창 처리기를 그대로 지난다).
    /// </summary>
    public partial class SqlEditorHeadlessTests
    {
        private static T Get<T>(object owner, string member)
        {
            var type = owner.GetType();
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var field = type.GetField(member, flags);
            if (field != null)
                return (T)field.GetValue(owner);
            return (T)type.GetProperty(member, flags).GetValue(owner);
        }

        private static object Call(object owner, string method, params object[] args)
        {
            return owner.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Invoke(owner, args);
        }

        private static UIElement Input(FrameworkElement editor)
        {
            dynamic ed = editor;
            return (UIElement)ed.TextArea;
        }

        private static string EditorText(FrameworkElement editor)
        {
            dynamic ed = editor;
            return (string)ed.Document.Text;
        }

        // ---------- 탭별 글·캐럿·선택·스크롤·되돌리기 ----------

        [UiFact]
        public async Task R1_Tabs_KeepTextCaretSelectionScrollUndo()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var (main, first) = await OpenSqlTab(ui, plugin);
            try
            {
                var big = SqlSamples.D3(500);
                ui.Run(() =>
                {
                    SetEditorText(first, big);
                    dynamic ed = first;
                    ed.TextArea.Caret.Offset = big.Length;
                    TypeText(Input(first), "x");
                    ed.ScrollToVerticalOffset(1500.0);
                    ed.Select(0, 6);
                });
                await Idle(ui);
                var before = ui.Run(() => { dynamic ed = first; return ((double)ed.VerticalOffset, (int)ed.SelectionStart, (int)ed.SelectionLength); });
                Assert.True(before.Item1 > 1000);

                // 새 탭(Ctrl+N — DB Helper 창 단축키)에 다른 글
                ui.Run(() => Press(Input(first), Key.N, ModifierKeys.Control));
                var second = await ui.WaitFor(() => { var e = ActiveEditor(main); return e != null && e != first ? e : null; }, Wait, "새 탭 편집기");
                ui.Run(() =>
                {
                    SetEditorText(second, "SELECT 2 FROM t2;\r\n");
                    dynamic ed = second;
                    ed.TextArea.Caret.Offset = 19;
                    TypeText(Input(second), "y");
                });

                // 첫 탭으로(탭 머리 누름) — 글·선택·스크롤 그대로, 되돌리기는 그 탭 것
                ui.Run(() => Ui.Click(Ui.Find<ButtonBase>(main, b => AutomationProperties.GetName(b) == "SQL 1")));
                await ui.WaitUntil(() => ActiveEditor(main) == first, Wait, "첫 탭으로");
                await Idle(ui);
                var after = ui.Run(() => { dynamic ed = first; return ((double)ed.VerticalOffset, (int)ed.SelectionStart, (int)ed.SelectionLength); });
                Assert.Equal(big + "x", ui.Run(() => EditorText(first)));
                Assert.Equal((before.Item2, before.Item3), (after.Item2, after.Item3));
                Assert.Equal(before.Item1, after.Item1, 1);
                Assert.Contains("선택 6자", ui.Run(() => CursorText(main)));
                ui.Run(() => Press(Input(first), Key.Z, ModifierKeys.Control));
                Assert.Equal(big, ui.Run(() => EditorText(first)));
                Assert.Equal("SELECT 2 FROM t2;\r\ny", ui.Run(() => EditorText(second)));
                Report(plugin, "R 탭 전환", "두 탭 사이를 오가도 글·선택·세로 스크롤·되돌리기 기록이 탭마다 그대로", "✓");
            }
            finally
            {
                ui.Run(() => main.Close());
            }
            Assert.Empty(ui.TakeErrors());
        }

        // ---------- 주석 전환·대소문자·맨 위 넣기는 되돌리기 한 번 ----------

        [UiFact]
        public async Task R2_CommentCaseInsert_UndoOnce()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var (main, editor) = await OpenSqlTab(ui, plugin);
            const string text = "SELECT a FROM t;\r\nSELECT b FROM u;\r\n";
            try
            {
                ui.Run(() =>
                {
                    SetEditorText(editor, text);
                    dynamic ed = editor;
                    ed.Document.UndoStack.ClearAll();
                    ed.Select(0, 16);
                    Press(Input(editor), Key.Oem2, ModifierKeys.Control);
                });
                Assert.StartsWith("-- SELECT a FROM t;", ui.Run(() => EditorText(editor)));
                ui.Run(() => Press(Input(editor), Key.Z, ModifierKeys.Control));
                Assert.Equal(text, ui.Run(() => EditorText(editor)));

                ui.Run(() =>
                {
                    dynamic ed = editor;
                    ed.Select(0, 6);
                    Press(Input(editor), Key.L, ModifierKeys.Control | ModifierKeys.Shift);
                });
                Assert.StartsWith("select a FROM t;", ui.Run(() => EditorText(editor)));
                Assert.Equal((0, 6), ui.Run(() => { dynamic ed = editor; return ((int)ed.SelectionStart, (int)ed.SelectionLength); }));
                ui.Run(() => Press(Input(editor), Key.Z, ModifierKeys.Control));
                Assert.Equal(text, ui.Run(() => EditorText(editor)));

                // 트리·기록에서 넣기(맨 위에 넣고 캐럿을 처음으로 — 같은 메서드를 부른다)
                ui.Run(() =>
                {
                    var workspace = Workspace(main);
                    Call(workspace, "InsertAtTop", Get<object>(workspace, "_active"), "SELECT * FROM x;\n");
                });
                Assert.Equal("SELECT * FROM x;\r\n" + text, ui.Run(() => EditorText(editor)));
                Assert.Equal(0, ui.Run(() => { dynamic ed = editor; return (int)ed.TextArea.Caret.Offset; }));
                ui.Run(() => Press(Input(editor), Key.Z, ModifierKeys.Control));
                Assert.Equal(text, ui.Run(() => EditorText(editor)));
                Report(plugin, "R 되돌리기 한 번", "주석 전환(Ctrl+/)·소문자로(Ctrl+Shift+L)·맨 위 넣기가 각각 Ctrl+Z 한 번으로 원래대로", "✓");
            }
            finally
            {
                ui.Run(() => main.Close());
            }
            Assert.Empty(ui.TakeErrors());
        }

        // ---------- 찾기: 고르고 세로·가로로 보이게 ----------

        [UiFact]
        public async Task R3_Find_SelectsAndScrolls()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var (main, editor) = await OpenSqlTab(ui, plugin);
            var text = SqlSamples.D3(3000) + "SELECT " + new string(' ', 400) + "'ZZFIND_ME' FROM dual;\r\n" + SqlSamples.D3(100);
            try
            {
                ui.Run(() =>
                {
                    SetEditorText(editor, text);
                    dynamic ed = editor;
                    ed.TextArea.Caret.Offset = 0;
                    ed.ScrollToHome();
                });
                await Idle(ui);
                ui.Run(() => Press(Input(editor), Key.F, ModifierKeys.Control));
                await ui.WaitUntil(() => FindBox(main) != null && FindBox(main).IsVisible, Wait, "찾기 막대");
                ui.Run(() => FindBox(main).Text = "zzfind_me");
                await Idle(ui);
                var found = ui.Run(() => { dynamic ed = editor; return ((int)ed.SelectionStart, (string)ed.SelectedText, (double)ed.VerticalOffset, (double)ed.HorizontalOffset); });
                Assert.Equal(text.IndexOf("ZZFIND_ME", StringComparison.Ordinal), found.Item1);
                Assert.Equal("ZZFIND_ME", found.Item2);
                Assert.True(found.Item3 > 10000, "세로로 스크롤되지 않았습니다: " + found.Item3);
                Assert.True(found.Item4 > 100, "가로로 스크롤되지 않았습니다: " + found.Item4);

                // 여러 개: Enter·F3 다음, Shift+Enter·Shift+F3 이전
                ui.Run(() => FindBox(main).Text = "orders");
                await Idle(ui);
                var firstHit = ui.Run(() => { dynamic ed = editor; return (int)ed.SelectionStart; });
                ui.Run(() => Press(FindBox(main), Key.Enter, ModifierKeys.None));
                var next = ui.Run(() => { dynamic ed = editor; return (int)ed.SelectionStart; });
                Assert.True(next > firstHit);
                ui.Run(() => Press(FindBox(main), Key.Enter, ModifierKeys.Shift));
                Assert.Equal(firstHit, ui.Run(() => { dynamic ed = editor; return (int)ed.SelectionStart; }));
                ui.Run(() => Press(FindBox(main), Key.Escape, ModifierKeys.None));
                await Idle(ui);
                Assert.False(ui.Run(() => FindBox(main).IsVisible));
                ui.Run(() => Press(Input(editor), Key.F3, ModifierKeys.None));
                Assert.Equal(next, ui.Run(() => { dynamic ed = editor; return (int)ed.SelectionStart; }));
                ui.Run(() => Press(Input(editor), Key.F3, ModifierKeys.Shift));
                Assert.Equal(firstHit, ui.Run(() => { dynamic ed = editor; return (int)ed.SelectionStart; }));
                _out.WriteLine("캡처: " + ui.Run(() => Ui.Snap(main, plugin.ShotFolder, "sqltab-find")));
                Report(plugin, "R 찾기", "Ctrl+F·입력하면 찾은 글자를 고르고 세로(" + found.Item3.ToString("0") + "px)·가로(" + found.Item4.ToString("0")
                    + "px)로 보이게, Enter·F3 다음, Shift+Enter·Shift+F3 이전, Esc 닫기", "✓");
            }
            finally
            {
                ui.Run(() => main.Close());
            }
            Assert.Empty(ui.TakeErrors());
        }

        // ---------- 파일 열기·고치기·저장(줄 끝·인코딩 그대로) ----------

        [UiFact]
        public async Task R4_FileOpenEditSave_KeepsNewlineAndEncoding()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var folder = Path.Combine(plugin.ShotFolder, "files");
            Directory.CreateDirectory(folder);
            var lf = Path.Combine(folder, "lf.sql");
            var crlf = Path.Combine(folder, "crlf-bom.sql");
            File.WriteAllBytes(lf, new UTF8Encoding(false).GetBytes("SELECT 1 FROM dual;\nSELECT '한글' FROM dual;\n"));
            File.WriteAllBytes(crlf, new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes("SELECT 1 FROM dual;\r\nSELECT '한글' FROM dual;\r\n")).ToArray());
            var (main, editor) = await OpenSqlTab(ui, plugin);
            try
            {
                var results = new List<string>();
                foreach (var (path, typed, bom, newline) in new[] { (lf, "x", false, "\n"), (crlf, "y", true, "\r\n") })
                {
                    var name = Path.GetFileName(path);
                    // 탐색기에서 끌어다 놓은 것과 같은 경로
                    ui.Run(() => Call(Workspace(main), "OpenPaths", (IEnumerable<string>)new[] { path }));
                    await ui.WaitUntil(() => Ui.Find<ButtonBase>(main, b => AutomationProperties.GetName(b) == name) != null, Wait, name + " 탭");
                    var opened = await ui.WaitFor(() => ActiveEditor(main), Wait, name + " 편집기");
                    Assert.Equal("SELECT 1 FROM dual;\r\nSELECT '한글' FROM dual;\r\n", ui.Run(() => EditorText(opened)));
                    ui.Run(() =>
                    {
                        dynamic ed = opened;
                        ed.TextArea.Caret.Offset = (int)ed.Document.TextLength;
                        TypeText(Input(opened), typed);
                    });
                    await Idle(ui);
                    Assert.Contains(name + "*", ui.Run(() => Ui.Texts(main)));
                    ui.Run(() => Press(Input(opened), Key.S, ModifierKeys.Control));
                    await Idle(ui);
                    var bytes = File.ReadAllBytes(path);
                    var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                    var saved = Encoding.UTF8.GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
                    Assert.Equal(bom, hasBom);
                    Assert.Equal(("SELECT 1 FROM dual;" + newline + "SELECT '한글' FROM dual;" + newline + typed), saved);
                    Assert.DoesNotContain(name + "*", ui.Run(() => Ui.Texts(main)));
                    results.Add(name + "(" + (bom ? "BOM" : "BOM 없음") + ", " + (newline == "\n" ? "LF" : "CRLF") + ")");
                }
                Report(plugin, "R 파일 열기·저장", string.Join(" · ", results) + " — 열면 편집기는 CRLF, 고치면 탭에 *, Ctrl+S로 저장하면 원래 줄 끝·BOM 그대로, * 사라짐", "✓");
            }
            finally
            {
                ui.Run(() => main.Close());
            }
            Assert.Empty(ui.TakeErrors());
        }

        // ---------- 임시 저장 되살리기 ----------

        [UiFact]
        public async Task R5_Drafts_RestoredOnReopen()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            const string text = "SELECT 'draft' FROM dual;\r\n-- 되살리기 확인\r\n";
            var (main, editor) = await OpenSqlTab(ui, plugin);
            ui.Run(() =>
            {
                SetEditorText(editor, text);
                dynamic ed = editor;
                ed.TextArea.Caret.Offset = 8;
            });
            await Idle(ui);
            // 창을 닫으면(Unloaded) 마지막 내용을 임시 저장한다 — 다음에 여는 창이 넘겨받는다
            ui.Run(() => main.Close());
            await Idle(ui);
            var (again, restored) = await OpenSqlTab(ui, plugin, true);
            try
            {
                Assert.Equal(text, ui.Run(() => EditorText(restored)));
                Assert.Equal(8, ui.Run(() => { dynamic ed = restored; return (int)ed.TextArea.Caret.Offset; }));
                Report(plugin, "R 임시 저장 되살리기", "창을 닫고 다시 열면 글과 캐럿 위치가 되살아남", "✓");
            }
            finally
            {
                ui.Run(() => again.Close());
                ClearDrafts(plugin);
            }
            Assert.Empty(ui.TakeErrors());
        }

        // ---------- 편집 메뉴 대상·위치 표시 ----------

        [UiFact]
        public async Task R6_EditMenu_CursorInfo()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var (main, editor) = await OpenSqlTab(ui, plugin);
            const string text = "SELECT a\r\nFROM t;";
            try
            {
                ui.Run(() =>
                {
                    SetEditorText(editor, text);
                    dynamic ed = editor;
                    ed.Document.UndoStack.ClearAll();
                    ed.TextArea.Caret.Offset = 12;
                });
                await Idle(ui);
                Assert.Equal("줄 2, 열 3", ui.Run(() => CursorText(main)));
                ui.Run(() => Ui.Menu(main, "편집", "모두 선택"));
                await Idle(ui);
                Assert.Equal(text.Length, ui.Run(() => { dynamic ed = editor; return (int)ed.SelectionLength; }));
                Assert.EndsWith("선택 16자", ui.Run(() => CursorText(main)));
                ui.Run(() => TypeText(Input(editor), "z"));
                Assert.Equal("z", ui.Run(() => EditorText(editor)));
                ui.Run(() => Ui.Menu(main, "편집", "실행 취소"));
                await Idle(ui);
                Assert.Equal(text, ui.Run(() => EditorText(editor)));
                Report(plugin, "R 편집 메뉴·위치 표시", "편집 › 모두 선택·실행 취소가 SQL 탭 편집기에 듣고, 편집기 바에 줄·열·선택 글자 수(CRLF는 한 글자)", "✓");
            }
            finally
            {
                ui.Run(() => main.Close());
            }
            Assert.Empty(ui.TakeErrors());
        }

        // ---------- 실제 Oracle: Ctrl+Enter·선택 영역·F5·F4 ----------

        [UiFact(requiresOracle: true)]
        public async Task R7_Run_CaretSelectionScriptDescribe_Oracle()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var connection = SeedConnection.Oracle("XE 시험", HeadlessEnvironment.Oracle, HeadlessEnvironment.OracleUser, HeadlessEnvironment.OraclePassword, "green");
            var (main, editor) = await OpenSqlTab(ui, plugin, connection);
            const string text = "SELECT 1 AS aaa_first FROM DUAL;\r\n\r\nSELECT 2 AS bbb_second FROM DUAL;\r\n";
            Window info = null;
            try
            {
                // 실행은 연결된 대상에서만(연결 안 됐으면 상태줄 [연결] 안내) — 사람처럼 먼저 [연결]
                ui.Run(() => Ui.Click(Ui.Button(main, "연결")));
                await ui.WaitUntil(() => Ui.Texts(main).Any(t => t.Contains("연결 1개")), Wait, "XE 연결");
                ui.Run(() =>
                {
                    SetEditorText(editor, text);
                    dynamic ed = editor;
                    ed.TextArea.Caret.Offset = text.IndexOf("bbb_second", StringComparison.Ordinal);
                    Press(Input(editor), Key.Enter, ModifierKeys.Control);
                });
                try
                {
                    await ui.WaitUntil(() => ResultHeaders(main).Any(h => h.Contains("BBB_SECOND")), TimeSpan.FromSeconds(40), "캐럿 문장 결과");
                }
                catch (TimeoutException)
                {
                    _out.WriteLine("화면 글: " + string.Join(" ¦ ", ui.Run(() => Ui.Texts(main))));
                    _out.WriteLine("결과 머리: " + string.Join(" ¦ ", ui.Run(() => ResultHeaders(main))));
                    _out.WriteLine("캡처: " + ui.Run(() => Ui.Snap(main, plugin.ShotFolder, "sqltab-run-timeout")));
                    throw;
                }
                ui.Run(() =>
                {
                    dynamic ed = editor;
                    ed.Select(0, text.IndexOf(';') + 1);
                    Press(Input(editor), Key.Enter, ModifierKeys.Control);
                });
                await ui.WaitUntil(() => ResultHeaders(main).Any(h => h.Contains("AAA_FIRST")), Wait, "선택 영역 결과");
                ui.Run(() =>
                {
                    dynamic ed = editor;
                    ed.Select(0, 0);
                    Press(Input(editor), Key.F5, ModifierKeys.None);
                });
                await ui.WaitUntil(() => Get<System.Collections.ICollection>(Get<object>(Workspace(main), "_active"), "Results").Count == 2 && !Get<bool>(Get<object>(Workspace(main), "_active"), "Running"),
                    Wait, "스크립트 결과 2개");
                ui.Run(() =>
                {
                    dynamic ed = editor;
                    ed.TextArea.Caret.Offset = text.IndexOf("DUAL", StringComparison.Ordinal) + 1;
                    Press(Input(editor), Key.F4, ModifierKeys.None);
                });
                info = await ui.WaitFor(() => Ui.Window("테이블 정보 — "), Wait, "테이블 정보 창");
                var title = ui.Run(() => info.Title);
                Assert.Contains("DUAL", title);
                _out.WriteLine("캡처: " + ui.Run(() => Ui.Snap(main, plugin.ShotFolder, "sqltab-run")));
                Report(plugin, "R 실행 (Oracle " + HeadlessEnvironment.Oracle + ")",
                    "Ctrl+Enter 캐럿 문장(BBB_SECOND)·선택 영역(AAA_FIRST)·F5 스크립트(결과 2개)·F4 테이블 정보(" + title + ")", "✓");
            }
            finally
            {
                ui.Run(() =>
                {
                    if (info != null)
                        info.Close();
                    main.Close();
                });
            }
            Assert.Empty(ui.TakeErrors());
        }

        /// <summary>보이는 결과 표(ListView·GridView)의 열 머리 글.</summary>
        private static List<string> ResultHeaders(Window main)
        {
            return Ui.FindAll<ListView>(main, l => l.IsVisible && l.View is GridView)
                .SelectMany(l => ((GridView)l.View).Columns.Select(c => Ui.ContentText(c.Header) ?? ""))
                .ToList();
        }
    }
}

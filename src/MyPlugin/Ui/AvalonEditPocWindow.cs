using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace MyPlugin
{
    /// <summary>
    /// [시험판 전용] AvalonEdit 편집기 PoC 창(docs/sql-editor-avalonedit-plan.md 1단계 실측용). 본 SQL 탭은 바꾸지 않는다.
    /// 도구 > AvalonEdit PoC로 지금 SQL 탭의 글·대상 DB 규칙을 가져와 연다. 주석·문자열은 SqlScript.HighlightSpans로 칠한다.
    /// 화면 아래에 실측값(넣기→첫 화면, 글 꺼내기·구간 계산 시간, 키가 편집기에서 처리됐는지)을 보이고 [결과 복사]로 표 한 줄씩 복사한다.
    /// </summary>
    internal static class AvalonEditPocWindow
    {
        public static Window Show(Window owner, string text, SqlLexRules rules, Action<string> copy)
        {
            var window = new Window
            {
                Title = "AvalonEdit PoC (시험판 — 실측용)",
                Width = 1000,
                Height = 720,
                MinWidth = 600,
                MinHeight = 400,
                ShowInTaskbar = false,
                ResizeMode = ResizeMode.CanResizeWithGrip
            };
            Theme.ApplyWindow(window);
            DialogKit.SetOwner(window, owner);
            var poc = new Poc(window, rules ?? SqlLexRules.Oracle, copy);
            window.Content = poc.Root;
            window.Loaded += (s, e) => poc.Load(string.IsNullOrEmpty(text) ? PocData.D1 : text, "지금 SQL 탭");
            window.Show();
            return window;
        }

        private sealed class Poc
        {
            private readonly Window _window;
            private readonly Action<string> _copy;
            private readonly TextEditor _editor;
            private readonly SpanColorizer _colorizer = new SpanColorizer();
            private readonly ComboBox _rules;
            private readonly TextBlock _measure;
            private readonly TextBlock _keys;
            private readonly List<string> _keyLog = new List<string>();
            private readonly List<double> _computeTimes = new List<double>();
            private readonly List<string> _results = new List<string>();
            private string _dataName = "";
            private string _loadLine = "";

            public Poc(Window window, SqlLexRules rules, Action<string> copy)
            {
                _window = window;
                _copy = copy;
                Root = new DockPanel { Margin = new Thickness(10) };

                var note = Theme.Text("시험판 전용 창입니다. 본 SQL 탭과 따로 동작하고 저장하지 않습니다. 계획서 4절 A~E를 확인하고 [결과 복사]로 7절 기록란에 붙이세요.", Theme.SecondaryText);
                note.TextWrapping = TextWrapping.Wrap;
                note.Margin = new Thickness(0, 0, 0, 6);
                DockPanel.SetDock(note, Dock.Top);
                Root.Children.Add(note);

                var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
                _rules = new ComboBox { Width = 120, Margin = new Thickness(0, 0, 8, 0), ToolTip = "구문 규칙(문장 나누기와 같은 규칙)" };
                foreach (var r in new[] { SqlLexRules.Oracle, SqlLexRules.Postgres, SqlLexRules.SqlServer })
                    _rules.Items.Add(new ComboBoxItem { Content = r.Name, Tag = r });
                _rules.SelectedIndex = rules == SqlLexRules.Postgres ? 1 : rules == SqlLexRules.SqlServer ? 2 : 0;
                _rules.SelectionChanged += (s, e) => Recompute("규칙 바꿈");
                bar.Children.Add(_rules);
                bar.Children.Add(Button("D1 경계 사례", () => Load(PocData.D1, "D1")));
                bar.Children.Add(Button("D3-1 1만 줄", () => Load(PocData.D3(10000), "D3-1 1만 줄")));
                bar.Children.Add(Button("D3-2 5만 줄", () => Load(PocData.D3(50000), "D3-2 5만 줄")));
                bar.Children.Add(Button("D3-3 10만 줄", () => Load(PocData.D3(100000), "D3-3 10만 줄")));
                bar.Children.Add(Button("D4-2 맨 앞 /* 넣기·빼기", ToggleLeadingComment));
                bar.Children.Add(Button("결과 복사", CopyResults));
                DockPanel.SetDock(bar, Dock.Top);
                Root.Children.Add(bar);

                var status = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
                _measure = Theme.Text("", Theme.PrimaryText);
                _measure.TextWrapping = TextWrapping.Wrap;
                _keys = Theme.Text("키: (아직 없음)", Theme.SecondaryText);
                _keys.TextWrapping = TextWrapping.Wrap;
                _keys.Margin = new Thickness(0, 4, 0, 0);
                status.Children.Add(_measure);
                status.Children.Add(_keys);
                DockPanel.SetDock(status, Dock.Bottom);
                Root.Children.Add(status);

                _editor = new TextEditor
                {
                    ShowLineNumbers = true,
                    FontFamily = Theme.Mono,
                    FontSize = 13,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    BorderThickness = new Thickness(1)
                };
                _editor.Options.EnableHyperlinks = false;
                _editor.Options.EnableEmailHyperlinks = false;
                _editor.SetResourceReference(Control.BackgroundProperty, Theme.PanelBackground);
                _editor.SetResourceReference(Control.ForegroundProperty, Theme.PrimaryText);
                _editor.SetResourceReference(Control.BorderBrushProperty, Theme.Border);
                _editor.SetResourceReference(TextEditor.LineNumbersForegroundProperty, Theme.SecondaryText);
                _editor.TextArea.SetResourceReference(ICSharpCode.AvalonEdit.Editing.TextArea.SelectionBrushProperty, Theme.Selection);
                // 선택 영역 글자는 원래 색 그대로(기본값은 흰 글자라 밝은 테마에서 안 보임)
                _editor.TextArea.SelectionForeground = null;
                _editor.TextArea.SelectionBorder = null;
                _editor.TextArea.TextView.LineTransformers.Add(_colorizer);
                _editor.Document.TextChanged += (s, e) => Recompute(null);
                Root.Children.Add(_editor);

                // 키 흐름: 창의 KeyDown(버블)에 처리된 이벤트까지 받아, 편집기가 먹었는지(Handled) 기록한다
                window.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKeyDown), true);
            }

            public DockPanel Root { get; }

            private SqlLexRules Rules
            {
                get { return (SqlLexRules)((ComboBoxItem)_rules.SelectedItem).Tag; }
            }

            private static Button Button(string text, Action click)
            {
                var button = new Button { Content = text, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 6, 0), Focusable = false };
                button.Click += (s, e) => click();
                return button;
            }

            public void Load(string text, string name)
            {
                _dataName = name;
                _computeTimes.Clear();
                var watch = Stopwatch.StartNew();
                _editor.Document.Text = text;
                var assigned = watch.Elapsed.TotalMilliseconds;
                _editor.ScrollToHome();
                // 넣은 뒤 화면을 다 그리고 한가해질 때까지(첫 화면)
                _window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                {
                    var shown = watch.Elapsed.TotalMilliseconds;
                    _loadLine = name + ": 줄 " + _editor.Document.LineCount.ToString("N0", CultureInfo.InvariantCulture)
                        + " · 글 " + Mb(text.Length) + " · 넣기 " + Ms(assigned) + " · 첫 화면까지 " + Ms(shown);
                    _results.Add(Row("넣기→첫 화면 (" + name + ")", _loadLine));
                    ShowMeasure();
                    _editor.Focus();
                }));
            }

            private void ToggleLeadingComment()
            {
                var doc = _editor.Document;
                if (doc.TextLength >= 2 && doc.GetText(0, 2) == "/*")
                    doc.Remove(0, 2);
                else
                    doc.Insert(0, "/*");
            }

            private void Recompute(string reason)
            {
                var watch = Stopwatch.StartNew();
                var text = _editor.Document.Text;
                var taken = watch.Elapsed.TotalMilliseconds;
                var spans = SqlScript.HighlightSpans(text, Rules);
                var computed = watch.Elapsed.TotalMilliseconds - taken;
                _colorizer.Spans = spans;
                _colorizer.Comment = Brush(Theme.SecondaryText) ?? Brushes.Gray;
                _colorizer.String = Theme.Warning;
                _editor.TextArea.TextView.Redraw();
                _computeTimes.Add(taken + computed);
                _lastCompute = "구간 계산: 글 꺼내기 " + Ms(taken) + " + 계산 " + Ms(computed) + " = " + Ms(taken + computed)
                    + " (구간 " + spans.Count.ToString("N0", CultureInfo.InvariantCulture) + "개, 규칙 " + Rules.Name + (reason != null ? ", " + reason : "") + ")"
                    + (_computeTimes.Count > 1 ? " · 이번 글에서 " + _computeTimes.Count + "회 중앙값 " + Ms(Median(_computeTimes)) + " · 최대 " + Ms(_computeTimes.Max()) : "");
                ShowMeasure();
            }

            private string _lastCompute = "";

            private Brush Brush(string key)
            {
                return _editor.TryFindResource(key) as Brush;
            }

            private void ShowMeasure()
            {
                _measure.Text = _loadLine + "\n" + _lastCompute + "\n16ms를 넘으면 키 입력마다 전체 다시 계산은 무리(4단계에서 지연·증분 계산).";
            }

            private void OnKeyDown(object sender, KeyEventArgs e)
            {
                var key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key == Key.System ? e.SystemKey : e.Key;
                if (key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftShift || key == Key.RightShift || key == Key.LeftAlt || key == Key.RightAlt)
                    return;
                var gesture = (Keyboard.Modifiers == ModifierKeys.None ? "" : Keyboard.Modifiers.ToString().Replace(", ", "+") + "+") + key
                    + (e.Key == Key.ImeProcessed ? "(IME)" : "");
                _keyLog.Insert(0, gesture + (e.Handled ? " → 편집기가 처리" : " → 통과(SQL 탭이면 단축키로 쓸 수 있음)"));
                if (_keyLog.Count > 8)
                    _keyLog.RemoveAt(_keyLog.Count - 1);
                _keys.Text = "키(최근 8개): " + string.Join("  ·  ", _keyLog);
            }

            private void CopyResults()
            {
                var lines = new List<string>(_results);
                if (_computeTimes.Count > 0)
                    lines.Add(Row("키 입력 강조 계산 (" + _dataName + ")", "중앙값 " + Ms(Median(_computeTimes)) + " · 최대 " + Ms(_computeTimes.Max()) + " · " + _computeTimes.Count + "회"));
                lines.AddRange(_keyLog.Select(k => Row("키", k)));
                _copy(string.Join(Environment.NewLine, lines));
            }

            // 계획서 7절 표 한 줄: | 날짜 | 측정자 | 환경 | 항목 | 결과 | 판단 |
            private static string Row(string item, string result)
            {
                return "| " + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " |  | Windows · AvalonEdit 6.3.0.90 PoC | " + item + " | " + result + " |  |";
            }

            private static double Median(List<double> values)
            {
                var sorted = values.OrderBy(v => v).ToList();
                return sorted[sorted.Count / 2];
            }

            private static string Ms(double value)
            {
                return value.ToString("F1", CultureInfo.InvariantCulture) + "ms";
            }

            private static string Mb(int chars)
            {
                return (chars * 2 / 1024.0 / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + "MB";
            }
        }

        /// <summary>구간 목록으로 줄마다 색을 칠한다(구간은 앞에서 뒤로 정렬 — 줄 시작 위치를 이분 탐색).</summary>
        private sealed class SpanColorizer : DocumentColorizingTransformer
        {
            public List<SqlSpan> Spans = new List<SqlSpan>();
            public Brush Comment = Brushes.Gray;
            public Brush String = Brushes.Orange;

            protected override void ColorizeLine(DocumentLine line)
            {
                var spans = Spans;
                if (spans.Count == 0)
                    return;
                int lo = 0, hi = spans.Count;
                while (lo < hi)
                {
                    var mid = (lo + hi) / 2;
                    if (spans[mid].End <= line.Offset)
                        lo = mid + 1;
                    else
                        hi = mid;
                }
                for (var i = lo; i < spans.Count && spans[i].Start < line.EndOffset; i++)
                {
                    var start = Math.Max(spans[i].Start, line.Offset);
                    var end = Math.Min(spans[i].End, line.EndOffset);
                    if (end <= start)
                        continue;
                    var brush = spans[i].Kind == SqlSpanKind.Comment ? Comment : String;
                    ChangeLinePart(start, end, element => element.TextRunProperties.SetForegroundBrush(brush));
                }
            }
        }

        /// <summary>계획서 5절 시험 데이터: D1(경계 사례), D3(크기별 생성 — 구성 비율 고정).</summary>
        private static class PocData
        {
            public const string D1 = "-- 줄 주석 한글\n"
                + "SELECT 1 FROM DUAL; -- 끝 주석\n"
                + "/* 블록\n"
                + "   여러 줄 -- 안의 줄 주석 기호는 주석의 일부 */\n"
                + "SELECT /*+ INDEX(t IX_T) */ * FROM T t;          -- 힌트(블록 주석으로 칠함)\n"
                + "SELECT 'a -- 문자열 안' , 'it''s', '/* 아님 */' FROM DUAL;\n"
                + "SELECT q'[ -- 아님 ' 아님 ]', q'{ } }', q'( ) )', q'< >', q'! ! !', nq'[ 한글 ]', N'유니코드' FROM DUAL;\n"
                + "SELECT \"Col -- 아님\", \"a\"\"b\" FROM DUAL;\n"
                + "SELECT 'line1\n"
                + "line2 -- 여러 줄 문자열 안' FROM DUAL;\n"
                + "SELECT 1 - -1 FROM DUAL;                           -- 빼기·음수(주석 아님)\n"
                + "SELECT 10/2 FROM DUAL;                            -- 나눗셈(주석 아님)\n"
                + "SELECT '😀 이모지' FROM DUAL; -- 서로게이트 쌍: 오프셋 어긋남 확인\n"
                + "BEGIN\n"
                + "  NULL; -- PL/SQL 안 주석\n"
                + "END;\n"
                + "/\n"
                + "-- PostgreSQL 규칙으로 바꿔 보기\n"
                + "DO $$ BEGIN RAISE NOTICE 'x; -- y'; END $$;\n"
                + "SELECT E'it\\'s' FROM t; /* 바깥 /* 안쪽 */ 주석 */\n"
                + "-- SQL Server 규칙으로 바꿔 보기\n"
                + "SELECT [col -- x] FROM [t];\n"
                + "GO\n"
                + "SELECT '닫히지 않은 문자열 → 끝까지 문자열\n";

            // 줄 주석 20%, 블록 주석 10%(3줄), 문자열 포함 30%(q'[…]' 포함), 일반 40%, 한글은 주석·문자열에 절반
            private static readonly string[] Unit =
            {
                "-- 주문 집계 주석 한글", "-- plain comment line",
                "/* 블록 주석 시작", "   두 번째 줄", "   세 번째 줄 */",
                "SELECT 'abc', '한글 문자열' FROM orders WHERE id = 1;", "SELECT q'[it's -- not]' FROM dual;", "UPDATE t SET name = 'x' WHERE id = 2;",
                "SELECT a, b, c FROM t1 JOIN t2 ON t1.id = t2.id;", "INSERT INTO t (a, b) VALUES (1, 2);", "SELECT COUNT(*) FROM orders;", "DELETE FROM t WHERE id = 3;"
            };

            public static string D3(int lines)
            {
                var sb = new StringBuilder(lines * 40);
                for (var i = 0; i < lines; i++)
                    sb.Append(Unit[i % Unit.Length]).Append("\r\n");
                return sb.ToString();
            }
        }
    }
}

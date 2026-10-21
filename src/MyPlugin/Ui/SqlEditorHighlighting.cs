using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace MyPlugin
{
    /// <summary>강조 계산 한 번의 측정값(PoC 창 아래에 보인다).</summary>
    public sealed class SqlHighlightUpdate
    {
        public bool Incremental { get; set; }
        /// <summary>null이면 글 고침, 아니면 처음부터 다시 훑은 까닭(규칙 바꿈 등).</summary>
        public string Reason { get; set; }
        /// <summary>구간 계산 시간(전체 다시 계산이면 글 꺼내기 포함).</summary>
        public double Milliseconds { get; set; }
        /// <summary>전체 다시 계산의 글 꺼내기(Document.Text) 시간.</summary>
        public double TextMilliseconds { get; set; }
        public int Scanned { get; set; }
        /// <summary>다시 칠하게 한 범위(글자 수). 0이면 다시 칠하지 않음.</summary>
        public int Range { get; set; }
        public int Spans { get; set; }
    }

    /// <summary>
    /// SQL 편집기(AvalonEdit <see cref="TextEditor"/>)에 구문 강조를 붙인다 — 주석·문자열·키워드, 탭 대상 DB 규칙(<see cref="SqlLexRules"/>).
    /// 4단계 SqlEditor가 그대로 쓴다(지금은 PoC 창).
    /// - 글이 바뀌면 <see cref="SqlHighlighter"/>로 바뀐 곳 근처만 다시 훑고 그 범위만 다시 칠한다. <see cref="Incremental"/> = false는 비교용(글마다 전체 다시 계산·전체 다시 칠함).
    /// - 색은 칠할 때 테마에서 정하고(<see cref="SqlHighlightLogic.Palette"/>) 편집기 바탕이 바뀌면(테마 변경 — 바탕이 리소스 참조일 때) 전부 다시 칠한다.
    /// - 아주 긴 줄(<see cref="MaxColoredLine"/>자 넘음)은 칠하지 않는다: 한 줄에 구간 수만 개를 나누면 AvalonEdit 줄 그리기가 구간 수의 제곱으로 느려진다.
    /// </summary>
    public sealed class SqlEditorHighlighting
    {
        public const int MaxColoredLine = 20000;
        private static readonly DependencyPropertyDescriptor BackgroundProperty = DependencyPropertyDescriptor.FromProperty(Control.BackgroundProperty, typeof(TextEditor));
        private readonly TextEditor _editor;
        private readonly DocumentSource _source;
        private readonly SpanColorizer _colorizer;
        private SqlLexRules _rules;
        private bool _incremental = true;
        private SqlHighlighter _highlighter;
        private List<SqlSpan> _full = new List<SqlSpan>();

        private SqlEditorHighlighting(TextEditor editor, SqlLexRules rules)
        {
            _editor = editor;
            _rules = rules ?? SqlLexRules.Oracle;
            _source = new DocumentSource(editor.Document);
            _colorizer = new SpanColorizer(() => _incremental ? _highlighter.Spans : (IReadOnlyList<SqlSpan>)_full);
        }

        public static SqlEditorHighlighting Attach(TextEditor editor, SqlLexRules rules)
        {
            var highlighting = new SqlEditorHighlighting(editor, rules);
            editor.TextArea.TextView.LineTransformers.Add(highlighting._colorizer);
            editor.Document.Changed += highlighting.OnDocumentChanged;
            BackgroundProperty.AddValueChanged(editor, highlighting.OnThemeChanged);
            highlighting.Rehighlight("붙임");
            return highlighting;
        }

        public void Detach()
        {
            BackgroundProperty.RemoveValueChanged(_editor, OnThemeChanged);
            _editor.Document.Changed -= OnDocumentChanged;
            _editor.TextArea.TextView.LineTransformers.Remove(_colorizer);
            _editor.TextArea.TextView.Redraw();
        }

        /// <summary>문장 나누기·키워드 규칙(탭 대상 DB). 바꾸면 처음부터 다시 훑고 전부 다시 칠한다 — 같은 글이 DB마다 다르게 나뉜다.</summary>
        public SqlLexRules Rules
        {
            get { return _rules; }
            set
            {
                if (value == null || value == _rules)
                    return;
                _rules = value;
                Rehighlight("규칙 바꿈");
            }
        }

        /// <summary>true(기본): 바뀐 곳 근처만 다시 계산. false: 비교용 — 글이 바뀔 때마다 전체 다시 계산·전체 다시 칠함.</summary>
        public bool Incremental
        {
            get { return _incremental; }
            set
            {
                if (value == _incremental)
                    return;
                _incremental = value;
                Rehighlight("방식 바꿈");
            }
        }

        public IReadOnlyList<SqlSpan> Spans
        {
            get { return _incremental ? _highlighter.Spans : _full; }
        }

        /// <summary>마지막 계산의 측정값.</summary>
        public SqlHighlightUpdate Last { get; private set; }

        /// <summary>계산할 때마다(글 고침·다시 훑기).</summary>
        public event EventHandler Updated;

        private void OnDocumentChanged(object sender, DocumentChangeEventArgs e)
        {
            var watch = Stopwatch.StartNew();
            var update = new SqlHighlightUpdate { Incremental = _incremental };
            if (_incremental)
            {
                // 지운 글을 넘기면 토큰 안을 고칠 때 둘레만 읽는다(통째로 바꾼 큰 글은 넘기지 않음 — 어차피 처음부터 훑는다)
                var removedText = e.RemovalLength > 0 && e.RemovalLength <= 4096 ? e.RemovedText.Text : null;
                var range = _highlighter.Update(_source, e.Offset, e.RemovalLength, e.InsertionLength, removedText);
                update.Milliseconds = watch.Elapsed.TotalMilliseconds;
                update.Scanned = _highlighter.LastScanned;
                if (range.End > range.Start && !InsideLongLine(range.Start, range.End))
                {
                    Redraw(range.Start, range.End);
                    update.Range = range.End - range.Start;
                }
                update.Spans = _highlighter.Spans.Count;
            }
            else
            {
                var text = _editor.Document.Text;
                update.TextMilliseconds = watch.Elapsed.TotalMilliseconds;
                _full = SqlScript.EditorSpans(text, _rules);
                update.Milliseconds = watch.Elapsed.TotalMilliseconds;
                update.Scanned = text.Length;
                update.Range = text.Length;
                update.Spans = _full.Count;
                _editor.TextArea.TextView.Redraw();
            }
            Publish(update);
        }

        // TextView.Redraw(offset, length)는 범위에 닿기만 해도 그 줄을 다시 만든다(앞 줄 끝 = 이 줄 시작). 범위는 줄 시작에서 시작하고
        // 다음 구간 시작에서 끝나므로 한 글자씩 안쪽으로 줄여 앞뒤 줄을 건드리지 않는다 — 앞 줄이 1MB 한 줄이면 키마다 2초가 된다
        private void Redraw(int start, int end)
        {
            var from = Math.Min(start + 1, _editor.Document.TextLength);
            var to = Math.Max(from, end - 1);
            _editor.TextArea.TextView.Redraw(from, to - from);
        }

        // 다시 칠할 범위가 칠하지 않는 긴 줄 하나 안이면 다시 그릴 까닭이 없다(1MB 한 줄을 다시 그리면 초 단위)
        private bool InsideLongLine(int start, int end)
        {
            var line = _editor.Document.GetLineByOffset(start);
            return line.Length > MaxColoredLine && end <= line.EndOffset + line.DelimiterLength;
        }

        private void Rehighlight(string reason)
        {
            var watch = Stopwatch.StartNew();
            _highlighter = new SqlHighlighter(_rules);
            if (_incremental)
            {
                _highlighter.Reset(_source);
                _full = new List<SqlSpan>();
            }
            else
            {
                _full = SqlScript.EditorSpans(_editor.Document.Text, _rules);
            }
            _editor.TextArea.TextView.Redraw();
            Publish(new SqlHighlightUpdate
            {
                Incremental = _incremental,
                Reason = reason,
                Milliseconds = watch.Elapsed.TotalMilliseconds,
                Scanned = _editor.Document.TextLength,
                Range = _editor.Document.TextLength,
                Spans = Spans.Count
            });
        }

        private void Publish(SqlHighlightUpdate update)
        {
            Last = update;
            var handler = Updated;
            if (handler != null)
                handler(this, EventArgs.Empty);
        }

        private void OnThemeChanged(object sender, EventArgs e)
        {
            _colorizer.ForgetPalette();
            _editor.TextArea.TextView.Redraw();
        }

        /// <summary>편집기 문서를 강조 계산이 읽는 글로(조각·줄 위치만 읽고 문서 전체 문자열은 만들지 않는다).</summary>
        private sealed class DocumentSource : ISqlTextSource
        {
            private readonly TextDocument _document;

            public DocumentSource(TextDocument document)
            {
                _document = document;
            }

            public int Length { get { return _document.TextLength; } }

            public string GetText(int start, int length)
            {
                return _document.GetText(start, length);
            }

            public int LineStart(int offset)
            {
                return _document.GetLineByOffset(offset).Offset;
            }

            public int NextLineStart(int offset)
            {
                var line = _document.GetLineByOffset(offset);
                return line.EndOffset + line.DelimiterLength;
            }
        }

        /// <summary>
        /// 구간 목록(앞에서 뒤로)으로 줄마다 색을 칠한다 — 줄에 걸친 첫 구간을 이분 탐색. 색은 처음 칠할 때 테마에서 정해 두고
        /// 테마가 바뀌면(<see cref="ForgetPalette"/>) 다시 정한다 — 줄마다 리소스를 찾으면 스크롤이 느려진다.
        /// </summary>
        private sealed class SpanColorizer : DocumentColorizingTransformer
        {
            private readonly Func<IReadOnlyList<SqlSpan>> _spans;
            private Brush[] _brushes;

            public SpanColorizer(Func<IReadOnlyList<SqlSpan>> spans)
            {
                _spans = spans;
            }

            public void ForgetPalette()
            {
                _brushes = null;
            }

            protected override void ColorizeLine(DocumentLine line)
            {
                var spans = _spans();
                if (spans.Count == 0 || line.Length > MaxColoredLine)
                    return;
                var brushes = PaletteBrushes(CurrentContext.TextView);
                for (var i = SqlHighlighter.FirstEndingAfter(spans, line.Offset); i < spans.Count && spans[i].Start < line.EndOffset; i++)
                {
                    var kind = spans[i].Kind;
                    if (kind == SqlSpanKind.Identifier)
                        continue;
                    var start = Math.Max(spans[i].Start, line.Offset);
                    var end = Math.Min(spans[i].End, line.EndOffset);
                    if (end <= start)
                        continue;
                    var brush = brushes[(int)kind];
                    ChangeLinePart(start, end, element => element.TextRunProperties.SetForegroundBrush(brush));
                }
            }

            // 테마 색 네 가지로 정한 붓(종류 번호 순: 주석·문자열·키워드)
            private Brush[] PaletteBrushes(FrameworkElement view)
            {
                if (_brushes == null)
                {
                    var palette = SqlHighlightLogic.Palette(Rgb(view, Theme.PanelBackground, 0x000000), Rgb(view, Theme.PrimaryText, 0xFFFFFF),
                        Rgb(view, Theme.SecondaryText, 0x999999), Rgb(view, Theme.Accent, 0x4F8CFF));
                    _brushes = new[] { Frozen(palette.Comment), Frozen(palette.String), Frozen(palette.Keyword) };
                }
                return _brushes;
            }

            private static int Rgb(FrameworkElement view, string key, int fallback)
            {
                var brush = view.TryFindResource(key) as SolidColorBrush;
                return brush == null ? fallback : (brush.Color.R << 16) | (brush.Color.G << 8) | brush.Color.B;
            }

            private static Brush Frozen(int rgb)
            {
                var brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
                brush.Freeze();
                return brush;
            }
        }
    }
}

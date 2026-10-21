using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;

namespace MyPlugin
{
    /// <summary>
    /// SQL 탭 편집기(docs/sql-editor-avalonedit-plan.md 3단계): AvalonEdit <see cref="TextEditor"/>를 품는다 — 상속하지 않아 AvalonEdit API가 밖으로 새지 않는다.
    /// 밖에 내는 것은 의도 단위다: 글, 캐럿, 선택, 바꾸기(되돌리기 한 번), 보이게 스크롤, 글·선택이 바뀜, 편집 명령 대상.
    /// TextBox 함수를 이름만 같게 흉내 내지 않는다(좌표 기준이 달라 몇 px씩 어긋나는 버그가 된다).
    /// 줄 번호(AvalonEdit 내장), 구문 강조(<see cref="SqlEditorHighlighting"/> — 탭 대상 DB 규칙), 키 동작·줄 간격(<see cref="SqlEditorSetup"/>),
    /// 설정 "SQL 편집기" 탭의 값(<see cref="ApplySettings"/>)은 안에서 처리한다. 탭마다 하나씩 두어 글·캐럿·선택·스크롤·되돌리기 기록이 탭별로 유지된다.
    /// 테마가 바뀐 것은 바탕 리소스를 가리키는 자기 속성으로 안다(정적 감시를 걸지 않아 탭·창을 닫으면 그대로 놓인다).
    /// </summary>
    internal sealed class SqlEditor : Decorator
    {
        public const double FontSizeDefault = 13;

        private static readonly DependencyProperty ThemeBackgroundProperty = DependencyProperty.Register(
            "ThemeBackground", typeof(object), typeof(SqlEditor), new PropertyMetadata(null, (d, e) => ((SqlEditor)d).OnThemeChanged()));

        private readonly TextEditor _editor;
        private readonly SqlEditorHighlighting _highlighting;

        public SqlEditor(string text, SqlLexRules rules, SqlEditorSettings settings)
        {
            settings = settings ?? new SqlEditorSettings();
            _editor = new TextEditor
            {
                ShowLineNumbers = true,
                WordWrap = false,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 2, 4, 2)
            };
            _editor.SetResourceReference(Control.BackgroundProperty, Theme.PanelBackground);
            _editor.SetResourceReference(Control.ForegroundProperty, Theme.PrimaryText);
            // 줄 번호는 예전 직접 그린 칸처럼 흐린 글자
            _editor.SetResourceReference(TextEditor.LineNumbersForegroundProperty, Theme.DisabledText);
            _editor.TextArea.SetResourceReference(ICSharpCode.AvalonEdit.Editing.TextArea.SelectionBrushProperty, Theme.Selection);
            // 선택 영역 글자는 원래 색 그대로(기본값은 흰 글자라 밝은 테마에서 안 보임), 테두리 없음
            _editor.TextArea.SelectionForeground = null;
            _editor.TextArea.SelectionBorder = null;
            AutomationProperties.SetName(_editor, "SQL 편집기");
            AutomationProperties.SetName(_editor.TextArea, "SQL 편집기");
            SqlEditorSetup.ApplyBehavior(_editor);
            SqlEditorSetup.ApplyFont(_editor, Theme.Mono, FontSizeDefault, settings.LineHeight);
            _editor.Document.Text = text ?? "";
            _editor.Document.UndoStack.ClearAll();
            _highlighting = SqlEditorHighlighting.Attach(_editor, rules, settings, false);
            _editor.Document.TextChanged += (s, e) => Raise(TextChanged);
            _editor.TextArea.SelectionChanged += (s, e) => Raise(SelectionChanged);
            _editor.TextArea.Caret.PositionChanged += (s, e) => Raise(SelectionChanged);
            Child = _editor;
            SetResourceReference(ThemeBackgroundProperty, Theme.PanelBackground);
        }

        /// <summary>글이 바뀜(입력·바꾸기·통째로 넣기).</summary>
        public event EventHandler TextChanged;

        /// <summary>캐럿이 움직이거나 선택이 바뀜.</summary>
        public event EventHandler SelectionChanged;

        public string Text
        {
            get { return _editor.Document.Text; }
        }

        public int TextLength
        {
            get { return _editor.Document.TextLength; }
        }

        public int CaretOffset
        {
            get { return _editor.TextArea.Caret.Offset; }
            set { _editor.TextArea.Caret.Offset = Math.Max(0, Math.Min(value, TextLength)); }
        }

        public int SelectionStart
        {
            get { return _editor.SelectionStart; }
        }

        public int SelectionLength
        {
            get { return _editor.SelectionLength; }
        }

        public string SelectedText
        {
            get { return _editor.SelectedText; }
        }

        public bool IsReadOnly
        {
            get { return _editor.IsReadOnly; }
            set { _editor.IsReadOnly = value; }
        }

        /// <summary>편집 메뉴(되돌리기·잘라내기·복사·붙여넣기·모두 선택)의 명령 대상.</summary>
        public IInputElement CommandTarget
        {
            get { return _editor.TextArea; }
        }

        /// <summary>편집기 바의 위치 표시("줄 n, 열 m · 선택 k자") — 문서 전체를 훑지 않고 편집기가 아는 줄·열로.</summary>
        public string CursorInfo
        {
            get
            {
                var location = _editor.Document.GetLocation(CaretOffset);
                var selected = SelectionLength > 0 ? WorkspaceLogic.SelectedCharCount(SelectedText, 0, SelectionLength) : 0;
                return WorkspaceLogic.CursorInfo(location.Line, location.Column, selected);
            }
        }

        /// <summary>글을 통째로 바꾸고 되돌리기 기록을 비운다(파일 열기·임시 저장 복원 — 그 전 글로 되돌아가지 않게).</summary>
        public void Load(string text)
        {
            _editor.Document.Text = text ?? "";
            _editor.Document.UndoStack.ClearAll();
        }

        public void Select(int start, int length)
        {
            start = Math.Max(0, Math.Min(start, TextLength));
            length = Math.Max(0, Math.Min(length, TextLength - start));
            _editor.Select(start, length);
        }

        /// <summary>start부터 length자를 text로 바꾼다 — 되돌리기 한 번에 돌아간다.</summary>
        public void Replace(int start, int length, string text)
        {
            start = Math.Max(0, Math.Min(start, TextLength));
            length = Math.Max(0, Math.Min(length, TextLength - start));
            _editor.Document.Replace(start, length, text ?? "");
        }

        /// <summary>그 위치가 보이게 세로·가로로 스크롤한다(이미 보이면 그대로).</summary>
        public void ScrollIntoView(int offset)
        {
            var location = _editor.Document.GetLocation(Math.Max(0, Math.Min(offset, TextLength)));
            _editor.ScrollTo(location.Line, location.Column);
        }

        public void ScrollToHome()
        {
            _editor.ScrollToHome();
        }

        /// <summary>입력 칸에 키보드 포커스를 준다. 주었으면 true.</summary>
        public bool FocusEditor()
        {
            return _editor.TextArea.Focus();
        }

        /// <summary>문장 나누기·키워드 규칙(탭 대상 DB). 바뀌면 처음부터 다시 칠한다.</summary>
        public SqlLexRules Rules
        {
            get { return _highlighting.Rules; }
            set { _highlighting.Rules = value; }
        }

        /// <summary>설정 "SQL 편집기" 탭의 값(줄 간격, 강조 모양·대상, 긴 줄 기준).</summary>
        public void ApplySettings(SqlEditorSettings settings)
        {
            settings = settings ?? new SqlEditorSettings();
            SqlEditorSetup.ApplyFont(_editor, Theme.Mono, FontSizeDefault, settings.LineHeight);
            _highlighting.Settings = settings;
        }

        private void OnThemeChanged()
        {
            if (_highlighting != null)
                _highlighting.ThemeChanged();
        }

        private void Raise(EventHandler handler)
        {
            if (handler != null)
                handler(this, EventArgs.Empty);
        }
    }
}

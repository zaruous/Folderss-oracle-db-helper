using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace MyPlugin
{
    /// <summary>
    /// Folderss 테마 리소스 키와 플러그인 공통 색·글꼴. 색은 고정값 대신 SetResourceReference로 테마 키에 연결한다(테마를 바꾸면 함께 바뀜).
    /// Folderss가 기본 스타일을 주는 컨트롤: Button, TextBox, ComboBox, ListBox, ListView(GridView 열 머리 포함), ContextMenu, MenuItem, ScrollBar, ToolTip, TextBlock, Separator.
    /// 그 밖(CheckBox, RadioButton, PasswordBox, GridSplitter, Border, Panel 등)은 여기 도우미로 색을 직접 연결한다.
    /// </summary>
    internal static class Theme
    {
        public const string WindowBackground = "WindowBackground";
        public const string PanelBackground = "PanelBackground";
        public const string SurfaceBackground = "SurfaceBackground";
        public const string ControlBackground = "ControlBackground";
        public const string ControlHover = "ControlHoverBrush";
        public const string ControlPressed = "ControlPressedBrush";
        public const string Border = "BorderBrush";
        public const string PrimaryText = "PrimaryText";
        public const string SecondaryText = "SecondaryText";
        public const string DisabledText = "DisabledTextBrush";
        public const string Accent = "AccentBrush";
        public const string AccentHover = "AccentHoverBrush";
        public const string Selection = "SelectionBrush";
        public const string RowHover = "RowHoverBrush";
        public const string AppFont = "AppFontFamily";

        /// <summary>SQL 편집기·결과 그리드용 고정폭 글꼴.</summary>
        public static readonly FontFamily Mono = new FontFamily("Cascadia Mono, Consolas, D2Coding, Courier New");

        // 테마에 없는 의미 색. 어두운·밝은 배경 모두에서 읽히는 중간 톤으로 고정한다.
        /// <summary>오류·위험(Folderss Git 대화상자의 경고색과 같음).</summary>
        public static readonly Brush Danger = Frozen(0xE0, 0x6C, 0x75);
        public static readonly Brush Warning = Frozen(0xD1, 0x9A, 0x66);
        public static readonly Brush Success = Frozen(0x3F, 0xB2, 0x7F);
        /// <summary>결과 편집: 고친 행·삭제 표시한 행의 바탕(반투명이라 어두운·밝은 테마 모두에서 드러난다).</summary>
        public static readonly Brush ModifiedRow = Frozen(0x66, 0xD1, 0x9A, 0x66);
        public static readonly Brush DeletedRow = Frozen(0x66, 0xE0, 0x6C, 0x75);

        private static readonly Brush Green = Frozen(0x2E, 0xA0, 0x5B);
        private static readonly Brush Yellow = Frozen(0xC9, 0x93, 0x0A);
        private static readonly Brush Red = Frozen(0xD9, 0x36, 0x3E);

        /// <summary>접속 색 표시(OracleConnectionProfile.Color) → 색. 없으면 null.</summary>
        public static Brush ConnectionColor(string color)
        {
            switch (color)
            {
                case "green": return Green;
                case "yellow": return Yellow;
                case "red": return Red;
                default: return null;
            }
        }

        /// <summary>접속 관리 화면의 색 표시 선택지 이름.</summary>
        public static string ConnectionColorTitle(string color)
        {
            switch (color)
            {
                case "green": return "초록 — 개발";
                case "yellow": return "노랑 — 검증";
                case "red": return "빨강 — 운영";
                default: return "없음";
            }
        }

        /// <summary>글자색을 테마 키에 연결한다(Control·TextBlock·TextElement 모두 같은 속성을 공유).</summary>
        public static T Foreground<T>(T element, string key = PrimaryText) where T : FrameworkElement
        {
            element.SetResourceReference(TextElement.ForegroundProperty, key);
            return element;
        }

        /// <summary>배경색을 테마 키에 연결한다(Control, Panel, Border, TextBlock).</summary>
        public static T Background<T>(T element, string key) where T : FrameworkElement
        {
            if (element is Control)
                element.SetResourceReference(Control.BackgroundProperty, key);
            else if (element is Panel)
                element.SetResourceReference(Panel.BackgroundProperty, key);
            else if (element is System.Windows.Controls.Border)
                element.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, key);
            else if (element is TextBlock)
                element.SetResourceReference(TextBlock.BackgroundProperty, key);
            return element;
        }

        public static TextBlock Text(string text, string key = PrimaryText)
        {
            return Foreground(new TextBlock { Text = text, TextWrapping = TextWrapping.NoWrap }, key);
        }

        /// <summary>보조 설명 글자(작고 흐리게).</summary>
        public static TextBlock Secondary(string text)
        {
            var block = Text(text, SecondaryText);
            block.FontSize = 11.5;
            return block;
        }

        /// <summary>플러그인이 직접 만드는 창(대화상자)에 Folderss 창과 같은 배경·글자·글꼴을 준다.</summary>
        public static void ApplyWindow(Window window)
        {
            window.SetResourceReference(Window.BackgroundProperty, WindowBackground);
            window.SetResourceReference(Window.ForegroundProperty, PrimaryText);
            window.SetResourceReference(Window.FontFamilyProperty, AppFont);
            window.FontSize = 13;
        }

        /// <summary>
        /// DB 배지: 탭·트리·메시지·확인 창에 같은 모양으로 쓴다.
        /// 색 표시가 있으면 그 색 바탕에 흰 글자, 없으면 읽기 전용은 회색 테두리, 그 밖은 강조색 테두리. 접속이 없으면 "대상 없음"(위험색).
        /// </summary>
        public static Border DbBadge(OracleConnectionProfile profile)
        {
            var text = new TextBlock
            {
                Text = profile == null ? "대상 없음" : profile.Name,
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            var badge = new System.Windows.Controls.Border
            {
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(5, 0, 5, 0),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = text
            };
            var fill = profile == null ? null : ConnectionColor(profile.Color);
            if (profile == null)
            {
                text.Foreground = Danger;
                badge.BorderBrush = Danger;
            }
            else if (fill != null)
            {
                text.Foreground = Brushes.White;
                badge.Background = fill;
                badge.BorderBrush = fill;
            }
            else if (profile.ReadOnly)
            {
                text.SetResourceReference(TextBlock.ForegroundProperty, SecondaryText);
                badge.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, Border);
            }
            else
            {
                text.SetResourceReference(TextBlock.ForegroundProperty, Accent);
                badge.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, Accent);
            }
            return badge;
        }

        private static Brush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        private static Brush Frozen(byte a, byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}

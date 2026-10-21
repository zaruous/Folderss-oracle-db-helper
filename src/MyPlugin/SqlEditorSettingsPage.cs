using Folderss.Plugins;
using ICSharpCode.AvalonEdit;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace MyPlugin
{
    /// <summary>
    /// 설정 창의 "SQL 편집기" 탭(<see cref="SqlEditorSettings"/>): Folderss 설정에는 없는 편집기 전용 설정.
    /// - 표시: 줄 간격(글꼴 기본 또는 px), 칠하지 않을 긴 줄 기준(기본 20,000자)
    /// - 구문 강조: 키워드·자료형·문자열·주석마다 칠함·색(비우면 테마 색)·굵게·기울임, 지금 테마 바탕 대비 명암비
    /// - 키워드 대상: 더 칠할 낱말, 칠하지 않을 낱말(세 DBMS 모두)
    /// - 미리 보기: 고치는 대로 바로 보인다(저장 전). [기본값으로 되돌리기]
    /// 설정 창 [저장]에서 쓰고 열려 있는 편집기(지금은 AvalonEdit PoC 창)에 바로 알린다. 잘못 적은 칸이 있으면 저장 실패로 알린다.
    /// </summary>
    public sealed class SqlEditorSettingsPage : IPluginSettingsPage
    {
        private const string PreviewText = "-- 주문 조회 (주석)\n"
            + "SELECT o.order_id, NVL(o.memo, '없음') AS memo, COUNT(*) cnt\n"
            + "  FROM orders o\n"
            + "  JOIN customers c ON c.id = o.customer_id\n"
            + " WHERE o.status IN ('PAID', 'SHIPPED')\n"
            + "   AND o.created >= SYSDATE - 7\n"
            + " GROUP BY o.order_id, o.memo\n"
            + " ORDER BY cnt DESC;\n"
            + "CREATE TABLE t_memo (\n"
            + "  id    NUMBER(10)    PRIMARY KEY,  /* 자료형: NUMBER·VARCHAR2 */\n"
            + "  memo  VARCHAR2(400) NOT NULL,\n"
            + "  body  TEXT\n"
            + ");\n";

        private readonly IPluginManager _manager;
        private ComboBox _lineHeight;
        private TextBox _maxLine;
        private TextBox _extra;
        private TextBox _excluded;
        private TextBlock _error;
        private ComboBox _previewRules;
        private TextEditor _preview;
        private SqlEditorHighlighting _previewHighlighting;
        private readonly Dictionary<SqlSpanKind, StyleRow> _rows = new Dictionary<SqlSpanKind, StyleRow>();
        private bool _filling;

        public SqlEditorSettingsPage(IPluginManager manager)
        {
            _manager = manager;
        }

        public string Title { get { return "SQL 편집기"; } }

        private sealed class StyleRow
        {
            public CheckBox Enabled;
            public TextBox Color;
            public Border Swatch;
            public CheckBox Bold;
            public CheckBox Italic;
            public TextBlock Contrast;
        }

        public FrameworkElement CreateView()
        {
            _rows.Clear();
            var root = new StackPanel { Margin = new Thickness(8, 8, 18, 8) };
            var scroll = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            var intro = Theme.Secondary("Folderss 설정에는 없는 SQL 편집기 전용 설정입니다. 지금은 도구 › AvalonEdit PoC (시험판) 창에 적용되고, SQL 탭 편집기를 바꾸면 SQL 탭에도 적용됩니다.");
            intro.TextWrapping = TextWrapping.Wrap;
            root.Children.Add(intro);

            // ---- 표시 ----
            root.Children.Add(Heading("표시", 14));
            _lineHeight = new ComboBox { Width = 210, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var value in SqlEditorSettings.LineHeightChoices)
                _lineHeight.Items.Add(new ComboBoxItem { Content = SqlEditorSettings.LineHeightLabel(value), Tag = value });
            AutomationProperties.SetName(_lineHeight, "줄 간격");
            AutomationProperties.SetAutomationId(_lineHeight, "sqleditor-line-height");
            root.Children.Add(Row("줄 간격", _lineHeight, "글꼴 기본은 글꼴 크기 13에서 15px입니다."));
            _maxLine = new TextBox { Width = 90, HorizontalContentAlignment = HorizontalAlignment.Right };
            AutomationProperties.SetName(_maxLine, "칠하지 않을 긴 줄(글자 수)");
            AutomationProperties.SetAutomationId(_maxLine, "sqleditor-max-line");
            root.Children.Add(Row("긴 줄", _maxLine, "자를 넘는 줄은 칠하지 않습니다 (" + SqlEditorSettings.MinMaxColoredLine.ToString("N0", CultureInfo.InvariantCulture) + "~"
                + SqlEditorSettings.MaxMaxColoredLine.ToString("N0", CultureInfo.InvariantCulture) + ", 기본 " + SqlEditorSettings.DefaultMaxColoredLine.ToString("N0", CultureInfo.InvariantCulture)
                + ") — 한 줄에 칠할 곳이 수만 개면 그리기가 느려집니다."));

            // ---- 구문 강조 ----
            root.Children.Add(Heading("구문 강조", 18));
            var note = Theme.Secondary("색을 비우면 테마에서 정한 색을 씁니다(자료형은 키워드와 같은 색). 명암비는 지금 테마 바탕과 비교한 값으로, 4.5:1 이상이면 읽기 편합니다.");
            note.TextWrapping = TextWrapping.Wrap;
            note.Margin = new Thickness(0, 0, 0, 6);
            root.Children.Add(note);
            root.Children.Add(StyleGrid());

            // ---- 키워드 대상 ----
            root.Children.Add(Heading("키워드 대상", 18));
            _extra = new TextBox { MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(_extra, "더 칠할 낱말");
            AutomationProperties.SetAutomationId(_extra, "sqleditor-extra-keywords");
            root.Children.Add(Row("더 칠할 낱말", _extra, null));
            root.Children.Add(Hint("공백이나 쉼표로 나눠 적습니다. 키워드 색으로 칠합니다(세 DBMS 모두). 예: NVL DECODE"));
            _excluded = new TextBox { MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(_excluded, "칠하지 않을 낱말");
            AutomationProperties.SetAutomationId(_excluded, "sqleditor-excluded-keywords");
            root.Children.Add(Row("칠하지 않을 낱말", _excluded, null));
            root.Children.Add(Hint("기본 목록(키워드·자료형)에서 뺍니다. 열 이름과 겹치는 낱말에 씁니다. 예: LEVEL TEXT"));
            root.Children.Add(Hint("기본 목록 — " + string.Join(" · ", new[] { SqlLexRules.Oracle, SqlLexRules.Postgres, SqlLexRules.SqlServer }
                .Select(r => r.Name + " 키워드 " + r.Keywords.Count.ToString(CultureInfo.InvariantCulture) + "·자료형 " + r.Types.Count.ToString(CultureInfo.InvariantCulture)))));

            // ---- 미리 보기 ----
            root.Children.Add(Heading("미리 보기", 18));
            _previewRules = new ComboBox { Width = 140, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var r in new[] { SqlLexRules.Oracle, SqlLexRules.Postgres, SqlLexRules.SqlServer })
                _previewRules.Items.Add(new ComboBoxItem { Content = r.Name, Tag = r });
            _previewRules.SelectedIndex = 0;
            AutomationProperties.SetAutomationId(_previewRules, "sqleditor-preview-rules");
            root.Children.Add(Row("규칙", _previewRules, "저장하기 전 모습입니다. 글을 고쳐 볼 수도 있습니다(저장되지 않음)."));
            _preview = new TextEditor { ShowLineNumbers = true, Height = 250, Margin = new Thickness(0, 6, 0, 0), BorderThickness = new Thickness(1) };
            _preview.SetResourceReference(Control.BackgroundProperty, Theme.PanelBackground);
            _preview.SetResourceReference(Control.ForegroundProperty, Theme.PrimaryText);
            _preview.SetResourceReference(Control.BorderBrushProperty, Theme.Border);
            _preview.SetResourceReference(TextEditor.LineNumbersForegroundProperty, Theme.SecondaryText);
            _preview.TextArea.SelectionForeground = null;
            _preview.TextArea.SelectionBorder = null;
            _preview.TextArea.SetResourceReference(ICSharpCode.AvalonEdit.Editing.TextArea.SelectionBrushProperty, Theme.Selection);
            AutomationProperties.SetAutomationId(_preview, "sqleditor-preview");
            SqlEditorSetup.ApplyBehavior(_preview);
            _preview.Document.Text = PreviewText;
            root.Children.Add(_preview);

            _error = Theme.Text("");
            _error.Foreground = Theme.Danger;
            _error.TextWrapping = TextWrapping.Wrap;
            _error.Margin = new Thickness(0, 8, 0, 0);
            AutomationProperties.SetAutomationId(_error, "sqleditor-error");
            root.Children.Add(_error);

            var reset = new Button { Content = "기본값으로 되돌리기", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(12, 3, 12, 3) };
            AutomationProperties.SetAutomationId(reset, "sqleditor-reset");
            reset.Click += (s, e) => Fill(new SqlEditorSettings());
            root.Children.Add(reset);

            Fill(Load());
            _lineHeight.SelectionChanged += (s, e) => Refresh();
            _previewRules.SelectionChanged += (s, e) =>
            {
                if (_previewHighlighting != null)
                    _previewHighlighting.Rules = PreviewRules;
            };
            foreach (var box in new[] { _maxLine, _extra, _excluded })
                box.TextChanged += (s, e) => Refresh();
            // 미리 보기 강조와 테마 감시(바탕이 바뀌면 명암비를 다시 잰다)는 화면에 붙어 있는 동안만 — 설정 창을 닫으면 놓아 준다
            var background = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(Control.BackgroundProperty, typeof(TextEditor));
            EventHandler themeChanged = (s, e) => Refresh();
            scroll.Loaded += (s, e) =>
            {
                if (_previewHighlighting != null)
                    return;
                string error;
                _previewHighlighting = SqlEditorHighlighting.Attach(_preview, PreviewRules, Collect(out error) ?? new SqlEditorSettings());
                background.AddValueChanged(_preview, themeChanged);
                Refresh();
            };
            scroll.Unloaded += (s, e) =>
            {
                if (_previewHighlighting == null)
                    return;
                background.RemoveValueChanged(_preview, themeChanged);
                _previewHighlighting.Detach();
                _previewHighlighting = null;
            };
            return scroll;
        }

        private SqlLexRules PreviewRules
        {
            get { return (SqlLexRules)((ComboBoxItem)_previewRules.SelectedItem).Tag; }
        }

        private UIElement StyleGrid()
        {
            var grid = new Grid();
            foreach (var width in new[] { 70.0, 54, 140, 54, 64, 150 })
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
            var headers = new[] { "대상", "칠함", "색", "굵게", "기울임", "명암비" };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var c = 0; c < headers.Length; c++)
            {
                var header = Theme.Secondary(headers[c]);
                Grid.SetColumn(header, c);
                grid.Children.Add(header);
            }
            var row = 1;
            foreach (var kind in SqlEditorSettings.StyledKinds)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var key = "sqleditor-style-" + kind.ToString().ToLowerInvariant();
                var name = Theme.Text(SqlEditorSettings.KindName(kind));
                name.VerticalAlignment = VerticalAlignment.Center;
                var item = new StyleRow
                {
                    Enabled = Box(key + "-enabled", SqlEditorSettings.KindName(kind) + " 칠함"),
                    Color = new TextBox { Width = 90, VerticalAlignment = VerticalAlignment.Center, ToolTip = "#RRGGBB — 비우면 테마 색" },
                    Swatch = new Border { Width = 18, Height = 18, Margin = new Thickness(6, 0, 0, 0), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center },
                    Bold = Box(key + "-bold", SqlEditorSettings.KindName(kind) + " 굵게"),
                    Italic = Box(key + "-italic", SqlEditorSettings.KindName(kind) + " 기울임"),
                    Contrast = Theme.Secondary("")
                };
                item.Swatch.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
                item.Contrast.VerticalAlignment = VerticalAlignment.Center;
                AutomationProperties.SetName(item.Color, SqlEditorSettings.KindName(kind) + " 색");
                AutomationProperties.SetAutomationId(item.Color, key + "-color");
                AutomationProperties.SetAutomationId(item.Contrast, key + "-contrast");
                var color = new StackPanel { Orientation = Orientation.Horizontal };
                color.Children.Add(item.Color);
                color.Children.Add(item.Swatch);
                var cells = new UIElement[] { name, item.Enabled, color, item.Bold, item.Italic, item.Contrast };
                for (var c = 0; c < cells.Length; c++)
                {
                    var element = (FrameworkElement)cells[c];
                    element.Margin = new Thickness(element.Margin.Left, 4, 0, 4);
                    Grid.SetRow(element, row);
                    Grid.SetColumn(element, c);
                    grid.Children.Add(element);
                }
                item.Enabled.Click += (s, e) => Refresh();
                item.Bold.Click += (s, e) => Refresh();
                item.Italic.Click += (s, e) => Refresh();
                item.Color.TextChanged += (s, e) => Refresh();
                _rows[kind] = item;
                row++;
            }
            return grid;
        }

        private static CheckBox Box(string automationId, string name)
        {
            // 글 없는 체크 상자(머리글이 열 이름) — 상자는 기본 모양이어야 체크 표시가 보인다
            var box = Theme.Foreground(new CheckBox { VerticalAlignment = VerticalAlignment.Center });
            AutomationProperties.SetAutomationId(box, automationId);
            AutomationProperties.SetName(box, name);
            return box;
        }

        private static TextBlock Heading(string text, double top)
        {
            var head = Theme.Text(text);
            head.FontWeight = FontWeights.SemiBold;
            head.Margin = new Thickness(0, top, 0, 6);
            return head;
        }

        private static TextBlock Hint(string text)
        {
            var hint = Theme.Secondary(text);
            hint.TextWrapping = TextWrapping.Wrap;
            hint.Margin = new Thickness(118, 2, 0, 0);
            return hint;
        }

        private static UIElement Row(string label, FrameworkElement input, string hint)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0), LastChildFill = true };
            var text = Theme.Text(label, Theme.SecondaryText);
            text.Width = 110;
            text.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(text, Dock.Left);
            row.Children.Add(text);
            input.VerticalAlignment = VerticalAlignment.Center;
            if (hint == null)
            {
                row.Children.Add(input);
                return row;
            }
            DockPanel.SetDock(input, Dock.Left);
            row.Children.Add(input);
            var note = Theme.Secondary(hint);
            note.TextWrapping = TextWrapping.Wrap;
            note.VerticalAlignment = VerticalAlignment.Center;
            note.Margin = new Thickness(8, 0, 0, 0);
            row.Children.Add(note);
            return row;
        }

        private SqlEditorSettings Load()
        {
            try
            {
                return SqlEditorSettings.Parse(_manager.GetSetting(SqlEditorSettings.SettingKey));
            }
            catch (Exception)
            {
                return new SqlEditorSettings();
            }
        }

        private void Fill(SqlEditorSettings settings)
        {
            _filling = true;
            try
            {
                var index = Array.IndexOf(SqlEditorSettings.LineHeightChoices, settings.LineHeight);
                if (index < 0)
                {
                    _lineHeight.Items.Add(new ComboBoxItem { Content = SqlEditorSettings.LineHeightLabel(settings.LineHeight), Tag = settings.LineHeight });
                    index = _lineHeight.Items.Count - 1;
                }
                _lineHeight.SelectedIndex = index;
                _maxLine.Text = settings.MaxColoredLine.ToString(CultureInfo.InvariantCulture);
                foreach (var pair in _rows)
                {
                    var style = settings.Style(pair.Key);
                    pair.Value.Enabled.IsChecked = style.Enabled;
                    pair.Value.Color.Text = SqlEditorSettings.FormatColor(style.Color);
                    pair.Value.Bold.IsChecked = style.Bold;
                    pair.Value.Italic.IsChecked = style.Italic;
                }
                _extra.Text = SqlEditorSettings.FormatWords(settings.ExtraKeywords);
                _excluded.Text = SqlEditorSettings.FormatWords(settings.ExcludedKeywords);
            }
            finally
            {
                _filling = false;
            }
            Refresh();
        }

        /// <summary>칸의 값으로 설정을 만든다. 잘못 적은 칸이 있으면 null과 그 까닭.</summary>
        private SqlEditorSettings Collect(out string error)
        {
            var settings = new SqlEditorSettings();
            var errors = new List<string>();
            var item = _lineHeight.SelectedItem as ComboBoxItem;
            settings.LineHeight = item == null ? 0 : (double)item.Tag;
            int maxLine;
            var problem = SqlEditorSettings.ParseMaxColoredLine(_maxLine.Text, out maxLine);
            if (problem != null)
                errors.Add(problem);
            settings.MaxColoredLine = maxLine;
            foreach (var pair in _rows)
            {
                var style = settings.Style(pair.Key);
                style.Enabled = pair.Value.Enabled.IsChecked == true;
                style.Bold = pair.Value.Bold.IsChecked == true;
                style.Italic = pair.Value.Italic.IsChecked == true;
                int? color;
                problem = SqlEditorSettings.ParseColor(pair.Value.Color.Text, out color);
                if (problem != null)
                    errors.Add(SqlEditorSettings.KindName(pair.Key) + " " + problem);
                style.Color = color;
            }
            List<string> words;
            problem = SqlEditorSettings.ParseWords(_extra.Text, out words);
            if (problem != null)
                errors.Add("더 칠할 " + problem);
            settings.ExtraKeywords.AddRange(words);
            problem = SqlEditorSettings.ParseWords(_excluded.Text, out words);
            if (problem != null)
                errors.Add("칠하지 않을 " + problem);
            settings.ExcludedKeywords.AddRange(words);
            error = errors.Count == 0 ? null : string.Join("\n", errors);
            return error == null ? settings : null;
        }

        // 칸을 고칠 때마다: 오류 글, 색 견본·명암비, 미리 보기
        private void Refresh()
        {
            if (_filling || _preview == null)
                return;
            string error;
            var settings = Collect(out error);
            _error.Text = error ?? "";
            var background = Rgb(_preview, Theme.PanelBackground, 0x000000);
            var palette = SqlHighlightLogic.Palette(background, Rgb(_preview, Theme.PrimaryText, 0xFFFFFF), Rgb(_preview, Theme.SecondaryText, 0x999999), Rgb(_preview, Theme.Accent, 0x4F8CFF));
            foreach (var pair in _rows)
            {
                int? custom;
                SqlEditorSettings.ParseColor(pair.Value.Color.Text, out custom);
                var themeColor = pair.Key == SqlSpanKind.Comment ? palette.Comment : pair.Key == SqlSpanKind.String ? palette.String : palette.Keyword;
                var color = custom ?? themeColor;
                pair.Value.Swatch.Background = new SolidColorBrush(Color.FromRgb((byte)(color >> 16), (byte)(color >> 8), (byte)color));
                var ratio = SqlHighlightLogic.Contrast(color, background);
                pair.Value.Contrast.Text = (custom.HasValue ? "" : "테마 색 ") + ratio.ToString("0.0", CultureInfo.InvariantCulture) + ":1"
                    + (ratio < SqlHighlightLogic.MinContrast ? " — 읽기 어려움" : "") + (pair.Value.Enabled.IsChecked == true ? "" : " (끔)");
                pair.Value.Contrast.Foreground = ratio < SqlHighlightLogic.MinContrast ? Theme.Danger : (Brush)_preview.TryFindResource(Theme.SecondaryText);
            }
            if (settings == null)
                return;
            SqlEditorSetup.ApplyFont(_preview, Theme.Mono, 13, settings.LineHeight);
            if (_previewHighlighting != null)
                _previewHighlighting.Settings = settings;
        }

        private static int Rgb(FrameworkElement view, string key, int fallback)
        {
            var brush = view.TryFindResource(key) as SolidColorBrush;
            return brush == null ? fallback : (brush.Color.R << 16) | (brush.Color.G << 8) | brush.Color.B;
        }

        /// <summary>잘못 적은 칸이 있으면 예외(설정 창이 저장 실패로 알린다). 저장하면 열린 편집기에 바로 알린다.</summary>
        public void Save()
        {
            if (_lineHeight == null)
                return;
            string error;
            var settings = Collect(out error);
            if (settings == null)
                throw new InvalidOperationException("SQL 편집기 설정을 저장하지 못했습니다.\n" + error);
            _manager.SetSetting(SqlEditorSettings.SettingKey, settings.Serialize());
            SqlEditorSettings.Publish(settings);
        }
    }
}

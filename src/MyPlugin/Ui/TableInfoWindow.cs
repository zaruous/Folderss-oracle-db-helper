using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace MyPlugin
{
    /// <summary>
    /// F4 테이블 정보 창(모달 아님 — 열어 둔 채 SQL을 쓸 수 있다, Esc로 닫음).
    /// 위: 이름·종류·행 수(통계)·날짜·주석·찾은 동의어. 가운데: [열]·[인덱스]·[제약 조건] 표(결과 그리드와 같은 표 — 정렬·복사 됨).
    /// 아래: [SELECT 문 넣기]·[빠른 조회]·[닫기].
    /// </summary>
    internal static class TableInfoWindow
    {
        public static Window Show(Window owner, TableDescription info, OracleConnectionProfile profile, Action insertSelect, Action quickQuery, Action<string> copy)
        {
            var qualified = WorkspaceLogic.QualifiedName(info.Owner, info.Name);
            var window = new Window
            {
                Title = "테이블 정보 — " + qualified + (profile != null ? " (" + profile.Name + ")" : ""),
                Width = 780,
                Height = 540,
                MinWidth = 480,
                MinHeight = 320,
                ShowInTaskbar = false,
                ResizeMode = ResizeMode.CanResizeWithGrip
            };
            Theme.ApplyWindow(window);
            DialogKit.SetOwner(window, owner);

            var root = new DockPanel { Margin = new Thickness(14, 12, 14, 12) };
            var header = Header(info, qualified, profile);
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var buttons = Buttons(window, insertSelect, quickQuery, info);
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);

            // [열]·[인덱스]·[제약 조건] 고르기 + 표
            var grids = new[]
            {
                Grid(ColumnsTable(info), copy),
                Grid(IndexesTable(info), copy),
                Grid(ConstraintsTable(info), copy)
            };
            var titles = new[]
            {
                "열 " + info.Columns.Count.ToString(CultureInfo.InvariantCulture),
                "인덱스 " + info.Indexes.Count.ToString(CultureInfo.InvariantCulture),
                "제약 조건 " + info.Constraints.Count.ToString(CultureInfo.InvariantCulture)
            };
            var host = new Grid();
            var strip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            var underlines = new Border[grids.Length];
            var selectors = new Button[grids.Length];
            Action<int> select = null;
            for (var i = 0; i < grids.Length; i++)
            {
                var index = i;
                host.Children.Add(grids[i].Value.View);
                Border underline;
                var button = WorkspaceUi.SelectorButton(Theme.Text(titles[i]), out underline);
                AutomationProperties.SetName(button, titles[i]);
                button.Click += (s, e) => select(index);
                underlines[i] = underline;
                selectors[i] = button;
                strip.Children.Add(button);
            }
            select = index =>
            {
                for (var i = 0; i < grids.Length; i++)
                {
                    grids[i].Value.View.Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
                    WorkspaceUi.SetSelected(selectors[i], underlines[i], i == index);
                }
            };
            var tableArea = new DockPanel();
            var stripBorder = new Border { Child = strip, BorderThickness = new Thickness(0, 0, 0, 1) };
            stripBorder.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
            DockPanel.SetDock(stripBorder, Dock.Top);
            tableArea.Children.Add(stripBorder);
            var hostBorder = new Border { Child = host, BorderThickness = new Thickness(1, 0, 1, 1) };
            hostBorder.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
            tableArea.Children.Add(hostBorder);
            root.Children.Add(tableArea);
            window.Content = root;

            window.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    window.Close();
                }
            };
            window.Loaded += (s, e) =>
            {
                // 표는 화면 트리에 붙은 뒤 채운다(테마 스타일을 찾으려고)
                foreach (var pair in grids)
                    pair.Value.Show(pair.Key.Columns, pair.Key.Rows);
                select(0);
            };
            window.Show();
            return window;
        }

        private static FrameworkElement Header(TableDescription info, string qualified, OracleConnectionProfile profile)
        {
            var panel = new StackPanel();
            var line = new WrapPanel();
            var name = Theme.Text(qualified);
            name.FontSize = 16;
            name.FontWeight = FontWeights.SemiBold;
            name.Margin = new Thickness(0, 0, 8, 0);
            line.Children.Add(name);
            var type = Theme.Text(info.Type == "VIEW" ? "뷰" : "테이블", Theme.SecondaryText);
            type.VerticalAlignment = VerticalAlignment.Bottom;
            type.Margin = new Thickness(0, 0, 8, 2);
            line.Children.Add(type);
            if (!string.IsNullOrEmpty(info.Status) && info.Status != "VALID")
            {
                var status = Theme.Text(info.Status);
                status.Foreground = Theme.Danger;
                status.VerticalAlignment = VerticalAlignment.Bottom;
                status.Margin = new Thickness(0, 0, 8, 2);
                line.Children.Add(status);
            }
            if (profile != null)
            {
                var badge = Theme.DbBadge(profile);
                badge.VerticalAlignment = VerticalAlignment.Center;
                line.Children.Add(badge);
            }
            panel.Children.Add(line);
            var facts = Theme.Text(TableInfoText.Facts(info), Theme.SecondaryText);
            facts.TextWrapping = TextWrapping.Wrap;
            facts.Margin = new Thickness(0, 4, 0, 0);
            panel.Children.Add(facts);
            if (!string.IsNullOrWhiteSpace(info.Comment))
            {
                var comment = Theme.Text(info.Comment);
                comment.TextWrapping = TextWrapping.Wrap;
                comment.Margin = new Thickness(0, 4, 0, 0);
                panel.Children.Add(comment);
            }
            return panel;
        }

        private static FrameworkElement Buttons(Window window, Action insertSelect, Action quickQuery, TableDescription info)
        {
            var insert = new Button { Content = "SELECT 문 넣기", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 6, 0) };
            insert.Click += (s, e) => insertSelect();
            var quick = new Button { Content = "빠른 조회 (앞 " + WorkspaceLogic.QuickQueryRows.ToString(CultureInfo.InvariantCulture) + "행)", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 6, 0) };
            quick.Click += (s, e) => quickQuery();
            var close = new Button { Content = "닫기", Padding = new Thickness(16, 3, 16, 3), IsCancel = true };
            close.Click += (s, e) => window.Close();
            var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            panel.Children.Add(insert);
            panel.Children.Add(quick);
            panel.Children.Add(close);
            return panel;
        }

        private static KeyValuePair<InfoTable, ResultGridView> Grid(InfoTable table, Action<string> copy)
        {
            ResultGridView grid = null;
            grid = new ResultGridView(all =>
            {
                var rows = all ? grid.RowsInViewOrder() : grid.SelectedRows();
                copy(WorkspaceLogic.ToTsv(all ? table.Columns.Select(c => c.Name) : null, rows.Select(r => r.Values)));
            }, copy);
            grid.View.Visibility = Visibility.Collapsed;
            return new KeyValuePair<InfoTable, ResultGridView>(table, grid);
        }

        private sealed class InfoTable
        {
            public List<ResultColumn> Columns { get; } = new List<ResultColumn>();
            public ObservableCollection<ResultGridRow> Rows { get; } = new ObservableCollection<ResultGridRow>();

            public InfoTable(params string[] columns)
            {
                foreach (var name in columns)
                    Columns.Add(new ResultColumn { Name = name, TypeLabel = "", IsNumeric = false });
            }

            public void Add(params string[] values)
            {
                // 정보 표의 빈 칸은 DB 값이 아니다 — NULL(흐린 기울임꼴)이 아니라 빈 칸으로
                Rows.Add(new ResultGridRow(Rows.Count + 1, values.Select(v => v ?? "").ToArray()));
            }
        }

        private static InfoTable ColumnsTable(TableDescription info)
        {
            var table = new InfoTable("이름", "형식", "NULL", "PK", "주석");
            foreach (var c in info.Columns)
                table.Add(c.Column.Name, c.Column.TypeLabel, c.Column.Nullable ? "Y" : "N", c.Column.PrimaryKey ? "PK" : null, c.Comment);
            return table;
        }

        private static InfoTable IndexesTable(TableDescription info)
        {
            var table = new InfoTable("이름", "고유", "열", "종류", "상태");
            foreach (var i in info.Indexes)
                table.Add(i.Name, i.Unique ? "UNIQUE" : null, i.Columns, i.Type, i.Status);
            return table;
        }

        private static InfoTable ConstraintsTable(TableDescription info)
        {
            var table = new InfoTable("이름", "종류", "열", "참조", "상태");
            foreach (var c in info.Constraints)
                table.Add(c.Name, TableInfo.ConstraintTypeLabel(c.Type), c.Columns, c.Reference, c.Status);
            return table;
        }
    }
}

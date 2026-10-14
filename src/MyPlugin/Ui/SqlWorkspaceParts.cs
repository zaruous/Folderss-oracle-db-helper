using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace MyPlugin
{
    /// <summary>SQL 작업 영역 화면 조각(코드로 만드는 컨트롤 모양). 색은 모두 테마 키에 연결한다.</summary>
    internal static class WorkspaceUi
    {
        /// <summary>
        /// 탭 줄·결과 머리의 고르는 버튼. 내용 아래 2px 밑줄은 고른 버튼만 강조색이다(SetSelected).
        /// 글자색은 내용 TextBlock에 직접 준다 — Folderss 버튼 템플릿의 마우스 상태와 무관하게 읽히게.
        /// </summary>
        public static Button SelectorButton(UIElement content, out Border underline)
        {
            underline = new Border
            {
                BorderThickness = new Thickness(0, 0, 0, 2),
                Padding = new Thickness(9, 3, 9, 1),
                Child = content
            };
            var button = new Button
            {
                Content = underline,
                Padding = new Thickness(0),
                Margin = new Thickness(0),
                BorderThickness = new Thickness(0, 0, 1, 0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch
            };
            button.SetResourceReference(Control.BorderBrushProperty, Theme.Border);
            return button;
        }

        /// <summary>
        /// 가로로 넘칠 수 있는 탭 줄. 스크롤바는 숨긴다(넘칠 때 스크롤바가 생기면 줄이 두꺼워져 아래 칸이 밀린다) — 휠로 가로 스크롤하고,
        /// 넘칠 때만 listButton(▾)이 보인다. ▾를 누르면 fillList가 채운 메뉴(전체 탭 목록)를 연다. listButton은 호출자가 배치한다.
        /// </summary>
        public static ScrollViewer TabScroll(Panel items, string listName, Action<ContextMenu> fillList, out Button listButton)
        {
            var scroll = new ScrollViewer
            {
                Content = items,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false
            };
            scroll.PreviewMouseWheel += (s, e) =>
            {
                if (scroll.ScrollableWidth <= 0)
                    return;
                e.Handled = true;
                scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - e.Delta / 2.0);
            };
            var button = new Button
            {
                Content = "▾",
                ToolTip = listName,
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(2, 3, 0, 3),
                VerticalAlignment = VerticalAlignment.Center,
                Focusable = false,
                Visibility = Visibility.Collapsed
            };
            AutomationProperties.SetName(button, listName);
            // 탭 수·창 폭이 바뀌면 ScrollChanged(ExtentWidth·ViewportWidth 변화)가 온다
            scroll.ScrollChanged += (s, e) =>
            {
                var visibility = scroll.ScrollableWidth > 0 ? Visibility.Visible : Visibility.Collapsed;
                if (button.Visibility != visibility)
                    button.Visibility = visibility;
            };
            button.Click += (s, e) =>
            {
                var menu = ScrollingMenu();
                fillList(menu);
                if (menu.Items.Count == 0)
                    return;
                menu.PlacementTarget = button;
                menu.Placement = PlacementMode.Bottom;
                menu.IsOpen = true;
            };
            listButton = button;
            return scroll;
        }

        /// <summary>
        /// 항목이 많으면 세로로 스크롤되는 메뉴. Folderss의 ContextMenu 템플릿에는 ScrollViewer가 없어 화면보다 길면 아래 항목에 닿을 수 없다.
        /// </summary>
        public static ContextMenu ScrollingMenu()
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            border.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
            var scroll = new FrameworkElementFactory(typeof(ScrollViewer));
            scroll.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
            scroll.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
            scroll.SetValue(UIElement.FocusableProperty, false);
            scroll.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
            border.AppendChild(scroll);
            return new ContextMenu
            {
                Template = new ControlTemplate(typeof(ContextMenu)) { VisualTree = border },
                MaxHeight = SystemParameters.WorkArea.Height * 0.6
            };
        }

        /// <summary>메뉴 항목. 머리글을 TextBlock으로 넣어 글 속 '_'가 바로 가기 키 표시로 사라지지 않게 한다(파일 이름에 흔함).</summary>
        public static MenuItem PlainMenuItem(string text, bool bold, Action click)
        {
            var item = new MenuItem { Header = new TextBlock { Text = text, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal } };
            item.Click += (s, e) => click();
            return item;
        }

        /// <summary>배치가 끝난 뒤 그 요소가 보이게 스크롤한다(숨긴 스크롤바로 넘친 탭 줄에서 고른 탭이 보이게).</summary>
        public static void BringIntoViewWhenLoaded(FrameworkElement element)
        {
            element.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                try
                {
                    if (element.IsLoaded)
                        element.BringIntoView();
                }
                catch (Exception)
                {
                    // 그 사이 탭 줄을 다시 그렸으면 할 일이 없다
                }
            }));
        }

        public static void SetSelected(Button button, Border underline, bool selected)
        {
            if (selected)
                underline.SetResourceReference(Border.BorderBrushProperty, Theme.Accent);
            else
                underline.ClearValue(Border.BorderBrushProperty);
            Theme.Background(button, selected ? Theme.PanelBackground : Theme.SurfaceBackground);
        }

        /// <summary>
        /// 주 동작 버튼(강조색 바탕). 템플릿을 직접 줘서 Folderss 버튼 템플릿의 마우스 오버 색이 강조색을 덮지 않게 한다.
        /// labels는 버튼 안 글자: 켜짐이면 바탕색 계열(강조색 위에서 두 테마 모두 읽힘), 꺼짐이면 흐린 글자색.
        /// </summary>
        public static void MakePrimary(Button button, params TextBlock[] labels)
        {
            button.Template = PrimaryTemplate();
            ApplyPrimaryLabels(button, labels);
            button.IsEnabledChanged += (s, e) => ApplyPrimaryLabels(button, labels);
        }

        private static void ApplyPrimaryLabels(Button button, TextBlock[] labels)
        {
            foreach (var label in labels)
                Theme.Foreground(label, button.IsEnabled ? Theme.PanelBackground : Theme.DisabledText);
        }

        private static ControlTemplate PrimaryTemplate()
        {
            var border = new FrameworkElementFactory(typeof(Border), "border");
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            border.SetResourceReference(Border.BackgroundProperty, Theme.Accent);
            border.SetResourceReference(Border.BorderBrushProperty, Theme.Accent);
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);

            var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension(Theme.AccentHover), "border"));
            hover.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension(Theme.AccentHover), "border"));
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension(Theme.ControlBackground), "border"));
            disabled.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension(Theme.Border), "border"));
            template.Triggers.Add(hover);
            template.Triggers.Add(disabled);
            template.Seal();
            return template;
        }

        /// <summary>개수 표시(작은 둥근 바탕).</summary>
        public static Border CountPill(TextBlock text)
        {
            text.FontSize = 10.5;
            Theme.Foreground(text, Theme.SecondaryText);
            var pill = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(6, 0, 6, 0),
                Margin = new Thickness(5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = text
            };
            pill.SetResourceReference(Border.BackgroundProperty, Theme.ControlHover);
            return pill;
        }

        /// <summary>
        /// 항목 컨테이너 스타일: 테마의 기본 스타일(scope에서 찾음)을 바탕으로 내용을 가로로 채운다(숫자 오른쪽 정렬·줄바꿈용).
        /// 테마 스타일을 찾지 못하면 null — 명시 스타일이 테마의 암시 스타일을 가리지 않게 호출자는 그대로 둔다.
        /// </summary>
        public static Style StretchItemStyle(FrameworkElement scope, Type itemType)
        {
            var baseStyle = scope.TryFindResource(itemType) as Style;
            if (baseStyle == null)
                return null;
            var style = new Style(itemType, baseStyle);
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            return style;
        }

        /// <summary>source(마우스 이벤트의 OriginalSource 등)가 target 안에 있는지. Run 같은 ContentElement도 따라 올라간다.</summary>
        public static bool IsInside(object source, DependencyObject target)
        {
            var node = source as DependencyObject;
            for (var depth = 0; node != null && depth < 64; depth++)
            {
                if (ReferenceEquals(node, target))
                    return true;
                node = node is Visual || node is Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
            }
            return false;
        }

        public static T FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null)
                return null;
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var found = child as T ?? FindDescendant<T>(child);
                if (found != null)
                    return found;
            }
            return null;
        }
    }
}

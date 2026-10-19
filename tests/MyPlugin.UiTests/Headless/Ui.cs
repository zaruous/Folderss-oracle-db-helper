using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MyPlugin.UiTests.Headless
{
    /// <summary>
    /// 화면 찾기·조작(UI 스레드에서만 부른다). 마우스·포커스·키 입력을 쓰지 않는다 — 자동화 피어(Invoke·Toggle·Selection·ExpandCollapse)와
    /// 속성으로 조작하므로 숨은 데스크톱에서도, 창이 가려져도 같다. 우클릭 메뉴는 ContextMenu.IsOpen으로 열어 항목을 Invoke한다.
    /// </summary>
    internal static class Ui
    {
        // ---------- 창 ----------

        public static List<Window> Windows()
        {
            return Application.Current.Windows.Cast<Window>().ToList();
        }

        /// <summary>제목이 이 글로 시작하는 보이는 창(없으면 null).</summary>
        public static Window Window(string titleStartsWith)
        {
            return Windows().FirstOrDefault(w => w.IsVisible && (w.Title ?? "").StartsWith(titleStartsWith, StringComparison.Ordinal));
        }

        // ---------- 트리 ----------

        /// <summary>화면 트리(보이는 것) + 논리 트리(접힌 메뉴 항목 등)를 함께 훑는다. 같은 요소는 한 번만.</summary>
        public static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            var seen = new HashSet<DependencyObject>();
            var stack = new Stack<DependencyObject>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                if (node == null || !seen.Add(node))
                    continue;
                yield return node;
                // 스택이라 나중에 넣은 것이 먼저 나온다: 오른쪽 메뉴·논리 자식(접힌 메뉴 항목 등)을 먼저 넣고, 화면 자식을 거꾸로 넣어
                // 화면 순서(위 → 아래)대로 나오게 한다. 같은 요소는 seen으로 한 번만
                var element = node as FrameworkElement;
                if (element != null && element.ContextMenu != null)
                    stack.Push(element.ContextMenu);
                foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>().Reverse())
                    stack.Push(child);
                if (node is Visual || node is System.Windows.Media.Media3D.Visual3D)
                {
                    var count = VisualTreeHelper.GetChildrenCount(node);
                    for (var i = count - 1; i >= 0; i--)
                        stack.Push(VisualTreeHelper.GetChild(node, i));
                }
            }
        }

        public static T Find<T>(DependencyObject root, Func<T, bool> match = null) where T : DependencyObject
        {
            return Descendants(root).OfType<T>().FirstOrDefault(e => match == null || match(e));
        }

        public static List<T> FindAll<T>(DependencyObject root, Func<T, bool> match = null) where T : DependencyObject
        {
            return Descendants(root).OfType<T>().Where(e => match == null || match(e)).ToList();
        }

        public static T ById<T>(DependencyObject root, string automationId) where T : DependencyObject
        {
            return Find<T>(root, e => AutomationProperties.GetAutomationId(e) == automationId);
        }

        /// <summary>글(내용·AutomationProperties.Name)이 정확히 같은 버튼.</summary>
        public static ButtonBase Button(DependencyObject root, string text)
        {
            return Find<ButtonBase>(root, b => b.IsVisible && (ContentText(b.Content) == text || AutomationProperties.GetName(b) == text));
        }

        /// <summary>보이는 TextBlock 글(화면 순서).</summary>
        public static List<string> Texts(DependencyObject root)
        {
            return FindAll<TextBlock>(root, t => t.IsVisible && !string.IsNullOrEmpty(t.Text)).Select(t => t.Text).ToList();
        }

        public static string ContentText(object content)
        {
            if (content == null)
                return null;
            var text = content as string;
            if (text != null)
                return text;
            var block = content as TextBlock;
            if (block != null)
                return block.Text;
            var element = content as DependencyObject;
            return element == null ? content.ToString() : string.Concat(FindAll<TextBlock>(element).Select(t => t.Text));
        }

        /// <summary>메뉴 머리글(접근 키 '_' 뺌).</summary>
        public static string HeaderText(MenuItem item)
        {
            return (ContentText(item.Header) ?? "").Replace("_", "");
        }

        // ---------- 조작 ----------

        public static void Click(ButtonBase button)
        {
            if (button == null)
                throw new InvalidOperationException("누를 버튼이 없습니다.");
            var peer = UIElementAutomationPeer.CreatePeerForElement(button);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
        }

        /// <summary>
        /// 체크 상자를 사람이 누른 것처럼(값이 바뀌고 Click 처리기가 돈다). 자동화 Toggle 패턴은 Click을 내지 않아 Click에 묶인 저장이 돌지 않는다.
        /// </summary>
        public static void ClickToggle(ToggleButton toggle)
        {
            if (toggle == null)
                throw new InvalidOperationException("누를 체크 상자가 없습니다.");
            typeof(ButtonBase).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(toggle, null);
        }

        /// <summary>요소에서 Enter 키를 누른 것처럼 KeyDown을 낸다(숨은 데스크톱에는 키보드 포커스가 없다).</summary>
        public static void PressEnter(UIElement element)
        {
            var source = PresentationSource.FromVisual(element);
            element.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, 0, System.Windows.Input.Key.Enter)
            {
                RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent
            });
        }

        public static void Toggle(ToggleButton toggle, bool on)
        {
            if (toggle.IsChecked != on)
                toggle.IsChecked = on;
        }

        public static void Invoke(MenuItem item)
        {
            if (item == null)
                throw new InvalidOperationException("누를 메뉴 항목이 없습니다.");
            var peer = UIElementAutomationPeer.CreatePeerForElement(item);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
        }

        /// <summary>메뉴 막대의 메뉴 경로(머리글이 이 글로 시작)를 따라 마지막 항목을 누른다: "도구", "일일 점검".</summary>
        public static void Menu(DependencyObject root, params string[] path)
        {
            ItemsControl current = Find<System.Windows.Controls.Menu>(root);
            for (var i = 0; i < path.Length; i++)
            {
                var item = current.Items.OfType<MenuItem>().FirstOrDefault(m => HeaderText(m).StartsWith(path[i], StringComparison.Ordinal));
                if (item == null)
                    throw new InvalidOperationException("메뉴 '" + path[i] + "'가 없습니다. 있는 것: " + string.Join(" · ", current.Items.OfType<MenuItem>().Select(HeaderText)));
                if (i == path.Length - 1)
                {
                    Invoke(item);
                    return;
                }
                item.IsSubmenuOpen = true;
                current = item;
            }
        }

        /// <summary>요소의 오른쪽 메뉴를 열고(Opened 처리기가 돈다) 머리글이 이 글로 시작하는 항목을 누른다. 항목 목록을 돌려준다.</summary>
        public static List<string> ContextMenu(FrameworkElement element, string itemStartsWith)
        {
            var menu = element.ContextMenu;
            if (menu == null)
                throw new InvalidOperationException("오른쪽 메뉴가 없는 요소입니다.");
            menu.PlacementTarget = element;
            menu.IsOpen = true;
            var items = menu.Items.OfType<MenuItem>().Where(m => m.Visibility == Visibility.Visible).ToList();
            var names = items.Select(HeaderText).ToList();
            if (itemStartsWith != null)
            {
                var item = items.FirstOrDefault(m => HeaderText(m).StartsWith(itemStartsWith, StringComparison.Ordinal));
                if (item == null)
                {
                    menu.IsOpen = false;
                    throw new InvalidOperationException("메뉴 항목 '" + itemStartsWith + "'가 없습니다. 있는 것: " + string.Join(" · ", names));
                }
                if (!item.IsEnabled)
                {
                    menu.IsOpen = false;
                    throw new InvalidOperationException("메뉴 항목 '" + itemStartsWith + "'가 꺼져 있습니다.");
                }
                menu.IsOpen = false;
                Invoke(item);
            }
            else
            {
                menu.IsOpen = false;
            }
            return names;
        }

        public static void Select(Selector selector, int index)
        {
            selector.SelectedIndex = index;
        }

        /// <summary>콤보의 항목 가운데 글이 이것인 것을 고른다.</summary>
        public static void Choose(ComboBox combo, string text)
        {
            for (var i = 0; i < combo.Items.Count; i++)
            {
                var item = combo.Items[i];
                var label = item is ComboBoxItem cbi ? ContentText(cbi.Content) : ContentText(item);
                if (label == text)
                {
                    combo.SelectedIndex = i;
                    return;
                }
            }
            throw new InvalidOperationException("콤보에 '" + text + "'가 없습니다.");
        }

        // ---------- 캡처 ----------

        /// <summary>창 내용을 PNG로(창 배경 위에 그린다 — RenderTargetBitmap은 화면 합성과 무관해 숨은 데스크톱에서도 같다).</summary>
        public static string Snap(Window window, string folder, string name)
        {
            var content = window.Content as FrameworkElement;
            if (content == null)
                throw new InvalidOperationException("창 내용이 없습니다.");
            content.UpdateLayout();
            var width = Math.Max(1, (int)Math.Ceiling(content.ActualWidth));
            var height = Math.Max(1, (int)Math.Ceiling(content.ActualHeight));
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(window.Background ?? Brushes.Black, null, new Rect(0, 0, width, height));
                dc.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, width, height));
            }
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.Combine(folder, name + ".png");
            using (var stream = File.Create(path))
                encoder.Save(stream);
            return path;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using Folderss.Plugins;

namespace PluginPreview
{
    /// <summary>
    /// 플러그인만 띄우는 미리보기. 사용: PluginPreview &lt;plugin.zip&gt; &lt;작업 폴더&gt; [테마].
    /// 작업 폴더의 plugin-data에 설정·임시 저장이 남아 다시 띄워도 이어진다(사용 중인 Folderss의 데이터는 건드리지 않음).
    /// </summary>
    internal static class Program
    {
        private static readonly string[] Themes = { "Black", "Light", "GitHub", "Nord", "Solarized", "Dracula", "Catppuccin" };

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length < 2)
            {
                MessageBox.Show("사용: PluginPreview <plugin.zip> <작업 폴더> [테마: " + string.Join(", ", Themes) + "]", "미리보기");
                return 2;
            }
            var zip = Path.GetFullPath(args[0]);
            var work = Path.GetFullPath(args[1]);
            var theme = Themes.FirstOrDefault(t => args.Length > 2 && string.Equals(t, args[2], StringComparison.OrdinalIgnoreCase)) ?? "Black";

            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            // Folderss App.xaml과 같은 순서: 색 사전 → 컨트롤 스타일
            app.Resources.MergedDictionaries.Add(LoadTheme(theme));
            app.Resources.MergedDictionaries.Add(LoadTheme("Controls"));
            app.DispatcherUnhandledException += (s, e) =>
            {
                MessageBox.Show(e.Exception.ToString(), "미리보기 — 처리되지 않은 예외");
                e.Handled = true;
            };

            var pluginDir = Extract(zip, work);
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(pluginDir, "plugin.json")));
            var id = manifest.RootElement.GetProperty("id").GetString();
            var name = manifest.RootElement.TryGetProperty("name", out var nameProperty) ? nameProperty.GetString() : id;
            var assemblyPath = Path.Combine(pluginDir, manifest.RootElement.GetProperty("assembly").GetString());
            var context = new PluginLoadContext(id, assemblyPath);
            var type = context.LoadFromAssemblyPath(assemblyPath).GetType(manifest.RootElement.GetProperty("type").GetString(), true);
            var manager = new PreviewPluginManager(id, pluginDir, Path.Combine(work, "plugin-data"), theme);
            var plugin = (IFolderssPlugin)Activator.CreateInstance(type);
            plugin.Initialize(manager);

            // Folderss에서는 설정 창의 플러그인 섹션에 나오는 탭(IPluginSettingsPage) — 미리보기는 위 막대의 버튼으로 연다
            var bar = new DockPanel { Margin = new Thickness(8, 4, 8, 4), LastChildFill = false };
            var settings = new Button { Content = "플러그인 설정…", Padding = new Thickness(10, 2, 10, 2), IsEnabled = manager.SettingsPages.Count > 0 };
            settings.Click += (s, e) => ShowSettings(manager, name);
            DockPanel.SetDock(settings, Dock.Right);
            bar.Children.Add(settings);
            var note = new TextBlock { Text = "미리보기 — Folderss 없이 플러그인만. 데이터: " + manager.DataDirectory, VerticalAlignment = VerticalAlignment.Center };
            note.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
            bar.Children.Add(note);
            var root = new DockPanel();
            DockPanel.SetDock(bar, Dock.Top);
            root.Children.Add(bar);
            root.Children.Add(plugin.CreateView());

            var window = new Window
            {
                Title = name + " — 미리보기 (테마 " + theme + ")",
                Content = root,
                Width = 1400,
                Height = 900,
                WindowStartupLocation = WindowStartupLocation.CenterScreen
            };
            window.SetResourceReference(Window.BackgroundProperty, "WindowBackground");
            window.SetResourceReference(Window.ForegroundProperty, "PrimaryText");
            return app.Run(window);
        }

        /// <summary>
        /// zip을 실행마다 새 폴더에 푼다 — 앞서 띄운 미리보기가 아직 열려 있으면 그 DLL은 잠겨 있다. 잠기지 않은 옛 폴더는 지운다.
        /// </summary>
        private static string Extract(string zip, string work)
        {
            Directory.CreateDirectory(work);
            foreach (var old in Directory.GetDirectories(work, "plugin-*"))
            {
                try
                {
                    Directory.Delete(old, true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
            var folder = Path.Combine(work, "plugin-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            ZipFile.ExtractToDirectory(zip, folder);
            return folder;
        }

        private static void ShowSettings(PreviewPluginManager manager, string name)
        {
            // Folderss 설정 창처럼 왼쪽 목록(탭 이름) + 오른쪽 내용. 화면은 창을 열 때마다 새로 만든다(계약: CreateView는 매번 새 요소)
            var views = manager.SettingsPages.Select(p => new ScrollViewer { Content = p.CreateView(), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(12) }).ToList();
            var list = new ListBox { Width = 180, Margin = new Thickness(8, 8, 0, 8), ItemsSource = manager.SettingsPages.Select(p => p.Title).ToList() };
            var content = new Border { Margin = new Thickness(8), BorderThickness = new Thickness(1) };
            content.SetResourceReference(Border.BackgroundProperty, "PanelBackground");
            content.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            list.SelectionChanged += (s, e) => content.Child = list.SelectedIndex >= 0 ? views[list.SelectedIndex] : null;
            list.SelectedIndex = 0;
            var pages = new DockPanel();
            DockPanel.SetDock(list, Dock.Left);
            pages.Children.Add(list);
            pages.Children.Add(content);
            var save = new Button { Content = "저장", Padding = new Thickness(16, 3, 16, 3), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var close = new Button { Content = "닫기", Padding = new Thickness(16, 3, 16, 3), IsCancel = true };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
            buttons.Children.Add(save);
            buttons.Children.Add(close);
            var dock = new DockPanel();
            DockPanel.SetDock(buttons, Dock.Bottom);
            dock.Children.Add(buttons);
            dock.Children.Add(pages);
            var dialog = new Window
            {
                Title = name + " 설정 — 미리보기",
                Content = dock,
                Width = 900,
                Height = 720,
                Owner = Application.Current.MainWindow,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            dialog.SetResourceReference(Window.BackgroundProperty, "WindowBackground");
            dialog.SetResourceReference(Window.ForegroundProperty, "PrimaryText");
            save.Click += (s, e) =>
            {
                // Folderss 설정 창처럼: 탭마다 Save, 실패(예외)는 모아서 알리고 창은 그대로
                var failures = new List<string>();
                foreach (var page in manager.SettingsPages)
                {
                    try
                    {
                        page.Save();
                    }
                    catch (Exception ex)
                    {
                        failures.Add(page.Title + ": " + ex.Message);
                    }
                }
                if (failures.Count == 0)
                    dialog.Close();
                else
                    MessageBox.Show(dialog, string.Join(Environment.NewLine, failures), "설정을 저장하지 못했습니다");
            };
            dialog.ShowDialog();
        }

        private static ResourceDictionary LoadTheme(string name)
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Themes." + name + ".xaml")
                ?? throw new InvalidOperationException("테마 리소스가 없습니다: " + name);
            return (ResourceDictionary)XamlReader.Load(stream);
        }
    }

    /// <summary>Folderss PluginManager.PluginLoadContext와 같다: 계약은 기본 컨텍스트에서, 나머지·네이티브 DLL은 플러그인의 .deps.json으로.</summary>
    internal sealed class PluginLoadContext : AssemblyLoadContext
    {
        private static readonly string ContractName = typeof(IFolderssPlugin).Assembly.GetName().Name;
        private readonly AssemblyDependencyResolver _resolver;

        public PluginLoadContext(string pluginId, string assemblyPath) : base("plugin:" + pluginId)
        {
            _resolver = new AssemblyDependencyResolver(assemblyPath);
        }

        protected override Assembly Load(AssemblyName assemblyName)
        {
            if (string.Equals(assemblyName.Name, ContractName, StringComparison.OrdinalIgnoreCase))
                return null;
            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path != null ? LoadFromAssemblyPath(path) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path != null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
        }
    }

    /// <summary>미리보기용 <see cref="IPluginManager"/>: 설정은 데이터 폴더의 settings.json(Folderss와 같은 평평한 문자열 사전) — 다시 띄워도 남는다. 본체 설정은 테마만.</summary>
    internal sealed class PreviewPluginManager : IPluginManager
    {
        private readonly Dictionary<string, string> _settings;
        private readonly object _gate = new object();
        private readonly string _file;
        private readonly string _theme;

        public PreviewPluginManager(string id, string pluginDirectory, string dataDirectory, string theme)
        {
            PluginId = id;
            PluginDirectory = pluginDirectory;
            DataDirectory = dataDirectory;
            _theme = theme;
            Directory.CreateDirectory(dataDirectory);
            _file = Path.Combine(dataDirectory, "settings.json");
            _settings = File.Exists(_file)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_file)) ?? new Dictionary<string, string>()
                : new Dictionary<string, string>();
        }

        public string PluginId { get; }
        public string PluginDirectory { get; }
        public string DataDirectory { get; }
        public List<IPluginSettingsPage> SettingsPages { get; } = new List<IPluginSettingsPage>();

        public string GetSetting(string key)
        {
            lock (_gate)
                return _settings.TryGetValue(key, out var value) ? value : null;
        }

        public void SetSetting(string key, string value)
        {
            lock (_gate)
            {
                if (value == null)
                    _settings.Remove(key);
                else
                    _settings[key] = value;
                File.WriteAllText(_file, JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
            }
        }

        public IReadOnlyDictionary<string, string> GetAllSettings()
        {
            lock (_gate)
                return new Dictionary<string, string>(_settings);
        }

        public IReadOnlyDictionary<string, string> GetAppSettings()
        {
            return new Dictionary<string, string> { { "theme", _theme } };
        }

        public IFolderPanel CreateFolderPanel(string path)
        {
            throw new NotSupportedException("미리보기는 폴더 패널을 주지 않습니다.");
        }

        public void AddSettingsPage(IPluginSettingsPage page)
        {
            SettingsPages.Add(page);
        }
    }
}

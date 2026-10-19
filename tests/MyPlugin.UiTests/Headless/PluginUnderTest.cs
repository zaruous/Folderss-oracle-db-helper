using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using Folderss.Plugins;

namespace MyPlugin.UiTests.Headless
{
    /// <summary>
    /// 시험할 플러그인(프로세스에 하나 — Folderss처럼 한 번 로드). zip을 풀어 Folderss와 같은 <see cref="PluginLoadContext"/>로 부르고
    /// <see cref="IFolderssPlugin.Initialize"/>에 시험용 <see cref="TestPluginManager"/>를 준다. 플러그인 형식은 다른 로드 컨텍스트에 있으므로
    /// 시험은 WPF 기본 형식·이름·AutomationId로만 화면을 찾는다(내부 형식을 직접 쓰지 않음 — Folderss 안과 같은 조건).
    /// </summary>
    internal sealed class PluginUnderTest
    {
        private static PluginUnderTest _instance;
        private static readonly object Gate = new object();

        private PluginUnderTest(UiThread ui)
        {
            var root = Path.Combine(HeadlessEnvironment.WorkFolder, "run-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            PluginDirectory = Path.Combine(root, "plugin");
            ZipFile.ExtractToDirectory(HeadlessEnvironment.PluginZip, PluginDirectory);
            using (var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(PluginDirectory, "plugin.json"))))
            {
                Id = manifest.RootElement.GetProperty("id").GetString();
                var assemblyPath = Path.Combine(PluginDirectory, manifest.RootElement.GetProperty("assembly").GetString());
                Context = new PluginLoadContext(Id, assemblyPath);
                Assembly = Context.LoadFromAssemblyPath(assemblyPath);
                var type = Assembly.GetType(manifest.RootElement.GetProperty("type").GetString(), true);
                Manager = new TestPluginManager(Id, PluginDirectory, Path.Combine(root, "plugin-data"));
                Plugin = ui.Run(() =>
                {
                    var plugin = (IFolderssPlugin)Activator.CreateInstance(type);
                    plugin.Initialize(Manager);
                    return plugin;
                });
            }
            ShotFolder = Path.Combine(root, "shots");
            Directory.CreateDirectory(ShotFolder);
        }

        public static PluginUnderTest Load(UiThread ui)
        {
            lock (Gate)
                return _instance ?? (_instance = new PluginUnderTest(ui));
        }

        public string Id { get; }
        public string PluginDirectory { get; }
        public string ShotFolder { get; }
        public PluginLoadContext Context { get; }
        public Assembly Assembly { get; }
        public IFolderssPlugin Plugin { get; }
        public TestPluginManager Manager { get; }

        /// <summary>
        /// ⋯ 메뉴 > 플러그인으로 연 것처럼 <see cref="IFolderssPlugin.CreateView"/>를 팝업 창에 담아 보인다(UI 스레드에서 부른다).
        /// 숨은 데스크톱이라 사용자 화면에는 보이지 않는다.
        /// </summary>
        public Window OpenView(string title = "DB Helper")
        {
            var window = new Window
            {
                Title = title,
                Content = Plugin.CreateView(),
                Width = 1400,
                Height = 900,
                Left = 0,
                Top = 0,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            window.SetResourceReference(Window.BackgroundProperty, "WindowBackground");
            window.SetResourceReference(Window.ForegroundProperty, "PrimaryText");
            window.Show();
            return window;
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

        /// <summary>이 컨텍스트가 불러온 네이티브 DLL 경로(시험이 e_sqlite3 같은 것을 확인한다).</summary>
        public List<string> NativeLoads { get; } = new List<string>();

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
            if (path == null)
                return IntPtr.Zero;
            lock (NativeLoads)
                NativeLoads.Add(path);
            return LoadUnmanagedDllFromPath(path);
        }
    }

    /// <summary>시험용 <see cref="IPluginManager"/>: 설정은 데이터 폴더의 settings.json(Folderss와 같은 평평한 문자열 사전), 본체 설정은 테마만.</summary>
    internal sealed class TestPluginManager : IPluginManager
    {
        private static readonly byte[] PasswordEntropy = Encoding.UTF8.GetBytes("Folderss.OracleHelper.v1");
        private readonly Dictionary<string, string> _settings = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly object _gate = new object();

        public TestPluginManager(string id, string pluginDirectory, string dataDirectory)
        {
            PluginId = id;
            PluginDirectory = pluginDirectory;
            DataDirectory = dataDirectory;
            Directory.CreateDirectory(dataDirectory);
        }

        public string PluginId { get; }
        public string PluginDirectory { get; }
        public string DataDirectory { get; }
        public List<IPluginSettingsPage> SettingsPages { get; } = new List<IPluginSettingsPage>();

        public string GetSetting(string key)
        {
            lock (_gate)
            {
                string value;
                return _settings.TryGetValue(key, out value) ? value : null;
            }
        }

        public void SetSetting(string key, string value)
        {
            lock (_gate)
            {
                if (value == null)
                    _settings.Remove(key);
                else
                    _settings[key] = value;
                File.WriteAllText(Path.Combine(DataDirectory, "settings.json"), JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
            }
        }

        public IReadOnlyDictionary<string, string> GetAllSettings()
        {
            lock (_gate)
                return new Dictionary<string, string>(_settings);
        }

        public IReadOnlyDictionary<string, string> GetAppSettings()
        {
            return new Dictionary<string, string> { { "theme", "Black" } };
        }

        public IFolderPanel CreateFolderPanel(string path)
        {
            throw new NotSupportedException("헤드리스 시험 호스트는 폴더 패널을 주지 않습니다.");
        }

        public void AddSettingsPage(IPluginSettingsPage page)
        {
            SettingsPages.Add(page);
        }

        /// <summary>접속 정보를 플러그인 저장 형식(connections 설정, camelCase JSON, DPAPI 비밀번호)으로 넣는다. password가 null이면 저장 안 함.</summary>
        public void SeedConnections(params SeedConnection[] connections)
        {
            var items = connections.Select(c => new Dictionary<string, object>
            {
                { "id", c.Id }, { "name", c.Name }, { "provider", c.Provider }, { "host", c.Host }, { "port", c.Port }, { "serviceName", c.Service },
                { "userId", c.User }, { "protectedPassword", c.Password == null ? null : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(c.Password), PasswordEntropy, DataProtectionScope.CurrentUser)) },
                { "readOnly", c.ReadOnly }, { "color", c.Color ?? "" }
            }).ToList();
            SetSetting("connections", JsonSerializer.Serialize(items));
        }
    }

    internal sealed class SeedConnection
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; }
        public string Provider { get; set; } = "oracle";
        public string Host { get; set; } = "localhost";
        public int Port { get; set; } = 1521;
        public string Service { get; set; }
        public string User { get; set; }
        public string Password { get; set; }
        public bool ReadOnly { get; set; }
        public string Color { get; set; }

        /// <summary>"host:port/service" → 접속 하나.</summary>
        public static SeedConnection Oracle(string name, string dsn, string user, string password, string color = "")
        {
            var hostPort = dsn.Split('/')[0];
            return new SeedConnection
            {
                Name = name, Host = hostPort.Split(':')[0], Port = int.Parse(hostPort.Split(':')[1]), Service = dsn.Split('/')[1],
                User = user, Password = password, Color = color
            };
        }
    }
}

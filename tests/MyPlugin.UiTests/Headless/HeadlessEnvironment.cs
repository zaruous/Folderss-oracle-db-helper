using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Xunit;

namespace MyPlugin.UiTests.Headless
{
    /// <summary>
    /// 헤드리스 시험의 실행 조건. 사용자 화면에 창이 뜨면 안 되므로 <b>숨은 데스크톱</b>(이름이 Default가 아닌 데스크톱)에서만 돈다 —
    /// scripts/ui-test.ps1이 CreateDesktop으로 만든 데스크톱에서 dotnet test를 실행한다(SwitchDesktop은 하지 않아 사용자 화면은 그대로).
    /// </summary>
    internal static class HeadlessEnvironment
    {
        private const int UoiName = 2;

        [DllImport("user32.dll")]
        private static extern IntPtr GetThreadDesktop(uint threadId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetUserObjectInformationW(IntPtr handle, int index, StringBuilder buffer, int length, out int needed);

        /// <summary>이 스레드가 붙은 데스크톱 이름("Default"면 사용자 화면).</summary>
        public static string DesktopName()
        {
            var buffer = new StringBuilder(256);
            int needed;
            return GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()), UoiName, buffer, buffer.Capacity * 2, out needed) ? buffer.ToString() : "";
        }

        public static bool OnHiddenDesktop
        {
            get
            {
                var name = DesktopName();
                return name.Length > 0 && !string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>시험할 플러그인 zip(FMON_UI_PLUGIN_ZIP, 없으면 저장소의 Release publish 결과).</summary>
        public static string PluginZip
        {
            get
            {
                var path = Environment.GetEnvironmentVariable("FMON_UI_PLUGIN_ZIP");
                if (!string.IsNullOrEmpty(path))
                    return path;
                var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
                return Path.Combine(root, "src", "MyPlugin", "bin", "Release", "net8.0-windows", "win-x64", "MyPlugin.zip");
            }
        }

        /// <summary>시험이 쓰는 폴더(데이터·캡처). FMON_UI_WORK, 없으면 임시 폴더 아래.</summary>
        public static string WorkFolder
        {
            get
            {
                var path = Environment.GetEnvironmentVariable("FMON_UI_WORK");
                if (string.IsNullOrEmpty(path))
                    path = Path.Combine(Path.GetTempPath(), "folderss-uitest");
                Directory.CreateDirectory(path);
                return path;
            }
        }

        /// <summary>실제 Oracle(host:port/service) — FMON_UI_ORACLE. 계정은 FMON_UI_ORACLE_USER/PW(기본 fmon_dba).</summary>
        public static string Oracle { get { return Environment.GetEnvironmentVariable("FMON_UI_ORACLE"); } }
        public static string OracleUser { get { return Environment.GetEnvironmentVariable("FMON_UI_ORACLE_USER") ?? "fmon_dba"; } }
        public static string OraclePassword { get { return Environment.GetEnvironmentVariable("FMON_UI_ORACLE_PW") ?? "Fmon_2026x"; } }
    }

    /// <summary>숨은 데스크톱이고 플러그인 zip이 있을 때만 도는 시험. requiresOracle이면 FMON_UI_ORACLE도 있어야 한다.</summary>
    public sealed class UiFactAttribute : FactAttribute
    {
        public UiFactAttribute(bool requiresOracle = false)
        {
            if (!HeadlessEnvironment.OnHiddenDesktop)
                Skip = "헤드리스 시험은 숨은 데스크톱에서만 돕니다(scripts/ui-test.ps1). 지금 데스크톱: " + HeadlessEnvironment.DesktopName();
            else if (!File.Exists(HeadlessEnvironment.PluginZip))
                Skip = "플러그인 zip이 없습니다: " + HeadlessEnvironment.PluginZip + " (dotnet publish src/MyPlugin -c Release)";
            else if (requiresOracle && string.IsNullOrEmpty(HeadlessEnvironment.Oracle))
                Skip = "FMON_UI_ORACLE(host:port/service)이 없어 실제 Oracle 시험을 건너뜁니다.";
        }
    }
}

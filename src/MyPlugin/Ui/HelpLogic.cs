using System;
using System.IO;

namespace MyPlugin
{
    /// <summary>
    /// 도움말 페이지(플러그인 폴더의 help\*.html — 배포 zip에 함께 들어간다). 창 안의 WebBrowser로 보이고, 없으면 GitHub Pages 주소로 연다.
    /// 팝업·메뉴의 긴 설명은 코드에 두지 않고 이 HTML에서 관리한다.
    /// </summary>
    internal static class HelpLogic
    {
        public const string FolderName = "help";
        public const string Index = "index.html";
        public const string Connections = "connections.html";
        public const string TableSpec = "table-spec.html";
        public const string CodeSpec = "gcm-spec.html";
        public const string Monitor = "monitor.html";

        /// <summary>온라인 사본(플러그인 폴더에 파일이 없을 때).</summary>
        public const string OnlineBase = "https://zaruous.github.io/Folderss-oracle-db-helper/help/";

        /// <summary>플러그인 폴더의 도움말 폴더. 폴더를 모르면 null.</summary>
        public static string Folder(string pluginDirectory)
        {
            return string.IsNullOrEmpty(pluginDirectory) ? null : Path.Combine(pluginDirectory, FolderName);
        }

        /// <summary>그 페이지 파일의 전체 경로. 폴더를 모르거나 파일이 없으면 null(온라인으로 연다).</summary>
        public static string LocalPath(string helpFolder, string page)
        {
            if (string.IsNullOrEmpty(helpFolder) || string.IsNullOrEmpty(page))
                return null;
            var path = Path.Combine(helpFolder, page);
            return File.Exists(path) ? path : null;
        }

        /// <summary>창에 띄울 주소: 로컬 파일이 있으면 file://, 없으면 온라인.</summary>
        public static Uri PageUri(string helpFolder, string page)
        {
            var local = LocalPath(helpFolder, page ?? Index);
            return local != null ? new Uri(local) : new Uri(OnlineBase + (page ?? Index));
        }

        /// <summary>창 제목: "DB Helper 도움말 — GCM 정의서".</summary>
        public static string Title(string page)
        {
            switch (page)
            {
                case Connections: return "DB Helper 도움말 — 접속";
                case TableSpec: return "DB Helper 도움말 — 테이블 정의서";
                case CodeSpec: return "DB Helper 도움말 — GCM 정의서";
                case Monitor: return "DB Helper 도움말 — 성능 모니터";
                default: return "DB Helper 도움말";
            }
        }
    }
}

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    /// <summary>도움말: 플러그인 폴더의 help\*.html이 있고 서로 잇는 링크·스타일이 깨지지 않았는지, 경로·제목 로직.</summary>
    public class HelpTests
    {
        private static string HelpFolder()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "MyPlugin", "help", HelpLogic.Index)))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(dir.FullName, "src", "MyPlugin", "help");
        }

        [Fact]
        public void Pages_Exist_AndLocalLinksResolve()
        {
            var folder = HelpFolder();
            foreach (var page in new[] { HelpLogic.Index, HelpLogic.Connections, HelpLogic.TableSpec, HelpLogic.CodeSpec })
            {
                var path = HelpLogic.LocalPath(folder, page);
                Assert.True(path != null, page + " 없음");
                var html = File.ReadAllText(path);
                Assert.Contains("<meta charset=\"utf-8\">", html);
                Assert.Contains("help.css", html);
                foreach (Match link in Regex.Matches(html, "href=\"([^\"#]+)\""))
                {
                    var target = link.Groups[1].Value;
                    if (target.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        continue;
                    Assert.True(File.Exists(Path.Combine(folder, target)), page + " → " + target + " 없음");
                }
            }
            Assert.True(File.Exists(Path.Combine(folder, "help.css")));
        }

        [Fact]
        public void CodeSpecPage_MentionsEveryAlias_AndPairRule()
        {
            var html = File.ReadAllText(Path.Combine(HelpFolder(), HelpLogic.CodeSpec));
            foreach (var alias in CodeSpecContract.Aliases)
                Assert.Contains("<code>" + alias.Name + "</code>", html);
            Assert.Contains("X_NAME", html);
            Assert.Contains("groupAttrValues", html);
        }

        [Fact]
        public void Paths_Titles_Online()
        {
            Assert.Null(HelpLogic.Folder(null));
            Assert.Equal(Path.Combine("p", "help"), HelpLogic.Folder("p"));
            Assert.Null(HelpLogic.LocalPath(null, HelpLogic.Index));
            Assert.Null(HelpLogic.LocalPath(Path.Combine(Path.GetTempPath(), "no-such-help"), HelpLogic.Index));
            Assert.Equal(HelpLogic.OnlineBase + HelpLogic.CodeSpec, HelpLogic.PageUri(null, HelpLogic.CodeSpec).AbsoluteUri);
            Assert.Equal(HelpLogic.OnlineBase + HelpLogic.Index, HelpLogic.PageUri(null, null).AbsoluteUri);
            Assert.True(HelpLogic.PageUri(HelpFolder(), HelpLogic.Index).IsFile);
            Assert.Equal("DB Helper 도움말 — GCM 정의서", HelpLogic.Title(HelpLogic.CodeSpec));
            Assert.Equal("DB Helper 도움말", HelpLogic.Title(HelpLogic.Index));
        }
    }
}

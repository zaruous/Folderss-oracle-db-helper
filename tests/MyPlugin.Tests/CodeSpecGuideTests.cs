using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    /// <summary>
    /// GCM 정의서 양식 작성 지침(docs/code-spec-templates)이 코드와 맞는지: variables.json의 변수 = 모델(CodeSpecModel.Context)이 실제로 넣는 값,
    /// guide.md가 모든 변수·양식 폴더·별칭 계약을 적었는지, 지침 주소.
    /// </summary>
    public class CodeSpecGuideTests
    {
        // 지침에서 그 종류의 변수를 적을 때 쓰는 이름($group.id 등)
        private static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>
        {
            { "doc", "$doc" }, { "db", "$db" }, { "source", "$source" }, { "group", "$group" }, { "code", "$code" }, { "foreach", "$foreach" }
        };

        internal static string DocsFolder()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "docs", "code-spec-templates", "variables.json")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(dir.FullName, "docs", "code-spec-templates");
        }

        private static Dictionary<string, HashSet<string>> Documented()
        {
            using (var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(DocsFolder(), "variables.json"))))
            {
                var result = new Dictionary<string, HashSet<string>>();
                foreach (var kind in json.RootElement.GetProperty("objects").EnumerateObject())
                    result[kind.Name] = new HashSet<string>(kind.Value.EnumerateObject().Select(p => p.Name), StringComparer.Ordinal);
                return result;
            }
        }

        private static CodeSpecData Data()
        {
            var table = new QueryTable();
            table.Columns.AddRange(new[] { "TABLE_NAME", "CODE", "CODE_NAME", "PLANT_CD" });
            table.Rows.Add(new[] { "G1", "A", "에이", "P1" });
            return CodeSpecModel.Build(table, "gcm.sql", "SELECT 1");
        }

        private static IEnumerable<string> Keys(object map)
        {
            return ((Dictionary<string, object>)map).Keys;
        }

        // 모델이 실제로 넣는 키 = variables.json의 키(양쪽으로 같은지)
        [Fact]
        public void VariablesJson_MatchesModelKeysExactly()
        {
            var context = CodeSpecModel.Context(new TableSpecDocument(), new OracleConnectionProfile { Name = "n" }, Data(), "Oracle");
            object value;
            context.TryGet("doc", out value);
            var actual = new Dictionary<string, IEnumerable<string>> { { "doc", Keys(value) } };
            context.TryGet("db", out value);
            actual["db"] = Keys(value);
            context.TryGet("source", out value);
            actual["source"] = Keys(value);
            context.TryGet("groups", out value);
            var group = (Dictionary<string, object>)((List<object>)value)[0];
            actual["group"] = group.Keys;
            actual["code"] = Keys(((List<object>)group["codes"])[0]);
            // $codes의 항목도 같은 키
            context.TryGet("codes", out value);
            Assert.Equal(actual["code"].OrderBy(k => k, StringComparer.Ordinal), Keys(((List<object>)value)[0]).OrderBy(k => k, StringComparer.Ordinal));

            var documented = Documented();
            Assert.Equal(new[] { "code", "db", "doc", "foreach", "group", "source" }, documented.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
            foreach (var pair in actual)
            {
                var missing = pair.Value.Where(k => !documented[pair.Key].Contains(k)).ToList();
                var stale = documented[pair.Key].Where(k => !pair.Value.Contains(k)).ToList();
                Assert.True(missing.Count == 0, pair.Key + " 문서에 없는 변수: " + string.Join(", ", missing));
                Assert.True(stale.Count == 0, pair.Key + " 모델에 없는 변수(문서만): " + string.Join(", ", stale));
            }
            Assert.Equal(new[] { "count", "first", "hasNext", "index", "last" }, documented["foreach"].OrderBy(k => k, StringComparer.Ordinal).ToArray());
        }

        [Fact]
        public void GuideMarkdown_MentionsEveryVariableFolderAndAliases()
        {
            var guide = File.ReadAllText(Path.Combine(DocsFolder(), "guide.md"));
            var missing = new List<string>();
            foreach (var kind in Documented())
            {
                foreach (var member in kind.Value)
                {
                    var name = Aliases[kind.Key] + "." + member;
                    if (!guide.Contains(name))
                        missing.Add(name);
                }
            }
            Assert.True(missing.Count == 0, "guide.md에 없는 변수: " + string.Join(", ", missing));
            // CodeSpecModel.Variables의 이름도 모두 있다("$a / $b" 꼴은 나눠서)
            var variables = CodeSpecModel.Variables.SelectMany(v => v.Name.Split('/')).Select(n => n.Trim()).Where(n => n.StartsWith("$", StringComparison.Ordinal))
                .Where(n => !guide.Contains(n)).ToList();
            Assert.True(variables.Count == 0, "guide.md에 없는 변수(Variables): " + string.Join(", ", variables));

            Assert.Contains(@"templates\" + CodeSpecTemplates.FolderName, guide);
            Assert.Contains(TableSpecTemplates.FolderName + @"\" + CodeSpecTemplates.FolderName, guide);
            foreach (var alias in CodeSpecContract.Aliases)
            {
                Assert.Contains("`" + alias.Name + "`", guide);
                Assert.Contains(alias.Description, guide);
            }
            Assert.Contains("$code.attrs.열이름", guide);
            Assert.Contains("$code.attrValues", guide);
            Assert.Contains("$code.attrText", guide);
            Assert.Contains("../table-spec-templates/guide.md", guide);
            // 출력 형식: CSV 머리글의 열 이름
            foreach (var column in CodeSpecModel.CsvHeader(null))
                Assert.Contains(column, guide);
        }

        [Fact]
        public void GuideUrls_PointToPublishedFiles()
        {
            Assert.EndsWith("/", CodeSpecTemplates.GuideUrl);
            Assert.Equal(CodeSpecTemplates.GuideUrl + "guide.md", CodeSpecTemplates.GuideMarkdownUrl);
            Assert.EndsWith("/code-spec-templates/", CodeSpecTemplates.GuideUrl);
            Assert.True(File.Exists(Path.Combine(DocsFolder(), "guide.md")));
            Assert.True(File.Exists(Path.Combine(DocsFolder(), "variables.json")));
        }
    }
}

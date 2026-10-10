using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ClosedXML.Excel;
using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    /// <summary>
    /// 양식 작성 지침(docs/table-spec-templates)이 코드와 맞는지: variables.json의 변수 = 모델(TableSpecModel)이 실제로 넣는 값,
    /// guide.md가 모든 변수를 적었는지. TABLESPEC_TEMPLATE이 있으면 그 양식(예: 지침의 예제 스크립트로 만든 것)을 그려 본다.
    /// </summary>
    public class TemplateGuideTests
    {
        // 지침에서 그 종류의 변수를 적을 때 쓰는 이름($table.name 등)
        private static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>
        {
            { "doc", "$doc" }, { "db", "$db" }, { "table", "$table" }, { "col", "$col" }, { "idx", "$idx" }, { "con", "$con" }, { "code", "$code" }, { "foreach", "$foreach" }
        };

        private static string DocsFolder()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "docs", "table-spec-templates", "variables.json")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(dir.FullName, "docs", "table-spec-templates");
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

        private static TableDescription Table()
        {
            var t = new TableDescription { Owner = "SHOP", Name = "ORDERS", Type = "TABLE", Comment = "주문" };
            t.Columns.Add(new DescribedColumn { Column = new ColumnInfo { Name = "ID", TypeLabel = "NUMBER(10)", Position = 1 }, Comment = "번호" });
            t.Indexes.Add(new IndexDescription { Name = "PK_ORDERS", Unique = true, Columns = "ID" });
            var check = new ConstraintDescription { Name = "CK", Type = "C", Columns = "ID", Condition = "ID IN (1,2)" };
            check.ColumnNames.Add("ID");
            t.Constraints.Add(check);
            return t;
        }

        // 모델이 실제로 넣는 키 = variables.json의 키(양쪽으로 같은지)
        [Fact]
        public void VariablesJson_MatchesModelKeysExactly()
        {
            var context = TableSpecModel.Context(new TableSpecDocument(), new OracleConnectionProfile { Name = "n" }, new[] { Table() }, "Oracle");
            object value;
            context.TryGet("doc", out value);
            var actual = new Dictionary<string, IEnumerable<string>> { { "doc", Keys(value) } };
            context.TryGet("db", out value);
            actual["db"] = Keys(value);
            context.TryGet("tables", out value);
            var table = (Dictionary<string, object>)((List<object>)value)[0];
            actual["table"] = table.Keys;
            actual["col"] = Keys(((List<object>)table["columns"])[0]);
            actual["idx"] = Keys(((List<object>)table["indexes"])[0]);
            actual["con"] = Keys(((List<object>)table["constraints"])[0]);
            context.TryGet("codes", out value);
            actual["code"] = Keys(((List<object>)value)[0]);

            var documented = Documented();
            foreach (var pair in actual)
            {
                var missing = pair.Value.Where(k => !documented[pair.Key].Contains(k)).ToList();
                var stale = documented[pair.Key].Where(k => !pair.Value.Contains(k)).ToList();
                Assert.True(missing.Count == 0, pair.Key + " 문서에 없는 변수: " + string.Join(", ", missing));
                Assert.True(stale.Count == 0, pair.Key + " 모델에 없는 변수(문서만): " + string.Join(", ", stale));
            }
            Assert.Equal(new[] { "count", "first", "hasNext", "index", "last" }, documented["foreach"].OrderBy(k => k, StringComparer.Ordinal).ToArray());
        }

        private static IEnumerable<string> Keys(object map)
        {
            return ((Dictionary<string, object>)map).Keys;
        }

        [Fact]
        public void GuideMarkdown_MentionsEveryVariable()
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
            Assert.Contains(TableSpecTemplates.FolderName, guide);
        }

        [Fact]
        public void GuideUrls_PointToPublishedFiles()
        {
            Assert.EndsWith("/", TableSpecTemplates.GuideUrl);
            Assert.Equal(TableSpecTemplates.GuideUrl + "guide.md", TableSpecTemplates.GuideMarkdownUrl);
            Assert.True(File.Exists(Path.Combine(DocsFolder(), "guide.md")));
            Assert.True(File.Exists(Path.Combine(DocsFolder(), "examples", "make_template.py")));
            Assert.True(File.Exists(Path.Combine(DocsFolder(), "examples", "check_template.py")));
        }

        // TABLESPEC_TEMPLATE=양식.xlsx 로 돌리면 그 양식을 그려 본다(남은 ${…}·#foreach 글자가 없어야 한다)
        [Fact]
        public void ExternalTemplate_RendersWithoutLeftovers()
        {
            var path = Environment.GetEnvironmentVariable("TABLESPEC_TEMPLATE");
            if (string.IsNullOrEmpty(path))
                return;
            var output = new MemoryStream();
            ExcelTemplate.Render(File.ReadAllBytes(path), TableSpecModel.Context(new TableSpecDocument { Project = "시험" }, new OracleConnectionProfile { Name = "n" }, new[] { Table() }, "Oracle"), output);
            var outPath = Environment.GetEnvironmentVariable("TABLESPEC_TEMPLATE_OUT");
            if (!string.IsNullOrEmpty(outPath))
                File.WriteAllBytes(outPath, output.ToArray());
            output.Position = 0;
            using (var book = new XLWorkbook(output))
            {
                var leftovers = book.Worksheets.SelectMany(w => w.CellsUsed()).Where(c => !c.HasFormula && c.Value.IsText)
                    .Select(c => c.GetText()).Where(t => t.Contains("${") || t.Contains("#foreach") || t.Contains("#end")).ToList();
                Assert.True(leftovers.Count == 0, "그려지지 않은 글자: " + string.Join(" | ", leftovers));
                Assert.DoesNotContain(book.Worksheets, w => w.Name.StartsWith("_", StringComparison.Ordinal));
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using MyPlugin;
using Xunit;
using Xunit.Abstractions;

namespace MyPlugin.Tests
{
    /// <summary>
    /// LLM용 지침(guide.md)의 예제가 실제로 동작하는지:
    /// - guide.md 3~7장의 템플릿 예제(인라인 코드)를 모두 뽑아 문법·변수가 맞는지 엔진으로 그려 본다(지침을 고치면 예제도 같이 확인됨).
    /// - 예제 스크립트(make_template.py)와 지침 7.1의 코드를 실제로 돌려 만든 양식이 검사 도구(check_template.py)를 통과하고 엔진으로 그려지는지.
    /// - 검사 도구가 지침 9장의 실수를 잡는지.
    /// 파이썬 시험은 python과 openpyxl이 있을 때만 돈다(없으면 건너뜀 — CI는 설치해 둔다).
    /// </summary>
    public class GuideExamplesTests
    {
        private readonly ITestOutputHelper _output;

        public GuideExamplesTests(ITestOutputHelper output)
        {
            _output = output;
        }

        // ================= guide.md 안의 예제 =================

        // 문법 설명용 자리표시(식·…·$x·$list·$name·$a·$s, 한글 변수 이름)는 예제가 아니다
        private static readonly Regex Placeholder = new Regex(@"식|…|\$!?\{?(x|list|name|a|s|변수|목록|항목|이름)\b|\$목록|\$항목|\$이름|\$변수", RegexOptions.CultureInvariant);
        private static readonly Regex Candidate = new Regex(@"\$!?\{[A-Za-z_]|#\{?(if|foreach|set|sheet|elseif)\}?\s*\(|#\{(else|end)\}|^=[A-Za-z]", RegexOptions.CultureInvariant);
        private static readonly Regex Leftover = new Regex(@"\$!?\{|\$[A-Za-z_][A-Za-z0-9_]*\.[A-Za-z_]|#\{?(if|foreach|set|elseif|else|end)\b", RegexOptions.CultureInvariant);

        // 지침이 "이렇게 하면 안 된다"고 일부러 보여 주는 예(오타 등) — 예제가 아니다
        private static readonly string[] IntentionalMistakes = { "${col.nmae}" };

        /// <summary>guide.md 3~7장(4.4 "지원하지 않음" 빼고)의 인라인 코드 중 템플릿 예제.</summary>
        internal static List<string> GuideSnippets()
        {
            var snippets = new List<string>();
            var section = 0;
            var unsupported = false;
            foreach (var line in File.ReadAllLines(Path.Combine(GuideFiles.Folder(), "guide.md")))
            {
                var top = Regex.Match(line, @"^## (\d+)\.");
                if (top.Success)
                    section = int.Parse(top.Groups[1].Value);
                if (line.StartsWith("### ", StringComparison.Ordinal))
                    unsupported = line.StartsWith("### 4.4", StringComparison.Ordinal);
                if (section < 3 || section > 7 || unsupported)
                    continue;
                foreach (Match m in Regex.Matches(line, "`([^`]+)`"))
                {
                    var code = m.Groups[1].Value;
                    if (Candidate.IsMatch(code) && !Placeholder.IsMatch(code) && !IntentionalMistakes.Contains(code) && !snippets.Contains(code))
                        snippets.Add(code);
                }
            }
            return snippets;
        }

        [Fact]
        public void GuideSnippets_AllRenderWithRealVariables()
        {
            var snippets = GuideSnippets();
            Assert.True(snippets.Count >= 20, "guide.md에서 뽑은 예제가 너무 적습니다(" + snippets.Count + "개) — 뽑는 규칙을 확인하세요");
            var failures = new List<string>();
            foreach (var snippet in snippets)
            {
                try
                {
                    var result = RenderSnippet(snippet);
                    if (Leftover.IsMatch(result))
                        failures.Add(snippet + " → 그려지지 않은 글자: " + result);
                }
                catch (TemplateException ex)
                {
                    failures.Add(snippet + " → " + ex.Message);
                }
            }
            _output.WriteLine("확인한 예제 " + snippets.Count + "개:" + Environment.NewLine + string.Join(Environment.NewLine, snippets));
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }

        // 예제 하나를 그린다: 수식은 엑셀로, 줄 지시문은 해석만, 그 밖은 모든 반복 변수가 있는 자리에서 셀 템플릿으로
        private static string RenderSnippet(string snippet)
        {
            if (snippet.StartsWith("=", StringComparison.Ordinal))
            {
                using (var template = new XLWorkbook())
                using (var stream = new MemoryStream())
                {
                    var s = template.AddWorksheet("S");
                    s.Cell("A1").Value = "#foreach($t in $tables)";
                    s.Cell("A2").Value = "#set($table = $t)";
                    s.Cell("B3").FormulaA1 = snippet.Substring(1);
                    s.Cell("A4").Value = "#end";
                    template.SaveAs(stream);
                    var output = new MemoryStream();
                    ExcelTemplate.Render(stream.ToArray(), GuideFixture.Rich(), output);
                    output.Position = 0;
                    using (var book = new XLWorkbook(output))
                        return book.Worksheet("S").Cell("B1").FormulaA1;
                }
            }
            if (ExcelTemplate.ParseDirective(snippet) != null)
                return "";
            var wrapped = "#foreach($table in $tables)#set($t = $table)#foreach($col in $table.columns)#set($c = $col)"
                + "#foreach($con in $table.constraints)#set($fk = $con)#set($ck = $con)#foreach($idx in $table.indexes)#foreach($code in $codes)"
                + snippet + "#end#end#end#end#end";
            return VelocityTemplate.Parse(wrapped).Render(GuideFixture.Rich());
        }

        [Fact]
        public void GuideSnippets_ExtractionSkipsPlaceholders()
        {
            var snippets = GuideSnippets();
            Assert.Contains("#if($col.isFk)→ ${col.fkRef}#end", snippets);
            Assert.Contains("#sheet($table in $tables)", snippets);
            Assert.Contains("=HYPERLINK(\"#'${t.sheetName}'!A1\",\"${t.name}\")", snippets);
            Assert.DoesNotContain(snippets, s => s.Contains("식") || s.Contains("$x "));
        }

        // ================= 파이썬 예제 =================

        [PythonFact]
        public void MakeTemplateScript_PassesChecker_AndRenders()
        {
            var dir = TempDir();
            try
            {
                var template = Path.Combine(dir, "간단형_예제.xlsx");
                var make = Python.Run(Path.Combine(GuideFiles.Folder(), "examples", "make_template.py"), dir, template);
                Assert.True(make.ExitCode == 0, make.Output);
                AssertCheckerPasses(template);
                AssertRenders(template);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [PythonFact]
        public void GuideSection7_1_CodeBlock_Runs_PassesChecker_AndRenders()
        {
            var guide = File.ReadAllText(Path.Combine(GuideFiles.Folder(), "guide.md"));
            var start = guide.IndexOf("### 7.1", StringComparison.Ordinal);
            var block = Regex.Match(guide.Substring(start), "```python\\r?\\n(.*?)```", RegexOptions.Singleline);
            Assert.True(block.Success, "지침 7.1에 ```python 코드가 없습니다");
            var dir = TempDir();
            try
            {
                var script = Path.Combine(dir, "guide_7_1.py");
                File.WriteAllText(script, block.Groups[1].Value, new UTF8Encoding(false));
                var run = Python.Run(script, dir);
                Assert.True(run.ExitCode == 0, run.Output);
                var template = Path.Combine(dir, "내_양식.xlsx");
                Assert.True(File.Exists(template), "7.1 예제가 내_양식.xlsx를 만들지 않았습니다");
                AssertCheckerPasses(template);
                AssertRenders(template);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [PythonFact]
        public void Checker_PassesBuiltInTemplates()
        {
            var dir = TempDir();
            try
            {
                foreach (var builtIn in TableSpecTemplates.All)
                {
                    var path = Path.Combine(dir, builtIn.Id + ".xlsx");
                    File.WriteAllBytes(path, builtIn.Create());
                    AssertCheckerPasses(path);
                }
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        /// <summary>지침 9장·8장 체크리스트의 실수를 검사 도구가 잡는지(엔진에서도 실제로 문제가 되는 것들).</summary>
        [PythonTheory]
        [InlineData("typo", "${doc.titel}", "$doc에 'titel'가 없습니다")]
        [InlineData("outsideLoop", "${col.name}", "알 수 없는 변수 $col")]
        [InlineData("unclosed", "#foreach($t in $tables)", "#foreach에 맞는 #end가 없습니다")]
        [InlineData("formula", "=HYPERLINK(\"#'$t.sheetName'!A1\",\"x\")", "수식 안 참조는 ${...} 꼴")]
        [InlineData("listMember", "#foreach($t in $tables)${t.columns.name}#end", "목록에는 'name'가 없습니다")]
        public void Checker_CatchesDocumentedMistakes(string name, string value, string expected)
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, name + ".xlsx");
                using (var book = new XLWorkbook())
                {
                    var s = book.AddWorksheet("S");
                    if (value.StartsWith("=", StringComparison.Ordinal))
                    {
                        s.Cell("A1").Value = "#foreach($t in $tables)";
                        s.Cell("B2").FormulaA1 = value.Substring(1);
                        s.Cell("A3").Value = "#end";
                    }
                    else
                    {
                        s.Cell("B1").Value = value;
                    }
                    book.SaveAs(path);
                }
                var check = Checker(path);
                Assert.True(check.ExitCode == 1, check.Output);
                Assert.Contains(expected, check.Output);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [PythonFact]
        public void Checker_CatchesSheetNameAndDirectivePlacement()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "sheets.xlsx");
                using (var book = new XLWorkbook())
                {
                    var after = book.AddWorksheet("내용 다음 sheet");
                    after.Cell("B1").Value = "머리";
                    after.Cell("A2").Value = "#sheet($table in $tables)";
                    var other = book.AddWorksheet("${table.owner}_${table.name}");
                    other.Cell("A1").Value = "#sheet($table in $tables)";
                    var mixed = book.AddWorksheet("섞임");
                    mixed.Cell("A1").Value = "#if($doc.title)";
                    mixed.Cell("C1").Value = "옆 칸";
                    book.SaveAs(path);
                }
                var check = Checker(path);
                Assert.Equal(1, check.ExitCode);
                Assert.Contains("#sheet는 시트 맨 위", check.Output);
                Assert.Contains("${table.sheetName}이 아니면", check.Output);
                Assert.Contains("같은 줄에 다른 값이 있어 줄 지시문이 아닙니다", check.Output);
                Assert.Contains("#if에 맞는 #end가 없습니다(셀 안)", check.Output);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        // ================= 도우미 =================

        private void AssertCheckerPasses(string template)
        {
            var check = Checker(template);
            _output.WriteLine(Path.GetFileName(template) + ": " + check.Output.Trim());
            Assert.True(check.ExitCode == 0 && check.Output.Contains("오류 0개"), check.Output);
        }

        private static ProcessResult Checker(string template)
        {
            return Python.Run(Path.Combine(GuideFiles.Folder(), "examples", "check_template.py"), Path.GetDirectoryName(template),
                template, Path.Combine(GuideFiles.Folder(), "variables.json"));
        }

        // 엔진으로 그려 남은 템플릿 글자·'_' 시트가 없는지
        private static void AssertRenders(string template)
        {
            var output = new MemoryStream();
            ExcelTemplate.Render(File.ReadAllBytes(template), GuideFixture.Rich(), output);
            output.Position = 0;
            using (var book = new XLWorkbook(output))
            {
                var leftovers = book.Worksheets.SelectMany(w => w.CellsUsed())
                    .Where(c => !c.HasFormula && c.Value.IsText && Leftover.IsMatch(c.GetText()))
                    .Select(c => c.Worksheet.Name + "!" + c.Address + " " + c.GetText()).ToList();
                Assert.True(leftovers.Count == 0, "그려지지 않은 글자: " + string.Join(" | ", leftovers));
                Assert.DoesNotContain(book.Worksheets, w => w.Name.StartsWith("_", StringComparison.Ordinal));
                Assert.Contains(book.Worksheets, w => w.Name == "ORDERS");
            }
        }

        private static string TempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "tablespec-guide-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    internal static class GuideFiles
    {
        /// <summary>저장소의 docs/table-spec-templates 폴더(시험 실행 폴더에서 위로 찾아감).</summary>
        public static string Folder()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "docs", "table-spec-templates", "guide.md")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(dir.FullName, "docs", "table-spec-templates");
        }
    }

    internal sealed class ProcessResult
    {
        public int ExitCode { get; set; }
        public string Output { get; set; }
    }

    /// <summary>python(또는 python3)과 openpyxl이 있으면 스크립트를 돌린다. 출력은 UTF-8.</summary>
    internal static class Python
    {
        private static readonly Lazy<string> Executable = new Lazy<string>(Find);

        public static bool Available
        {
            get { return Executable.Value != null; }
        }

        public const string SkipReason = "python과 openpyxl이 없어 지침 예제 시험을 건너뜁니다(pip install openpyxl).";

        private static string Find()
        {
            foreach (var name in new[] { "python", "python3" })
            {
                try
                {
                    var result = Start(name, new[] { "-c", "import openpyxl" }, Path.GetTempPath());
                    if (result.ExitCode == 0)
                        return name;
                }
                catch (Exception)
                {
                    // 그 이름의 실행 파일이 없음
                }
            }
            return null;
        }

        public static ProcessResult Run(string script, string workingDirectory, params string[] args)
        {
            return Start(Executable.Value, new[] { script }.Concat(args).ToArray(), workingDirectory);
        }

        private static ProcessResult Start(string executable, string[] args, string workingDirectory)
        {
            var info = new ProcessStartInfo(executable)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true
            };
            foreach (var arg in args)
                info.ArgumentList.Add(arg);
            info.Environment["PYTHONIOENCODING"] = "utf-8";
            info.Environment["PYTHONUTF8"] = "1";
            using (var process = Process.Start(info))
            {
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(120000))
                {
                    try { process.Kill(true); } catch (Exception) { }
                    return new ProcessResult { ExitCode = -1, Output = "시간 초과" };
                }
                return new ProcessResult { ExitCode = process.ExitCode, Output = stdout.Result + stderr.Result };
            }
        }
    }

    /// <summary>python·openpyxl이 없으면 건너뛰는 시험.</summary>
    public sealed class PythonFactAttribute : FactAttribute
    {
        public PythonFactAttribute()
        {
            if (!Python.Available)
                Skip = Python.SkipReason;
        }
    }

    public sealed class PythonTheoryAttribute : TheoryAttribute
    {
        public PythonTheoryAttribute()
        {
            if (!Python.Available)
                Skip = Python.SkipReason;
        }
    }
}

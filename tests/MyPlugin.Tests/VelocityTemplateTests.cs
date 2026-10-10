using System;
using System.Collections.Generic;
using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    /// <summary>VelocityTemplate: 참조·지시문·식·공백 처리·오류(테이블 정의서 템플릿 엔진).</summary>
    public class VelocityTemplateTests
    {
        private static VelocityContext Context()
        {
            var columns = new List<object>
            {
                Map("name", "EMPNO", "pk", "Y", "no", 1, "comment", "사원 번호"),
                Map("name", "ENAME", "pk", "", "no", 2, "comment", ""),
                Map("name", "DEPTNO", "pk", "", "no", 3, "comment", "부서")
            };
            return new VelocityContext(new Dictionary<string, object>
            {
                { "table", Map("name", "EMP", "owner", "SCOTT", "columns", columns, "numRows", 14L, "empty", new List<object>()) },
                { "title", "테이블 정의서" },
                { "n", 3 },
                { "flag", true },
                { "nothing", null }
            });
        }

        private static Dictionary<string, object> Map(params object[] pairs)
        {
            var map = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i + 1 < pairs.Length; i += 2)
                map[(string)pairs[i]] = pairs[i + 1];
            return map;
        }

        private static string Render(string template)
        {
            return VelocityTemplate.Parse(template).Render(Context());
        }

        // ---------- 참조 ----------

        [Theory]
        [InlineData("$title", "테이블 정의서")]
        [InlineData("${title}", "테이블 정의서")]
        [InlineData("$table.name", "EMP")]
        [InlineData("${table.owner}.${table.name}", "SCOTT.EMP")]
        [InlineData("$TABLE.Name", "EMP")]
        [InlineData("$table.columns.size()", "3")]
        [InlineData("${table.columns.size()}개", "3개")]
        [InlineData("$table.numRows행", "14행")]
        [InlineData("$table.name님", "EMP님")]
        [InlineData("$table.name.", "EMP.")]
        [InlineData("가격 $5", "가격 $5")]
        [InlineData("$", "$")]
        public void References(string template, string expected)
        {
            Assert.Equal(expected, Render(template));
        }

        [Fact]
        public void MissingReference_StaysAsWritten_SilentIsEmpty()
        {
            Assert.Equal("[$nope.x] [${table.nope}] []", Render("[$nope.x] [${table.nope}] [$!nothing]"));
            Assert.Equal("[]", Render("[$!{table.nope}]"));
        }

        [Fact]
        public void Escapes_DollarAndHash()
        {
            Assert.Equal("$title #if", Render("\\$title \\#if"));
        }

        [Fact]
        public void PropertyOfObject_CaseInsensitive()
        {
            var context = new VelocityContext(new Dictionary<string, object> { { "v", new Version(1, 2) } });
            Assert.Equal("1.2", VelocityTemplate.Parse("$v.major.$v.MINOR").Render(context));
        }

        // ---------- #foreach ----------

        [Fact]
        public void Foreach_WithLoopInfo()
        {
            var result = Render("#foreach($c in $table.columns)$foreach.count:$c.name#if($foreach.hasNext), #end#end");
            Assert.Equal("1:EMPNO, 2:ENAME, 3:DEPTNO", result);
        }

        [Fact]
        public void Foreach_VelocityCountAndIndex()
        {
            Assert.Equal("1/0 2/1 3/2 ", Render("#foreach($c in $table.columns)$velocityCount/$foreach.index #end"));
        }

        [Fact]
        public void Foreach_LoopVariableGoneAfterLoop()
        {
            Assert.Equal("EMPNOENAMEDEPTNO $c.name", Render("#foreach($c in $table.columns)$c.name#end $c.name"));
        }

        [Fact]
        public void Foreach_EmptyOrNull_RendersNothing()
        {
            Assert.Equal("[]", Render("[#foreach($x in $table.empty)x#end#foreach($x in $nothing)y#end]"));
        }

        [Fact]
        public void Foreach_Nested()
        {
            var context = new VelocityContext(new Dictionary<string, object>
            {
                { "tables", new List<object> { Map("name", "A", "cols", new List<object> { "x", "y" }), Map("name", "B", "cols", new List<object> { "z" }) } }
            });
            var result = VelocityTemplate.Parse("#foreach($t in $tables)$t.name(#foreach($c in $t.cols)$c#end)#end").Render(context);
            Assert.Equal("A(xy)B(z)", result);
        }

        // ---------- #if ----------

        [Theory]
        [InlineData("#if($flag)Y#{else}N#end", "Y")]
        [InlineData("#if(!$flag)Y#{else}N#end", "N")]
        [InlineData("#if($nothing)Y#{else}N#end", "N")]
        [InlineData("#if($table.empty)Y#{else}N#end", "N")]
        [InlineData("#if($table.columns)Y#{else}N#end", "Y")]
        [InlineData("#if('')Y#{else}N#end", "N")]
        [InlineData("#if(0)Y#{else}N#end", "N")]
        [InlineData("#if($n == 3)Y#end", "Y")]
        [InlineData("#if($n == \"3\")Y#{else}N#end", "Y")]
        [InlineData("#if($n > 2 && $n < 4)Y#end", "Y")]
        [InlineData("#if($n gt 5 or $flag)Y#end", "Y")]
        [InlineData("#if($table.name == 'EMP')Y#end", "Y")]
        [InlineData("#if($table.name != 'EMP')Y#{else}N#end", "N")]
        [InlineData("#if(($n + 1) * 2 == 8)Y#end", "Y")]
        [InlineData("#if($n == 1)A#elseif($n == 3)B#{else}C#end", "B")]
        [InlineData("#if($n == 1)A#elseif($n == 2)B#{else}C#end", "C")]
        [InlineData("#if($nothing == null)Y#end", "Y")]
        [InlineData("#{if}($flag)Y#{end}", "Y")]
        public void If(string template, string expected)
        {
            Assert.Equal(expected, Render(template));
        }

        [Fact]
        public void Set_GlobalAndExpression()
        {
            Assert.Equal("5 SCOTT.EMP", Render("#set($x = $n + 2)#set($q = $table.owner + '.' + $table.name)$x $q"));
        }

        [Fact]
        public void Set_InsideLoopChangesOuterVariable()
        {
            Assert.Equal("3", Render("#set($last = 0)#foreach($c in $table.columns)#set($last = $c.no)#end$last"));
        }

        // ---------- 주석·공백 ----------

        [Fact]
        public void Comments_AreRemoved()
        {
            Assert.Equal("ab", Render("a## 줄 주석\nb#* 블록\n주석 *#"));
        }

        [Fact]
        public void DirectiveAloneOnLine_LeavesNoBlankLine()
        {
            var template = "열:\n#foreach($c in $table.columns)\n  - $c.name\n  #if($c.pk == 'Y')\n    (PK)\n  #end\n#end\n끝";
            Assert.Equal("열:\n  - EMPNO\n    (PK)\n  - ENAME\n  - DEPTNO\n끝", Render(template));
        }

        [Fact]
        public void HashWithoutDirective_IsText()
        {
            Assert.Equal("#1 #endif #settings #include(x)", Render("#1 #endif #settings #include(x)"));
        }

        // ---------- 값 그대로 ----------

        [Fact]
        public void RenderValue_SingleReferenceKeepsType()
        {
            var context = Context();
            Assert.Equal(14L, VelocityTemplate.Parse("${table.numRows}").RenderValue(context));
            Assert.Equal(3, VelocityTemplate.Parse("$n").RenderValue(context));
            Assert.Null(VelocityTemplate.Parse("$!nothing").RenderValue(context));
            Assert.Equal("$nope", VelocityTemplate.Parse("$nope").RenderValue(context));
            Assert.Equal("14행", VelocityTemplate.Parse("${table.numRows}행").RenderValue(context));
        }

        [Fact]
        public void IsDynamic_OnlyWithReferencesOrDirectives()
        {
            Assert.False(VelocityTemplate.Parse("가격 $5 #1").IsDynamic);
            Assert.True(VelocityTemplate.Parse("$a").IsDynamic);
            Assert.True(VelocityTemplate.Parse("#set($a = 1)").IsDynamic);
        }

        // ---------- 오류 ----------

        [Theory]
        [InlineData("#if($a)x", "#if에 맞는 #end가 없습니다")]
        [InlineData("#foreach($a in $b)x", "#foreach에 맞는 #end가 없습니다")]
        [InlineData("x#end", "#end에 맞는 #if·#foreach가 없습니다")]
        [InlineData("#foreach($a $b)x#end", "#foreach($항목 in $목록)")]
        [InlineData("#set($a)", "#set($이름 = 값)")]
        [InlineData("#if($a ==)x#end", "식이 비어 있거나")]
        [InlineData("#if($a x)y#end", "식을 해석하지 못했습니다")]
        [InlineData("#if($a", "닫는 )")]
        public void Errors_AreReadable(string template, string message)
        {
            var ex = Assert.Throws<TemplateException>(() => VelocityTemplate.Parse(template));
            Assert.Contains(message, ex.Message);
        }

        [Fact]
        public void Evaluate_Expression()
        {
            Assert.Equal(true, VelocityTemplate.Evaluate("$n >= 3 and !$nothing", Context()));
            Assert.Equal(3, ((System.Collections.ICollection)VelocityTemplate.Evaluate("$table.columns", Context())).Count);
        }
    }
}

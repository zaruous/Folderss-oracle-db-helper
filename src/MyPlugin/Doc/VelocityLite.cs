using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace MyPlugin
{
    /// <summary>템플릿 문법 오류(닫히지 않은 #if·#foreach, 잘못된 식 등). 사람이 읽을 문장.</summary>
    public sealed class TemplateException : Exception
    {
        public TemplateException(string message) : base(message) { }
    }

    /// <summary>
    /// 템플릿 변수. 이름은 대소문자를 가리지 않는다. #foreach는 반복 변수·$foreach를 새 범위에 넣고 끝나면 지운다.
    /// #set은 그 이름이 있는 가장 안쪽 범위에, 없으면 맨 바깥(전역)에 쓴다(Velocity와 같음).
    /// </summary>
    public sealed class VelocityContext
    {
        private readonly List<Dictionary<string, object>> _scopes = new List<Dictionary<string, object>>();

        public VelocityContext()
        {
            _scopes.Add(NewScope());
        }

        public VelocityContext(IDictionary<string, object> values) : this()
        {
            if (values == null)
                return;
            foreach (var pair in values)
                _scopes[0][pair.Key] = pair.Value;
        }

        public bool TryGet(string name, out object value)
        {
            for (var i = _scopes.Count - 1; i >= 0; i--)
            {
                if (_scopes[i].TryGetValue(name, out value))
                    return true;
            }
            value = null;
            return false;
        }

        public void Set(string name, object value)
        {
            for (var i = _scopes.Count - 1; i >= 0; i--)
            {
                if (_scopes[i].ContainsKey(name))
                {
                    _scopes[i][name] = value;
                    return;
                }
            }
            _scopes[0][name] = value;
        }

        /// <summary>지금 범위에만 넣는다(반복 변수).</summary>
        public void SetLocal(string name, object value)
        {
            _scopes[_scopes.Count - 1][name] = value;
        }

        public void Push()
        {
            _scopes.Add(NewScope());
        }

        public void Pop()
        {
            if (_scopes.Count > 1)
                _scopes.RemoveAt(_scopes.Count - 1);
        }

        private static Dictionary<string, object> NewScope()
        {
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Apache Velocity 문법의 일부(테이블 정의서 산출물용, 순수 로직·테스트 대상). 지원:
    /// - 참조: $name, ${name}, $a.b.c, $!name(값이 없으면 빈 글자), $list.size()처럼 인자 없는 호출은 속성으로 본다.
    ///   이름은 영문·숫자·_만(Velocity 2와 같음 — "$table.name님"의 '님'은 이름이 아니다).
    ///   값이 없는 참조는 Velocity처럼 쓴 글자 그대로 남는다($!는 빈 글자).
    /// - #if(식) … #elseif(식) … #else … #end, #foreach($x in $list) … #end, #set($x = 식), #{else}·#{end}처럼 중괄호 형식.
    ///   #foreach 안에서 $foreach.count(1부터)·index(0부터)·hasNext·first·last, $velocityCount(1부터).
    /// - 식: 참조, "문자열"·'문자열', 숫자, true·false·null, ! && || and or not, == != &lt; &lt;= &gt; &gt;=, + - * / %, 괄호.
    ///   참·거짓은 Velocity 2와 같다: null·false·빈 글자·빈 목록·0은 거짓.
    /// - ## 줄 주석, #* 블록 주석 *#, \$·\#는 글자 그대로.
    /// - 지시문만 있는 줄은 그 줄(앞 공백·줄바꿈)을 결과에 남기지 않는다.
    /// 지원하지 않음: #macro·#parse·#include·#break, 인자 있는 메서드 호출, 목록·범위 리터럴.
    /// </summary>
    public sealed class VelocityTemplate
    {
        private static readonly string[] DirectiveNames = { "if", "elseif", "else", "end", "foreach", "set" };

        private readonly List<Node> _nodes;

        private VelocityTemplate(string source, List<Node> nodes)
        {
            Source = source;
            _nodes = nodes;
        }

        public string Source { get; }

        /// <summary>참조나 지시문이 하나라도 있으면 true(없으면 글자 그대로라 그릴 필요가 없다).</summary>
        public bool IsDynamic
        {
            get { return _nodes.Exists(n => !(n is TextNode)); }
        }

        public static VelocityTemplate Parse(string source)
        {
            return new VelocityTemplate(source ?? "", new Parser(source ?? "").ParseAll());
        }

        public string Render(VelocityContext context)
        {
            var output = new StringBuilder();
            RenderNodes(_nodes, context, output);
            return output.ToString();
        }

        /// <summary>
        /// 템플릿 전체가 참조 하나뿐이면(예: "${col.no}") 그 값을 그대로(숫자·날짜 형식 유지) 돌려준다. 값이 없으면 $!는 null, 아니면 쓴 글자.
        /// 그 밖에는 Render 결과(글자).
        /// </summary>
        public object RenderValue(VelocityContext context)
        {
            if (_nodes.Count == 1 && _nodes[0] is RefNode)
            {
                var reference = ((RefNode)_nodes[0]).Reference;
                var value = reference.Resolve(context);
                if (value != null)
                    return value;
                return reference.Silent ? null : reference.Raw;
            }
            return Render(context);
        }

        /// <summary>식 하나를 계산한다.</summary>
        public static object Evaluate(string expression, VelocityContext context)
        {
            return ExpressionParser.Parse(expression ?? "").Evaluate(context);
        }

        /// <summary>식을 미리 해석한다(엑셀 줄 지시문의 #if·#foreach·#set 인자 — 문법 오류를 템플릿을 읽을 때 알리고 반복마다 다시 해석하지 않게).</summary>
        public static Func<VelocityContext, object> Compile(string expression)
        {
            return ExpressionParser.Parse(expression ?? "").Evaluate;
        }

        // ================= 값 다루기(엑셀 줄 지시문에서도 쓴다) =================

        /// <summary>Velocity 2의 참·거짓: null·false·빈 글자·빈 목록·0은 거짓.</summary>
        public static bool IsTrue(object value)
        {
            if (value == null)
                return false;
            if (value is bool b)
                return b;
            if (value is string s)
                return s.Length > 0;
            if (IsNumber(value))
                return ToDecimal(value) != 0m;
            if (value is ICollection collection)
                return collection.Count > 0;
            if (value is IEnumerable enumerable)
                return enumerable.GetEnumerator().MoveNext();
            return true;
        }

        /// <summary>#foreach 대상: 목록이면 항목들, null이면 없음, 그 밖의 값 하나(글자·사전 포함)면 그 값 하나.</summary>
        public static List<object> Items(object value)
        {
            var items = new List<object>();
            if (value == null)
                return items;
            if (value is string || value is IDictionary || value is IDictionary<string, object> || !(value is IEnumerable))
            {
                items.Add(value);
                return items;
            }
            foreach (var item in (IEnumerable)value)
                items.Add(item);
            return items;
        }

        /// <summary>글자로: 숫자는 InvariantCulture, 날짜는 yyyy-MM-dd(시각이 있으면 HH:mm:ss까지), 목록은 [a, b].</summary>
        public static string Format(object value)
        {
            if (value == null)
                return "";
            if (value is string s)
                return s;
            if (value is bool b)
                return b ? "true" : "false";
            if (value is DateTime time)
                return time.TimeOfDay == TimeSpan.Zero ? time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            if (value is IFormattable formattable)
                return formattable.ToString(null, CultureInfo.InvariantCulture);
            if (value is IEnumerable enumerable && !(value is IDictionary) && !(value is IDictionary<string, object>))
            {
                var parts = new List<string>();
                foreach (var item in enumerable)
                    parts.Add(Format(item));
                return "[" + string.Join(", ", parts) + "]";
            }
            return value.ToString();
        }

        /// <summary>반복: 항목마다 새 범위에 변수·$foreach·$velocityCount를 넣고 body를 부른다.</summary>
        public static void Loop(VelocityContext context, string variable, object source, Action body)
        {
            var items = Items(source);
            for (var i = 0; i < items.Count; i++)
            {
                context.Push();
                try
                {
                    context.SetLocal(variable, items[i]);
                    context.SetLocal("foreach", new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        { "index", i },
                        { "count", i + 1 },
                        { "hasNext", i + 1 < items.Count },
                        { "first", i == 0 },
                        { "last", i + 1 == items.Count }
                    });
                    context.SetLocal("velocityCount", i + 1);
                    body();
                }
                finally
                {
                    context.Pop();
                }
            }
        }

        /// <summary>"$x in $list" → x, "$list". 형식이 다르면 TemplateException.</summary>
        public static void SplitForeach(string arguments, out string variable, out string source)
        {
            var text = (arguments ?? "").Trim();
            var at = text.IndexOf(" in ", StringComparison.Ordinal);
            variable = at < 0 ? "" : VariableName(text.Substring(0, at));
            source = at < 0 ? "" : text.Substring(at + 4).Trim();
            if (variable.Length == 0 || source.Length == 0)
                throw new TemplateException("#foreach는 #foreach($항목 in $목록) 형식이어야 합니다: #foreach(" + text + ")");
        }

        /// <summary>"$x = 식" → x, "식".</summary>
        public static void SplitSet(string arguments, out string variable, out string value)
        {
            var text = (arguments ?? "").Trim();
            var at = text.IndexOf('=');
            var valid = at > 0 && !(at + 1 < text.Length && text[at + 1] == '=');
            variable = valid ? VariableName(text.Substring(0, at)) : "";
            value = valid ? text.Substring(at + 1).Trim() : "";
            if (variable.Length == 0 || value.Length == 0)
                throw new TemplateException("#set은 #set($이름 = 값) 형식이어야 합니다: #set(" + text + ")");
        }

        // "$x" · "${x}" → "x". 이름 규칙에 맞지 않으면 "".
        private static string VariableName(string text)
        {
            var t = text.Trim();
            if (!t.StartsWith("$", StringComparison.Ordinal))
                return "";
            t = t.Substring(1);
            if (t.StartsWith("{", StringComparison.Ordinal) && t.EndsWith("}", StringComparison.Ordinal))
                t = t.Substring(1, t.Length - 2);
            if (t.Length == 0 || !IsIdentifierStart(t[0]))
                return "";
            foreach (var c in t)
            {
                if (!IsIdentifierPart(c))
                    return "";
            }
            return t;
        }

        /// <summary>s[open]이 '('일 때 짝이 맞는 ')' 위치(따옴표 안은 건너뜀). 없으면 -1.</summary>
        public static int MatchParen(string s, int open)
        {
            var depth = 0;
            for (var i = open; i < s.Length; i++)
            {
                var c = s[i];
                if (c == '"' || c == '\'')
                {
                    var close = s.IndexOf(c, i + 1);
                    if (close < 0)
                        return -1;
                    i = close;
                    continue;
                }
                if (c == '(')
                    depth++;
                else if (c == ')' && --depth == 0)
                    return i;
            }
            return -1;
        }

        private static bool IsIdentifierStart(char c)
        {
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_';
        }

        private static bool IsIdentifierPart(char c)
        {
            return IsIdentifierStart(c) || (c >= '0' && c <= '9');
        }

        // ================= 그리기 =================

        private static void RenderNodes(List<Node> nodes, VelocityContext context, StringBuilder output)
        {
            foreach (var node in nodes)
                node.Render(context, output);
        }

        private abstract class Node
        {
            public abstract void Render(VelocityContext context, StringBuilder output);
        }

        private sealed class TextNode : Node
        {
            public TextNode(string text) { Text = text; }
            public string Text { get; set; }

            public override void Render(VelocityContext context, StringBuilder output)
            {
                output.Append(Text);
            }
        }

        private sealed class RefNode : Node
        {
            public RefNode(Reference reference) { Reference = reference; }
            public Reference Reference { get; }

            public override void Render(VelocityContext context, StringBuilder output)
            {
                var value = Reference.Resolve(context);
                if (value != null)
                    output.Append(Format(value));
                else if (!Reference.Silent)
                    output.Append(Reference.Raw);
            }
        }

        private sealed class IfNode : Node
        {
            public List<KeyValuePair<Expression, List<Node>>> Branches { get; } = new List<KeyValuePair<Expression, List<Node>>>();
            public List<Node> Else { get; set; }

            public override void Render(VelocityContext context, StringBuilder output)
            {
                foreach (var branch in Branches)
                {
                    if (IsTrue(branch.Key.Evaluate(context)))
                    {
                        RenderNodes(branch.Value, context, output);
                        return;
                    }
                }
                if (Else != null)
                    RenderNodes(Else, context, output);
            }
        }

        private sealed class ForeachNode : Node
        {
            public string Variable { get; set; }
            public Expression Source { get; set; }
            public List<Node> Body { get; set; }

            public override void Render(VelocityContext context, StringBuilder output)
            {
                Loop(context, Variable, Source.Evaluate(context), () => RenderNodes(Body, context, output));
            }
        }

        private sealed class SetNode : Node
        {
            public string Variable { get; set; }
            public Expression Value { get; set; }

            public override void Render(VelocityContext context, StringBuilder output)
            {
                context.Set(Variable, Value.Evaluate(context));
            }
        }

        // ================= 참조 =================

        /// <summary>$a.b.c 한 개. Raw는 템플릿에 쓴 글자 그대로(값이 없을 때 남긴다).</summary>
        private sealed class Reference
        {
            public string Raw { get; set; }
            public bool Silent { get; set; }
            public List<string> Path { get; } = new List<string>();

            public object Resolve(VelocityContext context)
            {
                object current;
                if (!context.TryGet(Path[0], out current))
                    return null;
                for (var i = 1; i < Path.Count && current != null; i++)
                    current = Member(current, Path[i]);
                return current;
            }

            /// <summary>text[pos]의 '$'부터 참조 하나를 읽는다. 참조가 아니면 null(pos는 그대로).</summary>
            public static Reference Read(string text, ref int pos)
            {
                var p = pos + 1;
                var reference = new Reference();
                if (p < text.Length && text[p] == '!')
                {
                    reference.Silent = true;
                    p++;
                }
                var braced = p < text.Length && text[p] == '{';
                if (braced)
                    p++;
                var name = Identifier(text, ref p);
                if (name == null)
                    return null;
                reference.Path.Add(name);
                while (true)
                {
                    // 인자 없는 호출 $list.size()는 속성으로 본다
                    if (p + 1 < text.Length && text[p] == '(' && text[p + 1] == ')')
                        p += 2;
                    if (p + 1 < text.Length && text[p] == '.' && IsIdentifierStart(text[p + 1]))
                    {
                        p++;
                        reference.Path.Add(Identifier(text, ref p));
                        continue;
                    }
                    break;
                }
                if (braced)
                {
                    if (p >= text.Length || text[p] != '}')
                        return null;
                    p++;
                }
                reference.Raw = text.Substring(pos, p - pos);
                pos = p;
                return reference;
            }

            private static string Identifier(string text, ref int p)
            {
                if (p >= text.Length || !IsIdentifierStart(text[p]))
                    return null;
                var start = p;
                while (p < text.Length && IsIdentifierPart(text[p]))
                    p++;
                return text.Substring(start, p - start);
            }
        }

        /// <summary>속성 하나: 사전 키(대소문자 무시) → size·length·count·isEmpty·empty → 공개 속성(대소문자 무시). 없으면 null.</summary>
        private static object Member(object target, string name)
        {
            var map = target as IDictionary<string, object>;
            if (map != null)
            {
                object value;
                if (map.TryGetValue(name, out value))
                    return value;
                foreach (var pair in map)
                {
                    if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                        return pair.Value;
                }
            }
            switch (name.ToLowerInvariant())
            {
                case "size":
                case "length":
                case "count":
                    if (target is string s)
                        return s.Length;
                    if (target is ICollection c)
                        return c.Count;
                    break;
                case "isempty":
                case "empty":
                    if (target is string || target is ICollection)
                        return !IsTrue(target);
                    break;
            }
            if (map != null)
                return null;
            var property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            return property != null && property.GetIndexParameters().Length == 0 ? property.GetValue(target) : null;
        }

        // ================= 해석 =================

        private sealed class Parser
        {
            private readonly string _text;
            private int _pos;
            // #elseif(…)를 만난 블록이 읽어 둔 조건(ParseIf가 가져간다)
            private Expression _elseIfCondition;

            public Parser(string text)
            {
                _text = text;
            }

            public List<Node> ParseAll()
            {
                string stop;
                var nodes = ParseBlock(out stop);
                if (stop != null)
                    throw new TemplateException("#" + stop + "에 맞는 #if·#foreach가 없습니다.");
                return nodes;
            }

            // 블록 하나를 읽는다. #else·#elseif·#end를 만나면 멈추고 그 이름을 stop에 준다(끝까지 읽으면 null).
            private List<Node> ParseBlock(out string stop)
            {
                var nodes = new List<Node>();
                var text = new StringBuilder();
                stop = null;
                while (_pos < _text.Length)
                {
                    var c = _text[_pos];
                    if (c == '\\' && _pos + 1 < _text.Length && (_text[_pos + 1] == '$' || _text[_pos + 1] == '#'))
                    {
                        text.Append(_text[_pos + 1]);
                        _pos += 2;
                        continue;
                    }
                    if (c == '$')
                    {
                        var reference = Reference.Read(_text, ref _pos);
                        if (reference != null)
                        {
                            Flush(nodes, text);
                            nodes.Add(new RefNode(reference));
                            continue;
                        }
                    }
                    else if (c == '#' && _pos + 1 < _text.Length && _text[_pos + 1] == '#')
                    {
                        var newline = _text.IndexOf('\n', _pos);
                        _pos = newline < 0 ? _text.Length : newline + 1;
                        continue;
                    }
                    else if (c == '#' && _pos + 1 < _text.Length && _text[_pos + 1] == '*')
                    {
                        var close = _text.IndexOf("*#", _pos + 2, StringComparison.Ordinal);
                        _pos = close < 0 ? _text.Length : close + 2;
                        continue;
                    }
                    else if (c == '#')
                    {
                        var start = _pos;
                        var name = DirectiveName();
                        if (name != null)
                        {
                            Flush(nodes, text);
                            switch (name)
                            {
                                case "if":
                                    nodes.Add(ParseIf(nodes, start));
                                    break;
                                case "foreach":
                                    nodes.Add(ParseForeach(nodes, start));
                                    break;
                                case "set":
                                    nodes.Add(ParseSet(nodes, start));
                                    break;
                                case "elseif":
                                    _elseIfCondition = ExpressionParser.Parse(Arguments("elseif"));
                                    GobbleLine(nodes, start);
                                    stop = name;
                                    return nodes;
                                default: // else, end
                                    GobbleLine(nodes, start);
                                    stop = name;
                                    return nodes;
                            }
                            continue;
                        }
                    }
                    text.Append(c);
                    _pos++;
                }
                Flush(nodes, text);
                return nodes;
            }

            private Node ParseIf(List<Node> nodes, int start)
            {
                var node = new IfNode();
                var condition = ExpressionParser.Parse(Arguments("if"));
                GobbleLine(nodes, start);
                while (true)
                {
                    string stop;
                    var body = ParseBlock(out stop);
                    node.Branches.Add(new KeyValuePair<Expression, List<Node>>(condition, body));
                    if (stop == "elseif")
                    {
                        condition = _elseIfCondition;
                        continue;
                    }
                    if (stop == "else")
                    {
                        node.Else = ParseBlock(out stop);
                        if (stop != null && stop != "end")
                            throw new TemplateException("#else 뒤에 #" + stop + "이(가) 올 수 없습니다.");
                    }
                    if (stop != "end")
                        throw new TemplateException("#if에 맞는 #end가 없습니다.");
                    return node;
                }
            }

            private Node ParseForeach(List<Node> nodes, int start)
            {
                string variable;
                string source;
                SplitForeach(Arguments("foreach"), out variable, out source);
                GobbleLine(nodes, start);
                var node = new ForeachNode { Variable = variable, Source = ExpressionParser.Parse(source) };
                string stop;
                node.Body = ParseBlock(out stop);
                if (stop != "end")
                    throw new TemplateException(stop == null ? "#foreach에 맞는 #end가 없습니다." : "#foreach 안에 짝이 없는 #" + stop + "이(가) 있습니다.");
                return node;
            }

            private Node ParseSet(List<Node> nodes, int start)
            {
                string variable;
                string value;
                SplitSet(Arguments("set"), out variable, out value);
                GobbleLine(nodes, start);
                return new SetNode { Variable = variable, Value = ExpressionParser.Parse(value) };
            }

            // 지시문 이름 뒤의 ( … )를 읽는다(따옴표 안의 괄호는 세지 않음). 괄호 안 글자.
            private string Arguments(string directive)
            {
                while (_pos < _text.Length && (_text[_pos] == ' ' || _text[_pos] == '\t'))
                    _pos++;
                if (_pos >= _text.Length || _text[_pos] != '(')
                    throw new TemplateException("#" + directive + " 뒤에 ( 가 있어야 합니다.");
                var end = MatchParen(_text, _pos);
                if (end < 0)
                    throw new TemplateException("#" + directive + "(의 닫는 ) 가 없습니다.");
                var inner = _text.Substring(_pos + 1, end - _pos - 1);
                _pos = end + 1;
                return inner;
            }

            // #name 또는 #{name}. 지원하는 지시문이면 이름을 돌려주고 위치를 그 뒤로 옮긴다. 아니면 null(위치 그대로).
            private string DirectiveName()
            {
                var p = _pos + 1;
                var braced = p < _text.Length && _text[p] == '{';
                if (braced)
                    p++;
                var start = p;
                while (p < _text.Length && IsIdentifierStart(_text[p]) && _text[p] != '_')
                    p++;
                var word = _text.Substring(start, p - start);
                if (Array.IndexOf(DirectiveNames, word) < 0)
                    return null;
                if (braced)
                {
                    if (p >= _text.Length || _text[p] != '}')
                        return null;
                    p++;
                }
                else if (p < _text.Length && IsIdentifierPart(_text[p]))
                {
                    return null; // #endif·#settings 같은 글자
                }
                _pos = p;
                return word;
            }

            // 지시문(start부터 지금 위치까지)이 줄에 혼자 있으면(앞뒤가 공백뿐) 앞 공백을 결과에서 지우고 뒤의 줄바꿈까지 건너뛴다.
            private void GobbleLine(List<Node> nodes, int start)
            {
                var lineStart = _text.LastIndexOf('\n', Math.Max(0, start - 1));
                lineStart = start == 0 ? 0 : lineStart + 1;
                for (var i = lineStart; i < start; i++)
                {
                    if (_text[i] != ' ' && _text[i] != '\t')
                        return;
                }
                var p = _pos;
                while (p < _text.Length && (_text[p] == ' ' || _text[p] == '\t'))
                    p++;
                if (p < _text.Length && _text[p] == '\r' && p + 1 < _text.Length && _text[p + 1] == '\n')
                    p += 2;
                else if (p < _text.Length && _text[p] == '\n')
                    p++;
                else if (p < _text.Length)
                    return;
                var indent = start - lineStart;
                var last = nodes.Count > 0 ? nodes[nodes.Count - 1] as TextNode : null;
                if (indent > 0 && last != null && last.Text.Length >= indent)
                    last.Text = last.Text.Substring(0, last.Text.Length - indent);
                _pos = p;
            }

            private static void Flush(List<Node> nodes, StringBuilder text)
            {
                if (text.Length == 0)
                    return;
                var last = nodes.Count > 0 ? nodes[nodes.Count - 1] as TextNode : null;
                if (last != null)
                    last.Text += text.ToString();
                else
                    nodes.Add(new TextNode(text.ToString()));
                text.Clear();
            }
        }

        // ================= 식 =================

        private abstract class Expression
        {
            public abstract object Evaluate(VelocityContext context);
        }

        private sealed class Literal : Expression
        {
            private readonly object _value;
            public Literal(object value) { _value = value; }
            public override object Evaluate(VelocityContext context) { return _value; }
        }

        private sealed class RefExpression : Expression
        {
            private readonly Reference _reference;
            public RefExpression(Reference reference) { _reference = reference; }
            public override object Evaluate(VelocityContext context) { return _reference.Resolve(context); }
        }

        private sealed class Not : Expression
        {
            private readonly Expression _inner;
            public Not(Expression inner) { _inner = inner; }
            public override object Evaluate(VelocityContext context) { return !IsTrue(_inner.Evaluate(context)); }
        }

        private sealed class Binary : Expression
        {
            private readonly string _op;
            private readonly Expression _left;
            private readonly Expression _right;

            public Binary(string op, Expression left, Expression right)
            {
                _op = op;
                _left = left;
                _right = right;
            }

            public override object Evaluate(VelocityContext context)
            {
                if (_op == "&&")
                    return IsTrue(_left.Evaluate(context)) && IsTrue(_right.Evaluate(context));
                if (_op == "||")
                    return IsTrue(_left.Evaluate(context)) || IsTrue(_right.Evaluate(context));
                var a = _left.Evaluate(context);
                var b = _right.Evaluate(context);
                switch (_op)
                {
                    case "==": return AreEqual(a, b);
                    case "!=": return !AreEqual(a, b);
                    case "<": return Compare(a, b) < 0;
                    case "<=": return Compare(a, b) <= 0;
                    case ">": return Compare(a, b) > 0;
                    case ">=": return Compare(a, b) >= 0;
                    case "+":
                        if (IsNumber(a) && IsNumber(b))
                            return ToDecimal(a) + ToDecimal(b);
                        return Format(a) + Format(b);
                    case "-": return Arithmetic(a, b, (x, y) => x - y);
                    case "*": return Arithmetic(a, b, (x, y) => x * y);
                    case "/": return Arithmetic(a, b, (x, y) => y == 0 ? (decimal?)null : x / y);
                    default: return Arithmetic(a, b, (x, y) => y == 0 ? (decimal?)null : x % y);
                }
            }

            private static object Arithmetic(object a, object b, Func<decimal, decimal, decimal?> op)
            {
                if (!IsNumber(a) || !IsNumber(b))
                    return null;
                return op(ToDecimal(a), ToDecimal(b));
            }
        }

        private static bool AreEqual(object a, object b)
        {
            if (a == null || b == null)
                return a == null && b == null;
            if (IsNumber(a) && IsNumber(b))
                return ToDecimal(a) == ToDecimal(b);
            if (a is bool || b is bool)
                return a is bool && b is bool && (bool)a == (bool)b;
            return string.Equals(Format(a), Format(b), StringComparison.Ordinal);
        }

        private static int Compare(object a, object b)
        {
            if (IsNumber(a) && IsNumber(b))
                return ToDecimal(a).CompareTo(ToDecimal(b));
            return string.CompareOrdinal(Format(a), Format(b));
        }

        private static bool IsNumber(object value)
        {
            return value is int || value is long || value is decimal || value is double || value is float || value is short || value is byte;
        }

        private static decimal ToDecimal(object value)
        {
            if (value is double d)
                return double.IsNaN(d) || double.IsInfinity(d) ? 0m : (decimal)d;
            if (value is float f)
                return float.IsNaN(f) || float.IsInfinity(f) ? 0m : (decimal)f;
            return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        }

        /// <summary>식 해석기(우선순위: || → && → 비교 → + - → * / % → ! → 값).</summary>
        private sealed class ExpressionParser
        {
            private static readonly string[] ComparisonSymbols = { "==", "!=", "<=", ">=", "<", ">" };
            private static readonly string[,] ComparisonWords = { { "eq", "==" }, { "ne", "!=" }, { "le", "<=" }, { "ge", ">=" }, { "lt", "<" }, { "gt", ">" } };

            private readonly string _text;
            private int _pos;

            private ExpressionParser(string text)
            {
                _text = text;
            }

            public static Expression Parse(string text)
            {
                var parser = new ExpressionParser(text ?? "");
                var expression = parser.Or();
                parser.SkipSpace();
                if (parser._pos < parser._text.Length)
                    throw new TemplateException("식을 해석하지 못했습니다(" + (parser._pos + 1) + "번째 글자부터): " + text);
                return expression;
            }

            private Expression Or()
            {
                var left = And();
                while (Accept("||") || AcceptWord("or"))
                    left = new Binary("||", left, And());
                return left;
            }

            private Expression And()
            {
                var left = Comparison();
                while (Accept("&&") || AcceptWord("and"))
                    left = new Binary("&&", left, Comparison());
                return left;
            }

            private Expression Comparison()
            {
                var left = Additive();
                while (true)
                {
                    string op = null;
                    foreach (var symbol in ComparisonSymbols)
                    {
                        if (Accept(symbol))
                        {
                            op = symbol;
                            break;
                        }
                    }
                    for (var i = 0; op == null && i < ComparisonWords.GetLength(0); i++)
                    {
                        if (AcceptWord(ComparisonWords[i, 0]))
                            op = ComparisonWords[i, 1];
                    }
                    if (op == null)
                        return left;
                    left = new Binary(op, left, Additive());
                }
            }

            private Expression Additive()
            {
                var left = Multiplicative();
                while (true)
                {
                    if (Accept("+"))
                        left = new Binary("+", left, Multiplicative());
                    else if (Accept("-"))
                        left = new Binary("-", left, Multiplicative());
                    else
                        return left;
                }
            }

            private Expression Multiplicative()
            {
                var left = Unary();
                while (true)
                {
                    if (Accept("*"))
                        left = new Binary("*", left, Unary());
                    else if (Accept("/"))
                        left = new Binary("/", left, Unary());
                    else if (Accept("%"))
                        left = new Binary("%", left, Unary());
                    else
                        return left;
                }
            }

            private Expression Unary()
            {
                SkipSpace();
                if (_pos < _text.Length && _text[_pos] == '!' && !(_pos + 1 < _text.Length && _text[_pos + 1] == '='))
                {
                    _pos++;
                    return new Not(Unary());
                }
                if (AcceptWord("not"))
                    return new Not(Unary());
                return Primary();
            }

            private Expression Primary()
            {
                SkipSpace();
                if (_pos >= _text.Length)
                    throw new TemplateException("식이 비어 있거나 끝이 잘렸습니다: " + _text);
                var c = _text[_pos];
                if (c == '(')
                {
                    _pos++;
                    var inner = Or();
                    if (!Accept(")"))
                        throw new TemplateException("닫는 ) 가 없습니다: " + _text);
                    return inner;
                }
                if (c == '"' || c == '\'')
                {
                    var close = _text.IndexOf(c, _pos + 1);
                    if (close < 0)
                        throw new TemplateException("닫는 따옴표가 없습니다: " + _text);
                    var value = _text.Substring(_pos + 1, close - _pos - 1);
                    _pos = close + 1;
                    return new Literal(value);
                }
                if (c == '$')
                {
                    var reference = Reference.Read(_text, ref _pos);
                    if (reference == null)
                        throw new TemplateException("참조를 해석하지 못했습니다: " + _text);
                    return new RefExpression(reference);
                }
                if (char.IsDigit(c) || (c == '-' && _pos + 1 < _text.Length && char.IsDigit(_text[_pos + 1])))
                {
                    var start = _pos;
                    _pos++;
                    while (_pos < _text.Length && (char.IsDigit(_text[_pos]) || _text[_pos] == '.'))
                        _pos++;
                    decimal number;
                    if (!decimal.TryParse(_text.Substring(start, _pos - start), NumberStyles.Number, CultureInfo.InvariantCulture, out number))
                        throw new TemplateException("숫자를 해석하지 못했습니다: " + _text);
                    return new Literal(number);
                }
                if (AcceptWord("true"))
                    return new Literal(true);
                if (AcceptWord("false"))
                    return new Literal(false);
                if (AcceptWord("null"))
                    return new Literal(null);
                throw new TemplateException("식을 해석하지 못했습니다(" + (_pos + 1) + "번째 글자부터): " + _text);
            }

            private void SkipSpace()
            {
                while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos]))
                    _pos++;
            }

            private bool Accept(string token)
            {
                SkipSpace();
                if (string.CompareOrdinal(_text, _pos, token, 0, token.Length) != 0)
                    return false;
                _pos += token.Length;
                return true;
            }

            private bool AcceptWord(string word)
            {
                SkipSpace();
                if (string.CompareOrdinal(_text, _pos, word, 0, word.Length) != 0)
                    return false;
                var end = _pos + word.Length;
                if (end < _text.Length && IsIdentifierPart(_text[end]))
                    return false;
                _pos = end;
                return true;
            }
        }
    }
}

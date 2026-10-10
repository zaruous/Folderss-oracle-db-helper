using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ClosedXML.Excel;

namespace MyPlugin
{
    /// <summary>
    /// 엑셀 템플릿(.xlsx)에 Velocity 변수를 채워 산출물을 만든다(ClosedXML, 순수 로직·테스트 대상).
    /// - 셀: 글자에 $·#이 있으면 VelocityTemplate으로 그린다. 셀 전체가 참조 하나("${col.no}")면 값의 형식(숫자·날짜)을 그대로 쓴다.
    ///   수식은 "${"가 들어 있을 때만 그린다(수식의 $A$1 같은 절대 참조를 건드리지 않게).
    /// - 줄 지시문: 한 줄에 셀이 하나뿐이고 그 셀이 지시문 하나뿐이면(#foreach(…) · #if(…) · #elseif(…) · #else · #end · #set(…))
    ///   줄 단위로 동작한다 — #foreach ~ #end 사이 줄을 항목마다 되풀이, #if ~ #end 사이 줄을 조건이 참일 때만. 지시문 줄은 결과에 남지 않는다.
    /// - 시트 지시문: 시트 첫 지시문 줄이 #sheet($table in $tables)면 항목마다 그 시트를 복사해 그린다. 시트 이름은 탭 이름을 템플릿으로 그린다
    ///   (예: 탭 이름 "${table.sheetName}"). 엑셀 규칙에 맞게 고치고(31자, []:*?/\ 빼기) 겹치면 " (2)"를 붙인다.
    /// - 이름이 '_'로 시작하는 시트는 안내용 — 결과에서 뺀다.
    /// - 열 너비·인쇄 설정 등 시트 설정은 템플릿 그대로. 줄 높이·숨김, 셀 서식, 병합(줄 단위로 함께 나온 범위만)은 줄을 따라간다.
    ///   틀 고정 줄·반복 제목 줄(인쇄)은 지시문 줄이 빠진 만큼 옮기고, 인쇄 영역은 지운다(줄 수가 바뀌므로).
    /// 지원하지 않음: 조건부 서식·데이터 유효성·그림의 줄 이동.
    /// </summary>
    internal static class ExcelTemplate
    {
        public const int MaxSheetNameLength = 31;
        // 엑셀 셀 글자 한도
        private const int MaxCellText = 32767;

        private static readonly Regex DirectiveLine = new Regex(@"^#\{?(foreach|if|elseif|else|end|set|sheet)\}?", RegexOptions.CultureInvariant);

        public static void Render(string templatePath, VelocityContext context, string outputPath)
        {
            var bytes = File.ReadAllBytes(templatePath);
            using (var output = new MemoryStream())
            {
                Render(bytes, context, output);
                File.WriteAllBytes(outputPath, output.ToArray());
            }
        }

        /// <summary>템플릿(.xlsx 바이트)을 그려 output에 쓴다. 문법 오류는 "'시트'!B5: …" 꼴의 TemplateException.</summary>
        public static void Render(byte[] template, VelocityContext context, Stream output)
        {
            using (var source = new XLWorkbook(new MemoryStream(template)))
            using (var target = new XLWorkbook(new MemoryStream(template)))
            {
                var cache = new Dictionary<string, VelocityTemplate>(StringComparer.Ordinal);
                foreach (var sheet in source.Worksheets.ToList())
                {
                    var destination = target.Worksheet(sheet.Name);
                    if (sheet.Name.StartsWith("_", StringComparison.Ordinal))
                    {
                        destination.Delete();
                        continue;
                    }
                    var plan = SheetPlan.Read(sheet, cache);
                    Clear(destination);
                    if (plan.Sheet == null)
                    {
                        Emit(plan, destination, context);
                        continue;
                    }
                    var nameTemplate = Parse(cache, sheet.Name, sheet.Name + " (시트 이름)");
                    var position = destination.Position;
                    VelocityTemplate.Loop(context, plan.Sheet.Variable, Evaluate(plan.Sheet, context), () =>
                    {
                        var used = target.Worksheets.Select(w => w.Name).ToList();
                        var name = UniqueSheetName(nameTemplate.Render(context), used);
                        var copy = destination.CopyTo(name);
                        copy.Position = ++position;
                        Emit(plan, copy, context);
                    });
                    destination.Delete();
                }
                if (target.Worksheets.Count == 0)
                    target.AddWorksheet("빈 문서");
                target.SaveAs(output);
            }
        }

        /// <summary>시트 이름으로 쓸 수 있게: []:*?/\와 '(수식의 '시트'!A1 링크가 깨지지 않게) → _, 31자로 자르기, 비거나 History면 "Sheet".</summary>
        public static string SheetName(string name)
        {
            var chars = (name ?? "").Trim().Select(c => "[]:*?/\\'".IndexOf(c) >= 0 || char.IsControl(c) ? '_' : c).ToArray();
            var text = new string(chars).Trim();
            if (text.Length > MaxSheetNameLength)
                text = text.Substring(0, MaxSheetNameLength).TrimEnd();
            if (text.Length == 0 || string.Equals(text, "History", StringComparison.OrdinalIgnoreCase))
                text = "Sheet";
            return text;
        }

        /// <summary>SheetName으로 고친 뒤 used(대소문자 무시)와 겹치면 " (2)", " (3)"… 을 붙인다(31자 안에서).</summary>
        public static string UniqueSheetName(string name, ICollection<string> used)
        {
            var baseName = SheetName(name);
            var candidate = baseName;
            for (var n = 2; used.Any(u => string.Equals(u, candidate, StringComparison.OrdinalIgnoreCase)); n++)
            {
                var suffix = " (" + n.ToString(CultureInfo.InvariantCulture) + ")";
                var head = baseName.Length + suffix.Length > MaxSheetNameLength ? baseName.Substring(0, MaxSheetNameLength - suffix.Length).TrimEnd() : baseName;
                candidate = head + suffix;
            }
            return candidate;
        }

        /// <summary>셀 글자가 줄 지시문 하나뿐이면 그 지시문. 아니면 null(그 셀은 보통 템플릿).</summary>
        internal static RowDirective ParseDirective(string text)
        {
            var t = (text ?? "").Trim();
            var match = DirectiveLine.Match(t);
            if (!match.Success)
                return null;
            var name = match.Groups[1].Value;
            var rest = t.Substring(match.Length);
            if (name == "else" || name == "end")
                return rest.Trim().Length == 0 && (t[1] == '{') == match.Value.EndsWith("}", StringComparison.Ordinal) ? new RowDirective { Name = name } : null;
            if (t[1] == '{' && !match.Value.EndsWith("}", StringComparison.Ordinal))
                return null;
            var open = rest.Length - rest.TrimStart().Length;
            if (open >= rest.Length || rest[open] != '(' || VelocityTemplate.MatchParen(rest, open) != rest.Length - 1)
                return null;
            var arguments = rest.Substring(open + 1, rest.Length - open - 2);
            var directive = new RowDirective { Name = name };
            string variable = null;
            string expression = arguments;
            if (name == "foreach" || name == "sheet")
                VelocityTemplate.SplitForeach(arguments, out variable, out expression);
            else if (name == "set")
                VelocityTemplate.SplitSet(arguments, out variable, out expression);
            directive.Variable = variable;
            directive.Expression = VelocityTemplate.Compile(expression);
            return directive;
        }

        internal sealed class RowDirective
        {
            public string Name { get; set; }
            /// <summary>#foreach·#sheet의 항목 변수, #set의 변수.</summary>
            public string Variable { get; set; }
            /// <summary>미리 해석한 식(#else·#end는 null).</summary>
            public Func<VelocityContext, object> Expression { get; set; }
            /// <summary>"'시트'!A5" — 오류 문장 앞에 붙인다.</summary>
            public string Where { get; set; }
        }

        // ================= 시트 읽기 =================

        /// <summary>템플릿 시트 하나: 줄 지시문으로 묶은 줄 나무, 병합, 반복 제목 줄.</summary>
        private sealed class SheetPlan
        {
            public IXLWorksheet Source { get; private set; }
            public RowDirective Sheet { get; private set; }
            public List<Block> Blocks { get; } = new List<Block>();
            public Dictionary<int, List<IXLRangeAddress>> MergesByFirstRow { get; } = new Dictionary<int, List<IXLRangeAddress>>();

            public static SheetPlan Read(IXLWorksheet sheet, Dictionary<string, VelocityTemplate> cache)
            {
                var plan = new SheetPlan { Source = sheet };
                var stack = new Stack<KeyValuePair<RowDirective, List<Block>>>();
                var current = plan.Blocks;
                var lastRow = sheet.LastRowUsed(XLCellsUsedOptions.All);
                var count = lastRow == null ? 0 : lastRow.RowNumber();
                for (var r = 1; r <= count; r++)
                {
                    var row = sheet.Row(r);
                    var cells = row.CellsUsed(XLCellsUsedOptions.All).ToList();
                    // 수식 셀은 Value를 읽지 않는다(읽으면 ClosedXML이 수식을 계산하고, 템플릿 수식은 계산할 필요도 없고 틀리면 예외가 난다)
                    var filled = cells.Where(c => c.HasFormula || !c.Value.IsBlank).ToList();
                    RowDirective directive = null;
                    if (filled.Count == 1 && !filled[0].HasFormula && filled[0].Value.IsText)
                    {
                        try
                        {
                            directive = ParseDirective(filled[0].GetText());
                        }
                        catch (TemplateException ex)
                        {
                            throw new TemplateException(Where(sheet, filled[0]) + ": " + ex.Message);
                        }
                    }
                    if (directive != null)
                    {
                        directive.Where = Where(sheet, filled[0]);
                        current = plan.Apply(directive, stack, current);
                        continue;
                    }
                    current.Add(RowBlock.Read(sheet, row, cells, cache));
                }
                if (stack.Count > 0)
                    throw new TemplateException(stack.Peek().Key.Where + ": #" + stack.Peek().Key.Name + "에 맞는 #end가 없습니다.");
                foreach (var merge in sheet.MergedRanges)
                {
                    var first = merge.RangeAddress.FirstAddress.RowNumber;
                    List<IXLRangeAddress> list;
                    if (!plan.MergesByFirstRow.TryGetValue(first, out list))
                        plan.MergesByFirstRow.Add(first, list = new List<IXLRangeAddress>());
                    list.Add(merge.RangeAddress);
                }
                return plan;
            }

            // 지시문 하나를 나무에 넣고 다음 줄을 넣을 목록을 돌려준다
            private List<Block> Apply(RowDirective directive, Stack<KeyValuePair<RowDirective, List<Block>>> stack, List<Block> current)
            {
                switch (directive.Name)
                {
                    case "sheet":
                        if (Sheet != null || stack.Count > 0 || Blocks.Count > 0 && Blocks.Exists(b => !(b is RowBlock) || !((RowBlock)b).IsEmpty))
                            throw new TemplateException(directive.Where + ": #sheet는 시트 맨 위(다른 내용보다 먼저) 한 번만 쓸 수 있습니다.");
                        Sheet = directive;
                        Blocks.Clear();
                        return current;
                    case "set":
                        current.Add(new SetBlock { Directive = directive });
                        return current;
                    case "foreach":
                        var loop = new LoopBlock { Directive = directive };
                        current.Add(loop);
                        stack.Push(new KeyValuePair<RowDirective, List<Block>>(directive, current));
                        return loop.Body;
                    case "if":
                        var branch = new IfBlock();
                        branch.Branches.Add(new KeyValuePair<RowDirective, List<Block>>(directive, new List<Block>()));
                        current.Add(branch);
                        stack.Push(new KeyValuePair<RowDirective, List<Block>>(directive, current));
                        return branch.Branches[0].Value;
                    case "elseif":
                    case "else":
                        var open = stack.Count > 0 ? stack.Peek() : default(KeyValuePair<RowDirective, List<Block>>);
                        var block = open.Value == null ? null : open.Value[open.Value.Count - 1] as IfBlock;
                        if (block == null || block.Else != null)
                            throw new TemplateException(directive.Where + ": #" + directive.Name + "에 맞는 #if가 없습니다.");
                        if (directive.Name == "else")
                            return block.Else = new List<Block>();
                        var body = new List<Block>();
                        block.Branches.Add(new KeyValuePair<RowDirective, List<Block>>(directive, body));
                        return body;
                    default: // end
                        if (stack.Count == 0)
                            throw new TemplateException(directive.Where + ": #end에 맞는 #foreach·#if가 없습니다.");
                        return stack.Pop().Value;
                }
            }
        }

        private abstract class Block
        {
        }

        private sealed class RowBlock : Block
        {
            public int Row { get; private set; }
            public double? Height { get; private set; }
            public bool Hidden { get; private set; }
            public List<CellPlan> Cells { get; } = new List<CellPlan>();

            public bool IsEmpty
            {
                get { return Cells.All(c => !c.Source.HasFormula && c.Source.Value.IsBlank); }
            }

            public static RowBlock Read(IXLWorksheet sheet, IXLRow row, List<IXLCell> cells, Dictionary<string, VelocityTemplate> cache)
            {
                var block = new RowBlock { Row = row.RowNumber(), Hidden = row.IsHidden };
                if (Math.Abs(row.Height - sheet.RowHeight) > 0.01)
                    block.Height = row.Height;
                foreach (var cell in cells)
                {
                    var plan = new CellPlan { Source = cell, Column = cell.Address.ColumnNumber };
                    if (cell.HasFormula)
                    {
                        var formula = cell.FormulaA1;
                        if (formula.Contains("${") || formula.Contains("$!{"))
                            plan.Formula = Parse(cache, formula, Where(sheet, cell));
                    }
                    else if (cell.Value.IsText)
                    {
                        var text = cell.GetText();
                        if (text.IndexOf('$') >= 0 || text.IndexOf('#') >= 0)
                        {
                            var template = Parse(cache, text, Where(sheet, cell));
                            if (template.IsDynamic)
                                plan.Value = template;
                        }
                    }
                    block.Cells.Add(plan);
                }
                return block;
            }
        }

        private sealed class CellPlan
        {
            public IXLCell Source { get; set; }
            public int Column { get; set; }
            public VelocityTemplate Value { get; set; }
            public VelocityTemplate Formula { get; set; }
        }

        private sealed class LoopBlock : Block
        {
            public RowDirective Directive { get; set; }
            public List<Block> Body { get; } = new List<Block>();
        }

        private sealed class IfBlock : Block
        {
            public List<KeyValuePair<RowDirective, List<Block>>> Branches { get; } = new List<KeyValuePair<RowDirective, List<Block>>>();
            public List<Block> Else { get; set; }
        }

        private sealed class SetBlock : Block
        {
            public RowDirective Directive { get; set; }
        }

        // ================= 그리기 =================

        // 시트 설정은 두고 셀·병합·줄 높이만 지운다
        private static void Clear(IXLWorksheet sheet)
        {
            sheet.MergedRanges.RemoveAll();
            var last = sheet.LastRowUsed(XLCellsUsedOptions.All);
            if (last == null)
                return;
            foreach (var row in sheet.Rows(1, last.RowNumber()))
            {
                row.Clear(XLClearOptions.All);
                row.ClearHeight();
                if (row.IsHidden)
                    row.Unhide();
            }
        }

        private static void Emit(SheetPlan plan, IXLWorksheet target, VelocityContext context)
        {
            // (템플릿 줄, 결과 줄) 차례: 병합·반복 제목 줄을 옮기는 데 쓴다
            var emitted = new List<KeyValuePair<int, int>>();
            var next = 1;
            EmitBlocks(plan.Blocks, target, context, emitted, ref next);
            CopyMerges(plan, target, emitted);
            MovePrintTitles(plan.Source, target, emitted);
        }

        private static void EmitBlocks(List<Block> blocks, IXLWorksheet target, VelocityContext context, List<KeyValuePair<int, int>> emitted, ref int next)
        {
            foreach (var block in blocks)
            {
                var row = block as RowBlock;
                if (row != null)
                {
                    EmitRow(row, target, context, next);
                    emitted.Add(new KeyValuePair<int, int>(row.Row, next));
                    next++;
                    continue;
                }
                var loop = block as LoopBlock;
                if (loop != null)
                {
                    var at = next;
                    VelocityTemplate.Loop(context, loop.Directive.Variable, Evaluate(loop.Directive, context), () => EmitBlocks(loop.Body, target, context, emitted, ref at));
                    next = at;
                    continue;
                }
                var branch = block as IfBlock;
                if (branch != null)
                {
                    var chosen = branch.Else;
                    foreach (var pair in branch.Branches)
                    {
                        if (VelocityTemplate.IsTrue(Evaluate(pair.Key, context)))
                        {
                            chosen = pair.Value;
                            break;
                        }
                    }
                    if (chosen != null)
                        EmitBlocks(chosen, target, context, emitted, ref next);
                    continue;
                }
                var set = (SetBlock)block;
                context.Set(set.Directive.Variable, Evaluate(set.Directive, context));
            }
        }

        private static void EmitRow(RowBlock row, IXLWorksheet target, VelocityContext context, int at)
        {
            var targetRow = target.Row(at);
            if (row.Height.HasValue)
                targetRow.Height = row.Height.Value;
            if (row.Hidden)
                targetRow.Hide();
            foreach (var cell in row.Cells)
            {
                var targetCell = target.Cell(at, cell.Column);
                targetCell.CopyFrom(cell.Source);
                try
                {
                    if (cell.Formula != null)
                        targetCell.FormulaA1 = cell.Formula.Render(context);
                    else if (cell.Value != null)
                        SetValue(targetCell, cell.Value.RenderValue(context));
                }
                catch (TemplateException ex)
                {
                    throw new TemplateException(Where(cell.Source.Worksheet, cell.Source) + ": " + ex.Message);
                }
            }
        }

        private static void SetValue(IXLCell cell, object value)
        {
            if (value == null)
            {
                cell.Value = Blank.Value;
            }
            else if (value is string text)
            {
                if (text.Length > MaxCellText)
                    text = text.Substring(0, MaxCellText);
                // ClosedXML은 맨 앞 '를 엑셀의 "글자로 입력" 표시로 보고 뗀다 — Oracle 기본값 'N' 같은 글자를 지키려고 하나 더 붙인다
                if (text.StartsWith("'", StringComparison.Ordinal))
                    text = "'" + text;
                // 빈 글자는 빈 셀로(엑셀의 빈 칸 검사·ISBLANK가 기대하는 모양)
                if (text.Length == 0)
                    cell.Value = Blank.Value;
                else
                    cell.Value = text;
            }
            else if (value is bool flag)
            {
                cell.Value = flag;
            }
            else if (value is DateTime time)
            {
                cell.Value = time;
                if (cell.Style.NumberFormat.NumberFormatId == 0 && string.IsNullOrEmpty(cell.Style.NumberFormat.Format))
                    cell.Style.NumberFormat.Format = time.TimeOfDay == TimeSpan.Zero ? "yyyy-mm-dd" : "yyyy-mm-dd hh:mm";
            }
            else if (value is int || value is long || value is decimal || value is double || value is float || value is short || value is byte)
            {
                cell.Value = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            else
            {
                SetValue(cell, VelocityTemplate.Format(value));
            }
        }

        // 템플릿에서 r1~r2 줄이 결과에 연달아 나온 곳마다 같은 병합을 만든다
        private static void CopyMerges(SheetPlan plan, IXLWorksheet target, List<KeyValuePair<int, int>> emitted)
        {
            for (var i = 0; i < emitted.Count; i++)
            {
                List<IXLRangeAddress> merges;
                if (!plan.MergesByFirstRow.TryGetValue(emitted[i].Key, out merges))
                    continue;
                foreach (var merge in merges)
                {
                    var span = merge.LastAddress.RowNumber - merge.FirstAddress.RowNumber;
                    var whole = i + span < emitted.Count;
                    for (var k = 1; whole && k <= span; k++)
                        whole = emitted[i + k].Key == emitted[i].Key + k && emitted[i + k].Value == emitted[i].Value + k;
                    if (whole)
                        target.Range(emitted[i].Value, merge.FirstAddress.ColumnNumber, emitted[i].Value + span, merge.LastAddress.ColumnNumber).Merge();
                }
            }
        }

        private static void MovePrintTitles(IXLWorksheet source, IXLWorksheet target, List<KeyValuePair<int, int>> emitted)
        {
            // 틀 고정 줄: 지시문 줄이 빠진 만큼 위로(고정 끝 줄이 결과에 처음 나온 줄, 그 줄이 지시문이면 그 앞 줄)
            var frozen = source.SheetView.SplitRow;
            if (frozen > 0)
            {
                var rows = emitted.TakeWhile(e => e.Key <= frozen).Select(e => e.Value).DefaultIfEmpty(0).Max();
                if (rows > 0)
                    target.SheetView.Freeze(rows, source.SheetView.SplitColumn);
            }
            target.PageSetup.PrintAreas.Clear();
            var first = source.PageSetup.FirstRowToRepeatAtTop;
            var last = source.PageSetup.LastRowToRepeatAtTop;
            if (first <= 0 || last < first)
                return;
            var from = emitted.FirstOrDefault(e => e.Key == first);
            var to = emitted.FirstOrDefault(e => e.Key == last);
            if (from.Value > 0 && to.Value >= from.Value)
                target.PageSetup.SetRowsToRepeatAtTop(from.Value, to.Value);
        }

        private static object Evaluate(RowDirective directive, VelocityContext context)
        {
            try
            {
                return directive.Expression(context);
            }
            catch (TemplateException ex)
            {
                throw new TemplateException(directive.Where + ": " + ex.Message);
            }
        }

        private static VelocityTemplate Parse(Dictionary<string, VelocityTemplate> cache, string text, string where)
        {
            VelocityTemplate template;
            if (cache.TryGetValue(text, out template))
                return template;
            try
            {
                template = VelocityTemplate.Parse(text);
            }
            catch (TemplateException ex)
            {
                throw new TemplateException(where + ": " + ex.Message);
            }
            cache.Add(text, template);
            return template;
        }

        private static string Where(IXLWorksheet sheet, IXLCell cell)
        {
            return "'" + sheet.Name + "'!" + cell.Address.ToStringRelative();
        }
    }
}

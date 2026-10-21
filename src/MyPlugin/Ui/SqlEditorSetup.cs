using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;

namespace MyPlugin
{
    /// <summary>
    /// SQL 편집기(AvalonEdit <see cref="TextEditor"/>) 공통 설정 — PoC 창이 쓰고 3단계 SqlEditor가 그대로 쓴다.
    /// - 키 동작(계획서 8절 결정, 2026-10-21): Tab·Shift+Tab 들여쓰기, Enter 들여쓰기 유지, Home, 선택 없는 Ctrl+C·X는 AvalonEdit 동작 그대로,
    ///   Ctrl+D(줄 삭제)는 끈다(실수로 줄이 지워짐), Insert는 지금 TextBox처럼 겹쳐쓰기를 전환한다.
    /// - 줄 간격: AvalonEdit에는 줄 간격 속성이 없어 줄 높이가 글꼴에서 정해진다. 같은 글꼴을 가리키는 합성 글꼴을 만들어
    ///   줄 간격(LineSpacing)과 기준선을 정한다 — 한글·이모지는 글꼴 대체로 그대로 그려지고 줄 번호도 따라온다.
    /// </summary>
    public static class SqlEditorSetup
    {
        private static readonly Dictionary<string, FontFamily> Families = new Dictionary<string, FontFamily>();

        /// <summary>키 동작 결정을 편집기에 적용한다(편집기를 만들 때 한 번).</summary>
        public static void ApplyBehavior(TextEditor editor)
        {
            editor.Options.EnableHyperlinks = false;
            editor.Options.EnableEmailHyperlinks = false;
            editor.Options.AllowToggleOverstrikeMode = true;
            // 줄 삭제(Ctrl+D)는 단축키를 명령 자체(AvalonEditCommands.DeleteLine의 InputGestures)에 들고 있어 키 연결이 아니라 명령 연결을 뺀다 —
            // 그러면 Ctrl+D는 편집기를 지나간다(지금 TextBox처럼)
            RemoveCommand(editor.TextArea.CommandBindings, AvalonEditCommands.DeleteLine);
            foreach (var handler in editor.TextArea.DefaultInputHandler.NestedInputHandlers.OfType<ICSharpCode.AvalonEdit.Editing.TextAreaInputHandler>())
            {
                foreach (var binding in handler.CommandBindings.Where(b => b.Command == AvalonEditCommands.DeleteLine).ToList())
                    handler.CommandBindings.Remove(binding);
            }
        }

        private static void RemoveCommand(CommandBindingCollection bindings, ICommand command)
        {
            foreach (var binding in bindings.OfType<CommandBinding>().Where(b => b.Command == command).ToList())
                bindings.Remove(binding);
        }

        /// <summary>글꼴·크기·줄 간격(px, 0이면 글꼴 기본)을 편집기에 적용한다.</summary>
        public static void ApplyFont(TextEditor editor, FontFamily font, double fontSize, double lineHeight)
        {
            editor.FontSize = fontSize;
            editor.FontFamily = WithLineHeight(font, fontSize, lineHeight);
        }

        /// <summary>
        /// font와 같은 글꼴로 그리되 줄 높이가 lineHeight(px)인 합성 글꼴. lineHeight가 0이거나 글꼴 기본과 같으면 font 그대로.
        /// 글자는 늘어난 줄 높이의 가운데에 오게 기준선을 내린다. 같은 값이면 만든 것을 다시 쓴다.
        /// </summary>
        public static FontFamily WithLineHeight(FontFamily font, double fontSize, double lineHeight)
        {
            if (lineHeight <= 0 || fontSize <= 0)
                return font;
            GlyphTypeface glyph;
            if (!new Typeface(font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal).TryGetGlyphTypeface(out glyph))
                return font;
            if (Math.Abs(glyph.Height * fontSize - lineHeight) < 0.5)
                return font;
            var key = font.Source + "|" + fontSize + "|" + lineHeight;
            FontFamily family;
            lock (Families)
            {
                if (Families.TryGetValue(key, out family))
                    return family;
                family = new FontFamily();
                family.FamilyNames.Add(XmlLanguage.GetLanguage("en-us"), "SQL editor " + lineHeight + "px");
                family.FamilyMaps.Add(new FontFamilyMap { Target = font.Source, Unicode = "0000-10FFFF" });
                family.LineSpacing = lineHeight / fontSize;
                family.Baseline = glyph.Baseline + (family.LineSpacing - glyph.Height) / 2;
                Families[key] = family;
            }
            return family;
        }
    }
}

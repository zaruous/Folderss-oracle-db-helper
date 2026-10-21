using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MyPlugin
{
    /// <summary>강조 대상 하나의 모양(설정 "SQL 편집기" 탭의 구문 강조 한 줄).</summary>
    public sealed class SqlTokenStyle
    {
        public bool Enabled { get; set; } = true;

        /// <summary>글자색 0xRRGGBB. null이면 테마에서 정한 색(<see cref="SqlHighlightLogic.Palette"/>).</summary>
        public int? Color { get; set; }

        public bool Bold { get; set; }

        public bool Italic { get; set; }

        public SqlTokenStyle Clone()
        {
            return new SqlTokenStyle { Enabled = Enabled, Color = Color, Bold = Bold, Italic = Italic };
        }
    }

    /// <summary>
    /// SQL 편집기 설정(플러그인 설정 "SQL 편집기" 탭 — 순수 로직, 테스트 대상). 플러그인 설정의 키 하나(<see cref="SettingKey"/>)에 JSON으로 둔다.
    /// 지금은 도구 › AvalonEdit PoC 창이 쓰고, SQL 탭 편집기를 바꾸는 3단계부터 SQL 탭 편집기도 쓴다.
    /// - 줄 간격(px, 0이면 글꼴 기본) — Folderss 설정에는 없는 편집기 표시 설정
    /// - 칠하지 않을 긴 줄 기준(글자 수, 기본 20,000) — 한 줄에 구간이 수만 개면 줄 그리기가 느려진다
    /// - 강조 대상별 모양: 주석·문자열·키워드·자료형마다 칠함·색(비우면 테마 색)·굵게·기울임
    /// - 키워드 대상: 더 칠할 낱말(키워드로), 칠하지 않을 낱말(키워드·자료형에서 뺌) — 세 DBMS 모두에 적용
    /// 저장된 값이 깨졌거나 범위 밖이면 그 항목만 기본값으로 읽는다(편집기는 늘 열린다).
    /// </summary>
    public sealed class SqlEditorSettings
    {
        public const string SettingKey = "sqlEditor.settings";
        public const int DefaultMaxColoredLine = 20000;
        public const int MinMaxColoredLine = 1000;
        public const int MaxMaxColoredLine = 1000000;
        public const double MinLineHeight = 12;
        public const double MaxLineHeight = 48;

        /// <summary>설정 창에서 고르는 줄 간격(px). 0은 글꼴 기본(글꼴 13에서 15px).</summary>
        public static readonly double[] LineHeightChoices = { 0, 16, 17, 18, 19, 20, 21, 22, 24, 26, 28, 32 };

        /// <summary>모양을 정하는 강조 대상(설정 창 순서).</summary>
        public static readonly SqlSpanKind[] StyledKinds = { SqlSpanKind.Keyword, SqlSpanKind.DataType, SqlSpanKind.String, SqlSpanKind.Comment };

        private static readonly Regex WordPattern = new Regex(@"^[A-Za-z_][A-Za-z0-9_$#]*$", RegexOptions.CultureInvariant);

        public SqlEditorSettings()
        {
            foreach (var kind in StyledKinds)
                Styles[kind] = new SqlTokenStyle();
        }

        /// <summary>줄 간격(px). 0이면 글꼴 기본.</summary>
        public double LineHeight { get; set; }

        /// <summary>이 글자 수를 넘는 줄은 칠하지 않는다.</summary>
        public int MaxColoredLine { get; set; } = DefaultMaxColoredLine;

        public Dictionary<SqlSpanKind, SqlTokenStyle> Styles { get; } = new Dictionary<SqlSpanKind, SqlTokenStyle>();

        /// <summary>더 칠할 낱말(대문자) — 키워드로 칠한다.</summary>
        public List<string> ExtraKeywords { get; } = new List<string>();

        /// <summary>칠하지 않을 낱말(대문자) — 키워드·자료형 목록에서 뺀다.</summary>
        public List<string> ExcludedKeywords { get; } = new List<string>();

        /// <summary>저장한 설정이 바뀌었다(설정 창 [저장]) — 열려 있는 편집기가 바로 따른다.</summary>
        public static event EventHandler<SqlEditorSettings> Changed;

        public static void Publish(SqlEditorSettings settings)
        {
            var handler = Changed;
            if (handler != null)
                handler(null, settings);
        }

        public SqlTokenStyle Style(SqlSpanKind kind)
        {
            SqlTokenStyle style;
            return Styles.TryGetValue(kind, out style) ? style : new SqlTokenStyle();
        }

        public static string KindName(SqlSpanKind kind)
        {
            switch (kind)
            {
                case SqlSpanKind.Comment:
                    return "주석";
                case SqlSpanKind.String:
                    return "문자열";
                case SqlSpanKind.Keyword:
                    return "키워드";
                case SqlSpanKind.DataType:
                    return "자료형";
                default:
                    return kind.ToString();
            }
        }

        public bool IsDefault
        {
            get { return Serialize() == new SqlEditorSettings().Serialize(); }
        }

        public SqlEditorSettings Clone()
        {
            var copy = new SqlEditorSettings { LineHeight = LineHeight, MaxColoredLine = MaxColoredLine };
            foreach (var pair in Styles)
                copy.Styles[pair.Key] = pair.Value.Clone();
            copy.ExtraKeywords.AddRange(ExtraKeywords);
            copy.ExcludedKeywords.AddRange(ExcludedKeywords);
            return copy;
        }

        // ---------- 저장 형식 ----------

        private static string KindKey(SqlSpanKind kind)
        {
            switch (kind)
            {
                case SqlSpanKind.Comment:
                    return "comment";
                case SqlSpanKind.String:
                    return "string";
                case SqlSpanKind.Keyword:
                    return "keyword";
                default:
                    return "dataType";
            }
        }

        public string Serialize()
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("lineHeight", LineHeight);
                    writer.WriteNumber("maxColoredLine", MaxColoredLine);
                    writer.WriteStartObject("styles");
                    foreach (var kind in StyledKinds)
                    {
                        var style = Style(kind);
                        writer.WriteStartObject(KindKey(kind));
                        writer.WriteBoolean("enabled", style.Enabled);
                        if (style.Color.HasValue)
                            writer.WriteString("color", FormatColor(style.Color));
                        else
                            writer.WriteNull("color");
                        writer.WriteBoolean("bold", style.Bold);
                        writer.WriteBoolean("italic", style.Italic);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndObject();
                    writer.WriteStartArray("extraKeywords");
                    foreach (var word in ExtraKeywords)
                        writer.WriteStringValue(word);
                    writer.WriteEndArray();
                    writer.WriteStartArray("excludedKeywords");
                    foreach (var word in ExcludedKeywords)
                        writer.WriteStringValue(word);
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        /// <summary>저장한 설정을 읽는다. 비었거나 깨졌으면 기본값, 범위 밖·잘못된 항목은 그 항목만 기본값.</summary>
        public static SqlEditorSettings Parse(string json)
        {
            var settings = new SqlEditorSettings();
            if (string.IsNullOrWhiteSpace(json))
                return settings;
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                return settings;
            }
            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return settings;
                JsonElement value;
                double lineHeight;
                if (root.TryGetProperty("lineHeight", out value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out lineHeight)
                    && (lineHeight == 0 || (lineHeight >= MinLineHeight && lineHeight <= MaxLineHeight)))
                    settings.LineHeight = lineHeight;
                int maxLine;
                if (root.TryGetProperty("maxColoredLine", out value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out maxLine)
                    && maxLine >= MinMaxColoredLine && maxLine <= MaxMaxColoredLine)
                    settings.MaxColoredLine = maxLine;
                if (root.TryGetProperty("styles", out value) && value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var kind in StyledKinds)
                    {
                        JsonElement item;
                        if (!value.TryGetProperty(KindKey(kind), out item) || item.ValueKind != JsonValueKind.Object)
                            continue;
                        var style = settings.Style(kind);
                        style.Enabled = Bool(item, "enabled", true);
                        style.Bold = Bool(item, "bold", false);
                        style.Italic = Bool(item, "italic", false);
                        JsonElement color;
                        int? parsed;
                        if (item.TryGetProperty("color", out color) && color.ValueKind == JsonValueKind.String && ParseColor(color.GetString(), out parsed) == null)
                            style.Color = parsed;
                    }
                }
                settings.ExtraKeywords.AddRange(Words(root, "extraKeywords"));
                settings.ExcludedKeywords.AddRange(Words(root, "excludedKeywords"));
            }
            return settings;
        }

        private static bool Bool(JsonElement item, string name, bool fallback)
        {
            JsonElement value;
            if (!item.TryGetProperty(name, out value))
                return fallback;
            return value.ValueKind == JsonValueKind.True ? true : value.ValueKind == JsonValueKind.False ? false : fallback;
        }

        private static IEnumerable<string> Words(JsonElement root, string name)
        {
            JsonElement value;
            if (!root.TryGetProperty(name, out value) || value.ValueKind != JsonValueKind.Array)
                return Enumerable.Empty<string>();
            return value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString())
                .Where(w => w != null && WordPattern.IsMatch(w)).Select(w => w.ToUpperInvariant()).Distinct().ToList();
        }

        // ---------- 설정 창 입력 (맞으면 null, 아니면 저장 실패로 알릴 글) ----------

        public static string ParseMaxColoredLine(string text, out int value)
        {
            var trimmed = (text ?? "").Trim().Replace(",", "");
            if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= MinMaxColoredLine && value <= MaxMaxColoredLine)
                return null;
            value = DefaultMaxColoredLine;
            return "칠하지 않을 긴 줄 기준은 " + MinMaxColoredLine.ToString("N0", CultureInfo.InvariantCulture) + "~" + MaxMaxColoredLine.ToString("N0", CultureInfo.InvariantCulture)
                + " 사이의 글자 수여야 합니다: " + (text ?? "");
        }

        /// <summary>"#RRGGBB"·"RRGGBB"를 색으로. 비우면 null(테마 색).</summary>
        public static string ParseColor(string text, out int? color)
        {
            color = null;
            var trimmed = (text ?? "").Trim();
            if (trimmed.Length == 0)
                return null;
            if (trimmed.StartsWith("#", StringComparison.Ordinal))
                trimmed = trimmed.Substring(1);
            int value;
            if (trimmed.Length == 6 && int.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
            {
                color = value;
                return null;
            }
            return "색은 #RRGGBB(16진수 여섯 자리)로 적거나 비워 두세요(비우면 테마 색): " + text;
        }

        public static string FormatColor(int? color)
        {
            return color.HasValue ? "#" + color.Value.ToString("X6", CultureInfo.InvariantCulture) : "";
        }

        /// <summary>공백·쉼표·줄바꿈으로 나눈 낱말(영문·숫자·_·$·#, 글자로 시작) — 대문자로, 겹친 것은 하나로.</summary>
        public static string ParseWords(string text, out List<string> words)
        {
            words = new List<string>();
            var bad = new List<string>();
            foreach (var word in (text ?? "").Split(new[] { ' ', ',', ';', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!WordPattern.IsMatch(word))
                {
                    bad.Add(word);
                    continue;
                }
                var upper = word.ToUpperInvariant();
                if (!words.Contains(upper))
                    words.Add(upper);
            }
            return bad.Count == 0 ? null : "낱말은 영문·숫자·_·$·#로 적고 글자로 시작해야 합니다: " + string.Join(", ", bad);
        }

        public static string FormatWords(IEnumerable<string> words)
        {
            return string.Join(" ", words ?? Enumerable.Empty<string>());
        }

        public static string LineHeightLabel(double lineHeight)
        {
            if (lineHeight <= 0)
                return "글꼴 기본";
            return lineHeight.ToString("0.#", CultureInfo.InvariantCulture) + "px" + (lineHeight == 19 ? " (지금 SQL 탭과 같음)" : "");
        }
    }
}

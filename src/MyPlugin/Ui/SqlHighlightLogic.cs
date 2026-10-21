using System;

namespace MyPlugin
{
    /// <summary>
    /// SQL 편집기 강조 색을 테마 색에서 정한다(순수 로직, 테스트 대상 — 색은 0xRRGGBB).
    /// 목표: 바탕 대비 <see cref="MinContrast"/> 이상(WCAG), 주석은 본문 글자와도 구분(대비 <see cref="CommentSeparation"/> 이상).
    /// - 주석: 테마의 보조 글자색. 본문 글자와 너무 비슷하면(Nord·Catppuccin·Dracula) 바탕 쪽으로 옮겨 구분되게 한다(바탕 대비는 지킨다).
    /// - 문자열: 어두운 바탕은 주황(Theme.Warning), 밝은 바탕은 진한 주황.
    /// - 키워드: 테마 강조색. 바탕 대비가 모자라면(Solarized) 바탕에서 멀어지게 밝히거나 어둡게 한다.
    /// </summary>
    public static class SqlHighlightLogic
    {
        public const double MinContrast = 4.5;
        public const double CommentSeparation = 2.0;
        public const int DarkString = 0xD19A66;
        public const int LightString = 0xA65200;

        public static (int Comment, int String, int Keyword) Palette(int background, int text, int secondary, int accent)
        {
            var light = Luminance(background) > 0.5;
            var comment = secondary;
            if (Contrast(comment, text) < CommentSeparation)
                comment = TowardBackground(secondary, background);
            var keyword = AwayFromBackground(accent, background, light);
            return (comment, light ? LightString : DarkString, keyword);
        }

        /// <summary>WCAG 2 명암비(1~21).</summary>
        public static double Contrast(int a, int b)
        {
            var la = Luminance(a);
            var lb = Luminance(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }

        public static double Luminance(int rgb)
        {
            return 0.2126 * Channel((rgb >> 16) & 0xFF) + 0.7152 * Channel((rgb >> 8) & 0xFF) + 0.0722 * Channel(rgb & 0xFF);
        }

        // 바탕 대비가 MinContrast보다 조금 높게 남는 데까지 바탕 쪽으로(본문 글자와 가장 멀어진다)
        private static int TowardBackground(int color, int background)
        {
            const double target = MinContrast + 0.1;
            if (Contrast(color, background) <= target)
                return color;
            double lo = 0, hi = 1;
            for (var i = 0; i < 20; i++)
            {
                var mid = (lo + hi) / 2;
                if (Contrast(Blend(color, background, mid), background) >= target)
                    lo = mid;
                else
                    hi = mid;
            }
            return Blend(color, background, lo);
        }

        // 바탕 대비가 모자라면 흰색(어두운 바탕)·검은색(밝은 바탕) 쪽으로 모자란 만큼만
        private static int AwayFromBackground(int color, int background, bool light)
        {
            const double target = MinContrast + 0.1;
            if (Contrast(color, background) >= MinContrast)
                return color;
            var toward = light ? 0x000000 : 0xFFFFFF;
            double lo = 0, hi = 1;
            for (var i = 0; i < 20; i++)
            {
                var mid = (lo + hi) / 2;
                if (Contrast(Blend(color, toward, mid), background) >= target)
                    hi = mid;
                else
                    lo = mid;
            }
            return Blend(color, toward, hi);
        }

        private static int Blend(int a, int b, double t)
        {
            Func<int, int> mix = shift => (int)Math.Round(((a >> shift) & 0xFF) * (1 - t) + ((b >> shift) & 0xFF) * t);
            return (mix(16) << 16) | (mix(8) << 8) | mix(0);
        }

        private static double Channel(int value)
        {
            var s = value / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MyPlugin
{
    /// <summary>성능 모니터 창의 글·규칙(순수 로직, 테스트 대상). 화면(MonitorWindow)은 여기 결과를 보이기만 한다.</summary>
    internal static class MonitorLogic
    {
        public const string Title = "성능 모니터";

        /// <summary>섹션 하나가 가져오는 최대 행(세션·잠금이 많은 서버에서 창이 멈추지 않게). 이만큼 왔으면 "앞 N행"이라고 알린다.</summary>
        public const int MaxRows = 2000;

        /// <summary>자동 새로 고침 선택지(초). 0은 끔. 맨 앞이 기본.</summary>
        public static readonly IReadOnlyList<int> RefreshSeconds = new[] { 0, 5, 15, 60 };

        public const string PickDbMessage = "트리에서 대상 DB(또는 그 안의 객체)를 고른 뒤 다시 누르세요.";
        public const string NoSessionText = "연결되어 있지 않습니다. 트리에서 DB를 연결한 뒤 [새로 고침]을 누르세요.";
        public const string LoadingText = "가져오는 중…";

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>도구 메뉴 [성능 모니터…] 글: 트리에서 고른 DB가 있으면 "성능 모니터 — 개발 DB(_P)…"('_'는 메뉴 밑줄이라 두 개로).</summary>
        public static string MenuText(string dbName)
        {
            return string.IsNullOrEmpty(dbName)
                ? Title + "(_P)… (트리에서 DB를 고르세요)"
                : Title + " — " + dbName.Replace("_", "__") + "(_P)…";
        }

        /// <summary>자동 새로 고침 콤보 글: 0 → "끔", 그 밖 → "5초".</summary>
        public static string RefreshText(int seconds)
        {
            return seconds <= 0 ? "끔" : seconds.ToString(Inv) + "초";
        }

        public static string NotSupportedMessage(string displayName)
        {
            return displayName + " 접속에서는 " + Title + "를 아직 지원하지 않습니다.";
        }

        /// <summary>그 DB에서 사용자 문장이 실행 중일 때(세션을 같이 쓰므로 기다리지 않고 알린다).</summary>
        public static string BusyText(string dbName)
        {
            return "'" + dbName + "'에서 실행 중이라 가져오지 않았습니다. 끝난 뒤 [새로 고침]을 누르세요.";
        }

        /// <summary>"갱신 14:05:31 · 12행" (MaxRows만큼 왔으면 "앞 2,000행 · 더 있을 수 있음").</summary>
        public static string RefreshedText(DateTime at, int rows)
        {
            var count = rows >= MaxRows
                ? "앞 " + MaxRows.ToString("N0", Inv) + "행 · 더 있을 수 있음"
                : rows.ToString("N0", Inv) + "행";
            return "갱신 " + at.ToString("HH:mm:ss", Inv) + " · " + count;
        }

        /// <summary>권한 오류: "권한이 없어 가져오지 못했습니다 — 필요 권한: … (ORA-00942: …)".</summary>
        public static string PermissionDeniedText(MonitorSection section, string reason)
        {
            var need = section == null || string.IsNullOrWhiteSpace(section.Privilege) ? "" : " — 필요 권한: " + section.Privilege.Trim();
            return "권한이 없어 가져오지 못했습니다" + need + Reason(reason);
        }

        public static string FailedText(string reason)
        {
            return "가져오지 못했습니다" + Reason(reason);
        }

        private static string Reason(string reason)
        {
            return string.IsNullOrWhiteSpace(reason) ? "" : " (" + reason.Trim() + ")";
        }

        /// <summary>
        /// 조회 결과의 열을 결과 그리드 열로. QueryTable은 글자만 돌려주므로 숫자 열은 값으로 판단한다:
        /// NULL이 아닌 값이 하나 이상 있고 모두 숫자(InvariantCulture)로 읽히면 숫자 열(오른쪽 정렬·수로 정렬). 형식 글은 비운다.
        /// </summary>
        public static List<ResultColumn> Columns(QueryTable table)
        {
            if (table == null)
                throw new ArgumentNullException(nameof(table));
            var columns = new List<ResultColumn>();
            for (var i = 0; i < table.Columns.Count; i++)
            {
                var index = i;
                var values = table.Rows.Select(r => index < r.Length ? r[index] : null).Where(v => v != null).ToList();
                columns.Add(new ResultColumn
                {
                    Name = table.Columns[i] ?? "",
                    TypeLabel = "",
                    IsNumeric = values.Count > 0 && values.All(IsNumber)
                });
            }
            return columns;
        }

        private static bool IsNumber(string text)
        {
            decimal ignored;
            return decimal.TryParse(text, NumberStyles.Float, Inv, out ignored);
        }

        // ---------- 행 상세 목록 ----------

        public const string DetailHintText = "표의 행이나 도넛을 누르면 상세 목록을 보입니다.";

        /// <summary>상세 머리글: "데이터 파일 — MES_DATA".</summary>
        public static string DetailTitleText(MonitorDetail detail, string key)
        {
            return (detail == null ? "상세" : detail.Title) + " — " + (string.IsNullOrEmpty(key) ? "(없음)" : key);
        }

        /// <summary>표 행에서 키 열의 값. 열이 없거나 값이 없으면 null.</summary>
        public static string KeyOf(IList<ResultColumn> columns, string[] values, string keyColumn)
        {
            if (columns == null || values == null || string.IsNullOrEmpty(keyColumn))
                return null;
            for (var i = 0; i < columns.Count && i < values.Length; i++)
                if (string.Equals(columns[i].Name, keyColumn, StringComparison.Ordinal))
                    return string.IsNullOrEmpty(values[i]) ? null : values[i];
            return null;
        }

        /// <summary>상세 조회 상태: "12행" (0행이면 "없음").</summary>
        public static string DetailRowsText(int rows)
        {
            return rows == 0 ? "없음" : rows.ToString("N0", Inv) + "행";
        }

        // ---------- 사용률 차트(대시보드) ----------

        /// <summary>대시보드에 그릴 최대 도넛 수(테이블스페이스·파일이 아주 많아도 표가 밀려나지 않게). 나머지는 "외 N개"로 알린다.</summary>
        public const int MaxCharts = 12;

        /// <summary>사용률 경고 문턱(%): 이 이상이면 경고색, <see cref="DangerPercent"/> 이상이면 위험색.</summary>
        public const double WarningPercent = 80;
        public const double DangerPercent = 90;

        /// <summary>
        /// 결과 표에서 차트 조각을 뽑는다(행 순서대로, 최대 max개). 이름·사용·전체 열이 없으면 빈 목록.
        /// 전체가 0 이하이거나 사용·전체가 수로 읽히지 않는 행은 건너뛴다. 사용률은 0~100으로 자른다.
        /// </summary>
        public static List<UsageSlice> Usage(QueryTable table, MonitorUsageChart chart, int max)
        {
            var slices = new List<UsageSlice>();
            if (table == null || chart == null)
                return slices;
            var name = table.Columns.IndexOf(chart.NameColumn);
            var used = table.Columns.IndexOf(chart.UsedColumn);
            var total = table.Columns.IndexOf(chart.TotalColumn);
            if (name < 0 || used < 0 || total < 0)
                return slices;
            foreach (var row in table.Rows)
            {
                if (slices.Count >= max)
                    break;
                if (row.Length <= Math.Max(name, Math.Max(used, total)))
                    continue;
                decimal usedValue, totalValue;
                if (!decimal.TryParse(row[used], NumberStyles.Float, Inv, out usedValue) || !decimal.TryParse(row[total], NumberStyles.Float, Inv, out totalValue) || totalValue <= 0)
                    continue;
                var percent = (double)(usedValue * 100 / totalValue);
                slices.Add(new UsageSlice
                {
                    Name = row[name] ?? "",
                    Used = usedValue,
                    Total = totalValue,
                    Percent = Math.Max(0, Math.Min(100, percent))
                });
            }
            return slices;
        }

        /// <summary>사용률 색 단계: 0 보통(강조색), 1 경고(80% 이상), 2 위험(90% 이상).</summary>
        public static int UsageLevel(double percent)
        {
            if (percent >= DangerPercent)
                return 2;
            return percent >= WarningPercent ? 1 : 0;
        }

        /// <summary>도넛 가운데 글: "93%"(소수점 없이 반올림).</summary>
        public static string PercentText(double percent)
        {
            return Math.Round(percent, MidpointRounding.AwayFromZero).ToString("0", Inv) + "%";
        }

        /// <summary>도넛 아래 글: "191,562 / 204,800"(단위는 열 이름이 말한다 — MB).</summary>
        public static string UsageText(UsageSlice slice)
        {
            return slice.Used.ToString("#,##0.##", Inv) + " / " + slice.Total.ToString("#,##0.##", Inv);
        }

        /// <summary>차트 수가 MaxCharts를 넘을 때 대시보드 끝의 안내: "외 3개는 표에서". 넘지 않으면 null.</summary>
        public static string MoreChartsText(int rows, int shown)
        {
            return rows > shown ? "외 " + (rows - shown).ToString(Inv) + "개는 표에서" : null;
        }
    }

    /// <summary>사용률 도넛 하나: 이름·사용·전체·사용률(0~100).</summary>
    internal sealed class UsageSlice
    {
        public string Name { get; set; }
        public decimal Used { get; set; }
        public decimal Total { get; set; }
        public double Percent { get; set; }
    }
}

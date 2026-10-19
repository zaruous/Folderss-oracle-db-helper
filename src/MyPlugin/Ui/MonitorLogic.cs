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
        public const string NoSessionText = "모니터 세션을 열지 못했습니다(비밀번호 입력을 취소함). [새로 고침]을 누르면 다시 엽니다.";
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

        /// <summary>상태 글 뒤에 붙는 조회 소요 시간: " · 0.8초"(1초 미만은 소수 한 자리, 그 이상도 한 자리).</summary>
        public static string ElapsedSuffix(TimeSpan elapsed)
        {
            return " · " + Math.Max(0, elapsed.TotalSeconds).ToString("0.0", Inv) + "초";
        }

        /// <summary>캐시에서 보인 상세의 꼬리 글.</summary>
        public const string CachedSuffix = " · 캐시(섹션 새로 고침 때 비움)";

        /// <summary>상세 캐시 키: 상세 id와 행 키(구분자는 값에 나오지 않는 문자).</summary>
        public static string DetailCacheKey(MonitorDetail detail, string key)
        {
            return (detail == null ? "" : detail.Id) + "\u001F" + (key ?? "");
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

        // ---------- 필요 조건(확장 설치·버전) ----------

        /// <summary>점검 SQL 결과가 "충족"인지: 행이 있고 첫 값이 NULL·0·false·f·no·n이 아니면 충족.</summary>
        public static bool RequirementMet(QueryTable table)
        {
            if (table == null || table.Rows.Count == 0 || table.Rows[0].Length == 0)
                return false;
            var value = (table.Rows[0][0] ?? "").Trim();
            switch (value.ToLowerInvariant())
            {
                case "":
                case "0":
                case "false":
                case "f":
                case "no":
                case "n":
                    return false;
            }
            return true;
        }

        /// <summary>충족되지 않은 필요 조건의 상태 글(경고색): "설치·설정 필요 — …". guideLogged면 뒤에 "(설치 절차는 메시지 탭에)".</summary>
        public static string RequirementMissingText(MonitorRequirement requirement, bool guideLogged = false)
        {
            return "설치·설정 필요 — " + (requirement == null ? "" : requirement.Message.Trim()) + (guideLogged ? " (설치 절차는 메시지 탭에)" : "");
        }

        /// <summary>
        /// 접속 호스트가 이 PC인지: localhost · 127.x.x.x · ::1 · 이 PC의 컴퓨터 이름(대소문자 무시). 포트·공백은 뺀다.
        /// 서버가 이 PC여야 사용자가 직접 설치할 수 있으므로, 그때만 설치 절차를 메시지 탭에 남긴다.
        /// </summary>
        public static bool IsLocalHost(string host, string machineName = null)
        {
            var h = (host ?? "").Trim().TrimStart('[').TrimEnd(']');
            if (h.Length == 0)
                return false;
            if (string.Equals(h, "localhost", StringComparison.OrdinalIgnoreCase) || h == "::1" || h.StartsWith("127.", StringComparison.Ordinal))
                return true;
            var machine = machineName ?? Environment.MachineName;
            return !string.IsNullOrEmpty(machine) && (string.Equals(h, machine, StringComparison.OrdinalIgnoreCase)
                || h.StartsWith(machine + ".", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>메시지 탭에 남길 설치 안내(여러 줄): "[성능 모니터 · 느린 SQL] 설치·설정 필요: …" + 절차·링크. 안내가 없으면 null.</summary>
        public static string InstallGuideMessage(MonitorSection section, MonitorRequirement requirement)
        {
            if (requirement == null || string.IsNullOrWhiteSpace(requirement.InstallGuide))
                return null;
            return "[" + Title + " · " + (section == null ? "" : section.Title) + "] 설치·설정 필요: " + requirement.Message.Trim()
                + Environment.NewLine + requirement.InstallGuide.Trim().Replace("\n", Environment.NewLine);
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

        /// <summary>글로 보이는 상세(SQL 전문·실행 계획): 행마다 첫 열을 줄바꿈으로 잇는다. 행이 없으면 "".</summary>
        public static string DetailText(QueryTable table)
        {
            if (table == null || table.Rows.Count == 0)
                return "";
            return string.Join(Environment.NewLine, table.Rows.Select(r => r.Length > 0 ? r[0] ?? "" : ""));
        }

        // ---------- 행 조치(세션 종료) ----------

        /// <summary>조치에 넣을 열 값들(action.Columns 순서). 열이 하나라도 없으면 null.</summary>
        public static string[] ActionValues(IList<ResultColumn> columns, string[] values, MonitorAction action)
        {
            if (action == null)
                return null;
            var result = new string[action.Columns.Count];
            for (var i = 0; i < result.Length; i++)
            {
                var value = KeyOf(columns, values, action.Columns[i]);
                if (value == null)
                    return null;
                result[i] = value;
            }
            return result;
        }

        public const string ActionNoRowText = "먼저 표에서 행을 고르세요.";
        public const string ActionReadOnlyText = "읽기 전용 접속에서는 세션 종료 같은 조치를 할 수 없습니다. 접속 관리에서 읽기 전용을 끄세요.";
        public const string ActionBadValueText = "이 행의 값으로는 문장을 만들 수 없습니다(세션 번호가 수가 아님).";
        public const string ActionUnknownSessionText = "이 세션은 정보가 없어(이미 끝났거나 볼 수 없는 세션) 문장을 만들 수 없습니다. [새로 고침] 뒤 다시 고르세요.";

        /// <summary>잠금 트리의 근원 조치 메뉴 글: "세션 종료…" → "근원 세션 종료…".</summary>
        public static string RootActionTitle(MonitorAction action)
        {
            return "근원 " + (action == null ? "" : action.Title);
        }

        // ---------- 잠금 연쇄 트리 ----------

        public const string TableViewText = "표로 보기";
        public const string TreeViewText = "트리로 보기";
        public const string TableViewToolTip = "잠금 모드(TM·TX, 보유·요청 모드)까지 보이는 평면 표로 바꿉니다";
        public const string TreeViewToolTip = "근원 블로커 아래에 막힌 세션을 펼친 트리로 바꿉니다";
        public const string MinWaitToolTip = "사슬 안의 가장 긴 대기가 이보다 짧으면 숨깁니다(아주 짧은 대기 잡음 제외). 다시 조회하지 않습니다.";

        /// <summary>조치를 실행한 뒤 상태: "실행함: ALTER SYSTEM KILL SESSION '412,53021' IMMEDIATE".</summary>
        public static string ActionDoneText(string statement)
        {
            return "실행함: " + (statement ?? "").Trim();
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

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
    }
}

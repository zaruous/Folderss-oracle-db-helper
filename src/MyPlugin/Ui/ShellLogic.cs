using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MyPlugin
{
    /// <summary>창 전체 단축키.</summary>
    internal enum ShellShortcut { None, NewTab, Open, Save, SaveAs, CloseTab, RunScript }

    /// <summary>DB Helper 화면 틀(툴바·연결·창 닫기)의 화면과 무관한 판단과 문장(순수 로직, 테스트 대상).</summary>
    internal static class ShellLogic
    {
        /// <summary>
        /// 창 전체 단축키(key는 WPF Key의 이름): Ctrl+N 새 SQL 탭, Ctrl+O 열기, Ctrl+S 저장, Ctrl+Shift+S 다른 이름으로 저장, Ctrl+W 탭 닫기,
        /// F5 스크립트 실행(편집기의 모든 문장). Alt가 함께 눌렸거나 Ctrl이 없으면(F5 말고) None(메뉴 Alt 키·글자 입력과 섞이지 않게).
        /// </summary>
        public static ShellShortcut WindowShortcut(string key, bool control, bool shift, bool alt)
        {
            if (key == "F5")
                return !control && !shift && !alt ? ShellShortcut.RunScript : ShellShortcut.None;
            if (!control || alt)
                return ShellShortcut.None;
            switch (key)
            {
                case "N":
                    return shift ? ShellShortcut.None : ShellShortcut.NewTab;
                case "O":
                    return shift ? ShellShortcut.None : ShellShortcut.Open;
                case "S":
                    return shift ? ShellShortcut.SaveAs : ShellShortcut.Save;
                case "W":
                    return shift ? ShellShortcut.None : ShellShortcut.CloseTab;
                default:
                    return ShellShortcut.None;
            }
        }

        /// <summary>도움말 &gt; 단축키 창의 글.</summary>
        public static readonly string ShortcutsText = string.Join(Environment.NewLine, new[]
        {
            "[파일]",
            "Ctrl+N — 새 SQL 탭",
            "Ctrl+O — SQL 파일 열기",
            "Ctrl+S — 저장 (파일이 없으면 저장 창)",
            "Ctrl+Shift+S — 다른 이름으로 저장",
            "Ctrl+W — 탭 닫기 (저장하지 않은 SQL이 있으면 물어봄)",
            "",
            "[편집기]",
            "Ctrl+Enter — 커서가 있는 문장 실행 (선택 영역에 문장이 여럿이면 차례로 실행하고 조회마다 결과 탭)",
            "F5 — 스크립트 실행 (편집기의 모든 문장, 조회마다 결과 탭, 오류가 나면 멈춤)",
            "Ctrl+/ — 줄 주석 토글",
            "Ctrl+Shift+U / Ctrl+Shift+L — 선택 영역 대문자 / 소문자",
            "F4 — 커서가 있는 테이블 이름(또는 선택한 스키마.이름)의 정보: 열 · 인덱스 · 제약 조건",
            "Ctrl+Z / Ctrl+Y — 실행 취소 / 다시 실행",
            "Ctrl+F — 찾기 (F3 / Shift+F3 다음 / 이전, Esc 닫기)",
            "",
            "[결과]",
            "Ctrl+C — 선택한 행 복사(TSV)",
            "오른쪽 클릭 → 셀 복사 — 누른 칸 하나의 값",
            "Ctrl+F — 찾기 (가져온 행의 값, 찾은 칸으로 스크롤·강조; F3 / Shift+F3 다음 / 이전)",
            "열 머리 클릭 — 정렬 (오름차순 → 내림차순 → 해제)",
            "",
            "[트리]",
            "Enter — SELECT 넣기 · 펼치기 · 연결",
            "F1 — 테이블·뷰 빠른 조회 (앞 100행, 편집기는 그대로; 결과는 [행 추가]·[복제]·[편집]·[삭제]·[저장]으로 고쳐 저장)",
            "F4 — 테이블·뷰 정보",
            "Ctrl+클릭 · Shift+클릭 · Shift+↑↓ — 여러 줄 고르기 (테이블 정의서 대상)",
            "Shift+F10 또는 오른쪽 클릭 — 연결 · 다시 연결 · 연결 끊기 · 테이블 정의서 만들기",
            "도구 메뉴 — 테이블 정의서 만들기 · GCM 정의서 만들기 (트리에서 고른 DB)",
            "Esc — 검색 지우기",
            "",
            "[메뉴]",
            "Alt+F · E · S · C · T · H — 파일 · 편집 · SQL · 연결 · 도구 · 도움말",
            "",
            "SQL은 입력이 멈추고 1.5초 뒤 자동으로 임시 저장되어, 창을 다시 열면 되살아납니다."
        });

        /// <summary>툴바 "가져올 행" 선택지. 첫 값이 기본.</summary>
        public static readonly int[] FetchCounts = { 200, 1000, 5000 };

        public const int DefaultFetchCount = 200;

        /// <summary>연결 시간 제한(초).</summary>
        public const int ConnectTimeoutSeconds = 10;

        public const string NoStoredPasswordReason = "저장된 비밀번호가 없습니다. 입력한 비밀번호는 이번 연결에만 씁니다.";
        public const string RunningBlocksDisconnect = "실행 중인 탭이 있어 끊지 않았습니다. 먼저 취소하세요.";
        public const string Disconnected = "연결을 끊었습니다.";
        public const string CommitDone = "커밋 완료 (같은 DB의 모든 탭에 적용)";
        public const string RollbackDone = "롤백 완료 (같은 DB의 모든 탭에 적용)";
        public const string ConnectFailedPrefix = "연결하지 못했습니다: ";
        /// <summary>도구 메뉴 [GCM 정의서 만들기…] 글: 트리에서 고른 DB가 있으면 "GCM 정의서 만들기 — 개발 DB(_G)…"('_'는 메뉴 밑줄이라 두 개로).</summary>
        public static string CodeSpecMenuText(string dbName)
        {
            return string.IsNullOrEmpty(dbName)
                ? "GCM 정의서 만들기(_G)… (트리에서 DB를 고르세요)"
                : "GCM 정의서 만들기 — " + dbName.Replace("_", "__") + "(_G)…";
        }

        public const string ProfileRemovedWhileConnecting = "연결하는 동안 접속이 삭제되어 연결을 닫았습니다.";
        public const string ProfileChangedNote = "변경됨 — 다시 연결하면 적용";

        /// <summary>
        /// 연결할 때 쓴 접속 정보의 사본. 연결된 동안은 이 값으로 주소·이름·색을 보이고 확인 창·읽기 전용 검사를 한다
        /// (저장된 값을 바꿔도 열린 세션은 원래 DB에 붙어 있다).
        /// </summary>
        public static OracleConnectionProfile SessionCopy(OracleConnectionProfile profile)
        {
            return profile == null ? null : profile.Copy();
        }

        /// <summary>다시 읽은 저장 값(saved)에서 열린 세션(session 사본)에 바로 적용할 것: 읽기 전용을 켠 것만(더 안전한 쪽). 나머지는 다시 연결할 때.</summary>
        public static void ApplySavedToSession(OracleConnectionProfile session, OracleConnectionProfile saved)
        {
            if (session != null && saved != null && saved.ReadOnly)
                session.ReadOnly = true;
        }

        /// <summary>저장된 접속 정보가 열린 세션의 사본과 다르면 true(다시 연결하면 적용될 변경이 있다).</summary>
        public static bool ProfileChanged(OracleConnectionProfile saved, OracleConnectionProfile session)
        {
            if (saved == null || session == null)
                return false;
            return !saved.SameAs(session, false);
        }

        /// <summary>접속 표시 user@host:port/service.</summary>
        public static string Address(OracleConnectionProfile profile)
        {
            if (profile == null)
                return "";
            return Trim(profile.UserId) + "@" + Trim(profile.Host) + ":" + profile.Port.ToString(CultureInfo.InvariantCulture) + "/" + Trim(profile.ServiceName);
        }

        /// <summary>툴바의 고른 DB: "이름 · user@host:port/service". 고른 DB가 없으면 "트리에서 DB를 고르세요".</summary>
        public static string SelectedDbText(OracleConnectionProfile profile)
        {
            return profile == null ? "트리에서 DB를 고르세요" : Trim(profile.Name) + " · " + Address(profile);
        }

        /// <summary>
        /// 툴바의 모드 표시(지금 SQL 탭의 대상 DB). 커밋 대기가 있으면 "{이름} · {pendingText}"(위험 표시) — 읽기 전용이어도 SELECT … FOR UPDATE 잠금이 있을 수 있어 먼저 본다.
        /// 그 밖에는 읽기 전용이면 "{이름} · 읽기 전용", 아니면 "{이름} · 자동 커밋 끔". 대상이 없으면 "대상 DB 없음".
        /// </summary>
        public static string ModeText(OracleConnectionProfile profile, string pendingText, out bool danger)
        {
            danger = false;
            if (profile == null)
                return "대상 DB 없음";
            var name = Trim(profile.Name);
            if (!string.IsNullOrEmpty(pendingText))
            {
                danger = true;
                return name + " · " + pendingText;
            }
            return name + (profile.ReadOnly ? " · 읽기 전용" : " · 자동 커밋 끔");
        }

        /// <summary>연결 성공 메시지: "연결됨: user@host:port/service"(읽기 전용이면 " · 읽기 전용").</summary>
        public static string ConnectedMessage(OracleConnectionProfile profile)
        {
            return "연결됨: " + Address(profile) + (profile != null && profile.ReadOnly ? " · 읽기 전용" : "");
        }

        /// <summary>끊긴 세션을 버리고 다시 연결할 때. 커밋 대기가 있었으면 이미 서버에서 사라졌다고 알린다.</summary>
        public static string ReconnectMessage(string pendingText)
        {
            return string.IsNullOrEmpty(pendingText)
                ? "DB 연결이 끊겨 다시 연결합니다."
                : "DB 연결이 끊겨 다시 연결합니다. 커밋하지 않은 변경(" + pendingText + ")은 서버에서 이미 사라졌습니다.";
        }

        /// <summary>끊긴 세션을 끊을 때(커밋·롤백을 물을 수 없음).</summary>
        public static string BrokenDisconnectMessage(string pendingText)
        {
            return string.IsNullOrEmpty(pendingText)
                ? "연결이 이미 끊겨 있어 정리했습니다."
                : "연결이 이미 끊겨 커밋하지 않은 변경(" + pendingText + ")은 서버에서 사라졌습니다.";
        }

        /// <summary>같은 DB의 세션을 다른 실행이 쓰고 있어 action(커밋·롤백)을 못 했을 때.</summary>
        public static string BusyMessage(string action)
        {
            return "같은 DB에서 다른 실행이 진행 중입니다. 끝난 뒤 다시 " + action + "하세요 (DB마다 세션 1개).";
        }

        /// <summary>연결을 끊거나 창을 닫을 때 커밋·롤백이 실패함(세션은 그래도 닫힘).</summary>
        public static string CloseFailedMessage(bool commit, string error)
        {
            return commit
                ? "커밋하지 못해 변경이 저장되지 않았습니다: " + error + " (연결은 닫았습니다)"
                : "롤백하지 못했습니다: " + error + " (연결은 닫았습니다 — 서버가 변경을 버립니다)";
        }

        /// <summary>창 닫기 흐름에서 예상하지 못한 오류가 났을 때. 다음 닫기는 묻지 않고 닫는다.</summary>
        public static string CloseFlowFailedMessage(string error)
        {
            return "창을 닫는 중 오류가 났습니다: " + error + " 다시 닫으면 묻지 않고 닫습니다(커밋하지 않은 변경은 롤백됩니다).";
        }

        /// <summary>"가져올 행" 선택 값. 알 수 없는 값이면 기본값.</summary>
        public static int ParseFetchCount(object value)
        {
            int count;
            if (value is int)
                count = (int)value;
            else if (value == null || !int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out count))
                return DefaultFetchCount;
            return count > 0 ? count : DefaultFetchCount;
        }

        /// <summary>
        /// 다시 읽은 접속 목록(loaded)에, 목록에서 빠졌지만 이 창에서 아직 연결이 살아 있는 이전 접속(previous)을 뒤에 붙인다 — 끊기·커밋·롤백을 할 수 있게.
        /// orphans는 그렇게 붙인 Id들로 새로 채운다(끊으면 목록에서 뺀다). 같은 Id가 여럿이면 처음 것만 둔다.
        /// </summary>
        public static List<OracleConnectionProfile> MergeProfiles(IEnumerable<OracleConnectionProfile> loaded, IEnumerable<OracleConnectionProfile> previous,
            Func<string, bool> isLive, ICollection<string> orphans)
        {
            var merged = new List<OracleConnectionProfile>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (orphans != null)
                orphans.Clear();
            foreach (var profile in loaded ?? Enumerable.Empty<OracleConnectionProfile>())
            {
                if (profile != null && !string.IsNullOrEmpty(profile.Id) && ids.Add(profile.Id))
                    merged.Add(profile);
            }
            foreach (var profile in previous ?? Enumerable.Empty<OracleConnectionProfile>())
            {
                if (profile == null || string.IsNullOrEmpty(profile.Id) || ids.Contains(profile.Id) || isLive == null || !isLive(profile.Id))
                    continue;
                ids.Add(profile.Id);
                merged.Add(profile);
                if (orphans != null)
                    orphans.Add(profile.Id);
            }
            return merged;
        }

        private static string Trim(string value)
        {
            return (value ?? "").Trim();
        }

        private static bool Same(string a, string b)
        {
            return string.Equals(a, b, StringComparison.Ordinal);
        }
    }
}

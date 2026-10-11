using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;

namespace MyPlugin
{
    /// <summary>접속 관리 대화상자에서 편집 중인 접속 하나. [저장]할 때까지 저장된 목록은 건드리지 않는다.</summary>
    internal sealed class ConnectionDraft
    {
        private string _newPassword = "";

        public ConnectionDraft(OracleConnectionProfile profile)
        {
            if (profile == null)
                throw new ArgumentNullException(nameof(profile));
            Profile = profile;
        }

        /// <summary>편집 중인 사본. ProtectedPassword는 저장돼 있던 값 그대로 둔다(새 비밀번호·지우기는 저장할 때 반영).</summary>
        public OracleConnectionProfile Profile { get; }

        /// <summary>이번에 입력한 비밀번호. ""면 입력하지 않음.</summary>
        public string NewPassword
        {
            get { return _newPassword; }
            set { _newPassword = value ?? ""; }
        }

        /// <summary>[저장된 비밀번호 지우기]를 눌렀음.</summary>
        public bool ClearPassword { get; set; }

        /// <summary>저장된 비밀번호가 있고 지우기로 하지 않았음.</summary>
        public bool HasStoredPassword
        {
            get { return !ClearPassword && !string.IsNullOrWhiteSpace(Profile.ProtectedPassword); }
        }

        /// <summary>저장하면 비밀번호가 바뀜(새로 입력했거나 지우기로 함).</summary>
        public bool PasswordChanged
        {
            get { return _newPassword.Length > 0 || ClearPassword; }
        }
    }

    /// <summary>접속 관리 대화상자·설정 탭의 화면과 무관한 판단(테스트 대상).</summary>
    internal static class ConnectionManagerLogic
    {
        /// <summary>접속 테스트 시간 제한(초).</summary>
        public const int TestTimeoutSeconds = 10;

        public const string NewConnectionName = "새 접속";

        /// <summary>접속 테스트 전 결과 칸의 안내.</summary>
        public static string TestIdleText
        {
            get { return "최대 " + TestTimeoutSeconds.ToString(CultureInfo.InvariantCulture) + "초, 풀링 끔"; }
        }

        private const string UnprotectFailed = "저장된 비밀번호를 풀 수 없습니다(다른 PC나 다른 Windows 사용자가 저장함). 비밀번호를 다시 입력하세요.";

        /// <summary>저장된 JSON → 접속 목록(정리 포함). 손상됐으면 InvalidOperationException("저장된 접속 정보를 읽지 못했습니다: …").</summary>
        public static List<OracleConnectionProfile> ParseStored(string json)
        {
            List<OracleConnectionProfile> profiles;
            try
            {
                profiles = OracleConnectionStore.Deserialize(json);
            }
            catch (Exception ex)
            {
                throw LoadFailed(ex);
            }
            foreach (var profile in profiles)
                Normalize(profile);
            return profiles;
        }

        public static InvalidOperationException LoadFailed(Exception ex)
        {
            return new InvalidOperationException("저장된 접속 정보를 읽지 못했습니다: " + ex.Message, ex);
        }

        /// <summary>검증한 뒤 저장할 JSON. 검증에 걸리면 InvalidOperationException(오류 문장들을 줄바꿈으로).</summary>
        public static string SerializeForSave(IEnumerable<OracleConnectionProfile> profiles)
        {
            var list = profiles.ToList();
            if (list.Contains(null))
                throw new ArgumentException("빈 접속이 있습니다.", nameof(profiles));
            var errors = OracleConnectionStore.Validate(list);
            if (errors.Count > 0)
                throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
            return OracleConnectionStore.Serialize(list);
        }

        /// <summary>
        /// 저장된 비밀번호를 푼다. 없으면 null. 풀 수 없으면(다른 PC·다른 Windows 사용자가 저장, Base64가 아님)
        /// InvalidOperationException("저장된 비밀번호를 풀 수 없습니다 …").
        /// </summary>
        public static string StoredPassword(OracleConnectionProfile profile)
        {
            if (profile == null || string.IsNullOrWhiteSpace(profile.ProtectedPassword))
                return null;
            try
            {
                return OracleConnectionStore.UnprotectPassword(profile.ProtectedPassword);
            }
            catch (CryptographicException ex)
            {
                throw new InvalidOperationException(UnprotectFailed, ex);
            }
            catch (FormatException ex)
            {
                // 설정 파일을 손으로 고쳐 Base64가 아니게 된 경우
                throw new InvalidOperationException(UnprotectFailed, ex);
            }
        }

        /// <summary>읽은 값 정리: 문자열 null → "", 알 수 없는 색 → "", 빈 암호 값 → null. 같은 객체를 돌려준다.</summary>
        public static OracleConnectionProfile Normalize(OracleConnectionProfile profile)
        {
            profile.Name = profile.Name ?? "";
            profile.Host = profile.Host ?? "";
            profile.ServiceName = profile.ServiceName ?? "";
            profile.UserId = profile.UserId ?? "";
            if (string.IsNullOrWhiteSpace(profile.ProtectedPassword))
                profile.ProtectedPassword = null;
            if (profile.Color == null || Array.IndexOf(OracleConnectionStore.Colors, profile.Color) < 0)
                profile.Color = "";
            return profile;
        }

        /// <summary>모든 저장 값을 복사한 새 객체(정리 포함).</summary>
        public static OracleConnectionProfile Clone(OracleConnectionProfile profile)
        {
            return Normalize(profile.Copy());
        }

        /// <summary>저장되는 값이 모두 같은지. null과 ""는 같게 본다.</summary>
        public static bool SameProfile(OracleConnectionProfile a, OracleConnectionProfile b)
        {
            if (a == null || b == null)
                return a == b;
            return a.SameAs(b);
        }

        /// <summary>목록의 ● 표시: 새로 만들었거나, 저장된 값과 다르거나, 비밀번호를 바꿈.</summary>
        public static bool IsChanged(ConnectionDraft draft, IEnumerable<OracleConnectionProfile> originals)
        {
            if (draft.PasswordChanged)
                return true;
            var original = originals.FirstOrDefault(o => o != null && Same(o.Id, draft.Profile.Id));
            return original == null || !SameProfile(original, draft.Profile);
        }

        /// <summary>저장할 변경이 있는지: 추가·삭제·순서·값·비밀번호.</summary>
        public static bool IsDirty(IReadOnlyList<OracleConnectionProfile> originals, IReadOnlyList<ConnectionDraft> drafts)
        {
            if (originals.Count != drafts.Count)
                return true;
            for (var i = 0; i < drafts.Count; i++)
            {
                if (drafts[i].PasswordChanged || !SameProfile(originals[i], drafts[i].Profile))
                    return true;
            }
            return false;
        }

        /// <summary>[추가]: "새 접속", 있으면 "새 접속 2", "새 접속 3"…(앞뒤 공백·대소문자 무시).</summary>
        public static string NameForNew(IEnumerable<string> existing)
        {
            return Unique(NewConnectionName, NewConnectionName + " ", existing);
        }

        /// <summary>[복제]: "원본 복사", 있으면 "원본 복사 2"…</summary>
        public static string NameForCopy(string source, IEnumerable<string> existing)
        {
            var baseName = string.IsNullOrWhiteSpace(source) ? NewConnectionName : source.Trim();
            return Unique(baseName + " 복사", baseName + " 복사 ", existing);
        }

        /// <summary>비밀번호 칸 아래 안내.</summary>
        public static string PasswordState(ConnectionDraft draft)
        {
            if (draft.ClearPassword)
                return "저장하면 비밀번호를 지웁니다. 연결할 때마다 입력합니다.";
            if (draft.NewPassword.Length > 0)
                return "저장하면 새 비밀번호를 암호화해 저장합니다.";
            if (draft.HasStoredPassword)
                return "저장된 비밀번호가 있습니다(DPAPI, 이 PC·이 Windows 사용자만 풀 수 있음).";
            return "저장된 비밀번호가 없습니다. 연결할 때 입력합니다.";
        }

        /// <summary>비밀번호 칸이 비어 있을 때 흐리게 보이는 안내.</summary>
        public static string PasswordPlaceholder(ConnectionDraft draft)
        {
            return draft.HasStoredPassword ? "저장됨 — 바꾸려면 입력" : "저장하지 않으려면 비워 두기";
        }

        /// <summary>
        /// 저장할 수 없는 항목의 위치(목록에 빨간 표시). 규칙은 OracleConnectionStore.Validate와 같다:
        /// 항목마다의 필수 값·포트, 이름 중복(앞뒤 공백·대소문자 무시, 겹친 항목 모두), 내부 ID 중복.
        /// </summary>
        public static HashSet<int> InvalidIndexes(IReadOnlyList<OracleConnectionProfile> profiles)
        {
            var invalid = new HashSet<int>();
            for (var i = 0; i < profiles.Count; i++)
            {
                if (OracleConnectionStore.Validate(new[] { profiles[i] }).Count > 0)
                    invalid.Add(i);
            }
            MarkDuplicates(profiles, p => string.IsNullOrWhiteSpace(p.Name) ? null : p.Name.Trim(), StringComparer.OrdinalIgnoreCase, invalid);
            MarkDuplicates(profiles, p => string.IsNullOrWhiteSpace(p.Id) ? null : p.Id, StringComparer.Ordinal, invalid);
            return invalid;
        }

        /// <summary>접속 테스트를 할 수 없는 이유(빠진 값, 비밀번호 없음). 할 수 있으면 null. 이름은 테스트에 필요 없다.</summary>
        public static string TestInputError(OracleConnectionProfile profile, bool hasPassword)
        {
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(profile.Host))
                missing.Add("호스트");
            if (profile.Port < 1 || profile.Port > 65535)
                missing.Add("포트");
            if (string.IsNullOrWhiteSpace(profile.ServiceName))
                missing.Add("서비스명");
            if (string.IsNullOrWhiteSpace(profile.UserId))
                missing.Add("사용자");
            var parts = new List<string>();
            if (missing.Count > 0)
                parts.Add(string.Join("·", missing) + "을(를) 입력하세요.");
            if (!hasPassword)
                parts.Add("비밀번호를 입력하세요(테스트에만 쓰고, 저장 여부는 따로 정함).");
            return parts.Count == 0 ? null : string.Join(" ", parts);
        }

        /// <summary>
        /// 저장할 목록(새 객체). 새로 입력한 비밀번호는 protect(DPAPI)로 암호화하고, 지우기면 null, 그 밖은 저장돼 있던 값을 둔다.
        /// 이름·호스트·서비스명·사용자는 앞뒤 공백을 뺀다.
        /// </summary>
        public static List<OracleConnectionProfile> BuildProfilesToSave(IEnumerable<ConnectionDraft> drafts, Func<string, string> protect)
        {
            var result = new List<OracleConnectionProfile>();
            foreach (var draft in drafts)
            {
                var profile = Clone(draft.Profile);
                profile.Name = profile.Name.Trim();
                profile.Host = profile.Host.Trim();
                profile.ServiceName = profile.ServiceName.Trim();
                profile.UserId = profile.UserId.Trim();
                if (draft.NewPassword.Length > 0)
                    profile.ProtectedPassword = protect(draft.NewPassword);
                else if (draft.ClearPassword)
                    profile.ProtectedPassword = null;
                result.Add(profile);
            }
            return result;
        }

        /// <summary>저장된 목록에 있었는데 편집 목록에서 지운 접속.</summary>
        public static List<OracleConnectionProfile> RemovedProfiles(IEnumerable<OracleConnectionProfile> originals, IEnumerable<ConnectionDraft> drafts)
        {
            var kept = new HashSet<string>(drafts.Select(d => d.Profile.Id ?? ""), StringComparer.Ordinal);
            return originals.Where(o => o != null && !string.IsNullOrEmpty(o.Id) && !kept.Contains(o.Id)).ToList();
        }

        /// <summary>접속 표시: user@host:port/service(공급자 형식).</summary>
        public static string Address(OracleConnectionProfile profile)
        {
            return profile == null ? "" : DbProviders.For(profile).Address(profile);
        }

        /// <summary>목록에 보일 이름. 비어 있으면 "(이름 없음)".</summary>
        public static string DisplayName(OracleConnectionProfile profile)
        {
            return profile == null || string.IsNullOrWhiteSpace(profile.Name) ? "(이름 없음)" : profile.Name.Trim();
        }

        /// <summary>포트 칸: 숫자만 있으면 그 값, 아니면 0(저장할 때 "포트는 1~65535" 오류가 됨).</summary>
        public static int ParsePort(string text)
        {
            int port;
            return int.TryParse((text ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port) ? port : 0;
        }

        /// <summary>
        /// 연결할 때(접속 테스트·연결)의 오류 문장. 연결 끊김 번호(ORA-12537 등)는 DescribeError가 "다시 연결하세요"로 바꾸는데,
        /// 아직 연결되지 않았으므로 리스너·TNS가 알려 준 원래 첫 줄이 정확하다.
        /// </summary>
        public static string DescribeConnectError(Exception ex)
        {
            if (DbSession.IsBrokenError(ex) && !DbSession.IsCancellation(ex))
            {
                var line = FirstLine(ex.Message);
                if (line.Length > 0)
                    return line;
            }
            return DbSession.DescribeError(ex);
        }

        /// <summary>
        /// 오류 메시지의 첫 의미 있는 줄. 빈 줄과 도움말 주소(http…) 줄은 건너뛰고,
        /// "ORA-06550: line 1, column 7:"처럼 ':'로 끝나면 다음 줄(실제 원인)을 이어 붙인다(최대 두 줄).
        /// </summary>
        public static string FirstLine(string message)
        {
            var lines = (message ?? "").Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0
                    && !l.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    && !l.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (lines.Count == 0)
                return "";
            var first = lines[0];
            for (var i = 1; i < lines.Count && i < 3 && first.EndsWith(":", StringComparison.Ordinal); i++)
                first += " " + lines[i];
            return first;
        }

        /// <summary>접속 테스트 성공 문구: "접속 성공 · Oracle 23.4.0.24.05 · 0.2초". product는 공급자 표시 이름(없으면 Oracle).</summary>
        public static string TestSuccess(string serverVersion, TimeSpan elapsed, string product = null)
        {
            var seconds = elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "초";
            return string.IsNullOrWhiteSpace(serverVersion)
                ? "접속 성공 · " + seconds
                : "접속 성공 · " + (string.IsNullOrWhiteSpace(product) ? "Oracle" : product.Trim()) + " " + serverVersion.Trim() + " · " + seconds;
        }

        private static bool Same(string a, string b)
        {
            return string.Equals(a ?? "", b ?? "", StringComparison.Ordinal);
        }

        // first가 없으면 first, 있으면 prefix + 2, prefix + 3 … 중 처음으로 비어 있는 이름
        private static string Unique(string first, string prefix, IEnumerable<string> existing)
        {
            var taken = new HashSet<string>(existing.Where(n => n != null).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
            if (!taken.Contains(first))
                return first;
            for (var n = 2; ; n++)
            {
                var name = prefix + n.ToString(CultureInfo.InvariantCulture);
                if (!taken.Contains(name))
                    return name;
            }
        }

        private static void MarkDuplicates(IReadOnlyList<OracleConnectionProfile> profiles, Func<OracleConnectionProfile, string> key,
            StringComparer comparer, HashSet<int> invalid)
        {
            var groups = Enumerable.Range(0, profiles.Count)
                .Select(i => new { Index = i, Key = key(profiles[i]) })
                .Where(x => x.Key != null)
                .GroupBy(x => x.Key, comparer)
                .Where(g => g.Count() > 1);
            foreach (var group in groups)
            {
                foreach (var x in group)
                    invalid.Add(x.Index);
            }
        }
    }

    /// <summary>접속 Id별 연결 수. 여러 DB Helper 창이 같은 접속에 연결할 수 있어 수를 센다. 스레드 안전.</summary>
    internal sealed class ConnectionUseCounts
    {
        private readonly object _sync = new object();
        private readonly Dictionary<string, int> _counts = new Dictionary<string, int>(StringComparer.Ordinal);

        public void Add(string id)
        {
            if (string.IsNullOrEmpty(id))
                return;
            lock (_sync)
            {
                int count;
                _counts.TryGetValue(id, out count);
                _counts[id] = count + 1;
            }
        }

        /// <summary>하나 줄인다. 0이 되면 지운다(연결한 적 없는 Id면 아무것도 안 함).</summary>
        public void Remove(string id)
        {
            if (string.IsNullOrEmpty(id))
                return;
            lock (_sync)
            {
                int count;
                if (!_counts.TryGetValue(id, out count))
                    return;
                if (count <= 1)
                    _counts.Remove(id);
                else
                    _counts[id] = count - 1;
            }
        }

        public bool Contains(string id)
        {
            if (string.IsNullOrEmpty(id))
                return false;
            lock (_sync)
                return _counts.ContainsKey(id);
        }
    }
}

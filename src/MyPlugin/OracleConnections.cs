using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MyPlugin
{
    /// <summary>
    /// 접속 정보 하나(이름은 역사적으로 Oracle이지만 공급자 종류 <see cref="Provider"/>로 PostgreSQL 등도 담는다). 비밀번호는 DPAPI(현재 Windows 사용자)로 암호화한 값만 저장한다.
    /// 저장 필드를 더하면 <see cref="Copy"/>·<see cref="SameAs"/>도 함께 고친다(화면·세션 사본이 모두 그것을 쓴다).
    /// </summary>
    public sealed class OracleConnectionProfile
    {
        /// <summary>탭·트리가 접속을 가리키는 고정 ID. 이름을 바꿔도 유지된다. 비어 있으면 읽을 때 새로 만든다.</summary>
        public string Id { get; set; }

        public string Name { get; set; }

        /// <summary>DBMS 공급자 종류("oracle"·"postgres"). 이전 설정에는 없어 읽을 때 "oracle"로 채운다(<see cref="DbProviders.For(string)"/>).</summary>
        public string Provider { get; set; } = OracleProvider.KindName;

        public string Host { get; set; }
        public int Port { get; set; } = 1521;
        /// <summary>Oracle은 서비스명, PostgreSQL은 데이터베이스 이름(JSON 이름 serviceName은 호환을 위해 그대로).</summary>
        public string ServiceName { get; set; }
        public string UserId { get; set; }

        /// <summary>DPAPI로 암호화한 비밀번호(Base64). 없으면 null.</summary>
        public string ProtectedPassword { get; set; }

        /// <summary>true면 SELECT·WITH만 실행한다. 플러그인 쪽 차단이므로 DB 계정 권한도 읽기 전용으로 두는 것이 안전하다.</summary>
        public bool ReadOnly { get; set; }

        /// <summary>색 표시: <see cref="OracleConnectionStore.Colors"/> 중 하나("" = 없음).</summary>
        public string Color { get; set; } = "";

        /// <summary>모든 저장 값을 복사한 새 객체.</summary>
        public OracleConnectionProfile Copy()
        {
            return new OracleConnectionProfile
            {
                Id = Id,
                Name = Name,
                Provider = Provider,
                Host = Host,
                Port = Port,
                ServiceName = ServiceName,
                UserId = UserId,
                ProtectedPassword = ProtectedPassword,
                ReadOnly = ReadOnly,
                Color = Color
            };
        }

        /// <summary>저장되는 값이 모두 같은지(withId=false면 ID는 빼고). null과 ""는 같게 본다.</summary>
        public bool SameAs(OracleConnectionProfile other, bool withId = true)
        {
            if (other == null)
                return false;
            return (!withId || Same(Id, other.Id)) && Same(Name, other.Name) && Same(Provider, other.Provider) && Same(Host, other.Host) && Port == other.Port
                && Same(ServiceName, other.ServiceName) && Same(UserId, other.UserId)
                && Same(ProtectedPassword, other.ProtectedPassword) && ReadOnly == other.ReadOnly && Same(Color, other.Color);
        }

        private static bool Same(string a, string b)
        {
            return string.Equals(a ?? "", b ?? "", StringComparison.Ordinal);
        }
    }

    /// <summary>접속 정보 직렬화·검증·연결 문자열·비밀번호 암호화. 플러그인 설정의 <see cref="SettingKey"/> 하나에 JSON으로 저장한다.</summary>
    public static class OracleConnectionStore
    {
        public const string SettingKey = "connections";

        /// <summary>색 표시 값: 없음, 초록(개발), 노랑(검증), 빨강(운영).</summary>
        public static readonly string[] Colors = { "", "green", "yellow", "red" };

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        // DPAPI 추가 엔트로피. 바꾸면 이미 저장한 비밀번호를 풀 수 없다.
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Folderss.OracleHelper.v1");

        public static List<OracleConnectionProfile> Deserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new List<OracleConnectionProfile>();
            var profiles = JsonSerializer.Deserialize<List<OracleConnectionProfile>>(json, JsonOptions) ?? new List<OracleConnectionProfile>();
            foreach (var p in profiles)
            {
                // ID가 없던 이전 형식은 여기서 ID를 만든다(다음 저장 때 함께 저장됨).
                if (string.IsNullOrWhiteSpace(p.Id))
                    p.Id = NewId();
                if (!Colors.Contains(p.Color ?? ""))
                    p.Color = "";
                // provider가 없던 이전 형식·모르는 값은 Oracle(새 JSON을 이전 버전이 읽어도 모르는 속성을 무시하므로 양방향 호환)
                p.Provider = DbProviders.For(p.Provider).Kind;
            }
            return profiles;
        }

        public static string NewId()
        {
            return Guid.NewGuid().ToString("N");
        }

        public static string Serialize(IEnumerable<OracleConnectionProfile> profiles)
        {
            return JsonSerializer.Serialize(profiles.ToList(), JsonOptions);
        }

        /// <summary>저장할 수 없는 항목을 사람이 읽을 문장으로 돌려준다. 문제가 없으면 빈 목록.</summary>
        public static List<string> Validate(IReadOnlyList<OracleConnectionProfile> profiles)
        {
            var errors = new List<string>();
            for (var i = 0; i < profiles.Count; i++)
            {
                var p = profiles[i];
                var label = string.IsNullOrWhiteSpace(p.Name) ? (i + 1) + "번째 접속" : "'" + p.Name.Trim() + "'";
                if (string.IsNullOrWhiteSpace(p.Name))
                    errors.Add(label + ": 이름을 입력하세요.");
                // 호스트·포트·서비스(데이터베이스)·사용자는 공급자가 검사한다
                errors.AddRange(DbProviders.For(p).Validate(p, label));
            }
            if (profiles.Where(p => !string.IsNullOrWhiteSpace(p.Id)).GroupBy(p => p.Id).Any(g => g.Count() > 1))
                errors.Add("내부 ID가 같은 접속이 있습니다. 접속을 복제할 때 생긴 문제이니 하나를 지우고 다시 만드세요.");
            foreach (var dup in profiles.Where(p => !string.IsNullOrWhiteSpace(p.Name))
                                        .GroupBy(p => p.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                                        .Where(g => g.Count() > 1))
                errors.Add("'" + dup.Key + "': 같은 이름의 접속이 여러 개입니다.");
            return errors;
        }

        /// <summary>
        /// 접속 정보로 연결 문자열을 만든다(공급자의 빌더 — 값은 빌더가 따옴표 처리하므로 비밀번호에 ';' 등이 있어도 다른 키로 해석되지 않는다).
        /// 풀링은 끈다(접속 테스트가 백그라운드 풀 스레드를 남기지 않게).
        /// </summary>
        public static string BuildConnectionString(OracleConnectionProfile profile, string password, int timeoutSeconds)
        {
            var errors = Validate(new[] { profile });
            if (string.IsNullOrEmpty(password))
                errors.Add("비밀번호를 입력하세요.");
            if (errors.Count > 0)
                throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
            return DbProviders.For(profile).BuildConnectionString(profile, password, timeoutSeconds);
        }

        public static string ProtectPassword(string password)
        {
            var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(data);
        }

        /// <summary>다른 PC나 다른 Windows 사용자가 저장한 값이면 <see cref="CryptographicException"/>.</summary>
        public static string UnprotectPassword(string protectedPassword)
        {
            var data = ProtectedData.Unprotect(Convert.FromBase64String(protectedPassword), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MyPlugin
{
    /// <summary>GCM 정의서 출력 형식.</summary>
    internal enum CodeSpecOutput
    {
        /// <summary>엑셀 양식(Velocity 템플릿)에 채운 .xlsx.</summary>
        Excel,
        /// <summary>JSON(doc·db·source·groups[].codes[]).</summary>
        Json,
        /// <summary>CSV(코드마다 한 줄, UTF-8 BOM).</summary>
        Csv
    }

    /// <summary>
    /// [만들기…]로 마지막에 쓴 입력값(플러그인 설정 <see cref="CodeSpecLogic.SettingKey"/>에 JSON). 창을 다시 열면 이 값으로 채운다.
    /// 문서 정보, 양식·템플릿 파일·저장 폴더, 출력 형식, 마지막 SQL 본문, 접속마다 마지막에 쓴 SQL 파일, 그룹 정규식.
    /// </summary>
    internal sealed class CodeSpecSettings
    {
        public string Title { get; set; }
        public string Project { get; set; }
        public string System { get; set; }
        public string Author { get; set; }
        public string Version { get; set; }
        public string Date { get; set; }
        public string Description { get; set; }
        public string TemplateId { get; set; }
        public string TemplatePath { get; set; }
        public string Folder { get; set; }
        /// <summary>"xlsx"·"json"·"csv".</summary>
        public string Output { get; set; }
        /// <summary>마지막으로 실행한 SQL 본문(파일을 쓰지 않았을 때 되살린다).</summary>
        public string Sql { get; set; }
        /// <summary>접속 id → 마지막에 쓴 SQL 파일 경로. 같은 환경을 다시 열면 그 SQL을 보인다.</summary>
        public Dictionary<string, string> SqlFileByDb { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public string GroupPattern { get; set; }
        public bool GroupPatternSearchName { get; set; }
    }

    /// <summary>창 안에서 대상 DB를 바꿨을 때 돌려주는 요청: 그 DB id와 가져갈 편집기 SQL.</summary>
    internal sealed class CodeSpecSwitch
    {
        public string DbId { get; set; }
        public string Sql { get; set; }
    }

    /// <summary>GCM 정의서 창의 글·규칙(순수 로직, 테스트 대상).</summary>
    internal static class CodeSpecLogic
    {
        public const string Title = "GCM 정의서 만들기";

        /// <summary>마지막 입력값(CodeSpecSettings)을 담는 플러그인 설정 키.</summary>
        public const string SettingKey = "codeSpecLast";

        /// <summary>플러그인 데이터 폴더 아래 저장한 SQL 폴더 이름.</summary>
        public const string SqlFolderName = "code-sql";

        /// <summary>
        /// 처음 여는 사용자가 고쳐 쓰는 본보기 SQL(MES 범용 코드 테이블 꼴: 정의 테이블이 열 이름, 데이터 테이블이 값. 평범한 열 나열이라 어떤 DBMS에서나 같은 모양).
        /// 사내 테이블 구조가 담겨 있어 코드·배포 DLL·문서에 평문으로 두지 않고 <see cref="SqlCipherLogic"/>로 잠가 둔다(사용자 요구 — 키는 고정, 노출 방지용).
        /// 바꾸려면 평문 파일을 tests의 SqlCipherLogicTests.ProtectFile_Tool(DBH_PROTECT_IN/OUT)로 잠가 아래 글을 갈아 끼운다.
        /// </summary>
        internal const string DefaultSqlProtected =
            "dbh1:aC5rN9ZdAPSgEooUuIJyZqQ0mX2Pt90MVe6yOzgfke3Xi9jjT4cjVizgah/PN5PuLf3viyTehWMxl6yWXNv3NilZAgcRBMZ" +
            "EXuS7I72uT/tzLPj/HP7unEM8kM6OPv9DP/opsyP7aCSc8cb+EovQ732B7eNiigL+trfJXKxEIhf07Y1n31aXhFA/JyQYzUW50Yh" +
            "UcjBUDB2AShp/4sK2WmBWCDUDkEKZhHCrQ/U2id3gMnUOp+a9dxNIpTyB7ZjBHs9GBCJNrSiGJhbxVX7TxD+OY1DElFqKNzE4UfA" +
            "g2t540e8EeF2sI+3o/+tc0I51IJKfFtcLZ3GEmf8Kks2E/4RGZi5zAJXtnrVyPqiPL4ANprTokzLiTwzCyoJiTPMCQxg9QQyXrMG" +
            "GBT+uOAdO+noitNhBaEfXEeGrJ4xWPeY3IR0mhhj0t+Wih02H6uCG8UD8Sky2+upjUc7FSjM2uBiruVD0s2vetE1y6TtHyKWtSs6" +
            "DFO2RJkfY2pG8mQPdDaJiELSYoxT5FZo7DdTEidJspwYlb2xtsSWNPzJ8LNDbdr9jMApCF1UUgOTgFV+52pFVThqw+pngVRkhSWy" +
            "vdkWvssV7eTFcxU4wjaL8Z4Q3jbhOsLbfG/bIFVHi5jmDqZ6jWE1mfnPTs/H4S9PRZ4xokU+GpO9FSWtJMZ837/eYYYeF6+/JWLF" +
            "GZx6IDuDkg85b5Yc4Sp/phzUKmyNgUJiZKd3vl6DWHlyhoPnqoDWfJLByHK1EAEjamx8evv456ui2ees8cJANWhdFpYT+KRnvasp" +
            "hM4TARgBL3980LmIkjefCQYzTSXf+h6vMNnFE0ZARb4tKa//ZS3Qrmmt81j/4DjoZ5u4UJgBXrLO2GEBztSXlwElcMW35vthsXUx" +
            "/0mAsPHkD60W1D0D6xb+1uRKnLDSw56pTMzlImgdxRodaSS2lEyyxt5AGpCfDwV1eCbdIIDfei24trBS6x9GCcg6XaVJdaws3SlW" +
            "PQ9SZ/JT8ab96jP46URk6uCDSNOLC4yjPKmLW7UulrwV/eXwcQU3r6lCJDeYmIZUs+camRMzowopAvlnTbEHjDV2Qk4Z739gM8io" +
            "uL7T+YFJnYbqXJOfkzC13m8GkFd1qPeS8Sn2051jbc9hH6UHhfnwERLEO0ykEOg/c+hAfW4YeANys3YAv80clHgsylv4MekGB4Ft" +
            "rjdDSKFrdsYJ5p1sAbY1iboGfW9GjlOaiD5F1P18JGUH2l2HD+tJlIlejhrbYPhbR8vkZGRfbdcD3cTwh3g3OMPcevPgL3EIS9i/" +
            "rRCg8c/tDGByezZml8U2UL90oPgnx8GowLD2QNR2MEmegrSQHYELX5gR9YTzryTSr5DvOBUvWYUizG4eXFHvizY1xhOBZb/SztCS" +
            "k9+7hd15Z0qsRiG+3xkNfVLiJi3c6LkXsuURgSk81qPHUS4i1swWUx3yhana+BNojOPcDnkMmZd5KPNEAkWgA68HunIav2BYgl79" +
            "uaOTRNoBLIGJtI7DEUziz/yt0l4aFGLKnBnpmOmxFaIG7aHuYJexYPB+KNtYIYW0GeLWeoXnrRxPYFSIMYY1nAH+DR+4oedVT/zm" +
            "93iSEYfrepDsP73cE4TUEX0lA+YaRMtuiNnCawwqyr3FC0Eg2ZiA3qZvq15tjIWVNS0BVB49UvExO7srOdhwxzcx52MYWXUVMmeI" +
            "VQCdv9PyLWLenKXHgn0qXQZNXBhG6aMhwJjNifZPYrJsF1WfE+C7qLBlCs95d7c+OIApBO0g5oxnyiQw+df6A/nmOIJW8965bBL1" +
            "SyUY030EcTHDgV9hJZnxowVl7eRGUMFBh2Fc5aYsJIsPc/4JvhduAuyMu8PTEWouHtgVxR26aTa22/9AlGXx50WtA1n59t0AoNo5" +
            "MZXKkntx7fONjwPqHU0d+kieVkhoSkvD7DEZgm2yI/gSk4FFObUT+vxLeVjo0c1OeeNwM5VTVnnTkHi/c+WY/R69S+5FN8hBydxx" +
            "lBo/O3OsjJVnKnaTW4Y5pYYn6tOggs7OXKb9OSYrw1y8E7B3cR70V+D+t7Bj+y1AgNIrqFHU3wQDxfn4BiTdGW0XDGmQXn3wXGjN" +
            "NZEmqVbDwBPG9JMV+Ic5Aina3kIUAN0yG7WAn9AhylHYKS27WYU2ed7qjbbtwwPMotJ22Wo1uazrq4RQNj+AzytRSoM5juHtXIwx" +
            "LYafXo0Y5PYksvVjD1Jp7cziBGANuJ+RDhhhY7yb3MC4Yvcy0WBXyRD3/xbeqYFiaw/xBB7yTtucZ/kO7Mg3eHlXyb2eSUncd5dq" +
            "LS+dCRazvHkqcATAPMMx02mlWoLxOiAmUXPutAXEPfTlIPOhaTRMlg1qYs7u+b3tZlSwRPceuAsRxInigwQj6HNqEaXde5Sp8ozV" +
            "FMvhtVUWhtTtHgZp9EIWxwNxgjBbdzMiTw9GN34UxkD337uNAqou2IVYOe4HFxBXYbt+hKi15bdTmtmt3dtPWDUxQXxUrZh85YzO" +
            "hYtF/R6fQ1ZGjL9xzYCr0RvIYzjPuyHSVnPXWkvKyD/JNa7L9fPEUlh78qic758s4jh9dzDemSYYovFwVIBGSFS7ojQTQ+sOvmC7" +
            "34Y90BlhTqt/uGjCHmaewiX37AIQ92Gwc4UqBlx5pvY5jGsowhUdhgNzO+K1+F2tig9ir6pwdb4pvBGafNJTB+X1g35fvCedcaRm" +
            "yB5/+83cWzRs8RuvE0mCzmNz9YH7Pe0Jw3XGgj3faZizNb2LFV1i4RIyPtHPM4xql3LxIxVwTE+y+JNao8ThACuGbKOyRs9s6nfF" +
            "duUkUrl0827hI8dPj8yiRuvZdC0KljNOXROs5rXcu4lLlZ/KfXSmkVhyvtA41G8+IDYH0JPKmG0zovKSE6+QgJ8+sZ9Z7jyprbPa" +
            "KguWpZj2qfJh4IZHQVtPmED2jxgxY2xMlcclRvR6mR7MIU8vaYkPWGPwQPp2446iDH7zsFdUZ4wZRt0i2q+lzoTwkKSeMjNsrfEo" +
            "2hSTz8zJL0JYjob+L0pQAFuCTEk43uu+TaKs5uYnT9NuD3qN1XKIkffEa8+d1Sxk8YTdOayzf2gXpruBQ3jF227IWqJq+MMcUcoz" +
            "E3J551HaBbDPyiZh/QHB1KwB2Cgs/ZteY09gGTRMcCQkNBlY+Hrlibx1fVppUpUmehn8z1jEywM7GyDSnuAO6hOcSimtyko+anUS" +
            "3DZc7ravDQrC8KaskFANa91EwwwQLDze1xXP23+K1+RpIsfdSEZuS6sT7ltE5o4OB877mS6VRhx9rz23XxDPcf8WN5OAWEp6vo3+" +
            "63DMPopXj0EyiREWRJ82bajmOpaJEriDNtKKbXAqHUGgor7LprMCVjKwe3Ky9+ZnCirEfaC9wvH75ZCuPNx6ytfkG7jbDl4m3djN" +
            "9NeysqGyD4FZyFrtXrZAKnHhBkjbuTJjxNu9Tw6CxE3RzUT40TBFFTw0MQ1VRZAb1Vr0BYfVVWFAxLNJMqb8pRf0CvtbkrmTQjXb" +
            "ilYSchuTswPMZwQLnC4CfaXmBfwJ8H0fudC46uylvBMoojScatsO6I9CZWPspJOCo5fcDBZxX8Feu1h1vRiKedGhNe1pX8IBdhId" +
            "F+HYYJMyL6Dgw1CL2gNsIAsP9FOrGJkMxkbKQxQWb27DXT3D/FQAEvtnNAZ0M6grwwBHuVVEK1leHkruXSks+IaFI6RgeHNsqVEV" +
            "1h7i9YiDQZbkDk2VT81JvMJ4FV0dr5xs3yeGX0crDlvgmWvnkwCBPZhojzyvvssQrrV8HmftYaFzxgEzBUrXm5HLBrrSyEC2PLyr" +
            "ox2c+QWV26Aqi7tDz9jcqOOlGnm8oFFZe5qLxBj6X/AUpX5R6Xeqj92diLz9T/MhQuJukJi/N+A2cAvV3pEXrSevv1/0AXBo2e4t" +
            "8gy3fQDqw3ghqccMK";

        /// <summary>본보기 SQL 평문(실행 시에만 푼다). 풀지 못하면 빈 글.</summary>
        public static string DefaultSql
        {
            get { return SqlCipherLogic.Unprotect(DefaultSqlProtected) ?? ""; }
        }

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        // ================= 설정 =================

        public static CodeSpecSettings SettingsFrom(CodeSpecSettings previous, TableSpecDocument document, string templateId, string templatePath, string folder,
            CodeSpecOutput output, string sql, string dbId, string sqlFile, string groupPattern, bool groupPatternSearchName)
        {
            document = document ?? new TableSpecDocument();
            var settings = new CodeSpecSettings
            {
                Title = document.Title ?? "",
                Project = document.Project ?? "",
                System = document.System ?? "",
                Author = document.Author ?? "",
                Version = document.Version ?? "",
                Date = document.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Description = document.Description ?? "",
                TemplateId = templateId,
                TemplatePath = string.IsNullOrWhiteSpace(templatePath) ? null : templatePath,
                Folder = string.IsNullOrWhiteSpace(folder) ? null : folder,
                Output = OutputName(output),
                Sql = sql ?? "",
                GroupPattern = groupPattern ?? "",
                GroupPatternSearchName = groupPatternSearchName
            };
            if (previous != null && previous.SqlFileByDb != null)
            {
                foreach (var pair in previous.SqlFileByDb)
                    settings.SqlFileByDb[pair.Key] = pair.Value;
            }
            if (!string.IsNullOrEmpty(dbId))
            {
                if (string.IsNullOrWhiteSpace(sqlFile))
                    settings.SqlFileByDb.Remove(dbId);
                else
                    settings.SqlFileByDb[dbId] = sqlFile;
            }
            return settings;
        }

        /// <summary>설정 글. SQL은 <see cref="SqlCipherLogic"/>로 잠가 설정 파일에 순수 SQL이 보이지 않게 한다.</summary>
        public static string SerializeSettings(CodeSpecSettings settings)
        {
            if (settings == null)
                return null;
            var sql = settings.Sql;
            try
            {
                settings.Sql = SqlCipherLogic.Protect(sql);
                return JsonSerializer.Serialize(settings, JsonOptions);
            }
            finally
            {
                settings.Sql = sql;
            }
        }

        /// <summary>설정 글을 읽는다. 없거나 깨졌으면 null(창은 기본값으로 연다).</summary>
        public static CodeSpecSettings ParseSettings(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;
            try
            {
                var settings = JsonSerializer.Deserialize<CodeSpecSettings>(json, JsonOptions);
                if (settings != null && settings.SqlFileByDb == null)
                    settings.SqlFileByDb = new Dictionary<string, string>(StringComparer.Ordinal);
                if (settings != null)
                    settings.Sql = SqlCipherLogic.Unprotect(settings.Sql) ?? ""; // 잠긴 SQL은 풀고, 예전 평문은 그대로. 깨졌으면 비워 본보기로
                return settings;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>창에 채울 문서 정보: 저장값이 있으면 그 값, 없으면 기본값(제목 "공통 코드 정의서", 작성자 defaultAuthor, 버전 1.0, 작성일 today).</summary>
        public static TableSpecDocument DocumentFrom(CodeSpecSettings settings, string defaultAuthor, DateTime today)
        {
            if (settings == null)
                return new TableSpecDocument { Title = CodeSpecModel.DefaultTitle, Author = defaultAuthor, Date = today };
            return new TableSpecDocument
            {
                Title = settings.Title ?? CodeSpecModel.DefaultTitle,
                Project = settings.Project,
                System = settings.System,
                Author = settings.Author ?? defaultAuthor,
                Version = settings.Version ?? "1.0",
                Date = TableSpecLogic.ParseDate(settings.Date) ?? today,
                Description = settings.Description
            };
        }

        /// <summary>창을 열 때 보일 SQL: 그 접속의 마지막 SQL 파일(있고 읽히면) → 마지막 SQL 본문 → 본보기 SQL. sqlFile에는 쓴 파일 경로(없으면 null).</summary>
        public static string InitialSql(CodeSpecSettings settings, string dbId, Func<string, string> readFile, out string sqlFile)
        {
            sqlFile = null;
            if (settings == null)
                return DefaultSql;
            string path;
            if (!string.IsNullOrEmpty(dbId) && settings.SqlFileByDb != null && settings.SqlFileByDb.TryGetValue(dbId, out path) && !string.IsNullOrEmpty(path) && readFile != null)
            {
                try
                {
                    var text = readFile(path);
                    if (text != null)
                    {
                        sqlFile = path;
                        return text;
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // 파일을 옮겼거나 지웠다 — 마지막 본문으로
                }
            }
            return string.IsNullOrWhiteSpace(settings.Sql) ? DefaultSql : settings.Sql;
        }

        // ================= 출력 형식 =================

        public static string OutputName(CodeSpecOutput output)
        {
            switch (output)
            {
                case CodeSpecOutput.Json:
                    return "json";
                case CodeSpecOutput.Csv:
                    return "csv";
                default:
                    return "xlsx";
            }
        }

        /// <summary>"json"·"csv" → 그 형식, 그 밖(null 포함)은 엑셀.</summary>
        public static CodeSpecOutput OutputFrom(string name)
        {
            switch ((name ?? "").Trim().ToLowerInvariant())
            {
                case "json":
                    return CodeSpecOutput.Json;
                case "csv":
                    return CodeSpecOutput.Csv;
                default:
                    return CodeSpecOutput.Excel;
            }
        }

        public static string OutputLabel(CodeSpecOutput output)
        {
            switch (output)
            {
                case CodeSpecOutput.Json:
                    return "JSON";
                case CodeSpecOutput.Csv:
                    return "CSV";
                default:
                    return "엑셀 (양식)";
            }
        }

        /// <summary>저장 대화상자 필터.</summary>
        public static string SaveFilter(CodeSpecOutput output)
        {
            switch (output)
            {
                case CodeSpecOutput.Json:
                    return "JSON (*.json)|*.json";
                case CodeSpecOutput.Csv:
                    return "CSV (*.csv)|*.csv";
                default:
                    return "Excel 통합 문서 (*.xlsx)|*.xlsx";
            }
        }

        /// <summary>저장 파일 기본 이름: "{문서 제목}_{DB 이름}_{yyyyMMdd}.{xlsx|json|csv}". 파일 이름에 못 쓰는 글자는 _.</summary>
        public static string DefaultFileName(string title, string dbName, CodeSpecOutput output, DateTime date)
        {
            var name = TableSpecLogic.DefaultFileName(string.IsNullOrWhiteSpace(title) ? CodeSpecModel.DefaultTitle : title, dbName, null, date);
            return Path.GetFileNameWithoutExtension(name) + "." + OutputName(output);
        }

        // ================= 저장한 SQL =================

        /// <summary>플러그인 데이터 폴더의 SQL 폴더 경로. 데이터 폴더를 모르면 null.</summary>
        public static string SqlFolder(string dataDirectory)
        {
            return string.IsNullOrEmpty(dataDirectory) ? null : Path.Combine(dataDirectory, SqlFolderName);
        }

        /// <summary>SQL 폴더의 *.sql 전체 경로(파일 이름 순). 폴더가 없거나 읽을 수 없으면 빈 목록.</summary>
        public static List<string> ListSqlFiles(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                return new List<string>();
            try
            {
                return new DirectoryInfo(folder).GetFiles("*.sql")
                    .Where(f => (f.Attributes & FileAttributes.Hidden) == 0)
                    .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(f => f.FullName).ToList();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return new List<string>();
            }
        }

        /// <summary>목록에 보이는 이름(확장자 뺀 파일 이름).</summary>
        public static string SqlFileLabel(string path)
        {
            return string.IsNullOrEmpty(path) ? "" : Path.GetFileNameWithoutExtension(path);
        }

        // ================= 글 =================

        /// <summary>[열 확인] 뒤 상태 글.</summary>
        public static string CheckText(CodeSpecCheck check)
        {
            if (check == null)
                return "";
            if (!check.Ok)
                return check.Summary + " — SELECT 목록에 AS 별칭을 붙이세요.";
            return check.Summary + ".";
        }

        /// <summary>그룹 필터 뒤 글: "그룹 12개 중 5개 · 코드 48개".</summary>
        public static string FilterText(int shown, int total, int codes, bool filtered)
        {
            var text = filtered
                ? "그룹 " + total.ToString("N0", CultureInfo.InvariantCulture) + "개 중 " + shown.ToString("N0", CultureInfo.InvariantCulture) + "개"
                : "그룹 " + total.ToString("N0", CultureInfo.InvariantCulture) + "개";
            return text + " · 코드 " + codes.ToString("N0", CultureInfo.InvariantCulture) + "개";
        }

        /// <summary>
        /// 만들기 전에 막아야 할 빈 결과의 안내 글. 조회가 0행이거나 정규식에 맞는 그룹이 없으면 빈 정의서가 나오므로 만들지 않고 이유를 알린다. 괜찮으면 null.
        /// </summary>
        public static string EmptyText(int rows, int groups, bool filtered)
        {
            if (rows <= 0)
                return "조회 결과가 0행입니다 — WHERE 조건(공장 코드 등)을 확인하세요. 정의서를 만들지 않았습니다.";
            if (groups <= 0 && filtered)
                return "그룹 정규식에 맞는 그룹이 없습니다 — 정규식을 비우거나 고치세요. 정의서를 만들지 않았습니다.";
            return null;
        }

        /// <summary>다 만든 뒤 상태 글.</summary>
        public static string DoneText(int groups, int codes, CodeSpecOutput output, string path)
        {
            return "그룹 " + groups.ToString("N0", CultureInfo.InvariantCulture) + "개 · 코드 " + codes.ToString("N0", CultureInfo.InvariantCulture) + "개로 "
                + OutputLabel(output) + " 정의서를 만들었습니다: " + path;
        }

        /// <summary>
        /// [LLM 지침 복사]로 클립보드에 넣는 요청 글(GCM 양식). 별칭 계약과 양식 폴더가 들어 있다.
        /// </summary>
        public static string LlmPrompt(string guideUrl, string guideMarkdownUrl, string folder, string baseTemplate)
        {
            var lines = new List<string>
            {
                "DB Helper(Folderss 플러그인)의 GCM(공통 코드) 정의서 엑셀 양식을 만들어 줘.",
                "",
                "- 먼저 양식 작성 지침을 읽어: " + guideMarkdownUrl + " (사람용 페이지: " + guideUrl + ")",
                "- 만든 .xlsx는 이 폴더에 저장해: " + (string.IsNullOrEmpty(folder) ? "(DB Helper의 GCM 양식 폴더 — 팝업의 [양식 폴더]로 열 수 있음)" : folder),
                "  이 폴더의 .xlsx는 DB Helper의 [양식] 목록에 '내 양식 — 파일 이름'으로 보인다.",
                string.IsNullOrEmpty(baseTemplate)
                    ? "- 바탕 양식이 필요하면 DB Helper에서 기본 양식을 [복사해서 고치기]로 이 폴더에 꺼낸 파일을 고쳐."
                    : "- 이 양식을 바탕으로 고쳐 새 파일로 저장해(원본은 두고): " + baseTemplate,
                "- 변수는 $doc·$db·$source·$groups($group.codes)·$codes($code)만 쓴다. 추가 속성은 ${code.attrs.열이름} 또는 ${code.attrText}.",
                "- 지침의 검증 체크리스트를 모두 지켜(지시문 짝, 지시문 셀은 줄에 혼자, 변수 이름 철자, 시트 이름 31자).",
                "",
                "원하는 양식:",
                "(여기에 원하는 모양을 적으세요 — 예: 그룹마다 시트, 코드 표에 '비고' 칸, 추가 속성 PLANT_CD를 별도 열로)"
            };
            return string.Join(Environment.NewLine, lines);
        }

        /// <summary>LLM에게 "우리 GCM 테이블로 별칭 계약에 맞는 SQL을 써 달라"고 할 때 붙여 넣는 요청 글. product는 DBMS 이름(Oracle·PostgreSQL, 없으면 Oracle).</summary>
        public static string LlmSqlPrompt(string product = null)
        {
            var lines = new List<string>
            {
                "DB Helper(Folderss 플러그인)의 GCM 정의서용 " + (string.IsNullOrWhiteSpace(product) ? "Oracle" : product.Trim()) + " SELECT 문을 우리 공통 코드 테이블로 작성해 줘.",
                "",
                "결과 열의 별칭(AS)이 아래 계약과 맞아야 해(대소문자 무시). 플러그인은 SQL을 해석하지 않고 결과 열 이름만 본다.",
                ""
            };
            foreach (var alias in CodeSpecContract.Aliases)
                lines.Add("- " + alias.Name + (alias.Required ? " (필수)" : " (선택)") + ": " + alias.Description);
            lines.Add("- 그 밖의 열은 모두 추가 속성으로 정의서에 함께 나온다(예: PLANT_CD, LANG).");
            lines.Add("");
            lines.Add("바인드 변수는 쓰지 말고 값을 직접 적어. 끝에 ORDER BY 그룹, 정렬 순서, 코드.");
            lines.Add("");
            lines.Add("우리 테이블 구조:");
            lines.Add("(여기에 공통 코드 테이블·열 이름을 적으세요)");
            return string.Join(Environment.NewLine, lines);
        }
    }
}

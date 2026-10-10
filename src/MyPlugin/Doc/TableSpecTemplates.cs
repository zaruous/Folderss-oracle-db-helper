using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace MyPlugin
{
    /// <summary>양식 하나(대상 선택 창의 [양식] 목록): 플러그인 기본 양식 또는 사용자 양식 폴더의 .xlsx.</summary>
    internal sealed class SpecTemplate
    {
        public SpecTemplate(string id, string name, string description, string fileName, Func<byte[]> create, string path = null)
        {
            Id = id;
            Name = name;
            Description = description;
            FileName = fileName;
            Create = create;
            Path = path;
        }

        /// <summary>기본 양식은 "default"·"spec-sheet", 사용자 양식은 "user:파일이름.xlsx".</summary>
        public string Id { get; }
        /// <summary>목록에 보이는 이름.</summary>
        public string Name { get; }
        /// <summary>목록 아래 한 줄 설명.</summary>
        public string Description { get; }
        /// <summary>[복사해서 고치기]로 만들 파일 이름(확장자 포함).</summary>
        public string FileName { get; }
        /// <summary>템플릿 바이트. 사용자 양식은 부를 때마다 파일을 읽는다(엑셀에서 고친 것이 바로 반영).</summary>
        public Func<byte[]> Create { get; }
        /// <summary>사용자 양식 파일 경로. 기본 양식이면 null.</summary>
        public string Path { get; }

        public bool IsBuiltIn
        {
            get { return Path == null; }
        }

        public override string ToString()
        {
            return Name;
        }
    }

    /// <summary>
    /// 양식 목록: 플러그인에 들어 있는 기본 양식 + 사용자 양식 폴더(플러그인 데이터 폴더\templates)의 .xlsx.
    /// 사용자 양식 폴더에 .xlsx를 넣으면(직접 고친 것, 팀에서 받은 것, LLM이 만든 것) 다음에 목록을 펼칠 때 "내 양식 — 이름"으로 보인다.
    /// 엑셀이 여는 동안 만드는 잠금 파일(~$…)과 숨김 파일은 뺀다.
    /// </summary>
    internal static class TableSpecTemplates
    {
        public const string DefaultId = "default";
        public const string SpecSheetId = "spec-sheet";
        public const string UserPrefix = "user:";
        /// <summary>플러그인 데이터 폴더 아래 사용자 양식 폴더 이름.</summary>
        public const string FolderName = "templates";
        public const string UserNamePrefix = "내 양식 — ";

        /// <summary>LLM·사람용 양식 작성 지침(GitHub Pages).</summary>
        public const string GuideUrl = "https://zaruous.github.io/Folderss-oracle-db-helper/table-spec-templates/";
        /// <summary>같은 지침의 Markdown 원문(LLM이 읽기 좋은 형태).</summary>
        public const string GuideMarkdownUrl = "https://zaruous.github.io/Folderss-oracle-db-helper/table-spec-templates/guide.md";

        /// <summary>플러그인 기본 양식(맨 앞이 처음 고른 양식). 새 기본 양식은 여기에 한 줄 더한다.</summary>
        public static readonly IReadOnlyList<SpecTemplate> All = new[]
        {
            new SpecTemplate(DefaultId, "기본 — 표지 · 목록 · 테이블별 시트",
                "표지·개정 이력, 목록(시트 링크), 테이블마다 개요·열·인덱스·제약 조건",
                "테이블정의서_기본.xlsx", TableSpecTemplate.Create),
            new SpecTemplate(SpecSheetId, "명세서형 — 논리명 · 키 · 외래키 부록 · 공통 코드",
                "워크스코브 양식 구성: 표지(DBMS 버전·기준일·목록), 논리명·키, 외래키·인덱스 부록, 공통 코드, 개정 이력",
                "테이블명세서.xlsx", TableSpecSheetTemplate.Create)
        };

        /// <summary>플러그인 데이터 폴더의 사용자 양식 폴더 경로. 데이터 폴더를 모르면 null.</summary>
        public static string UserFolder(string dataDirectory)
        {
            return string.IsNullOrEmpty(dataDirectory) ? null : System.IO.Path.Combine(dataDirectory, FolderName);
        }

        /// <summary>기본 양식 + 사용자 양식(파일 이름 순). 폴더가 없거나 읽을 수 없으면 기본 양식만.</summary>
        public static List<SpecTemplate> List(string userFolder)
        {
            var list = new List<SpecTemplate>(All);
            if (string.IsNullOrEmpty(userFolder) || !Directory.Exists(userFolder))
                return list;
            try
            {
                var files = new DirectoryInfo(userFolder).GetFiles("*.xlsx")
                    .Where(f => !f.Name.StartsWith("~$", StringComparison.Ordinal) && (f.Attributes & FileAttributes.Hidden) == 0)
                    .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase);
                foreach (var file in files)
                    list.Add(User(file));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // 폴더를 읽지 못하면 기본 양식만 보인다
            }
            return list;
        }

        private static SpecTemplate User(FileInfo file)
        {
            var path = file.FullName;
            return new SpecTemplate(UserPrefix + file.Name, UserNamePrefix + System.IO.Path.GetFileNameWithoutExtension(file.Name),
                "사용자 양식 · " + file.Name + " · 고친 날 " + file.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                file.Name, () => ReadShared(path), path);
        }

        /// <summary>id의 양식. 없으면(지운 사용자 양식 등) 맨 앞(기본).</summary>
        public static SpecTemplate Find(IEnumerable<SpecTemplate> list, string id)
        {
            var items = (list ?? All).ToList();
            return items.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal)) ?? items.FirstOrDefault() ?? All[0];
        }

        /// <summary>기본 양식 중 id의 양식. 없으면 맨 앞.</summary>
        public static SpecTemplate Find(string id)
        {
            return Find(All, id);
        }

        /// <summary>folder 안에서 겹치지 않는 파일 이름: "기본.xlsx" → 있으면 "기본 (2).xlsx", "기본 (3).xlsx"…</summary>
        public static string CopyName(string folder, string fileName)
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(fileName ?? "양식");
            var extension = System.IO.Path.GetExtension(fileName ?? "");
            if (string.IsNullOrEmpty(extension))
                extension = ".xlsx";
            var candidate = name + extension;
            for (var n = 2; File.Exists(System.IO.Path.Combine(folder, candidate)); n++)
                candidate = name + " (" + n.ToString(CultureInfo.InvariantCulture) + ")" + extension;
            return candidate;
        }

        /// <summary>엑셀에서 열어 둔 파일도 읽을 수 있게 쓰기 공유로 읽는다.</summary>
        public static byte[] ReadShared(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var copy = new MemoryStream())
            {
                stream.CopyTo(copy);
                return copy.ToArray();
            }
        }
    }
}

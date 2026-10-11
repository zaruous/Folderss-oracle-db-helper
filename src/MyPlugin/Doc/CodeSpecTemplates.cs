using System.Collections.Generic;

namespace MyPlugin
{
    /// <summary>
    /// GCM 정의서 양식 목록: 플러그인 기본 양식 + 사용자 양식 폴더(플러그인 데이터 폴더\templates\gcm)의 .xlsx.
    /// 테이블 정의서 양식 폴더(templates) 바로 아래가 아니라 하위 폴더를 써서 두 목록이 섞이지 않는다.
    /// </summary>
    internal static class CodeSpecTemplates
    {
        public const string DefaultId = "gcm-default";
        public const string FlatId = "gcm-flat";
        /// <summary>테이블 정의서 양식 폴더 아래 GCM 양식 폴더 이름.</summary>
        public const string FolderName = "gcm";

        /// <summary>LLM·사람용 GCM 양식 작성 지침(GitHub Pages).</summary>
        public const string GuideUrl = "https://zaruous.github.io/Folderss-oracle-db-helper/code-spec-templates/";
        /// <summary>같은 지침의 Markdown 원문.</summary>
        public const string GuideMarkdownUrl = "https://zaruous.github.io/Folderss-oracle-db-helper/code-spec-templates/guide.md";

        /// <summary>플러그인 기본 양식(맨 앞이 처음 고른 양식).</summary>
        public static readonly IReadOnlyList<SpecTemplate> All = new[]
        {
            new SpecTemplate(DefaultId, "기본 — 표지 · 그룹 목록 · 그룹별 시트",
                "표지(출처 SQL·조회 시각), 그룹 목록(시트 링크), 그룹마다 코드 표(코드·명칭·설명·정렬·사용·추가 속성)",
                "GCM정의서_기본.xlsx", CodeSpecTemplate.Create),
            new SpecTemplate(FlatId, "단일 시트형 — 모든 코드 한 표",
                "표지와 코드 시트 하나(그룹·코드를 한 표에, 그룹이 바뀌면 그룹 줄)",
                "GCM정의서_단일시트.xlsx", CodeSpecTemplate.CreateFlat)
        };

        /// <summary>사용자 GCM 양식 폴더 경로(…\templates\gcm). 데이터 폴더를 모르면 null.</summary>
        public static string UserFolder(string dataDirectory)
        {
            var parent = TableSpecTemplates.UserFolder(dataDirectory);
            return parent == null ? null : System.IO.Path.Combine(parent, FolderName);
        }

        /// <summary>기본 양식 + 사용자 양식(파일 이름 순).</summary>
        public static List<SpecTemplate> List(string userFolder)
        {
            return TableSpecTemplates.List(userFolder, All);
        }

        /// <summary>id의 양식. 없으면 맨 앞(기본).</summary>
        public static SpecTemplate Find(IEnumerable<SpecTemplate> list, string id)
        {
            return TableSpecTemplates.Find(list ?? All, id);
        }
    }
}

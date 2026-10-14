using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text;

namespace MyPlugin
{
    /// <summary>소스 조회 한 행: PART(뷰·PROCEDURE·PACKAGE BODY 등 조각 구분), TEXT(그 조각의 원문 — Oracle은 한 줄).</summary>
    public sealed class SourcePart
    {
        public string Part { get; set; }
        public string Text { get; set; }
    }

    /// <summary>
    /// 뷰·코드 객체(프로시저·함수·패키지) 소스 보기의 공통 규칙(순수 로직, 테스트 대상).
    /// 공급자별 조회 SQL과 잇는 법은 <see cref="IMetadataProvider.Source"/>·<see cref="IMetadataProvider.BuildSource"/>.
    /// </summary>
    internal static class ObjectSource
    {
        /// <summary>소스 보기 대상: 뷰와 코드 묶음(PROCEDURE·FUNCTION·PACKAGE).</summary>
        public static bool Supports(string objectType)
        {
            return objectType == "VIEW" || (objectType != null && TreeGroups.GroupOf(objectType) == TreeGroups.Code);
        }

        public static SourcePart Read(IDataRecord record)
        {
            return new SourcePart { Part = Text(record, "PART"), Text = Text(record, "TEXT") };
        }

        /// <summary>
        /// Oracle ALL_SOURCE 줄들을 PART(PACKAGE·PACKAGE BODY …)가 바뀔 때마다 나눠 "CREATE OR REPLACE " + 원문 + '/' 줄로 잇는다.
        /// 원문 첫 줄은 "PROCEDURE 이름 …"처럼 스키마가 없다(넣으면 현재 스키마에 만들어진다).
        /// </summary>
        public static string JoinPlSql(IList<SourcePart> parts)
        {
            var blocks = new List<string>();
            StringBuilder current = null;
            string part = null;
            foreach (var line in parts)
            {
                if (current == null || !string.Equals(line.Part, part, StringComparison.Ordinal))
                {
                    if (current != null)
                        blocks.Add(current.ToString());
                    current = new StringBuilder();
                    part = line.Part;
                }
                current.Append(line.Text);
            }
            if (current != null)
                blocks.Add(current.ToString());
            var texts = blocks.Where(b => b.Trim().Length > 0).Select(b => "CREATE OR REPLACE " + b.Trim() + "\n/\n").ToList();
            return texts.Count == 0 ? null : string.Join("\n", texts);
        }

        /// <summary>조각마다 앞뒤 공백을 지우고 suffix(없으면 "")로 끝나게 해 빈 줄로 잇는다. 글이 없으면 null.</summary>
        public static string JoinStatements(IList<SourcePart> parts, string suffix)
        {
            var texts = parts.Select(p => (p.Text ?? "").Trim()).Where(t => t.Length > 0)
                .Select(t => (suffix.Length > 0 && !t.EndsWith(suffix, StringComparison.Ordinal) ? t + suffix : t) + "\n").ToList();
            return texts.Count == 0 ? null : string.Join("\n", texts);
        }

        /// <summary>창 제목·메시지에 쓰는 "스키마.이름"(따옴표 규칙이 DBMS마다 달라 따옴표 없이).</summary>
        public static string DisplayName(string owner, string name)
        {
            return string.IsNullOrEmpty(owner) ? name : owner + "." + name;
        }

        public static string NotFoundMessage(string display)
        {
            return "'" + display + "'의 소스를 읽을 수 없습니다(볼 권한이 없거나, 암호화된 객체이거나, 없는 객체입니다).";
        }

        public static string BusyMessage(string dbName)
        {
            return "'" + dbName + "'에서 실행 중이라 소스를 가져오지 않았습니다. 끝난 뒤 다시 여세요.";
        }

        public static string LoadingMessage(string name)
        {
            return name + " 소스를 가져오는 중…";
        }

        public static string FailedMessage(string reason)
        {
            return "소스를 가져오지 못했습니다: " + reason;
        }

        private static string Text(IDataRecord record, string name)
        {
            var value = record[name];
            return value == null || value is DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    /// <summary>
    /// GCM 본보기 SQL 잠금: 되돌리기, 평문 호환, 고친 글 거부, 본보기 SQL이 코드에 평문으로 없음, GCM 설정의 SQL이 잠김.
    /// 본보기 SQL을 바꿀 때는 <see cref="ProtectFile_Tool"/>을 환경 변수 DBH_PROTECT_IN(평문 파일)·DBH_PROTECT_OUT(결과 파일)으로 돌려
    /// 나온 글을 CodeSpecLogic.DefaultSqlProtected에 넣는다.
    /// </summary>
    public class SqlCipherLogicTests
    {
        [Fact]
        public void Protect_RoundTrips_AndHidesText()
        {
            var sql = "SELECT * FROM 주문 WHERE 상태 = 'A' -- 한글·기호 ∑ \r\n;";
            var stored = SqlCipherLogic.Protect(sql);

            Assert.StartsWith(SqlCipherLogic.Prefix, stored);
            Assert.True(SqlCipherLogic.IsProtected(stored));
            Assert.DoesNotContain("SELECT", stored);
            Assert.DoesNotContain("주문", stored);
            Assert.Equal(sql, SqlCipherLogic.Unprotect(stored));
            // 부를 때마다 다른 글(nonce) — 같은 SQL인지 파일만 봐서는 모른다
            Assert.NotEqual(stored, SqlCipherLogic.Protect(sql));
            Assert.Equal("", SqlCipherLogic.Unprotect(SqlCipherLogic.Protect("")));
            Assert.Null(SqlCipherLogic.Protect(null));
        }

        [Fact]
        public void Unprotect_PlainTextPassesThrough_TamperedIsNull()
        {
            Assert.Equal("SELECT 1", SqlCipherLogic.Unprotect("SELECT 1"));
            Assert.Null(SqlCipherLogic.Unprotect(null));
            Assert.Equal("", SqlCipherLogic.Unprotect(""));
            Assert.False(SqlCipherLogic.IsProtected("SELECT 1"));

            var stored = SqlCipherLogic.Protect("SELECT 1");
            var chars = stored.ToCharArray();
            var last = chars.Length - 2;
            chars[last] = chars[last] == 'A' ? 'B' : 'A';
            Assert.Null(SqlCipherLogic.Unprotect(new string(chars)));
            Assert.Null(SqlCipherLogic.Unprotect(SqlCipherLogic.Prefix + "not-base64!"));
            Assert.Null(SqlCipherLogic.Unprotect(SqlCipherLogic.Prefix + Convert.ToBase64String(new byte[5])));
        }

        [Fact]
        public void DefaultSql_IsStoredProtected_AndDecryptsToContractSql()
        {
            // 코드에는 잠근 글만 있다(배포 DLL·저장소에 사내 코드 테이블 구조가 평문으로 남지 않게)
            Assert.True(SqlCipherLogic.IsProtected(CodeSpecLogic.DefaultSqlProtected));
            var sql = CodeSpecLogic.DefaultSql;
            Assert.StartsWith("-- GCM", sql);
            Assert.Contains("AS TABLE_NAME", sql);
            Assert.Contains("AS CODE,", sql);
            Assert.Contains("AS CODE_NAME", sql);
            Assert.Contains("KEY_5_NAME", sql);
            Assert.Contains("DATA_10_NAME", sql);
            Assert.Contains("LEFT JOIN", sql);
            Assert.Contains("ORDER BY", sql);
            Assert.DoesNotContain("\n\r", sql);

            var source = File.ReadAllText(SourcePath("src", "MyPlugin", "Ui", "CodeSpecLogic.cs"));
            Assert.DoesNotContain("AS TABLE_NAME,", source);
        }

        [Fact]
        public void CodeSpecSettings_SqlIsProtected_ObjectUnchanged_LegacyPlainStillLoads()
        {
            var settings = new CodeSpecSettings { Title = "t", Sql = "SELECT a AS TABLE_NAME FROM hidden_table", SqlFileByDb = new Dictionary<string, string>() };
            var json = CodeSpecLogic.SerializeSettings(settings);

            Assert.DoesNotContain("hidden_table", json);
            Assert.Contains("\"title\":\"t\"", json);
            Assert.Equal("SELECT a AS TABLE_NAME FROM hidden_table", settings.Sql); // 직렬화가 원본을 바꾸지 않는다
            Assert.Equal("SELECT a AS TABLE_NAME FROM hidden_table", CodeSpecLogic.ParseSettings(json).Sql);

            var legacy = CodeSpecLogic.ParseSettings("{\"title\":\"t\",\"sql\":\"SELECT 1\"}");
            Assert.Equal("SELECT 1", legacy.Sql);
            var broken = CodeSpecLogic.ParseSettings("{\"title\":\"t\",\"sql\":\"" + SqlCipherLogic.Prefix + "AAAA\"}");
            Assert.Equal("", broken.Sql);
        }

        /// <summary>
        /// 도구: 환경 변수 DBH_PROTECT_IN의 평문 파일을 잠가 DBH_PROTECT_OUT에 쓴다(없으면 아무것도 하지 않음).
        /// 본보기 SQL을 고칠 때 쓴다: dotnet test --filter ProtectFile_Tool.
        /// </summary>
        [Fact]
        public void ProtectFile_Tool()
        {
            var input = Environment.GetEnvironmentVariable("DBH_PROTECT_IN");
            var output = Environment.GetEnvironmentVariable("DBH_PROTECT_OUT");
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(output) || !File.Exists(input))
                return;
            var plain = File.ReadAllText(input, Encoding.UTF8);
            var stored = SqlCipherLogic.Protect(plain);
            Assert.Equal(plain, SqlCipherLogic.Unprotect(stored));
            File.WriteAllText(output, stored, new UTF8Encoding(false));
        }

        private static string SourcePath(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "MyPlugin", "MyPlugin.csproj")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
        }
    }
}

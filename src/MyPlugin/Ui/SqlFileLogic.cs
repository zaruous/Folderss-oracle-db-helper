using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace MyPlugin
{
    /// <summary>SQL 파일 인코딩. 연 파일은 읽은 인코딩 그대로 저장한다(새 파일은 BOM 없는 UTF-8 — SQL*Plus 등이 BOM을 글자로 읽는다).</summary>
    internal enum SqlFileEncoding
    {
        Utf8,
        Utf8Bom,
        Utf16Le,
        Utf16Be,
        /// <summary>한국어 Windows 기본(EUC-KR 확장). 오래된 도구가 저장한 .sql 파일에 흔하다.</summary>
        Cp949
    }

    /// <summary>읽은 SQL 파일.</summary>
    internal sealed class SqlFileContent
    {
        public string Text { get; set; }
        public SqlFileEncoding Encoding { get; set; }
        /// <summary>파일의 줄바꿈("\r\n" 또는 "\n"). 저장할 때 되돌린다.</summary>
        public string Newline { get; set; }
    }

    /// <summary>
    /// SQL 파일 열기·저장과 최근 파일 목록(순수 로직 + 파일, 시험 대상).
    /// - 읽기: BOM(UTF-8·UTF-16)을 보고, 없으면 엄격한 UTF-8로 읽어 보고 깨지면 CP949로 읽는다. 너무 큰 파일(MaxFileBytes)은 열지 않는다.
    /// - 쓰기: 같은 폴더의 임시 파일에 쓴 뒤 바꿔치기(쓰다 끊겨도 원래 파일이 남음). CP949로 나타낼 수 없는 글자가 있으면 UTF-8로 저장한다.
    /// </summary>
    internal static class SqlFileLogic
    {
        /// <summary>열 수 있는 가장 큰 파일. 편집기(TextBox)가 감당할 수 있는 크기로 막는다.</summary>
        public const long MaxFileBytes = 5L * 1024 * 1024;

        public const int RecentLimit = 10;
        public const string RecentSettingKey = "recentSqlFiles";
        public const string FolderSettingKey = "lastSqlFolder";
        public const string DialogFilter = "SQL 파일 (*.sql)|*.sql|텍스트 파일 (*.txt)|*.txt|모든 파일 (*.*)|*.*";
        public const string DefaultExtension = ".sql";

        private const string TempSuffix = ".dbhelper-tmp";

        // 프로세스 전체 Encoding 등록(RegisterProvider)을 바꾸지 않고 이 공급자에서 직접 얻는다
        private static readonly Encoding Cp949 = CodePagesEncodingProvider.Instance.GetEncoding(949);
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        // ---------- 읽기 ----------

        public static SqlFileContent Read(string path)
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length > MaxFileBytes)
                throw new IOException(TooLargeMessage(info.Length));
            return Decode(File.ReadAllBytes(path));
        }

        public static SqlFileContent Decode(byte[] bytes)
        {
            bytes = bytes ?? new byte[0];
            string text;
            SqlFileEncoding encoding;
            if (StartsWith(bytes, 0xEF, 0xBB, 0xBF))
            {
                text = StrictUtf8Or(bytes, 3, Encoding.UTF8);
                encoding = SqlFileEncoding.Utf8Bom;
            }
            else if (StartsWith(bytes, 0xFF, 0xFE))
            {
                text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
                encoding = SqlFileEncoding.Utf16Le;
            }
            else if (StartsWith(bytes, 0xFE, 0xFF))
            {
                text = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
                encoding = SqlFileEncoding.Utf16Be;
            }
            else
            {
                try
                {
                    text = StrictUtf8.GetString(bytes);
                    encoding = SqlFileEncoding.Utf8;
                }
                catch (DecoderFallbackException)
                {
                    text = Cp949.GetString(bytes);
                    encoding = SqlFileEncoding.Cp949;
                }
            }
            return new SqlFileContent { Text = text, Encoding = encoding, Newline = DetectNewline(text) };
        }

        /// <summary>파일의 줄바꿈: "\r\n"이 있으면 그것, "\n"만 있으면 "\n", 줄바꿈이 없으면 Windows 기본 "\r\n".</summary>
        public static string DetectNewline(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "\r\n";
            var crlf = text.IndexOf("\r\n", StringComparison.Ordinal);
            if (crlf >= 0)
                return "\r\n";
            return text.IndexOf('\n') >= 0 ? "\n" : "\r\n";
        }

        // ---------- 쓰기 ----------

        /// <summary>글을 인코딩한다. CP949로 나타낼 수 없는 글자가 있으면 BOM 없는 UTF-8로 바꾸고 used로 알린다.</summary>
        public static byte[] Encode(string text, SqlFileEncoding encoding, out SqlFileEncoding used)
        {
            text = text ?? "";
            used = encoding;
            switch (encoding)
            {
                case SqlFileEncoding.Utf8Bom:
                    return Concat(new byte[] { 0xEF, 0xBB, 0xBF }, Encoding.UTF8.GetBytes(text));
                case SqlFileEncoding.Utf16Le:
                    return Concat(new byte[] { 0xFF, 0xFE }, Encoding.Unicode.GetBytes(text));
                case SqlFileEncoding.Utf16Be:
                    return Concat(new byte[] { 0xFE, 0xFF }, Encoding.BigEndianUnicode.GetBytes(text));
                case SqlFileEncoding.Cp949:
                    return TryCp949(text, out used);
                default:
                    return Encoding.UTF8.GetBytes(text);
            }
        }

        /// <summary>파일에 쓴다(임시 파일 → 바꿔치기). 실제로 쓴 인코딩을 돌려준다. 실패하면 예외(IOException·UnauthorizedAccessException 등).</summary>
        public static SqlFileEncoding Write(string path, string text, SqlFileEncoding encoding)
        {
            SqlFileEncoding used;
            var bytes = Encode(text, encoding, out used);
            var folder = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);
            var temp = path + TempSuffix;
            try
            {
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, path, true);
            }
            finally
            {
                if (File.Exists(temp))
                {
                    try
                    {
                        File.Delete(temp);
                    }
                    catch (Exception)
                    {
                        // 남은 임시 파일은 다음 저장 때 덮어쓴다
                    }
                }
            }
            return used;
        }

        // ---------- 최근 파일 ----------

        /// <summary>설정 값(줄마다 경로 하나) → 목록. 빈 줄·중복(대소문자 무시)은 빼고 RecentLimit개까지.</summary>
        public static List<string> ParseRecent(string setting)
        {
            var list = new List<string>();
            foreach (var line in (setting ?? "").Split('\n'))
            {
                var path = line.Trim();
                if (path.Length == 0 || list.Any(p => SamePath(p, path)))
                    continue;
                list.Add(path);
                if (list.Count >= RecentLimit)
                    break;
            }
            return list;
        }

        public static string SerializeRecent(IEnumerable<string> paths)
        {
            return string.Join("\n", (paths ?? Enumerable.Empty<string>()).Take(RecentLimit));
        }

        /// <summary>path를 맨 앞에 넣는다(이미 있으면 앞으로 옮김). RecentLimit개까지.</summary>
        public static List<string> AddRecent(IEnumerable<string> current, string path)
        {
            var list = new List<string>();
            if (!string.IsNullOrWhiteSpace(path))
                list.Add(path.Trim());
            foreach (var p in current ?? Enumerable.Empty<string>())
            {
                if (list.Count >= RecentLimit)
                    break;
                if (!string.IsNullOrWhiteSpace(p) && !list.Any(x => SamePath(x, p)))
                    list.Add(p);
            }
            return list;
        }

        /// <summary>
        /// 드래그 드롭으로 받은 경로 중 열 파일: 폴더·없는 파일은 빼고, 같은 파일은 한 번만(받은 순서). exists는 시험에서 바꾼다.
        /// </summary>
        public static List<string> DroppedFiles(IEnumerable<string> paths, Func<string, bool> exists = null)
        {
            exists = exists ?? File.Exists;
            var result = new List<string>();
            foreach (var path in paths ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(path) || !exists(path))
                    continue;
                if (!result.Any(p => SamePath(p, path)))
                    result.Add(path);
            }
            return result;
        }

        /// <summary>같은 파일인지(전체 경로, 대소문자 무시 — Windows 파일 시스템).</summary>
        public static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
                return false;
            return string.Equals(FullPath(a), FullPath(b), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>최근 파일 메뉴 글: "_1  orders.sql   D:\work\sql". 밑줄은 메뉴 단축 글자(1~9, 10번째는 0).</summary>
        public static string RecentMenuText(int index, string path)
        {
            var number = (index + 1) % 10;
            var name = Path.GetFileName(path);
            var folder = Path.GetDirectoryName(path) ?? "";
            // 파일 이름의 밑줄이 단축 글자로 먹히지 않게 두 번 쓴다
            return "_" + number.ToString(CultureInfo.InvariantCulture) + "  " + name.Replace("_", "__") + "   " + folder.Replace("_", "__");
        }

        // ---------- 이름·문장 ----------

        /// <summary>새 파일의 기본 이름: 탭 이름 + .sql(파일 이름에 못 쓰는 글자는 _).</summary>
        public static string DefaultFileName(string title)
        {
            var name = string.IsNullOrWhiteSpace(title) ? "query" : title.Trim();
            foreach (var c in Path.GetInvalidFileNameChars().Concat(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }))
                name = name.Replace(c, '_');
            return name.EndsWith(DefaultExtension, StringComparison.OrdinalIgnoreCase) ? name : name + DefaultExtension;
        }

        public static string EncodingName(SqlFileEncoding encoding)
        {
            switch (encoding)
            {
                case SqlFileEncoding.Utf8Bom: return "UTF-8(BOM)";
                case SqlFileEncoding.Utf16Le: return "UTF-16 LE";
                case SqlFileEncoding.Utf16Be: return "UTF-16 BE";
                case SqlFileEncoding.Cp949: return "CP949(EUC-KR)";
                default: return "UTF-8";
            }
        }

        public static string OpenedMessage(string path, SqlFileEncoding encoding)
        {
            return "열었습니다: " + path + " (" + EncodingName(encoding) + ")";
        }

        public static string SavedMessage(string path, SqlFileEncoding encoding)
        {
            return "저장했습니다: " + path + " (" + EncodingName(encoding) + ")";
        }

        public static string FallbackMessage(string path)
        {
            return "CP949로 나타낼 수 없는 글자가 있어 UTF-8로 저장했습니다: " + path;
        }

        public static string OpenFailedMessage(string path, string reason)
        {
            return "파일을 열지 못했습니다: " + path + " — " + reason;
        }

        public static string SaveFailedMessage(string path, string reason)
        {
            return "파일을 저장하지 못했습니다: " + path + " — " + reason;
        }

        public static string TooLargeMessage(long bytes)
        {
            return "파일이 너무 큽니다(" + (bytes / 1024 / 1024).ToString(CultureInfo.InvariantCulture) + "MB). "
                + (MaxFileBytes / 1024 / 1024).ToString(CultureInfo.InvariantCulture) + "MB까지 열 수 있습니다.";
        }

        public static string AlreadyOpenMessage(string path)
        {
            return "이미 열려 있는 파일이라 그 탭으로 옮겼습니다: " + path;
        }

        // ---------- 내부 ----------

        private static string FullPath(string path)
        {
            try
            {
                return Path.GetFullPath(path.Trim());
            }
            catch (Exception)
            {
                return path.Trim();
            }
        }

        private static bool StartsWith(byte[] bytes, params byte[] prefix)
        {
            if (bytes.Length < prefix.Length)
                return false;
            for (var i = 0; i < prefix.Length; i++)
            {
                if (bytes[i] != prefix[i])
                    return false;
            }
            return true;
        }

        private static string StrictUtf8Or(byte[] bytes, int offset, Encoding fallback)
        {
            try
            {
                return StrictUtf8.GetString(bytes, offset, bytes.Length - offset);
            }
            catch (DecoderFallbackException)
            {
                return fallback.GetString(bytes, offset, bytes.Length - offset);
            }
        }

        private static byte[] TryCp949(string text, out SqlFileEncoding used)
        {
            var bytes = Cp949.GetBytes(text);
            // 되읽어 같으면 CP949로 나타낼 수 있는 글이다(없는 글자는 ?로 바뀐다)
            if (string.Equals(Cp949.GetString(bytes), text, StringComparison.Ordinal))
            {
                used = SqlFileEncoding.Cp949;
                return bytes;
            }
            used = SqlFileEncoding.Utf8;
            return Encoding.UTF8.GetBytes(text);
        }

        private static byte[] Concat(byte[] a, byte[] b)
        {
            var result = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, result, 0, a.Length);
            Buffer.BlockCopy(b, 0, result, a.Length, b.Length);
            return result;
        }
    }
}

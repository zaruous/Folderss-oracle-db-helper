using System;
using System.Security.Cryptography;
using System.Text;

namespace MyPlugin
{
    /// <summary>
    /// 디스크에 남는 SQL(임시 저장 탭·GCM 창의 마지막 SQL)을 그대로 보이지 않게 잠근다. 키는 플러그인 안에 고정되어 있다 —
    /// 비밀을 지키는 장치가 아니라 설정 파일을 열어 봤을 때 순수 SQL이 드러나지 않게 하는 용도다(사용자 요구).
    /// 형식: "dbh1:" + Base64(nonce 12바이트 + tag 16바이트 + 암호문). AES-GCM이라 고친 파일은 풀리지 않는다(null).
    /// 접두사가 없는 글은 예전 평문으로 보고 그대로 돌려준다(이전 버전이 남긴 파일과 호환).
    /// </summary>
    internal static class SqlCipherLogic
    {
        public const string Prefix = "dbh1:";

        private const int NonceSize = 12;
        private const int TagSize = 16;

        // 고정 키: 플러그인 Id에서 뽑는다(32바이트). 바꾸면 이전에 잠근 파일을 못 읽는다
        private static readonly byte[] Key = SHA256.HashData(Encoding.UTF8.GetBytes("zaruous.folderss-db-helper/sql-at-rest/v1"));

        public static bool IsProtected(string text)
        {
            return text != null && text.StartsWith(Prefix, StringComparison.Ordinal);
        }

        /// <summary>글을 잠근다. null은 null. 같은 글이라도 부를 때마다 다른 결과(nonce).</summary>
        public static string Protect(string text)
        {
            if (text == null)
                return null;
            var plain = Encoding.UTF8.GetBytes(text);
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var cipher = new byte[plain.Length];
            var tag = new byte[TagSize];
            using (var aes = new AesGcm(Key, TagSize))
                aes.Encrypt(nonce, plain, cipher, tag);
            var packed = new byte[NonceSize + TagSize + cipher.Length];
            Buffer.BlockCopy(nonce, 0, packed, 0, NonceSize);
            Buffer.BlockCopy(tag, 0, packed, NonceSize, TagSize);
            Buffer.BlockCopy(cipher, 0, packed, NonceSize + TagSize, cipher.Length);
            return Prefix + Convert.ToBase64String(packed);
        }

        /// <summary>잠근 글을 푼다. 접두사가 없으면 평문으로 보고 그대로. 깨졌거나 고쳐진 글이면 null.</summary>
        public static string Unprotect(string stored)
        {
            if (!IsProtected(stored))
                return stored;
            try
            {
                var packed = Convert.FromBase64String(stored.Substring(Prefix.Length));
                if (packed.Length < NonceSize + TagSize)
                    return null;
                var nonce = new byte[NonceSize];
                var tag = new byte[TagSize];
                var cipher = new byte[packed.Length - NonceSize - TagSize];
                Buffer.BlockCopy(packed, 0, nonce, 0, NonceSize);
                Buffer.BlockCopy(packed, NonceSize, tag, 0, TagSize);
                Buffer.BlockCopy(packed, NonceSize + TagSize, cipher, 0, cipher.Length);
                var plain = new byte[cipher.Length];
                using (var aes = new AesGcm(Key, TagSize))
                    aes.Decrypt(nonce, cipher, tag, plain);
                return Encoding.UTF8.GetString(plain);
            }
            catch (FormatException)
            {
                return null;
            }
            catch (CryptographicException)
            {
                return null;
            }
        }
    }
}

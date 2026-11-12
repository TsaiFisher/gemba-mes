using System.Security.Cryptography;
using System.Text;

namespace SmartFactory.MES.Services;

/// <summary>
/// <c>ConnectionStrings:DefaultEncrypted</c> 的加解密（AES-256-GCM；金鑰 PBKDF2-SHA256 10 萬次、隨機 salt）。
/// 單一來源：以 <c>&lt;Compile Include Link&gt;</c> 共用於 Security DLL、LicenseAuthority、tools/EncryptConnectionString。
///
/// <list type="bullet">
///   <item><c>ENC:v2</c>：金鑰材料＝指紋＋授權編號。兩者在複製出來的磁碟上都拿得到。</item>
///   <item><c>ENC:v3</c>（§18.15.9）：再加上主機板 UUID <b>原值</b>。授權檔與申請碼只有它的雜湊，
///         所以複製品就算改掉程式裡的主機檢查，也推不出金鑰、拿不到資料庫密碼。</item>
/// </list>
/// 授權有列主機識別碼時只接受 v3（<paramref name="hostBound"/>）；沒有列的（CI、內部 CI 授權）照舊用 v2。
/// </summary>
public static class ConnectionStringCipher
{
    public const string V2Prefix = "ENC:v2:";
    public const string V3Prefix = "ENC:v3:";

    private const int Iterations = 100_000;

    /// <summary><paramref name="hostUuid"/> 不為 null 時產生 v3，否則 v2。</summary>
    public static string Encrypt(string plainText, string fingerprint, string licenseId, string? hostUuid)
    {
        var salt       = RandomNumberGenerator.GetBytes(16);
        var nonce      = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var cipher     = new byte[plainBytes.Length];
        var tag        = new byte[AesGcm.TagByteSizes.MaxSize];

        using (var gcm = new AesGcm(DeriveKey(fingerprint, licenseId, hostUuid, salt), AesGcm.TagByteSizes.MaxSize))
            gcm.Encrypt(nonce, plainBytes, cipher, tag);

        return (hostUuid is null ? V2Prefix : V3Prefix) +
               $"{Convert.ToBase64String(salt)}:{Convert.ToBase64String(nonce)}:" +
               $"{Convert.ToBase64String(cipher)}:{Convert.ToBase64String(tag)}";
    }

    /// <param name="hostUuid">本機主機板 UUID 原值（v3 必填）。</param>
    /// <param name="hostBound">授權有列主機識別碼：拒收 v2——否則複製品沿用舊密文就繞過了主機綁定。</param>
    public static string Decrypt(string cipherText, string fingerprint, string licenseId, string? hostUuid, bool hostBound)
    {
        string body;
        if (cipherText.StartsWith(V3Prefix, StringComparison.Ordinal))
        {
            if (hostUuid is null)
                throw new InvalidOperationException(
                    "連線字串為 ENC:v3（綁定主機），但此授權沒有列主機識別碼或讀不到本機主機識別碼，無法解密。");
            body = cipherText[V3Prefix.Length..];
        }
        else if (cipherText.StartsWith(V2Prefix, StringComparison.Ordinal))
        {
            if (hostBound)
                throw new InvalidOperationException(
                    "此授權綁定主機，連線字串必須以 ENC:v3 重新加密：在這台主機執行 tools/EncryptConnectionString，加 --this-host。\n" +
                    "ENC:v2 的金鑰只由指紋與授權編號推導，複製出來的環境也解得開。");
            body = cipherText[V2Prefix.Length..];
            hostUuid = null;
        }
        else
            throw new InvalidOperationException(
                "連線字串格式錯誤（需以 ENC:v3: 或 ENC:v2: 開頭）。請在這台主機以 tools/EncryptConnectionString 重新加密" +
                "（授權有列主機識別碼加 --this-host，沒有則加 --no-host-binding）。");

        var parts = body.Split(':');
        if (parts.Length != 4)
            throw new InvalidOperationException("連線字串格式錯誤（應為 ENC:v3:{salt}:{nonce}:{cipher}:{tag}）。");

        var salt   = Convert.FromBase64String(parts[0]);
        var nonce  = Convert.FromBase64String(parts[1]);
        var cipher = Convert.FromBase64String(parts[2]);
        var tag    = Convert.FromBase64String(parts[3]);

        var plain = new byte[cipher.Length];
        using var gcm = new AesGcm(DeriveKey(fingerprint, licenseId, hostUuid, salt), AesGcm.TagByteSizes.MaxSize);
        gcm.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] DeriveKey(string fingerprint, string licenseId, string? hostUuid, byte[] salt)
    {
        // v2 的金鑰材料維持原樣（既有密文要解得開）；v3 以分隔字串附加，避免與 v2 的拼接撞在一起
        var material = hostUuid is null ? fingerprint + licenseId : $"{fingerprint}{licenseId}|host:v1:{hostUuid}";
        // 靜態 Pbkdf2 與舊的 new Rfc2898DeriveBytes(...).GetBytes(32) 輸出相同（字串一樣以 UTF-8 編碼）；
        // 舊建構子已過時（SYSLIB0060），執行加密工具時會印出建置警告，使用者以為出錯（2026-11-12 rc3 裝機回報）。
        // 相容性由 ConnectionStringCipherGoldenTests 的舊密文守住。
        return Rfc2898DeriveBytes.Pbkdf2(material, salt, Iterations, HashAlgorithmName.SHA256, 32);
    }
}

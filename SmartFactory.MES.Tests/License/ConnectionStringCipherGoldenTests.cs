using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.License;

/// <summary>
/// 既有密文必須永遠解得開：金鑰衍生（PBKDF2-SHA256、10 萬次、32 bytes）一改，
/// 所有部署的 DefaultEncrypted 會同時失效，而且只在啟動時才發現。
///
/// <para>下面兩組密文是 2026-11-12 以**舊寫法**（<c>new Rfc2898DeriveBytes(...).GetBytes(32)</c>，SYSLIB0060 已過時）
/// 經加密工具產生；改用 <c>Rfc2898DeriveBytes.Pbkdf2</c> 之後仍須解得出同一段明文。
/// 只是測試向量：指紋、授權編號與主機 UUID 都是假的。</para>
/// </summary>
public class ConnectionStringCipherGoldenTests
{
    private static readonly string Fingerprint = new('A', 64);
    private const string LicenseId = "LIC-GOLDEN-TEST";
    private const string HostUuid = "4c4c4544-0042-3510-8051-b4c04f4e3232";
    private const string Plaintext = "Host=golden;Database=vector;Username=u;Password=p";

    [Fact]
    public void V3_HostBound_LegacyCiphertext_StillDecrypts()
    {
        const string v3 = "ENC:v3:8NKgM6Ely+k+R8bejk7xvg==:MuDmF9bUjL92g6/s:0YbaN3xEYj7nGJu6r5WHJ3RvYNCfBH0aHl5zqsnPj86fkuUqGsvlrlNj8vr9/qHXpw==:C4AZcnEVRQKYKr65r2JZaA==";
        Assert.Equal(Plaintext, ConnectionStringCipher.Decrypt(v3, Fingerprint, LicenseId, HostUuid, hostBound: true));
    }

    [Fact]
    public void V2_LegacyCiphertext_StillDecrypts()
    {
        const string v2 = "ENC:v2:zQRDF978MP20FdxLmKZhkg==:WxhtASfT30VXjSZD:x8Fc6vtUTRo5e6MMebctemjM8T8fzMFtpoCOjGYexFta5Y4kr64y2C8fMokR3RXGkA==:fz4ZpMUt6ZTwt2k399qIfg==";
        Assert.Equal(Plaintext, ConnectionStringCipher.Decrypt(v2, Fingerprint, LicenseId, null, hostBound: false));
    }
}

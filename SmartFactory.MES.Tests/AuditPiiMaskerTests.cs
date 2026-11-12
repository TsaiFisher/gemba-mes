using SmartFactory.MES.Data;
using Xunit;

namespace SmartFactory.MES.Tests
{
    /// <summary>
    /// M5：AuditLog PII 遮罩純函式測試。
    /// </summary>
    public class AuditPiiMaskerTests
    {
        [Theory]
        [InlineData("john.doe@example.com", "j*******@example.com")]
        [InlineData("a@b.com", "*@b.com")]
        [InlineData("ab@corp.com", "a*@corp.com")]
        public void MaskEmail_KeepsFirstCharAndDomain(string input, string expected)
        {
            Assert.Equal(expected, AuditPiiMasker.MaskEmail(input));
        }

        [Fact]
        public void MaskEmail_Malformed_NoAt_FallsBackToGeneric()
        {
            // 無 '@' → 一般遮罩（末 3 碼），不外洩完整內容
            Assert.Equal("*******ail", AuditPiiMasker.MaskEmail("notanemail"));
        }

        [Fact]
        public void MaskEmail_LeadingAt_FallsBackToGeneric()
        {
            // "@x.com" 本地部分為空 → 一般遮罩，避免露出完整字串
            Assert.Equal("***com", AuditPiiMasker.MaskEmail("@x.com"));
        }

        [Theory]
        [InlineData("0912345678", "*******678")]
        [InlineData("12", "**")]
        [InlineData("123", "***")]
        [InlineData("1234", "*234")]
        public void MaskGeneric_KeepsLastThree(string input, string expected)
        {
            Assert.Equal(expected, AuditPiiMasker.MaskGeneric(input));
        }

        [Fact]
        public void Mask_EmailProperty_IsMasked()
        {
            Assert.Equal("j***@corp.com", AuditPiiMasker.Mask("Email", "john@corp.com"));
        }

        [Fact]
        public void Mask_PhoneNumberProperty_IsMasked()
        {
            Assert.Equal("*******678", AuditPiiMasker.Mask("PhoneNumber", "0912345678"));
        }

        [Fact]
        public void Mask_PropertyNameCaseInsensitive()
        {
            Assert.Equal("j***@corp.com", AuditPiiMasker.Mask("email", "john@corp.com"));
            Assert.Equal("*******678", AuditPiiMasker.Mask("phonenumber", "0912345678"));
        }

        [Fact]
        public void Mask_NonPiiProperty_ReturnsUnchanged()
        {
            Assert.Equal("Admin", AuditPiiMasker.Mask("UserName", "Admin"));
            Assert.Equal("zh-TW", AuditPiiMasker.Mask("Language", "zh-TW"));
        }

        [Fact]
        public void Mask_NullValue_ReturnsNull()
        {
            Assert.Null(AuditPiiMasker.Mask("Email", null));
        }

        [Fact]
        public void Mask_EmptyString_ReturnsUnchanged()
        {
            Assert.Equal(string.Empty, AuditPiiMasker.Mask("Email", string.Empty));
        }

        [Fact]
        public void Mask_NonStringValue_ReturnsUnchanged()
        {
            // 非字串型別（理論上不會有名為 Email 的 int，但保守處理）
            Assert.Equal(123, AuditPiiMasker.Mask("Email", 123));
        }

        [Fact]
        public void IsMaskedProperty_ReportsPiiNames()
        {
            Assert.True(AuditPiiMasker.IsMaskedProperty("Email"));
            Assert.True(AuditPiiMasker.IsMaskedProperty("PhoneNumber"));
            Assert.False(AuditPiiMasker.IsMaskedProperty("UserName"));
        }
    }
}

using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests
{
    /// <summary>
    /// L5：上傳影像 magic bytes 檔頭簽章驗證。
    /// </summary>
    public class ImageSignatureValidatorTests
    {
        [Fact]
        public void Jpeg_Signature_Accepted()
        {
            byte[] head = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46 };
            Assert.True(ImageSignatureValidator.MatchesImageSignature(head));
        }

        [Fact]
        public void Png_Signature_Accepted()
        {
            byte[] head = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            Assert.True(ImageSignatureValidator.MatchesImageSignature(head));
        }

        [Fact]
        public void Gif_Signature_Accepted()
        {
            byte[] head = { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00 };
            Assert.True(ImageSignatureValidator.MatchesImageSignature(head));
        }

        [Fact]
        public void Webp_Signature_Accepted()
        {
            byte[] head = { 0x52, 0x49, 0x46, 0x46, 0x24, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50 };
            Assert.True(ImageSignatureValidator.MatchesImageSignature(head));
        }

        [Fact]
        public void Svg_Xml_Text_Rejected()
        {
            // "<?xml v" — SVG/XML 開頭，非點陣影像
            byte[] head = { 0x3C, 0x3F, 0x78, 0x6D, 0x6C, 0x20, 0x76 };
            Assert.False(ImageSignatureValidator.MatchesImageSignature(head));
        }

        [Fact]
        public void Html_Polyglot_Rejected()
        {
            // "<html>" 改成 .png 副檔名上傳的偽裝檔
            byte[] head = { 0x3C, 0x68, 0x74, 0x6D, 0x6C, 0x3E };
            Assert.False(ImageSignatureValidator.MatchesImageSignature(head));
        }

        [Fact]
        public void Riff_Without_Webp_Rejected()
        {
            // RIFF 容器但非 WEBP（如 WAV：RIFF....WAVE）
            byte[] head = { 0x52, 0x49, 0x46, 0x46, 0x24, 0x00, 0x00, 0x00, 0x57, 0x41, 0x56, 0x45 };
            Assert.False(ImageSignatureValidator.MatchesImageSignature(head));
        }

        [Fact]
        public void TooShort_Rejected()
        {
            byte[] head = { 0xFF, 0xD8 };
            Assert.False(ImageSignatureValidator.MatchesImageSignature(head));
        }
    }
}

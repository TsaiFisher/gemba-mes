using Microsoft.AspNetCore.Http;

namespace SmartFactory.MES.Services
{
    /// <summary>
    /// L5（security-assessment-and-plan.md）：上傳影像的 magic bytes（檔頭簽章）驗證。
    /// 副檔名與 content-type 皆可偽造，故實際比對檔頭確認內容確為允許的點陣影像，
    /// 擋改副檔名的 polyglot／偽裝檔。刻意用檔頭比對而非引入影像解碼相依，
    /// 以免擴大套件漏洞掃描面（H3）。
    /// </summary>
    public static class ImageSignatureValidator
    {
        /// <summary>讀取上傳檔前置位元組並判斷是否為 JPEG/PNG/GIF/WebP。</summary>
        public static bool HasAllowedImageSignature(IFormFile file)
        {
            Span<byte> head = stackalloc byte[12];
            int read;
            using (var stream = file.OpenReadStream())
                read = ReadFully(stream, head);
            return MatchesImageSignature(head[..read]);
        }

        private static int ReadFully(Stream stream, Span<byte> buffer)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int n = stream.Read(buffer[total..]);
                if (n == 0) break;
                total += n;
            }
            return total;
        }

        /// <summary>純函式：依檔頭位元組判斷是否為 JPEG/PNG/GIF/WebP。</summary>
        public static bool MatchesImageSignature(ReadOnlySpan<byte> head)
        {
            // JPEG: FF D8 FF
            if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
                return true;
            // PNG: 89 50 4E 47 0D 0A 1A 0A
            if (head.Length >= 8 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E &&
                head[3] == 0x47 && head[4] == 0x0D && head[5] == 0x0A && head[6] == 0x1A && head[7] == 0x0A)
                return true;
            // GIF: "GIF8"
            if (head.Length >= 4 && head[0] == 0x47 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x38)
                return true;
            // WebP: "RIFF"...."WEBP"
            if (head.Length >= 12 && head[0] == 0x52 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x46 &&
                head[8] == 0x57 && head[9] == 0x45 && head[10] == 0x42 && head[11] == 0x50)
                return true;
            return false;
        }
    }
}

using QRCoder;

namespace SmartFactory.MES.Services
{
    public static class PrintHelper
    {
        /// <summary>
        /// 產生 QR Code SVG 字串（向量格式，列印不失真）。
        /// 可直接以 @Html.Raw(ViewBag.QrSvg) 內嵌於 View。
        /// </summary>
        /// <param name="content">QR Code 內容（URL 或純文字）</param>
        /// <param name="pixelsPerModule">每個 module 的像素大小，建議 4–6</param>
        public static string GenerateQrSvg(string content, int pixelsPerModule = 5)
        {
            using var generator = new QRCodeGenerator();
            var qrData = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
            var svgCode = new SvgQRCode(qrData);
            var svg = svgCode.GetGraphic(pixelsPerModule);

            // QRCoder 輸出的 SVG 沒有 viewBox，CSS width/height 只裁切不縮放。
            // 加入 viewBox 並改為 100%，讓外層容器的 CSS 尺寸控制顯示大小。
            var m = System.Text.RegularExpressions.Regex.Match(svg, @"width=""(\d+)"" height=""(\d+)""");
            if (m.Success)
            {
                var w = m.Groups[1].Value;
                var h = m.Groups[2].Value;
                svg = svg.Replace(
                    $@"width=""{w}"" height=""{h}""",
                    $@"viewBox=""0 0 {w} {h}"" width=""100%"" height=""100%"""
                );
            }
            return svg;
        }
    }
}

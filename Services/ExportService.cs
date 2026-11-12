using ClosedXML.Excel;
using System.Text;
using System.Text.Json;

namespace SmartFactory.MES.Services
{
    public interface IExportService
    {
        /// <summary>產生 Excel (.xlsx) 檔案位元組</summary>
        byte[] ToExcel(ExportRequest request);

        /// <summary>產生 CSV 字串（UTF-8 BOM，相容 Excel 開啟中文）</summary>
        byte[] ToCsv(ExportRequest request);

        /// <summary>產生 JSON 位元組</summary>
        byte[] ToJson(ExportRequest request);
    }

    /// <summary>匯出資料結構</summary>
    public class ExportRequest
    {
        /// <summary>工作表 / 檔案標題</summary>
        public string Title { get; set; } = "Export";

        /// <summary>欄位標題（與 Rows 的每個 object[] 順序對應）</summary>
        public string[] Headers { get; set; } = [];

        /// <summary>資料列，每列為 object?[]</summary>
        public IEnumerable<object?[]> Rows { get; set; } = [];
    }

    public class ExportService : IExportService
    {
        public byte[] ToExcel(ExportRequest request)
        {
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add(request.Title.Length > 31
                ? request.Title[..31]   // Excel 工作表名稱最長 31 字元
                : request.Title);

            // 標題列
            for (int i = 0; i < request.Headers.Length; i++)
            {
                var cell = ws.Cell(1, i + 1);
                cell.Value = request.Headers[i];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#4472C4");
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            }

            // 資料列
            int row = 2;
            foreach (var dataRow in request.Rows)
            {
                for (int col = 0; col < dataRow.Length; col++)
                {
                    var val = dataRow[col];
                    var cell = ws.Cell(row, col + 1);
                    switch (val)
                    {
                        case DateTime dt:
                            cell.Value = dt;
                            cell.Style.DateFormat.Format = "yyyy-MM-dd HH:mm:ss";
                            break;
                        case DateOnly d:
                            cell.Value = d.ToDateTime(TimeOnly.MinValue);
                            cell.Style.DateFormat.Format = "yyyy-MM-dd";
                            break;
                        case bool b:
                            cell.Value = b;
                            break;
                        case int or long or decimal or double or float:
                            cell.Value = XLCellValue.FromObject(val);
                            break;
                        default:
                            cell.Value = val?.ToString() ?? "";
                            break;
                    }
                }
                row++;
            }

            // 自動欄寬、凍結標題、自動篩選
            ws.ColumnsUsed().AdjustToContents();
            ws.SheetView.FreezeRows(1);
            if (ws.RangeUsed() != null)
                ws.RangeUsed()!.SetAutoFilter();

            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            return ms.ToArray();
        }

        public byte[] ToCsv(ExportRequest request)
        {
            var sb = new StringBuilder();

            // 標題列
            sb.AppendLine(string.Join(",", request.Headers.Select(EscapeCsv)));

            // 資料列
            foreach (var row in request.Rows)
            {
                sb.AppendLine(string.Join(",", row.Select(v => EscapeCsv(v?.ToString() ?? ""))));
            }

            // UTF-8 BOM（讓 Excel 正確識別中文）
            return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        }

        public byte[] ToJson(ExportRequest request)
        {
            var list = request.Rows.Select(row =>
            {
                var dict = new Dictionary<string, object?>();
                for (int i = 0; i < request.Headers.Length && i < row.Length; i++)
                    dict[request.Headers[i]] = row[i];
                return dict;
            }).ToList();

            return JsonSerializer.SerializeToUtf8Bytes(list, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        }

        private static string EscapeCsv(string? value)
        {
            if (value == null) return "";
            // CSV Injection 防護（OWASP）：=、+、-、@ 開頭的值前置單引號，
            // 防止 Excel 將其解析為公式執行（DDE / HYPERLINK 攻擊向量）。
            if (value.Length > 0 &&
                (value[0] == '=' || value[0] == '+' || value[0] == '-' || value[0] == '@'))
                value = "'" + value;

            if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
                return $"\"{value.Replace("\"", "\"\"")}\"";
            return value;
        }
    }
}

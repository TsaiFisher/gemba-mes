using SmartFactory.MES.Modules._Shared;
using System.ComponentModel.DataAnnotations;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.PUR.ViewModels
{
    /// <summary>採購單 Side Panel 表單 ViewModel</summary>
    public class PurchaseOrderFormVm : IValidatableObject
    {
        public string? PurchaseOrderId { get; set; }
        /// <summary>xmin 樂觀並發 token（對應 Entity.xmin；見 ord-module.md §13）</summary>
        public uint RowVersion { get; set; }
        public DocumentApprovalStatus ApprovalStatus { get; set; } = DocumentApprovalStatus.Draft;

        [Required]
        public DateOnly OrderDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);

        [Required, MaxLength(450)]
        public string CreatedBy { get; set; } = string.Empty;

        [Required, MaxLength(20)]
        public string SupplierCode { get; set; } = string.Empty;

        [MaxLength(300)]
        /// <summary>貿易條件（Incoterms，CodeEntry: <c>trade_term</c>）。語意見對應 entity 的文件。</summary>
        public string? TradeTermCode { get; set; }

        public string? DeliveryAddress { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        public List<PurchaseOrderLineVm> Lines { get; set; } = new();

        /// <summary>
        /// 明細層條件必填。幣別與稅別留空會讓金額以本幣、零稅率靜默結算，
        /// 採購單位留空則庫存換算率直接視為 1（見 UomService.GetConversionRateAsync），
        /// 因此只要該行有金額就必須指定；金額為 0 的行（如僅佔位）不強制。
        /// </summary>
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (Lines.Count == 0)
            {
                yield return new ValidationResult(PzText.Get("Pur_OrderNeedsLines"));
                yield break;
            }

            for (int i = 0; i < Lines.Count; i++)
            {
                var line = Lines[i];
                var hasAmount = line.UnitPrice != 0m || line.TotalAmount != 0m;
                if (!hasAmount) continue;

                if (string.IsNullOrWhiteSpace(line.CurrencyCode))
                    yield return new ValidationResult(
                        PzText.Get("Pur_LineCurrencyRequiredFmt", i + 1));

                if (string.IsNullOrWhiteSpace(line.TaxCode))
                    yield return new ValidationResult(
                        PzText.Get("Pur_LineTaxRequiredFmt", i + 1));

                if (string.IsNullOrWhiteSpace(line.UomCode))
                    yield return new ValidationResult(
                        PzText.Get("Pur_LineUomRequiredFmt", i + 1));

                if (line.UnitPrice < 0m)
                    yield return new ValidationResult(
                        PzText.Get("Pur_LinePriceNegativeFmt", i + 1));
            }
        }
    }

    public class PurchaseOrderLineVm
    {
        public int LineNo { get; set; }

        [MaxLength(30)]
        public string? PurchaseRequestId { get; set; }

        public int? PrLineNo { get; set; }

        [Required, MaxLength(30)]
        public string ProductCode { get; set; } = string.Empty;

        public DateOnly? DeliveryDate { get; set; }

        [Range(0.0001, double.MaxValue)]
        public decimal OrderQty { get; set; }

        [MaxLength(20)]
        public string? UomCode { get; set; }

        public decimal StockQty { get; set; }

        [MaxLength(20)]
        public string? StockUomCode { get; set; }

        public decimal UnitPrice { get; set; }

        [MaxLength(20)]
        public string? CurrencyCode { get; set; }

        public decimal ExchangeRate { get; set; } = 1;

        [MaxLength(20)]
        public string? TaxCode { get; set; }

        public decimal TaxRate { get; set; }

        public decimal LineAmount { get; set; }
        public decimal TaxAmount { get; set; }
        public decimal TotalAmount { get; set; }
        /// <summary>本位幣含稅總額（顯示用；儲存時由後端依權威匯率重算，不採信前端值）</summary>
        public decimal AmountLocal { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        public bool IsClosed { get; set; }
    }
}

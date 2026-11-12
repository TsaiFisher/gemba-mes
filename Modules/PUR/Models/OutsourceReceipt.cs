using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.INV.Models;

namespace SmartFactory.MES.Modules.PUR.Models
{
    /// <summary>
    /// 委外完工收貨單主檔（Phase A）。
    /// 委外廠商完工交件後建立，Confirm 觸發 INV Posting TypeCode=OI（委外入庫）。
    /// FK → MFG.OutsourcePurchaseOrder 僅存字串，不設 EF 導航（跨模組）。
    /// </summary>
    public class OutsourceReceipt : IPlantEntity, IDataAuthEntity
    {
        /// <summary>委外收貨單號（格式：OR{yyyy}{00000}）</summary>
        [Key, MaxLength(30)]
        public string OutsourceReceiptId { get; set; } = string.Empty;

        /// <summary>來源委外採購單號 → MFG.OutsourcePurchaseOrder（僅字串，應用層驗證）</summary>
        [Required, MaxLength(30)]
        public string OutsourcePurchaseOrderId { get; set; } = string.Empty;

        /// <summary>收貨日期</summary>
        public DateOnly ReceiptDate { get; set; }

        /// <summary>建單人 UserId</summary>
        [Required, MaxLength(450)]
        public string CreatedBy { get; set; } = string.Empty;

        /// <summary>委外廠商代號 → FK to Company（IsSupplier = true）</summary>
        [Required, MaxLength(20)]
        public string SupplierCode { get; set; } = string.Empty;

        /// <summary>庫存異動類別代碼 = "OI"（委外入庫）</summary>
        [MaxLength(20)]
        public string TypeCode { get; set; } = "OI";

        /// <summary>
        /// 業務類別＝<c>"Purchase"</c>，與異動類別主檔的 <c>OI（委外進貨）</c> 一致。
        ///
        /// <para><b>不是 "Outsource"</b>：<c>BuildJournalLines</c> 對 Outsource 類一律回空分錄，
        /// 那是為了**委外發料**（料只換形態、價值留在存貨科目，不該再入帳一次）。
        /// 收回成品是反過來的一筆——加工費是新增的價值，必須 借 1401／貸 2152，
        /// 才能與應付發票的 借 2152／貸 2100 對衝歸零。</para>
        ///
        /// <para>預設值原本是 "Outsource"，於是加工費從來沒有借進存貨科目，
        /// 而 2152 每收一次貨就多掛一筆永遠沖不掉的借方；庫存明細帳（含加工費）
        /// 因此永久高於總帳存貨科目，差額正好是累計加工費（人工驗收 W2-F13）。
        /// 流程測試看不到是因為它**自己指定** TypeCategory = "Purchase"
        /// （Flows/OutsourceFlowTests:173），驗的是這裡本來就該有的值。</para>
        /// </summary>
        [MaxLength(20)]
        public string TypeCategory { get; set; } = "Purchase";

        /// <summary>單據狀態</summary>
        public DocStatus Status { get; set; } = DocStatus.Draft;

        /// <summary>幣別 → FK to Currency</summary>
        [MaxLength(20)]
        public string? CurrencyCode { get; set; }

        /// <summary>匯率（對本位幣）</summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal ExchangeRate { get; set; } = 1;

        /// <summary>本位幣總金額（各明細 AmountLocal 加總）。明細可為不同幣別，故表頭一律以本幣合計。</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal TotalAmount { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        /// <summary>
        /// 本單為該委外採購單的**最後一批**：勾選後於確認時結清超耗扣款與在外原料。
        ///
        /// 為什麼需要人來勾：超耗只有在委外單結束時才算得出來——收貨量還沒到訂購量時，
        /// 沒收回來的部分究竟是「還在廠商那裡加工」還是「已經被報廢」，系統分不出來。
        /// 收足訂購量的那一批會自動視為最後一批，本欄是**短交結案**用的：
        /// 廠商交了 90 個就宣告做完，剩下的 10 個份原料是報廢，得有人說一聲。
        ///
        /// 不勾也不會有帳務破口——在外原料餘額留著，等後續批次或結案批來收；
        /// 代價只是那筆料錢會晚一點才有落點。
        /// </summary>
        public bool IsFinalReceipt { get; set; }

        public DocumentApprovalStatus ApprovalStatus { get; set; } = DocumentApprovalStatus.Draft;
        public string? ApprovalRequestId { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // IPlantEntity
        public string? PlantId { get; set; }

        // IDataAuthEntity
        public string? CreatedByRoleId { get; set; }
        public string? OrganizationUnitPath { get; set; }

        /// <summary>PostgreSQL 系統欄位 xmin，樂觀並發 token（UseXminAsConcurrencyToken；見 ord-module.md §13）</summary>
        public uint xmin { get; set; }

        // Navigation
        public ICollection<OutsourceReceiptLine> Lines { get; set; } = new List<OutsourceReceiptLine>();
    }
}

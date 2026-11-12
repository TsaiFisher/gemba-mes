using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Modules.BAS.Models
{
    /// <summary>
    /// 單號格式設定。複合 PK (DocType, ScopeKey)。不套用廠別 Query Filter（設定畫面全廠管理）。
    /// ScopeKey = "" 為該單據類型的「預設規則」：未建立專屬規則的廠別一律套用預設，
    /// 且共用同一條序號（連續編，跨廠不重號）；有專屬規則的廠別序號獨立起算，
    /// 故同一 DocType 各設定的編號規則指紋（前置碼＋分隔符＋日期格式）不得重複。
    /// <para>
    /// PlantId 為「適用廠別」，可為逗號分隔多廠別清單（如 "P01,P02"）：多個廠別共用同一條規則、
    /// 同一個序號 scope（規則相同故合併儲存為一筆，而非逐廠各建一筆）。<b>PlantId 是可編輯欄位</b>，
    /// 業務規則變動時可增減適用廠別；解析廠別對應規則採「清單是否包含」比對，見 <see cref="DocNumberService.Applies"/>。
    /// </para>
    /// <para>
    /// ScopeKey 是建立時產生、<b>永不變動</b>的不透明序號鍵（預設規則為 ""，專屬規則為 GUID）。
    /// 序號計數表 <see cref="DocNumberSequence"/> 以 ScopeKey 為 scope（見 <see cref="DocNumberService.ScopeOf"/>），
    /// 與可變的 PlantId 解耦：如此增減適用廠別不會換掉序號 scope、不致重發已用過的號碼撞 PK。
    /// </para>
    /// </summary>
    public class DocNumberConfig
    {
        /// <summary>單據類型代號，e.g. "GoodsReceipt", "SalesOrder"</summary>
        [MaxLength(50)]
        public string DocType { get; set; } = "";

        /// <summary>
        /// 序號 scope 鍵：建立時產生、永不變動的不透明鍵（預設規則="", 專屬規則=GUID）。
        /// 複合 PK 之一，與可變的 PlantId 解耦，確保增減適用廠別時序號連續不重發。
        /// </summary>
        [MaxLength(500)]
        public string ScopeKey { get; set; } = "";

        /// <summary>適用廠別（可編輯）；空字串 = 預設（適用所有未特別設定的廠別）；非空可為逗號分隔多廠別清單</summary>
        [MaxLength(500)]
        public string PlantId { get; set; } = "";

        /// <summary>PlantId 拆解後的廠別代碼清單（非資料庫欄位，供顯示/勾選還原使用）</summary>
        [NotMapped]
        public IReadOnlyList<string> PlantIdList =>
            PlantId.Split(',', StringSplitOptions.RemoveEmptyEntries);

        /// <summary>顯示名稱，e.g. "進貨單"</summary>
        [Required, MaxLength(100)]
        public string DocTypeName { get; set; } = "";

        /// <summary>前置碼，e.g. "GR", "II", "PR"</summary>
        [Required, MaxLength(20)]
        public string Prefix { get; set; } = "";

        /// <summary>日期段格式</summary>
        public DocDateFormat DateFormat { get; set; } = DocDateFormat.YY;

        /// <summary>流水碼位數（1–9）</summary>
        [Range(1, 9)]
        public int SeqLength { get; set; } = 5;

        /// <summary>分隔符號（空字串或 null = 無分隔）</summary>
        [MaxLength(5)]
        public string? Separator { get; set; } = "-";

        /// <summary>流水碼重置週期</summary>
        public DocSeqReset ResetCycle { get; set; } = DocSeqReset.Yearly;

        /// <summary>是否為系統內建（true = 不可刪除）</summary>
        public bool IsSystemType { get; set; } = false;

        public bool IsActive { get; set; } = true;
    }

    public enum DocDateFormat
    {
        None     = 0,   // 無日期段   e.g. PR-00001
        YY       = 1,   // 2碼年      e.g. PR-25-00001
        YYMM     = 2,   // 年月       e.g. PR-2503-0001
        YYYYMMDD = 3,   // 完整日期   e.g. PR-20250327-001
    }

    public enum DocSeqReset
    {
        Never   = 0,   // 永不重置
        Yearly  = 1,   // 每年重置（最常用）
        Monthly = 2,   // 每月重置
    }
}

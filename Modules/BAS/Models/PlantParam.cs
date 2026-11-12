using System.ComponentModel.DataAnnotations;

namespace SmartFactory.MES.Modules.BAS.Models
{
    /// <summary>
    /// 廠別參數覆寫（ProjectZ 自有；`.claude/specifications/plant-scoped-system-params.md` §3.1）。
    ///
    /// <para><b>只放覆寫，不放全域</b>（定案＝儲存方案甲）：全域值仍住在 Core 的 <c>SystemParams</c>，
    /// 解析順序為「本廠覆寫 → 全域值 → 程式內建預設」。這樣既有資料一列都不用搬，
    /// Core 的參數畫面也維持原樣。代價是同一個參數的值分散在兩張表——這是刻意接受的。</para>
    ///
    /// <para><b>刻意不實作 <c>IPlantEntity</c></b>：全域查詢過濾會讓「讀別廠的覆寫值」變成做不到，
    /// 而設定畫面本來就要能編輯指定廠別。廠別比對一律由服務層明確帶入
    /// （<c>ReportDefinition</c> 已有同樣的取捨先例）。</para>
    /// </summary>
    public class PlantParam
    {
        /// <summary>參數鍵；與 <c>ProjectZSystemParamKeys</c> 的常數同值。複合主鍵之一。</summary>
        [MaxLength(100)]
        public string Key { get; set; } = string.Empty;

        /// <summary>適用廠別。複合主鍵之一，故不可為 null（本表不存全域列，全域在 Core 的 SystemParams）。</summary>
        [MaxLength(20)]
        public string PlantId { get; set; } = string.Empty;

        /// <summary>覆寫值。null 與「沒有這一列」語意不同：null＝該廠明確設為空值。</summary>
        public string? Value { get; set; }

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.INV.Models
{
    /// <summary>
    /// FIFO 成本層的**消耗紀錄**：一次出庫從哪一層扣了多少、以什麼單價扣的。
    ///
    /// <para><b>為什麼需要</b>：在此之前，成本層與消耗它的單據之間沒有任何連結——
    /// <see cref="InventoryCostLayer"/> 只有一個 <c>QtyRemaining</c> 被逐次扣減。
    /// 於是撤回／作廢一筆出庫時，唯一能做的事是<b>另建一層</b>把量加回去
    /// （<c>AddLayerAsync</c>），而新層的 <c>ReceiptDate</c> 是當天。
    /// 總量與總價值都會回到原點，所有既有稽核都查不出異狀，但 FIFO 的**順序**變了：
    /// 最舊的那批貨還回來以後排到最後面，之後出庫的取價順序整個位移。
    /// 人工驗收 W4-F10 的實測：同一張跨廠調撥確認→撤回三次，原本的第 19 層
    /// 305 → 290，另外多出三個日期是當天的 5@300 新層。數量、價值、借貸全部平，
    /// 只有後續每一筆出庫的成本悄悄變了。</para>
    ///
    /// <para>有了這張表，撤回就能<b>還進原本被扣的那一層</b>
    /// （<c>RestoreConsumedAsync</c>），FIFO 順序原樣復原。</para>
    ///
    /// <para><b>不改成「層引用單據」的理由</b>：一次出庫可能跨多層、一層也會被多張單據分次消耗，
    /// 是多對多，只能用獨立的消耗紀錄表達。<see cref="RestoredQty"/> 記已還回量而不是刪除本列，
    /// 是為了讓「消耗→撤回→再消耗」這種來回操作留下完整軌跡（W4-F10 的三圈實測就靠它對帳）。</para>
    /// </summary>
    public class InventoryCostLayerConsumption : IPlantEntity
    {
        public long Id { get; set; }

        /// <summary>被消耗的成本層（<see cref="InventoryCostLayer.Id"/>）。</summary>
        public int LayerId { get; set; }

        [Required, MaxLength(20)]
        public string PlantId { get; set; } = "";

        [Required, MaxLength(50)]
        public string ProductCode { get; set; } = "";

        [Required, MaxLength(20)]
        public string LocationCode { get; set; } = "";

        [MaxLength(50)]
        public string LotNo { get; set; } = "";

        /// <summary>消耗這一層的單據號（撤回時以它回查）。</summary>
        [Required, MaxLength(30)]
        public string DocNo { get; set; } = "";

        /// <summary>消耗日期（記錄用；FIFO 順序仍以層的 ReceiptDate 為準）。</summary>
        public DateOnly ConsumedDate { get; set; }

        /// <summary>本次自該層扣掉的量。</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal Qty { get; set; }

        /// <summary>已還回該層的量（撤回／作廢時累加，上限為 <see cref="Qty"/>）。</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal RestoredQty { get; set; }

        /// <summary>消耗當下該層的單位成本（存證；還回時沿用，不重算）。</summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal UnitCost { get; set; }

        [ForeignKey(nameof(LayerId))]
        public InventoryCostLayer? Layer { get; set; }

        // IPlantEntity
        string? IPlantEntity.PlantId { get => PlantId; set => PlantId = value ?? ""; }
    }
}

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.MFG.Models
{
    /// <summary>部門（工作中心）當月實際費用（roadmap-fin-cst-sub.md §3.3 費率重估分子來源）。
    /// 月結時由會計輸入各工作中心當月實際直接薪資 / 折舊水電等，作為「實際人工/製造費率」的分子。
    /// 持久化以利稽核與重開帳重跑；未來 GL 自動歸集可改以同來源 provider 寫入本表。</summary>
    public class DepartmentActualCost : IPlantEntity
    {
        public int Id { get; set; }

        /// <summary>會計期間 YYYYMM</summary>
        [Required, MaxLength(6)]
        public string PeriodId { get; set; } = string.Empty;

        /// <summary>工作中心代碼（FK → WorkCenter.WorkCenterCode）</summary>
        [Required, MaxLength(30)]
        public string WorkCenterCode { get; set; } = string.Empty;

        /// <summary>當月部門實際直接人工費用（薪資等），費率重估分子</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal ActualLaborCost { get; set; }

        /// <summary>當月部門實際製造費用（折舊 / 水電等），費率重估分子</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal ActualOverheadCost { get; set; }

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public string? PlantId { get; set; }
    }
}

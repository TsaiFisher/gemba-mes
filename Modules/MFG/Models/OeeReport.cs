using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Modules.MFG.Services;

namespace SmartFactory.MES.Modules.MFG.Models
{
    /// <summary>OEE（設備綜合效率）報表：依機台彙總期間內可用率／表現性／良率。</summary>
    public class OeeReport
    {
        public List<OeeRow> Rows { get; set; } = new();
    }

    /// <summary>單一機台的 OEE 明細（原始量 + 計算比率）。</summary>
    public class OeeRow
    {
        public string MachineCode { get; init; } = "";
        public string MachineName { get; init; } = "";

        /// <summary>品號（機台在區間內可能生產多品號，依品號拆列；無資料時為空字串）。</summary>
        public string ProductCode { get; init; } = "";
        public string ProductName { get; init; } = "";

        /// <summary>該品號的庫存單位（供 TotalQty/GoodQty 依 UOM 小數位顯示；跨品號機台彙總本身無單一 UOM 可用，故拆列後才有意義）。</summary>
        public string? UomCode { get; init; }

        /// <summary>計畫生產時間（分鐘，WorkCalendar 正班+加班；屬機台層級，同機台各品號列共用同一值)。</summary>
        public decimal PlannedMinutes { get; init; }

        /// <summary>實際稼動時間（分鐘，上工紀錄起訖累計）。</summary>
        public decimal RunMinutes { get; init; }

        /// <summary>理想加工時間（分鐘，產出×標準單位工時）。</summary>
        public decimal IdealMinutes { get; init; }

        /// <summary>總產出數（良+不良）。</summary>
        public decimal TotalQty { get; init; }

        /// <summary>良品數。</summary>
        public decimal GoodQty { get; init; }

        [NotMapped] public decimal Availability => OeeCalculator.Availability(PlannedMinutes, RunMinutes);
        [NotMapped] public decimal Performance  => OeeCalculator.Performance(RunMinutes, IdealMinutes);
        [NotMapped] public decimal Quality      => OeeCalculator.Quality(TotalQty, GoodQty);
        [NotMapped] public decimal Oee          => Availability * Performance * Quality;
    }
}

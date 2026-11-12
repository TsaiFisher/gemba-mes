using System.ComponentModel.DataAnnotations;

namespace SmartFactory.MES.Modules.BAS.Models
{
    /// <summary>單號流水碼計數表（每廠 + 每週期獨立計數）</summary>
    public class DocNumberSequence
    {
        /// <summary>單據類型代號（FK → DocNumberConfig）</summary>
        [MaxLength(50)]
        public string DocType { get; set; } = "";

        /// <summary>廠別</summary>
        [MaxLength(20)]
        public string PlantId { get; set; } = "";

        /// <summary>週期鍵值：Yearly="2025"、Monthly="202503"、Never="----"</summary>
        [MaxLength(10)]
        public string Period { get; set; } = "";

        /// <summary>最後使用的流水號（原子遞增）</summary>
        public int LastSeq { get; set; } = 0;
    }
}

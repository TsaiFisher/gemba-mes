using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Modules.BAS.Models
{
    /// <summary>公司聯絡人（COMP_D）</summary>
    public class CompanyContact
    {
        [Key]
        public int Id { get; set; }

        [Required, MaxLength(20)]
        public string CompanyCode { get; set; } = string.Empty;

        /// <summary>項次（同一公司內的排序序號）</summary>
        public int SeqNo { get; set; }

        /// <summary>職稱</summary>
        [MaxLength(100)]
        public string? Role { get; set; }

        /// <summary>聯絡人姓名</summary>
        [MaxLength(100)]
        public string? ContactName { get; set; }

        [MaxLength(50)]
        public string? Phone { get; set; }

        [MaxLength(50)]
        public string? Fax { get; set; }

        [MaxLength(50)]
        public string? Mobile { get; set; }

        [MaxLength(200)]
        public string? Address { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        // Navigation
        [ForeignKey(nameof(CompanyCode))]
        public Company? Company { get; set; }
    }
}

using System.ComponentModel.DataAnnotations;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Models
{
    public class Plant
    {
        [Key]
        [Required]
        [StringLength(20, MinimumLength = 1)]
        public string Id { get; set; } = string.Empty; // 廠別代碼 (例如: P1, P2)

        [Required]
        [StringLength(100)]
        public string Name { get; set; } = string.Empty; // 廠別名稱中文 (例如: 台北廠)

        [StringLength(100)]
        public string? NameEn { get; set; } // 廠別名稱英文 (例如: Taipei Plant)

        [MaxLength(300)]
        public string? AddressZh { get; set; } // 廠別地址（中文）

        [MaxLength(300)]
        public string? AddressEn { get; set; } // 廠別地址（英文）

        [StringLength(10)]
        public string? DefaultLanguage { get; set; } // 預設語系 (zh-TW, en-US)

        // ValidTimeZone：擋的不是畫面（下拉本來就只給得出合法值），是匯入、SQL 直改與 API。
        // 壞掉的時區代碼不會讓任何操作失敗，只會讓 DateTimeService 靜靜退回預設時區，
        // 現場看到的是「日期差一天」而不是錯誤訊息。
        [StringLength(50)]
        [ValidTimeZone]
        public string? DefaultTimeZone { get; set; } // 預設時區 (Asia/Taipei)
    }
}

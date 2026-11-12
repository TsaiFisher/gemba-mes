using System.ComponentModel.DataAnnotations;

namespace SmartFactory.MES.Models
{
    /// <summary>系統全域參數（KV 形式，非廠別隔離）</summary>
    public class SystemParam
    {
        [Key]
        [MaxLength(100)]
        public string Key { get; set; } = string.Empty;

        public string? Value { get; set; }

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>已知的系統參數 Key 常數</summary>
    public static class SystemParamKeys
    {
        public const string CompanyNameZh = "company_name_zh";
        public const string CompanyNameEn = "company_name_en";
        public const string CompanyShortZh = "company_short_zh";
        public const string CompanyShortEn = "company_short_en";
        public const string CompanyLogo    = "company_logo";    // 儲存相對路徑 e.g. /uploads/logo.png

        /// <summary>線上授權確認（"true"／其他）。只對 offline 授權加嚴；VM 強制與簽章要求不讀它（SECURITY-OPERATIONS.md §18.15.8）。</summary>
        public const string LicenseOnlineCheck = "license_online_check";
    }
}

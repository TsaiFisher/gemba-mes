using System.ComponentModel.DataAnnotations;

namespace SmartFactory.MES.Models
{
    /// <summary>
    /// L4（security-assessment-and-plan.md）：密碼歷史，用於禁止重用最近 N 次密碼。
    /// 僅保存密碼「雜湊」（與 AspNetUsers.PasswordHash 同性質），不含明文；
    /// 由 PasswordHistoryInterceptor 於任何密碼變更時自動寫入，
    /// PasswordHistoryValidator 於變更前比對候選密碼是否命中歷史。
    /// </summary>
    public class PasswordHistory
    {
        public int Id { get; set; }

        /// <summary>對應 AspNetUsers.Id（FK，使用者刪除時級聯清除）。</summary>
        [Required]
        [MaxLength(450)] // 對齊 Identity 主鍵欄位長度
        public string UserId { get; set; } = string.Empty;

        /// <summary>當時的密碼雜湊（PasswordHasher 產物）。</summary>
        [Required]
        public string PasswordHash { get; set; } = string.Empty;

        /// <summary>雜湊寫入時間（UTC），驗證取最近 N 筆時的排序依據。</summary>
        public DateTime CreatedAt { get; set; }
    }
}

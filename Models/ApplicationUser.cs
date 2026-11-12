using Microsoft.AspNetCore.Identity;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Models
{
    public class ApplicationUser : IdentityUser
    {
        public string? ActivePlantId { get; set; }  // 目前作用廠別
        // 個人時區。空值＝跟隨廠別時區（見 IUserContext.TimeZoneId 的三層鏈）。
        // 掛驗證的理由與 Plant.DefaultTimeZone 相同：解不開的代碼不會拋到使用者面前，
        // DateTimeService 接住後退回預設時區，症狀只是「日期差一天」且無任何訊息。
        [ValidTimeZone]
        public string? TimeZoneId { get; set; }      // 多時區識別
        public string? Language { get; set; }        // 多語言偏好
        public string? ActiveRoleId { get; set; }    // 目前使用的角色 ID

        [System.ComponentModel.DataAnnotations.Schema.ForeignKey("ActiveRoleId")]
        public virtual ApplicationRole? ActiveRole { get; set; }

        [System.ComponentModel.DataAnnotations.Schema.ForeignKey("ActivePlantId")]
        public virtual Plant? ActivePlant { get; set; }

        /// <summary>首次登入或管理員重設密碼後，強制使用者變更密碼</summary>
        public bool MustChangePassword { get; set; }
    }
}
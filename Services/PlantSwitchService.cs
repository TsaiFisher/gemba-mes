using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Services
{
    /// <summary>
    /// 使用者「可以切到哪些廠」的查詢結果。
    /// </summary>
    /// <param name="Plants">可切換的廠別，依代碼排序。</param>
    /// <param name="RoleIsRestricted">
    /// 目前角色是否有廠別限制（<c>RolePlantAssignment</c> 至少一筆）。
    /// 畫面用它決定要不要提示「你的角色被限制在這幾個廠」——
    /// <b>不能</b>用「清單長度」反推：不限廠的角色拿到的是全部廠別，也可能只有一個廠。
    /// </param>
    public sealed record SwitchablePlants(IReadOnlyList<Plant> Plants, bool RoleIsRestricted);

    /// <summary>切換廠別的結果。</summary>
    public enum PlantSwitchOutcome
    {
        /// <summary>已切換並存檔。</summary>
        Switched,
        /// <summary>目標廠別不在這個角色被允許的範圍內（或該廠別不存在）。</summary>
        NotAllowed,
        /// <summary>找不到使用者。</summary>
        UserNotFound,
        /// <summary>通過授權但存檔失敗。</summary>
        SaveFailed,
    }

    /// <summary>
    /// 「這個使用者可以切到哪些廠、能不能切到某個廠」的<b>唯一判定來源</b>。
    ///
    /// <para><b>為什麼要有這個服務</b>：這條規則（角色有 <c>PlantAssignments</c> 就只准清單內的廠，
    /// 沒有就准系統所有廠）原本以 inline 的形式寫在 <c>UserSettingsController</c> 裡，
    /// 而且<b>同一支控制器裡就有兩份</b>——<c>Index()</c> 列可選廠別是一份，
    /// <c>SwitchPlant</c> 驗證是另一份。手機端要做廠別切換時若再抄一份就是第三份，
    /// 而這種規則分岔的方向是<b>放行不該放行的廠</b>：那是資料權限缺陷，不是顯示瑕疵。</para>
    ///
    /// <para>⚠️ <b>這條規則在此之前是零測試覆蓋的。</b>同一支控制器的「語言」切換有
    /// <c>UserSettingsLanguageTests</c> 五支測試，「廠別」切換一支都沒有——
    /// 而後者決定的是使用者能看到哪個廠的資料。抽成服務的主要價值不是去重，
    /// 是讓這條邊界規則第一次有地方可以測。</para>
    /// </summary>
    public interface IPlantSwitchService
    {
        /// <summary>取得這個使用者目前角色可以切換的廠別。</summary>
        Task<SwitchablePlants> GetSwitchablePlantsAsync(string? userId);

        /// <summary>
        /// 嘗試把使用者的作用中廠別切到 <paramref name="plantId"/>。
        /// 授權不通過時<b>不會</b>寫入任何東西。
        /// </summary>
        Task<PlantSwitchOutcome> TrySwitchAsync(string? userId, string? plantId);
    }

    /// <inheritdoc />
    public class PlantSwitchService : IPlantSwitchService
    {
        private readonly ApplicationDbContext _db;

        public PlantSwitchService(ApplicationDbContext db) => _db = db;

        public async Task<SwitchablePlants> GetSwitchablePlantsAsync(string? userId)
        {
            if (string.IsNullOrEmpty(userId))
                return new SwitchablePlants(Array.Empty<Plant>(), RoleIsRestricted: false);

            var assignments = await LoadAssignmentsAsync(userId, includePlant: true);
            if (assignments == null)
                return new SwitchablePlants(Array.Empty<Plant>(), RoleIsRestricted: false);

            if (assignments.Count > 0)
            {
                // 角色有限制：只給指派的那幾個廠。
                // Plant 導覽為 null 只可能發生在資料異常（FK 是 Cascade，正常刪不出孤兒），
                // 濾掉而不是讓整個畫面炸掉。
                return new SwitchablePlants(
                    assignments.Select(a => a.Plant!).Where(p => p != null)
                               .OrderBy(p => p.Id, StringComparer.Ordinal).ToList(),
                    RoleIsRestricted: true);
            }

            // 角色不限制：系統所有廠別。
            // ⚠️ 一定要 IgnoreQueryFilters：Plant 本身沒有廠別過濾，但這裡的語意是
            // 「跨廠列出全部」，把意圖寫在程式碼上，別依賴「剛好沒有過濾」這件事。
            return new SwitchablePlants(
                await _db.Plants.IgnoreQueryFilters().AsNoTracking()
                    .OrderBy(p => p.Id).ToListAsync(),
                RoleIsRestricted: false);
        }

        public async Task<PlantSwitchOutcome> TrySwitchAsync(string? userId, string? plantId)
        {
            if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(plantId))
                return PlantSwitchOutcome.NotAllowed;

            // 這裡刻意**不**用 AsNoTracking：下面要改 ActivePlantId。
            // IUserContext 在管線早期就已經載入過同一個使用者（含 ActiveRole/PlantAssignments），
            // 所以這次查詢多半命中 identity map，拿到的就是那個被追蹤的實體。
            var user = await _db.Users
                .Include(u => u.ActiveRole!)
                .ThenInclude(r => r.PlantAssignments)
                .FirstOrDefaultAsync(u => u.Id == userId);

            if (user == null) return PlantSwitchOutcome.UserNotFound;
            if (user.ActiveRole == null || !await _db.UserRoles.AnyAsync(
                    x => x.UserId == userId && x.RoleId == user.ActiveRoleId))
                return PlantSwitchOutcome.NotAllowed;

            var assignments = user.ActiveRole?.PlantAssignments;
            var allowed = assignments is { Count: > 0 }
                // 角色有限制：只比對指派清單，**不要**再去查 Plants。
                // 指派本身有 FK 指向 Plant，能在清單裡就代表廠別存在。
                ? assignments.Any(a => a.PlantId == plantId)
                // 角色不限制：任何真實存在的廠別都可以。
                : await _db.Plants.IgnoreQueryFilters().AnyAsync(p => p.Id == plantId);

            if (!allowed) return PlantSwitchOutcome.NotAllowed;

            user.ActivePlantId = plantId;
            try
            {
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                return PlantSwitchOutcome.SaveFailed;
            }

            return PlantSwitchOutcome.Switched;
        }

        /// <summary>
        /// 讀出使用者目前角色的廠別指派；使用者不存在時回 <c>null</c>
        /// （與「角色沒有任何指派」＝空清單區分開，兩者的意思相反：
        /// 空清單代表<b>不限廠</b>，也就是可以切到全部）。
        /// </summary>
        private async Task<List<RolePlantAssignment>?> LoadAssignmentsAsync(
            string userId, bool includePlant)
        {
            var query = _db.Users.AsNoTracking()
                .Include(u => u.ActiveRole!)
                .ThenInclude(r => r.PlantAssignments);

            var user = includePlant
                ? await query.ThenInclude(a => a.Plant)
                    .FirstOrDefaultAsync(u => u.Id == userId)
                : await query.FirstOrDefaultAsync(u => u.Id == userId);

            if (user?.ActiveRole == null || !await _db.UserRoles.AnyAsync(
                    x => x.UserId == userId && x.RoleId == user.ActiveRoleId)) return null;
            return user.ActiveRole?.PlantAssignments.ToList() ?? new List<RolePlantAssignment>();
        }
    }
}

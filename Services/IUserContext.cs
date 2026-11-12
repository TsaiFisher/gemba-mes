using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Services
{
    public interface IUserContext
    {
        string? PlantId { get; }
        string? TimeZoneId { get; }
        string? Language { get; }
        string? UserId { get; }
        string? ActiveRoleId { get; }
        string? ActiveRolePath { get; }
        /// <summary>
        /// 目前角色允許的廠別清單。若為空，表示角色為非限制型（如 Admin），可存取使用者的所有廠別。
        /// </summary>
        IReadOnlyList<string> AllowedPlantIds { get; }
    }

    public class UserContext : IUserContext
    {
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ApplicationDbContext _db;
        private ApplicationUser? _currentUser;
        private bool _isLoaded;
        private bool _activeRoleRevoked;
        public bool HasStaleAuthorization => _activeRoleRevoked || HasInvalidPlantContext;
        // Do not mutate the tracked user while rejecting a stale authorization context.
        // 「從未選過廠別」（ActivePlantId 為 null）也算在內：此時寫入的單據 PlantId 會是 null。
        // 中介層會先以 TryInitializeDefaultPlantAsync 補上角色預設廠別，補不了才擋。
        public bool HasInvalidPlantContext => _currentUser?.ActiveRole is { PlantAssignments.Count: > 0 } role
            && !_activeRoleRevoked && !role.PlantAssignments.Any(x => x.PlantId == _currentUser.ActivePlantId);

        /// <summary>
        /// 使用者從未選過廠別、作用中角色又限定廠別時，補上該角色的預設廠別（無預設取代碼最小者）並存檔。
        ///
        /// <para><b>為什麼要有</b>：新建使用者不會設定 <c>ActivePlantId</c>，只有切換廠別／角色時才寫入。
        /// 失效廠別一律 403 之後，這種使用者登入後除了復原頁面全部被擋，而這不是授權失效，只是還沒初始化。
        /// 2026-11-06 審閱 ProjectZ #246 時以測試確認。</para>
        ///
        /// <para>只處理 null；<b>選過但已不在角色允許範圍</b>的廠別仍維持拒絕，由使用者到設定頁重選，
        /// 避免靜默把人搬到另一個廠。必須在請求開頭（中介層、尚無其他待存變更）呼叫，
        /// 存檔只會寫到這一個欄位。</para>
        /// </summary>
        public async Task<bool> TryInitializeDefaultPlantAsync(CancellationToken ct = default)
        {
            await EnsureLoadedAsync(ct);
            if (_activeRoleRevoked || _currentUser is not { ActivePlantId: null } user ||
                user.ActiveRole is not { PlantAssignments.Count: > 0 } role)
                return false;

            var plantId = role.PlantAssignments.OrderByDescending(x => x.IsDefault)
                .ThenBy(x => x.PlantId, StringComparer.Ordinal).First().PlantId;
            user.ActivePlantId = plantId;
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // 存不了就還原，維持拒絕；不讓這個請求帶著未落地的廠別往下走。
                var entry = _db.Entry(user);
                entry.Property(x => x.ActivePlantId).CurrentValue = null;
                entry.Property(x => x.ActivePlantId).IsModified = false;
                return false;
            }
            // 廠別時區（TimeZoneId）取自 ActivePlant 導覽屬性；補載以免這個請求退回個人時區。
            await _db.Entry(user).Reference(x => x.ActivePlant).LoadAsync(ct);
            return true;
        }

        public UserContext(IHttpContextAccessor httpContextAccessor, UserManager<ApplicationUser> userManager,
                           ApplicationDbContext db)
        {
            _httpContextAccessor = httpContextAccessor;
            _userManager = userManager;
            _db = db;
        }

        /// <summary>
        /// 以非同步方式先載入目前使用者；之後的屬性讀取都走快取，不再查資料庫。
        ///
        /// <para><b>為什麼要有</b>：屬性 getter 只能同步查資料庫，而每個請求最早讀到廠別的是
        /// <c>UserPreferenceMiddleware</c>——等於<b>每一個請求</b>都有兩次同步查詢卡住執行緒。
        /// 2026-10-21 換班尖峰壓測（300 人同時）時執行緒池被這裡塞住，請求握著連線更久，連線池跟著耗盡。
        /// 中介層先 await 這支，屬性那條同步路徑就只剩背景作業等沒有中介層的情境會走到。</para>
        /// </summary>
        public async Task EnsureLoadedAsync(CancellationToken ct = default)
        {
            if (_isLoaded) return;
            var userId = CurrentUserId();
            if (userId != null)
            {
                _currentUser = await UserQuery(userId).FirstOrDefaultAsync(ct);
                _activeRoleRevoked = _currentUser?.ActiveRoleId is { } roleId &&
                    !await _db.UserRoles.AnyAsync(ur => ur.UserId == userId && ur.RoleId == roleId, ct);
            }
            _isLoaded = true;
        }

        private void EnsureUserLoaded()
        {
            if (_isLoaded) return;
            var userId = CurrentUserId();
            if (userId != null)
            {
                _currentUser = UserQuery(userId).FirstOrDefault();

                // ActiveRoleId 只是「上次選的角色」，不是授權來源：使用者被移出該角色後它不會自己消失。
                // 2026-10-07 實測：dev 2 月被改成只有 Operator，ActiveRoleId 卻停在 Admin（"1"），
                // 於是桌機權限、手機選單全部照 Admin 給——角色維護畫面上看起來一切正常。
                // 已不屬於的角色一律當作沒有角色（default-deny），角色相依的屬性也一併清空。
                //
                // ⚠️ 只記旗標、不改實體：_currentUser 是追蹤中的實體，同一請求裡別處可能拿到同一個 instance，
                // 在這裡改它（或 Detach）會讓那邊的存檔靜默寫入／遺失。資料修復交給角色異動的寫入點。
                _activeRoleRevoked = _currentUser?.ActiveRoleId is { } roleId &&
                    !_db.UserRoles.Any(ur => ur.UserId == userId && ur.RoleId == roleId);
            }
            _isLoaded = true;
        }

        private string? CurrentUserId()
        {
            var user = _httpContextAccessor.HttpContext?.User;
            return user?.Identity?.IsAuthenticated == true ? _userManager.GetUserId(user) : null;
        }

        // 使用 Eager Loading 取得 ActiveRole 及其 OrganizationUnit、PlantAssignments（同步與非同步兩條路共用同一個查詢）
        private IQueryable<ApplicationUser> UserQuery(string userId) =>
            _userManager.Users
                .Include(u => u.ActiveRole!)
                .ThenInclude(r => r.OrganizationUnit)
                .Include(u => u.ActiveRole!)
                .ThenInclude(r => r.PlantAssignments)
                .Include(u => u.ActivePlant)
                .Where(u => u.Id == userId);

        public string? PlantId { get { EnsureUserLoaded(); return _activeRoleRevoked || HasInvalidPlantContext ? null : _currentUser?.ActivePlantId; } }

        /// <summary>
        /// 時區優先順序：廠別時區 → 使用者個人時區 → null（由 DateTimeService fallback 至 Asia/Taipei）。
        ///
        /// <para><b>空白字串＝未設定，不是「設定成空白」</b>。這裡原本是 <c>??</c>，只擋 null：
        /// 廠別時區若是空字串或全空白，這個屬性會把它當成有值回傳，**個人時區那一層因此被整個跳過**，
        /// 接著空字串傳進 <c>FindSystemTimeZoneById</c> 拋例外、被 <see cref="DateTimeService"/> 接住
        /// 退回預設時區——一路無聲，而且看起來就像「這個人本來就沒設個人時區」。
        /// 目前的廠別維護畫面產不出空白字串（空選項經模型繫結是 null），但匯入與 SQL 直改可以。</para>
        /// </summary>
        public string? TimeZoneId
        {
            get
            {
                EnsureUserLoaded();
                var plantTimeZone = _currentUser?.ActivePlant?.DefaultTimeZone;
                return !string.IsNullOrWhiteSpace(plantTimeZone) ? plantTimeZone : _currentUser?.TimeZoneId;
            }
        }
        public string? Language { get { EnsureUserLoaded(); return _currentUser?.Language; } }
        public string? UserId { get { EnsureUserLoaded(); return _currentUser?.Id; } }
        public string? ActiveRoleId { get { EnsureUserLoaded(); return _activeRoleRevoked || HasInvalidPlantContext ? null : _currentUser?.ActiveRoleId; } }
        public string? ActiveRolePath { get { EnsureUserLoaded(); return ActiveRole?.OrganizationUnit?.Path; } }
        public IReadOnlyList<string> AllowedPlantIds
        {
            get
            {
                EnsureUserLoaded();
                return ActiveRole?.PlantAssignments
                    .Select(p => p.PlantId).ToList() ?? new List<string>();
            }
        }

        /// <summary>使用者仍擁有的作用中角色；已被移出該角色時為 null（見 EnsureUserLoaded）。</summary>
        private ApplicationRole? ActiveRole => _activeRoleRevoked || HasInvalidPlantContext ? null : _currentUser?.ActiveRole;
    }
}

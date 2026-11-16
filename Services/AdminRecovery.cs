using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Services;

/// <summary>
/// 帳號救援：管理員替使用者重設密碼（畫面），以及伺服器上的 <c>admin-recover</c> 指令（建立第一位管理員／救回管理員）。
///
/// <para><b>為什麼要有</b>（2026-11-16 評估）：帳號、角色、權限設定等畫面只限作用中角色為 Admin，
/// 而系統原本 (1) 沒有任何「替別人重設密碼」的功能——一般使用者忘記密碼只能刪掉帳號重建；
/// (2) 正式環境不會自動建立管理員，文件寫的 <c>--seed-admin</c> 從未實作；
/// (3) 唯一的管理員忘記密碼、遺失驗證器、或被取消 Admin 角色時，只剩直接改資料庫一途。</para>
///
/// <para>兩條路共用同一份「重設登入資料」，行為才不會各走各的：產生一次性臨時密碼、下次登入強制變更、
/// 解除鎖定、更新 SecurityStamp（讓既有登入失效）。臨時密碼由系統產生而不是由操作者輸入：
/// 不會出現在指令列參數或 shell 歷史裡，也不會有人順手設成好記的弱密碼。</para>
/// </summary>
public static class AdminRecovery
{
    /// <summary>指令名稱：<c>dotnet SmartFactory.MES.dll admin-recover &lt;帳號&gt;</c>。</summary>
    public const string CommandName = "admin-recover";

    // 去掉易混淆的 0/O、1/l/I；三類字元各一組，保證符合「大寫＋小寫＋數字、至少 8 碼」的密碼規則
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Lower = "abcdefghijkmnpqrstuvwxyz";
    private const string Digit = "23456789";

    /// <summary>
    /// 產生臨時密碼，形如 <c>Kf7m-Qx2p-Rt9w-Hn4c</c>：四組、每組必含大寫／小寫／數字各一個，
    /// 好抄寫也好唸。約 82 bits，且只在下次登入前有效（登入後強制變更）。
    /// </summary>
    public static string GenerateTemporaryPassword()
    {
        var all = Upper + Lower + Digit;
        var groups = new string[4];
        for (var g = 0; g < groups.Length; g++)
        {
            var chars = new[] { Pick(Upper), Pick(Lower), Pick(Digit), Pick(all) };
            RandomNumberGenerator.Shuffle<char>(chars);
            groups[g] = new string(chars);
        }
        return string.Join('-', groups);

        static char Pick(string set) => set[RandomNumberGenerator.GetInt32(set.Length)];
    }

    /// <summary>
    /// 重設登入資料：換成臨時密碼、下次登入強制變更、解除鎖定並歸零失敗次數。
    /// <paramref name="resetTwoFactor"/> 為 true 時連兩階段驗證一起重置（只有伺服器指令會用；
    /// 畫面上的重設密碼不動兩階段驗證——那是另一顆按鈕，兩件事分開授權、分開留紀錄）。
    /// 密碼變更本身會更新 SecurityStamp，該帳號既有的登入在下一次驗證時失效。
    /// </summary>
    /// <returns>成功時回臨時密碼；失敗時 <c>Password</c> 為 null、<c>Result</c> 帶原因。</returns>
    public static async Task<(IdentityResult Result, string? Password)> ResetCredentialsAsync(
        UserManager<ApplicationUser> users, ApplicationUser user, bool resetTwoFactor = false)
    {
        var password = GenerateTemporaryPassword();
        var result = await users.HasPasswordAsync(user)
            ? await users.ResetPasswordAsync(user, await users.GeneratePasswordResetTokenAsync(user), password)
            : await users.AddPasswordAsync(user, password);
        if (!result.Succeeded) return (result, null);

        user.MustChangePassword = true;
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        if (resetTwoFactor) user.TwoFactorEnabled = false;
        result = await users.UpdateAsync(user);
        if (!result.Succeeded) return (result, null);

        if (resetTwoFactor)
        {
            result = await users.ResetAuthenticatorKeyAsync(user);
            if (!result.Succeeded) return (result, null);
        }
        return (IdentityResult.Success, password);
    }

    /// <summary>啟動參數是不是要求執行本指令（第一個參數為 <see cref="CommandName"/>）。</summary>
    public static bool IsRequested(string[] args) =>
        args.Length > 0 && string.Equals(args[0], CommandName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 執行 <c>admin-recover &lt;帳號&gt;</c>。由 Program.cs 在套完 migration 之後、啟動網站之前呼叫，
    /// 執行完就結束程序，不會開始接受連線（網站正在執行時也能另外跑這個指令）。
    ///
    /// <para>帳號不存在就建立（首次安裝建立第一位管理員）；存在就救回：重設密碼、解除鎖定、
    /// 重置兩階段驗證、補回 Admin 角色並設為作用中角色。能執行這個指令的人本來就讀得到連線字串、
    /// 改得了資料庫，所以沒有多開一道門；差別是不必手算 Identity 的密碼雜湊，而且會留下稽核紀錄。</para>
    /// </summary>
    /// <returns>程序結束碼：0 成功、1 失敗、2 用法錯誤。</returns>
    public static async Task<int> RunAsync(IServiceProvider services, string[] args, TextWriter output)
    {
        var userName = args.Length > 1 ? args[1].Trim() : "";
        if (userName.Length == 0 || args.Length > 2 || userName.StartsWith('-'))
        {
            await output.WriteLineAsync(CoreText.Get("AdminRecover_Usage"));
            return 2;
        }

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();

        var role = await roles.FindByNameAsync(AdminAccess.RoleName);
        if (role == null)
        {
            role = new ApplicationRole { Name = AdminAccess.RoleName };
            if (!await Check(await roles.CreateAsync(role), output)) return 1;
        }

        var user = await users.FindByNameAsync(userName);
        var created = user == null;
        if (user == null)
        {
            user = new ApplicationUser { UserName = userName, Email = $"{userName}@localhost", EmailConfirmed = true };
            if (!await Check(await users.CreateAsync(user), output)) return 1;
        }

        var (result, password) = await ResetCredentialsAsync(users, user, resetTwoFactor: true);
        if (!await Check(result, output)) return 1;

        if (!await users.IsInRoleAsync(user, AdminAccess.RoleName)
            && !await Check(await users.AddToRoleAsync(user, AdminAccess.RoleName), output)) return 1;

        // 作用中角色切到 Admin；Admin 角色若限定廠別而目前廠別不在其中，換到該角色的預設廠
        //（無預設取代碼最小者），否則登入後仍會被「廠別失效」擋在外面。
        user.ActiveRoleId = role.Id;
        var plants = await db.RolePlantAssignments.AsNoTracking().Where(x => x.RoleId == role.Id)
            .OrderByDescending(x => x.IsDefault).ThenBy(x => x.PlantId).Select(x => x.PlantId).ToListAsync();
        if (plants.Count > 0 && !plants.Contains(user.ActivePlantId ?? "")) user.ActivePlantId = plants[0];
        if (!await Check(await users.UpdateAsync(user), output)) return 1;

        // 稽核紀錄：欄位異動已由攔截器自動記下（操作者空白）；另寫一筆說明來源，
        // 讓其他管理員在稽核紀錄裡看得出「這個帳號曾經從伺服器被救回／建立」。
        db.AuditLogs.Add(new AuditLog
        {
            EntityType = "User",
            Action = AuditAction.Modified,
            EntityId = user.Id,
            UserName = CommandName,
            PlantId = user.ActivePlantId,
            Changes = System.Text.Json.JsonSerializer.Serialize(new
            {
                Remark = CoreText.Get(created ? "AdminRecover_AuditCreated" : "AdminRecover_AuditRecovered", userName)
            })
        });
        await db.SaveChangesAsync();

        await output.WriteLineAsync(CoreText.Get(created ? "AdminRecover_Created" : "AdminRecover_Recovered", userName));
        await output.WriteLineAsync(CoreText.Get("AdminRecover_Password", password));
        await output.WriteLineAsync(CoreText.Get("AdminRecover_Next"));
        return 0;
    }

    private static async Task<bool> Check(IdentityResult result, TextWriter output)
    {
        if (result.Succeeded) return true;
        await output.WriteLineAsync(CoreText.Get("AdminRecover_Failed",
            string.Join("; ", result.Errors.Select(e => e.Description))));
        return false;
    }
}

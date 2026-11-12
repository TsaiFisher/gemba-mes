using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using System.Security.Claims;
using Xunit;

namespace SmartFactory.MES.Tests.Timezone;

/// <summary>
/// 廠別時區優先於個人時區（人工驗收 §10.3 TZ-1）。
///
/// <para><b>這一組防的是什麼</b>：TZ-1 的自動化覆蓋原為 🟡——
/// <c>DateTimeServiceTests</c> 驗的是「拿到一個時區 ID 之後怎麼換算」，
/// 而 <b>「該拿哪一個」那一段從來沒有測試</b>（<c>Services/IUserContext.cs</c> 的
/// <c>TimeZoneId</c>：廠別時區 → 個人時區 → null）。</para>
///
/// <para>🚨 <b>既有的 <c>UserContextTests.Properties_ReturnCorrectValues</c> 看起來有驗時區，
/// 其實驗不到</b>：它沒有建 <c>Plant</c>，<c>ActivePlant</c> 是 null，所以走的一定是
/// 個人時區那一邊。<b>兩個來源給同一個值時，順序寫反也一樣綠</b>——
/// 這裡每一條都刻意讓兩個來源給<b>不同</b>的值。</para>
///
/// <para>順序錯的症狀是「業務日差一天」：使用者個人設 <c>Asia/Taipei</c>、
/// 調到紐約廠建單，單據日期會是台北的今天。單據上只有一個日期欄位，
/// 看不出它是誰的今天。</para>
///
/// <para>⚠️ <b>為什麼這一組不寫在 <c>SmartFactory.MES.Tests/Services/UserContextTests.cs</c></b>：
/// 那是 <b>Core 既有檔</b>，就地修改會被 <c>scripts/check-core-drift.sh</c> 擋下
/// （2026-09-15 實測 CI 紅在「核心區域完整性檢查」）。核心區域只能新增檔案，
/// 所以另開一支住在 ProjectZ 自己的 <c>Timezone/</c> 目錄。</para>
///
/// <para>Core #31 之後 <c>UserContextTests</c> 已有同樣的優先序守門（該檔在 Core repo 內改）。
/// 這一組仍然留著：它用 UTC+14 與 UTC−11 這組相距 25 小時的時區，
/// 而且走到 <c>DateTimeService.GetLocalDate()</c>——驗的是<b>業務日</b>，
/// 不只是「屬性回傳哪一個字串」。</para>
/// </summary>
public class PlantTimeZonePrecedenceTests : TestBase
{
    /// <summary>UTC+14 與 UTC−11 相距 25 小時，任何時刻當地日期都必然不同（見 <see cref="TimezoneTestKit"/>）。</summary>
    private const string FarEast = TimezoneTestKit.FarEast;
    private const string FarWest = TimezoneTestKit.FarWest;

    private readonly Mock<IHttpContextAccessor> _http = new();
    private readonly UserManager<ApplicationUser> _userManager;

    public PlantTimeZonePrecedenceTests()
    {
        // 用真的 UserManager 配 InMemory：要驗的正是 ActivePlant 那一段 Include，
        // 換成替身就只剩下那個三元判斷本身，鏈有沒有接上反而測不到。
        _userManager = new UserManager<ApplicationUser>(
            new UserStore<ApplicationUser>(_context), null,
            new PasswordHasher<ApplicationUser>(), null, null, null, null, null, null);
    }

    private async Task<UserContext> AuthenticatedContextAsync(
        string userId, string? plantId, string? plantTimeZone, string? personalTimeZone)
    {
        if (plantId is not null)
        {
            _context.Plants.Add(new Plant
            {
                Id = plantId, Name = plantId, DefaultTimeZone = plantTimeZone,
            });
            await _context.SaveChangesAsync();
        }

        await _userManager.CreateAsync(new ApplicationUser
        {
            Id = userId, UserName = userId, Email = $"{userId}@test.com",
            ActivePlantId = plantId, TimeZoneId = personalTimeZone,
        });
        await _context.SaveChangesAsync();

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "TestAuth");
        _http.Setup(x => x.HttpContext)
             .Returns(new DefaultHttpContext { User = new ClaimsPrincipal(identity) });

        return new UserContext(_http.Object, _userManager, _context);
    }

    /// <summary>兩者都有值時取<b>廠別</b>的。</summary>
    [Fact]
    public async Task 廠別時區優先於個人時區()
    {
        var ctx = await AuthenticatedContextAsync(
            "u-tz-1", plantId: "P-EAST", plantTimeZone: FarEast, personalTimeZone: FarWest);

        Assert.Equal(FarEast, ctx.TimeZoneId);
    }

    /// <summary>
    /// 廠別沒設時區時才退回個人時區。反面守門：把順序寫成「一律取廠別」，
    /// 沒設時區的廠會讓所有人的業務日一起掉到 <c>DateTimeService</c> 的 fallback。
    /// </summary>
    [Fact]
    public async Task 廠別未設時區時退回個人時區()
    {
        var ctx = await AuthenticatedContextAsync(
            "u-tz-2", plantId: "P-NOTZ", plantTimeZone: null, personalTimeZone: FarWest);

        Assert.Equal(FarWest, ctx.TimeZoneId);
    }

    /// <summary>
    /// 廠別時區是<b>空白字串</b>時等同未設定，退回個人時區。
    ///
    /// <para>這一條原本是<b>鎖現況</b>的：<c>IUserContext.TimeZoneId</c> 當時用 <c>??</c>，
    /// 只擋 <c>null</c>，空白字串會被當成有值回傳，個人時區那一層因此被整個跳過；
    /// 空字串再傳進 <c>TimeZoneInfo.FindSystemTimeZoneById("")</c> 拋例外、被
    /// <c>DateTimeService</c> 吞掉退到 Asia/Taipei——業務日既不是廠別的也不是個人的，
    /// 而畫面上沒有任何徵兆。Core #31（§11 #13）已改為 <c>IsNullOrWhiteSpace</c> 判定，
    /// 與 <c>ReportScheduleTimeZoneResolver</c> 一致，本條隨之改為主張正確行為。</para>
    /// </summary>
    [Fact]
    public async Task 廠別時區為空白時退回個人時區()
    {
        var ctx = await AuthenticatedContextAsync(
            "u-tz-3", plantId: "P-BLANK", plantTimeZone: "   ", personalTimeZone: FarWest);

        Assert.Equal(FarWest, ctx.TimeZoneId);

        // 走完整條鏈：業務日是個人時區的今天，不是 DateTimeService 的 fallback。
        Assert.Equal(TimezoneTestKit.TodayIn(FarWest),
                     new DateTimeService(ctx).GetLocalDate());
    }

    /// <summary>
    /// 同一個使用者切到不同時區的兩個廠，業務日跟著<b>廠</b>走。
    /// 這一條走完整條鏈（<c>UserContext</c> → <c>DateTimeService</c> → <c>GetLocalDate</c>），
    /// 上面三條只驗到鏈的第一段。
    /// </summary>
    [Fact]
    public async Task 業務日跟著廠別走而不是個人設定()
    {
        var east = new DateTimeService(await AuthenticatedContextAsync(
            "u-tz-4", "P-E", FarEast, TimezoneTestKit.Taipei));

        // ⚠️ 必須在換掉 HttpContext **之前**取值。UserContext 是延遲載入的
        // （EnsureUserLoaded 在第一次讀屬性時才去問 accessor），先建物件後取值的話
        // 兩個 context 會讀到同一個使用者，於是兩邊拿到同一天而測試看起來像「沒差別」。
        // 這正是正式環境 scoped 生命週期的樣子：一個請求一個 context，先建再用。
        var eastDate = east.GetLocalDate();

        _http.Reset();
        var west = new DateTimeService(await AuthenticatedContextAsync(
            "u-tz-5", "P-W", FarWest, TimezoneTestKit.Taipei));

        Assert.NotEqual(eastDate, west.GetLocalDate());
    }
}

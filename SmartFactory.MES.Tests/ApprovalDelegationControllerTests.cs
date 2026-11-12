using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Moq;
using SmartFactory.MES.Controllers;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using System.Security.Claims;
using Xunit;

namespace SmartFactory.MES.Tests
{
    /// <summary>
    /// 簽核代理人自助設定（休假委派）。2026-10-20 總檢時發現 Core 與部署專案都沒有測試。
    /// 守的是三件事：只能動自己的設定、輸入驗證（含同時段只能一筆生效）、
    /// 生效中（啟用且涵蓋今日）才即時把既有待簽單改派給代理人。
    /// </summary>
    public class ApprovalDelegationControllerTests : TestBase
    {
        private static readonly DateTime Now = new(2026, 1, 15, 9, 0, 0);
        private static readonly DateOnly Today = DateOnly.FromDateTime(Now);

        private readonly Mock<IApprovalService> _approval = new();
        private readonly ApplicationUser _me    = new() { Id = "U-ME",    UserName = "me" };
        private readonly ApplicationUser _other = new() { Id = "U-OTHER", UserName = "other" };
        private readonly ApplicationUser _sub   = new() { Id = "U-SUB",   UserName = "sub" };

        public ApprovalDelegationControllerTests()
        {
            _context.Users.AddRange(_me, _other, _sub);
            _context.SaveChanges();
            _approval.Setup(a => a.ApplyDelegationToPendingAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(2);
        }

        private ApprovalDelegationController Ctrl(ApplicationUser? as_ = null)
        {
            var um = GetMockUserManager();
            um.Setup(u => u.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync(as_ ?? _me);
            var dt = new Mock<IDateTimeService>();
            dt.Setup(d => d.Now).Returns(Now);
            var c = new ApprovalDelegationController(_context, um.Object, _approval.Object, dt.Object)
            {
                ControllerContext = GetControllerContext(userId: (as_ ?? _me).Id),
            };
            c.TempData = new TempDataDictionary(c.ControllerContext.HttpContext, Mock.Of<ITempDataProvider>());
            return c;
        }

        private ApprovalDelegation Seed(string delegator, string delegate_, int fromDays, int toDays, bool active = true)
        {
            var d = new ApprovalDelegation
            {
                DelegatorId = delegator, DelegateId = delegate_, IsActive = active,
                StartDate = Today.AddDays(fromDays), EndDate = Today.AddDays(toDays),
            };
            _context.ApprovalDelegations.Add(d);
            _context.SaveChanges();
            _context.ChangeTracker.Clear();
            return d;
        }

        private static ApprovalDelegation Input(string delegate_, int fromDays, int toDays, bool active = true, string? id = null) => new()
        {
            Id = id ?? Guid.NewGuid().ToString(), DelegateId = delegate_, IsActive = active,
            StartDate = Today.AddDays(fromDays), EndDate = Today.AddDays(toDays),
        };

        // ── 新增與驗證 ─────────────────────────────────────────────────────

        [Fact]
        public async Task Save_EffectiveToday_CreatesForCaller_AndSweepsPendingRequests()
        {
            var c = Ctrl();
            await c.Save(Input(_sub.Id, 0, 3));

            var saved = Assert.Single(_context.ApprovalDelegations);
            Assert.Equal(_me.Id, saved.DelegatorId);   // 一律掛在呼叫者身上，不吃表單
            _approval.Verify(a => a.ApplyDelegationToPendingAsync(_me.Id, _me.Id), Times.Once);
            Assert.Equal(CoreText.Get("Msg_DelegationSavedMovedFmt", 2), c.TempData["SuccessMessage"]);
        }

        [Theory]
        [InlineData(1, 3, true)]    // 明天才開始
        [InlineData(-5, -1, true)]  // 已經結束
        [InlineData(0, 3, false)]   // 停用中
        public async Task Save_NotEffectiveToday_DoesNotSweep(int from, int to, bool active)
        {
            await Ctrl().Save(Input(_sub.Id, from, to, active));

            Assert.Single(_context.ApprovalDelegations);
            _approval.Verify(a => a.ApplyDelegationToPendingAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Theory]
        [InlineData("",          0, 3, "Msg_DelegateRequired")]
        [InlineData("U-ME",      0, 3, "Msg_DelegateNotSelf")]
        [InlineData("U-NOBODY",  0, 3, "Msg_DelegateNotFound")]
        [InlineData("U-SUB",     3, 2, "Msg_DelegationEndBeforeStart")]
        public async Task Save_InvalidInput_Rejected_NothingSaved(string delegate_, int from, int to, string key)
        {
            var c = Ctrl();
            await c.Save(Input(delegate_, from, to));

            Assert.Empty(_context.ApprovalDelegations);
            Assert.Equal(CoreText.Get(key), c.TempData["ErrorMessage"]);
        }

        [Fact]
        public async Task Save_SameDayStartAndEnd_IsValid()
        {
            await Ctrl().Save(Input(_sub.Id, 0, 0));
            Assert.Single(_context.ApprovalDelegations);
        }

        /// <summary>同一時段只能一筆生效設定（解析時才不會有歧義）；停用中的不擋，邊界相接也算重疊。</summary>
        [Theory]
        [InlineData(5, 10, true,  true)]    // 完全重疊
        [InlineData(10, 12, true, true)]    // 起日＝既有迄日：仍重疊
        [InlineData(11, 12, true, false)]   // 隔天開始：不重疊
        [InlineData(5, 10, false, false)]   // 新的這筆停用：不擋
        public async Task Save_OverlapWithActiveDelegation(int from, int to, bool active, bool rejected)
        {
            Seed(_me.Id, _sub.Id, 5, 10);
            var c = Ctrl();

            await c.Save(Input(_other.Id, from, to, active));

            Assert.Equal(rejected ? 1 : 2, _context.ApprovalDelegations.Count());
            if (rejected) Assert.Equal(CoreText.Get("Msg_DelegationOverlap"), c.TempData["ErrorMessage"]);
        }

        [Fact]
        public async Task Save_OverlapWithInactiveDelegation_Allowed()
        {
            Seed(_me.Id, _sub.Id, 5, 10, active: false);
            await Ctrl().Save(Input(_other.Id, 5, 10));
            Assert.Equal(2, _context.ApprovalDelegations.Count());
        }

        [Fact]
        public async Task Save_OtherUsersOverlap_DoesNotBlockMine()
        {
            Seed(_other.Id, _sub.Id, 0, 10);
            await Ctrl().Save(Input(_sub.Id, 0, 10));
            Assert.Equal(2, _context.ApprovalDelegations.Count());
        }

        // ── 編輯：只能改自己的 ────────────────────────────────────────────

        [Fact]
        public async Task Save_EditOwn_UpdatesInPlace_WithoutTriggeringSelfOverlap()
        {
            var mine = Seed(_me.Id, _sub.Id, 5, 10);

            await Ctrl().Save(Input(_other.Id, 6, 12, id: mine.Id));

            var saved = Assert.Single(_context.ApprovalDelegations);
            Assert.Equal((_other.Id, Today.AddDays(6), Today.AddDays(12)), (saved.DelegateId, saved.StartDate, saved.EndDate));
        }

        [Fact]
        public async Task Save_EditSomeoneElses_Forbidden_AndUnchanged()
        {
            var theirs = Seed(_other.Id, _sub.Id, 5, 10);

            var result = await Ctrl().Save(Input(_sub.Id, 1, 2, id: theirs.Id));

            Assert.IsType<ForbidResult>(result);
            var row = Assert.Single(_context.ApprovalDelegations);
            Assert.Equal((_other.Id, Today.AddDays(5)), (row.DelegatorId, row.StartDate));
        }

        // ── 啟停與刪除 ─────────────────────────────────────────────────────

        [Fact]
        public async Task Toggle_Enable_EffectiveToday_Sweeps_Disable_DoesNot()
        {
            var mine = Seed(_me.Id, _sub.Id, -1, 1, active: false);

            await Ctrl().Toggle(mine.Id);
            Assert.True(_context.ApprovalDelegations.Single().IsActive);
            _approval.Verify(a => a.ApplyDelegationToPendingAsync(_me.Id, _me.Id), Times.Once);

            _context.ChangeTracker.Clear();
            await Ctrl().Toggle(mine.Id);
            Assert.False(_context.ApprovalDelegations.Single().IsActive);
            _approval.Verify(a => a.ApplyDelegationToPendingAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        }

        [Fact]
        public async Task Toggle_And_Delete_SomeoneElses_Forbidden()
        {
            var theirs = Seed(_other.Id, _sub.Id, 0, 3);

            Assert.IsType<ForbidResult>(await Ctrl().Toggle(theirs.Id));
            Assert.IsType<ForbidResult>(await Ctrl().Delete(theirs.Id));
            Assert.True(_context.ApprovalDelegations.Single().IsActive);
        }

        [Fact]
        public async Task Toggle_And_Delete_Missing_NotFound()
        {
            Assert.IsType<NotFoundResult>(await Ctrl().Toggle("nope"));
            Assert.IsType<NotFoundResult>(await Ctrl().Delete("nope"));
        }

        [Fact]
        public async Task Delete_Own_Removes()
        {
            var mine = Seed(_me.Id, _sub.Id, 0, 3);
            await Ctrl().Delete(mine.Id);
            Assert.Empty(_context.ApprovalDelegations);
        }

        // ── 清單只看得到與自己有關的 ──────────────────────────────────────

        [Fact]
        public async Task Index_ShowsOnlyMine_AndActiveDelegatedToMeToday()
        {
            Seed(_me.Id, _sub.Id, 0, 3);
            Seed(_other.Id, _sub.Id, 0, 3);                 // 別人的：不顯示
            Seed(_other.Id, _me.Id, -1, 1);                 // 代理到我、生效中：顯示
            Seed(_sub.Id, _me.Id, 2, 4);                    // 代理到我、尚未生效：不顯示

            var view = Assert.IsType<ViewResult>(await Ctrl().Index(null));

            var mine = Assert.IsAssignableFrom<IEnumerable<ApprovalDelegation>>(view.Model);
            Assert.All(mine, d => Assert.Equal(_me.Id, d.DelegatorId));
            var toMe = Assert.IsAssignableFrom<IEnumerable<ApprovalDelegation>>(view.ViewData["DelegatedToMe"]);
            Assert.Equal(_other.Id, Assert.Single(toMe).DelegatorId);
        }

        [Fact]
        public async Task NotSignedIn_Challenged()
        {
            var um = GetMockUserManager();
            um.Setup(u => u.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync((ApplicationUser?)null);
            var c = new ApprovalDelegationController(_context, um.Object, _approval.Object, GetMockDateTimeService().Object)
            {
                ControllerContext = GetControllerContext(),
            };

            Assert.IsType<ChallengeResult>(await c.Index(null));
            Assert.IsType<ChallengeResult>(await c.Save(Input(_sub.Id, 0, 1)));
        }
    }
}

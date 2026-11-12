using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests
{
    /// <summary>
    /// 「這個使用者可以切到哪些廠、能不能切到某個廠」的規則。
    ///
    /// <para>⚠️ <b>這條規則在本檔出現之前是零測試覆蓋的。</b>同一支
    /// <c>UserSettingsController</c> 的「語言」切換有 <see cref="UserSettingsLanguageTests"/>
    /// 五支測試，而「廠別」切換一支都沒有——後者決定的卻是使用者能看到哪個廠的資料。
    /// 規則原本以 inline 形式在該控制器裡存在<b>兩份</b>（<c>Index()</c> 列清單一份、
    /// <c>SwitchPlant</c> 驗證一份），沒有任何東西保證兩份一致。</para>
    ///
    /// <para>所以本檔最重要的不是任何單一情境，而是最後那支
    /// <c>列得出來的與切得動的是同一組廠</c>——它釘的是這兩份判定合而為一這件事本身。</para>
    /// </summary>
    public class PlantSwitchServiceTests : TestBase
    {
        private readonly PlantSwitchService _service;

        public PlantSwitchServiceTests() => _service = new PlantSwitchService(_context);

        // ── 列清單 ──────────────────────────────────────────────────────────

        [Fact]
        public async Task 角色有廠別指派時只列出被指派的廠()
        {
            await SeedPlantsAsync("P1", "P2", "P3");
            var userId = await SeedUserAsync("R-LIMITED", assignedPlants: new[] { "P2", "P3" });

            var result = await _service.GetSwitchablePlantsAsync(userId);

            Assert.Equal(new[] { "P2", "P3" }, result.Plants.Select(p => p.Id));
            Assert.True(result.RoleIsRestricted);
        }

        [Fact]
        public async Task 角色沒有廠別指派時列出全部廠_因為空集合的意思是不限廠()
        {
            // 🚨 這是整條規則最容易被寫反的地方：「沒有指派」不是「一個廠都不能去」，
            // 而是「不限制」。寫反的症狀是權限最大的角色反而一個廠都切不了。
            await SeedPlantsAsync("P1", "P2", "P3");
            var userId = await SeedUserAsync("R-FREE", assignedPlants: Array.Empty<string>());

            var result = await _service.GetSwitchablePlantsAsync(userId);

            Assert.Equal(new[] { "P1", "P2", "P3" }, result.Plants.Select(p => p.Id));
            Assert.False(result.RoleIsRestricted);
        }

        [Fact]
        public async Task 只被指派一個廠與系統只有一個廠_是不同的兩件事()
        {
            // RoleIsRestricted 不能用「清單長度」反推。畫面靠它決定要不要提示
            // 「你的角色被限制在這幾個廠」——用長度判斷的話，系統只有一個廠時
            // 會對所有人顯示那句提示，而那是假的。
            await SeedPlantsAsync("P1");
            var restricted = await SeedUserAsync("R-ONE", assignedPlants: new[] { "P1" });
            var free       = await SeedUserAsync("R-ANY", assignedPlants: Array.Empty<string>());

            var a = await _service.GetSwitchablePlantsAsync(restricted);
            var b = await _service.GetSwitchablePlantsAsync(free);

            Assert.Single(a.Plants);
            Assert.Single(b.Plants);          // 清單一模一樣
            Assert.True(a.RoleIsRestricted);  // 但意義相反
            Assert.False(b.RoleIsRestricted);
        }

        [Fact]
        public async Task 使用者不存在時回空清單而不是全部廠()
        {
            // 「查不到使用者」若走進不限廠的分支，就會把系統所有廠列給一個
            // 身分解析不出來的呼叫者。這條路徑在手機端是走得到的（Cookie 過期）。
            await SeedPlantsAsync("P1", "P2");

            var result = await _service.GetSwitchablePlantsAsync("no-such-user");

            Assert.Empty(result.Plants);
        }

        // ── 切換 ────────────────────────────────────────────────────────────

        [Fact]
        public async Task 有限制的角色切到未被指派的廠會被擋下且不寫入()
        {
            await SeedPlantsAsync("P1", "P2");
            var userId = await SeedUserAsync("R-LIMITED", assignedPlants: new[] { "P1" });

            var outcome = await _service.TrySwitchAsync(userId, "P2");

            Assert.Equal(PlantSwitchOutcome.NotAllowed, outcome);

            // 只驗回傳值不夠：一個「先寫了再回報失敗」的實作也會通過。
            _context.ChangeTracker.Clear();
            var user = await _context.Users.SingleAsync(u => u.Id == userId);
            Assert.Equal("P1", user.ActivePlantId);
        }

        [Fact]
        public async Task 有限制的角色切到被指派的廠會成功並寫入()
        {
            // 反面對照：沒有這一支的話，一個「永遠 NotAllowed」的實作也會讓上面那支變綠，
            // 而那種實作的症狀是「切廠功能整個壞掉」。
            await SeedPlantsAsync("P1", "P2");
            var userId = await SeedUserAsync("R-LIMITED", assignedPlants: new[] { "P1", "P2" });

            var outcome = await _service.TrySwitchAsync(userId, "P2");

            Assert.Equal(PlantSwitchOutcome.Switched, outcome);
            _context.ChangeTracker.Clear();
            Assert.Equal("P2", (await _context.Users.SingleAsync(u => u.Id == userId)).ActivePlantId);
        }

        [Fact]
        public async Task 不限廠的角色可以切到任何存在的廠()
        {
            await SeedPlantsAsync("P1", "P2", "P3");
            var userId = await SeedUserAsync("R-FREE", assignedPlants: Array.Empty<string>());

            Assert.Equal(PlantSwitchOutcome.Switched, await _service.TrySwitchAsync(userId, "P3"));
        }

        [Fact]
        public async Task 不限廠的角色切到不存在的廠仍然要被擋下()
        {
            // 「不限廠」是「所有**存在的**廠」，不是「任何字串都收」。
            // 少了這個檢查，一個打錯字或偽造的廠別代碼會被寫進 ActivePlantId，
            // 而使用者接下來看到的是一個空系統——所有清單都是空的，沒有錯誤訊息。
            await SeedPlantsAsync("P1");
            var userId = await SeedUserAsync("R-FREE", assignedPlants: Array.Empty<string>());

            var outcome = await _service.TrySwitchAsync(userId, "NOPE");

            Assert.Equal(PlantSwitchOutcome.NotAllowed, outcome);
            _context.ChangeTracker.Clear();
            Assert.Equal("P1", (await _context.Users.SingleAsync(u => u.Id == userId)).ActivePlantId);
        }

        [Fact]
        public async Task 使用者不存在時回UserNotFound而不是安靜地不做事()
        {
            await SeedPlantsAsync("P1");

            Assert.Equal(PlantSwitchOutcome.UserNotFound,
                await _service.TrySwitchAsync("no-such-user", "P1"));
        }

        [Theory]
        [InlineData(null, "P1")]
        [InlineData("", "P1")]
        [InlineData("u", null)]
        [InlineData("u", "")]
        public async Task 缺參數一律當成不允許_不得落入不限廠的分支(string? userId, string? plantId)
        {
            // plantId 為空時若沒有先擋，`assignments.Any(a => a.PlantId == null)` 是 false，
            // 但不限廠分支的 `AnyAsync(p => p.Id == null)` 在某些提供者上行為不一致。
            // 一律先擋掉，行為才不依賴資料庫提供者。
            await SeedPlantsAsync("P1");

            Assert.Equal(PlantSwitchOutcome.NotAllowed,
                await _service.TrySwitchAsync(userId, plantId));
        }

        // ── 兩份判定合而為一（本檔的重點）────────────────────────────────────

        /// <summary>有限制／不限廠兩種角色都要驗——分岔可能只出現在其中一種。</summary>
        public static TheoryData<string[]> AssignmentShapes() => new()
        {
            new[] { "P2", "P3" },      // 有限制
            Array.Empty<string>(),     // 不限廠
        };

        [Theory]
        [MemberData(nameof(AssignmentShapes))]
        public async Task 列得出來的與切得動的是同一組廠(string[] assigned)
        {
            // 🚨 這支釘的是規則「只有一份」這件事本身。
            // 規則分岔的兩個方向代價不對稱：
            //   列得少、切得多 → 使用者切得到一個下拉裡看不到的廠（**資料權限缺陷**）
            //   列得多、切得少 → 按了沒反應（惱人，但不外洩）
            // 前者正是把規則抄成第二份時最容易長出來的形狀。
            await SeedPlantsAsync("P1", "P2", "P3");
            var userId = await SeedUserAsync("R-X", assigned);

            var listed = (await _service.GetSwitchablePlantsAsync(userId))
                .Plants.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);

            foreach (var plantId in new[] { "P1", "P2", "P3" })
            {
                var outcome = await _service.TrySwitchAsync(userId, plantId);
                var switchable = outcome == PlantSwitchOutcome.Switched;

                Assert.True(listed.Contains(plantId) == switchable,
                    $"{plantId}：清單{(listed.Contains(plantId) ? "有" : "沒有")}它，" +
                    $"但實際{(switchable ? "切得動" : "切不動")}——兩份判定已經分岔。");
            }
        }

        // ── 種子 ────────────────────────────────────────────────────────────

        private async Task SeedPlantsAsync(params string[] ids)
        {
            _context.Plants.AddRange(ids.Select(id => new Plant { Id = id, Name = id }));
            await _context.SaveChangesAsync();
        }

        /// <summary>
        /// 建一個角色（可帶廠別指派）與一個使用它的使用者，回傳使用者 Id。
        /// <c>ActivePlantId</c> 一律起始於第一個廠，讓「沒有寫入」驗得出來。
        /// </summary>
        private async Task<string> SeedUserAsync(string roleId, string[] assignedPlants)
        {
            var uniqueRoleId = $"{roleId}-{Guid.NewGuid():N}";
            _context.Roles.Add(new ApplicationRole { Id = uniqueRoleId, Name = uniqueRoleId });
            _context.RolePlantAssignments.AddRange(assignedPlants.Select(p =>
                new RolePlantAssignment { RoleId = uniqueRoleId, PlantId = p }));

            var userId = Guid.NewGuid().ToString();
            _context.Users.Add(new ApplicationUser
            {
                Id = userId,
                UserName = userId,
                ActiveRoleId = uniqueRoleId,
                ActivePlantId = "P1",
            });

            _context.UserRoles.Add(new Microsoft.AspNetCore.Identity.IdentityUserRole<string> { UserId = userId, RoleId = uniqueRoleId });
            await _context.SaveChangesAsync();
            _context.ChangeTracker.Clear();
            return userId;
        }
    }
}

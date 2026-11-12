using Microsoft.Extensions.Configuration;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Localization;
using Moq;
using SmartFactory.MES;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.RPS
{
    /// <summary>
    /// R-1：報表工具開關 <c>false → true</c> 的**升級路徑**（manual-test-plan.md §10.2）。
    ///
    /// <para><b>為什麼要單獨一個檔</b>：<see cref="RolePermissionSeederTests"/> 的
    /// <c>MenuConfig()</c> 把 <c>Features:ReportTools:Enabled</c> 寫死成 <c>"true"</c>——
    /// 那是刻意的（它要驗的是三支畫面的矩陣列），但也因此**整份檔案永遠在開關開啟下跑**，
    /// 「關掉時到底少了什麼」與「從關到開的那一次啟動會發生什麼」一支都沒驗到。
    /// R-1 缺的正是這一半：核心判準「已被移除的權限不得補回」早有
    /// <c>Backfill_skips_key_already_seen_elsewhere</c> 覆蓋，沒有覆蓋的是
    /// 「開關開啟後選單與矩陣真的長出來」。</para>
    ///
    /// <para><b>這條鏈為什麼值得逐段釘住</b>：開關關閉時 <c>ProjectZMenuContributor</c>
    /// 連選單都不註冊，於是整條鏈一起消失——選單沒有 → 權限矩陣長不出那三列 →
    /// 沒有 <c>MenuPermission</c> 種子 → 直接輸入網址被 default-deny 擋下。
    /// 好處是不必在 Controller 再寫一次開關判斷，代價是**這條鏈斷在任何一節都沒有訊號**：
    /// 畫面只是不出現，而「不出現」正是關閉時的正確行為。</para>
    /// </summary>
    public class ReportToolsFeatureUpgradeTests
    {
        /// <summary>Phase 0-B 的三支畫面。開關罩住的就是這三個 MenuKey。</summary>
        private static readonly string[] PhaseBKeys =
            { "ReportSchedule", "ReportInbox", "ReportRunHistory" };

        private static IConfiguration Config(bool enabled) =>
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [SmartFactory.MES.Modules.RPS.ReportToolsFeature.ConfigKey] =
                        enabled ? "true" : "false",
                })
                .Build();

        /// <summary>真實選單樹（三個 contributor），開關由參數決定——本檔的全部區辨力都在這個參數上。</summary>
        private static List<RolePermissionSeeder.ScreenInfo> Screens(bool enabled)
        {
            var core = new Mock<IStringLocalizer<SharedResource>>();
            core.Setup(l => l[It.IsAny<string>()]).Returns((string k) => new LocalizedString(k, k));
            var pz = new Mock<IStringLocalizer<ProjectZSharedResource>>();
            pz.Setup(l => l[It.IsAny<string>()]).Returns((string k) => new LocalizedString(k, k));

            return RolePermissionSeeder.CollectScreens(new IMenuContributor[]
            {
                new CoreMenuContributor(core.Object),
                new ProjectZMenuContributor(pz.Object, Config(enabled)),
                new UserSettingsMenuContributor(core.Object),
            });
        }

        // ── ① 開關關閉：選單沒有那三項，其餘一項都不能少 ────────────────────────

        [Fact]
        public void 開關關閉時_三支畫面連選單都不出現()
        {
            var off = Screens(enabled: false);

            foreach (var key in PhaseBKeys)
                Assert.DoesNotContain(off, s => s.MenuKey == key);
        }

        [Fact]
        public void 開關關閉時_只少掉那三項_不得波及其他畫面()
        {
            // 開關是「多一塊」不是「換一套」。只斷言那三項不見了，漏得掉「順手把別的也關掉了」——
            // 而那種漏法在畫面上同樣只是「某頁不見了」，跟關閉開關的正確行為長得一模一樣。
            var offKeys = Screens(enabled: false).Select(s => s.MenuKey).Distinct().ToHashSet();
            var onKeys = Screens(enabled: true).Select(s => s.MenuKey).Distinct().ToHashSet();

            Assert.Equal(PhaseBKeys.OrderBy(k => k), onKeys.Except(offKeys).OrderBy(k => k));
            Assert.Empty(offKeys.Except(onKeys));
        }

        // ── ② 開關開啟：三項長出來，而且動詞照實宣告 ──────────────────────────

        [Fact]
        public void 開關開啟時_三支畫面出現在報表管理群組底下()
        {
            var on = Screens(enabled: true);

            foreach (var key in PhaseBKeys)
            {
                var screen = Assert.Single(on, s => s.MenuKey == key);
                Assert.Equal("RptManagement", screen.Group);
            }
        }

        [Fact]
        public void 開關開啟時_動詞照實宣告_唯讀畫面不得長出寫入勾選框()
        {
            // MenuItem.SupportedActions 的預設值是五個動詞全開，多宣告的動詞會在權限維護畫面
            // 長出**永遠沒有作用**的勾選框，也會讓「某角色全站唯讀」這類跨畫面不變式
            // 被一筆假的寫入權打破。排程維護是 CRUD 但沒有列印；收件匣與執行紀錄是唯讀畫面。
            var on = Screens(enabled: true);

            Assert.Equal(
                new[] { "Read", "Create", "Update", "Delete" },
                Assert.Single(on, s => s.MenuKey == "ReportSchedule").Actions);

            Assert.Equal(new[] { "Read" }, Assert.Single(on, s => s.MenuKey == "ReportInbox").Actions);
            Assert.Equal(new[] { "Read" }, Assert.Single(on, s => s.MenuKey == "ReportRunHistory").Actions);
        }

        // ── ③ 升級路徑：既有部署把開關轉開後的第一次啟動 ──────────────────────

        /// <summary>
        /// 既有部署的模擬：全系統已出現過的鍵 = **開關關閉時**的選單樹。
        /// 這正是「升級前」那個部署的權限表長的樣子——那三個鍵從來沒有人看過。
        /// </summary>
        private static HashSet<string> LegacySeenKeys() =>
            Screens(enabled: false).Select(s => s.MenuKey).Distinct().ToHashSet();

        [Fact]
        public void 升級路徑_開關轉開後第一次啟動_補出十六列權限()
        {
            var on = Screens(enabled: true);
            var seen = LegacySeenKeys();

            var added = RolePermissionSeeder.Roles
                .SelectMany(r => RolePermissionSeeder.ComputeBackfill(
                    r.Id, new HashSet<string>(seen), seen, on))
                .ToList();

            // 16 列不是湊出來的數字，是 manual-test-plan.md §10.2 於 2026-09-10 在本機實地開關
            // 一次量到的結果；逐列分佈見下一支。數字在這裡的作用是「矩陣被改動時會有人知道」。
            Assert.Equal(16, added.Count);
            Assert.All(added, p => Assert.Contains(p.MenuKey, PhaseBKeys));
        }

        [Fact]
        public void 升級路徑_補出來的十六列_逐角色分佈與矩陣一致()
        {
            var on = Screens(enabled: true);
            var seen = LegacySeenKeys();

            List<MenuPermission> Backfill(string roleId) =>
                RolePermissionSeeder.ComputeBackfill(roleId, new HashSet<string>(seen), seen, on);

            // 收件匣是「我的檔案」——八個角色都拿得到，因為它只列出寄給自己的那些。
            foreach (var role in RolePermissionSeeder.Roles)
                Assert.Contains(Backfill(role.Id), p => p.MenuKey == "ReportInbox" && p.CanRead);

            // 排程維護與執行紀錄只給四個角色：Admin、高階管理者、財務、管理者。
            foreach (var roleId in new[] { "1", "HQMGR", "FIN", "MGR" })
            {
                Assert.Contains(Backfill(roleId), p => p.MenuKey == "ReportSchedule" && p.CanRead);
                Assert.Contains(Backfill(roleId), p => p.MenuKey == "ReportRunHistory" && p.CanRead);
            }

            // 其餘四個角色連排程清單都看不到——不是「看得到但改不了」。
            foreach (var roleId in new[] { "SALES", "WH", "PUR", "OP" })
            {
                Assert.DoesNotContain(Backfill(roleId), p => p.MenuKey == "ReportSchedule");
                Assert.DoesNotContain(Backfill(roleId), p => p.MenuKey == "ReportRunHistory");
            }
        }

        [Fact]
        public void 升級路徑_只有Admin與高階管理者拿得到排程的寫入權()
        {
            var on = Screens(enabled: true);
            var seen = LegacySeenKeys();

            foreach (var role in RolePermissionSeeder.Roles)
            {
                var schedule = RolePermissionSeeder
                    .ComputeBackfill(role.Id, new HashSet<string>(seen), seen, on)
                    .FirstOrDefault(p => p.MenuKey == "ReportSchedule");
                if (schedule is null) continue;

                var writable = role.Id is "1" or "HQMGR";
                Assert.Equal(writable, schedule.CanCreate);
                Assert.Equal(writable, schedule.CanUpdate);
                Assert.Equal(writable, schedule.CanDelete);

                // 排程沒有列印語意，誰都不該拿到 Print。
                Assert.False(schedule.CanPrint);
            }
        }

        // ── ④ 第二次啟動：尊重管理者的移除決定 ────────────────────────────────

        [Fact]
        public void 升級路徑_管理者移除過的那一列_第二次啟動不得補回來()
        {
            // 這一支驗的是**兩次啟動之間**的狀態機，不是單次呼叫的參數組合
            // （後者已由 RolePermissionSeederTests.Backfill_skips_key_already_seen_elsewhere 覆蓋）。
            // 場景：開關轉開 → 第一次啟動補出 16 列 → 管理者認為財務不該維護排程，把那一列刪掉
            // → 下一次重啟。ComputeBackfill 只補「全系統從未出現過」的鍵，而此時 ReportSchedule
            // 已經全系統出現過（其他三個角色還持有），所以那個刪除決定必須活下來。
            var on = Screens(enabled: true);
            var seenAfterFirstBoot = on.Select(s => s.MenuKey).Distinct().ToHashSet();

            var finKeysAfterRemoval = seenAfterFirstBoot
                .Where(k => k != "ReportSchedule")
                .ToHashSet();

            var secondBoot = RolePermissionSeeder.ComputeBackfill(
                "FIN", finKeysAfterRemoval, seenAfterFirstBoot, on);

            Assert.DoesNotContain(secondBoot, p => p.MenuKey == "ReportSchedule");
            Assert.Empty(secondBoot);
        }

        [Fact]
        public void 升級路徑_第二次啟動不得重複補任何一列()
        {
            // 補種子是每次啟動都會跑的（步驟 3b），所以「第二次跑會不會再補一次」是真的會發生的事。
            // 重複補的症狀不是報錯，是權限表裡多出一列同鍵的列——而畫面只會顯示其中一列。
            var on = Screens(enabled: true);
            var seenAfterFirstBoot = on.Select(s => s.MenuKey).Distinct().ToHashSet();

            foreach (var role in RolePermissionSeeder.Roles)
                Assert.Empty(RolePermissionSeeder.ComputeBackfill(
                    role.Id, new HashSet<string>(seenAfterFirstBoot), seenAfterFirstBoot, on));
        }
    }
}

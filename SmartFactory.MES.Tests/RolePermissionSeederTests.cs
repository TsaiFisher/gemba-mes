using Microsoft.Extensions.Configuration;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Localization;
using Moq;
using SmartFactory.MES;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests
{
    /// <summary>
    /// RolePermissionSeeder 角色矩陣測試。對「真實選單樹」（CoreMenuContributor +
    /// ProjectZMenuContributor + UserSettingsMenuContributor）驗證每個角色種出的權限符合
    /// 使用者確認的矩陣，避免日後選單/政策變動悄悄改變生產授權。
    /// </summary>
    public class RolePermissionSeederTests
    {

        /// <summary>選單設定：報表工具開關開啟，讓 Phase 0-B 的三支畫面納入測試範圍。</summary>
        private static IConfiguration MenuConfig() =>
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [SmartFactory.MES.Modules.RPS.ReportToolsFeature.ConfigKey] = "true",
                })
                .Build();
        private static List<RolePermissionSeeder.ScreenInfo> RealScreens()
        {
            var core = new Mock<IStringLocalizer<SharedResource>>();
            core.Setup(l => l[It.IsAny<string>()]).Returns((string k) => new LocalizedString(k, k));
            var pz = new Mock<IStringLocalizer<ProjectZSharedResource>>();
            pz.Setup(l => l[It.IsAny<string>()]).Returns((string k) => new LocalizedString(k, k));

            var contributors = new IMenuContributor[]
            {
                new CoreMenuContributor(core.Object),
                new ProjectZMenuContributor(pz.Object, MenuConfig()),
                new UserSettingsMenuContributor(core.Object),
            };
            return RolePermissionSeeder.CollectScreens(contributors);
        }

        private static List<MenuPermission> Perms(string roleId) =>
            RolePermissionSeeder.ComputePermissions(roleId, RealScreens());

        private static MenuPermission? P(List<MenuPermission> perms, string key) =>
            perms.FirstOrDefault(p => p.MenuKey == key);

        private static int DistinctScreens() =>
            RealScreens().Select(s => s.MenuKey).Distinct().Count();

        [Fact]
        public void CollectScreens_covers_known_menu_keys()
        {
            var keys = RealScreens().Select(s => s.MenuKey).Distinct().ToList();
            Assert.True(keys.Count >= 120, $"預期至少 120 個選單鍵，實際 {keys.Count}");
            // 跨模組抽樣，確認走訪涵蓋 Core 與 ProjectZ 各模組
            foreach (var k in new[] { "SalesOrder", "PurchaseOrder", "WarehouseMaster",
                                      "Dispatch", "APInvoice", "JournalEntry",
                                      "UserManagement", "ApprovalTemplateSetting" })
                Assert.Contains(k, keys);
        }

        [Fact]
        public void Admin_gets_full_on_every_screen()
        {
            var p = Perms("1");
            Assert.Equal(DistinctScreens(), p.Count);          // 每個畫面都有列
            Assert.All(p, x => Assert.True(x.CanRead));         // 全部至少可讀
            Assert.True(P(p, "SalesOrder")!.CanCreate);         // 支援 Create 的給滿
            Assert.True(P(p, "ApprovalTemplateSetting")!.CanRead); // 含簽核設定類
            Assert.True(P(p, "UserManagement")!.CanCreate);     // 含 IT 系統管理
        }

        [Fact]
        public void Sales_full_on_ORD_read_others_none_on_finance()
        {
            var p = Perms("SALES");
            Assert.True(P(p, "SalesOrder")!.CanCreate);                 // ORD 全 CRUD
            Assert.True(P(p, "GoodsShipment")!.CanUpdate);
            var dispatch = P(p, "Dispatch")!;
            Assert.True(dispatch.CanRead);                              // MFG 唯讀
            Assert.False(dispatch.CanCreate);
            Assert.Null(P(p, "APInvoice"));                            // FIN 無
            Assert.Null(P(p, "JournalEntry"));                        // ACT 無
            Assert.Null(P(p, "UserManagement"));                      // IT 無
        }

        [Fact]
        public void Warehouse_full_on_INV_and_BAS()
        {
            var p = Perms("WH");
            Assert.True(P(p, "StockTransfer")!.CanCreate);             // INV 全 CRUD
            Assert.True(P(p, "WarehouseMaster")!.CanCreate);          // BAS 全 CRUD
            Assert.False(P(p, "SalesOrder")!.CanCreate);             // ORD 唯讀
            Assert.True(P(p, "SalesOrder")!.CanRead);
            Assert.Null(P(p, "APInvoice"));                          // FIN 無
        }

        [Fact]
        public void Procurement_full_on_PUR_only()
        {
            var p = Perms("PUR");
            Assert.True(P(p, "PurchaseOrder")!.CanCreate);            // PUR 全 CRUD
            Assert.True(P(p, "GoodsReceipt")!.CanUpdate);
            Assert.False(P(p, "WarehouseMaster")!.CanCreate);       // BAS 唯讀
            Assert.True(P(p, "WarehouseMaster")!.CanRead);
            Assert.Null(P(p, "JournalEntry"));                      // ACT 無
        }

        [Fact]
        public void Osp_group_procurement_full_warehouse_ops_split()
        {
            // 委外管理（OSP，pur-domain.md §4.4）：PROC 全權；WH 僅倉庫作業畫面
            //（發退料/進貨）可執行，其餘（採購/報價/重工）唯讀；OP 無。
            var proc = Perms("PUR");
            Assert.True(P(proc, "OutsourcePurchaseOrder")!.CanCreate);
            Assert.True(P(proc, "OutsourceRework")!.CanCreate);

            var wh = Perms("WH");
            Assert.True(P(wh, "OutsourceMaterial")!.CanCreate);      // 倉庫作業：執行
            Assert.True(P(wh, "OutsourceReceipt")!.CanCreate);
            var whOpo = P(wh, "OutsourcePurchaseOrder")!;
            Assert.True(whOpo.CanRead);                              // 採購職能：唯讀
            Assert.False(whOpo.CanCreate);
            Assert.False(P(wh, "OutsourceRework")!.CanCreate);

            Assert.Null(P(Perms("OP"), "OutsourceRework"));          // OP 無
        }

        [Fact]
        public void Finance_full_on_FIN_and_ACT()
        {
            var p = Perms("FIN");
            Assert.True(P(p, "APInvoice")!.CanCreate);               // FIN 全 CRUD
            Assert.True(P(p, "AccountingPeriod")!.CanCreate);       // ACT 全 CRUD

            // 代表畫面原本挑 JournalEntry，當時它是唯讀的傳票查詢（整支 Controller 沒有
            // 任何 [HttpPost]），2026-08-14 宣告成 ["Read","Print"] 之後 CanCreate 就成了 false。
            // ⚠️ 2026-09-21 那個前提被推翻了：手動傳票畫面掛進同一支 Controller，
            // SupportedActions 補上 Create，所以財務角色現在**應該**有 CanCreate。
            // 少宣告的話那個權限授不出去，且下次存檔角色時會被靜默設成 false。
            Assert.True(P(p, "JournalEntry")!.CanRead);
            Assert.True(P(p, "JournalEntry")!.CanCreate);
            Assert.True(P(p, "MonthEnd")!.CanUpdate);
            Assert.False(P(p, "PurchaseOrder")!.CanCreate);        // PUR 唯讀
            Assert.True(P(p, "PurchaseOrder")!.CanRead);
        }

        [Fact]
        public void Manager_read_only_across_all_modules()
        {
            var p = Perms("MGR");
            // 全模組唯讀：每列 CanRead=true 且無任何寫入
            Assert.All(p, x => Assert.True(x.CanRead));
            Assert.All(p, x => Assert.False(x.CanCreate || x.CanUpdate || x.CanDelete || x.CanPrint));
            Assert.True(P(p, "APInvoice")!.CanRead);                // FIN 唯讀
            Assert.True(P(p, "UserManagement")!.CanRead);          // ADMINMOD 唯讀
            // 唯讀涵蓋除「簽核設定類（僅 Admin）」外的所有畫面
            Assert.Equal(DistinctScreens() - 2, p.Count);
            Assert.Null(P(p, "ApprovalTemplateSetting"));
            Assert.Null(P(p, "DocumentApprovalConfig"));
        }

        [Fact]
        public void Operator_executes_shopfloor_reads_queries_no_sales()
        {
            var p = Perms("OP");
            Assert.True(P(p, "Dispatch")!.CanCreate);                // 作業畫面可執行
            Assert.True(P(p, "ProductionCompletion")!.CanUpdate);
            var routing = P(p, "Routing")!;
            Assert.True(routing.CanRead);                            // MFG 其餘唯讀
            Assert.False(routing.CanCreate);
            Assert.True(P(p, "WorkOrderProgress")!.CanRead);        // 狀態查詢唯讀
            Assert.Null(P(p, "SalesOrder"));                        // ORD 無
            Assert.Null(P(p, "PurchaseOrder"));                     // PUR 無
            Assert.True(P(p, "WarehouseMaster")!.CanRead);          // BAS 唯讀
        }

        [Fact]
        public void HQManager_full_except_IT_admin()
        {
            var p = Perms("HQMGR");
            Assert.True(P(p, "SalesOrder")!.CanCreate);              // 營運全權
            Assert.True(P(p, "APInvoice")!.CanCreate);
            Assert.True(P(p, "WarehouseMaster")!.CanCreate);
            Assert.Null(P(p, "UserManagement"));                    // 不含 IT 系統設定
            Assert.Null(P(p, "RoleManagement"));
            Assert.Null(P(p, "ApprovalTemplateSetting"));          // 簽核設定亦僅 Admin
        }

        // ── 既有部署補授權（ComputeBackfill）──────────────────────────────────
        [Fact]
        public void Backfill_adds_new_menu_key_to_authorized_role()
        {
            var screens = RealScreens();
            // 模擬既有部署：INV 授權角色（WH）已持有除 InventoryAging 外的所有鍵，
            // 且 InventoryAging 全系統從未出現（新畫面）。
            var allKeys = screens.Select(s => s.MenuKey).Distinct().ToHashSet();
            const string newKey = "InventoryAging";
            Assert.Contains(newKey, allKeys); // 前提：選單樹確實有此新畫面

            var globallySeen = allKeys.Where(k => k != newKey).ToHashSet();
            var whExisting   = new HashSet<string>(globallySeen);

            var toAdd = RolePermissionSeeder.ComputeBackfill("WH", whExisting, globallySeen, screens);

            var added = Assert.Single(toAdd);
            Assert.Equal(newKey, added.MenuKey);
            Assert.True(added.CanRead); // INV×WH 矩陣為 Full → 至少可讀
        }

        [Fact]
        public void Backfill_skips_key_already_seen_elsewhere()
        {
            var screens = RealScreens();
            // InventoryAging 已在系統出現（例如 Admin 已持有）→ 對缺該鍵的其他角色不再補
            const string newKey = "InventoryAging";
            var globallySeen = screens.Select(s => s.MenuKey).Distinct().ToHashSet(); // 全部已知
            var whExisting   = globallySeen.Where(k => k != newKey).ToHashSet();       // WH 恰缺此鍵

            var toAdd = RolePermissionSeeder.ComputeBackfill("WH", whExisting, globallySeen, screens);

            Assert.Empty(toAdd); // 既存鍵被刻意移除的情境：不重新賦予
        }

        [Fact]
        public void Backfill_skips_key_the_role_already_holds()
        {
            // 釘的是 ComputeBackfill 的**契約**，不是某條真實路徑：在 seeder 的唯一呼叫點上，
            // roleExistingKeys 取自該角色、globallySeen 取自全表，前者必為後者子集，
            // 於是「全系統沒出現過，但這個角色已經有了」這個組合永遠不會發生——
            // 也就是說把 `!roleExistingKeys.Contains(...)` 整個拿掉，線上行為一模一樣，
            // 而在補這一支之前**沒有任何測試會紅**（2026-09-17 突變驗證撞出來的）。
            //
            // 那為什麼不乾脆拿掉：它是 public 純函式，兩個集合由呼叫端各自算出來，
            // 「已持有的不重複補」是這個函式自己承諾的事，不該靠呼叫端剛好不會那樣傳來維持。
            // 重複補的症狀不是報錯，是權限表多出一列同鍵的列，而畫面只會顯示其中一列。
            var screens = RealScreens();
            const string newKey = "InventoryAging";

            var allKeys = screens.Select(s => s.MenuKey).Distinct().ToHashSet();
            var globallySeen = allKeys.Where(k => k != newKey).ToHashSet();  // 全系統沒出現過
            var whExisting   = new HashSet<string>(allKeys);                  // 但 WH 已經持有

            var toAdd = RolePermissionSeeder.ComputeBackfill("WH", whExisting, globallySeen, screens);

            Assert.Empty(toAdd);
        }

        [Fact]
        public void Backfill_respects_matrix_denies_new_key_for_unauthorized_role()
        {
            var screens = RealScreens();
            // 新的 PUR 畫面對 Operator（PUR=None）不應補
            const string newPurKey = "OutsourcePerformance"; // RPT 群組；改用真正 PUR 鍵驗證矩陣拒絕
            var allKeys = screens.Select(s => s.MenuKey).Distinct().ToHashSet();

            // 取一個 OP 在矩陣中拿不到的既有 PUR 鍵當「新鍵」情境
            const string purOnlyKey = "PurchaseOrder"; // PUR×OP = None
            Assert.Contains(purOnlyKey, allKeys);

            var globallySeen = allKeys.Where(k => k != purOnlyKey).ToHashSet();
            var opExisting   = new HashSet<string>(globallySeen);

            var toAdd = RolePermissionSeeder.ComputeBackfill("OP", opExisting, globallySeen, screens);

            Assert.DoesNotContain(toAdd, p => p.MenuKey == purOnlyKey); // 矩陣 None → 不補
            _ = newPurKey;
        }

        [Fact]
        public void ApprovalConfig_is_admin_only_across_all_roles()
        {
            foreach (var role in new[] { "HQMGR", "SALES", "WH", "PUR", "FIN", "MGR", "OP" })
            {
                var p = Perms(role);
                Assert.Null(P(p, "ApprovalTemplateSetting"));
                Assert.Null(P(p, "DocumentApprovalConfig"));
                // 但操作型簽核畫面（待我簽核）人人可讀
                Assert.True(P(p, "PendingApprovals")!.CanRead);
            }
        }

        // ── 機密報表分級（業務裁決 2026-08-04）────────────────────────────────

        /// <summary>
        /// 機密清單的每個鍵都必須真的存在於選單樹。
        /// 少了這條，任何一次 MenuKey 改名都會讓該報表**靜默退出保護**——
        /// 清單裡的字串比對不到任何畫面，不會有錯誤，只是不再生效。
        /// </summary>
        [Fact]
        public void Confidential_report_keys_all_exist_in_menu_tree()
        {
            var keys = RealScreens().Select(s => s.MenuKey).Distinct().ToHashSet();
            var missing = RolePermissionSeeder.ConfidentialReports.Where(k => !keys.Contains(k)).ToList();
            Assert.True(missing.Count == 0,
                $"機密報表清單有 {missing.Count} 個鍵在選單樹中不存在（改名或刪除？）：{string.Join(", ", missing)}");
        }

        [Fact]
        public void Confidential_reports_visible_to_hq_manager_finance_and_module_owner()
        {
            // HQ / MGR / FIN 跨模組恆可見（主管看不到損益、財務看不到存貨金額，是錯配）
            foreach (var role in new[] { "HQMGR", "MGR", "FIN" })
            {
                var p = Perms(role);
                Assert.True(P(p, "ProfitLoss")!.CanRead, $"{role} 應看得到損益表");
                Assert.True(P(p, "InventoryValue")!.CanRead, $"{role} 應看得到存貨金額");
                Assert.True(P(p, "SalesRanking")!.CanRead, $"{role} 應看得到銷售排行");
            }

            // 該模組矩陣為 Full 的角色亦可見：WH 之於 INV、SALES 之於 ORD、PROC 之於 PUR
            Assert.True(P(Perms("WH"), "InventoryValuation")!.CanRead);
            Assert.True(P(Perms("SALES"), "SalesRanking")!.CanRead);
            Assert.True(P(Perms("PUR"), "PurchasePrice")!.CanRead);
        }

        [Fact]
        public void Confidential_reports_hidden_from_unrelated_roles()
        {
            // 作業員不該看到毛利／邊際貢獻／在製金額（本次裁決前 OP 全部看得到）
            var op = Perms("OP");
            foreach (var key in new[] { "ContributionMargin", "MarginAnalysis", "WipReport", "InventoryValue" })
            {
                var row = P(op, key);
                Assert.NotNull(row);                  // 列仍在（顯式拒絕，不是沒有資料）
                Assert.False(row!.CanRead, $"OP 不應看得到 {key}");
            }

            // 業務看不到存貨金額與採購單價；倉庫看不到銷售排行與毛利
            Assert.False(P(Perms("SALES"), "InventoryValue")!.CanRead);
            Assert.False(P(Perms("SALES"), "PurchasePrice")!.CanRead);
            Assert.False(P(Perms("WH"), "SalesRanking")!.CanRead);
            Assert.False(P(Perms("WH"), "GrossMargin")!.CanRead);

            // 非機密（不含金額）者不受影響：作業員仍看得到產能與工單進度
            Assert.True(P(op, "Oee")!.CanRead);
            Assert.True(P(op, "WorkOrderProgress")!.CanRead);
            Assert.True(P(op, "MultiLevelBom")!.CanRead);
        }

        [Fact]
        public void ComputeConfidentialRevocations_targets_only_over_granted_rows()
        {
            var screens = RealScreens();
            // 模擬既有部署：所有角色對所有畫面都有 CanRead=true（本次裁決前的實際狀態）
            var existing = new List<MenuPermission>
            {
                new() { RoleId = "OP",    MenuKey = "ContributionMargin", CanRead = true },
                new() { RoleId = "SALES", MenuKey = "InventoryValue",     CanRead = true },
                new() { RoleId = "WH",    MenuKey = "InventoryValue",     CanRead = true }, // INV=Full → 保留
                new() { RoleId = "MGR",   MenuKey = "ProfitLoss",         CanRead = true }, // 恆可見
                new() { RoleId = "FIN",   MenuKey = "GrossMargin",        CanRead = true }, // 恆可見
                new() { RoleId = "1",     MenuKey = "ProfitLoss",         CanRead = true }, // Admin
                new() { RoleId = "OP",    MenuKey = "Oee",                CanRead = true }, // 非機密
                new() { RoleId = "OP",    MenuKey = "ContributionMargin", CanRead = false }, // 已關閉→不重複
                new() { RoleId = "CUSTOM",MenuKey = "ProfitLoss",         CanRead = true }, // 自訂角色不動
            };

            var revoked = RolePermissionSeeder.ComputeConfidentialRevocations(screens, existing);

            Assert.Equal(2, revoked.Count);
            Assert.Contains(revoked, r => r.RoleId == "OP"    && r.MenuKey == "ContributionMargin");
            Assert.Contains(revoked, r => r.RoleId == "SALES" && r.MenuKey == "InventoryValue");
        }
    }
}

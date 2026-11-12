using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Services
{
    /// <summary>
    /// 角色權限 seed（上線前補角色授權）。
    ///
    /// 由 <see cref="IMenuContributor"/> 走訪實際選單樹（與側欄同源，永遠涵蓋全部選單鍵），
    /// 依「模組 × 角色 → 權限級別」矩陣產生 <see cref="MenuPermission"/>，讓乾淨部署自動就位，
    /// 不再依賴有人記得在權限設定畫面手動勾選。
    ///
    /// 冪等：角色不存在才建；角色「尚無任何 MenuPermission」才種其權限——
    /// 故不會覆蓋已在權限設定畫面手動調整過的角色（如既有 dev admin）。
    /// 預設所有環境執行，可用組態 "Security:SeedRolePermissions": false 關閉。
    ///
    /// 範圍註記：本 seed 只管「選單存取授權」（MenuPermission）。資料列可見範圍
    /// （IDataAuthEntity / CanViewData / OrganizationUnit）為另一機制，不在本 seed 內。
    /// 角色矩陣經使用者 2026-06-30 確認。
    /// </summary>
    public static class RolePermissionSeeder
    {
        public enum Access { None, Read, Full }

        public sealed record ScreenInfo(string Group, string MenuKey, IReadOnlyList<string> Actions);

        // 角色：Id / 顯示名稱 / 矩陣鍵（Admin "1" 沿用既有 dev seed 慣例，且 PermissionService 已給其資料範圍旁路）
        public static readonly (string Id, string Name, string NameEn, string Key)[] Roles =
        {
            ("1",     "Admin",      "System Admin",    "ADMIN"),
            ("HQMGR", "高階管理者", "Senior Manager",  "HQ"),
            ("SALES", "業務人員",   "Sales",           "SALES"),
            ("WH",    "倉庫人員",   "Warehouse",       "WH"),
            ("PUR",   "採購人員",   "Procurement",     "PROC"),
            ("FIN",   "財務人員",   "Finance",         "FIN"),
            ("MGR",   "管理人員",   "Manager",         "MGR"),
            ("OP",    "作業人員",   "Operator",        "OP"),
        };

        // 選單頂層群組鍵 → 模組分類
        private static readonly Dictionary<string, string> GroupToModule = new()
        {
            ["Home"] = "COMMON",
            ["UserSettings"] = "COMMON",
            ["AdminManagement"] = "ADMINMOD",
            ["ApprovalManagement"] = "APPROVAL",
            ["BasManagement"] = "BAS",
            ["MfgManagement"] = "MFG",
            ["PurManagement"] = "PUR",
            ["OspManagement"] = "OSP",
            ["OrdManagement"] = "ORD",
            ["QcrManagement"] = "QCR",
            ["InvManagement"] = "INV",
            ["FinManagement"] = "FIN",
            ["ActManagement"] = "ACT",
            ["RptManagement"] = "RPT",

            // 手冊 Q&A 助理（S-7）。**不掛在 RptManagement 之類的既有群組下**：
            // 照抄 RPT 那一列 (F,R,R,R,R,R,R) 會在開燈那一刻讓每個作業員自動拿到
            // LLM 助理權限，而沒有人按過任何按鈕（RolePermissionSeeder 步驟 3b 會把
            // 「從未出現過的選單鍵」自動補給角色）。第一版收在 (F,N,N,N,N,R,N)，
            // 待 §八 的機密邊界裁決後再放寬（規格 §17.4）。
            ["QaManagement"] = "QA_ASSISTANT",
        };

        // Operator 在 MFG 可執行的作業畫面（其餘 MFG 唯讀）
        private static readonly HashSet<string> OperatorMfgExecute = new()
        {
            "Dispatch", "ProductionCompletion", "MachineStatusOrder",
            "MaterialSlip", "PreparationBoard", "BatchRelease",
        };

        // 倉庫在 OSP（委外管理）可執行的倉庫作業畫面（其餘 OSP 唯讀）：
        // 發退料/進貨為倉庫實體作業；採購/報價/重工屬採購職能（PROC=Full）。
        private static readonly HashSet<string> WarehouseOspExecute = new()
        {
            "OutsourceMaterial", "OutsourceReceipt",
        };

        // Approval 設定類（建立簽核流程/文件設定）僅 Admin，其餘角色即使 APPROVAL=Read 也不給
        private static readonly HashSet<string> ApprovalAdminOnly = new()
        {
            "ApprovalTemplateSetting", "DocumentApprovalConfig",
        };

        // ── 報表工具三支畫面（Phase 0-B）：授權依**畫面性質**而非所屬選單群組 ────────────
        // 它們掛在 RptManagement 群組下，但沿用 RPT 矩陣（HQ 以外一律 Read）會同時錯兩邊：
        // 收件匣會變成沒人能用的唯讀清單，排程維護則會對所有角色開放。
        //
        // REPORT_SELF：個人工具。收件匣查詢層已限定「我是 owner 或收件人」，且下載時
        //   會依**目標報表**的 MenuKey 再檢查一次（§7.8 S5），全開不構成越權。
        private static readonly HashSet<string> ReportSelfScreens = new()
        {
            "ReportInbox",
        };

        // REPORT_ADMIN：管理視角。看得到別人的排程與收件人＝看得到「誰在定期收哪些機密報表」，
        //   執行紀錄同理（含失敗原因與報表名稱），故限高階／財務／管理。
        private static readonly HashSet<string> ReportAdminScreens = new()
        {
            "ReportSchedule", "ReportRunHistory",
        };

        // ── 機密報表分級（業務裁決 2026-08-04；見 .claude/specifications/report-permission-inventory.md §四之二）──
        //
        // 分級規則：**含金額 → 機密；其餘 → 內部（維持現行可見範圍，不動）**。
        // 這 28 支即盤點母體 52 支中「含金額」的全部。
        //
        // ⚠️ 機密**不是**用 CanCreate 當閘門表達的，理由是實測出來的兩個錯配：
        //   ① MGR（管理人員）在下方 Matrix 的 13 個模組**全部是 Read、沒有任何 Full**
        //      → 用 CanCreate 當機密閘門＝主管看不到任何一張機密報表（今天 ProfitLoss/BalanceSheet 正是如此）。
        //   ② FIN 對 INV 只有 Read（財務不建庫存單據），但結帳、存貨跌價、NRV 都需要存貨金額。
        //   CanCreate 表達的是「能不能建單」，拿它代理「能不能看金額」必然錯配。
        // → 動詞一律用 CanRead（語意單一：能不能看這張畫面），機密分級改由 **MenuPermission 資料**表達，
        //   初始值由下方 IsConfidentialVisible 決定，之後管理者仍可在權限維護畫面逐張調整。
        public static readonly HashSet<string> ConfidentialReports = new()
        {
            // ACT / FIN（金額類本就限 HQ/FIN/MGR，校正後不變）
            "BadDebtProvision", "JournalEntry", "APAging", "ARAging",
            // INV（存貨金額、庫齡、週轉、異動明細含單價成本）
            "InventoryAging", "InventoryBalance", "InventoryTurnover", "InventoryValue",
            "StockLedger", "StockMovement", "StockVariance",
            "InventoryValuation", "SlowMovingInventory",
            // MFG（在製金額、工單差異）
            "WipReport", "WorkOrderVariance",
            // ORD（銷售金額、預測、趨勢、排行）
            "SalesForecast", "SalesTrend", "SalesRanking",
            // PUR（採購金額、採購單價）
            "PurchaseAmount", "PurchasePrice",
            // RPT（毛利、邊際貢獻、損益、資產負債、儀表板、委外績效、資料稽核）
            "ContributionMargin", "MarginAnalysis", "DataAudit",
            "BalanceSheet", "Dashboard", "GrossMargin", "OutsourcePerformance", "ProfitLoss",
            // 合併財報：跨廠加總的全公司金額，比單廠資產負債表更敏感，必然是機密。
            "ConsolidatedStatement",
            // 現場遙測：沒有金額，但列出誰在哪支手機、何時、做到哪裡出錯——人員活動紀錄不該全員可見
            "FieldTelemetry",
        };

        // 機密報表恆可見的角色（跨模組）：高階管理、管理人員、財務。
        // 其餘角色只有在「該模組矩陣為 Full」時才看得到（如 WH 之於 INV、SALES 之於 ORD、PROC 之於 PUR）。
        private static readonly string[] ConfidentialAlwaysRoles = { "HQ", "MGR", "FIN" };

        /// <summary>機密報表可見角色 ＝ HQ ∪ MGR ∪ FIN ∪「該模組矩陣為 Full 的角色」（Admin 恆可見）。</summary>
        public static bool IsConfidentialVisible(string? roleKey, string module)
        {
            if (string.IsNullOrEmpty(roleKey)) return false;
            if (roleKey == "ADMIN") return true;
            if (ConfidentialAlwaysRoles.Contains(roleKey)) return true;
            return Matrix.TryGetValue(module, out var row)
                && row.TryGetValue(roleKey, out var a)
                && a == Access.Full;
        }

        // ── 資料可見範圍（row-level）──────────────────────────────────────────
        // 實際 row 過濾由 Core 全域查詢過濾器以 OrganizationUnitPath 前綴判定：
        //   非空 RolePath → 看「path 為其後代」＋「OrganizationUnitPath 為 null（公開/部門資料）」的列。
        // 「看全部資料」角色指派至根 OrganizationUnit，其 path 即涵蓋全部列。
        //
        // ⚠️ 根 OU 的 PlantId 必須為 null（＝全公司，對齊 OrganizationController：root=null、GetEffectivePlantId 往上找回 null）。
        //    OrganizationUnit 實作 IPlantEntity，Core 全域過濾器對「ActiveRole.OrganizationUnit」的 Include 亦生效；
        //    而 UserContext.EnsureUserLoaded 在 UserPreferenceMiddleware 設 CurrentPlantId 之前就載入 OU（此刻 CurrentPlantId=null），
        //    若根 OU 綁某廠別（如 P1），該 Include 在真 PostgreSQL 上會被廠別過濾器濾掉 → ActiveRolePath 解析為 null
        //    → 高階/管理退回「只看自己 RoleId」，view-all 失效（InMemory 不對 Include 套過濾器故單元測試抓不到，須真 PG 驗）。
        //    PlantId=null 時 null==null 成立（EF null 語意），任何廠別情境皆解析成功，亦天然涵蓋跨廠。
        //
        // Admin（角色 "1"）亦納入 view-all：其「全部的權限」在資料維度應能檢視全部列（原 CanViewData Name 旁路已無呼叫端＝死碼）。
        public const string RootOrgId = "ORG-ROOT";
        public const string RootOrgName = "總公司";
        private static readonly string[] ViewAllRoleIds = { "1", "HQMGR", "MGR" };

        // 模組 × 角色 → 權限級別（ADMIN 走 catch-all 全權，不在表內）
        // 欄序：HQ, SALES, WH, PROC, FIN, MGR, OP
        private static readonly Dictionary<string, Dictionary<string, Access>> Matrix = Build();

        public static async Task SeedAsync(IServiceProvider sp)
        {
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("RolePermissionSeeder");
            var roleManager = sp.GetRequiredService<RoleManager<ApplicationRole>>();
            var db = sp.GetRequiredService<ApplicationDbContext>();
            var contributors = sp.GetServices<IMenuContributor>();

            // 1) 確保角色存在（冪等）
            foreach (var (id, name, nameEn, _) in Roles)
            {
                if (await roleManager.FindByIdAsync(id) != null) continue;
                var r = await roleManager.CreateAsync(new ApplicationRole
                {
                    Id = id,
                    Name = name,
                    NameZh = name,
                    NameEn = nameEn,
                });
                if (r.Succeeded) logger.LogInformation("建立角色 {Id} ({Name})", id, name);
                else logger.LogError("建立角色 {Id} 失敗：{Errors}", id,
                    string.Join("; ", r.Errors.Select(e => e.Description)));
            }

            // 2) 收集所有選單畫面
            var screens = CollectScreens(contributors);

            // 3) 逐角色種權限（僅當該角色尚無任何 MenuPermission）
            foreach (var (id, _, _, _) in Roles)
            {
                if (await db.MenuPermissions.AnyAsync(p => p.RoleId == id))
                {
                    logger.LogInformation("角色 {Id} 已有權限資料，略過 seed（不覆蓋手動設定）", id);
                    continue;
                }

                var perms = ComputePermissions(id, screens);
                db.MenuPermissions.AddRange(perms);
                await db.SaveChangesAsync();
                logger.LogInformation("角色 {Id} 種入 {Count} 筆選單權限", id, perms.Count);
            }

            // 3b) 既有部署補授權：新增畫面上線時，步驟 3 會因「角色已有權限」整個略過，
            //     導致新選單鍵漏授權（default-deny → 所有既有角色都看不到新畫面，需人工逐一勾選）。
            //     此處只補「全系統從未出現過」的選單鍵（見 ComputeBackfill）：
            //       ✓ 全新畫面 → 依矩陣自動補到各授權角色（含 Admin）
            //       ✗ 既存畫面被管理者刻意移除（其他角色仍持有該鍵）→ 不重新賦予（尊重手動決定）
            //     一次快照 globallySeen 於迴圈外，避免本輪新增使後續角色誤判為「已知」。
            var globallySeen = (await db.MenuPermissions.Select(p => p.MenuKey).Distinct().ToListAsync())
                .ToHashSet();
            foreach (var (id, _, _, _) in Roles)
            {
                var existingKeys = (await db.MenuPermissions.Where(p => p.RoleId == id)
                    .Select(p => p.MenuKey).ToListAsync()).ToHashSet();
                if (existingKeys.Count == 0) continue; // 全新角色已由步驟 3 完整種入

                var toAdd = ComputeBackfill(id, existingKeys, globallySeen, screens);
                if (toAdd.Count == 0) continue;

                db.MenuPermissions.AddRange(toAdd);
                await db.SaveChangesAsync();
                logger.LogInformation("角色 {Id} 補入 {Count} 筆新畫面權限：{Keys}",
                    id, toAdd.Count, string.Join(", ", toAdd.Select(t => t.MenuKey)));
            }

            // 3c) 一次性機密報表可見範圍校正（見 ComputeConfidentialRevocations 的說明）。
            //     只跑一次：跑完寫入 SystemParam 標記。若不設標記，管理者日後在權限維護畫面
            //     刻意把某角色的 CanRead 開回來，下次啟動就會被 seeder 再關掉——那是不可理解的行為。
            //     標記鍵刻意寫字面量而非加進 Core 的 SystemParamKeys（避免 Core 漂移）。
            const string gradeMarker = "security.report_grade_correction";
            if (!await db.SystemParams.AnyAsync(p => p.Key == gradeMarker))
            {
                var allPerms = await db.MenuPermissions.ToListAsync();
                var revocations = ComputeConfidentialRevocations(screens, allPerms);
                foreach (var p in revocations) p.CanRead = false;

                db.SystemParams.Add(new SystemParam { Key = gradeMarker, Value = "v1" });
                await db.SaveChangesAsync();

                if (revocations.Count > 0)
                    logger.LogInformation(
                        "機密報表可見範圍校正：關閉 {Count} 筆 CanRead（{Detail}）",
                        revocations.Count,
                        string.Join(", ", revocations
                            .GroupBy(p => p.RoleId)
                            .Select(g => $"{g.Key}×{g.Count()}")));
                else
                    logger.LogInformation("機密報表可見範圍校正：無須調整（既有授權皆已符合分級）");
            }

            // 4) 資料可見範圍：根 OU + 指派「看全部」角色
            await EnsureDataScopeAsync(roleManager, db, logger);
        }

        /// <summary>
        /// 建立根 OrganizationUnit（全公司，PlantId=null）並指派給「看全部資料」角色
        /// （系統管理者/高階管理者/管理人員），使其 OU-path 經 Core 全域查詢過濾器涵蓋全部資料列。
        /// 冪等、不覆蓋手動設定；舊版綁廠別的根 OU 會被回正為全公司。
        /// </summary>
        private static async Task EnsureDataScopeAsync(
            RoleManager<ApplicationRole> roleManager, ApplicationDbContext db, ILogger logger)
        {
            // 1) 確保根 OU 存在。OU 受廠別過濾，啟動期 CurrentPlantId 為 null，
            //    故存在性檢查與建立都用 IgnoreQueryFilters，避免下次啟動找不到而重建撞 PK。
            //    既有部署若曾由舊版建立「綁廠別」的根 OU，一併回正為 PlantId=null（否則 view-all 在真 PG 失效）。
            var root = await db.OrganizationUnits.IgnoreQueryFilters()
                .FirstOrDefaultAsync(o => o.Id == RootOrgId);
            if (root == null)
            {
                root = new OrganizationUnit
                {
                    Id = RootOrgId,
                    Name = RootOrgName,
                    Path = RootOrgName,   // 根節點 Path = 名稱（與 OrganizationController 產生規則一致）
                    ParentId = null,
                    PlantId = null,       // 全公司根節點：跨廠皆可解析（見上方註解）
                    SortOrder = 0,
                };
                db.OrganizationUnits.Add(root);
                await db.SaveChangesAsync();
                logger.LogInformation("建立根 OrganizationUnit {Id}（{Name}，全公司）", RootOrgId, RootOrgName);
            }
            else if (!string.IsNullOrEmpty(root.PlantId))
            {
                // 舊版遺留：根 OU 綁了廠別 → 回正為全公司，修復 view-all 在真 PG 被過濾的缺口。
                logger.LogWarning("根 OrganizationUnit {Id} 舊值綁廠別 {Plant}，回正為全公司（PlantId=null）", RootOrgId, root.PlantId);
                root.PlantId = null;
                await db.SaveChangesAsync();
            }

            // 2) 指派「看全部」角色至根 OU（僅當尚未指派，不覆蓋手動設定）
            foreach (var roleId in ViewAllRoleIds)
            {
                var role = await roleManager.FindByIdAsync(roleId);
                if (role == null || !string.IsNullOrEmpty(role.OrganizationUnitId)) continue;
                role.OrganizationUnitId = root.Id;
                var r = await roleManager.UpdateAsync(role);
                if (r.Succeeded) logger.LogInformation("角色 {Id} 指派至根 OU（資料可見範圍＝全部）", roleId);
                else logger.LogError("角色 {Id} 指派 OU 失敗：{Errors}", roleId,
                    string.Join("; ", r.Errors.Select(e => e.Description)));
            }
        }

        /// <summary>走訪所有 contributor 的選單樹，收集每個「有畫面（Controller/Page）」的選單鍵與其頂層群組。</summary>
        public static List<ScreenInfo> CollectScreens(IEnumerable<IMenuContributor> contributors)
        {
            var acc = new List<ScreenInfo>();
            foreach (var c in contributors)
                foreach (var top in c.GetMenuItems())
                    Collect(top, top.MenuKey, acc);
            return acc;
        }

        /// <summary>依角色矩陣，為指定角色計算其應有的 MenuPermission 列（純函式，便於測試）。</summary>
        public static List<MenuPermission> ComputePermissions(string roleId, IReadOnlyList<ScreenInfo> screens)
        {
            var roleKey = Roles.FirstOrDefault(r => r.Id == roleId).Key;
            var list = new List<MenuPermission>();
            var seen = new HashSet<string>();

            foreach (var s in screens)
            {
                if (!seen.Add(s.MenuKey)) continue; // 同鍵只種一次
                var access = Resolve(roleKey, s.Group, s.MenuKey);
                if (access == Access.None) continue;

                bool can(string a) => s.Actions.Contains(a);

                // 機密報表（含金額）只給 HQ/MGR/FIN 及該模組 Full 的角色；其餘角色連 Read 都不給。
                // 放在種子這一層，乾淨部署的權限列從一開始就是對的，
                // 步驟 3c 的一次性校正只需處理既有部署（兩條路徑收斂到同一結果）。
                var module = ResolveModule(s.Group, s.MenuKey);
                bool confidentialOk = !ConfidentialReports.Contains(s.MenuKey)
                                      || IsConfidentialVisible(roleKey, module);

                // 檢視權是其餘一切動詞的前提（2026-08-14 裁決）。
                // 這不是防禦性寫法，而是把一條本來就成立的不變式寫死：所有畫面的
                // Index 閘門都是 CanRead，沒有它就進不了畫面，建檔／列印按鈕也不會出現——
                // 一列「CanRead=false 但 CanPrint=true」的權限只會誤導維護者。
                // 唯一會讓 Read 落空而其餘動詞不落空的旁路是上面的 confidentialOk：
                // 今天 IsConfidentialVisible 對模組 Full 角色恆為 true 所以碰不到，
                // 但那是矩陣的巧合，不是這裡該依賴的前提。
                bool canRead = can("Read") && confidentialOk;

                list.Add(new MenuPermission
                {
                    RoleId    = roleId,
                    MenuKey   = s.MenuKey,
                    CanRead   = canRead,
                    CanCreate = canRead && access == Access.Full && can("Create"),
                    CanUpdate = canRead && access == Access.Full && can("Update"),
                    CanDelete = canRead && access == Access.Full && can("Delete"),
                    CanPrint  = canRead && access == Access.Full && can("Print"),
                });
            }
            return list;
        }

        /// <summary>
        /// 既有部署補授權（純函式，便於測試）：給定角色目前已持有的選單鍵、全系統已出現過的選單鍵、
        /// 及當前選單樹，算出「應補入」的新畫面權限列。
        /// 只補「全系統從未出現過（不在 globallySeenKeys）且該角色尚缺」的鍵，且仍受角色矩陣約束
        /// （矩陣判為 None 的模組不會補）。既存但被刻意移除的鍵不重新賦予。
        ///
        /// <para>⚠️ 「且該角色尚缺」那一半在**步驟 3b 的呼叫路徑上恆為真**：那裡的
        /// <c>existingKeys</c> 取自該角色、<c>globallySeen</c> 取自全表，前者必為後者子集，
        /// 所以「全系統沒出現過但這個角色已經有了」不會發生。留著它是因為這是 public 純函式，
        /// 兩個集合由呼叫端各自算出來，「已持有的不重複補」是本函式自己承諾的事，
        /// 不該靠呼叫端剛好不會那樣傳來維持。契約由
        /// <c>RolePermissionSeederTests.Backfill_skips_key_the_role_already_holds</c> 釘住——
        /// 在補那一支之前，把這個條件拿掉不會有任何測試紅。</para>
        /// </summary>
        public static List<MenuPermission> ComputeBackfill(
            string roleId,
            ISet<string> roleExistingKeys,
            ISet<string> globallySeenKeys,
            IReadOnlyList<ScreenInfo> screens)
        {
            var newKeys = screens.Select(s => s.MenuKey).Distinct()
                .Where(k => !globallySeenKeys.Contains(k)).ToHashSet();
            if (newKeys.Count == 0) return new List<MenuPermission>();

            return ComputePermissions(roleId, screens)
                .Where(p => newKeys.Contains(p.MenuKey) && !roleExistingKeys.Contains(p.MenuKey))
                .ToList();
        }

        /// <summary>
        /// 一次性機密報表可見範圍校正（純函式，便於測試）：從既有權限列中，
        /// 算出「持有機密報表 CanRead、但依 <see cref="IsConfidentialVisible"/> 不該持有」的那些列。
        ///
        /// 為什麼需要這一步：報表閘門動詞由 CanCreate 統一改為 CanRead 的**同時**，
        /// 既有 MenuPermission 的 CanRead 早就是 true（種權限時就寫入，見 ComputePermissions），
        /// 因此「只改動詞」會讓 12 張今天僅 HQ 可見的報表**瞬間擴散到整個模組矩陣**，
        /// 而且無 migration、無稽核事件、事後無跡可循。校正必須與動詞變更同批上線，中間不能有窗口。
        ///
        /// 不動兩類列：矩陣外的自訂角色（人工設定，尊重管理者決定）、Admin。
        /// </summary>
        public static List<MenuPermission> ComputeConfidentialRevocations(
            IReadOnlyList<ScreenInfo> screens,
            IReadOnlyList<MenuPermission> existing)
        {
            var moduleOf = screens
                .Where(s => ConfidentialReports.Contains(s.MenuKey))
                .GroupBy(s => s.MenuKey)
                .ToDictionary(
                    g => g.Key,
                    g => GroupToModule.TryGetValue(g.First().Group, out var m) ? m : "_UNKNOWN");

            var result = new List<MenuPermission>();
            foreach (var p in existing)
            {
                if (!p.CanRead) continue;
                if (!moduleOf.TryGetValue(p.MenuKey, out var module)) continue;

                var roleKey = Roles.FirstOrDefault(r => r.Id == p.RoleId).Key;
                if (string.IsNullOrEmpty(roleKey)) continue;      // 自訂角色不動
                if (IsConfidentialVisible(roleKey, module)) continue;

                result.Add(p);
            }
            return result;
        }

        private static void Collect(MenuItem item, string topKey, List<ScreenInfo> acc)
        {
            bool isScreen = !string.IsNullOrEmpty(item.Controller) || !string.IsNullOrEmpty(item.Page);
            if (isScreen && !string.IsNullOrEmpty(item.MenuKey))
                acc.Add(new ScreenInfo(topKey, item.MenuKey, item.SupportedActions));
            foreach (var sub in item.SubItems)
                Collect(sub, topKey, acc);
        }

        /// <summary>
        /// 選單鍵 → 授權模組。多數畫面由所屬選單群組決定，少數（報表工具三支）依畫面性質另判。
        /// 抽成一支的理由：<see cref="Resolve"/> 與 <see cref="ComputePermissions"/> 都要算模組，
        /// 兩邊各寫一次的話，機密分級那一段會拿到與授權那一段不同的模組。
        /// </summary>
        private static string ResolveModule(string group, string menuKey)
        {
            if (ReportSelfScreens.Contains(menuKey)) return "REPORT_SELF";
            if (ReportAdminScreens.Contains(menuKey)) return "REPORT_ADMIN";
            return GroupToModule.TryGetValue(group, out var m) ? m : "_UNKNOWN";
        }

        private static Access Resolve(string? roleKey, string group, string menuKey)
        {
            if (string.IsNullOrEmpty(roleKey)) return Access.None;

            // Admin 一律全權（含未來新增模組的 catch-all）
            if (roleKey == "ADMIN") return Access.Full;

            // Approval 設定類僅 Admin
            if (ApprovalAdminOnly.Contains(menuKey)) return Access.None;

            var module = ResolveModule(group, menuKey);

            // Operator 在 MFG 的作業畫面可執行，其餘 MFG 唯讀
            if (roleKey == "OP" && module == "MFG")
                return OperatorMfgExecute.Contains(menuKey) ? Access.Full : Access.Read;

            // 倉庫在 OSP 的倉庫作業畫面可執行，其餘 OSP 唯讀
            if (roleKey == "WH" && module == "OSP" && WarehouseOspExecute.Contains(menuKey))
                return Access.Full;

            if (module == "_UNKNOWN") return Access.None; // 未知群組：default-deny（新模組需補矩陣）

            return Matrix.TryGetValue(module, out var row) && row.TryGetValue(roleKey, out var a)
                ? a : Access.None;
        }

        private static Dictionary<string, Dictionary<string, Access>> Build()
        {
            Access N = Access.None, R = Access.Read, F = Access.Full;
            Dictionary<string, Access> Row(Access hq, Access sales, Access wh, Access proc, Access fin, Access mgr, Access op) => new()
            {
                ["HQ"] = hq, ["SALES"] = sales, ["WH"] = wh, ["PROC"] = proc,
                ["FIN"] = fin, ["MGR"] = mgr, ["OP"] = op,
            };
            return new()
            {                       //   HQ SALES  WH PROC FIN MGR  OP
                ["COMMON"]   = Row(R,  R,    R,  R,   R,  R,   R),
                ["ADMINMOD"] = Row(N,  N,    N,  N,   N,  R,   N),
                ["APPROVAL"] = Row(R,  R,    R,  R,   R,  R,   R),
                ["BAS"]      = Row(F,  R,    F,  R,   R,  R,   R),
                ["MFG"]      = Row(F,  R,    R,  R,   R,  R,   R), // OP 由 Resolve 特判
                ["PUR"]      = Row(F,  R,    R,  F,   R,  R,   N),
                ["OSP"]      = Row(F,  R,    R,  F,   R,  R,   N), // WH 由 Resolve 特判（發退料/進貨 Full）
                ["ORD"]      = Row(F,  F,    R,  R,   R,  R,   N),
                ["QCR"]      = Row(F,  R,    R,  R,   R,  R,   R),
                ["INV"]      = Row(F,  R,    F,  R,   R,  R,   R),
                ["FIN"]      = Row(F,  N,    N,  N,   F,  R,   N),
                ["ACT"]      = Row(F,  N,    N,  N,   F,  R,   N),
                ["RPT"]      = Row(F,  R,    R,  R,   R,  R,   R),

                // 報表工具（見 ReportSelfScreens／ReportAdminScreens 的說明）
                ["REPORT_SELF"]  = Row(F,  F,    F,  F,   F,  F,   F),
                ["REPORT_ADMIN"] = Row(F,  N,    N,  N,   R,  R,   N),

                // 手冊 Q&A 助理（S-7，規格 §17.4）。Full 與 Read 的差別在此有實際意義：
                // **問問題只要 Read，手動重建索引要 Update**（QaAssistantController.Rebuild）——
                // 重建會打滿一顆 CPU 幾分鐘，並與所有人的提問搶同一個推論槽。
                ["QA_ASSISTANT"] = Row(F,  N,    N,  N,   N,  R,   N),
            };
        }
    }
}

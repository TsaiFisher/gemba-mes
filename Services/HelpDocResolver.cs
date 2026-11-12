using System.Globalization;
using System.Text;

namespace SmartFactory.MES.Services
{
    /// <summary>
    /// 畫面 → 使用手冊頁對應：頂部導覽列的「?」說明連結由此解析。
    /// 手冊頁路徑慣例：/docs/user/{module}/{controller 之 kebab-case}.html。
    /// 未登錄的 controller 回傳 null（不顯示說明連結）。
    /// 新增畫面時：產生對應手冊頁後，將 controller 名稱加入所屬模組清單。
    ///
    /// 手冊本身是中英雙語同頁（doc-i18n.js 依 ?lang= 切換），故 UI 為英文時
    /// 連結需帶 ?lang=en，使用者才不會先看到中文再自行切換。
    /// </summary>
    public static class HelpDocResolver
    {
        private static readonly Dictionary<string, string> ControllerModule = BuildMap();

        private static Dictionary<string, string> BuildMap()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            void Add(string module, params string[] controllers)
            {
                foreach (var c in controllers) map[c] = module;
            }

            Add("bas",
                "Company", "ProductCategory", "Product", "Tax", "Currency", "Uom",
                "Container", "ContainerUnit", "Tooling", "InventoryTransactionType", "WorkCenter",
                "Warehouse", "ShiftGroup", "Shift", "SpecialDay", "DocNumberConfig",
                "PlantParamOverview");
            Add("mfg",
                "Routing", "WorkCalendar", "PlannedDowntime", "MrpRun", "WorkOrder",
                "ProductionBatch", "BatchRelease",
                "Schedule", "ScheduleQuery", "PreparationBoard", "Dispatch", "ProductionCompletion",
                "MachineStatusOrder", "MaterialSlip", "WorkOrderVariance", "OrderChangeDecision",
                "WorkOrderProgress", "DefectRate", "WipReport", "Utilization",
                "ProductionEfficiency", "Oee", "MachineDowntime", "MaterialSlipProgress",
                "MrpAchievement", "LineStatus", "MultiLevelBom", "MultiLevelRouting",
                "MrpVarianceDecision", "WorkOrderMaterialStatus");
            Add("pur",
                "PurchaseRequest", "PurchaseOrder", "GoodsReceipt", "GoodsReturn",
                "VendorPriceList", "VendorEvaluation",
                "PurchaseOverdue", "SupplierDeliveryRate", "PurchaseAmount", "PurchasePrice");
            // 委外管理（OSP）：選單 Phase F 已把委外自 MFG/INV/PUR 遷出成獨立群組，手冊比照獨立。
            Add("osp",
                "OutsourceQuotation", "OutsourcePurchaseOrder", "OutsourceMaterial",
                "OutsourceReceipt", "OutsourceRework", "OutsourceProgress",
                "OutsourcePrice");
            Add("ord",
                "SalesOrder", "GoodsShipment", "SalesReturn", "InternalOrder", "OrderOverdue",
                "SalesRanking", "SalesTrend", "DeliveryRate", "ReturnRate", "SalesForecast");
            Add("qcr",
                "DefectCode", "InspectionSpec", "InspectionOrder", "Ncr", "Instrument",
                "SpcAnalysis");
            Add("inv",
                "InventoryBalance", "StockMovement", "StockLedger", "WarehouseOps",
                "MaterialStaging", "ScrapDoc", "LoanDoc", "SampleDoc", "InventoryCount",
                "StockAdjustment", "ReworkMaterial", "StockTransfer",
                "CrossPlantTransfer", "InventoryValuation", "LandedCostAllocation",
                "StockShortage", "SlowMovingInventory", "InventoryAging", "InventoryValue",
                "InventoryTurnover", "StockVariance");
            // 會計科目（ChartOfAccount）：2026-09-22 由 bas 移來，跟著選單走。
            // 本檔的 module 決定手冊頁路徑（/docs/user/{module}/…），而手冊的分類
            // 一向跟著**使用者走進來的那條路**，不跟著程式碼的模組名——控制器仍住在
            // Modules/BAS，但使用者是從「財務管理」選單點進去的，手冊就歸 fin。
            // （同一條理由已用在報表工具：程式在 RPS，手冊收在 rpt。）
            Add("fin",
                "ChartOfAccount",
                "APInvoice", "ARInvoice", "APPayment", "ARReceipt", "BankAccount",
                "APAging", "ARAging", "ARCreditNote", "APDebitNote");
            Add("act",
                "AccountingPeriod", "JournalEntry", "TrialBalance", "MonthEnd", "DepartmentActualCost",
                "NrvAssessment", "Ecl", "BadDebtProvision");
            Add("rpt",
                "Dashboard", "ProfitLoss", "GrossMargin", "MarginAnalysis",
                "ContributionMargin", "BalanceSheet", "ConsolidatedStatement",
                "OutsourcePerformance",
                "DataAudit",
                // 現場遙測（2026-10-21）：與資料稽核同屬「系統健康」，使用者從報表管理選單走進來
                "FieldTelemetry",
                // 報表工具（Phase 0-B）。手冊歸在 rpt 而非另開 rps 目錄：
                // 使用者是從報表選單走進來的，手冊的分類要跟著使用者的路徑走，不跟著程式碼的模組名。
                "ReportSchedule", "ReportInbox", "ReportRunHistory");
            // 系統管理（非作業模組）。2026-09-09 補齊：原本只收 SystemParam／QaAssistant，
            // 理由是「使用者/角色/權限/代碼維護屬建置階段設定」——但那個前提站不住：
            // 這些畫面在選單上、天天有人點，而畫面右上角的「?」是 null 就整個不顯示，
            // 使用者看到的是「這個功能沒有說明」而不是「這個功能不需要說明」。
            // 手冊 Q&A 助理（S-7）收在 sys：使用者是從「手冊助理」選單走進來的，
            // 但它與系統參數一樣屬於全公司共用、不隸屬任何作業模組。
            // ⚠️ 登錄在這裡有第二個作用：本頁會被索引成 MenuKey=QaAssistant 的 chunk，
            // 於是「這個助理怎麼用」變成它自己答得出來的問題，而且只答給有權限的人。
            // 擷取物一覽（QaExtract）仍刻意不登錄——它是給維護者看的擷取結果，不是操作畫面。
            Add("sys",
                "User", "Role", "Organization", "Plant", "Permission", "MobilePermission",
                "AuditLog", "TwoFactorAdmin", "CodeEntry",
                // 授權狀態（License）：2026-11-12 進管理系統選單後補登（原本只有提示條連結、沒有手冊）
                "License",
                // 個人設定（UserSettings）：2026-09-18 補登。它是唯一不需要任何權限的畫面，
                // 每個人都看得到、也天天用（切廠別、切身份），卻從第一版就沒有手冊——
                // 漏掉的原因是這兩支守門都從**本表**出發（登錄了就檢查頁在不在），
                // 反方向「選單上有畫面但沒登錄」全站沒有人看。
                // 它同時是時區三層鏈（廠別 → 個人 → 系統預設）唯一講得清楚的地方。
                "UserSettings",
                "SystemParam", "QaAssistant");
            // 簽核管理（APR）：手冊獨立成一個模組目錄而不併進 sys，理由與 rpt 收報表工具同一條——
            // 手冊的分類跟著使用者走進來的選單路徑走。Approval 一支 controller 對應選單上兩個項目
            // （待我簽核／全部簽核申請），共用同一頁手冊。
            Add("apr",
                "Approval", "ApprovalTemplate", "DocumentApprovalConfig");

            return map;
        }

        /// <summary>
        /// 反查：手冊頁的相對路徑（<c>ord/delivery-rate.html</c>）→ controller 名稱（<c>DeliveryRate</c>）。
        /// 目錄頁（<c>ord/index.html</c>）與手冊首頁沒有對應畫面，回傳 null。
        ///
        /// 這是手冊 Q&amp;A 的 L1 權限過濾依據（規格 §10.10）：**controller 名稱就是 MenuKey**，
        /// 索引時存進 chunk，檢索時以使用者的可讀 MenuKey 集合過濾。
        /// 用反查而非另建一份對照表，是為了讓「新畫面必補手冊頁」那條既有守門
        /// （<c>HelpDocResolverTests</c>）同時守住這裡——兩份表遲早會不一致，而不一致的形狀是
        /// 「某個畫面的手冊查得到，但沒有人有權限看到它」。
        /// </summary>
        public static string? ResolveController(string? relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) return null;
            return ReverseMap.Value.TryGetValue(relativePath.Replace('\\', '/'), out var c) ? c : null;
        }

        /// <summary>controller 名稱 → 所屬手冊模組目錄（<c>ord</c>／<c>inv</c>…）；未登錄時 null。</summary>
        public static string? ModuleOf(string? controller) =>
            !string.IsNullOrEmpty(controller) && ControllerModule.TryGetValue(controller, out var m) ? m : null;

        private static readonly Lazy<Dictionary<string, string>> ReverseMap = new(() =>
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (controller, module) in ControllerModule)
                map[$"{module}/{ToKebab(controller)}.html"] = controller;
            return map;
        });

        /// <summary>依 controller 名稱回傳手冊頁 URL（依目前 UI 語系帶語系參數）；無對應時回傳 null。</summary>
        public static string? Resolve(string? controller) => Resolve(controller, CurrentDocLang());

        /// <summary>同上，但由呼叫端指定手冊語系（"zh" / "en"）。</summary>
        public static string? Resolve(string? controller, string? lang)
        {
            if (string.IsNullOrEmpty(controller)) return null;
            if (!ControllerModule.TryGetValue(controller, out var module)) return null;
            return WithLang($"/docs/user/{module}/{ToKebab(controller)}.html", lang);
        }

        /// <summary>UI 語系 → 手冊語系。手冊只有中英兩版，非英文一律走中文。</summary>
        public static string CurrentDocLang() =>
            string.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "en",
                StringComparison.OrdinalIgnoreCase) ? "en" : "zh";

        /// <summary>手冊 URL 加語系參數（依目前 UI 語系）。中文為手冊預設，不加參數。</summary>
        public static string WithLang(string url) => WithLang(url, CurrentDocLang());

        /// <summary>手冊 URL 加語系參數。中文為手冊預設，不加參數。</summary>
        public static string WithLang(string url, string? lang) =>
            string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase)
                ? url + (url.Contains('?') ? "&" : "?") + "lang=en"
                : url;

        /// <summary>PascalCase → kebab-case（連續大寫視為一組縮寫，如 APInvoice → ap-invoice）。</summary>
        internal static string ToKebab(string name)
        {
            var sb = new StringBuilder(name.Length + 8);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (char.IsUpper(c) && i > 0)
                {
                    bool prevLower = char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]);
                    bool nextLower = i + 1 < name.Length && char.IsLower(name[i + 1]);
                    if (prevLower || (char.IsUpper(name[i - 1]) && nextLower))
                        sb.Append('-');
                }
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }
    }
}

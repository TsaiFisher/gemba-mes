using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.BAS.Models
{
    /// <summary>廠別參數的控制項型態。</summary>
    public enum PlantParamKind
    {
        /// <summary>true / false 開關。</summary>
        Bool,
        /// <summary>自由文字（倉庫、儲位代碼等）。</summary>
        Text,
        /// <summary>非負數值。</summary>
        Number,
        /// <summary>固定選項之一。</summary>
        Choice,
    }

    /// <summary>一個可廠別化參數的描述。</summary>
    /// <param name="Key">系統參數鍵（<see cref="Models.ProjectZSystemParamKeys"/> 的常數）。</param>
    /// <param name="Kind">控制項型態。</param>
    /// <param name="Module">分組用模組代碼（INV / MFG）。</param>
    /// <param name="Choices"><see cref="PlantParamKind.Choice"/> 的選項；其他型態為 null。</param>
    public sealed record PlantParamDef(
        string Key,
        PlantParamKind Kind,
        string Module,
        string[]? Choices = null)
    {
        /// <summary>「是否覆寫」核取方塊的表單欄位名。</summary>
        public string OverrideField => "pp_ovr_" + Key;

        /// <summary>值控制項的表單欄位名。</summary>
        public string ValueField => "pp_val_" + Key;

        /// <summary>資源鍵（resx）——以參數鍵轉 PascalCase，避免另立一份對照表。</summary>
        public string ResourceKey => string.Concat(
            Key.Split('_', StringSplitOptions.RemoveEmptyEntries)
               .Select(s => char.ToUpperInvariant(s[0]) + s[1..]));
    }

    /// <summary>
    /// 可廠別化參數的單一事實來源（`.claude/specifications/plant-scoped-system-params.md` §2）。
    ///
    /// <para>檢視與 <c>PlantFormEnhancementFilter</c> 都讀本清單，所以「畫面有這一格、後端沒存」
    /// 或反過來的漂移不可能發生——加一個參數只要改這裡一行。</para>
    ///
    /// <para>⚠️ 不含 <c>mfg_default_issue_method</c>：規格 §7 #8 裁決＝先自目錄移除。
    /// 該參數**全庫沒有任何讀取點**（`mfg-domain.md` ⑥），放進目錄只會讓它出現在「廠別參數設定」
    /// 頁籤與「各廠參數覆寫一覽」上——使用者設了不會有任何效果，也不會有任何提示。
    /// 常數與全域值都保留，既有資料不受影響；等 §25.6 的三層 fallback 真的接上再放回來
    /// （resx 的欄位名稱已留在原處，屆時只要加回這一行）。</para>
    ///
    /// <para>⚠️ 不含 <c>inv_default_cost_method</c>：規格 §7.1 評估後裁決不納入（同一廠內改法
    /// 不可追溯，且 <c>ProductCategory.CostMethod</c> 已提供更細的覆寫）。也不含 §2 ④ 的 16 個
    /// ——那些是法人層或跨廠語意，廠別化會產生無法解釋的結果。</para>
    /// </summary>
    public static class PlantParamCatalog
    {
        public const string ModuleInv = "INV";
        public const string ModuleMfg = "MFG";

        /// <summary>自動重排規則的選項（＝<c>ScheduleRule</c> 列舉名稱；BAS 不參照 MFG 型別，故列字面）。</summary>
        public static readonly string[] AutoScheduleRuleChoices = ["Priority", "EDD", "SPT", "FIFO", "LIFO"];

        public static readonly IReadOnlyList<PlantParamDef> All =
        [
            // ── 庫存 ─────────────────────────────────────────────
            new(ProjectZSystemParamKeys.InvSimplifiedMode, PlantParamKind.Bool, ModuleInv),

            // ── 製造 ─────────────────────────────────────────────
            new(ProjectZSystemParamKeys.MfgSimplifiedMode,          PlantParamKind.Bool,   ModuleMfg),
            new(ProjectZSystemParamKeys.MfgStandbySchedulable,      PlantParamKind.Bool,   ModuleMfg),
            new(ProjectZSystemParamKeys.MfgMrpAutoCloseObsoletePr,  PlantParamKind.Bool,   ModuleMfg),
            new(ProjectZSystemParamKeys.MfgMaterialGate,            PlantParamKind.Choice, ModuleMfg, ["None", "Warning", "Block"]),
            new(ProjectZSystemParamKeys.MfgMachineStatusGate,       PlantParamKind.Choice, ModuleMfg, ["None", "Warning", "Block"]),
            new(ProjectZSystemParamKeys.MfgOverReportTolerancePct,  PlantParamKind.Number, ModuleMfg),
            new(ProjectZSystemParamKeys.MfgOverReportAction,        PlantParamKind.Choice, ModuleMfg, ["Block", "Supervisor"]),
            new(ProjectZSystemParamKeys.MfgOutsourceOverReceiptTolerancePct, PlantParamKind.Number, ModuleMfg),
            new(ProjectZSystemParamKeys.MfgOutsourceOverReceiptAction, PlantParamKind.Choice, ModuleMfg, ["Supervisor", "Block"]),
            new(ProjectZSystemParamKeys.MfgOutsourceOverReceiptFee,    PlantParamKind.Choice, ModuleMfg, ["Pay", "NoPay"]),
            new(ProjectZSystemParamKeys.MfgOutsourceIssueOnReceipt,   PlantParamKind.Bool, ModuleMfg),
            new(ProjectZSystemParamKeys.MfgOutsourceInspectAtReceipt, PlantParamKind.Bool, ModuleMfg),
            new(ProjectZSystemParamKeys.MfgOutsourceAutoPo,           PlantParamKind.Bool, ModuleMfg),
            new(ProjectZSystemParamKeys.MfgLotPolicy,               PlantParamKind.Choice, ModuleMfg, ["LotForLot", "MinMultiple"]),
            new(ProjectZSystemParamKeys.MfgWoApprovalThreshold,     PlantParamKind.Number, ModuleMfg),
            new(ProjectZSystemParamKeys.MfgWoApprovalOutsource,     PlantParamKind.Bool,   ModuleMfg),
            new(ProjectZSystemParamKeys.MfgWoApprovalRework,        PlantParamKind.Bool,   ModuleMfg),
            new(ProjectZSystemParamKeys.MfgWoApprovalOnDelay,       PlantParamKind.Bool,   ModuleMfg),
            new(ProjectZSystemParamKeys.MfgFgWarehouse,             PlantParamKind.Text,   ModuleMfg),
            new(ProjectZSystemParamKeys.MfgFgLocation,              PlantParamKind.Text,   ModuleMfg),
            new(ProjectZSystemParamKeys.MfgDefaultStagingWh,        PlantParamKind.Text,   ModuleMfg),
            new(ProjectZSystemParamKeys.MfgDefaultStagingLocation,  PlantParamKind.Text,   ModuleMfg),
            new(ProjectZSystemParamKeys.MfgAutoScheduleOnChange,       PlantParamKind.Bool, ModuleMfg),
            new(ProjectZSystemParamKeys.MfgAutoScheduleBeforeDispatch, PlantParamKind.Bool, ModuleMfg),
            new(ProjectZSystemParamKeys.MfgAutoScheduleDailyTime,      PlantParamKind.Text, ModuleMfg),
            new(ProjectZSystemParamKeys.MfgAutoScheduleRule,           PlantParamKind.Choice, ModuleMfg, AutoScheduleRuleChoices),
            new(ProjectZSystemParamKeys.MfgAutoScheduleFreezeHours,    PlantParamKind.Number, ModuleMfg),
        ];

        public static IEnumerable<PlantParamDef> ForModule(string module) =>
            All.Where(d => d.Module == module);
    }
}

using SmartFactory.MES.Modules.BAS.Models;

namespace SmartFactory.MES.Modules.BAS.ViewModels
{
    /// <summary>
    /// 各廠參數覆寫一覽（`plant-scoped-system-params.md` §7 待決 #7，裁決＝唯讀報表）。
    ///
    /// <para>廠別參數的編輯是**逐廠**的（廠別維護面板），所以「這個參數目前有哪幾個廠改過」
    /// 在原本的操作動線上看不到——要一廠一廠點開比對。本報表把同一份
    /// <see cref="PlantParamCatalog"/> 轉置成「參數 × 廠別」矩陣補上這個視角。</para>
    ///
    /// <para>刻意不放任何值型別屬性：已覆寫廠數由 View 就 <see cref="PlantParamOverviewRow.Cells"/>
    /// 計算。這類報表 VM 的旗標欄位並不是使用者看得到的欄位名稱，
    /// 為了通過 R5b 而去 ProjectZValidationResource 生一個假欄位名反而是噪音。</para>
    /// </summary>
    public sealed record PlantParamOverviewCell(string PlantId, string State, string? DisplayValue)
    {
        /// <summary>該廠有覆寫列（含「明確覆寫成空值」）。</summary>
        public const string Overridden = "Overridden";

        /// <summary>該廠沒有覆寫列，吃全域值。</summary>
        public const string Inherited = "Inherited";
    }

    /// <summary>報表一列＝一個參數；<paramref name="Cells"/> 與 Controller 給的廠別清單同順序。</summary>
    public sealed record PlantParamOverviewRow(
        PlantParamDef Def,
        string? GlobalValue,
        IReadOnlyList<PlantParamOverviewCell> Cells);

    /// <summary>整份報表：欄（廠別）與列（參數）分開帶，避免 View 再去查一次廠別。</summary>
    public sealed record PlantParamOverviewVm(
        IReadOnlyList<PlantParamOverviewPlant> Plants,
        IReadOnlyList<PlantParamOverviewRow> Rows);

    /// <summary>報表的欄：廠別代碼與顯示名稱。</summary>
    public sealed record PlantParamOverviewPlant(string Id, string Name);
}

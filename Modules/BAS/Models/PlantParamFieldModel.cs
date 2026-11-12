namespace SmartFactory.MES.Modules.BAS.Models
{
    /// <summary>
    /// 廠別參數三態欄位的檢視模型（<c>Views/Plant/_PlantParamField.cshtml</c>）。
    /// </summary>
    /// <param name="Def">參數描述。</param>
    /// <param name="IsOverridden">本廠是否有覆寫（有列＝覆寫，無列＝繼承全域）。</param>
    /// <param name="Value">控制項預填值：覆寫時為覆寫值，繼承時為全域值（讓使用者一勾就是從全域接手改）。</param>
    /// <param name="GlobalValue">全域值，供對照顯示；覆寫時加刪除線。</param>
    public sealed record PlantParamFieldModel(
        PlantParamDef Def,
        bool IsOverridden,
        string? Value,
        string? GlobalValue);
}

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>
    /// 「工單耗用材料」的庫存異動類別（完工材料成本、在製品材料腿、資料稽核共用這一份）。
    ///
    /// 一般工單的發退料與後沖是 <c>Production</c>；重工工單投產自動開的重工發料（RI）／重工退料（RR）是
    /// <c>Rework</c>。原本各處只認 <c>Production</c>，重工工單發出去的料就不進工單材料成本、也不在在製品裡——
    /// 完工入庫的良品材料成本是 0，料在發料當下已直接轉損失（mfg-domain.md §16.4b，2026-10-16 修正）。
    ///
    /// 只用在「依工單的發退料單單號（或 <c>{工單}-BF</c>）篩出來的異動」上；庫存模組的重工發退料單
    /// （/ReworkMaterial，不屬於任何工單）同樣是 <c>Rework</c>，但它的單號永遠不會出現在那份清單裡。
    /// </summary>
    public static class WorkOrderMaterialMovement
    {
        public static readonly string[] Categories = { "Production", "Rework" };
    }
}

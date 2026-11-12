namespace SmartFactory.MES.Modules.BAS.Services
{
    /// <summary>
    /// 全系統可儲存的小數位數上限。
    ///
    /// 金額與數量欄位以 <c>numeric(18,4)</c> 為主（全庫 241 欄），因此無論單位或幣別
    /// 主檔把小數位數設到幾位，超過 4 位的部分都存不進資料庫——PostgreSQL 會在寫入時
    /// 再無聲四捨五入一次，造成「應用層算出來的值」與「資料庫實際存的值」不同。
    /// 把上限集中在這裡，讓輸入驗證（[Range]）與四捨五入（Round*Async 的夾擠）
    /// 共用同一個數字，避免兩邊各自寫死而漂移。
    /// </summary>
    public static class DecimalPrecision
    {
        /// <summary>可儲存的最大小數位數（對應 numeric(18,4) 的 scale）。</summary>
        public const int MaxStorable = 4;

        /// <summary>
        /// 全庫 decimal 欄位的三種 scale，以及各自的適用範圍。
        /// 新增欄位時對照本表選一個，並以 <c>[Column(TypeName = ...)]</c> 明寫
        /// （不寫會產出無界的 PG <c>numeric</c>，見 AlignUnboundedNumericColumns migration）。
        ///
        /// <list type="table">
        /// <item><term>decimal(18,4)</term><description>
        ///   **交易量與金額**——使用者輸入或由其累加而來，寫入前都經
        ///   <c>RoundQtyAsync</c>／<c>RoundAsync</c> 夾到 <see cref="MaxStorable"/> 位。
        ///   全庫多數欄位屬此類。</description></item>
        /// <item><term>decimal(18,6)</term><description>
        ///   **每單位比率與其推導值**——BOM 單位用量、單位工時、匯率、單位換算分子分母，
        ///   以及由它們乘算出來的需求量（如 <c>WorkOrderMaterial.RequiredQty</c>）。
        ///   這些不是使用者輸入的量，不受 UOM 小數位數約束，收窄成 4 位會實際改變算量。</description></item>
        /// <item><term>decimal(8,4)</term><description>稅率（百分比小數，如 0.05）。</description></item>
        /// </list>
        ///
        /// 唯一刻意保留無界 <c>numeric</c> 的是檢驗量測值（規格上下限／目標值／量測值／σ）：
        /// 精度由量測儀器決定，不屬於上面任何一類。
        /// </summary>
        public const int RatioScale = 6;

        /// <summary>把主檔設定的小數位數夾進可儲存範圍，供既有非法資料的防呆。</summary>
        public static int Clamp(int decimalPlaces) => Math.Clamp(decimalPlaces, 0, MaxStorable);
    }
}

namespace SmartFactory.MES.Modules.ACT
{
    /// <summary>
    /// 標準會計科目對應（§4.4）。本 MES 採「管理日記帳」設計，無獨立科目主檔；
    /// 各 posting service 以此常數產生 <see cref="Models.JournalLine"/>，確保科目代碼/名稱一致，
    /// 可彙總匯出至外部會計系統（鼎新／SAP）。新增業務交易過帳時於此補列科目。
    /// </summary>
    public static class StandardAccounts
    {
        // ── 資產 1xxx ──
        public const string CashAndBankCode             = "1130";
        public const string CashAndBankName             = "銀行存款";
        public const string AccountsReceivableCode      = "1170";
        public const string AccountsReceivableName      = "應收帳款";
        /// <summary>備抵損失（應收帳款的減項，貸方餘額）。月結步驟 9 依 ECL 評估額調整，見 PeriodEndAdjustmentService。</summary>
        public const string ReceivableLossAllowanceCode = "1179";
        public const string ReceivableLossAllowanceName = "備抵損失-應收帳款";
        public const string RawMaterialInventoryCode    = "1401";
        public const string RawMaterialInventoryName    = "原料存貨";
        public const string InputVatCode                = "1155";   // 進項稅額（AP 認列時借方，對應進貨稅額）

        /// <summary>
        /// 廠際調撥往來（跨廠調撥的清算科目）。
        ///
        /// 各廠共用同一套帳，但 <c>JournalEntry.PlantId</c> 是有廠別的，全站稽核與對帳
        /// 也都逐廠在跑，所以「每一廠的存貨科目要等於該廠的庫存明細帳」是這個系統
        /// 實際主張的不變量。跨廠調撥把貨從甲廠搬到乙廠，價值必須跟著搬：
        ///   撥出（XP）：甲廠 借 1450 ／ 貸 1401
        ///   撥入（XI）：乙廠 借 1401 ／ 貸 1450
        /// 在途期間（已撥出、未收貨）甲廠的 1450 就是在途存貨；收貨之後兩廠的 1450
        /// 一正一負，即甲廠對乙廠的往來餘額，**全公司合計恆為 0**。
        /// </summary>
        /// <summary>
        /// 備抵存貨跌價損失（存貨的減項，貸方餘額）。月結步驟 9 依 NRV 評估額調整。
        /// <para><b>不直接貸 1401</b>：1401 要等於庫存明細帳（INV_ROLLFORWARD 稽核），
        /// 而 NRV 減損不改單位成本也不產生庫存異動。</para>
        /// </summary>
        public const string InventoryValuationAllowanceCode = "1419";
        public const string InventoryValuationAllowanceName = "備抵存貨跌價損失";
        public const string InterPlantClearingCode      = "1450";
        public const string InterPlantClearingName      = "廠際調撥往來";
        public const string InputVatName                = "進項稅額";

        // ── 負債 2xxx ──
        public const string AccountsPayableCode         = "2100";
        public const string AccountsPayableName         = "應付帳款";
        public const string GrIrAPClearingCode          = "2152";   // 進貨暫估應付（Purchase GR/IR）
        public const string GrIrAPClearingName          = "進貨暫估應付";
        public const string AccruedFreightCode          = "2153";   // 暫估應付運費（Landed Cost GR/IR）
        public const string AccruedFreightName          = "暫估應付運費";
        public const string OutputVatCode               = "2155";   // 銷項稅額（AR 開票時貸方，對應銷貨稅額）
        public const string OutputVatName               = "銷項稅額";
        /// <summary>
        /// 借入物料的負債（`IB` 借入／`BR` 借入還回）。
        ///
        /// <para><b>刻意不用 2152「進貨暫估應付」</b>：那個科目的語意是「已收貨未收發票，
        /// 等 AP 認列來沖」——採購鏈的每一個環節都預期它會被一張發票沖掉，
        /// 而 `GRIR_AGING` 稽核正是在找沖不掉的滯留。借料沒有發票會來，
        /// 掛進 2152 等於長出一筆 AP 端根本不知道、永遠沒人沖的負債，
        /// 並且會讓帳齡稽核每天報一筆假異常（與 4210 存貨盤盈那條註解同一個道理）。</para>
        ///
        /// <para>實物借還不開發票（無銷售事實）。若最後不還而改買斷，
        /// 屆時才開發票並把本科目轉列 2100 應付帳款。</para>
        /// </summary>
        public const string LoanPayableCode             = "2170";   // 其他應付款—借入物料
        public const string LoanPayableName             = "其他應付款-借入物料";

        // ── 收入 4xxx ──
        public const string SalesRevenueCode            = "4100";   // 銷貨收入（AR 開票時貸方）
        public const string SalesRevenueName            = "銷貨收入";
        public const string FxGainCode                  = "4190";
        public const string FxGainName                  = "匯兌利益";
        // 存貨盤盈：盤點多出來的貨沒有供應商、沒有人會來請款，不能貸「進貨暫估應付」——
        // 那個科目是採購鏈用來等 AP 認列來沖的，掛進去等於長出一筆永遠沒人沖的假負債。
        public const string InventoryGainCode           = "4210";
        public const string InventoryGainName           = "存貨盤盈";

        // ── 成本／費用 5xxx ──
        public const string CogsCode                    = "5001";
        public const string CogsName                    = "銷貨成本";
        public const string DirectLaborAbsorbedCode     = "5110";   // 完工人工吸收（貸方 contra，§16.3）
        public const string DirectLaborAbsorbedName     = "直接人工-已分配";
        public const string MfgOverheadAbsorbedCode     = "5120";   // 完工製費吸收（貸方 contra，§16.3）
        public const string MfgOverheadAbsorbedName     = "製造費用-已分配";
        public const string MfgCostVarianceCode         = "5130";   // 製造成本差異（期末實際費率重估回刷差異，§3.3）
        public const string MfgCostVarianceName         = "製造成本差異";
        public const string ScrapLossCode               = "5210";
        public const string ScrapLossName               = "報廢損失";
        // 存貨盤損：與報廢損失分開。報廢是「知道是哪一批、為什麼壞」的主動處分，
        // 盤損是「帳實不符、原因不明」的被動認列，兩者的管理意義與追查對象都不同。
        public const string InventoryLossCode           = "5220";
        public const string InventoryLossName           = "存貨盤損";
        /// <summary>
        /// 樣品費（`SO` 樣品出貨／`SR` 樣品退回）。
        ///
        /// <para><b>與 5210 報廢損失分開</b>：樣品送客戶是**推廣行為**，料是照計畫送出去的；
        /// 報廢是實體損壞的處分。兩者都讓存貨減少，但一個是花錢買機會、一個是認賠，
        /// 混在一起之後「報廢率」這個管理指標會隨業務拜訪量起伏，而那兩件事無關。</para>
        /// </summary>
        public const string SampleExpenseCode           = "5230";
        public const string SampleExpenseName           = "樣品費";
        /// <summary>存貨跌價損失（IAS 2，銷貨成本項下；迴轉時貸方）。</summary>
        public const string InventoryWriteDownLossCode  = "5240";
        public const string InventoryWriteDownLossName  = "存貨跌價損失";
        /// <summary>
        /// 重工損失（mfg-domain.md §16.4b）：重工工單超出「不良品原值」的那部分成本（重工用料、人工、製費），
        /// 以及庫存模組重工發退料單發出的料。與 5210 報廢損失分開——報廢是認賠處分，重工是把東西救回來
        /// 多花的錢，兩個管理指標混在一起就看不出品質異常成本（IAS 2：異常耗損不得計入存貨成本）。
        /// </summary>
        public const string ReworkLossCode              = "5250";
        public const string ReworkLossName              = "重工損失";
        /// <summary>
        /// 內部領用費用（2026-10-24 使用者裁決）：內部訂單領用（NO）借記、退料（NR）貸回。
        /// 原本落在報廢損失，與「壞掉、不能用」的報廢混在一起，看不出部門實際領用多少。
        /// </summary>
        public const string InternalUseExpenseCode      = "5260";
        public const string InternalUseExpenseName      = "內部領用費用";
        public const string FxLossCode                  = "5199";
        public const string FxLossName                  = "匯兌損失";
        /// <summary>
        /// 進貨退出價差（2026-10-23）：退貨存貨照現值出、進貨暫估應付照原進貨單價沖，兩者差額落這裡。
        /// 原本兩邊都用現值，同料號進貨價不同時 2152 留下永遠沖不掉的差額（GRIR_AGING 每天報、擋月結）。
        /// </summary>
        public const string PurchaseReturnVarianceCode  = "5152";
        public const string PurchaseReturnVarianceName  = "進貨退出價差";
        public const string LandedCostVarianceCode      = "5151";   // 進料費用差異（PPV，已出庫對應）
        public const string LandedCostVarianceName      = "進料費用差異";

        // ── 營業費用 6xxx ──
        /// <summary>預期信用減損損失（IFRS 9；迴轉時貸方）。</summary>
        public const string CreditImpairmentLossCode    = "6250";
        public const string CreditImpairmentLossName    = "預期信用減損損失";

        /// <summary>
        /// 本檔宣告的所有系統科目（代碼, 名稱）。以反射列舉 <c>XxxCode</c>／<c>XxxName</c> 成對常數，
        /// 而不是再維護一份清單——清單抄第二次就會與常數分岔，而分岔沒有任何徵兆。
        ///
        /// <para>用途是守門：<c>ChartOfAccountSeedTests</c> 拿它比對 migration 裡的種子，
        /// 在這裡加一個常數卻沒補種子時會當場紅一支。</para>
        /// </summary>
        public static IReadOnlyList<(string Code, string Name)> All()
        {
            var fields = typeof(StandardAccounts)
                .GetFields(System.Reflection.BindingFlags.Public |
                           System.Reflection.BindingFlags.Static |
                           System.Reflection.BindingFlags.FlattenHierarchy)
                .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
                .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!, StringComparer.Ordinal);

            return fields.Keys
                .Where(k => k.EndsWith("Code", StringComparison.Ordinal))
                .Select(k => (Key: k[..^"Code".Length], Code: fields[k]))
                .Where(x => fields.ContainsKey(x.Key + "Name"))
                .Select(x => (x.Code, Name: fields[x.Key + "Name"]))
                .OrderBy(x => x.Code, StringComparer.Ordinal)
                .ToList();
        }
    }
}

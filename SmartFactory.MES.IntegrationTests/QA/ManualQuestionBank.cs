namespace SmartFactory.MES.IntegrationTests.QA
{
    /// <summary>難題的種類，各自針對一種指標飽和的來源。</summary>
    public enum HardKind
    {
        /// <summary>近義干擾：<c>Distractor</c> 必須排在 <c>Expected</c> 之後。</summary>
        Distractor,

        /// <summary>需推理：答案在內文的一句規則裡，標題不含線索。</summary>
        Inference,

        /// <summary>跨頁：<c>Expected</c> 是一組頁面，量涵蓋率而非名次。</summary>
        CrossPage,
    }

    /// <param name="Question">提問，用使用者真的會打的字。</param>
    /// <param name="Expected">正解頁（跨頁題為多份）。</param>
    /// <param name="Distractor">近義干擾頁；非干擾題為 <c>null</c>。</param>
    public sealed record HardQuestion(
        string Question, string[] Expected, string? Distractor, HardKind Kind);

    /// <summary>
    /// **難題題庫**（規格 §10.7「難題題庫」、定案 #40）。
    ///
    /// <para><b>為什麼要有第二份題庫。</b><see cref="ManualRetrievalBenchmarkTests.Bank"/> 那 30 題
    /// 在語意通道上量到 Recall@1 <b>30/30</b>、MRR 1.000——**指標已經飽和**。
    /// 飽和的尺量不出任何東西：換 embedding 模型最多打平，而「打平」會被讀成「換不換都一樣」，
    /// 那是一個由量尺產生的結論，不是由模型產生的。生成模型選型（待決 #5）同理。</para>
    ///
    /// <para><b>三類難題，各自針對一種飽和來源。</b></para>
    /// <list type="bullet">
    ///   <item><b>近義干擾</b>（<see cref="HardKind.Distractor"/>）——手冊裡本來就有大量近義頁：
    ///     「庫存評估表」vs「庫存價值分析」、三種毛利、AP/AR 鏡像對。舊題庫每題只有一個合理答案，
    ///     所以「命中」很容易；這裡每題**指定一個必須被壓在正解之下的干擾頁**，
    ///     量的是<b>排序的分辨力</b>而不是召回。這個指標天生不會飽和。</item>
    ///   <item><b>需推理</b>（<see cref="HardKind.Inference"/>）——問的是內文裡的一句規則
    ///     （「呆滯天數怎麼算」「已投產工單重排會不會被動到」），標題完全幫不上忙。
    ///     舊題庫的題目多半能靠標題命中。</item>
    ///   <item><b>跨頁</b>（<see cref="HardKind.CrossPage"/>）——正解是**一組頁面**。
    ///     這類提問在真實使用中很常見（「委外從詢價到收貨」），而 Recall@1 對它沒有意義。</item>
    /// </list>
    ///
    /// <para><b>正解都是從手冊內文查證出來的，不是照標題編的。</b>例如「月結要跟財報存貨科目核對」
    /// 判給「庫存評估表」而非「庫存價值分析」，依據是前者內文寫著「供月結與財報存貨科目核對」、
    /// 後者寫的是「掌握資金積壓分布」與「ABC 分析」——兩頁的標題比內文更像，這正是要量的東西。</para>
    ///
    /// <para><b>題庫與量測分開放</b>（2026-08-06 拆出本檔）：同一批題目要同時餵給
    /// 檢索評測（<see cref="ManualHardBenchmarkTests"/>）與生成評測
    /// （<see cref="ManualGenerationBenchmarkTests"/>）。兩邊各留一份的話，
    /// 改了一邊沒改另一邊，比出來的檢索與生成就不是同一份題目——
    /// 而那種偏差不會有任何錯誤訊息。</para>
    /// </summary>
    public static class ManualQuestionBank
    {
        private static HardQuestion D(string q, string expected, string distractor) =>
            new(q, new[] { expected }, distractor, HardKind.Distractor);

        private static HardQuestion I(string q, string expected) =>
            new(q, new[] { expected }, null, HardKind.Inference);

        private static HardQuestion X(string q, params string[] expected) =>
            new(q, expected, null, HardKind.CrossPage);

        public static readonly HardQuestion[] Hard =
        {
            // ── 近義干擾：兩頁的標題比內文更像 ────────────────────────────
            D("月結要跟財報的存貨科目核對金額，該用哪張表",
                "inv/inventory-valuation.html", "inv/inventory-value.html"),
            D("想知道資金積壓在哪些料、哪幾項佔的比重最大",
                "inv/inventory-value.html", "inv/inventory-valuation.html"),
            D("要抓出超過門檻天數都沒有進出的料",
                "inv/slow-moving-inventory.html", "inv/inventory-aging.html"),
            D("庫存依入庫時間分成幾個區間、各區間金額佔多少",
                "inv/inventory-aging.html", "inv/slow-moving-inventory.html"),
            D("哪個客戶的毛利率最低",
                "rpt/margin-analysis.html", "rpt/gross-margin.html"),
            D("這一季整體毛利率的走勢",
                "rpt/gross-margin.html", "rpt/margin-analysis.html"),
            D("訂價前想知道扣掉變動成本後每個產品還剩多少貢獻",
                "rpt/contribution-margin.html", "rpt/gross-margin.html"),
            D("供應商準時到貨的比率",
                "pur/supplier-delivery-rate.html", "ord/delivery-rate.html"),
            D("我們出貨給客戶準不準時",
                "ord/delivery-rate.html", "pur/supplier-delivery-rate.html"),
            D("盤點盤出來的差異金額依原因統計",
                "inv/stock-variance.html", "inv/stock-adjustment.html"),
            D("盤盈盤虧要開什麼單子調整庫存",
                "inv/stock-adjustment.html", "inv/stock-variance.html"),
            D("某個品號這個月的期初、進出、期末各是多少",
                "inv/stock-ledger.html", "inv/stock-movement.html"),
            D("庫存數字不對，想追到原始單據",
                "inv/stock-movement.html", "inv/stock-ledger.html"),
            D("業務要查某張工單被排在什麼時候",
                "mfg/schedule-query.html", "mfg/schedule.html"),
            D("要重新排機台的甘特圖",
                "mfg/schedule.html", "mfg/schedule-query.html"),
            D("報工的實際工時跟途程標準工時差多少",
                "mfg/production-efficiency.html", "mfg/oee.html"),
            D("稼動率乘產能效率再乘良率的那個指標",
                "mfg/oee.html", "mfg/utilization.html"),
            D("機台負載會不會超過可用時數",
                "mfg/utilization.html", "mfg/oee.html"),
            D("客戶欠我們的錢逾期多久、金額多少",
                "fin/ar-aging.html", "fin/ap-aging.html"),
            D("還沒付給供應商的錢逾期多久",
                "fin/ap-aging.html", "fin/ar-aging.html"),
            D("明天要開工的派工，料齊了沒、模具會不會撞",
                "mfg/preparation-board.html", "inv/material-staging.html"),
            D("依工單 BOM 開單子去倉庫領料",
                "mfg/material-slip.html", "inv/material-staging.html"),
            D("採購單過了交期還沒到貨要跟催",
                "pur/purchase-overdue.html", "ord/order-overdue.html"),
            D("存貨成本高於可變現淨值要提列跌價",
                "act/nrv-assessment.html", "act/ecl.html"),
            D("用帳齡矩陣估應收的預期信用損失率",
                "act/ecl.html", "act/bad-debt-provision.html"),
            D("關帳之後就不能過帳，這個開關在哪裡",
                "act/accounting-period.html", "act/month-end.html"),
            D("月結要逐項打勾的檢查清單",
                "act/month-end.html", "act/accounting-period.html"),

            // ── 需推理：答案是內文的一句規則，標題不含線索 ─────────────────
            I("毛利率算出來是負的代表什麼", "rpt/gross-margin.html"),
            I("呆滯天數是怎麼算出來的", "inv/slow-moving-inventory.html"),
            I("庫齡固定分成幾段", "inv/inventory-aging.html"),
            I("毛利的成本是用標準成本推估還是實際出庫成本", "rpt/margin-analysis.html"),
            I("稼動率的分母是什麼", "mfg/utilization.html"),
            I("OEE 的計畫時間是從哪裡來的", "mfg/oee.html"),
            I("壞帳的逾期天數怎麼算", "act/bad-debt-provision.html"),
            I("MRP 重跑會不會把已經核准的請購單蓋掉", "mfg/mrp-variance-decision.html"),
            I("已經投產的工單重新排程會被動到嗎", "mfg/schedule.html"),
            I("發料的成本是用哪個時點的", "mfg/material-slip.html"),
            I("MRP 是逐批下還是有最小批量", "mfg/mrp-run.html"),
            I("大區間查帳本明細會被截斷，合計還準嗎", "inv/stock-ledger.html"),
            I("已經結案的採購行還會出現在跟催清單嗎", "pur/purchase-overdue.html"),
            I("同一副模具同時段排在兩台機台會被擋下來嗎", "mfg/schedule.html"),

            // ── 跨頁：正解是一組頁面 ──────────────────────────────────────
            X("委外加工從詢價到收貨的完整流程",
                "osp/outsource-quotation.html", "osp/outsource-purchase-order.html",
                "osp/outsource-material.html", "osp/outsource-receipt.html"),
            X("月結關帳前要做哪些事、關帳開關在哪",
                "act/month-end.html", "act/accounting-period.html"),
            X("存貨提減損評估完怎麼跟月結銜接",
                "act/nrv-assessment.html", "act/month-end.html"),
            X("逾期的單子——採購沒進來的跟客戶沒出去的分別在哪看",
                "pur/purchase-overdue.html", "ord/order-overdue.html"),
            X("從 MRP 展開到工單開工要經過哪些畫面",
                "mfg/mrp-run.html", "mfg/work-order.html", "mfg/dispatch.html"),
            X("工單的料從備料、發料到進度追蹤",
                "inv/material-staging.html", "mfg/material-slip.html",
                "mfg/material-slip-progress.html"),
            X("應收從開發票、收款到帳齡",
                "fin/ar-invoice.html", "fin/ar-receipt.html", "fin/ar-aging.html"),
            X("應付從發票到付款",
                "fin/ap-invoice.html", "fin/ap-payment.html"),
            X("不良品判定後的處置跟重工用料",
                "qcr/ncr.html", "inv/rework-material.html"),
            X("供應商評鑑要看哪些數據",
                "pur/vendor-evaluation.html", "pur/supplier-delivery-rate.html"),
            X("盤點從建清冊到差異分析",
                "inv/inventory-count.html", "inv/stock-variance.html",
                "inv/stock-adjustment.html"),
            X("班別跟工作日曆會影響排程的哪些地方",
                "bas/shift.html", "mfg/work-calendar.html", "mfg/schedule.html"),
        };

        /// <summary>
        /// **規則類題目**（規格 §10.11、之十二）：答案只在**擷取物**裡，手冊沒有這一頁。
        ///
        /// <para><b>刻意不併進 <see cref="Hard"/>。</b>那 53 題是跨輪比較的基準
        /// （定案 #40、#44、#46 都拿它比過），把題數改掉會讓所有歷史數字失去可比性——
        /// 而這一組問的根本是另一件事：**沒有擷取物時答不出來、有了才答得出來**。
        /// 兩個量尺混在一個陣列裡，下一次有人只想比生成模型時就會連帶量到擷取物。</para>
        ///
        /// <para><b><c>Expected</c> 是 <c>rules/&lt;slug&gt;.html</c></b>，不是手冊頁碼。
        /// 頁碼形狀與手冊一致是刻意的（見 <c>ExtractEntry.Slug</c>），所以
        /// <c>QaPromptBuilder.PageCode</c> 對兩種語料是同一段程式；評測端也就不需要特例。</para>
        ///
        /// <para><b>slug 是標題路徑的雜湊，不是段落序號</b>（2026-08-17 改）。下面這幾串
        /// 看起來像亂碼的十六進位是刻意的：序號版的 slug 綁「第幾段」，在來源規格
        /// 插一個標題就會整批位移，於是這裡的 <c>Expected</c> 會安靜地指到別的內容，
        /// 量出一個「引文全錯」的假結果。要對回是哪一段，查
        /// <c>.claude/qa-extracts/currency.json</c> 同一則的 <c>headingPath</c>。</para>
        ///
        /// <para><b>只涵蓋已核可的 4 段</b>（換算公式／匯率類型選擇／表頭以本幣加總／常見錯誤對照）。
        /// 未核可的段落照樣進索引但被 <c>DocChunkFilter</c> 擋在檢索之外，
        /// 拿它們出題等於在量一個永遠拿不到的正解——那是 <see cref="RejectedRuleProbes"/> 的工作。</para>
        ///
        /// <para>⚠️ <b>前兩題的 <c>Expected</c> 刻意含手冊頁</b>：查證手冊時發現
        /// <c>bas/currency.html</c> 已經寫著「本位幣金額 ＝ 外幣金額 × 匯率」——
        /// **擷取物最想補的那一條規則，手冊本來就有**。不把手冊頁列進 <c>Expected</c> 的話，
        /// 沒有擷取物的那一臂會因為引了正確的手冊頁而被記成引錯頁，
        /// 於是量出一個「擷取物讓引文變準了」的假效果。後四題手冊真的沒有
        /// （<c>Buy</c>／<c>Sell</c>／<c>Mid</c> 的選用時機、預設值、表頭以本幣加總都不在手冊裡）。</para>
        /// </summary>
        public static readonly HardQuestion[] RuleGrounded =
        {
            new("外幣單據換算成本幣是要乘匯率還是除匯率",
                new[] { "rules/currency-8be6d411.html", "rules/currency-855873ea.html", "bas/currency.html" },
                null, HardKind.Inference),
            new("匯率換算最常見的錯誤是什麼",
                new[] { "rules/currency-855873ea.html", "bas/currency.html" },
                null, HardKind.Inference),
            I("採購付款要用買入匯率還是賣出匯率",
                "rules/currency-d151520a.html"),
            I("成本計算跟存貨評價該用哪一種匯率",
                "rules/currency-d151520a.html"),
            I("一張單據裡有不同外幣的明細，表頭合計要怎麼算",
                "rules/currency-9b1a9a6a.html"),
            I("沒有指定匯率類型的時候系統預設用哪一種",
                "rules/currency-d151520a.html"),
        };

        /// <summary>
        /// **<see cref="RuleGrounded"/> 的英文鏡像**（§17.9 缺口 G4）。
        ///
        /// <para><b>題目逐題對應、<c>Expected</c> 一字不改。</b>要量的是「同一個問題換成英文問
        /// 還拿不拿得到同一段規則」，正解跟著改就量不到那件事了。手冊頁的 <c>DocUrl</c>
        /// 中英共用同一個路徑（只有 <c>Lang</c> 欄不同），所以 <c>bas/currency.html</c>
        /// 在兩份題庫裡是同一個字串——這是查證過的，不是假設。</para>
        ///
        /// <para><b>現況這六題預期是 0 命中規則段落</b>：擷取物 1288 段全部是 <c>zh</c>，
        /// 而 <c>DocChunkFilter</c> 的語系條件套在兩個語料別上。這一組的用途就是把那件事
        /// 變成一個**跑得出來的數字**——在它存在之前，G4 只是一段讀 SQL 讀出來的推論。</para>
        ///
        /// <para>⚠️ <b>前兩題就算不開跨語系也可能有分數</b>，因為它們的 <c>Expected</c> 含
        /// <c>bas/currency.html</c> 而手冊有英文版。判讀時要分開看「命中規則段落」與
        /// 「命中任一正解」——只看後者會把手冊的功勞算到擷取物頭上，
        /// 那正是 <see cref="RuleGrounded"/> 那段註解已經記過一次的陷阱。</para>
        /// </summary>
        public static readonly HardQuestion[] RuleGroundedEn =
        {
            new("To convert a foreign currency document into local currency, do I multiply or divide by the rate?",
                new[] { "rules/currency-8be6d411.html", "rules/currency-855873ea.html", "bas/currency.html" },
                null, HardKind.Inference),
            new("What is the most common mistake in exchange rate conversion?",
                new[] { "rules/currency-855873ea.html", "bas/currency.html" },
                null, HardKind.Inference),
            I("Should purchase payments use the buying rate or the selling rate?",
                "rules/currency-d151520a.html"),
            I("Which exchange rate type should be used for costing and inventory valuation?",
                "rules/currency-d151520a.html"),
            I("If one document has detail lines in different foreign currencies, how is the header total calculated?",
                "rules/currency-9b1a9a6a.html"),
            I("Which exchange rate type does the system use by default when none is specified?",
                "rules/currency-d151520a.html"),
        };

        /// <summary>
        /// **退回段落的對照題**：這些提問的答案落在**被審核退回**的擷取物裡
        /// （<c>currency-81e73a60</c> 系統會自動用最新匯率／<c>currency-19fe712b</c> 方向寫反／
        /// <c>currency-c58a6ef9</c> API 回傳結構／<c>currency-1640e02b</c> 歷程段落）。
        ///
        /// <para><b>兩臂都該拒答</b>。這一組量的不是模型，是**核可閘門真的有作用**：
        /// 未核可的段落已經在索引裡、也已經被嵌入，只差 <c>DocChunkFilter</c> 那一個
        /// <c>AND c."Approved"</c>。閘門若失效，症狀不是報錯而是「答得出來」——
        /// 而答出來的正是審核者判定不可用的內容（方向寫反的那一段就在其中）。</para>
        ///
        /// <para><b>候選題被砍掉一題</b>：「系統是自動抓最新匯率還是依單據日期抓」原本要用來
        /// 對照 <c>currency-81e73a60</c>，但手冊 <c>bas/currency.html</c> 寫著
        /// 「若當日無匯率，系統會自動使用最近一筆有效匯率」——**手冊答得出來**，
        /// 模型照實回答會被記成閘門失效。**探針必須是手冊也沒有的東西**，否則量到的是誤報。</para>
        /// </summary>
        public static readonly string[] RejectedRuleProbes =
        {
            "查匯率的 API 要打哪一支、查不到的時候回傳什麼",
            "採購單存的總金額是交易幣還是換算後的本幣",
        };

        /// <summary>
        /// 不該有答案的提問，**刻意挑會踩字形假朋友的**。
        ///
        /// <para>S2-d 量到分離度訂不出門檻，上界那題是「留職停薪」被比成「計劃停機」——
        /// 樣本只有 4 題，上界等於由一題決定。這裡擴到 12 題並刻意加入
        /// 請假／請購、報銷／報廢、停車位／儲位、盤纏／盤點 這類**字面接近但語意無關**的提問，
        /// 讓上界是量出來的而不是猜的。**這一組變難了，分離度只會更難看——這是要的**：
        /// 一個由簡單樣本撐出來的漂亮門檻，上線後才是真的危險。</para>
        ///
        /// <para>對生成評測而言，這 12 題是**拒答題**：檢索一定會給回弱相關的參考資料
        /// （定案 #29 不設門檻），模型該做的是說它不知道，而不是拿那些參考資料硬掰。</para>
        /// </summary>
        public static readonly string[] OutOfScope =
        {
            "鸚鵡螺保養週期",
            "今天午餐吃什麼",
            "公司尾牙什麼時候辦",
            "怎麼申請留職停薪",
            "員工特休假怎麼請",
            "出差的交通費怎麼報銷",
            "公司 VPN 連不上要找誰",
            "停車位怎麼申請",
            "薪資單什麼時候發",
            "教育訓練課程在哪裡報名",
            "會議室怎麼預約",
            "識別證掉了怎麼補辦",
        };
    }
}

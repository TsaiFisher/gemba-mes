using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.RPS.Contracts;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.RPS;

/// <summary>
/// 十一張報表畫面匯出的欄頭**逐字**釘住（§16.1 S-5 收尾）。
///
/// <para><b>為什麼需要這一條</b>：S-5 收尾把畫面 <c>Export</c> 裡的硬編雙語陣列刪掉、
/// 改接工具宣告的欄位，於是原本「兩份互相對照」的那條守門（<c>ReportExportHeaderTests
/// .工具宣告的欄頭與畫面匯出逐欄一致</c>）失去比對對象而拿掉了。拿掉之後，
/// 使用者實際拿到的欄頭字串就**再也沒有任何測試釘住**——
/// <c>每個欄頭鍵在兩個語系都有值</c> 只驗「查得到、不是鍵本身」，
/// 把 resx 的「客戶代號」改成「客戶」它一樣全綠。</para>
///
/// <para>所以這裡把**收尾前畫面實際輸出的字串**抄下來當期望值。這不是重述工具的宣告
/// （那會是套套邏輯），是拿 2026-09-15 收尾前的行為當基準：
/// 任何一欄的用詞被改動，這條會紅，並指出是哪一張報表的第幾欄。
/// 用詞若是**刻意**要改，連同這裡的期望值一起改——重點是它必須是個決定，不是副作用。</para>
///
/// <para>⚠️ 欄頭來源是 resx，而 resx 的查找吃 <c>CurrentUICulture</c>。這裡逐語系設定並還原，
/// 不能靠 CI 的預設語系——CI 跑在中性語系底下，只驗那一格會漏掉 en-US 的錯字。</para>
/// </summary>
public class ReportScreenExportHeaderTests : TestBase
{
    /// <summary>收尾前各畫面 <c>Export</c> 硬編的雙語標題陣列，逐字照抄。</summary>
    private static readonly Dictionary<string, (string[] Zh, string[] En)> ScreenHeaders = new()
    {
        ["ReturnRate"] = (
            ["品號", "客戶代號", "出貨量", "退貨量", "退貨率%"],
            ["Product", "Customer", "Shipped Qty", "Returned Qty", "Return Rate%"]),
        ["DeliveryRate"] = (
            ["客戶代號", "應出明細數", "準時筆數", "逾期筆數", "達成率%"],
            ["Customer", "Due Lines", "On-Time", "Late", "On-Time Rate%"]),
        ["OrderOverdue"] = (
            ["訂單號", "客戶", "訂單日", "品號", "行號", "預交日", "訂購量", "已出量", "未出量", "逾期天數"],
            ["Sales Order", "Customer", "Order Date", "Product", "Line", "Delivery Date", "Order Qty", "Shipped Qty", "Pending Qty", "Overdue Days"]),
        ["PurchaseOverdue"] = (
            ["採購單號", "供應商", "採購日", "品號", "行號", "預交日", "採購量", "已到量", "未到量", "逾期天數", "採購員"],
            ["PO No.", "Supplier", "Order Date", "Product", "Line", "Delivery Date", "Order Qty", "Received Qty", "Pending Qty", "Overdue Days", "Buyer"]),
        ["WorkOrderProgress"] = (
            ["工單號", "品號", "類型", "狀態", "計劃開工", "計劃完工", "計劃量", "完工量", "進度%", "逾期"],
            ["Work Order", "Product", "Type", "Status", "Plan Start", "Plan End", "Plan Qty", "Done Qty", "Progress%", "Overdue"]),
        ["InventoryValuation"] = (
            ["品號", "品名", "儲位", "批號", "在庫量", "均價", "評估金額"],
            ["Product", "Name", "Location", "Lot", "Qty", "Unit Cost", "Total Value"]),
        ["MrpAchievement"] = (
            ["MRP批次", "品號", "需求日期", "計劃量", "實際量", "達成率%", "短缺量", "錯誤"],
            ["MRP Run", "Product", "Required Date", "Required Qty", "Actual Qty", "Achievement%", "Shortfall", "Error"]),
        // 2026-10-27 改「目前＋預計」兩段（與投產料閘共用在途推算），刻意改用詞並加四欄
        ["StockShortage"] = (
            ["品號", "品名", "庫存單位", "安全存量", "在手量（不含待驗）", "預留量", "目前可用", "目前缺口", "採購在途", "委外在途", "預計可用", "預計缺口"],
            ["Product", "Name", "UOM", "Safety Stock", "On Hand (excl. Quarantine)", "Reserved", "Available Now", "Shortage Now", "PO In Transit", "Outsource In Transit", "Projected Available", "Projected Shortage"]),
        ["DefectRate"] = (
            ["品號", "合格量", "不良量", "總量", "不良率%", "報工筆數"],
            ["Product", "OK Qty", "NG Qty", "Total Qty", "Defect Rate%", "Reports"]),
        ["SlowMovingInventory"] = (
            ["品號", "品名", "儲位", "批號", "庫存量", "單位成本", "庫存金額", "最後異動日", "呆滯天數"],
            ["Product", "Name", "Location", "Lot", "Qty", "Unit Cost", "Value", "Last Movement", "Idle Days"]),
        // ── RPT 批次 2（2026-09-15 接進排程時一併釘住）──────────────
        ["GrossMargin"] = (
            ["品號", "出貨金額", "成本", "毛利", "毛利率%", "出貨次數"],
            ["Product", "Revenue", "Cost", "GrossProfit", "Margin%", "ShipCount"]),
        ["MarginAnalysis"] = (
            ["維度", "營收", "銷貨成本", "毛利", "毛利率%", "出貨筆數"],
            ["Dimension", "Revenue", "COGS", "Gross Profit", "Margin %", "Shipments"]),
        ["OutsourcePerformance"] = (
            ["委外廠商", "廠商名稱", "收貨筆數", "準時率%", "收回成品量", "加工費", "超耗扣款", "超耗佔比%"],
            ["Supplier", "Name", "Receipts", "OnTime%", "ReceivedQty", "ProcessingFee", "ScrapPenalty", "ScrapRate%"]),
        ["ContributionMargin"] = (
            ["品號", "品名", "營收", "銷貨成本", "變動占比%", "變動成本", "邊際貢獻", "貢獻率%"],
            ["Product", "Name", "Revenue", "COGS", "Var %", "Variable Cost", "Contribution", "CM %"]),
        ["ProfitLoss"] = (
            ["科目代碼", "科目名稱", "分類", "金額"],
            ["Account", "Name", "Category", "Amount"]),
        ["BalanceSheet"] = (
            ["類別", "科目代碼", "科目名稱", "餘額"],
            ["Category", "Account", "Name", "Balance"]),
    };

    private static List<IReportToolDescriptor> RegisteredTools(ApplicationDbContext ctx)
    {
        var services = new ServiceCollection();
        services.AddSingleton(ctx);
        services.AddSingleton(new Mock<IDateTimeService>().Object);
        services.AddSingleton(new Mock<IPermissionService>().Object);
        services.AddSingleton(new Mock<IUserContext>().Object);
        // RPT 批次 2 的 service 需要這兩個；本測試只問「工具宣告了哪些欄位」，
        // 不執行查詢，所以替身即可——但**必須登錄**，否則工具連建構都建不起來。
        services.AddSingleton(new Mock<ISystemParamService>().Object);
        services.AddSingleton(new Mock<ICurrencyService>().Object);
        services.AddLocalization(o => o.ResourcesPath = "Resources");
        services.AddLogging();
        // 幣別服務吃 IMemoryCache（見 ReportToolKeys 同一行的說明）。
        services.AddMemoryCache();

        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        // 模組清單是單一來源（含 BAS——RPT 財報吃它的 IAccountClassifier）：
        ReportToolKeys.RegisterToolModules(services, config);

        return services.BuildServiceProvider().GetServices<IReportToolDescriptor>().ToList();
    }

    private static string[] HeadersOf(IReportToolDescriptor tool, string culture, string? groupBy = null)
    {
        var request = (ReportRequestBase)Activator.CreateInstance(tool.RequestType)!;
        request.PlantId = "P001";
        request.GroupBy = groupBy;

        var localizer = ReportHeaderLocalizer.Create();
        var previous  = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            return tool.GetExportColumns(request)
                       .Select(c => localizer[c.HeaderKey].Value)
                       .ToArray();
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [Fact]
    public void 各報表的匯出欄頭與釘住當時逐字相同()
    {
        var tools    = RegisteredTools(_context).ToDictionary(t => t.ToolKey);
        var problems = new List<string>();

        foreach (var (toolKey, expected) in ScreenHeaders)
        {
            Assert.True(tools.ContainsKey(toolKey), $"工具 {toolKey} 未登錄——期望值表已與現況脫節");
            var tool = tools[toolKey];

            foreach (var (culture, want) in new[] { ("zh-TW", expected.Zh), ("en-US", expected.En) })
            {
                var got = HeadersOf(tool, culture);
                if (got.Length != want.Length)
                {
                    problems.Add($"{toolKey}（{culture}）：現在 {got.Length} 欄，收尾前 {want.Length} 欄");
                    continue;
                }
                for (var i = 0; i < want.Length; i++)
                    if (got[i] != want[i])
                        problems.Add($"{toolKey}（{culture}）第 {i + 1} 欄：現在「{got[i]}」，收尾前「{want[i]}」");
            }
        }

        Assert.True(problems.Count == 0,
            "畫面匯出的欄頭與 S-5 收尾前不同（若是刻意改用詞，請一併更新本測試的期望值）：\n  "
            + string.Join("\n  ", problems));
    }

    /// <summary>
    /// <c>SalesRanking</c> 單獨一條：第二欄的欄頭隨 <c>GroupBy</c> 變，
    /// 收尾前那一格是變數 <c>groupLabel</c>（zh 品項／客戶，en 直接用 <c>groupBy</c> 原字）。
    /// 舊的守門解析不了非字面值，**這一欄從來沒有被比對過**。
    /// </summary>
    [Theory]
    [InlineData("Customer", "客戶",  "Customer")]
    [InlineData("Product",  "品項",  "Product")]
    [InlineData(null,       "客戶",  "Customer")]   // 非 Product 一律視為依客戶
    public void 銷售排行第二欄的欄頭跟著分群走(string? groupBy, string zh, string en)
    {
        var tool = RegisteredTools(_context).Single(t => t.ToolKey == "SalesRanking");

        Assert.Equal(["名次", zh, "銷售金額", "訂單數", "明細筆數"],
                     HeadersOf(tool, "zh-TW", groupBy));
        Assert.Equal(["Rank", en, "Sales Amount", "Orders", "Lines"],
                     HeadersOf(tool, "en-US", groupBy));
    }
}

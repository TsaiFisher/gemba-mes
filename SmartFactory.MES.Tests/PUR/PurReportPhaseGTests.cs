using Microsoft.AspNetCore.Mvc;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.PUR.Controllers;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.PUR.Services;
using SmartFactory.MES.Modules.PUR.ViewModels;
using SmartFactory.MES.Services;
using Xunit;
using SmartFactory.MES.Tests.RPS;

namespace SmartFactory.MES.Tests.PUR;

/// <summary>OTD 聚合純函式測試（pur-domain.md §5.3/§5.5）。</summary>
public class OtdAggregatorTests
{
    /// <summary>基準「今天」。未交事實的遲延天數以此計。</summary>
    private static readonly DateOnly AsOf = new(2026, 6, 30);

    /// <summary>已交清的事實：收貨日 vs 預交日。</summary>
    private static OtdFact F(string sup, int y, int m, int d, int dueY, int dueM, int dueD)
        => new(sup, new DateOnly(y, m, d), new DateOnly(dueY, dueM, dueD), AsOf);

    /// <summary>未交清的事實（2026-09-21 新增的那一類）：只有預交日，遲延以 AsOf 計。</summary>
    private static OtdFact Outstanding(string sup, int dueY, int dueM, int dueD)
        => new(sup, null, new DateOnly(dueY, dueM, dueD), AsOf);

    [Fact]
    public void Compute_月份分組與達成率()
    {
        var facts = new[]
        {
            F("S1", 2026, 1, 10, 2026, 1, 10),  // 準時（同日視為準時）
            F("S1", 2026, 1, 15, 2026, 1, 12),  // 逾期 3 天
            F("S2", 2026, 2, 5,  2026, 2, 10),  // 準時（提前）
        };

        var (months, _) = OtdAggregator.Compute(facts);

        Assert.Equal(2, months.Count);
        Assert.Equal("2026-01", months[0].YearMonth);
        Assert.Equal(2, months[0].TotalLines);
        Assert.Equal(1, months[0].OnTimeLines);
        Assert.Equal(50m, months[0].OnTimeRate);
        Assert.Equal("2026-02", months[1].YearMonth);
        Assert.Equal(100m, months[1].OnTimeRate);
    }

    [Fact]
    public void Compute_未交清一律算遲交_遲延以今天計()
    {
        var facts = new[]
        {
            F("S1", 2026, 3, 10, 2026, 3, 10),   // 已交清、準時
            Outstanding("S1", 2026, 3, 20),      // 未交清，預交日 3/20 → 到 6/30 已 102 天
        };

        var (months, suppliers) = OtdAggregator.Compute(facts);

        var m = Assert.Single(months);
        Assert.Equal("2026-03", m.YearMonth);
        Assert.Equal(2, m.TotalLines);
        Assert.Equal(1, m.OnTimeLines);
        Assert.Equal(50m, m.OnTimeRate);

        var s1 = Assert.Single(suppliers);
        Assert.Equal(1, s1.LateLines);
        Assert.Equal(102, s1.MaxOverdueDays);    // 3/20 → 6/30
    }

    [Fact]
    public void Compute_依預交日分月而非收貨日()
    {
        // 母體是「這個月應該交的單」。用收貨日分月的話，一張 1 月該交、3 月才收到的單
        // 會被算進 3 月的達成率——1 月看起來沒問題，而它正是 1 月遲交的那一筆。
        var facts = new[] { F("S1", 2026, 3, 5, 2026, 1, 20) };

        var (months, _) = OtdAggregator.Compute(facts);

        var m = Assert.Single(months);
        Assert.Equal("2026-01", m.YearMonth);
        Assert.Equal(0, m.OnTimeLines);
    }

    [Fact]
    public void Compute_平均與最大逾期天數只計逾期筆()
    {
        var facts = new[]
        {
            F("S1", 2026, 3, 11, 2026, 3, 10),  // 逾期 1
            F("S1", 2026, 3, 15, 2026, 3, 10),  // 逾期 5
            F("S1", 2026, 3, 1,  2026, 3, 10),  // 準時（提前不抵銷逾期）
        };

        var (months, suppliers) = OtdAggregator.Compute(facts);

        Assert.Equal(3m, months[0].AvgOverdueDays);   // (1+5)/2
        Assert.Equal(5, months[0].MaxOverdueDays);
        Assert.Equal(3m, suppliers[0].AvgOverdueDays);
        Assert.Equal(5, suppliers[0].MaxOverdueDays);
    }

    [Fact]
    public void Compute_廠商延誤排名_逾期筆數多者在前()
    {
        var facts = new[]
        {
            F("GOOD", 2026, 1, 5, 2026, 1, 10),   // 準時
            F("BAD",  2026, 1, 12, 2026, 1, 10),  // 逾期
            F("BAD",  2026, 1, 20, 2026, 1, 10),  // 逾期
            F("MID",  2026, 1, 11, 2026, 1, 10),  // 逾期 1
        };

        var (_, suppliers) = OtdAggregator.Compute(facts);

        Assert.Equal("BAD", suppliers[0].SupplierCode);
        Assert.Equal("MID", suppliers[1].SupplierCode);
        Assert.Equal("GOOD", suppliers[2].SupplierCode);
        Assert.Equal(2, suppliers[0].LateLines);
        Assert.Equal(0m, suppliers[2].AvgOverdueDays);
    }

    [Fact]
    public void Compute_空事實回傳空集合()
    {
        var (months, suppliers) = OtdAggregator.Compute(Array.Empty<OtdFact>());
        Assert.Empty(months);
        Assert.Empty(suppliers);
    }
}

/// <summary>
/// Phase G 報表控制器測試：稽催表 IsClosed/逾期天數過濾/採購員、
/// 委外稽催 SplitQty 與收貨分攤、OTD 委外來源、採購金額本幣換算。
/// </summary>
public class PurReportPhaseGControllerTests : TestBase
{
    private readonly Mock<IUserContext>       _mockUser = new();
    private readonly Mock<IPermissionService> _mockPerm = new();
    private readonly Mock<IDateTimeService>   _mockDt   = new();

    private const string PlantId = "P001";
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    public PurReportPhaseGControllerTests()
    {
        _context.CurrentPlantId = PlantId;
        _mockUser.Setup(u => u.PlantId).Returns(PlantId);
        _mockPerm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanCreate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanPrint(It.IsAny<string>())).ReturnsAsync(true);
        _mockDt.Setup(d => d.Now).Returns(DateTime.Today);
        // GetLocalDate 必須和本檔種資料用的 Today 同一天。沒設定時 Moq 回 default(DateOnly)
        // ＝0001-01-01，逾期天數會全部變成負數而被門檻濾光——症狀是「查無資料」，
        // 看起來像查詢條件寫錯，其實是替身沒設。
        _mockDt.Setup(d => d.GetLocalDate(It.IsAny<string?>())).Returns(Today);
        _context.Companies.Add(new Modules.BAS.Models.Company
        {
            CompanyCode = "S1", CompanyName = "供應商一", IsSupplier = true, IsActive = true,
        });
        _context.SaveChanges();
    }

    // ── 採購稽催表 ───────────────────────────────────────────────

    private PurchaseOverdueController BuildOverdueController()
    {
        var ctrl = new PurchaseOverdueController(
            _context, _mockUser.Object, _mockPerm.Object,
            GetMockExportService().Object, _mockDt.Object,
            new SmartFactory.MES.Modules.PUR.Reporting.PurchaseOverdueTool(
                _context,
                new SmartFactory.MES.Modules.RPS.Services.RelativePeriodResolver(_mockDt.Object, _context),
                _mockDt.Object),
            GetMockLocalizer<ProjectZSharedResource>().Object,
            ReportHeaderLocalizer.Create());
        ctrl.ControllerContext = GetControllerContext();
        return ctrl;
    }

    private void SeedOverduePo()
    {
        _context.Users.Add(new ApplicationUser { Id = "U1", UserName = "buyer1" });
        var po = new PurchaseOrder
        {
            PurchaseOrderId = "PO1", SupplierCode = "S1", CreatedBy = "U1",
            OrderDate = Today.AddDays(-30), PlantId = PlantId,
            ApprovalStatus = DocumentApprovalStatus.Approved,
        };
        po.Lines.Add(new PurchaseOrderLine
        {
            PurchaseOrderId = "PO1", LineNo = 1, ProductCode = "PX",
            DeliveryDate = Today.AddDays(-10), OrderQty = 10, ReceivedQty = 5,
        });
        po.Lines.Add(new PurchaseOrderLine
        {
            PurchaseOrderId = "PO1", LineNo = 2, ProductCode = "PX",
            DeliveryDate = Today.AddDays(-10), OrderQty = 10, ReceivedQty = 5,
            IsClosed = true,   // 已結案：不再稽催
        });
        po.Lines.Add(new PurchaseOrderLine
        {
            PurchaseOrderId = "PO1", LineNo = 3, ProductCode = "PY",
            DeliveryDate = Today.AddDays(-3), OrderQty = 5, ReceivedQty = 0,
        });
        _context.PurchaseOrders.Add(po);
        _context.SaveChanges();
    }

    [Fact]
    public async Task 稽催表_排除已結案行_並帶採購員()
    {
        SeedOverduePo();
        var result = await BuildOverdueController().Index(null, null, null, null, searched: true);

        var rows = Assert.IsType<List<PurchaseOverdueRowVm>>(Assert.IsType<ViewResult>(result).Model);
        Assert.Equal(2, rows.Count);                          // 已結案 L2 不列
        Assert.DoesNotContain(rows, r => r.LineNo == 2);
        Assert.All(rows, r => Assert.Equal("buyer1", r.Buyer));
        Assert.Equal(10, rows[0].OverdueDays);                // 逾期天數大者在前
    }

    [Fact]
    public async Task 稽催表_依逾期天數過濾()
    {
        SeedOverduePo();
        var result = await BuildOverdueController().Index(null, null, null, null,
            minOverdueDays: 5, searched: true);

        var rows = Assert.IsType<List<PurchaseOverdueRowVm>>(Assert.IsType<ViewResult>(result).Model);
        var row = Assert.Single(rows);                        // 僅逾期 10 天的 L1
        Assert.Equal(1, row.LineNo);
    }

    // ── 委外稽催表（OutsourceProgress）──────────────────────────

    private OutsourceProgressController BuildProgressController()
    {
        var ctrl = new OutsourceProgressController(
            _context, _mockUser.Object, _mockPerm.Object,
            GetMockExportService().Object, _mockDt.Object,
            GetMockLocalizer<ProjectZSharedResource>().Object);
        ctrl.ControllerContext = GetControllerContext();
        return ctrl;
    }

    private void SeedOpoWithSplitLines()
    {
        var opo = new OutsourcePurchaseOrder
        {
            OutsourcePurchaseOrderId = "OPO1", SupplierCode = "S1", PlantId = PlantId,
            OrderDate = Today.AddDays(-20), ExpectedDeliveryDate = Today.AddDays(-4),
            Status = OutsourcePoStatus.Confirmed,
        };
        // 拆單：同品號兩行，SplitQty 6/4（Qty 皆為原始需求 10）
        opo.Lines.Add(new OutsourcePurchaseOrderLine
        {
            OutsourcePurchaseOrderId = "OPO1", LineNo = 1, ProductCode = "PX",
            Qty = 10, SplitQty = 6, SourceWorkOrderId = "WO1",
        });
        opo.Lines.Add(new OutsourcePurchaseOrderLine
        {
            OutsourcePurchaseOrderId = "OPO1", LineNo = 2, ProductCode = "PX",
            Qty = 10, SplitQty = 4, SourceWorkOrderId = "WO1",
        });
        _context.OutsourcePurchaseOrders.Add(opo);

        // 已確認（未過帳）收貨 6：應計入已驗收，且依 LineNo 順序先抵 L1
        _context.OutsourceReceipts.Add(new OutsourceReceipt
        {
            OutsourceReceiptId = "OR1", OutsourcePurchaseOrderId = "OPO1",
            SupplierCode = "S1", CreatedBy = "U1", PlantId = PlantId,
            ReceiptDate = Today.AddDays(-2), Status = DocStatus.Confirmed,
        });
        _context.OutsourceReceiptLines.Add(new OutsourceReceiptLine
        {
            OutsourceReceiptId = "OR1", LineNo = 1, OpoLineNo = 1,
            ProductCode = "PX", ReceiptQty = 6,
        });
        _context.SaveChanges();
    }

    [Fact]
    public async Task 委外稽催_拆單行用SplitQty_收貨依行序分攤_逾期天數()
    {
        SeedOpoWithSplitLines();
        var result = await BuildProgressController().Index(null, null, null, null, searched: true);

        var rows = Assert.IsType<List<OutsourceProgressRowVm>>(Assert.IsType<ViewResult>(result).Model);
        Assert.Equal(2, rows.Count);

        var l1 = rows.Single(r => r.LineNo == 1);
        Assert.Equal(6, l1.OrderedQty);      // SplitQty 而非 Qty=10
        Assert.Equal(6, l1.ReceivedQty);     // Confirmed 收貨計入
        Assert.False(l1.IsOverdue);          // 已收足不再稽催
        Assert.Equal(0, l1.OverdueDays);

        var l2 = rows.Single(r => r.LineNo == 2);
        Assert.Equal(4, l2.OrderedQty);
        Assert.Equal(0, l2.ReceivedQty);     // 收貨 6 已被 L1 吸收，不重複計
        Assert.True(l2.IsOverdue);
        Assert.Equal(4, l2.OverdueDays);
    }

    [Fact]
    public async Task 委外稽催_依逾期天數過濾()
    {
        SeedOpoWithSplitLines();

        var over5 = await BuildProgressController().Index(null, null, null, null,
            minOverdueDays: 5, searched: true);
        Assert.Empty(Assert.IsType<List<OutsourceProgressRowVm>>(
            Assert.IsType<ViewResult>(over5).Model));

        var over3 = await BuildProgressController().Index(null, null, null, null,
            minOverdueDays: 3, searched: true);
        var row = Assert.Single(Assert.IsType<List<OutsourceProgressRowVm>>(
            Assert.IsType<ViewResult>(over3).Model));
        Assert.Equal(2, row.LineNo);
    }

    // ── 交期達成率 OTD ───────────────────────────────────────────

    private SupplierDeliveryRateController BuildOtdController()
    {
        var ctrl = new SupplierDeliveryRateController(
            _context, _mockUser.Object, _mockPerm.Object,
            GetMockExportService().Object, _mockDt.Object,
            // 真實工具＋真實 RelativePeriodResolver：本檔驗的是 Controller→Tool→事實建構
            // 整條鏈的行為，換成 mock 等於把待測邏輯換掉。
            new SmartFactory.MES.Modules.PUR.Reporting.SupplierDeliveryRateTool(
                _context,
                new SmartFactory.MES.Modules.RPS.Services.RelativePeriodResolver(_mockDt.Object, _context),
                new OtdFactService(_context),
                _mockDt.Object),
            GetMockLocalizer<ProjectZSharedResource>().Object,
            GetMockLocalizer<ProjectZReportHeaderResource>().Object);
        ctrl.ControllerContext = GetControllerContext();
        return ctrl;
    }

    private void SeedOtdFacts()
    {
        // PO 來源：過帳進貨，預交日前 2 天逾期
        var po = new PurchaseOrder
        {
            PurchaseOrderId = "PO1", SupplierCode = "S1", CreatedBy = "U1",
            OrderDate = Today.AddDays(-40), PlantId = PlantId,
            ApprovalStatus = DocumentApprovalStatus.Approved,
        };
        po.Lines.Add(new PurchaseOrderLine
        {
            PurchaseOrderId = "PO1", LineNo = 1, ProductCode = "PX",
            DeliveryDate = Today.AddDays(-12), OrderQty = 10, ReceivedQty = 10,
        });
        _context.PurchaseOrders.Add(po);
        _context.GoodsReceipts.Add(new GoodsReceipt
        {
            GoodsReceiptId = "GR1", SupplierCode = "S1", CreatedBy = "U1",
            PlantId = PlantId, ReceiptDate = Today.AddDays(-10), Status = DocStatus.Posted,
        });
        _context.GoodsReceiptLines.Add(new GoodsReceiptLine
        {
            GoodsReceiptId = "GR1", LineNo = 1, PurchaseOrderId = "PO1", PoLineNo = 1,
            ProductCode = "PX", ReceiptQty = 10,
        });

        // 委外來源：Confirmed 收貨，準時
        _context.OutsourcePurchaseOrders.Add(new OutsourcePurchaseOrder
        {
            OutsourcePurchaseOrderId = "OPO1", SupplierCode = "S2", PlantId = PlantId,
            OrderDate = Today.AddDays(-20), ExpectedDeliveryDate = Today.AddDays(-1),
            Status = OutsourcePoStatus.Confirmed,
        });
        // 新口徑要比對「訂購量 vs 收貨量」才知道交清了沒，兩邊的明細行都得有。
        _context.OutsourcePurchaseOrderLines.Add(new OutsourcePurchaseOrderLine
        {
            OutsourcePurchaseOrderId = "OPO1", LineNo = 1, ProductCode = "PY", Qty = 5,
        });
        _context.OutsourceReceipts.Add(new OutsourceReceipt
        {
            OutsourceReceiptId = "OR1", OutsourcePurchaseOrderId = "OPO1",
            SupplierCode = "S2", CreatedBy = "U1", PlantId = PlantId,
            ReceiptDate = Today.AddDays(-3), Status = DocStatus.Confirmed,
        });
        _context.OutsourceReceiptLines.Add(new OutsourceReceiptLine
        {
            OutsourceReceiptId = "OR1", LineNo = 1, ProductCode = "PY", ReceiptQty = 5,
        });
        _context.SaveChanges();
    }

    [Fact]
    public async Task OTD_合併採購與委外來源_月份與廠商統計()
    {
        SeedOtdFacts();
        var result = await BuildOtdController().Index(null, null, null, searched: true);

        var vm = Assert.IsType<SupplierDeliveryRateVm>(Assert.IsType<ViewResult>(result).Model);
        Assert.Equal(2, vm.SupplierRows.Count);

        var s1 = vm.SupplierRows.Single(r => r.SupplierCode == "S1");
        Assert.Equal(1, s1.TotalLines);
        Assert.Equal(1, s1.LateLines);       // 晚 2 天
        Assert.Equal(2m, s1.AvgOverdueDays);
        Assert.Equal(2, s1.MaxOverdueDays);
        Assert.Equal("供應商一", s1.SupplierName);

        var s2 = vm.SupplierRows.Single(r => r.SupplierCode == "S2");
        Assert.Equal(100m, s2.OnTimeRate);   // 委外準時

        Assert.NotEmpty(vm.MonthRows);
        Assert.Equal(2, vm.MonthRows.Sum(m => m.TotalLines));
    }

    /// <summary>
    /// <b>本次口徑變更的核心案例</b>：已經過了預交日、貨還沒來的單要算遲交。
    ///
    /// <para>舊版母體只有已過帳的收貨，這張單完全不在分母裡——於是採購逾期跟催表列著它、
    /// 交期達成率卻是 100%。兩張表都沒算錯，是母體互斥。</para>
    /// </summary>
    [Fact]
    public async Task OTD_已逾期未交的單要算遲交而不是消失()
    {
        var po = new PurchaseOrder
        {
            PurchaseOrderId = "PO-LATE", SupplierCode = "S1", CreatedBy = "U1",
            OrderDate = Today.AddDays(-40), PlantId = PlantId,
            ApprovalStatus = DocumentApprovalStatus.Approved,
        };
        po.Lines.Add(new PurchaseOrderLine
        {
            PurchaseOrderId = "PO-LATE", LineNo = 1, ProductCode = "PX",
            DeliveryDate = Today.AddDays(-7), OrderQty = 10, ReceivedQty = 0,   // 一件都沒到
        });
        _context.PurchaseOrders.Add(po);
        _context.SaveChanges();

        var result = await BuildOtdController().Index(null, null, null, searched: true);
        var vm = Assert.IsType<SupplierDeliveryRateVm>(Assert.IsType<ViewResult>(result).Model);

        var s1 = vm.SupplierRows.Single(r => r.SupplierCode == "S1");
        Assert.Equal(1, s1.TotalLines);
        Assert.Equal(0, s1.OnTimeLines);
        Assert.Equal(1, s1.LateLines);
        Assert.Equal(0m, s1.OnTimeRate);     // 不再是「查無資料所以 100%」
        Assert.Equal(7, s1.MaxOverdueDays);  // 遲延以今天計，且會持續增加
    }

    [Fact]
    public async Task OTD_未到期的未交單不列入母體()
    {
        // 還沒到預交日就還沒有結果。列入會稀釋分母，讓達成率隨著「開了多少張未來的單」浮動。
        var po = new PurchaseOrder
        {
            PurchaseOrderId = "PO-FUTURE", SupplierCode = "S1", CreatedBy = "U1",
            OrderDate = Today, PlantId = PlantId,
            ApprovalStatus = DocumentApprovalStatus.Approved,
        };
        po.Lines.Add(new PurchaseOrderLine
        {
            PurchaseOrderId = "PO-FUTURE", LineNo = 1, ProductCode = "PX",
            DeliveryDate = Today.AddDays(10), OrderQty = 10, ReceivedQty = 0,
        });
        _context.PurchaseOrders.Add(po);
        _context.SaveChanges();

        var result = await BuildOtdController().Index(
            Today.ToString("yyyy-MM-dd"), Today.AddDays(30).ToString("yyyy-MM-dd"),
            null, searched: true);
        var vm = Assert.IsType<SupplierDeliveryRateVm>(Assert.IsType<ViewResult>(result).Model);

        Assert.Empty(vm.SupplierRows);
    }

    [Fact]
    public async Task OTD_分批收貨以收齊那一天判斷準時()
    {
        // 首批準時、尾批拖很久的話，只看第一次收貨會判成準時。
        var po = new PurchaseOrder
        {
            PurchaseOrderId = "PO-SPLIT", SupplierCode = "S1", CreatedBy = "U1",
            OrderDate = Today.AddDays(-40), PlantId = PlantId,
            ApprovalStatus = DocumentApprovalStatus.Approved,
        };
        po.Lines.Add(new PurchaseOrderLine
        {
            PurchaseOrderId = "PO-SPLIT", LineNo = 1, ProductCode = "PX",
            DeliveryDate = Today.AddDays(-20), OrderQty = 10, ReceivedQty = 10,
        });
        _context.PurchaseOrders.Add(po);

        foreach (var (id, day, qty) in new[]
                 { ("GR-A", -22, 4m), ("GR-B", -5, 6m) })   // 首批提前、尾批晚 15 天
        {
            _context.GoodsReceipts.Add(new GoodsReceipt
            {
                GoodsReceiptId = id, SupplierCode = "S1", CreatedBy = "U1",
                PlantId = PlantId, ReceiptDate = Today.AddDays(day), Status = DocStatus.Posted,
            });
            _context.GoodsReceiptLines.Add(new GoodsReceiptLine
            {
                GoodsReceiptId = id, LineNo = 1, PurchaseOrderId = "PO-SPLIT", PoLineNo = 1,
                ProductCode = "PX", ReceiptQty = qty,
            });
        }
        _context.SaveChanges();

        var result = await BuildOtdController().Index(null, null, null, searched: true);
        var vm = Assert.IsType<SupplierDeliveryRateVm>(Assert.IsType<ViewResult>(result).Model);

        var s1 = vm.SupplierRows.Single(r => r.SupplierCode == "S1");
        Assert.Equal(1, s1.LateLines);
        Assert.Equal(15, s1.MaxOverdueDays);
    }

    [Fact]
    public async Task OTD_來源過濾_只看採購()
    {
        SeedOtdFacts();
        var result = await BuildOtdController().Index(null, null, null, source: "PO", searched: true);

        var vm = Assert.IsType<SupplierDeliveryRateVm>(Assert.IsType<ViewResult>(result).Model);
        var row = Assert.Single(vm.SupplierRows);
        Assert.Equal("S1", row.SupplierCode);
    }

    // ── 採購金額（本幣換算）─────────────────────────────────────

    [Fact]
    public async Task 採購金額_多幣別以匯率換算本幣加總()
    {
        _context.GoodsReceipts.Add(new GoodsReceipt
        {
            GoodsReceiptId = "GR1", SupplierCode = "S1", CreatedBy = "U1",
            PlantId = PlantId, ReceiptDate = Today, Status = DocStatus.Posted,
            TotalAmount = 600,   // 表頭值刻意與明細不符，確認報表走明細而非表頭
        });
        _context.GoodsReceiptLines.Add(new GoodsReceiptLine
        {
            GoodsReceiptId = "GR1", LineNo = 1, ProductCode = "PX",
            // TotalAmount 為交易幣，AmountLocal 為存檔時鎖定的本幣（100 USD × 32）
            ReceiptQty = 1, CurrencyCode = "USD", ExchangeRate = 32, TotalAmount = 100, AmountLocal = 3200,
        });
        _context.GoodsReceiptLines.Add(new GoodsReceiptLine
        {
            GoodsReceiptId = "GR1", LineNo = 2, ProductCode = "PY",
            ReceiptQty = 1, CurrencyCode = "TWD", ExchangeRate = 1, TotalAmount = 500, AmountLocal = 500,
        });
        _context.SaveChanges();

        var ctrl = new PurchaseAmountController(
            _context, _mockUser.Object, _mockPerm.Object,
            GetMockExportService().Object, _mockDt.Object,
            new SmartFactory.MES.Modules.PUR.Reporting.PurchaseAmountTool(
                _context,
                new SmartFactory.MES.Modules.RPS.Services.RelativePeriodResolver(_mockDt.Object, _context),
                _mockDt.Object),
            GetMockLocalizer<ProjectZSharedResource>().Object,
            GetMockLocalizer<ProjectZReportHeaderResource>().Object);
        ctrl.ControllerContext = GetControllerContext();

        var result = await ctrl.Index(Today.Year, "Supplier", searched: true);
        var rows = Assert.IsType<List<PurchaseAmountRowVm>>(Assert.IsType<ViewResult>(result).Model);

        var row = Assert.Single(rows);
        Assert.Equal(3200m + 500m, row.TotalAmount);   // 3,700 本幣（加總明細 AmountLocal）
        Assert.Equal(1, row.ReceiptCount);                 // 同一張進貨單只算一筆
    }
}

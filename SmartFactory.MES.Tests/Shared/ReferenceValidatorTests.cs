using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared.ReferenceValidation;
using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.ORD.Models;
using SmartFactory.MES.Modules.PUR.Models;
using Xunit;

namespace SmartFactory.MES.Tests.Shared;

/// <summary>
/// 下拉參照欄位的伺服器端檢查（存在／本廠／仍可用）。規則登錄見 <see cref="ReferenceRules"/>，
/// 盤點見 .claude/reviews/reference-validation-inventory.md。
/// </summary>
public class ReferenceValidatorTests : TestBase
{
    private const string Plant = "P1";

    public ReferenceValidatorTests() => _context.CurrentPlantId = Plant;

    private Task<IReadOnlyList<ReferenceViolation>> Validate() => ReferenceValidator.ValidateAsync(_context);

    private void SeedProduct(string code, bool active = true) =>
        _context.Products.Add(new Product { ProductCode = code, ProductName = code, StockUom = "PCS", IsActive = active });

    private static WorkOrder NewWorkOrder(string id, string product) => new()
    {
        WorkOrderId = id, ProductionBatchId = "B1", ProductCode = product, RoutingId = "",
        Quantity = 10m, PlantId = Plant, Status = WorkOrderStatus.Planned,
    };

    private void SeedMachine(string wc, string machine, string plant, MachineStatus status = MachineStatus.Available, bool line = false)
    {
        _context.WorkCenters.Add(new WorkCenter { WorkCenterCode = wc, WorkCenterName = wc, PlantId = plant, IsProductionLine = line });
        if (machine != "")
            _context.WorkCenterMachines.Add(new WorkCenterMachine
            {
                WorkCenterCode = wc, MachineCode = machine, MachineName = machine, MachineStatus = status,
            });
    }

    private static Dispatch NewDispatch(string machine) => new()
    {
        DispatchId = "D1", WorkOrderId = "WO1", OperationSeq = 10, PlantId = Plant,
        Quantity = 10m, Status = DispatchStatus.Pending, MachineCode = machine,
    };

    // ── 存在／停用 ───────────────────────────────────────────────────────────

    [Fact]
    public async Task 新增的工單品號不存在_回報不存在()
    {
        _context.WorkOrders.Add(NewWorkOrder("WO1", "NO-SUCH"));

        var v = Assert.Single(await Validate());
        Assert.Equal((nameof(WorkOrder), nameof(WorkOrder.ProductCode), ReferenceProblem.NotFound),
                     (v.Entity, v.Property, v.Problem));
    }

    [Fact]
    public async Task 新增的工單品號已停用_回報停用()
    {
        SeedProduct("P-OLD", active: false);
        await _context.SaveChangesAsync();
        _context.WorkOrders.Add(NewWorkOrder("WO1", "P-OLD"));

        Assert.Equal(ReferenceProblem.Inactive, Assert.Single(await Validate()).Problem);
    }

    [Fact]
    public async Task 品號存在且啟用_通過()
    {
        SeedProduct("P-1");
        await _context.SaveChangesAsync();
        _context.WorkOrders.Add(NewWorkOrder("WO1", "P-1"));

        Assert.Empty(await Validate());
    }

    [Fact]
    public async Task 同一次存檔新增的品號_視為存在()
    {
        _context.Uoms.Add(new Uom { UomCode = "PCS", UomName = "PCS" });   // 品號本身的庫存單位也會被檢查
        SeedProduct("P-NEW");
        _context.WorkOrders.Add(NewWorkOrder("WO1", "P-NEW"));

        Assert.Empty(await Validate());
    }

    [Fact]
    public async Task 空值不在此檢查()
    {
        _context.WorkOrders.Add(NewWorkOrder("WO1", ""));
        Assert.Empty(await Validate());
    }

    // ── 只驗有變更的值 ─────────────────────────────────────────────────────

    [Fact]
    public async Task 舊單據引用的品號後來停用_修改其他欄位不受影響()
    {
        SeedProduct("P-1");
        _context.WorkOrders.Add(NewWorkOrder("WO1", "P-1"));
        await _context.SaveChangesAsync();
        _context.Products.Single().IsActive = false;
        await _context.SaveChangesAsync();

        _context.WorkOrders.Single().Quantity = 20m;

        Assert.Empty(await Validate());
    }

    [Fact]
    public async Task 修改時把品號改成停用的_擋下()
    {
        SeedProduct("P-1");
        SeedProduct("P-OLD", active: false);
        _context.WorkOrders.Add(NewWorkOrder("WO1", "P-1"));
        await _context.SaveChangesAsync();

        _context.WorkOrders.Single().ProductCode = "P-OLD";

        Assert.Equal(ReferenceProblem.Inactive, Assert.Single(await Validate()).Problem);
    }

    [Fact]
    public async Task 整列標成已修改但值沒變_不驗()
    {
        // Identity 式的 Update 會把整列標為 Modified；只看 IsModified 會誤驗
        SeedProduct("P-1");
        _context.WorkOrders.Add(NewWorkOrder("WO1", "P-1"));
        await _context.SaveChangesAsync();
        _context.Products.Single().IsActive = false;
        await _context.SaveChangesAsync();

        _context.Update(_context.WorkOrders.Single());

        Assert.Empty(await Validate());
    }

    // ── 廠別 ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task 派工掛別廠的機台_回報不屬於本廠()
    {
        SeedMachine("WC-P2", "M-P2", plant: "P2");
        await _context.SaveChangesAsync();
        _context.Dispatches.Add(NewDispatch("M-P2"));

        Assert.Equal(ReferenceProblem.OtherPlant, Assert.Single(await Validate()).Problem);
    }

    [Fact]
    public async Task 同代碼機台在本廠也有_以本廠那台為準()
    {
        SeedMachine("WC-P2", "M1", plant: "P2");
        SeedMachine("WC-P1", "M1", plant: Plant);
        await _context.SaveChangesAsync();
        _context.Dispatches.Add(NewDispatch("M1"));

        Assert.Empty(await Validate());
    }

    [Fact]
    public async Task 派工掛已廢棄機台_回報停用()
    {
        SeedMachine("WC1", "M-RET", plant: Plant, status: MachineStatus.Retired);
        await _context.SaveChangesAsync();
        _context.Dispatches.Add(NewDispatch("M-RET"));

        Assert.Equal(ReferenceProblem.Inactive, Assert.Single(await Validate()).Problem);
    }

    [Fact]
    public async Task 派工掛未登錄線別的生產線_以工作中心代碼通過()
    {
        SeedMachine("LINE1", "", plant: Plant, line: true);
        await _context.SaveChangesAsync();
        _context.Dispatches.Add(NewDispatch("LINE1"));

        Assert.Empty(await Validate());
    }

    [Fact]
    public async Task 對廢棄機台開狀態單_擋下_避免把廢棄機台改回可用()
    {
        SeedMachine("WC1", "M-RET", plant: Plant, status: MachineStatus.Retired);
        await _context.SaveChangesAsync();
        _context.MachineStatusOrderLines.Add(new MachineStatusOrderLine
        {
            OrderId = "MSO1", LineSeq = 1, MachineCode = "M-RET", WorkCenterCode = "WC1",
            TargetStatus = MachineStatus.Available,
        });

        var v = Assert.Single(await Validate());
        Assert.Equal((ReferenceMaster.Machine, ReferenceProblem.Inactive), (v.Master, v.Problem));
    }

    // ── 附加規則 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task 不良原因要在ng_reason類別裡_別類別的同代碼不算()
    {
        _context.CodeEntries.Add(new CodeEntry { Category = "scrap_reason", Code = "R01", NameZh = "R01" });
        await _context.SaveChangesAsync();
        _context.ProductionReports.Add(new ProductionReport
        {
            ReportId = "RP1", SessionId = "S1", DispatchId = "D1", WorkOrderId = "WO1", PlantId = Plant, NgReasonCode = "R01",
        });

        Assert.Equal(ReferenceProblem.NotFound, Assert.Single(await Validate()).Problem);
    }

    [Theory]
    [InlineData("WH1", true)]
    [InlineData("WH2", false)]
    public async Task 儲位必須屬於同一列上的倉庫(string toWarehouse, bool ok)
    {
        SeedProduct("P-1");
        _context.Uoms.Add(new Uom { UomCode = "PCS", UomName = "PCS" });
        _context.Warehouses.Add(new Warehouse { WhCode = "WH1", WhName = "WH1", PlantId = Plant });
        _context.Warehouses.Add(new Warehouse { WhCode = "WH2", WhName = "WH2", PlantId = Plant });
        _context.StorageLocations.Add(new StorageLocation { WhCode = "WH1", LocationCode = "L1", LocationName = "L1" });
        await _context.SaveChangesAsync();

        _context.MaterialSlipLines.Add(new MaterialSlipLine
        {
            SlipId = "S1", LineNo = 1, ProductCode = "P-1", UomCode = "PCS",
            ToWarehouseCode = toWarehouse, ToLocationCode = "L1",
        });

        var violations = await Validate();
        if (ok) Assert.Empty(violations);
        else Assert.Equal((ReferenceMaster.StorageLocation, "WH2/L1"),
                          (Assert.Single(violations).Master, violations[0].Value));
    }

    [Fact]
    public async Task 部門實際成本的工作中心停用了_月結仍可寫入_但別廠的仍擋()
    {
        _context.WorkCenters.Add(new WorkCenter { WorkCenterCode = "WC-OFF", WorkCenterName = "x", PlantId = Plant, IsActive = false });
        _context.WorkCenters.Add(new WorkCenter { WorkCenterCode = "WC-P2", WorkCenterName = "y", PlantId = "P2" });
        _context.AccountingPeriods.Add(new AccountingPeriod { PeriodId = "202610", PlantId = Plant });
        await _context.SaveChangesAsync();

        _context.DepartmentActualCosts.Add(new DepartmentActualCost { PeriodId = "202610", WorkCenterCode = "WC-OFF", PlantId = Plant });
        Assert.Empty(await Validate());

        _context.DepartmentActualCosts.Add(new DepartmentActualCost { PeriodId = "202610", WorkCenterCode = "WC-P2", PlantId = Plant });
        Assert.Equal(ReferenceProblem.OtherPlant, Assert.Single(await Validate()).Problem);
    }

    // ── 第 2 批：出入庫的儲位、部門廠別繼承 ─────────────────────────────────

    private async Task SeedInactiveLocationAsync()
    {
        _context.Warehouses.Add(new Warehouse { WhCode = "WH1", WhName = "WH1", PlantId = Plant });
        _context.StorageLocations.Add(new StorageLocation { WhCode = "WH1", LocationCode = "OLD", LocationName = "OLD", IsActive = false });
        await _context.SaveChangesAsync();
    }

    [Fact]
    public async Task 出貨從已停用的儲位出清剩餘庫存_允許()
    {
        await SeedInactiveLocationAsync();
        _context.GoodsShipmentLocationDetails.Add(new GoodsShipmentLocationDetail
        {
            GoodsShipmentId = "GS1", LineNo = 1, WhCode = "WH1", LocationCode = "OLD",
        });

        Assert.Empty(await Validate());
    }

    [Fact]
    public async Task 收料進已停用的儲位_擋下()
    {
        await SeedInactiveLocationAsync();
        _context.GoodsReceiptLocationDetails.Add(new GoodsReceiptLocationDetail
        {
            GoodsReceiptId = "GR1", LineNo = 1, WhCode = "WH1", LocationCode = "OLD",
        });

        var v = Assert.Single(await Validate());
        Assert.Equal((ReferenceMaster.StorageLocation, ReferenceProblem.Inactive), (v.Master, v.Problem));
    }

    [Theory]
    [InlineData("UNIT-P2-CHILD", ReferenceProblem.OtherPlant)]   // 自己沒填廠別，上層是 P2
    [InlineData("UNIT-SHARED", null)]                             // 一路往上都沒有廠別＝全公司共用
    [InlineData("UNIT-P1", null)]
    public async Task 部門的廠別沿上層繼承(string deptId, ReferenceProblem? expected)
    {
        // 存檔會把空廠別自動填成目前廠別（ApplyGlobalValues）；廠別為空的單位是舊資料，建資料時先清空情境模擬
        _context.CurrentPlantId = null;
        _context.OrganizationUnits.AddRange(
            new OrganizationUnit { Id = "UNIT-P2", Name = "P2", PlantId = "P2" },
            new OrganizationUnit { Id = "UNIT-P2-CHILD", Name = "P2 child", ParentId = "UNIT-P2" },
            new OrganizationUnit { Id = "UNIT-SHARED", Name = "shared" },
            new OrganizationUnit { Id = "UNIT-P1", Name = "P1", PlantId = Plant });
        await _context.SaveChangesAsync();
        _context.CurrentPlantId = Plant;

        _context.InternalOrders.Add(new InternalOrder { InternalOrderId = "NO1", PlantId = Plant, DepartmentCode = deptId });

        var violations = await Validate();
        if (expected == null) Assert.Empty(violations);
        else Assert.Equal(expected, Assert.Single(violations).Problem);
    }

    // ── 第 3 批：跨廠調撥 ─────────────────────────────────────────────────

    [Theory]
    [InlineData("WH-P1", "WH-P2", "P2", null)]                         // 正常：本廠出、目的廠收
    [InlineData("WH-P2", "WH-P2", "P2", ReferenceProblem.OtherPlant)]  // 來源倉庫是別廠的
    [InlineData("WH-P1", "WH-P2", "P9", ReferenceProblem.NotFound)]    // 目的廠不存在
    public async Task 跨廠調撥_來源比本廠_目的倉庫屬目的廠不比本廠(
        string from, string to, string toPlant, ReferenceProblem? expected)
    {
        _context.CurrentPlantId = null;   // 建資料時不讓 ApplyGlobalValues 把倉庫的廠別蓋掉
        _context.Plants.AddRange(new Plant { Id = Plant, Name = Plant }, new Plant { Id = "P2", Name = "P2" });
        _context.Warehouses.Add(new Warehouse { WhCode = "WH-P1", WhName = "1", PlantId = Plant });
        _context.Warehouses.Add(new Warehouse { WhCode = "WH-P2", WhName = "2", PlantId = "P2" });
        await _context.SaveChangesAsync();
        _context.CurrentPlantId = Plant;

        _context.CrossPlantTransfers.Add(new CrossPlantTransfer
        {
            CrossPlantTransferId = "CPT1", PlantId = Plant, FromWhCode = from, ToWhCode = to, ToPlantId = toPlant,
        });

        var violations = await Validate();
        if (expected == null) Assert.Empty(violations);
        else Assert.Equal(expected, Assert.Single(violations).Problem);
    }

    // ── 第 4 批：分錄科目只驗存在 ─────────────────────────────────────────

    [Theory]
    [InlineData("6100", null)]                         // 科目已停用：自動過帳仍要能寫入
    [InlineData("9999", ReferenceProblem.NotFound)]    // 科目不存在：擋下
    public async Task 分錄科目只驗存在_停用科目不擋自動過帳(string account, ReferenceProblem? expected)
    {
        _context.ChartOfAccounts.Add(new ChartOfAccount { AccountCode = "6100", AccountName = "x", IsActive = false });
        await _context.SaveChangesAsync();

        _context.JournalLines.Add(new JournalLine { JournalId = "J1", LineNo = 1, AccountCode = account, AccountName = "x" });

        var violations = await Validate();
        if (expected == null) Assert.Empty(violations);
        else Assert.Equal(expected, Assert.Single(violations).Problem);
    }

    // ── 例外與訊息 ───────────────────────────────────────────────────────────

    [Fact]
    public async Task EnsureValid_不通過時丟例外_訊息寫出主檔與值()
    {
        _context.WorkOrders.Add(NewWorkOrder("WO1", "NO-SUCH"));

        var ex = await Assert.ThrowsAsync<ReferenceValidationException>(() => ReferenceValidator.EnsureValidAsync(_context));

        Assert.Single(ex.Violations);
        Assert.Contains("NO-SUCH", ex.Message);
        Assert.DoesNotContain("RefVal_", ex.Message);    // 資源鍵都查得到，沒有原樣漏出
        Assert.DoesNotContain("RefMaster_", ex.Message);
    }

    [Fact]
    public void 每種主檔都有中文名稱_新增主檔種類時不會漏補()
    {
        var saved = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("zh-TW");
            foreach (var m in Enum.GetValues<ReferenceMaster>())
            {
                var text = ReferenceValidator.Describe(new ReferenceViolation("E", "P", m, "X", ReferenceProblem.NotFound));
                Assert.DoesNotContain(m.ToString(), text);
            }
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = saved; }
    }

    // ── 既有資料盤點（AuditAsync）────────────────────────────────────────────

    [Fact]
    public async Task 盤點_既有資料的不存在與他廠值逐值回報列數_停用排最後()
    {
        SeedProduct("P-1");
        SeedProduct("P-OLD", active: false);
        SeedMachine("WC2", "M-P2", "P2");
        _context.WorkOrders.Add(NewWorkOrder("WO1", "NO-SUCH"));
        _context.WorkOrders.Add(NewWorkOrder("WO2", "NO-SUCH"));
        _context.WorkOrders.Add(NewWorkOrder("WO3", "P-OLD"));
        _context.WorkOrders.Add(NewWorkOrder("WO4", "P-1"));
        _context.Dispatches.Add(NewDispatch("M-P2"));
        await _context.SaveChangesAsync();   // 單元測試的 DbContext 沒掛攔截器：模擬啟用前就存在的資料

        var findings = await ReferenceValidator.AuditAsync(_context);

        var missing = Assert.Single(findings, f => f.Violation.Value == "NO-SUCH");
        Assert.Equal((ReferenceProblem.NotFound, 2, Plant), (missing.Violation.Problem, missing.Count, missing.Plant));
        Assert.Equal(ReferenceProblem.OtherPlant,
            Assert.Single(findings, f => f.Violation.Property == nameof(Dispatch.MachineCode)).Violation.Problem);
        Assert.Equal(ReferenceProblem.Inactive, findings[^1].Violation.Problem);
        Assert.Equal("P-OLD", findings[^1].Violation.Value);
        Assert.DoesNotContain(findings, f => f.Violation.Value == "P-1");
    }

    [Fact]
    public async Task 盤點_資料都合格_沒有結果()
    {
        _context.Uoms.Add(new Uom { UomCode = "PCS", UomName = "PCS" });   // 盤點連品號主檔自己的庫存單位也查
        SeedProduct("P-1");
        _context.WorkOrders.Add(NewWorkOrder("WO1", "P-1"));
        await _context.SaveChangesAsync();

        Assert.Empty(await ReferenceValidator.AuditAsync(_context));
    }
}

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.PUR.Controllers;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.PUR.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.PUR;

/// <summary>
/// OutsourceReceiptController.Save 新增守門——聚焦「伺服端採番欄位的隱含必填縫」。
/// </summary>
public class OutsourceReceiptControllerTests : TestBase
{
    private const string PlantId = "P001";
    private const string Opo     = "OP-2607-00001";

    private readonly OutsourceReceiptController _controller;
    private readonly Mock<IPlantParamService>      _mockSysParam = new();
    private readonly Mock<IInventoryPostingService> _mockPosting  = new();
    private readonly Mock<IAPInvoiceService>        _mockApInvoice = new();
    private readonly Mock<IApprovalService>         _mockApproval  = new();
    private readonly Mock<IUomService>              _mockUom       = new();

    public OutsourceReceiptControllerTests()
    {
        _mockUom.WithStockConversion(_context);   // 存檔改由 ToStockQtyAsync 換算庫存數量（2026-10-12）
        _context.CurrentPlantId = PlantId;
        _context.CurrentRoleId  = "1";   // 對齊種子 OPO 的 CreatedByRoleId，使資料權限過濾看得到

        var mockUser = new Mock<IUserContext>();
        mockUser.Setup(u => u.PlantId).Returns(PlantId);
        mockUser.Setup(u => u.UserId).Returns("user1");

        var mockPerm = new Mock<IPermissionService>();
        mockPerm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        mockPerm.Setup(p => p.CanCreate(It.IsAny<string>())).ReturnsAsync(true);
        mockPerm.Setup(p => p.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);

        var mockDocNo = new Mock<IDocNumberService>();
        mockDocNo.Setup(d => d.NextAsync("OutsourceReceipt", It.IsAny<string>(),
                It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("OR-2607-00001");

        // 未設定的 Mock 對 Task<Dictionary> 會回 null，Confirm 取值時就 NRE；
        // 本測試不驗成本結算，回空字典即可
        // （在外原料與超耗的口徑見 Flows/OutsourceSplitReceiptFlowTests）。
        var mockSettlement = new Mock<IOutsourceReceiptSettlementService>();
        mockSettlement.Setup(m => m.SettleAsync(
                It.IsAny<OutsourceReceipt>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, OutsourceLineSettlement>());

        // 乾淨部署上 DocumentApprovalConfigs 沒有 OutsourceReceipt，GetApprovalConfig 回 null；
        // 這正是 W2-F10 的前提，故明確設成 null 而不是讓 Moq 回一個假設定。
        _mockApproval.Setup(a => a.GetApprovalConfig(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((DocumentApprovalConfig?)null);

        _controller = new OutsourceReceiptController(
            _context, mockUser.Object, mockPerm.Object, new Mock<IDateTimeService>().Object,
            _mockApproval.Object, mockDocNo.Object,
            _mockPosting.Object, _mockSysParam.Object,
            _mockUom.Object, GetMockCurrencyService().Object,
            _mockApInvoice.Object, new Mock<IExportService>().Object,
            new Mock<IInventoryReservationService>().Object,
            mockSettlement.Object,
            new Mock<SmartFactory.MES.Modules.ACT.Services.IJournalEntryService>().Object,
            GetMockLocalizer<ProjectZSharedResource>().Object);

        // 來源委外採購單（Save 對新單會驗其存在且同廠）
        _context.OutsourcePurchaseOrders.Add(new OutsourcePurchaseOrder
        {
            OutsourcePurchaseOrderId = Opo, SupplierCode = "SUP001",
            OrderDate = new DateOnly(2026, 7, 1), Status = OutsourcePoStatus.Confirmed,
            PlantId = PlantId, CreatedByRoleId = "1",
        });
        _context.SaveChanges();
    }

    [Fact]
    public async Task Save_New_WithImplicitRequiredPkAndCreatedByModelError_IsClearedAndSaves()
    {
        // 迴歸守門：HTTP 模型繫結會為「空的非可空字串」欄位（OutsourceReceiptId 由伺服端採番、
        // CreatedBy 由伺服端指派）加上隱含 [Required] 錯誤；Save 若未先 ModelState.Remove 兩者，
        // ModelState 恆 invalid → 面板靜默重渲染建不出單（此縫由 DocActionSmoke 實機揪出）。
        var vm = new OutsourceReceipt
        {
            OutsourcePurchaseOrderId = Opo, SupplierCode = "SUP001",
            ReceiptDate = new DateOnly(2026, 7, 10), ExchangeRate = 1m,
        };
        _controller.ModelState.AddModelError(nameof(OutsourceReceipt.OutsourceReceiptId), "為必填欄位。");
        _controller.ModelState.AddModelError(nameof(OutsourceReceipt.CreatedBy), "為必填欄位。");

        var result = await _controller.Save(vm);

        Assert.IsType<ContentResult>(result);   // 成功回 toast script（非 _Form partial）
        Assert.Single(_context.OutsourceReceipts.ToList());
    }

    [Fact]
    public async Task Save_New_WithImplicitRequiredLineParentFkModelError_IsClearedAndSaves()
    {
        // 迴歸守門（第二層縫）：前端 addLineRow 不渲染 Lines[i].OutsourceReceiptId（明細 parent FK、
        // 非可空字串、伺服端才賦值），binder 仍會對每個明細列的空值加隱含 [Required] 錯誤。
        // 表頭移除後若未逐列移除此驗證，帶明細的新單仍會 ModelState invalid 靜默重繪建不出單
        // （此更深一層由實機 DocActionSmoke 揪出——表頭修好後仍紅）。
        var vm = new OutsourceReceipt
        {
            OutsourcePurchaseOrderId = Opo, SupplierCode = "SUP001",
            ReceiptDate = new DateOnly(2026, 7, 10), ExchangeRate = 1m,
            Lines = new List<OutsourceReceiptLine>
            {
                new() { LineNo = 1, ProductCode = "P-1", ReceiptQty = 5m, UnitCost = 3m },
            },
        };
        _controller.ModelState.AddModelError(nameof(OutsourceReceipt.OutsourceReceiptId), "為必填欄位。");
        _controller.ModelState.AddModelError(nameof(OutsourceReceipt.CreatedBy), "為必填欄位。");
        _controller.ModelState.AddModelError(
            $"Lines[0].{nameof(OutsourceReceiptLine.OutsourceReceiptId)}", "為必填欄位。");

        var result = await _controller.Save(vm);

        Assert.IsType<ContentResult>(result);
        var saved = Assert.Single(_context.OutsourceReceipts.Include(r => r.Lines).ToList());
        Assert.Single(saved.Lines);
    }

    [Fact]
    public async Task Save_Edit_KeepsIsFinalReceipt()
    {
        // 「本單為最後一批」是短交結案唯一的訊號——沒有它，超耗扣款與在外原料的掃尾
        // 都不會發生（見 IOutsourceReceiptSettlementService）。編輯路徑是逐欄位手抄到
        // existing 的，漏抄一欄不會有任何錯誤：勾選存檔後再改一次備註，勾就無聲地掉了，
        // 而後果要到收貨確認、甚至月結對帳時才看得見。
        _context.OutsourceReceipts.Add(new OutsourceReceipt
        {
            OutsourceReceiptId = "OR-2607-00009", OutsourcePurchaseOrderId = Opo,
            SupplierCode = "SUP001", ReceiptDate = new DateOnly(2026, 7, 10),
            CreatedBy = "user1", ExchangeRate = 1m, PlantId = PlantId, CreatedByRoleId = "1",
            Status = DocStatus.Draft, IsFinalReceipt = true,
        });
        _context.SaveChanges();

        var vm = new OutsourceReceipt
        {
            OutsourceReceiptId = "OR-2607-00009", OutsourcePurchaseOrderId = Opo,
            SupplierCode = "SUP001", ReceiptDate = new DateOnly(2026, 7, 10),
            ExchangeRate = 1m, Remarks = "改個備註", IsFinalReceipt = true,
            Lines = new List<OutsourceReceiptLine>
            {
                new() { LineNo = 1, ProductCode = "P-1", ReceiptQty = 5m, UnitCost = 3m },
            },
        };

        var result = await _controller.Save(vm);

        Assert.IsType<ContentResult>(result);
        var saved = _context.OutsourceReceipts.Single(r => r.OutsourceReceiptId == "OR-2607-00009");
        Assert.True(saved.IsFinalReceipt);
    }

    // ── Confirm：庫存簡化模式（inv_simplified_mode）分支 ────────────────────

    private void SeedApprovedDraft(string id = "OR-2607-00001", decimal stockQty = 6m)
    {
        _context.OutsourceReceipts.Add(new OutsourceReceipt
        {
            OutsourceReceiptId = id, OutsourcePurchaseOrderId = Opo, SupplierCode = "SUP001",
            ReceiptDate = new DateOnly(2026, 7, 10), CreatedBy = "user1", ExchangeRate = 1m,
            PlantId = PlantId, CreatedByRoleId = "1",
            Status = DocStatus.Draft, ApprovalStatus = DocumentApprovalStatus.Approved,
            Lines = new List<OutsourceReceiptLine>
            {
                new()
                {
                    OutsourceReceiptId = id, LineNo = 1, ProductCode = "P-1",
                    ReceiptQty = stockQty, StockQty = stockQty, UomCode = "PC", StockUomCode = "PC",
                    UnitCost = 3m, WhCode = "WH01", LocationCode = "L-01",
                },
            },
        });
        _context.SaveChanges();
    }

    [Fact]
    public async Task Confirm_SimplifiedMode_ShouldPostImmediatelyWithStockQty()
    {
        SeedApprovedDraft(stockQty: 6m);
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("true");
        PostDocSimplifiedRequest? captured = null;
        _mockPosting.Setup(p => p.PostDocSimplifiedAsync(It.IsAny<PostDocSimplifiedRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PostDocSimplifiedRequest, CancellationToken>((r, _) => captured = r)
            .Returns(Task.CompletedTask);

        var result = Assert.IsType<JsonResult>(await _controller.Confirm("OR-2607-00001"));

        Assert.True((bool)result.Value!.GetType().GetProperty("success")!.GetValue(result.Value)!);
        _mockPosting.Verify(p => p.PostDocSimplifiedAsync(It.IsAny<PostDocSimplifiedRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotNull(captured);
        var line = Assert.Single(captured!.Lines);
        Assert.Equal("P-1", line.ProductCode);
        Assert.Equal(6m, line.Qty);
        Assert.Equal(3m, line.UnitPrice);
        // 簡化模式下仍須產生應付發票（過帳與發票同一交易）
        _mockApInvoice.Verify(s => s.GenerateFromOutsourceReceiptAsync("OR-2607-00001", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Confirm_FullMode_ShouldOnlyMarkConfirmedWithoutPosting()
    {
        SeedApprovedDraft();
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("false");

        Assert.IsType<JsonResult>(await _controller.Confirm("OR-2607-00001"));

        _mockPosting.Verify(p => p.PostDocSimplifiedAsync(It.IsAny<PostDocSimplifiedRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(DocStatus.Confirmed, _context.OutsourceReceipts.Single().Status);
        _mockApInvoice.Verify(s => s.GenerateFromOutsourceReceiptAsync("OR-2607-00001", It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>參數從未設定（乾淨部署，GetAsync 回 null）時視同完整模式，不得直接過帳。</summary>
    [Fact]
    public async Task Confirm_ParamNeverSet_ShouldBehaveAsFullMode()
    {
        SeedApprovedDraft();
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

        await _controller.Confirm("OR-2607-00001");

        _mockPosting.Verify(p => p.PostDocSimplifiedAsync(It.IsAny<PostDocSimplifiedRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(DocStatus.Confirmed, _context.OutsourceReceipts.Single().Status);
    }

    // ── Void：Confirm 當下已產生的應付發票不得留成孤兒 ──────────────────────

    private void SeedApInvoiceFor(string receiptId, APInvoiceStatus status = APInvoiceStatus.Open)
        => _context.APInvoices.Add(new APInvoice
        {
            APInvoiceId = "API-0001", SupplierCode = "SUP001", PlantId = PlantId,
            InvoiceDate = new DateOnly(2026, 7, 10), DueDate = new DateOnly(2026, 8, 9),
            ExchangeRate = 1m, TotalAmount = 18m, AmountLocal = 18m,
            SourceDocType = "OutsourceReceipt", SourceDocId = receiptId, Status = status,
        });

    /// <summary>
    /// 作廢已確認的收貨：Confirm 已自動開出應付發票，若只改收貨單狀態，
    /// 那張發票會掛在作廢單上變成孤兒——供應商對帳多一筆、AP 未清餘額永遠掛帳。
    /// </summary>
    [Fact]
    public async Task Void_ConfirmedReceipt_AlsoVoidsGeneratedApInvoice()
    {
        SeedApprovedDraft();
        _context.OutsourceReceipts.Single().Status = DocStatus.Confirmed;
        SeedApInvoiceFor("OR-2607-00001");
        _context.SaveChanges();

        var result = Assert.IsType<JsonResult>(await _controller.Void("OR-2607-00001"));

        Assert.True((bool)result.Value!.GetType().GetProperty("success")!.GetValue(result.Value)!);
        Assert.Equal(DocStatus.Void, _context.OutsourceReceipts.Single().Status);
        _mockApInvoice.Verify(s => s.VoidAsync("API-0001", It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>已付款的發票不能作廢，此時整個作廢動作必須擋下，不能只作廢收貨單。</summary>
    [Fact]
    public async Task Void_WhenGeneratedApInvoiceAlreadyPaid_IsBlocked()
    {
        SeedApprovedDraft();
        _context.OutsourceReceipts.Single().Status = DocStatus.Confirmed;
        SeedApInvoiceFor("OR-2607-00001", APInvoiceStatus.Paid);
        _context.SaveChanges();

        var result = Assert.IsType<JsonResult>(await _controller.Void("OR-2607-00001"));

        Assert.False((bool)result.Value!.GetType().GetProperty("success")!.GetValue(result.Value)!);
        Assert.Equal(DocStatus.Confirmed, _context.OutsourceReceipts.Single().Status);   // 未被作廢
        _mockApInvoice.Verify(s => s.VoidAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>草稿作廢（尚未 Confirm、沒有發票）維持原行為，不得誤呼叫發票作廢。</summary>
    [Fact]
    public async Task Void_DraftWithoutInvoice_VoidsWithoutTouchingAp()
    {
        SeedApprovedDraft();

        var result = Assert.IsType<JsonResult>(await _controller.Void("OR-2607-00001"));

        Assert.True((bool)result.Value!.GetType().GetProperty("success")!.GetValue(result.Value)!);
        Assert.Equal(DocStatus.Void, _context.OutsourceReceipts.Single().Status);
        _mockApInvoice.Verify(s => s.VoidAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── W2-F10：送審在沒有簽核設定時不得炸 500 ────────────────────────────

    /// <summary>
    /// 沒有設定簽核流程時，送審應直接核准，而不是 NullReferenceException。
    ///
    /// <c>GetApprovalConfig</c> 對未設定的單別回 null，本控制器自己抄了一份送審流程
    /// 卻漏了 <c>ModuleControllerBase.SubmitForApprovalAsync</c> 早就有的 null 判斷，
    /// 於是 <c>config.ApprovalTemplateId</c> 直接擲空參考 → HTTP 500，
    /// 前端 <c>postAction</c> 的 <c>r.json().catch()</c> 把它吞成一句「操作失敗。」。
    /// 而收貨確認的前提是 ApprovalStatus=Approved，整條委外流程就此走不下去
    /// （乾淨部署必然踩到：DocumentApprovalConfigs 根本沒有這個單別）。
    /// </summary>
    [Fact]
    public async Task Submit_WithNoApprovalConfig_ApprovesInsteadOfThrowing()
    {
        _context.OutsourceReceipts.Add(new OutsourceReceipt
        {
            OutsourceReceiptId = "OR-2607-00020", OutsourcePurchaseOrderId = Opo,
            SupplierCode = "SUP001", ReceiptDate = new DateOnly(2026, 7, 10),
            CreatedBy = "user1", ExchangeRate = 1m, PlantId = PlantId, CreatedByRoleId = "1",
            Status = DocStatus.Draft, ApprovalStatus = DocumentApprovalStatus.Draft,
            Lines = { new OutsourceReceiptLine { LineNo = 1, ProductCode = "P-1", ReceiptQty = 5m, UnitCost = 3m } },
        });
        _context.SaveChanges();
        _context.ChangeTracker.Clear();

        var result = await _controller.Submit("OR-2607-00020");

        var data = Assert.IsType<JsonResult>(result).Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);

        _context.ChangeTracker.Clear();
        var saved = _context.OutsourceReceipts.Single(r => r.OutsourceReceiptId == "OR-2607-00020");
        Assert.Equal(DocumentApprovalStatus.Approved, saved.ApprovalStatus);
        _mockApproval.Verify(a => a.SubmitForApproval(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    // ── W2-F11：明細的 OpoLineNo 與 StockQty 由伺服端補齊 ──────────────────

    /// <summary>
    /// 收貨明細存檔後必須帶著 <c>OpoLineNo</c> 與 <c>StockQty</c>。
    ///
    /// <c>OutsourceReceiptSettlementService.SettleAsync</c> 第一行就是
    /// <c>Where(l =&gt; l.OpoLineNo.HasValue &amp;&amp; l.StockQty &gt; 0)</c>——
    /// 缺任何一個，在外原料不會併回成品、超耗扣款永遠算成 0，而且不會有錯誤訊息；
    /// 簡化模式下更會拿 StockQty=0 去過帳，成品一個都進不了庫（W2-F11）。
    /// 表單的 addLineRow 從來沒有 render 這兩欄，所以只能由伺服端導出。
    /// </summary>
    [Fact]
    public async Task Save_New_DerivesOpoLineNoAndStockQty_ForSettlement()
    {
        _context.OutsourcePurchaseOrderLines.Add(new OutsourcePurchaseOrderLine
        {
            OutsourcePurchaseOrderId = Opo, LineNo = 7, SourceWorkOrderId = "WO-1",
            OperationSeq = 1, ProductCode = "P-1", Qty = 20m, UomCode = "PCS", UnitPrice = 25m,
        });
        _context.Products.Add(new SmartFactory.MES.Modules.BAS.Models.Product
        {
            ProductCode = "P-1", ProductName = "委外成品", StockUom = "PCS",
            PurchaseUom = "PCS", SalesUom = "PCS", IsActive = true,
        });
        _context.SaveChanges();

        var vm = new OutsourceReceipt
        {
            OutsourcePurchaseOrderId = Opo, SupplierCode = "SUP001",
            ReceiptDate = new DateOnly(2026, 7, 10), ExchangeRate = 1m,
            Lines = { new OutsourceReceiptLine { LineNo = 1, ProductCode = "P-1", ReceiptQty = 10m, UomCode = "PCS", UnitCost = 25m } },
        };

        var result = await _controller.Save(vm);

        Assert.IsType<ContentResult>(result);
        var line = Assert.Single(_context.OutsourceReceipts.Include(r => r.Lines).Single().Lines);
        Assert.Equal(7, line.OpoLineNo);
        Assert.Equal(10m, line.StockQty);
        Assert.Equal("PCS", line.StockUomCode);
    }

    /// <summary>收貨單位與庫存單位不同時，StockQty 走單位換算而不是照抄。</summary>
    [Fact]
    public async Task Save_New_ConvertsStockQty_WhenReceiptUomDiffersFromStockUom()
    {
        _context.OutsourcePurchaseOrderLines.Add(new OutsourcePurchaseOrderLine
        {
            OutsourcePurchaseOrderId = Opo, LineNo = 1, SourceWorkOrderId = "WO-1",
            OperationSeq = 1, ProductCode = "P-2", Qty = 5m, UomCode = "BOX", UnitPrice = 100m,
        });
        _context.Products.Add(new SmartFactory.MES.Modules.BAS.Models.Product
        {
            ProductCode = "P-2", ProductName = "委外成品(箱)", StockUom = "PCS",
            PurchaseUom = "BOX", SalesUom = "PCS", IsActive = true,
        });
        _context.SaveChanges();
        _mockUom.Setup(u => u.ConvertAsync("P-2", "BOX", "PCS", 3m, It.IsAny<CancellationToken>()))
            .ReturnsAsync(36m);

        var vm = new OutsourceReceipt
        {
            OutsourcePurchaseOrderId = Opo, SupplierCode = "SUP001",
            ReceiptDate = new DateOnly(2026, 7, 10), ExchangeRate = 1m,
            Lines = { new OutsourceReceiptLine { LineNo = 1, ProductCode = "P-2", ReceiptQty = 3m, UomCode = "BOX", UnitCost = 100m } },
        };

        var result = await _controller.Save(vm);

        Assert.IsType<ContentResult>(result);
        var line = Assert.Single(_context.OutsourceReceipts.Include(r => r.Lines).Single().Lines);
        Assert.Equal(36m, line.StockQty);
        Assert.Equal("PCS", line.StockUomCode);
        _mockUom.Verify(u => u.ConvertAsync("P-2", "BOX", "PCS", 3m, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>表單已帶值時不覆寫（同一品號在同一張委外採購單上出現兩次，只能靠表單分辨）。</summary>
    [Fact]
    public async Task Save_New_KeepsClientSuppliedOpoLineNo_WhenProductIsAmbiguous()
    {
        _context.OutsourcePurchaseOrderLines.AddRange(
            new OutsourcePurchaseOrderLine
            {
                OutsourcePurchaseOrderId = Opo, LineNo = 1, SourceWorkOrderId = "WO-1",
                OperationSeq = 1, ProductCode = "P-1", Qty = 10m, UomCode = "PCS", UnitPrice = 25m,
            },
            new OutsourcePurchaseOrderLine
            {
                OutsourcePurchaseOrderId = Opo, LineNo = 2, SourceWorkOrderId = "WO-2",
                OperationSeq = 1, ProductCode = "P-1", Qty = 10m, UomCode = "PCS", UnitPrice = 25m,
            });
        _context.Products.Add(new SmartFactory.MES.Modules.BAS.Models.Product
        {
            ProductCode = "P-1", ProductName = "委外成品", StockUom = "PCS",
            PurchaseUom = "PCS", SalesUom = "PCS", IsActive = true,
        });
        _context.SaveChanges();

        var vm = new OutsourceReceipt
        {
            OutsourcePurchaseOrderId = Opo, SupplierCode = "SUP001",
            ReceiptDate = new DateOnly(2026, 7, 10), ExchangeRate = 1m,
            Lines = { new OutsourceReceiptLine { LineNo = 1, OpoLineNo = 2, ProductCode = "P-1", ReceiptQty = 4m, UomCode = "PCS", UnitCost = 25m } },
        };

        var result = await _controller.Save(vm);

        Assert.IsType<ContentResult>(result);
        var line = Assert.Single(_context.OutsourceReceipts.Include(r => r.Lines).Single().Lines);
        Assert.Equal(2, line.OpoLineNo);
    }

    /// <summary>
    /// 單位換算查不到時要回面板錯誤，而不是一頁 500。
    ///
    /// 這是導出 StockQty 之後才浮上來的：以前 StockQty 恆 0，主檔缺換算比例毫無徵兆；
    /// 現在會去查換算，缺的話是**主檔沒建好**（維護人員補得起來），不是系統壞了。
    /// 實機第一次跑就踩到——委外報價開 PCS、品號庫存單位是 SET、兩者之間沒有換算列。
    /// </summary>
    [Fact]
    public async Task Save_New_MissingUomConversion_ReturnsFormError_NotServerError()
    {
        _context.OutsourcePurchaseOrderLines.Add(new OutsourcePurchaseOrderLine
        {
            OutsourcePurchaseOrderId = Opo, LineNo = 1, SourceWorkOrderId = "WO-1",
            OperationSeq = 1, ProductCode = "P-3", Qty = 5m, UomCode = "PCS", UnitPrice = 10m,
        });
        _context.Products.Add(new SmartFactory.MES.Modules.BAS.Models.Product
        {
            ProductCode = "P-3", ProductName = "單位不一致的委外品", StockUom = "SET",
            PurchaseUom = "PCS", SalesUom = "SET", IsActive = true,
        });
        _context.SaveChanges();
        _mockUom.Setup(u => u.ConvertAsync("P-3", "PCS", "SET", It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UomConversionNotFoundException("P-3", "PCS", "SET"));

        var vm = new OutsourceReceipt
        {
            OutsourcePurchaseOrderId = Opo, SupplierCode = "SUP001",
            ReceiptDate = new DateOnly(2026, 7, 10), ExchangeRate = 1m,
            Lines = { new OutsourceReceiptLine { LineNo = 1, ProductCode = "P-3", ReceiptQty = 2m, UomCode = "PCS", UnitCost = 10m } },
        };

        var result = await _controller.Save(vm);

        Assert.IsType<PartialViewResult>(result);
        Assert.Contains(_controller.ModelState[string.Empty]!.Errors,
                        e => e.ErrorMessage.Contains("PCS") && e.ErrorMessage.Contains("SET"));
        Assert.Empty(_context.OutsourceReceipts.ToList());
    }

    /// <summary>
    /// 委外收貨的業務類別必須是 Purchase，收成品才會 借 1401／貸 2152。
    ///
    /// 預設 "Outsource" 會讓 <c>BuildJournalLines</c> 回空分錄——那條規則是給**委外發料**的
    /// （料只換形態，價值本來就在存貨科目）。收成品反過來：加工費是新增的價值，
    /// 不借進存貨就永遠不會出現在總帳，而應付發票的 借 2152 沒有對手方，
    /// 2152 每收一次貨就多掛一筆沖不掉的借方（W2-F13）。
    /// 異動類別主檔的 OI 本來就是 Purchase，這裡只是把預設值對回去。
    /// </summary>
    [Fact]
    public void TypeCategory_DefaultsToPurchase_SoReceiptDebitsInventoryAndCreditsGrIr()
    {
        Assert.Equal("OI", new OutsourceReceipt().TypeCode);
        Assert.Equal("Purchase", new OutsourceReceipt().TypeCategory);
    }

    /// <summary>
    /// 合約允收損耗率沿委外採購單快照到收貨明細——這是超耗扣款「賠不賠」的唯一依據。
    ///
    /// 這個欄位過去只存在於收貨明細上，**上游沒有任何來源**（委外採購單與報價都沒有這個欄位、
    /// 全庫沒有一個寫入點），於是恆為 0；而扣款計算被 <c>allowedRate &gt; 0</c> 擋著，
    /// 超耗扣款在正式環境**結構性不可達**（W2-F14）。
    /// 現在的鏈是：委外報價 → 委外採購單行 → 收貨明細，與加工費同一條。
    /// </summary>
    [Fact]
    public async Task Save_New_SnapshotsAllowedScrapRate_FromPurchaseOrderLine()
    {
        SeedProduct("P-1");
        _context.OutsourcePurchaseOrderLines.Add(new OutsourcePurchaseOrderLine
        {
            OutsourcePurchaseOrderId = Opo, LineNo = 1, SourceWorkOrderId = "WO-1",
            OperationSeq = 1, ProductCode = "P-1", Qty = 20m, UomCode = "PCS",
            UnitPrice = 25m, AllowedScrapRate = 0.05m,
        });
        _context.SaveChanges();

        var result = await _controller.Save(NewReceiptVm("P-1", qty: 10m));

        Assert.IsType<ContentResult>(result);
        var line = Assert.Single(_context.OutsourceReceipts.Include(r => r.Lines).Single().Lines);
        Assert.Equal(0.05m, line.AllowedScrapRate);
    }

    /// <summary>
    /// 合約沒約定損耗條款時保持 null，**不是 0**。
    ///
    /// null ＝ 沒談過 → 不扣款；0 ＝ 明確約定零容忍 → 短交即全額求償。
    /// 兩者的財務結果完全相反，用不可為 null 的 decimal 就分不出來。
    /// </summary>
    [Fact]
    public async Task Save_New_LeavesAllowedScrapRateNull_WhenContractHasNoClause()
    {
        SeedProduct("P-1");
        _context.OutsourcePurchaseOrderLines.Add(new OutsourcePurchaseOrderLine
        {
            OutsourcePurchaseOrderId = Opo, LineNo = 1, SourceWorkOrderId = "WO-1",
            OperationSeq = 1, ProductCode = "P-1", Qty = 20m, UomCode = "PCS",
            UnitPrice = 25m, AllowedScrapRate = null,
        });
        _context.SaveChanges();

        await _controller.Save(NewReceiptVm("P-1", qty: 10m));

        var line = Assert.Single(_context.OutsourceReceipts.Include(r => r.Lines).Single().Lines);
        Assert.Null(line.AllowedScrapRate);
    }

    private void SeedProduct(string code) =>
        _context.Products.Add(new SmartFactory.MES.Modules.BAS.Models.Product
        {
            ProductCode = code, ProductName = code, StockUom = "PCS",
            PurchaseUom = "PCS", SalesUom = "PCS", IsActive = true,
        });

    private OutsourceReceipt NewReceiptVm(string productCode, decimal qty) => new()
    {
        OutsourcePurchaseOrderId = Opo, SupplierCode = "SUP001",
        ReceiptDate = new DateOnly(2026, 7, 10), ExchangeRate = 1m,
        Lines = { new OutsourceReceiptLine { LineNo = 1, ProductCode = productCode, ReceiptQty = qty, UomCode = "PCS", UnitCost = 25m } },
    };

    // ── W2-F9：委外採購單談好的加工費要帶到收貨畫面 ────────────────────────

    /// <summary>
    /// <c>GetOpoLines</c> 必須回傳 OPO 行上的加工費單價。
    /// 回 null 的話畫面預設 0，作業員不自己翻單重打就是應付 0、成品吸收 0 加工費，
    /// 而且沒有任何提示（W2-F9）——委外報價 → OPO 一路談好的價格在最後一步掉了。
    /// </summary>
    [Fact]
    public async Task GetOpoLines_ReturnsAgreedUnitPrice_AndLineNo()
    {
        _context.OutsourcePurchaseOrderLines.Add(new OutsourcePurchaseOrderLine
        {
            OutsourcePurchaseOrderId = Opo, LineNo = 3, SourceWorkOrderId = "WO-1",
            OperationSeq = 1, ProductCode = "P-1", Qty = 20m, UomCode = "PCS", UnitPrice = 25m,
        });
        _context.SaveChanges();

        var json = Assert.IsType<JsonResult>(await _controller.GetOpoLines(Opo));
        var row = Assert.Single(Assert.IsAssignableFrom<System.Collections.IEnumerable>(json.Value!).Cast<object>());

        Assert.Equal(3, row.GetType().GetProperty("LineNo")!.GetValue(row));
        Assert.Equal(25m, row.GetType().GetProperty("unitCost")!.GetValue(row));
    }
}

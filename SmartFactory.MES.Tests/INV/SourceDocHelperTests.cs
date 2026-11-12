using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;

namespace SmartFactory.MES.Tests.INV;

/// <summary>
/// SourceDocHelper.GetRemainingQtyAsync — 驗證沖銷餘量計算正確性，
/// 重點：在審核流程中，Draft/PendingApproval 的沖銷單應計入待沖量，
/// 避免「多張草稿同時審核後超額沖銷」問題。
/// </summary>
public class SourceDocHelperTests : TestBase
{
    private const string PlantId = "P001";

    public SourceDocHelperTests()
    {
        _context.CurrentPlantId = PlantId;
        _context.SaveChanges();
    }

    // ─────────── SampleDoc (CI → CR) ───────────

    private SampleDoc SeedSourceCi(string id, decimal qty, decimal qtySourceCleared = 0)
    {
        var doc = new SampleDoc
        {
            SampleDocId = id,
            SampleDate  = new DateOnly(2026, 4, 1),
            TypeCode    = "CI",
            EntityType  = "Customer",
            EntityCode  = "CUS001",
            CreatedBy   = "u1",
            PlantId     = PlantId,
            Status      = DocStatus.Confirmed,
            ApprovalStatus = DocumentApprovalStatus.Approved,
        };
        doc.Lines.Add(new SampleDocLine
        {
            SampleDocId = id, LineNo = 1,
            ProductCode = "MAT001", Qty = qty, UomCode = "PC",
            StockQty = qty, StockUomCode = "PC",
            QtySourceCleared = qtySourceCleared,
        });
        _context.SampleDocs.Add(doc);
        _context.SaveChanges();
        return doc;
    }

    private void SeedReturnCr(string returnId, string sourceId, decimal qty,
        DocumentApprovalStatus appr = DocumentApprovalStatus.Draft,
        DocStatus status = DocStatus.Draft)
    {
        var doc = new SampleDoc
        {
            SampleDocId = returnId,
            SampleDate  = new DateOnly(2026, 4, 2),
            TypeCode    = "CR",
            EntityType  = "Customer",
            EntityCode  = "CUS001",
            CreatedBy   = "u1",
            PlantId     = PlantId,
            Status      = status,
            ApprovalStatus = appr,
        };
        doc.Lines.Add(new SampleDocLine
        {
            SampleDocId  = returnId, LineNo = 1,
            ProductCode  = "MAT001", Qty = qty, UomCode = "PC",
            StockQty     = qty, StockUomCode = "PC",
            SourceDocId  = sourceId,
            SourceLineNo = 1,
        });
        _context.SampleDocs.Add(doc);
        _context.SaveChanges();
    }

    [Fact]
    public async Task Sample_NoReturns_ShouldReturnFullQty()
    {
        SeedSourceCi("CI001", qty: 2);
        var remaining = await SourceDocHelper.GetRemainingQtyAsync(_context, "Sample", "CI001", 1);
        Assert.Equal(2m, remaining);
    }

    [Fact]
    public async Task Sample_OneDraftReturn_ShouldDeductPending()
    {
        SeedSourceCi("CI001", qty: 2);
        SeedReturnCr("CR001", "CI001", qty: 1, appr: DocumentApprovalStatus.Draft);

        var remaining = await SourceDocHelper.GetRemainingQtyAsync(_context, "Sample", "CI001", 1);

        Assert.Equal(1m, remaining); // 2 - 0 cleared - 1 pending = 1
    }

    [Fact]
    public async Task Sample_TwoDraftReturns_ShouldDeductBoth()
    {
        SeedSourceCi("CI001", qty: 2);
        SeedReturnCr("CR001", "CI001", qty: 1, appr: DocumentApprovalStatus.Draft);
        SeedReturnCr("CR002", "CI001", qty: 1, appr: DocumentApprovalStatus.Draft);

        var remaining = await SourceDocHelper.GetRemainingQtyAsync(_context, "Sample", "CI001", 1);

        Assert.Equal(0m, remaining); // 2 - 0 - 2 pending = 0
    }

    [Fact]
    public async Task Sample_PendingApprovalReturn_ShouldDeductPending()
    {
        SeedSourceCi("CI001", qty: 2);
        SeedReturnCr("CR001", "CI001", qty: 1, appr: DocumentApprovalStatus.PendingApproval);

        var remaining = await SourceDocHelper.GetRemainingQtyAsync(_context, "Sample", "CI001", 1);

        Assert.Equal(1m, remaining); // PendingApproval also counts as in-flight
    }

    [Fact]
    public async Task Sample_ApprovedReturn_ShouldNotCountAsPending()
    {
        // Posted CR → QtySourceCleared on the source line is updated at posting;
        // the posted return must NOT be double-counted as pending
        SeedSourceCi("CI001", qty: 2, qtySourceCleared: 2);
        SeedReturnCr("CR001", "CI001", qty: 2, appr: DocumentApprovalStatus.Approved, status: DocStatus.Posted);

        var remaining = await SourceDocHelper.GetRemainingQtyAsync(_context, "Sample", "CI001", 1);

        // Posted return is reflected via QtySourceCleared; pending = 0
        Assert.Equal(0m, remaining); // 2 - 2 cleared - 0 pending = 0
    }

    [Fact]
    public async Task Sample_ApprovedClearedThenDraftOverrun_ShouldReturnNegative()
    {
        // Source qty=2, already cleared=2 (posted), a new Draft CR adds 1 more → over-cleared
        SeedSourceCi("CI001", qty: 2, qtySourceCleared: 2);
        SeedReturnCr("CR002", "CI001", qty: 1, appr: DocumentApprovalStatus.Draft);

        var remaining = await SourceDocHelper.GetRemainingQtyAsync(_context, "Sample", "CI001", 1);

        Assert.Equal(-1m, remaining); // 2 - 2 - 1 = -1 → controller rejects saving CR002
    }

    [Fact]
    public async Task Sample_ExcludeReturnDocId_ShouldNotCountSelf()
    {
        SeedSourceCi("CI001", qty: 2);
        SeedReturnCr("CR001", "CI001", qty: 1, appr: DocumentApprovalStatus.Draft);

        // Editing CR001: pass excludeReturnDocId="CR001" so it doesn't count its own qty
        var remaining = await SourceDocHelper.GetRemainingQtyAsync(
            _context, "Sample", "CI001", 1, excludeReturnDocId: "CR001");

        Assert.Equal(2m, remaining); // 2 - 0 cleared - 0 pending (CR001 excluded) = 2
    }

    [Fact]
    public async Task Sample_ReturnForDifferentLine_ShouldNotAffectThisLine()
    {
        SeedSourceCi("CI001", qty: 2);
        // This return references lineNo=2 (different line), should not count against lineNo=1
        var doc = new SampleDoc
        {
            SampleDocId = "CR001",
            SampleDate  = new DateOnly(2026, 4, 2),
            TypeCode    = "CR",
            EntityType  = "Customer",
            EntityCode  = "CUS001",
            CreatedBy   = "u1",
            PlantId     = PlantId,
            Status      = DocStatus.Draft,
            ApprovalStatus = DocumentApprovalStatus.Draft,
        };
        doc.Lines.Add(new SampleDocLine
        {
            SampleDocId = "CR001", LineNo = 1,
            ProductCode = "MAT001", Qty = 2, UomCode = "PC",
            StockQty = 2, StockUomCode = "PC",
            SourceDocId = "CI001",
            SourceLineNo = 2, // different source line
        });
        _context.SampleDocs.Add(doc);
        _context.SaveChanges();

        var remaining = await SourceDocHelper.GetRemainingQtyAsync(_context, "Sample", "CI001", 1);

        Assert.Equal(2m, remaining); // line 1 unaffected
    }

    [Fact]
    public async Task Sample_UnknownSourceDoc_ShouldReturnNull()
    {
        var remaining = await SourceDocHelper.GetRemainingQtyAsync(_context, "Sample", "NOPE", 1);
        Assert.Null(remaining);
    }

    [Fact]
    public async Task Sample_UnknownTypeCategory_ShouldReturnNull()
    {
        var remaining = await SourceDocHelper.GetRemainingQtyAsync(_context, "Unknown", "CI001", 1);
        Assert.Null(remaining);
    }

    // ─────────── LoanDoc (IB → BR) ───────────

    private LoanDoc SeedSourceIb(string id, decimal qty, decimal qtySourceCleared = 0)
    {
        var doc = new LoanDoc
        {
            LoanDocId   = id,
            LoanDate    = new DateOnly(2026, 4, 1),
            TypeCode    = "IB",
            TypeCategory = "Loan",
            EntityType  = "Customer",
            EntityCode  = "CUS001",
            CreatedBy   = "u1",
            PlantId     = PlantId,
            Status      = DocStatus.Confirmed,
            ApprovalStatus = DocumentApprovalStatus.Approved,
        };
        doc.Lines.Add(new LoanDocLine
        {
            LoanDocId = id, LineNo = 1,
            ProductCode = "MAT001", Qty = qty, UomCode = "PC",
            StockQty = qty, StockUomCode = "PC",
            QtySourceCleared = qtySourceCleared,
        });
        _context.LoanDocs.Add(doc);
        _context.SaveChanges();
        return doc;
    }

    private void SeedReturnBr(string returnId, string sourceId, decimal qty,
        DocumentApprovalStatus appr = DocumentApprovalStatus.Draft)
    {
        var doc = new LoanDoc
        {
            LoanDocId   = returnId,
            LoanDate    = new DateOnly(2026, 4, 2),
            TypeCode    = "BR",
            TypeCategory = "Loan",
            EntityType  = "Customer",
            EntityCode  = "CUS001",
            CreatedBy   = "u1",
            PlantId     = PlantId,
            Status      = DocStatus.Draft,
            ApprovalStatus = appr,
        };
        doc.Lines.Add(new LoanDocLine
        {
            LoanDocId    = returnId, LineNo = 1,
            ProductCode  = "MAT001", Qty = qty, UomCode = "PC",
            StockQty     = qty, StockUomCode = "PC",
            SourceDocId  = sourceId,
            SourceLineNo = 1,
        });
        _context.LoanDocs.Add(doc);
        _context.SaveChanges();
    }

    [Fact]
    public async Task Loan_NoReturns_ShouldReturnFullQty()
    {
        SeedSourceIb("IB001", qty: 5);
        var remaining = await SourceDocHelper.GetRemainingQtyAsync(_context, "Loan", "IB001", 1);
        Assert.Equal(5m, remaining);
    }

    [Fact]
    public async Task Loan_TwoDraftReturns_ShouldDeductBoth()
    {
        SeedSourceIb("IB001", qty: 5);
        SeedReturnBr("BR001", "IB001", qty: 2, appr: DocumentApprovalStatus.Draft);
        SeedReturnBr("BR002", "IB001", qty: 2, appr: DocumentApprovalStatus.Draft);

        var remaining = await SourceDocHelper.GetRemainingQtyAsync(_context, "Loan", "IB001", 1);

        Assert.Equal(1m, remaining); // 5 - 0 - 4 pending = 1
    }

    [Fact]
    public async Task Loan_ExcludeReturnDocId_ShouldNotCountSelf()
    {
        SeedSourceIb("IB001", qty: 5);
        SeedReturnBr("BR001", "IB001", qty: 3, appr: DocumentApprovalStatus.Draft);

        var remaining = await SourceDocHelper.GetRemainingQtyAsync(
            _context, "Loan", "IB001", 1, excludeReturnDocId: "BR001");

        Assert.Equal(5m, remaining);
    }

    [Fact]
    public async Task Loan_DraftPlusPendingApproval_ShouldDeductBoth()
    {
        SeedSourceIb("IB001", qty: 5);
        SeedReturnBr("BR001", "IB001", qty: 2, appr: DocumentApprovalStatus.Draft);
        SeedReturnBr("BR002", "IB001", qty: 2, appr: DocumentApprovalStatus.PendingApproval);

        var remaining = await SourceDocHelper.GetRemainingQtyAsync(_context, "Loan", "IB001", 1);

        Assert.Equal(1m, remaining);
    }
}

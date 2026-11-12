using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.INV.Models;
using Xunit;

namespace SmartFactory.MES.Tests._Shared;

/// <summary>
/// <see cref="ApprovalDocumentQueries.LoadApprovalStatusAsync{T}"/> 的行為。
///
/// 這支 helper 是 13 支單據 controller 修改守衛的共同依據，
/// 它回錯值不會有任何地方報錯——只會讓那 13 道守衛一起靜默失效，
/// 所以三種邊界（空 id／查無此單／真有此單）都要釘住。
/// </summary>
public class ApprovalDocumentQueriesTests : TestBase
{
    private const string Plant = "P001";

    public ApprovalDocumentQueriesTests()
    {
        _context.CurrentPlantId = Plant;
        // 這支 helper 走一般查詢，會吃到廠別與資料權限（CreatedByRoleId）兩層全域過濾器，
        // 所以 CurrentRoleId 必須與種子的 CreatedByRoleId 一致，否則單據對它是不存在的。
        _context.CurrentRoleId = "1";
    }

    private void Seed(string id, DocumentApprovalStatus status)
    {
        _context.LoanDocs.Add(new LoanDoc
        {
            LoanDocId = id, LoanDate = new DateOnly(2026, 4, 19),
            TypeCode = "IB", EntityType = "Customer", EntityCode = "C001",
            Status = DocStatus.Draft, ApprovalStatus = status,
            PlantId = Plant, CreatedByRoleId = "1",
        });
        _context.SaveChanges();
    }

    /// <summary>新增單據沒有既有狀態可讀，必須回 null（不可回 Draft 讓呼叫端誤判成「已確認是草稿」）。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyId_ReturnsNull(string? id)
        => Assert.Null(await _context.LoadApprovalStatusAsync<LoanDoc>(id));

    /// <summary>查無此單也回 null：由呼叫端的更新路徑去報「找不到單據」，守衛不搶這個訊息。</summary>
    [Fact]
    public async Task MissingDoc_ReturnsNull()
        => Assert.Null(await _context.LoadApprovalStatusAsync<LoanDoc>("NOPE"));

    [Theory]
    [InlineData(DocumentApprovalStatus.Draft)]
    [InlineData(DocumentApprovalStatus.PendingApproval)]
    [InlineData(DocumentApprovalStatus.Approved)]
    [InlineData(DocumentApprovalStatus.Rejected)]
    public async Task ExistingDoc_ReturnsDbStatus(DocumentApprovalStatus status)
    {
        Seed("IB2026041901", status);
        Assert.Equal(status, await _context.LoadApprovalStatusAsync<LoanDoc>("IB2026041901"));
    }

    /// <summary>
    /// 不得把實體帶進 identity map：更新路徑常是「重建同鍵實體再 Update()」，
    /// 若這支 helper 先追蹤了同一鍵，那裡會炸「已被追蹤」。
    /// </summary>
    [Fact]
    public async Task DoesNotTrackEntity()
    {
        Seed("IB2026041901", DocumentApprovalStatus.PendingApproval);
        _context.ChangeTracker.Clear();

        await _context.LoadApprovalStatusAsync<LoanDoc>("IB2026041901");

        Assert.Empty(_context.ChangeTracker.Entries<LoanDoc>());
    }
}

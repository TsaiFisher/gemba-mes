using Microsoft.AspNetCore.Mvc;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests
{
    /// <summary>
    /// 簽核 v1.4「同一簽核人自動跳簽」與 ModuleControllerBase 送簽助手的互動：
    /// SubmitForApproval 可能因全部步驟被跳過而直接回傳 Approved（contributor 已回寫），
    /// 助手不可再把單據覆寫回 PendingApproval，且回應訊息應區分兩種結果。
    /// </summary>
    public class ModuleSubmitAutoApproveTests : TestBase
    {
        private class TestDocController : ModuleControllerBase
        {
            public Task<IActionResult> Submit(string id)
                => SubmitForApprovalAsync<StockAdjustment>(id, "StockAdjustment", "StockAdjustment");
        }

        private static Mock<IApprovalService> BuildApprovalMock(ApprovalStatus resultStatus)
        {
            var approval = new Mock<IApprovalService>();
            approval.Setup(a => a.GetApprovalConfig("StockAdjustment", It.IsAny<string?>()))
                .ReturnsAsync(new DocumentApprovalConfig
                {
                    DocumentType = "StockAdjustment", IsApprovalRequired = true, ApprovalTemplateId = "T1"
                });
            approval.Setup(a => a.SubmitForApproval("SA-001", "StockAdjustment", "T1", It.IsAny<string>()))
                .ReturnsAsync(new ApprovalRequest
                {
                    Id = "REQ-1", DocumentId = "SA-001", DocumentType = "StockAdjustment",
                    CurrentStepId = "S1", Status = resultStatus
                });
            return approval;
        }

        private TestDocController BuildController(ApprovalStatus resultStatus)
        {
            var controller = new TestDocController
            {
                ControllerContext = GetControllerContextWithServices(
                    approvalService: BuildApprovalMock(resultStatus).Object)
            };
            controller.ControllerContext.HttpContext.Request.Headers["X-Requested-With"] = "XMLHttpRequest";
            return controller;
        }

        [Fact]
        public async Task Submit_NormalFlow_SetsPendingApproval()
        {
            _context.StockAdjustments.Add(new StockAdjustment
            {
                StockAdjustmentId = "SA-001", ApprovalStatus = DocumentApprovalStatus.Draft
            });
            await _context.SaveChangesAsync();

            var result = await BuildController(ApprovalStatus.Pending).Submit("SA-001");

            Assert.IsType<OkObjectResult>(result);
            var doc = await _context.StockAdjustments.FindAsync("SA-001");
            Assert.Equal(DocumentApprovalStatus.PendingApproval, doc!.ApprovalStatus);
            Assert.Equal("REQ-1", doc.ApprovalRequestId);
        }

        [Fact]
        public async Task Submit_AllStepsAutoSkipped_KeepsApprovedStatus()
        {
            _context.StockAdjustments.Add(new StockAdjustment
            {
                StockAdjustmentId = "SA-001", ApprovalStatus = DocumentApprovalStatus.Draft
            });
            await _context.SaveChangesAsync();

            var result = await BuildController(ApprovalStatus.Approved).Submit("SA-001");

            var ok = Assert.IsType<OkObjectResult>(result);
            var doc = await _context.StockAdjustments.FindAsync("SA-001");
            Assert.Equal(DocumentApprovalStatus.Approved, doc!.ApprovalStatus); // 不得覆寫回 PendingApproval
            Assert.Equal("REQ-1", doc.ApprovalRequestId);
            Assert.Contains("StagingAutoApproved", ok.Value!.ToString()); // mock localizer 回傳 key 本身
        }
    }
}

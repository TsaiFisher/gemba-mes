using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests
{
    /// <summary>
    /// PushApprovalNotifier 目標解析測試：與「待我簽核」判定語意一致。
    /// </summary>
    public class PushApprovalNotifierTests
    {
        private readonly Mock<IPushNotificationService> _push = new();
        private readonly PushApprovalNotifier _notifier;

        public PushApprovalNotifierTests() => _notifier = new PushApprovalNotifier(_push.Object);

        private static ApprovalRequest Request(string? activeApprover = null, string? requester = "U-req") => new()
        {
            Id = "R1", DocumentId = "PO001", DocumentType = "PurchaseOrder",
            CurrentStepId = "S1", Status = ApprovalStatus.Pending,
            ActiveApproverId = activeApprover, RequesterId = requester
        };

        [Fact]
        public async Task NotifyPending_ActiveApproverSet_PushesToThatUser()
        {
            var step = new ApprovalStep { Id = "S1", ApproverType = ApproverType.Role, TargetRoleId = "RoleA" };

            await _notifier.NotifyPendingAsync(Request(activeApprover: "U-x"), step);

            _push.Verify(p => p.SendToUserAsync("U-x", It.Is<Func<PushMessage>>(f => f().Body.Contains("PO001")), It.IsAny<string?>()), Times.Once);
            _push.Verify(p => p.SendToRoleAsync(It.IsAny<string>(), It.IsAny<Func<PushMessage>>(), It.IsAny<string?>()), Times.Never);
        }

        [Fact]
        public async Task NotifyPending_RoleStep_PushesToRole()
        {
            var step = new ApprovalStep { Id = "S1", ApproverType = ApproverType.Role, TargetRoleId = "RoleA" };

            await _notifier.NotifyPendingAsync(Request(), step);

            _push.Verify(p => p.SendToRoleAsync("RoleA", It.IsAny<Func<PushMessage>>(), It.IsAny<string?>()), Times.Once);
        }

        [Fact]
        public async Task NotifyPending_UserStep_PushesToTargetUser()
        {
            var step = new ApprovalStep { Id = "S1", ApproverType = ApproverType.User, TargetUserId = "U-target" };

            await _notifier.NotifyPendingAsync(Request(), step);

            _push.Verify(p => p.SendToUserAsync("U-target", It.IsAny<Func<PushMessage>>(), It.IsAny<string?>()), Times.Once);
        }

        [Fact]
        public async Task NotifyPending_UnresolvedSupervisor_PushesNothing()
        {
            var step = new ApprovalStep { Id = "S1", ApproverType = ApproverType.Supervisor };

            await _notifier.NotifyPendingAsync(Request(), step);

            _push.Verify(p => p.SendToUserAsync(It.IsAny<string>(), It.IsAny<Func<PushMessage>>(), It.IsAny<string?>()), Times.Never);
            _push.Verify(p => p.SendToRoleAsync(It.IsAny<string>(), It.IsAny<Func<PushMessage>>(), It.IsAny<string?>()), Times.Never);
        }

        [Fact]
        public async Task NotifyFinalized_PushesToRequester()
        {
            await _notifier.NotifyFinalizedAsync(Request(), ApprovalStatus.Rejected);

            _push.Verify(p => p.SendToUserAsync("U-req", It.Is<Func<PushMessage>>(f => f().Body.EndsWith(PzText.Get("Push_ApprovalRejected"))), It.IsAny<string?>()), Times.Once);
        }

        [Fact]
        public async Task NotifyFinalized_NoRequester_PushesNothing()
        {
            await _notifier.NotifyFinalizedAsync(Request(requester: null), ApprovalStatus.Approved);

            _push.Verify(p => p.SendToUserAsync(It.IsAny<string>(), It.IsAny<Func<PushMessage>>(), It.IsAny<string?>()), Times.Never);
        }
    }
}

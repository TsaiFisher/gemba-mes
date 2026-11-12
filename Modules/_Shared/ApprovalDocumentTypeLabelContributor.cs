using Microsoft.Extensions.Localization;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules._Shared
{
    /// <summary>
    /// ProjectZ 送審單據類型的顯示名稱：ProjectZSharedResource 的 <c>ApprovalDocType_{代碼}</c>。
    /// 單據類型清單以 <see cref="ApprovalDocumentLinks.Templates"/> 為準，
    /// ApprovalDocumentTypeLabelCoverageTests 守門每一種都有中英名稱。
    /// </summary>
    public class ApprovalDocumentTypeLabelContributor : IApprovalDocumentTypeLabelContributor
    {
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;

        public ApprovalDocumentTypeLabelContributor(IStringLocalizer<ProjectZSharedResource> localizer)
            => _localizer = localizer;

        public string? GetLabel(string documentType)
        {
            var s = _localizer["ApprovalDocType_" + documentType];
            return s.ResourceNotFound ? null : s.Value;
        }
    }
}

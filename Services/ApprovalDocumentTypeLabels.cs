using Microsoft.Extensions.Localization;

namespace SmartFactory.MES.Services
{
    /// <summary>
    /// 部署專案提供自家送審單據類型的顯示名稱（依目前 UI 語系）。
    /// 簽核紀錄存的是單據類型代碼（PurchaseOrder、CodeEntry…），畫面要顯示成「採購單」／Purchase Order；
    /// Core 不認識部署專案的單據類型，名稱只能由各模組註冊此介面提供，與 IMenuContributor 同一種擴充方式。
    /// </summary>
    public interface IApprovalDocumentTypeLabelContributor
    {
        /// <summary>不認識此類型回 null，交給下一個來源。</summary>
        string? GetLabel(string documentType);
    }

    /// <summary>簽核畫面用：單據類型代碼 → 目前語系的顯示名稱。</summary>
    public interface IApprovalDocumentTypeLabels
    {
        string Get(string documentType);
    }

    /// <summary>
    /// 解析順序：部署專案的 contributor → Core SharedResource 的 <c>ApprovalDocType_{代碼}</c> → 代碼原樣。
    /// 找不到名稱時回代碼而不是空白：新增單據類型忘了補名稱，畫面上仍看得出是哪一種。
    /// </summary>
    public class ApprovalDocumentTypeLabels : IApprovalDocumentTypeLabels
    {
        private readonly IEnumerable<IApprovalDocumentTypeLabelContributor> _contributors;
        private readonly IStringLocalizer<SharedResource> _localizer;

        public ApprovalDocumentTypeLabels(
            IEnumerable<IApprovalDocumentTypeLabelContributor> contributors,
            IStringLocalizer<SharedResource> localizer)
        {
            _contributors = contributors;
            _localizer    = localizer;
        }

        public string Get(string documentType)
        {
            if (string.IsNullOrEmpty(documentType)) return documentType;

            foreach (var c in _contributors)
            {
                var label = c.GetLabel(documentType);
                if (!string.IsNullOrWhiteSpace(label)) return label;
            }

            var core = _localizer["ApprovalDocType_" + documentType];
            return core.ResourceNotFound ? documentType : core.Value;
        }
    }
}

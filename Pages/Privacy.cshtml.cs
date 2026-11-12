using System.Globalization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Pages;

/// <summary>
/// 隱私權政策（中英雙語）。
///
/// <para><b>資料管理者是部署單位，不是軟體供應商</b>：系統由各公司自行架設，資料存在他們自己的
/// 伺服器上，所以「誰保管資料、向誰行使權利」由部署單位提供，不寫死任何一家公司。
/// 部署單位名稱依序取：組態 <c>PrivacyPolicy:Organization</c>（明確指定）→ 系統參數的公司名稱
/// （依介面語系，中英互為備援）→ 都沒有才退回「貴公司」的通用寫法。聯絡窗口取組態
/// <c>PrivacyPolicy:Contact</c>。</para>
///
/// <para>公司名稱不可用 <see cref="ISystemParamService.GetCompanyNameAsync"/>：它查無值時回傳
/// 「SmartFactory」，頁面會因此把軟體供應商寫成資料管理者——正是本頁要避免的錯誤。</para>
///
/// <para><b>部署專案的補充段落</b>：本頁只寫 Core 本身確實會做的事。部署專案若有額外的資料處理
/// （例如推播、問答助理、自動清除排程），放一個 <c>Pages/_PrivacyExtra.{語系}.cshtml</c>
/// 即會接在通用內容之後——新增檔案即可，不必改本頁（不製造 Core 漂移）。找不到就不顯示。</para>
/// </summary>
public class PrivacyModel : PageModel
{
    public const string OrganizationKey = "PrivacyPolicy:Organization";
    public const string ContactKey = "PrivacyPolicy:Contact";

    private readonly IConfiguration _configuration;
    private readonly ICompositeViewEngine _viewEngine;
    private readonly ISystemParamService _systemParams;

    public PrivacyModel(IConfiguration configuration, ICompositeViewEngine viewEngine, ISystemParamService systemParams)
    {
        _configuration = configuration;
        _viewEngine = viewEngine;
        _systemParams = systemParams;
    }

    /// <summary>介面語系是否為中文；其餘一律顯示英文版。</summary>
    public bool IsChinese { get; private set; }

    /// <summary>部署單位名稱（組態優先，其次系統參數的公司名稱）；皆未設定為 null。</summary>
    public string? Organization { get; private set; }

    /// <summary>聯絡窗口（信箱、電話或單位名稱，自由文字）；未設定為 null。</summary>
    public string? Contact { get; private set; }

    /// <summary>部署專案提供的補充段落 partial 名稱；沒有則為 null。</summary>
    public string? ExtraPartial { get; private set; }

    public async Task OnGetAsync()
    {
        IsChinese = CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        Organization = NullIfBlank(_configuration[OrganizationKey]) ?? await CompanyNameFromSystemParamsAsync();
        Contact = NullIfBlank(_configuration[ContactKey]);

        var extra = IsChinese ? "_PrivacyExtra.zh-TW" : "_PrivacyExtra.en-US";
        ExtraPartial = _viewEngine.FindView(PageContext, extra, isMainPage: false).Success ? extra : null;
    }

    private async Task<string?> CompanyNameFromSystemParamsAsync()
    {
        var all = await _systemParams.GetAllAsync();
        var zh = NullIfBlank(all.GetValueOrDefault(SystemParamKeys.CompanyNameZh));
        var en = NullIfBlank(all.GetValueOrDefault(SystemParamKeys.CompanyNameEn));
        return IsChinese ? zh ?? en : en ?? zh;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

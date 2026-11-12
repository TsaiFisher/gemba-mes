using Microsoft.Extensions.Configuration;

namespace SmartFactory.MES.Modules.RPS;

/// <summary>
/// 報表工具（Phase 0-B 排程段）的功能開關，鍵名 <c>Features:ReportTools:Enabled</c>（§三、§17.2）。
///
/// <para><b>預設 false 是刻意的</b>：開啟後會多一支每分鐘輪詢的背景服務與一個必填的檔案儲存目錄，
/// 不用排程的部署不該被迫承擔。既有部署升級後行為不變，要用的人明確打開。</para>
///
/// <para><b>開關只罩 Phase 0-B 的新東西</b>（派工器、保留期清理、三支畫面），
/// 不罩 Phase 0-A 已上線的報表工具目錄與常用查詢。理由是回歸而非潔癖：
/// <c>_SavedQueries</c> partial 已被 ORD／INV 六支報表畫面無條件引用，
/// 而本開關預設關閉——把它一併罩住等於所有既有部署升級後憑空少了「常用查詢」。</para>
/// </summary>
public static class ReportToolsFeature
{
    public const string ConfigKey = "Features:ReportTools:Enabled";

    public static bool IsEnabled(IConfiguration? configuration)
        => configuration?.GetValue(ConfigKey, false) ?? false;
}

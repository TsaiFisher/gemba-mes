using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SmartFactory.MES.Data
{
    /// <summary>
    /// 部署專案（模組）掛到 <see cref="ApplicationDbContext"/> 的 SaveChanges 攔截器。
    ///
    /// <para>攔截器只能在建立 DbContext 選項時加入，而那段在 Core 的 <c>Program.cs</c>，部署專案不得修改。
    /// 模組在 <c>IAppModule.RegisterServices</c> 以 <c>services.AddScoped&lt;IModuleSaveChangesInterceptor, X&gt;()</c>
    /// 註冊即可，Core 建立 DbContext 時會一併掛上——新增檔案、不改 Core。</para>
    ///
    /// <para>模組攔截器排在 Core 自己的稽核攔截器<b>之前</b>：驗證類攔截器擋下存檔時，
    /// 不應先留下稽核快照。</para>
    /// </summary>
    public interface IModuleSaveChangesInterceptor : ISaveChangesInterceptor
    {
    }
}

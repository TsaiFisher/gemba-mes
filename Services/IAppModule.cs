namespace SmartFactory.MES.Services
{
    /// <summary>
    /// 顧問模組擴充點。每個模組實作此介面，即可在不修改 Core 任何檔案的情況下
    /// 自動完成服務注入與應用程式設定。
    ///
    /// 使用方式：在 Modules/{ModuleName}/ 下新建 {ModuleName}Module.cs 並實作此介面，
    /// Core 的 Program.cs 會自動掃描並載入。
    /// </summary>
    public interface IAppModule
    {
        /// <summary>在 WebApplication 建立前注冊服務（DI、MVC Options、View 路徑等）</summary>
        void RegisterServices(IServiceCollection services, IConfiguration configuration);

        /// <summary>在 WebApplication 建立後設定 Middleware 或其他應用程式層設定</summary>
        void Configure(WebApplication app);
    }
}

using SmartFactory.MES.Models;

namespace SmartFactory.MES.Services
{
    /// <summary>
    /// 檢查 CodeEntry 是否被其他資料引用。
    /// Core 預設實作不檢查，顧問專案透過 IAppModule 註冊自訂實作以檢查模組引用。
    /// </summary>
    public interface ICodeEntryReferenceChecker
    {
        Task<List<string>> CheckReferencesAsync(CodeEntry entry);
    }

    public class DefaultCodeEntryReferenceChecker : ICodeEntryReferenceChecker
    {
        public Task<List<string>> CheckReferencesAsync(CodeEntry entry)
            => Task.FromResult(new List<string>());
    }
}

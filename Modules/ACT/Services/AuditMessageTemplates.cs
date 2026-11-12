using System.Globalization;
using System.Resources;
using System.Text.Json;

namespace SmartFactory.MES.Modules.ACT.Services
{
    /// <summary>
    /// 以**中性資源**（正體中文）組出稽核描述，不經 DI。
    ///
    /// <para><c>DataAuditService</c> 用它填 <c>ErrorDescription</c>。刻意不注入
    /// <see cref="IAuditMessageFormatter"/>：那支服務的建構式被測試與背景排程各自 new 出來，
    /// 為了一句描述多一個必填相依，只會逼出一堆「傳 null 就好」的呼叫端。</para>
    ///
    /// <para>資源名是中性 resx 的資訊清單名稱（<c>SmartFactory.MES.Resources.ProjectZAuditResource</c>）。
    /// 取 <see cref="CultureInfo.InvariantCulture"/> 就是取中性那份，與執行緒語系無關——
    /// 存檔值必須跟著誰在看而變動才是錯的。</para>
    /// </summary>
    public static class AuditMessageTemplates
    {
        private static readonly ResourceManager Rm = new(
            "SmartFactory.MES.Resources.ProjectZAuditResource",
            typeof(ProjectZAuditResource).Assembly);

        public static string FormatDefault(string messageKey, IReadOnlyList<string> args)
        {
            var template = Rm.GetString(messageKey, CultureInfo.InvariantCulture);
            if (template == null) return $"[{messageKey}] {string.Join(" / ", args)}";
            try
            {
                return string.Format(CultureInfo.InvariantCulture, template,
                                     args.Cast<object?>().ToArray());
            }
            catch (FormatException)
            {
                // 參數個數對不上：寧可留下看得出是哪個鍵的字串，也不要讓關帳前稽核擲例外。
                return $"[{messageKey}] {string.Join(" / ", args)}";
            }
        }

        /// <summary>中性資源裡是否有這個鍵（供守門測試用）。</summary>
        public static bool HasKey(string messageKey)
            => Rm.GetString(messageKey, CultureInfo.InvariantCulture) != null;

        public static string ArgsToJson(IReadOnlyList<string> args) => JsonSerializer.Serialize(args);
    }
}

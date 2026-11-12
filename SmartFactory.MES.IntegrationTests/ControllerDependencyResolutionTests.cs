using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SmartFactory.MES.IntegrationTests
{
    /// <summary>
    /// 每一支 Controller 的建構式相依都必須能從 DI 解析出來。
    ///
    /// <para><b>這支測試守的是一個「建置成功、測試全綠、線上 500」的缺陷類型。</b>
    /// 少一筆 DI 註冊不會編譯失敗——MVC 是在**收到請求時**才用
    /// <c>ActivatorUtilities</c> 建控制器的，所以症狀只會出現在真的有人打開那一頁的時候，
    /// 而沒有測試打得到的頁面就永遠不會有人發現。</para>
    ///
    /// <para><b>為什麼在本專案特別容易發生</b>：<c>Program.cs</c> 在
    /// <c>.gitattributes</c> 裡是 <c>merge=ours</c>（本專案有 Cookie 強化、推播註冊、
    /// Mobile 模組種子等客製）。合併 Core 時，Core 新增的
    /// <c>builder.Services.AddScoped&lt;IFoo, Foo&gt;()</c> 會被**整個丟掉，
    /// 不留衝突、也不警告**——而那一行往往正是某支 Core 控制器的相依。
    /// 2026-09-08 同步 Core 的 <c>IPlantSwitchService</c> 時就是靠人工比對才發現要補，
    /// 當時沒有任何自動化擋得住它。</para>
    ///
    /// <para>⚠️ 這支測試只驗「建得出來」，不驗行為。它擋的是整頁 500，
    /// 不是邏輯錯誤——但那正是目前完全沒有守門的那一格。</para>
    /// </summary>
    [Collection("Integration")]
    public class ControllerDependencyResolutionTests
    {
        private readonly TestWebApplicationFactory _factory;

        public ControllerDependencyResolutionTests(TestWebApplicationFactory factory)
            => _factory = factory;

        [Fact]
        public void 每一個簽核摘要provider都註冊了而且解析得出來()
        {
            // 🚨 這一支守的失效**比 Controller 那支更安靜**。
            //
            // 漏註冊一個 IApprovalSummaryProvider 不會 500、不會有任何錯誤——
            // MobileApprovalController 是用 IEnumerable<IApprovalSummaryProvider> 建字典的，
            // 沒註冊的單別就只是「不在字典裡」，於是手機端顯示
            // 「此單別請用電腦簽核」。那是一個**合法的降級狀態**，
            // 與「刻意還沒實作」長得一模一樣，沒有人會發現是漏了一行註冊。
            //
            // ApprovalSummaryCoverageTests 用反射掃型別，掃得到「類別存在」但看不到
            // 「有沒有被註冊進 DI」——這一支補的正是那個缺口。
            var declared = typeof(SmartFactory.MES.Modules._Shared.IApprovalSummaryProvider).Assembly
                .GetTypes()
                .Where(t => t is { IsAbstract: false, IsInterface: false }
                            && typeof(SmartFactory.MES.Modules._Shared.IApprovalSummaryProvider).IsAssignableFrom(t))
                .ToList();

            Assert.True(declared.Count >= 10,
                $"只掃到 {declared.Count} 個 IApprovalSummaryProvider 實作，遠少於預期——" +
                "多半是反射掃不到了，而不是 provider 真的變少。");

            using var scope = _factory.Services.CreateScope();
            var registered = scope.ServiceProvider
                .GetServices<SmartFactory.MES.Modules._Shared.IApprovalSummaryProvider>()
                .ToList();

            var registeredTypes = registered.Select(p => p.GetType()).ToHashSet();
            var missing = declared.Where(t => !registeredTypes.Contains(t))
                .Select(t => t.FullName!)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

            Assert.True(missing.Count == 0,
                "以下 IApprovalSummaryProvider 有實作但**沒有註冊進 DI**：\n  "
                + string.Join("\n  ", missing)
                + "\n\n症狀不是錯誤，是那個單別在手機上安靜地變成「請用電腦簽核」——"
                + "與「刻意還沒實作」分不出來。請在對應模組的 IAppModule.RegisterServices 補上。");

            // 同一個 DocumentType 註冊兩次是設定錯誤：控制器用 GroupBy().First() 收斂，
            // 於是**其中一支會永遠不被呼叫到**，而兩支的摘要內容可能不同。
            var dup = registered.GroupBy(p => p.DocumentType, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => $"{g.Key}：{string.Join("、", g.Select(x => x.GetType().Name))}")
                .ToList();

            Assert.True(dup.Count == 0,
                "以下 DocumentType 註冊了不只一個 provider，其中一支永遠不會被呼叫到：\n  "
                + string.Join("\n  ", dup));
        }

        [Fact]
        public void 每一支Controller的相依都解析得出來()
        {
            var controllers = ControllerTypes();

            // 下限守門：反射掃不到型別時（改了組件載入方式、改了命名），
            // 這支會安靜地變成「零項全過」的裝飾品。
            Assert.True(controllers.Count >= 50,
                $"只掃到 {controllers.Count} 支 Controller，遠少於預期——" +
                "多半是反射掃不到了，而不是控制器真的變少。");

            using var scope = _factory.Services.CreateScope();
            var sp = scope.ServiceProvider;

            var failures = new List<string>();
            foreach (var type in controllers)
            {
                try
                {
                    // 與 MVC 實際建立控制器的方式相同：建構式參數逐一從 DI 取。
                    ActivatorUtilities.CreateInstance(sp, type);
                }
                catch (Exception ex)
                {
                    // InvalidOperationException 的訊息會指名解析不出來的那個型別，
                    // 直接帶出來——否則看到的只是「某支控制器壞了」。
                    failures.Add($"{type.FullName}\n      → {Innermost(ex).Message}");
                }
            }

            Assert.True(failures.Count == 0,
                "以下 Controller 無法從 DI 建立，實際請求進來會是 500（而且沒有任何測試打得到）：\n  "
                + string.Join("\n  ", failures)
                + "\n\n最常見的成因：Program.cs 少了一行服務註冊。"
                + "\n⚠️ 本專案的 Program.cs 是 merge=ours——Core 新增的註冊在合併時會被靜靜丟掉，"
                + "必須在本專案這一側自己補上。");
        }

        private static List<Type> ControllerTypes()
        {
            var appAssembly = typeof(SmartFactory.MES.Services.IUserContext).Assembly;

            return SafeTypes(appAssembly)
                .Where(t => t is { IsAbstract: false, IsGenericTypeDefinition: false }
                            && typeof(ControllerBase).IsAssignableFrom(t))
                .OrderBy(t => t.FullName, StringComparer.Ordinal)
                .ToList();
        }

        private static IEnumerable<Type> SafeTypes(Assembly a)
        {
            try { return a.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null)!; }
        }

        private static Exception Innermost(Exception ex)
            => ex.InnerException is null ? ex : Innermost(ex.InnerException);
    }
}

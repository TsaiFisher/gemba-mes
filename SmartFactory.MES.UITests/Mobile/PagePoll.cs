using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.Mobile
{
    /// <summary>
    /// 在 C# 端輪詢頁面上的條件。
    ///
    /// <para><b>為什麼不用 <c>WaitForFunctionAsync("async () => …")</c></b>：async 判斷式回傳的是 Promise，
    /// Promise 本身是 truthy，Playwright 第一次評估就放行——那個等待從來沒有真的等過，接著的斷言讀到半途的狀態。
    /// 2026-10-21 寫混沌測試時發現：「等佇列送完」在佇列還有兩筆 pending 時就通過了。
    /// 讀 IndexedDB 一定是非同步的，所以凡是等佇列狀態的，一律走這裡。</para>
    /// </summary>
    internal static class PagePoll
    {
        /// <param name="onTick">每隔一秒（實際時間）呼叫一次，例如推進假時鐘讓「每分鐘重試」再跑一輪。</param>
        public static async Task<bool> UntilAsync(IPage page, string asyncPredicateJs, int timeoutMs = 10_000,
            Func<Task>? onTick = null)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            var nextTick = DateTime.UtcNow.AddSeconds(1);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (await page.EvaluateAsync<bool>(asyncPredicateJs)) return true;
                }
                catch (PlaywrightException)
                {
                    // 頁面正在重新載入（例如佇列送出後 location.reload）：腳本還沒載入或 IndexedDB 還沒開，下一輪再看
                }
                if (onTick != null && DateTime.UtcNow >= nextTick)
                {
                    await onTick();
                    nextTick = DateTime.UtcNow.AddSeconds(1);
                }
                await Task.Delay(100);
            }
            return false;
        }
    }
}

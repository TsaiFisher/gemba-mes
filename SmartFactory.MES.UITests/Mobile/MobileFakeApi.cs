using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.Mobile
{
    /// <summary>假後端（<c>RouteAsync("**/api/**")</c> 全換假資料）的共用設定。</summary>
    internal static class MobileFakeApi
    {
        /// <summary>
        /// 遙測（telemetry.js 每分鐘送 <c>/api/telemetry/events</c>）一律回 204、不交給假後端。
        ///
        /// <para>不排除的話，假後端會把遙測當成一筆寫入：計數多一、clientId 是空的，
        /// 「伺服器處理了幾筆」「收到幾個 POST」這類斷言全部差一（2026-10-21 加遙測後 6 支因此紅）。
        /// 必須在泛用的 <c>**/api/**</c> 之後註冊：Playwright 的路由後註冊的先比對。</para>
        /// </summary>
        public static Task IgnoreTelemetryAsync(IBrowserContext context) =>
            context.RouteAsync("**/api/telemetry/events", route => route.FulfillAsync(new RouteFulfillOptions { Status = 204 }));
    }
}

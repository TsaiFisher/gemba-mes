using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.Shared
{
    /// <summary>
    /// 使用手冊靜態檔（<c>/docs/**</c>）與手機 PWA 靜態檔（<c>/mobile/**</c>）的快取策略。
    ///
    /// <para><b>要擋的是什麼：</b><c>UseStaticFiles</c> 預設只送 ETag 與 Last-Modified，不送
    /// <c>Cache-Control</c>。少了它，瀏覽器改用**啟發式快取**——以「距上次修改多久」推算可以放心用多久，
    /// 於是**愈久沒改的檔案被快取愈久**。手冊改版後，HTML 可能是新的而 js/css 還是舊的：
    /// 新舊混搭的失敗模式比整份都舊難查得多（實際踩過：教學課程卡的連結修好了，
    /// 瀏覽器仍拿舊的 doc-i18n.js，畫面上完全看不出是快取）。</para>
    ///
    /// <para><b>手機 PWA 同一個陷阱（2026-10-09 Android 實測 P-5）：</b>bump <c>sw.js</c> 的
    /// <c>CACHE_NAME</c> 後手機確實裝了新 SW，但預快取約 80 個檔只有 1 個打到伺服器，
    /// 其餘全由 HTTP 快取供應舊檔——改版等於沒送出去。<c>sw.js</c> 的 install 已改用
    /// <c>cache:'reload'</c>，這裡是第二道：連 SW 以外的一般載入也一律重驗。</para>
    ///
    /// <para><b>為什麼是 no-cache 而不是 no-store：</b><c>no-cache</c> 仍然會快取，只是每次都要拿
    /// ETag 去重驗；沒改的話伺服器回 304、不重傳內容。代價是一次往返，對手冊這種流量無所謂，
    /// 換到的是「改了就一定看得到」。<c>no-store</c> 則是每次都全量重傳，沒必要。</para>
    ///
    /// <para><b>為什麼寫在模組而不是 Program.cs：</b>零衝突模組架構要求擴充一律走 <c>IAppModule</c>。
    /// <c>Program.cs</c> 先跑完所有模組的 <c>Configure</c>（行 283）才 <c>UseStaticFiles</c>（行 340），
    /// 所以這裡註冊的中介軟體必然排在靜態檔之前，設下的標頭會跟著靜態檔回應一起送出。</para>
    /// </summary>
    public class ManualDocsCacheModule : IAppModule
    {
        /// <summary>手冊的掛載路徑。<c>/docs</c> 底下只有靜態檔，沒有 MVC 路由。</summary>
        private const string DocsPath = "/docs";

        /// <summary>手機 PWA 的掛載路徑。API 在 <c>/api/mobile</c>，不受這裡影響。</summary>
        private const string MobilePath = "/mobile";

        public void RegisterServices(IServiceCollection services, IConfiguration configuration) { }

        public void Configure(WebApplication app)
        {
            app.Use(async (context, next) =>
            {
                if (context.Request.Path.StartsWithSegments(DocsPath)
                    || context.Request.Path.StartsWithSegments(MobilePath))
                {
                    // 在 next() 之前設定：StaticFileMiddleware 之後只會補上自己的
                    // ETag / Last-Modified / Content-Type，不會清掉這一行。
                    context.Response.Headers["Cache-Control"] = "no-cache";
                }

                await next();
            });
        }
    }
}

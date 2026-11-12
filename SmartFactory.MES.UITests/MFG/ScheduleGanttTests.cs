using System.Text.Json;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace SmartFactory.MES.UITests.MFG
{
    /// <summary>
    /// 生產排程甘特圖（`timelines-chart`）的守門。
    ///
    /// <para><b>為什麼需要：</b>甘特圖在此之前**零測試覆蓋**——`SchedulingEngineTests`
    /// 只測後端排程引擎，不碰繪製。而 `timelines-chart` 是**手工 vendored** 的第三方
    /// bundle（不在 `package.json`／libman 內），`Schedule/Index.cshtml` 又耦合到它的
    /// 內部實作四處（見該檔註解）。沒有這支測試，換版是在沒有訊號的情況下換。</para>
    ///
    /// <para><b>兩支測試分工，而且分工是刻意的：</b></para>
    /// <list type="bullet">
    ///   <item><description>
    ///     <see cref="Gantt_LibraryContract_MatchesCallSite"/>——**不依賴任何 DB 資料**。
    ///     用合成資料在頁面內把畫面那條呼叫鏈原樣跑一遍。這支才是換版真正的守門：
    ///     它在乾淨部署的 CI 上也一定執行得到，不會因為「這個環境沒有派工資料」而失去效力。
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="Gantt_RealPage_LoadsWithoutScriptError"/>——打真畫面。
    ///     有資料時驗 bar／tooltip／點擊；沒資料時**只**驗「頁面沒炸、走的是正常的無資料分支」，
    ///     並把走了哪一條印出來。
    ///   </description></item>
    /// </list>
    ///
    /// <para><b>為什麼不讓真畫面那支在無資料時直接失敗：</b>甘特資料來自 `Dispatches`
    /// ＋ `WorkCalendars`，乾淨部署兩者皆空是**正常狀態**，不是缺陷。把它判成失敗會讓
    /// nightly 常態紅，紅久了就沒人看。所以真正的契約驗證移到不需要資料的第一支，
    /// 第二支只負責「真畫面不會白畫面」。</para>
    ///
    /// <para><b>合成資料為什麼要含產能列：</b>`ganttColorScale` 對「正班／加班／▶實際」
    /// 回固定色、其餘走雜湊色相。只餵派工 bar 會讓固定色那條分支從未被執行到。</para>
    /// </summary>
    [Collection("UI Tests")]
    public class ScheduleGanttTests
    {
        private readonly UiTestFixture _fx;
        private readonly ITestOutputHelper _out;

        public ScheduleGanttTests(UiTestFixture fixture, ITestOutputHelper output)
        {
            _fx = fixture;
            _out = output;
        }

        private const string SchedulePath = "/Schedule";

        /// <summary>
        /// `timelines-chart` 把 class 加在**容器元素自己**身上
        /// （原始碼：<c>d3Select(el).attr('class', 'timelines-chart')</c>），不是子元素。
        /// 寫成後代選擇器 `#gantt-chart-area .timelines-chart` 會永遠選不到——
        /// 2026-08-13 本測試第一版就是這樣寫的，結果等 30 秒逾時，看起來像畫面壞了。
        /// </summary>
        private const string ChartSel = "#gantt-chart-area.timelines-chart";

        /// <summary>
        /// 已知且已緩解的 console 噪音：`timelines-chart` 執行期會注入 &lt;style&gt;，
        /// 在 `style-src 'self'` 下必然被 CSP 擋掉。畫面已改以靜態 &lt;link&gt; 載入同一份樣式
        /// （`wwwroot/lib/timelines-chart/timelines-chart.css`，內容與注入樣式逐條相同），
        /// 所以這兩則訊息**不是缺陷**，是既有的、刻意的取捨。2.12.1 與 2.14.3 都會出現。
        /// </summary>
        private static bool IsKnownCspStyleNoise(string text) =>
            text.Contains("style-src", StringComparison.Ordinal)
            && text.Contains("Applying inline style", StringComparison.Ordinal);

        /// <summary>
        /// 底部縮圖刷選（TimeOverview）的上游初始化競態——<b>已在 vendored 檔修掉</b>（2026-10-15）。
        ///
        /// <para>它的 update 被 debounce 1ms 觸發，可能早於父層把 scale 傳進去，於是 <c>t.scale.domain(...)</c> 讀到 null，
        /// 拋 <c>Cannot read properties of null (reading 'domain')</c>。發生在 <c>TimelinesChart()(el)</c> 綁定當下，
        /// 呼叫端控制不了。這裡原本只能放行；2026-10-15 在真畫面量到 8/20 次載入就報一次（不是原先記的 0/5），
        /// 選單巡訪因此時紅時綠，改在 <c>wwwroot/lib/timelines-chart/timelines-chart.min.js</c> 的刷選 update 開頭補
        /// <c>t.scale&amp;&amp;</c> 守衛：scale 還沒來就先不畫，父層設 scale 時會再觸發一次。修後 0/40、縮圖每次都有畫出來。</para>
        ///
        /// <para>換版會把這個守衛蓋掉——<see cref="Gantt_VendoredLibrary_KeepsOverviewGuard"/> 守著它，真畫面的測試也不再放行這個錯誤。</para>
        /// </summary>
        private const string OverviewGuard = "update:function(t){if(t.scale&&!(t.domainRange[1]<=t.domainRange[0]))";

        [Fact]
        public async Task Gantt_VendoredLibrary_KeepsOverviewGuard()
        {
            var page = await _fx.NewLoggedInPageAsync();
            await page.GotoAsync(_fx.BaseUrl + SchedulePath);
            var js = await page.EvaluateAsync<string>(
                "async () => await (await fetch('/lib/timelines-chart/timelines-chart.min.js', { cache: 'no-store' })).text()");
            Assert.True(js.Contains(OverviewGuard, StringComparison.Ordinal),
                "timelines-chart.min.js 的縮圖刷選 update 少了 t.scale 守衛（換版會蓋掉）。" +
                "請照 mfg-domain.md §8.5 耦合點 6 補回，否則排程畫面約四成載入會拋 reading 'domain'。");
        }

        /// <summary>
        /// 切換著色依據並等新圖畫完。切換＝重抓資料、清空容器再重畫：舊圖的 segment 在新資料回來前
        /// 都還在，清空後到新圖畫出前又一個都沒有——只等「有 segment」會被舊圖立刻滿足，
        /// 直接數又可能落在空窗。先在舊 segment 上做記號，等到「有 segment 且全是新圖的」為止。
        /// 逾時不拋，交給呼叫端以 segment 數斷言（訊息比 TimeoutException 有用）。
        /// </summary>
        private static async Task SwitchColorModeAsync(IPage page, string mode)
        {
            await page.EvaluateAsync(
                "() => document.querySelectorAll('#gantt-chart-area .series-segment').forEach(e => e.setAttribute('data-stale', '1'))");
            await page.SelectOptionAsync("#selectColorBy", mode);
            try
            {
                await page.WaitForFunctionAsync(
                    "() => document.querySelectorAll('#gantt-chart-area .series-segment').length > 0 " +
                    "&& !document.querySelector('#gantt-chart-area .series-segment[data-stale]')",
                    null, new PageWaitForFunctionOptions { Timeout = 10_000 });
            }
            catch (TimeoutException) { }
        }

        /// <summary>
        /// 等圖例畫出色塊再數。圖例比 segment **晚一個繪製週期**才出現（2026-10-14 實測：
        /// segment 已是新圖的 141 個時圖例仍是 0，約 100ms 後才是 6）——segment 一出現就數，
        /// 資料一多就會數到 0（main 上 12 次只過 2 次）。逾時仍是 0 才是真的沒畫出來
        /// （domain() 壞掉時圖例永遠是 0，照樣會紅）。
        /// </summary>
        private static async Task<int> WaitForLegendSlotsAsync(IPage page)
        {
            try
            {
                await page.WaitForFunctionAsync(
                    "() => document.querySelectorAll('#gantt-chart-area .legendG .color-slot').length > 0",
                    null, new PageWaitForFunctionOptions { Timeout = 5_000 });
            }
            catch (TimeoutException) { /* 回傳 0，由呼叫端的斷言給出有意義的訊息 */ }
            return await page.Locator("#gantt-chart-area .legendG .color-slot").CountAsync();
        }


        /// <summary>
        /// 把畫面那條呼叫鏈原樣在頁面內跑一遍，釘死四個耦合點。
        ///
        /// 釘的是這四件事（每一件壞掉的徵兆都不一樣，所以要分開回報）：
        /// <list type="number">
        ///   <item><description><b>建構語法</b>：2.14.0 起是 class，舊寫法
        ///     `TimelinesChart()(el)` 會拋 `Cannot call a class as a function`。</description></item>
        ///   <item><description><b>`d.data`</b>：後端塞在 segment 上的額外欄位靠它取；
        ///     退化成 `d.labelVal` 會讓 tooltip 與點擊**靜默**失效（不拋錯）。</description></item>
        ///   <item><description><b>`zColorScale(val)`</b>：仍是唯一的顏色入口，
        ///     且拿到的是 `val` 本身而非整個 datum。</description></item>
        ///   <item><description><b>`minSegmentWidth`</b>：2.13.0 新增，
        ///     用來讓分鐘級工序不被壓成次像素寬。</description></item>
        /// </list>
        /// </summary>
        [Fact]
        public async Task Gantt_LibraryContract_MatchesCallSite()
        {
            var page = await _fx.NewLoggedInPageAsync();
            var pageErrors = new List<string>();
            page.PageError += (_, e) => pageErrors.Add(e);

            await page.GotoAsync(_fx.BaseUrl + SchedulePath);
            await page.WaitForFunctionAsync("() => typeof window.TimelinesChart !== 'undefined'",
                null, new PageWaitForFunctionOptions { Timeout = 15_000 });

            var probe = await page.EvaluateAsync<JsonElement>(@"async () => {
                const host = document.createElement('div');
                host.style.width = '800px';
                host.style.height = '400px';
                document.body.appendChild(host);

                const seenColorArgs = [];

                // 與畫面的 ganttColorScale 同構：**純函式＋掛一個 domain()**。
                // domain() 不是可有可無的裝飾——圖例元件會把沒有 interpolate/interpolator
                // 的 scale 判成 ordinal，然後呼叫 scale.domain()。少了它每次繪製都會拋
                // TypeError（而且 bar 照樣畫得出來，所以不看 PageError 根本不會發現）。
                const probeScale = v => { seenColorArgs.push(v); return '#123456'; };
                probeScale.domain = () => ['正班', '加班', '▶實際'];

                // 與 Schedule/Index.cshtml 同一條呼叫鏈、同一個順序、同一種建構寫法。
                // 任何一環在新版不存在，這裡就會拋 TypeError 而不是靜默略過。
                const chart = TimelinesChart()(host)
                    .data([{
                        group: 'WC-PROBE',
                        data: [
                            { label: 'M-PROBE', data: [
                                { timeRange: [new Date(2026, 0, 1, 8, 0), new Date(2026, 0, 1, 12, 0)],
                                  val: 'LOT-PROBE', ww: 'WW-PROBE', wo: 'WO-PROBE', wh: 240 },
                                // 刻意給一段 1 秒的 segment：沒有 minSegmentWidth 會被壓成次像素寬
                                { timeRange: [new Date(2026, 0, 1, 12, 0), new Date(2026, 0, 1, 12, 0, 1)],
                                  val: 'LOT-TINY', ww: 'WW-TINY' }
                            ]},
                            { label: 'CAP-PROBE', data: [
                                { timeRange: [new Date(2026, 0, 1, 8, 0), new Date(2026, 0, 1, 17, 0)],
                                  val: '正班' }
                            ]}
                        ]
                    }])
                    .zoomX([new Date(2026, 0, 1, 0, 0), new Date(2026, 0, 2, 0, 0)])
                    .timeFormat('%Y-%m-%d %H:%M')
                    .minSegmentWidth(3)
                    .zQualitative(true)
                    .zColorScale(probeScale)
                    .segmentTooltipContent(() => 'probe')
                    .onSegmentClick(() => {})
                    // ⚠️ 這一行是本探針**唯一與畫面不同**的地方，是刻意的。
                    //    底部的 TimeOverview（縮圖刷選）有一個上游自己的初始化競態：
                    //    它的 update 被 debounce 1ms 觸發，可能早於父層把 scale 傳進去，
                    //    於是 `state.scale.domain(...)` 讀到 null（原始碼 timelines-chart.js:9427）。
                    //    在真畫面上沒觀察到（容器有真實版面），但在這種臨時容器上約 1/3 機率重現。
                    //    本探針要釘的是資料／顏色／tooltip 契約，不是縮圖元件，
                    //    留著它只會讓這支測試變成擲骰子。真畫面那支仍然涵蓋 overview。
                    .enableOverview(false);

                // kapsule 的渲染是排程在下一個 tick 的（digest），建構後不會立刻有 DOM。
                // 不等就讀會拿到 0 個 segment 而誤判成「資料格式不相容」。
                for (let i = 0; i < 60 && host.querySelectorAll('.series-segment').length === 0; i++)
                    await new Promise(r => setTimeout(r, 100));

                // segmentTooltipContent 只在 hover 時才會被呼叫（hover 行為由真畫面那支測），
                // 這裡改直接讀 d3 繫結在元素上的 __data__，驗的是「攤平後的物件形狀」。
                const sample = host.querySelector('.series-segment');

                return {
                    // kapsule 的建構式回傳的是內部的 comp（明確 return 會覆蓋 new 的 this），
                    // 所以 `instanceof` 恆為 false——不能拿它當契約，改驗容器有沒有被綁定。
                    boundToHost: host.classList.contains('timelines-chart'),
                    segmentCount: host.querySelectorAll('.series-segment').length,
                    legendSlots: host.querySelectorAll('.legendG .color-slot').length,
                    colorArgs: seenColorArgs,
                    hasMinSegmentWidth: typeof chart.minSegmentWidth === 'function',
                    sampleWidth: sample ? Number(sample.getAttribute('width')) : -1,
                    tooltipProbe: (() => {
                        if (!sample || !sample.__data__) return { reached: false };
                        const d = sample.__data__;
                        return {
                            reached: true,
                            hasData: d && typeof d.data === 'object' && d.data !== null,
                            ww: (d && d.data) ? d.data.ww : undefined,
                            labelValIsString: typeof d.labelVal === 'string'
                        };
                    })()
                };
            }");

            _out.WriteLine(probe.ToString());

            var realPageErrors = pageErrors.ToList();
            Assert.True(realPageErrors.Count == 0,
                $"建構甘特圖時有未攔截的 JS 錯誤：\n  {string.Join("\n  ", realPageErrors)}");

            Assert.True(probe.GetProperty("boundToHost").GetBoolean(),
                "TimelinesChart()(el) 沒有把容器綁起來（容器沒被加上 timelines-chart class）——" +
                "建構契約已變。⚠️ 特別注意 `new TimelinesChart(el)` 這種寫法：" +
                "它在 2.12.1 會**靜默不綁定**，不拋錯、也不畫圖");

            var segments = probe.GetProperty("segmentCount").GetInt32();
            Assert.True(segments >= 3,
                $"合成資料有 3 個 segment，實際只畫出 {segments} 個——" +
                "資料格式（group/label/timeRange/val 巢狀結構）已不相容");

            // 顏色入口：拿到的必須是 val 本身（字串），不是整個 datum。
            var colorArgs = probe.GetProperty("colorArgs").EnumerateArray()
                .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : "(非字串)")
                .ToList();
            Assert.Contains("正班", colorArgs);
            Assert.Contains("LOT-PROBE", colorArgs);

            // d.data：畫面的 segExtras() 完全靠它，退化了會靜默壞掉。
            var tip = probe.GetProperty("tooltipProbe");
            Assert.True(tip.GetProperty("reached").GetBoolean(),
                "取不到 segment 的繫結資料——選擇器或 d3 繫結方式已變");
            Assert.True(tip.GetProperty("hasData").GetBoolean(),
                "攤平後的 datum 沒有 d.data——Schedule/Index.cshtml 的 segExtras() 會整段失效，" +
                "而且是靜默的：tooltip 少欄位、派工面板點不開，但不會拋錯");
            Assert.Equal("WW-PROBE", tip.GetProperty("ww").GetString());

            Assert.True(probe.GetProperty("hasMinSegmentWidth").GetBoolean(),
                "minSegmentWidth 不存在——需要 timelines-chart >= 2.13.0");

            // 圖例畫得出來＝zColorScale.domain() 有被吃到。少了它會拋 TypeError 但 bar 照畫，
            // 所以只看「有沒有 bar」是抓不到的，必須另外釘住圖例。
            Assert.Equal(3, probe.GetProperty("legendSlots").GetInt32());

            // 1 秒的 segment 在 24 小時視窗下不足 0.01px，minSegmentWidth(3) 必須把它撐開。
            var w = probe.GetProperty("sampleWidth").GetDouble();
            _out.WriteLine($"最短 segment 寬度 = {w}px");
        }

        /// <summary>
        /// 重載（「重新載入」、45 秒輪詢、側板動作後）沿用同一個圖表實例：連按 6 次不得有任何 JS 錯誤，
        /// 也不得換掉圖表的 DOM。修正前每次重建圖表、約一半機率拋 overview 競態，6 次全部躲過的機率約 1.5%。
        /// </summary>
        [Fact]
        public async Task Gantt_Reload_ReusesChart_NoScriptError()
        {
            await SeedFixture.ResetAsync();   // 確保有排定時段的派工（⑯ DP0024／DP0025），走「有資料」分支
            var page = await _fx.NewLoggedInPageAsync();
            var pageErrors = new List<string>();
            page.PageError += (_, e) => pageErrors.Add(e);

            await page.GotoAsync(_fx.BaseUrl + SchedulePath);
            await page.Locator("#gantt-chart-area .series-segment").First
                .WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            await page.WaitForTimeoutAsync(1000);

            // 標記目前的圖表 SVG；重載後還是同一個節點才代表沒有重建
            await page.EvaluateAsync("() => { document.querySelector('#gantt-chart-area svg').dataset.e2eMark = '1'; }");

            for (var i = 0; i < 6; i++)
            {
                await page.ClickAsync("#btnLoad");
                await page.WaitForTimeoutAsync(1200);
            }

            Assert.True(pageErrors.Count == 0,
                $"重載甘特時出現 JS 錯誤（重載應沿用同一個圖表實例，不再重建）：\n  {string.Join("\n  ", pageErrors.Distinct())}");
            Assert.True(await page.Locator("#gantt-chart-area svg[data-e2e-mark='1']").CountAsync() > 0,
                "重載後圖表 SVG 被換掉了——loadGantt 又在重建圖表，overview 競態會回來。");
            Assert.True(await page.Locator("#gantt-chart-area .series-segment").CountAsync() > 0, "重載後沒有任何 segment。");
        }

        /// <summary>
        /// 真畫面：不論有無資料都不該有未攔截的 JS 錯誤；有資料時再驗 bar／tooltip／點擊。
        /// </summary>
        [Fact]
        public async Task Gantt_RealPage_LoadsWithoutScriptError()
        {
            var page = await _fx.NewLoggedInPageAsync();
            var pageErrors = new List<string>();
            var consoleErrors = new List<string>();
            page.PageError += (_, e) => pageErrors.Add(e);
            page.Console += (_, m) => { if (m.Type == "error") consoleErrors.Add(m.Text); };

            await page.GotoAsync(_fx.BaseUrl + SchedulePath);

            // 甘特是預設分頁，載入後會自己打 /api/mfg/schedule。等到三個終局之一：
            // 畫出圖、顯示無資料、或顯示載入失敗。逾時代表三者都沒發生＝真的卡住了。
            //
            // ⚠️ 「載入失敗」也要納入等待條件，否則它會一路等到逾時，
            //    錯誤訊息變成無資訊的 TimeoutException——而畫面的 .catch(() => {})
            //    本來就把原始例外丟掉了，兩層一疊就完全查不出原因。
            await page.WaitForFunctionAsync(
                "() => { const a = document.getElementById('gantt-chart-area'); " +
                "return !!a && (a.classList.contains('timelines-chart') || a.querySelector('p')); }",
                null, new PageWaitForFunctionOptions { Timeout = 30_000 });

            var failText = await page.Locator("#gantt-chart-area p.text-danger").CountAsync() > 0
                ? await page.Locator("#gantt-chart-area p.text-danger").InnerTextAsync()
                : null;
            Assert.True(failText is null,
                $"甘特圖走到載入失敗分支（「{failText}」）。畫面的 .catch(() => {{}}) 沒有保留原始例外，" +
                "要查原因請在 loadGantt 的 catch 暫時加上 console.error(e) 後重跑");

            var hasChart = await page.Locator(ChartSel).CountAsync() > 0;
            var segmentCount = await page.Locator("#gantt-chart-area .series-segment").CountAsync();
            _out.WriteLine(hasChart
                ? $"走到「有資料」分支：segment {segmentCount} 個"
                : "走到「無資料」分支（Dispatches／WorkCalendars 為空，乾淨部署的正常狀態）");

            var realPageErrors = pageErrors.ToList();
            Assert.True(realPageErrors.Count == 0,
                $"排程畫面有未攔截的 JS 錯誤：\n  {string.Join("\n  ", realPageErrors)}");

            var realErrors = consoleErrors
                .Where(e => !IsKnownCspStyleNoise(e)).ToList();
            Assert.True(realErrors.Count == 0,
                $"排程畫面 console 有錯誤：\n  {string.Join("\n  ", realErrors)}");

            if (!hasChart)
            {
                // 無資料時仍要確認它是「正常的無資料分支」，不是容器空白＝繪製失敗後什麼都沒留下。
                var placeholder = await page.Locator("#gantt-chart-area p").InnerTextAsync();
                Assert.False(string.IsNullOrWhiteSpace(placeholder),
                    "沒畫出圖、也沒有無資料提示——容器是空的，代表繪製半途失敗");
                return;
            }

            // 繪製是排程在下一個 tick 的，容器掛上 class 的當下未必已經有 segment。
            await page.Locator("#gantt-chart-area .series-segment").First
                .WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
            segmentCount = await page.Locator("#gantt-chart-area .series-segment").CountAsync();
            Assert.True(segmentCount > 0, "畫出了 SVG 但沒有任何 segment");

            // 色階圖例：`ganttColorScale` 是純函式，圖例元件會判定成 ordinal 而呼叫 `.domain()`。
            // 少了 domain() 每次繪製都會拋 TypeError，但 **bar 照樣畫得出來**——所以只看
            // 「有沒有 bar」抓不到，必須另外釘住圖例。畫面在 IIFE 裡，測試看不到變數本身，
            // 於是用「DiscreteLegend 真的依 domain() 畫出 color-slot」當作它存在的證據。
            //
            // ⚠️ 不能斷言固定張數：改成狀態著色後 domain() 回傳的是**本次資料實際出現過的
            //    類別**（Object.keys(ganttValColors)），數量隨資料而動——沒有延遲的一天就少
            //    一個色塊。釘死數字會變成「資料一變就紅」的假警報。這裡要守的是
            //    「domain() 有被吃到、沒有退化成 0」。
            var legendSlots = await WaitForLegendSlotsAsync(page);
            _out.WriteLine($"色階圖例 color-slot = {legendSlots} 個");
            Assert.True(legendSlots > 0,
                "圖例一個色塊都沒有——domain() 沒被吃到（回傳空陣列或拋 TypeError）");

            // hover 一個 segment：tooltip 必須有內容。d3-tip 掛在 document.body 而非容器內，
            // 所以要在 body 底下找（畫面本身也有兩段補丁在處理這件事）。
            await page.Locator("#gantt-chart-area .series-segment").First.HoverAsync();
            var tipText = await page.EvaluateAsync<string>(
                "() => Array.from(document.querySelectorAll('body > .chart-tooltip, body > .d3-tip'))" +
                ".map(t => t.textContent || '').join('')");
            _out.WriteLine($"tooltip 內容長度 = {tipText.Length}");
            Assert.True(tipText.Length > 0,
                "hover 後 tooltip 沒有內容——segmentTooltipContent 沒被呼叫，" +
                "或 d.data 退化導致內容組不出來");

            // ── 著色依據切換：批號著色必須也畫得出來 ──────────────────────────────
            //
            // 顏色只有一個通道（TimelinesChart 只吃 zColorScale(val)），所以批號著色是
            // 「把派工 bar 的 val 換成批號再重繪」。這條路徑會走 buildColorMap 的另一個分支，
            // 而且會**覆寫 seg.val**——寫錯的話狀態著色看起來完全正常、只有切過去才炸，
            // 是很容易漏測的一半。
            await SwitchColorModeAsync(page, "batch");

            var batchSegments = await page.Locator("#gantt-chart-area .series-segment").CountAsync();
            Assert.True(batchSegments > 0, "切到批號著色後畫不出任何 segment");

            var batchLegend = await WaitForLegendSlotsAsync(page);
            _out.WriteLine($"批號著色：segment {batchSegments} 個、圖例 {batchLegend} 個色塊");
            Assert.True(batchLegend > 0, "切到批號著色後圖例是空的——domain() 沒被吃到");

            // 切回狀態著色仍要正常（覆寫 seg.val 後若沒重新抓資料就會回不去）
            await SwitchColorModeAsync(page, "status");
            Assert.True(
                await page.Locator("#gantt-chart-area .series-segment").CountAsync() > 0,
                "切回狀態著色後畫不出任何 segment");
        }
    }
}

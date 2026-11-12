using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace SmartFactory.MES.UITests.Tutor
{
    /// <summary>
    /// 教學熱點座標的**有效性**守門。
    ///
    /// <para><b>要擋的是什麼：</b><c>tutor/hotspots.json</c> 是拿選擇器在真畫面上量出來的框。
    /// 畫面改版之後座標不會自己失效——框照樣畫得出來，只是**指到錯的地方**。
    /// 那是靜默錯位：教學看起來仍在運作，只有人眼看得出它在框空白處。
    /// 靜態測試（<c>UserManualTutorTests</c>）驗得了格式與範圍，驗不了「還指著那顆按鈕嗎」。</para>
    ///
    /// <para><b>為什麼不是重跑產生器：</b>產生器（<c>~/.cache/manual-tutor/hotspotgen</c>）會**寫檔**，
    /// 測試不該改工作區的內容；而且產生器要人記得跑，這條測試的存在意義正是「不用記得」。
    /// 兩者共用同一份 <c>hotspot-selectors.json</c>，不會有第二份選擇器清單。</para>
    ///
    /// <para>只驗中文版：中英兩版的版面差異已由座標分開記錄，
    /// 而選擇器是同一組——中文過了英文不會單獨壞掉（會壞的是版面位移，那屬於重拍截圖的範疇）。</para>
    ///
    /// <para><b>守門範圍＝手冊真的點名的熱點：</b>播放器只有在步驟內文出現「X」時才去查 X 的框
    /// （<c>tutor.js</c> 的 <c>hotspotFor</c>）。實測 622 個框裡有 336 個沒有任何一頁會用到——
    /// 其中 170 個是 <c>操作</c>／<c>Actions</c> 欄頭：86 張截圖有這個框，手冊卻只有一頁真的寫
    /// 「點『操作』欄」，其餘多是報表頁（沒有操作欄，<c>thead th:last-child</c> 選到的是
    /// 「閒置天數」「良率」這類欄頭）。那些框畫不出來，卻剛好是唯一會隨本機資料庫浮動的一類，
    /// 守它只會製造假紅。所以守門範圍是**算出來**的：哪一頁引用了這張截圖、那頁內文有沒有點名這個控制項。
    /// 手冊哪天改寫成「點『操作』欄」，那個框就自動回到守門範圍——時機正好是它開始被畫出來的那一刻。</para>
    ///
    /// <para><b>完整模式與精簡模式：</b>座標是對著**拍截圖那台機器**量出來的，
    /// 而表格是 auto layout——欄寬由那台機器當下的資料內容決定。換一個環境（CI 的乾淨庫、
    /// 不同的字型 render）量到的數字必然不同，那不是缺陷，是拿蘋果比橘子。
    /// 因此座標比對只在完整模式做（預設；<c>hotspotgen</c> 所在的那台機器），
    /// CI（<c>GITHUB_ACTIONS=true</c>）自動降為精簡模式：只驗「選擇器還選得到那顆控制項」。
    /// 可用 <c>TUTOR_HOTSPOT_MODE=full|reduced</c> 強制指定。
    /// 完整模式下仍有兩類熱點的座標本來就沒有可比性，由 <c>hotspot-selectors.json</c> 的旗標標明：
    /// <c>widthFromData</c>（表格欄頭，只比上緣與右緣——那兩條邊是容器決定的）與
    /// <c>positionFromData</c>（彈窗頁尾按鈕，被上方清單推著上下移，只驗選擇器）。</para>
    ///
    /// <para><b>略過不是通過：</b>「這一頁在這個環境沒有資料列」會讓欄頭根本不 render
    /// （多數列表頁是 <c>@if (!Model.Any())</c> 才顯示提示，連 <c>&lt;table&gt;</c> 都不輸出）。
    /// 那種熱點會被記為略過並印出來，且略過過半就直接失敗——否則「全部略過」會長得像全綠。</para>
    /// </summary>
    [Collection("UI Tests")]
    public class HotspotSelectorTests
    {
        /// <summary>座標容忍度（佔截圖寬/高的比例）。1% ≈ 1440px 寬的 14px——
        /// 比「按鈕移到別的地方」小得多，又容得下字型 render 的細微差異。</summary>
        private const double Tolerance = 0.01;

        /// <summary>截圖的拍攝規格。座標是相對這個尺寸正規化的，量測時必須一致。</summary>
        private const int ShotWidth = 1440, ShotHeight = 900;

        /// <summary>是否比對座標。見類別註解「完整模式與精簡模式」。</summary>
        private static bool FullMode
        {
            get
            {
                var mode = Environment.GetEnvironmentVariable("TUTOR_HOTSPOT_MODE");
                if (!string.IsNullOrWhiteSpace(mode))
                    return mode.Trim().Equals("full", StringComparison.OrdinalIgnoreCase);
                return Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true";
            }
        }

        private readonly UiTestFixture _fx;
        private readonly ITestOutputHelper _out;

        public HotspotSelectorTests(UiTestFixture fixture, ITestOutputHelper output)
        {
            _fx = fixture;
            _out = output;
        }

        private static string TutorDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SmartFactory.MES.csproj")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(dir!.FullName, "wwwroot", "docs", "user", "tutor");
        }

        private static JsonElement Load(string name) =>
            JsonDocument.Parse(File.ReadAllText(Path.Combine(TutorDir(), name))).RootElement;

        private static readonly Regex TagRe = new("<[^>]+>", RegexOptions.Compiled);
        private static readonly Regex ZhNameRe = new("「([^」]{1,20})」", RegexOptions.Compiled);
        private static readonly Regex ImgSrcRe = new("<img[^>]+src=\"([^\"]+)\"", RegexOptions.Compiled);

        /// <summary>手冊內文點名了哪些控制項，以截圖為鍵。見類別註解「守門範圍」。
        /// 掃一次就夠，整輪共用。</summary>
        private static readonly Lazy<Dictionary<string, HashSet<string>>> LiveIndex = new(BuildLiveIndex);

        private static Dictionary<string, HashSet<string>> BuildLiveIndex()
        {
            var tutor = Path.GetFullPath(TutorDir());
            var userDir = Path.GetFullPath(Path.Combine(tutor, ".."));
            var index = new Dictionary<string, HashSet<string>>();

            foreach (var file in Directory.EnumerateFiles(userDir, "*.html", SearchOption.AllDirectories))
            {
                if (file.StartsWith(tutor, StringComparison.Ordinal)) continue;   // 播放器自己不是手冊頁

                var raw = File.ReadAllText(file);

                // 「」要從**看得到的字**裡抓，所以先去標籤——屬性值裡的字播放器讀不到。
                var names = ZhNameRe.Matches(TagRe.Replace(raw, " "))
                    .Select(m => m.Groups[1].Value).ToHashSet();
                if (names.Count == 0) continue;

                foreach (Match m in ImgSrcRe.Matches(raw))
                {
                    var key = ShotKey(userDir, file, m.Groups[1].Value);
                    if (key is null) continue;
                    if (!index.TryGetValue(key, out var set)) index[key] = set = new HashSet<string>();
                    set.UnionWith(names);
                }
            }
            return index;
        }

        /// <summary>把手冊頁裡的 <c>&lt;img src&gt;</c> 換算成 hotspots.json 的鍵（相對 docs/user 的路徑）。</summary>
        private static string? ShotKey(string userDir, string pageFile, string src)
        {
            src = src.Split('?', '#')[0];
            if (src.Length == 0 || src.StartsWith("data:", StringComparison.Ordinal)
                || src.Contains("://", StringComparison.Ordinal)) return null;

            if (src.StartsWith('/'))
            {
                const string root = "/docs/user/";
                var i = src.IndexOf(root, StringComparison.Ordinal);
                return i < 0 ? null : src[(i + root.Length)..];
            }

            var abs = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pageFile)!, src));
            return Path.GetRelativePath(userDir, abs).Replace('\\', '/');
        }

        private static HashSet<string> LiveNames(string img) =>
            LiveIndex.Value.TryGetValue(img, out var s) ? s : new HashSet<string>();

        /// <summary>這個控制項在手冊裡可能被寫成哪些字：zh 加上 zhAlias
        /// （resx 鍵 Search 有「查詢」「搜尋」兩種中譯，兩種寫法都查得到框）。</summary>
        private static IEnumerable<string> NamesOf(JsonElement c)
        {
            yield return c.GetProperty("zh").GetString()!;
            if (c.TryGetProperty("zhAlias", out var a))
                foreach (var v in a.EnumerateArray()) yield return v.GetString()!;
        }

        [Fact]
        public async Task 熱點選擇器仍指向截圖上的那顆控制項()
        {
            var cfg = Load("hotspot-selectors.json");
            var recorded = Load("hotspots.json");
            var defaults = cfg.GetProperty("defaults").EnumerateArray().ToList();

            var full = FullMode;
            var page = await _fx.NewLoggedInPageAsync();
            await page.SetViewportSizeAsync(ShotWidth, ShotHeight);

            var failures = new List<string>();
            var skipped = new List<string>();
            var wantedTotal = 0;
            var unusedTotal = 0;     // 有座標、但手冊不會點名＝播放器畫不出來，不在守門範圍
            var checkedCount = 0;    // 真的做了選擇器比對的熱點數
            var geometryCount = 0;   // 真的做了座標比對的熱點數

            foreach (var shot in cfg.GetProperty("shots").EnumerateArray())
            {
                var img = shot.GetProperty("img").GetString()!;

                // 只驗「目前真的有座標」的那些。沒有座標的組合本來就不會被播放器用到，
                // 去驗它等於把「這個畫面沒有新增鈕」當成缺陷——那是內容事實。
                if (!recorded.TryGetProperty(img, out var boxes)) continue;

                var excluded = shot.TryGetProperty("excludeDefaults", out var ex0)
                    ? ex0.EnumerateArray().Select(v => v.GetString()!).ToHashSet()
                    : new HashSet<string>();

                var controls = new List<JsonElement>();
                if (!shot.TryGetProperty("useDefaults", out var ud) || ud.GetBoolean())
                    controls.AddRange(defaults.Where(c => !excluded.Contains(c.GetProperty("zh").GetString()!)));
                if (shot.TryGetProperty("controls", out var cs))
                    controls.AddRange(cs.EnumerateArray());

                var recordedHere = controls
                    .Where(c => boxes.TryGetProperty(c.GetProperty("zh").GetString()!, out _))
                    .ToList();

                // 手冊不會點名的框畫不出來，守它只是拿環境差異當缺陷。見類別註解「守門範圍」。
                var live = LiveNames(img);
                var wanted = recordedHere.Where(c => NamesOf(c).Any(live.Contains)).ToList();
                unusedTotal += recordedHere.Count - wanted.Count;
                if (wanted.Count == 0) continue;
                wantedTotal += wanted.Count;

                // needsDoc：這張圖要點進某張既有單據才開得起來（選擇器裡寫死了單號）。
                // 那份資料只存在於拍截圖的那台機器，CI 的乾淨庫不會有。
                if (!full && shot.TryGetProperty("needsDoc", out var nd) && nd.GetBoolean())
                {
                    skipped.Add($"{img}：整張略過（needsDoc——要有本機才有的既有單據才開得起來）");
                    continue;
                }

                try
                {
                    await GoAsync(page, shot);
                }
                catch (Exception ex)
                {
                    // needsDoc 的圖靠選擇器裡寫死的單號才開得起來。換一個庫沒有那張單據是環境事實，
                    // 與 needsRows 同一類——記為略過，不是缺陷。（略過過半仍會讓整條測試失敗。）
                    var line = ex.Message.Split('\n')[0];
                    if (shot.TryGetProperty("needsDoc", out var nd1) && nd1.GetBoolean())
                        skipped.Add($"{img}：整張略過（needsDoc——此環境沒有選擇器裡寫死的那張單據：{line}）");
                    else
                        failures.Add($"{img}：開不起來（{ex.GetType().Name} {line}）");
                    continue;
                }

                bool? hasRows = null;   // 一頁只量一次

                foreach (var c in wanted)
                {
                    var name = c.GetProperty("zh").GetString()!;
                    var selector = c.GetProperty("selector").GetString()!;

                    // needsRows：像「操作」欄頭這種，列表沒有資料列時整個 <table> 都不會 render。
                    // 那是環境事實（乾淨庫），不是熱點壞掉——但也因此驗不了，只能誠實記為略過。
                    if (c.TryGetProperty("needsRows", out var nr) && nr.GetBoolean())
                    {
                        hasRows ??= await HasDataRowsAsync(page);
                        if (hasRows == false)
                        {
                            skipped.Add($"{img} 的「{name}」：略過（此環境這個列表沒有資料列，欄頭不會 render）");
                            continue;
                        }
                    }

                    checkedCount++;

                    var loc = page.Locator(selector).First;
                    if (await loc.CountAsync() == 0 || !await loc.IsVisibleAsync())
                    {
                        failures.Add($"{img} 的「{name}」：選擇器已選不到 → {selector}");
                        continue;
                    }

                    var b = await loc.BoundingBoxAsync();
                    if (b is null) { failures.Add($"{img} 的「{name}」：沒有版面框"); continue; }

                    // 座標只在拍截圖的那台機器上才有可比性。見類別註解。
                    if (!full) continue;

                    // positionFromData：彈窗頁尾這種被上方清單推著上下移的控制項——
                    // 資料多一列按鈕就往下 40px，比座標等於比資料筆數。只驗選擇器選得到。
                    if (c.TryGetProperty("positionFromData", out var pfd) && pfd.GetBoolean()) continue;
                    geometryCount++;

                    // 別名（查詢／搜尋）兩個鍵座標相同，取手冊實際用的那個。
                    var boxName = NamesOf(c).FirstOrDefault(n => live.Contains(n) && boxes.TryGetProperty(n, out _)) ?? name;
                    var want = boxes.GetProperty(boxName).EnumerateArray().Select(v => v.GetDouble()).ToArray();
                    var got = new[] { b.X / ShotWidth, b.Y / ShotHeight, b.Width / ShotWidth, b.Height / ShotHeight };

                    // 表格欄頭是 auto layout：左緣、寬、高由當下的資料決定（欄變寬、標題換行成兩列），
                    // 但**上緣與右緣是容器決定的**——thead 的頂端、表格的右邊界，與資料無關。
                    // 實測過兩個曾被誤報成「偏移」的案例，上緣與右緣都完全一致，差異 100% 落在寬度。
                    var autoWidth = c.TryGetProperty("widthFromData", out var wfd) && wfd.GetBoolean();
                    var drift = autoWidth
                        ? Math.Max(Math.Abs(want[1] - got[1]), Math.Abs(want[0] + want[2] - got[0] - got[2]))
                        : want.Zip(got, (w, g) => Math.Abs(w - g)).Max();
                    if (drift > Tolerance)
                        failures.Add(
                            $"{img} 的「{name}」：{(autoWidth ? "上緣／右緣" : "位置")}已偏移 {drift:P1}（容忍 {Tolerance:P0}）。"
                            + $"記錄 [{string.Join(", ", want.Select(v => v.ToString("F4")))}]，"
                            + $"實際 [{string.Join(", ", got.Select(v => v.ToString("F4")))}]。"
                            + "先確認這台機器連的是**拍截圖時那個資料庫**——換一個庫量到的數字本來就不同。"
                            + "確認畫面真的改版了，才重拍截圖並重跑 ~/.cache/manual-tutor/hotspotgen"
                            + "（它會用今天的資料重寫全部座標，且此環境沒有資料列的熱點會被靜默刪掉）。");
                }
            }

            _out.WriteLine(full
                ? "模式：完整（選擇器＋座標）"
                : "模式：精簡（只驗選擇器——這台機器的資料與字型都不是拍截圖的那台，座標沒有可比性）");
            _out.WriteLine($"檢查了 {checkedCount} 個熱點的選擇器、{geometryCount} 個熱點的座標"
                + $"（{recorded.EnumerateObject().Count()} 張截圖中的中文版，守門範圍內共 {wantedTotal} 個熱點）");
            _out.WriteLine($"另有 {unusedTotal} 個框手冊不會點名——播放器畫不出來，不在守門範圍（見類別註解）");
            if (skipped.Count > 0)
                _out.WriteLine($"略過 {skipped.Count} 個：\n  " + string.Join("\n  ", skipped));

            // 通過但一個都沒檢查＝這條測試沒有在保護任何東西。
            // 「沉默不等於成功」——這是同一個工作階段裡踩過兩次的坑，所以在這裡明講。
            Assert.True(checkedCount > 0,
                "一個熱點都沒檢查到——hotspots.json 與 hotspot-selectors.json 對不起來，"
                + "這條測試等於沒有在保護任何東西。");

            // 略過同理：略過過半就不是「環境剛好缺幾頁資料」，是這輪根本沒在守門。
            Assert.True(checkedCount * 2 > wantedTotal,
                $"{wantedTotal} 個熱點裡只檢查了 {checkedCount} 個，其餘全被略過——"
                + "這輪等於沒有在守門。先確認測試資料（scripts/seed-e2e-fixture.sql）有沒有被清掉：\n  "
                + string.Join("\n  ", skipped));

            Assert.True(!full || geometryCount > 0,
                "完整模式卻一個座標都沒比對到——座標守門失效了。");

            Assert.True(failures.Count == 0,
                $"檢查了 {checkedCount} 個熱點，其中 {failures.Count} 個已失效：\n  "
                + string.Join("\n  ", failures));
        }

        /// <summary>這一頁的列表現在有沒有資料列。
        /// 兩種空法都要認得：整個 <c>&lt;table&gt;</c> 不 render（多數單據頁），
        /// 以及 table 照 render、tbody 內只有一列跨欄的「查無資料」（多數 BAS 主檔頁）。</summary>
        private static async Task<bool> HasDataRowsAsync(IPage page)
        {
            var rows = await page.Locator("table tbody tr").CountAsync();
            if (rows == 0) return false;
            var placeholders = await page.Locator("table tbody tr td[colspan]").CountAsync();
            return rows > placeholders;
        }

        /// <summary>與 <c>hotspotgen</c> 相同的開畫面程序：goto → 依序點 clicks → 等 waitFor。</summary>
        private async Task GoAsync(IPage page, JsonElement shot)
        {
            var query = shot.TryGetProperty("query", out var q) ? q.GetString() : null;
            var url = $"{_fx.BaseUrl}{shot.GetProperty("route").GetString()}?searched=true{query}";

            var resp = await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            if ((resp?.Status ?? 0) >= 400) throw new InvalidOperationException($"HTTP {resp!.Status}");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 8000 });

            if (shot.TryGetProperty("clicks", out var clicks))
                foreach (var click in clicks.EnumerateArray())
                {
                    var loc = page.Locator(click.GetString()!).First;
                    await loc.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 });
                    await loc.ClickAsync();
                }

            if (shot.TryGetProperty("waitFor", out var wf) && !string.IsNullOrEmpty(wf.GetString()))
                await page.Locator(wf.GetString()!).First
                    .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 });

            // offcanvas 有滑入動畫；沒跑完就量，量到的是位移中的座標。
            if (shot.TryGetProperty("clicks", out _)) await page.WaitForTimeoutAsync(1200);
        }
    }
}

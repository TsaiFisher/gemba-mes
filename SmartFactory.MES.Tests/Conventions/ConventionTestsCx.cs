using System.Text.RegularExpressions;
using Xunit;

namespace SmartFactory.MES.Tests.Conventions
{
    /// <summary>
    /// Cx 系列：針對本次 session 暴露的實際 bug 類型所補充的建置期把關規則。
    ///
    /// Cx1 — ProjectZSharedResource key 完整性
    /// Cx2 — crud-panel.js JS 函式 export 一致性
    /// Cx3 — Model GUID 預設值造成 isNew 誤判
    /// Cx4 — Side Panel Partial 不可含 inner offcanvas-header
    /// </summary>
    public class ConventionTestsCx
    {
        // ── Cx1 ─────────────────────────────────────────────────────────────
        // View 透過 IStringLocalizer<ProjectZSharedResource>（alias L）存取的 key，
        // 必須在 ProjectZSharedResource.{zh-TW,en-US}.resx 雙語皆有。
        // 覆蓋 R1 的盲點：R1 只掃 View-specific resx，本規則掃共用 SharedResource。
        [Fact]
        public void Cx1_ProjectZSharedResource_ViewKeys_ExistInBothResx()
        {
            var root = ConventionScan.RepoRoot();
            var zh = ConventionScan.ResxKeys(
                Path.Combine(root, "Resources", "ProjectZSharedResource.zh-TW.resx"));
            var en = ConventionScan.ResxKeys(
                Path.Combine(root, "Resources", "ProjectZSharedResource.en-US.resx"));

            // 偵測 inject ProjectZSharedResource 並取得 alias（通常為 L）
            var injectPattern = new Regex(
                @"@inject\s+.*?ProjectZSharedResource\s*>\s*(\w+)",
                RegexOptions.Compiled);

            // 提取 alias["key"] 或 alias.GetString("key") 的靜態字串 key
            var violations = new List<string>();

            foreach (var view in ConventionScan.Files("Modules", "*.cshtml"))
            {
                var content = File.ReadAllText(view);
                var injectMatch = injectPattern.Match(content);
                if (!injectMatch.Success) continue;

                var alias = Regex.Escape(injectMatch.Groups[1].Value);
                // 只取靜態字串（排除插值 $"..."）
                var keyPattern = new Regex(
                    $@"{alias}\[\s*""([A-Za-z][A-Za-z0-9_]*)""\s*\]",
                    RegexOptions.Compiled);

                foreach (System.Text.RegularExpressions.Match km in
                    keyPattern.Matches(content))
                {
                    var key = km.Groups[1].Value;
                    if (!zh.Contains(key) || !en.Contains(key))
                        violations.Add($"{ConventionScan.Rel(view)}:{key}");
                }
            }

            AssertRule("Cx1", violations);
        }

        // ── Cx2 ─────────────────────────────────────────────────────────────
        // View 中 onclick / onsubmit 呼叫的 crud-panel.js 函式必須在
        // crud-panel.js 的 window.xxx 中 export，否則按鈕執行時會 ReferenceError。
        [Fact]
        public void Cx2_CrudPanel_FunctionsCalledFromViews_AreExported()
        {
            var root = ConventionScan.RepoRoot();
            var jsPath = Path.Combine(root, "wwwroot", "js", "crud-panel.js");
            if (!File.Exists(jsPath))
            {
                Assert.Fail("crud-panel.js 不存在，請確認路徑。");
                return;
            }

            var jsContent = File.ReadAllText(jsPath);

            // 從 crud-panel.js 取得所有 window.xxx 的 export 名稱
            var exported = new Regex(@"window\.(\w+)\s*=")
                .Matches(jsContent)
                .Select(m => m.Groups[1].Value)
                .ToHashSet();

            // 已知屬於 crud-panel.js 管轄的函式集
            var crudPanelFunctions = new HashSet<string>
            {
                "openPanel", "submitPanel", "closeSidePanel", "hidePanel",
                "deleteItem", "submitApproval", "withdrawApproval",
                "refreshList", "toastSuccess", "armValidation", "execScripts"
            };

            // 掃 View 中所有 event handler inline 呼叫
            var callPattern = new Regex(
                @"(?:onclick|onsubmit|onchange)\s*=\s*""([^""]*)",
                RegexOptions.Compiled);

            var fnCallPattern = new Regex(@"\b(\w+)\s*\(", RegexOptions.Compiled);

            var violations = new List<string>();

            foreach (var view in ConventionScan.Files("Modules", "*.cshtml")
                .Concat(ConventionScan.Files("Views", "*.cshtml")))
            {
                var content = File.ReadAllText(view);
                foreach (System.Text.RegularExpressions.Match ev in
                    callPattern.Matches(content))
                {
                    foreach (System.Text.RegularExpressions.Match fn in
                        fnCallPattern.Matches(ev.Groups[1].Value))
                    {
                        var name = fn.Groups[1].Value;
                        if (crudPanelFunctions.Contains(name) && !exported.Contains(name))
                            violations.Add($"{ConventionScan.Rel(view)}:{name}");
                    }
                }
            }

            AssertRule("Cx2", violations.Distinct().ToList());
        }

        // ── Cx3 ─────────────────────────────────────────────────────────────
        // View 用 string.IsNullOrEmpty(Model.X) 判斷 isNew 時，
        // 若 Model 的 X 屬性有 = Guid.NewGuid() 預設值，
        // 則 isNew 永遠是 false → Create 表單打到 Edit action → RecordNotFound。
        [Fact]
        public void Cx3_IsNew_Model_NoGuidDefault_OnDetectedProperty()
        {
            var root = ConventionScan.RepoRoot();

            // 收集 Models/ + Modules/ 中所有 { PropertyName → 有 Guid.NewGuid() 預設 }
            var guidDefaultProps = new HashSet<string>();
            var guidPropPattern = new Regex(
                @"public\s+string\s+(\w+)\s*\{[^}]*\}\s*=\s*Guid\.NewGuid\(",
                RegexOptions.Compiled);

            foreach (var cs in Directory
                .EnumerateFiles(Path.Combine(root, "Models"),
                    "*.cs", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(
                    Path.Combine(root, "Modules"),
                    "*.cs", SearchOption.AllDirectories))
                .Where(f => Path.GetExtension(f).Equals(".cs", StringComparison.OrdinalIgnoreCase)
                         && !f.Contains(Path.DirectorySeparatorChar + "bin" +
                             Path.DirectorySeparatorChar)
                         && !f.Contains(Path.DirectorySeparatorChar + "obj" +
                             Path.DirectorySeparatorChar)))
            {
                foreach (System.Text.RegularExpressions.Match m in
                    guidPropPattern.Matches(File.ReadAllText(cs)))
                    guidDefaultProps.Add(m.Groups[1].Value);
            }

            // 收集 View 中用 string.IsNullOrEmpty(Model.X) 判 isNew 的屬性名
            var isNewPattern = new Regex(
                @"string\.IsNullOrEmpty\s*\(\s*Model\??\.\s*(\w+)\s*\)",
                RegexOptions.Compiled);

            var violations = new List<string>();
            foreach (var view in ConventionScan.Files("Modules", "*.cshtml"))
            {
                foreach (System.Text.RegularExpressions.Match m in
                    isNewPattern.Matches(File.ReadAllText(view)))
                {
                    var prop = m.Groups[1].Value;
                    if (guidDefaultProps.Contains(prop))
                        violations.Add($"{ConventionScan.Rel(view)}:{prop}");
                }
            }

            AssertRule("Cx3", violations);
        }

        // ── Cx4 ─────────────────────────────────────────────────────────────
        // 注入到 sidePanelBody 的 Partial View（_ 開頭）不應含 offcanvas-header，
        // 否則與外層 Index.cshtml 的 offcanvas-header 疊成雙標題。
        [Fact]
        public void Cx4_SidePanelPartials_NoInnerOffcanvasHeader()
        {
            var violations = new List<string>();
            var headerPattern = new Regex(
                @"class\s*=\s*""[^""]*offcanvas-header[^""]*""",
                RegexOptions.Compiled);

            foreach (var view in ConventionScan.Files("Modules", "*.cshtml"))
            {
                // 只掃 _ 開頭的 partial（排除 _ViewImports / _ViewStart）
                var fname = Path.GetFileName(view);
                if (!fname.StartsWith('_')) continue;
                if (fname is "_ViewImports.cshtml" or "_ViewStart.cshtml") continue;

                var content = File.ReadAllText(view);
                // partial 裡如果同時有 offcanvas-header 和 offcanvas-body，
                // 就是會造成雙標題的結構
                if (headerPattern.IsMatch(content) &&
                    content.Contains("offcanvas-body"))
                    violations.Add(ConventionScan.Rel(view));
            }

            AssertRule("Cx4", violations);
        }

        // ── Cx5 ─────────────────────────────────────────────────────────────
        // Model 的 DataAnnotation 屬性（ErrorMessage / DisplayName / Display.Name）
        // 不得含硬編碼 CJK（中日韓）文字。
        //
        // 原因：
        //   ① 硬編碼中文只在中文環境正確，英文模式下仍顯示中文 → 多語破窗
        //   ② Core / ProjectZ 同步時會被 cp 覆蓋，客製化訊息悄悄消失
        //
        // 正確做法：不帶 ErrorMessage，讓驗證訊息走 SharedResource resx（R5b 把關）；
        //            DisplayName 若需要也走 resx key 而非 CJK 字面。
        [Fact]
        public void Cx5_DataAnnotations_NoHardcodedCjkInErrorMessageOrDisplayName()
        {
            var root = ConventionScan.RepoRoot();
            var cjkPattern = new Regex(@"[一-鿿㐀-䶿　-〿]",
                RegexOptions.Compiled);

            // 偵測：[Required(ErrorMessage = "...CJK...")], [StringLength(..., ErrorMessage = "...")]
            //       [Display(Name = "...CJK...")], [DisplayName("...CJK...")]
            var attrPattern = new Regex(
                @"(?:ErrorMessage|DisplayName?)\s*=\s*""([^""]*)",
                RegexOptions.Compiled);

            var violations = new List<string>();

            // 注意：.NET 的 "*.cs" 萬用字元在部分平台也會比對 ".cshtml"，
            // 需明確用 Path.GetExtension 過濾只保留真正的 .cs 檔。
            var csFiles = Directory
                .EnumerateFiles(Path.Combine(root, "Models"),
                    "*.cs", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(
                    Path.Combine(root, "Modules"),
                    "*.cs", SearchOption.AllDirectories))
                .Where(f => Path.GetExtension(f).Equals(".cs", StringComparison.OrdinalIgnoreCase)
                         && !f.Contains(Path.DirectorySeparatorChar + "bin" +
                             Path.DirectorySeparatorChar)
                         && !f.Contains(Path.DirectorySeparatorChar + "obj" +
                             Path.DirectorySeparatorChar));

            foreach (var cs in csFiles)
            {
                var lines = File.ReadAllLines(cs);
                for (int i = 0; i < lines.Length; i++)
                {
                    foreach (System.Text.RegularExpressions.Match m in
                        attrPattern.Matches(lines[i]))
                    {
                        if (cjkPattern.IsMatch(m.Groups[1].Value))
                            violations.Add(
                                $"{ConventionScan.Rel(cs)}:{i + 1}:{m.Groups[1].Value[..Math.Min(20, m.Groups[1].Value.Length)]}");
                    }
                }
            }

            AssertRule("Cx5", violations);
        }

        // ── Cx6 ─────────────────────────────────────────────────────────────
        // 使用 ProjectZSharedResource（L["..."]）的 View，
        // 其 HTML 元素文字（<h1>–<h6>、<label>、<th>、<td>、<span>、<p>）
        // 不得含硬編碼 CJK，應改用 @L["key"]。
        //
        // 補 Cx1 盲點：Cx1 只確認「用到的 key 在 resx 存在」，
        // 但無法偵測「根本沒有用 L["key"]、直接寫中文」的情況。
        //
        // 注意：本規則僅掃有 ProjectZSharedResource 注入的 View，
        //       沒注入的 View 代表刻意不走多語（例如：純展示或 print）。
        [Fact]
        public void Cx6_ViewsWithSharedResource_NoHardcodedCjkInHtmlElements()
        {
            var violations = new List<string>();
            var cjkPattern = new Regex(@"[一-鿿㐀-䶿]", RegexOptions.Compiled);

            // 偵測 <tagName ...>TEXT</tagName> 中 TEXT 含 CJK 且不含 @ 運算式
            // 涵蓋：h1-h6、label、th、td、span、p、button、li、small
            var elemPattern = new Regex(
                @"<(?:h[1-6]|label|th|td|span|p|button|li|small)\b[^>]*>([^<@]+)",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

            foreach (var view in ConventionScan.Files("Modules", "*.cshtml"))
            {
                var content = File.ReadAllText(view);
                // 只掃有注入 ProjectZSharedResource 的 View
                if (!content.Contains("ProjectZSharedResource")) continue;

                var lines = content.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    // 跳過 Razor 指令行（@inject/@using/@model）、Razor 區塊、HTML 注解
                    var trimmed = line.TrimStart();
                    if (trimmed.StartsWith("@") || trimmed.StartsWith("<!--") ||
                        trimmed.StartsWith("@*"))
                        continue;

                    foreach (System.Text.RegularExpressions.Match m in elemPattern.Matches(line))
                    {
                        var text = m.Groups[1].Value.Trim();
                        if (cjkPattern.IsMatch(text))
                            violations.Add(
                                $"{ConventionScan.Rel(view)}:{i + 1}:{text[..Math.Min(20, text.Length)]}");
                    }
                }
            }

            AssertRule("Cx6", violations);
        }

        // ── Cx7 ─────────────────────────────────────────────────────────────
        // 使用 ProjectZSharedResource 的 View，其 <script> 區塊內
        // innerHTML / insertAdjacentHTML / textContent 等 DOM 字串賦值
        // 不得含 CJK 字面，應改用 _msgs.xxx 等 Razor 預注入變數。
        //
        // 補 R4b 盲點：R4b 只掃 alert/confirm；
        // 本規則額外掃 innerHTML、innerText、textContent 賦值與 template literal。
        [Fact]
        public void Cx7_ScriptBlocks_NoHardcodedCjkInDomAssignments()
        {
            var violations = new List<string>();
            var cjkPattern = new Regex(@"[一-鿿㐀-䶿]", RegexOptions.Compiled);

            // 偵測 innerHTML/innerText/textContent/insertAdjacentHTML 的賦值字串
            var domAssignPattern = new Regex(
                @"(?:innerHTML|innerText|textContent|insertAdjacentHTML)\s*[=+].*?[`'""](.*?)[`'""]",
                RegexOptions.Compiled | RegexOptions.Singleline);

            // 偵測 template literal 內的 CJK（`...<th>CJK</th>...`）
            var templateLiteralPattern = new Regex(
                @"`([^`]*[一-鿿㐀-䶿][^`]*)`",
                RegexOptions.Compiled);

            // 提取 <script>...</script> 區塊
            var scriptBlockPattern = new Regex(
                @"<script\b[^>]*>(.*?)</script>",
                RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

            foreach (var view in ConventionScan.Files("Modules", "*.cshtml"))
            {
                var content = File.ReadAllText(view);
                if (!content.Contains("ProjectZSharedResource")) continue;

                foreach (System.Text.RegularExpressions.Match scriptBlock in
                    scriptBlockPattern.Matches(content))
                {
                    // 先剝 JS 註解：反引號是跨整段 script 配對的，兩個 template literal 之間的
                    // 中文註解會被當成 template 內容誤報。// 前不可是 : 或引號（保留 https:// 等字串）。
                    var script = Regex.Replace(scriptBlock.Groups[1].Value, @"/\*.*?\*/", "", RegexOptions.Singleline);
                    script = Regex.Replace(script, @"(?<![:'""\\/])//[^\n]*", "");
                    var relView = ConventionScan.Rel(view);

                    // 偵測 DOM 賦值字串中的 CJK
                    foreach (System.Text.RegularExpressions.Match m in
                        domAssignPattern.Matches(script))
                    {
                        if (cjkPattern.IsMatch(m.Groups[1].Value))
                            violations.Add($"{relView}:innerHTML:{m.Groups[1].Value[..Math.Min(20, m.Groups[1].Value.Length)]}");
                    }

                    // 偵測 template literal 中的 CJK（table header 等動態 HTML）
                    foreach (System.Text.RegularExpressions.Match m in
                        templateLiteralPattern.Matches(script))
                    {
                        // 排除已用 ${...} 變數注入的（代表已走 _msgs 機制）
                        var raw = m.Groups[1].Value;
                        if (cjkPattern.IsMatch(raw) &&
                            !raw.Contains("${_msgs.") && !raw.Contains("${window.i18n"))
                        {
                            // 正規化：移除換行以避免 baseline 分行問題
                            var flat = Regex.Replace(raw, @"\s+", " ").Trim();
                            violations.Add($"{relView}:template:{flat[..Math.Min(20, flat.Length)]}");
                        }
                    }
                }
            }

            AssertRule("Cx7", violations);
        }

        // ── Cx8 ─────────────────────────────────────────────────────────────
        // inline 事件處理器棘輪（P2 CSP unsafe-inline 回補）。
        // CSP3 一旦於 script-src 加入 nonce-source 即「忽略」unsafe-inline，屆時所有
        // inline on*= 屬性都會失效。/new-screen-projectz.md §10 已止住新畫面 inflow，
        // 本規則凍結既有 880 個存量（141 檔）為 baseline：既有檔逐批改 data-action 後
        // 對應行從 current 消失（自然通過），但**新增**任何 inline handler（含既有檔內
        // 新增）即不在 baseline → 建置失敗，防回流。鍵採「相對路徑 + 正規化屬性」
        // （無行號，符合 baseline 以檔案路徑為鍵的慣例）。回補完成、移除 unsafe-inline
        // 後本規則與 baseline 可一併退場。
        [Fact]
        public void Cx8_InlineEventHandlers_NoNewBeyondBaseline()
        {
            // 比對 <tag ... onX="..."> / onX='...'；要求屬性前為空白以免命中如 "button="。
            var handlerPattern = new Regex(
                @"(?<=\s)(on(?:click|change|submit|input|focus|blur|keyup|keydown|keypress))\s*=\s*(""[^""]*""|'[^']*')",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

            var violations = new List<string>();

            foreach (var view in ConventionScan.Files("Modules", "*.cshtml")
                .Concat(ConventionScan.Files("Views", "*.cshtml")))
            {
                var fname = Path.GetFileName(view);
                if (fname is "_ViewImports.cshtml" or "_ViewStart.cshtml") continue;

                var content = File.ReadAllText(view);
                var rel = ConventionScan.Rel(view);

                foreach (System.Text.RegularExpressions.Match m in handlerPattern.Matches(content))
                {
                    // 正規化：事件名小寫 + 屬性值壓平空白並截斷，避免換行/長字串造成 baseline 分行。
                    var ev = m.Groups[1].Value.ToLowerInvariant();
                    var val = Regex.Replace(m.Groups[2].Value, @"\s+", " ").Trim();
                    if (val.Length > 80) val = val[..80];
                    violations.Add($"{rel} | {ev}={val}");
                }
            }

            AssertRule("Cx8", violations);
        }


        // ── Cx9 ─────────────────────────────────────────────────────────────
        // window.i18n.t('key') 的 key 必須同時：①在 _JsLocalization.cshtml 的 keys
        // 白名單內；②在雙語 resx 皆有。
        // 為什麼需要：i18n.t 對查無的 key 是 **return key**（靜默 fallback），
        // 於是使用者會在 confirm() 對話框看到 "ConfirmARReceipt" 這種原始鍵值，
        // 而 build、測試、Console 全都不會叫。Cx1 只管 C# 側的 L["k"]，
        // 白名單是手抄清單，漏抄無人察覺——2026-09-05 實測一次抓到 6 顆
        // （FIN 的 AR/AP 發票作廢、AR 收款、AP 付款確認與作廢，ACT 月結）。
        // 零容忍、無 baseline：漏掛白名單是純粹的手抄疏漏，沒有合理存量。
        //
        // 2026-09-08 併入 PR #131 的同題規則（該 PR 另立 JsLocalizationKeysRegisteredTests
        // 做同一件事，兩支合併時取聯集，那支已刪）。它比本規則多守四件事：
        //   ① 取用正則吃雙引號——原本只認 t('k')，t("k") 整批掃不到。
        //   ② 白名單解析先剝掉 // 註解，否則註解裡的雙引號字串會被當成鍵。
        //      這一項是**把規則變寬**：漏登錄的鍵會因為在註解裡出現過而「看起來有登錄」。
        //   ③ 反向也要查：白名單有、resx 沒有。_JsLocalization 對白名單裡的
        //      **每一顆**都做 L[k].Value，而 IStringLocalizer 查不到就回傳鍵名本身，
        //      於是白名單反過來把鍵名送進全站字典。原本只查「有被用到」的鍵，
        //      這一類要等下一個人用它時才現形。
        //   ④ 兩個下限斷言。①～③ 全是正則，而正則失效時規則不會紅、只會靜靜通過
        //      ——「掃到 0 筆」與「全部合規」在 Assert 上長得一模一樣。
        [Fact]
        public void Cx9_JsI18nKeys_AreWhitelistedAndBilingual()
        {
            var root = ConventionScan.RepoRoot();
            var jsLoc = Path.Combine(root, "Views", "Shared", "_JsLocalization.cshtml");
            Assert.True(File.Exists(jsLoc), $"找不到 {jsLoc}");

            var locSrc = File.ReadAllText(jsLoc);
            var arrStart = locSrc.IndexOf("string[] keys", StringComparison.Ordinal);
            Assert.True(arrStart >= 0, "_JsLocalization.cshtml 找不到 string[] keys 宣告");
            var arrEnd = locSrc.IndexOf("};", arrStart, StringComparison.Ordinal);
            // 逐行剝掉 // 註解後才取字串常值，見上方 ②
            var declBody = string.Join("\n", locSrc[arrStart..arrEnd]
                .Split('\n').Select(l => Regex.Replace(l, @"//.*$", "")));
            var whitelist = new HashSet<string>(
                Regex.Matches(declBody, @"""([A-Za-z0-9_]+)""")
                     .Select(m => m.Groups[1].Value));

            Assert.True(whitelist.Count >= 40,
                $"只從 _JsLocalization.cshtml 解析到 {whitelist.Count} 個鍵（現況 54）"
                + "——解析八成失效了，規則會靜靜地全綠。");

            var zh = ConventionScan.ResxKeys(
                Path.Combine(root, "Resources", "ProjectZSharedResource.zh-TW.resx"));
            var en = ConventionScan.ResxKeys(
                Path.Combine(root, "Resources", "ProjectZSharedResource.en-US.resx"));

            // `window.i18n?.t('k')`（optional chaining）也要算——第一版只認 `i18n.t`，
            // 漏掉 5 顆（盤點結帳、儀器校正、NCR 三個確認對話框），同一天就踩到了。
            var usePattern = new Regex(@"i18n\??\.t\(\s*['""]([A-Za-z0-9_]+)['""]", RegexOptions.Compiled);
            var violations = new List<string>();
            var usedKeys = new HashSet<string>(StringComparer.Ordinal);

            var sources = ConventionScan.Files("Modules", "*.cshtml")
                .Concat(ConventionScan.Files("Views", "*.cshtml"))
                .Concat(ConventionScan.Files("wwwroot", "*.js"));

            foreach (var file in sources)
            {
                var rel = ConventionScan.Rel(file);
                foreach (System.Text.RegularExpressions.Match m in usePattern.Matches(File.ReadAllText(file)))
                {
                    var key = m.Groups[1].Value;
                    usedKeys.Add(key);
                    if (!whitelist.Contains(key))
                        violations.Add($"{rel} | {key} 未列入 _JsLocalization.cshtml 白名單（前端會顯示原始鍵值）");
                    else if (!zh.Contains(key) || !en.Contains(key))
                        violations.Add($"{rel} | {key} 缺 resx（zh={zh.Contains(key)} en={en.Contains(key)}）");
                }
            }

            Assert.True(usedKeys.Count >= 25,
                $"只掃到 {usedKeys.Count} 個相異的 i18n.t 取用（現況 33）——掃描範圍或正則八成錯了。");

            // 反向：白名單登錄了但 resx 缺值，見上方 ③
            foreach (var key in whitelist.Where(k => !zh.Contains(k) || !en.Contains(k))
                                         .OrderBy(k => k, StringComparer.Ordinal))
                violations.Add($"{ConventionScan.Rel(jsLoc)} | {key} 已登錄白名單但缺 resx"
                             + $"（zh={zh.Contains(key)} en={en.Contains(key)}）"
                             + "——白名單會把鍵名本身送進全站字典");

            Assert.True(violations.Count == 0,
                "Cx9 違規：\n" + string.Join("\n", violations.Distinct().OrderBy(v => v)));
        }

        private static void AssertRule(string ruleId, IEnumerable<string> violations)
        {
            var (ok, message) = ConventionScan.CheckAgainstBaseline(ruleId, violations);
            Assert.True(ok, message);
        }
    }
}

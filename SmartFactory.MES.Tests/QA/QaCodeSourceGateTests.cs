using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace SmartFactory.MES.Tests.QA
{
    /// <summary>
    /// 程式碼來源清單的守門（規格 §10.11.13／待決 #50）。
    ///
    /// <para><b>為什麼要在 CI 再守一次</b>：`qa-extract-generate.py` 的閘門只擋得住
    /// 「跑產生器」這條路。真正決定什麼進語料的是 <c>.claude/qa-extracts/*.json</c>，
    /// 而那是人手改的檔案（核可＝改 <c>approved</c> 再送 PR，定案 #45）——
    /// 手寫一份 manifest、或把 <c>sourcePath</c> 改成別支 `.cs`，完全繞得過 Python 那道。</para>
    ///
    /// <para>三條各自對應一種失敗，且**都不會有錯誤訊息**：
    /// 清單列了不存在的檔（改名後忘了同步）→ 那份擷取物永遠 stale，症狀是「答不出來」；
    /// 清單列了紅線檔 → 授權或 2FA 的內部說明被改寫成使用者規則，躺在語料裡等人核可；
    /// manifest 指向清單外的 `.cs` → 預設拒絕形同虛設。</para>
    /// </summary>
    public class QaCodeSourceGateTests
    {
        private readonly ITestOutputHelper _out;

        public QaCodeSourceGateTests(ITestOutputHelper output) => _out = output;

        /// <summary>
        /// 與 `qa-extract-generate.py` 的 <c>CODE_REDLINE</c> 同一份定義。
        /// **兩邊會漂移**，這是已知的代價：Python 那道守的是「跑產生器」，這道守的是
        /// 「進版控」，兩條路徑不同進程、不同語言，沒有共用的地方可以放。
        /// 漂移的方向是安全的——這邊多擋一個字只會多要求一次人工說明，
        /// 少擋一個字仍有 Python 那道在前面。
        /// </summary>
        private static readonly Regex Redline = new(
            "License|Fingerprint|TwoFactor|2fa|Totp|Recovery|Crypt|Password|Secret" +
            "|Token|Antiforgery|DataProtection|Auth(entication|orization)?(Handler|Service|Filter)?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly string[] PathDeny =
        {
            "SmartFactory.MES.Tests/", "SmartFactory.MES.IntegrationTests/",
            "SmartFactory.MES.UITests/", "/Migrations/", "/obj/", "/bin/",
        };

        private static string RepoRoot()
        {
            // ⚠️ `.git` 在 **worktree 裡是一個檔案**（內容是 `gitdir: ...`），不是目錄。
            // 只判 Directory.Exists 的話，這三支守門在任何 worktree 都會以
            // 「找不到 repo 根目錄」失敗，而 CI 的 actions/checkout 產生的是真目錄，
            // 於是 CI 恆綠、本機恆紅——最容易被當成環境問題而放著不管的那種形狀。
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null
                   && !Directory.Exists(Path.Combine(dir.FullName, ".git"))
                   && !File.Exists(Path.Combine(dir.FullName, ".git")))
                dir = dir.Parent;
            return dir?.FullName ?? throw new DirectoryNotFoundException("找不到 repo 根目錄");
        }

        /// <summary>清單裡的路徑（去掉註解與空行）。檔案不存在時回空——**空＝一支都不准收**。</summary>
        private static List<string> ListedSources(string root)
        {
            var file = Path.Combine(root, ".claude", "qa-extracts", "code-sources.txt");
            if (!File.Exists(file)) return new List<string>();

            return File.ReadAllLines(file)
                .Select(l => l.Split('#', 2)[0].Trim())
                .Where(l => l.Length > 0)
                .Select(l => l.Split('\t')[0].Trim())
                .ToList();
        }

        [Fact]
        public void 清單裡的每一支都要存在且不得命中紅線或測試路徑()
        {
            var root = RepoRoot();
            var listed = ListedSources(root);
            if (listed.Count == 0) return;   // 還沒開放任何程式碼來源時本條無事可做

            var violations = new List<string>();

            foreach (var rel in listed)
            {
                if (!rel.EndsWith(".cs", StringComparison.Ordinal))
                    violations.Add($"{rel}：只收 .cs");

                if (!File.Exists(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar))))
                    violations.Add($"{rel}：檔案不存在（改名或刪除後沒同步清單，那份擷取物會永遠 stale）");

                if (Redline.IsMatch(Path.GetFileName(rel)))
                    violations.Add($"{rel}：檔名命中紅線（授權／指紋／2FA／密碼／權杖／加密／驗證授權）");

                if (PathDeny.Any(d => ("/" + rel).Contains(d, StringComparison.Ordinal)))
                    violations.Add($"{rel}：在測試／migration／建置產物路徑下");
            }

            Assert.True(violations.Count == 0,
                $"程式碼來源清單有 {violations.Count} 項違規：\n  " + string.Join("\n  ", violations));

            _out.WriteLine($"程式碼來源 {listed.Count} 支，全部存在且未命中紅線。");
        }

        /// <summary>
        /// **這一條才是閘門真正的牙齒。** 上一條守的是清單本身，這一條守的是
        /// 「manifest 只能來自清單上的來源」——否則手寫一份 manifest 就繞過了整道閘門，
        /// 而繞過的痕跡只有 <c>sourcePath</c> 那一行字。
        /// </summary>
        [Fact]
        public void 每一份程式碼manifest的來源都必須在清單上()
        {
            var root = RepoRoot();
            var dir = Path.Combine(root, ".claude", "qa-extracts");
            if (!Directory.Exists(dir)) return;

            var listed = ListedSources(root).ToHashSet(StringComparer.Ordinal);
            var offenders = new List<string>();
            var codeManifests = 0;

            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (!doc.RootElement.TryGetProperty("sourcePath", out var sp)) continue;

                var src = sp.GetString() ?? string.Empty;
                if (!src.EndsWith(".cs", StringComparison.Ordinal)) continue;

                codeManifests++;
                if (!listed.Contains(src))
                    offenders.Add($"{Path.GetFileName(file)} → {src}");
            }

            Assert.True(offenders.Count == 0,
                "以下 manifest 的來源不在 .claude/qa-extracts/code-sources.txt 上，" +
                "等於繞過預設拒絕的閘門：\n  " + string.Join("\n  ", offenders));

            _out.WriteLine($"程式碼 manifest {codeManifests} 份，來源全部在清單上。");
        }

        /// <summary>
        /// 與 `qa-extract-generate.py` 的 <c>CODE_REDLINE_BODY</c> 同一份定義（同樣會漂移，
        /// 理由同 <see cref="Redline"/>）。
        ///
        /// <para><b>為什麼需要第三道</b>：上面兩條比對的都是**檔名**。授權／金鑰／2FA
        /// 那種內容可以住在任何一支檔名乾淨的檔裡——`Program.cs:665` 就是整段
        /// 啟動授權驗證，而 `Program.cs` 這個檔名一個字都不命中。
        /// **過濾的維度與內容所在的維度不同，就一定漏。**</para>
        /// </summary>
        private static readonly Regex RedlineBody = new(
            "License|Fingerprint|TwoFactor|Totp|Antiforgery|DataProtection" +
            "|RecoveryCode|PasswordHash|clientCode" +
            "|授權檔|授權驗證|授權失效|機器指紋|環境指紋|雙因素|備援碼|私鑰|公鑰|金鑰|密碼雜湊",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// 內容紅線的例外，以 slug 為鍵。**預設拒絕、逐則列舉**，與 `code-sources.txt`
        /// 同一個形狀：加一則進來是一個在 PR diff 裡看得到、要被審的動作。
        ///
        /// <para>⚠️ 不准為了讓這條測試變綠而放寬 <see cref="RedlineBody"/> ——
        /// 把規則改成符合現有資料，是閘門無聲腐爛的標準路徑。要留就留在這裡，
        /// 一則一行、附理由。</para>
        /// </summary>
        private static readonly Dictionary<string, string> RedlineBodyAllow = new(StringComparer.Ordinal)
        {
            // 目前是空的，而且**空才是正常狀態**。唯一一則候選
            // （`code-mobile-mobilepushcontroller-c0a05efb`，推播訂閱綁定失敗的原因）
            // 使用者於 2026-08-26 裁決退回：它講得出的使用者價值只有「綁定失敗」一句，
            // 不值得為它開一個例外。加東西進來之前先問「這則真的有人會問嗎」。
        };

        /// <summary>
        /// **擷取物的說明本身**不得出現授權／指紋／2FA／金鑰這類機制內容。
        ///
        /// <para>manifest 是人手改的檔案。前兩條守住了「來源是哪一支檔」，
        /// 但一則擷取物的文字可以在人工改寫時被改成任何東西——而語料是
        /// **任何登入者查得到**的。這一條守的是最後那一段。</para>
        /// </summary>
        [Fact]
        public void 擷取物的說明不得出現授權金鑰2FA之類的機制內容()
        {
            var root = RepoRoot();
            var dir = Path.Combine(root, ".claude", "qa-extracts");
            if (!Directory.Exists(dir)) return;

            var offenders = new List<string>();
            var scanned = 0;

            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (!doc.RootElement.TryGetProperty("entries", out var entries)) continue;

                foreach (var e in entries.EnumerateArray())
                {
                    // **只掃 approved。** 被退回的那些留在 manifest 裡是刻意的（決策紀錄），
                    // 而 `DocChunkFilter` 要 `Approved AND NOT IsStale` 才會撈——
                    // 掃全部會讓「退回一則」變成 CI 永久紅，逼人去刪決策紀錄。
                    if (!(e.TryGetProperty("approved", out var ap) && ap.ValueKind == JsonValueKind.True))
                        continue;

                    scanned++;
                    var slug = e.TryGetProperty("slug", out var sl) ? sl.GetString() ?? "" : "";
                    // `headingPath` 一起掃：詞彙通道的 `Lexemes` ＝ `HeadingPath` ＋ `Text`
                    // 平鋪（§10.11.12），章節位置和說明一樣是**查得到的內容**。
                    var head = e.TryGetProperty("headingPath", out var h) ? h.GetString() ?? "" : "";
                    var title = e.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    var text = e.TryGetProperty("text", out var x) ? x.GetString() ?? "" : "";

                    var m = RedlineBody.Match(head + "\n" + title + "\n" + text);
                    if (!m.Success) continue;
                    if (RedlineBodyAllow.ContainsKey(slug)) continue;

                    // 只印命中的那個字與 slug，不印整段：把它印進 CI log 等於換個地方外洩。
                    offenders.Add($"{slug}（命中「{m.Value}」）");
                }
            }

            Assert.True(offenders.Count == 0,
                $"以下 {offenders.Count} 則擷取物的說明命中內容紅線。" +
                "要嘛退回，要嘛加進 RedlineBodyAllow 並寫明理由——" +
                "不准改 RedlineBody 讓它變綠：\n  " + string.Join("\n  ", offenders));

            _out.WriteLine($"已核可擷取物 {scanned} 則，章節位置與說明未命中內容紅線"
                + $"（例外 {RedlineBodyAllow.Count} 則）。");
        }
    }
}

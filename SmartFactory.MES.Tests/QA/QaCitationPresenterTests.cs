using System.Text.Json;
using SmartFactory.MES.Modules.QA.Contracts;
using SmartFactory.MES.Modules.QA.Services;
using Xunit;

namespace SmartFactory.MES.Tests.QA
{
    /// <summary>
    /// <see cref="QaCitationPresenter"/> 的守門測試（定案 #47／規格 §10.11.12）。
    ///
    /// <para><b>這是 S-7 唯一不必有推論服務就能完整驗證的部分，也是最容易靜默壞掉的部分。</b>
    /// 分類判錯不會拋例外、不會變紅：使用者只會拿到一條點不下去的連結，
    /// 或者——更糟——一個編出來的頁碼看起來跟真的一樣。</para>
    /// </summary>
    public class QaCitationPresenterTests
    {
        private static DocSearchHit Hit(string code, string heading, string text, string lang = "zh") =>
            new(1, 1, lang, heading, text,
                QaPromptBuilder.DocRoot + code, "MENU", Array.Empty<string>(), 1.0);

        // ── 三種形狀 ────────────────────────────────────────────────────────

        [Fact]
        public void 手冊引文維持真連結_不夾帶內文()
        {
            var given = new[] { Hit("inv/slow-moving-inventory.html", "庫存 › 呆滯料", "全文") };

            var one = Assert.Single(
                QaCitationPresenter.Build(new[] { "inv/slow-moving-inventory.html" }, given));

            Assert.Equal(QaCitationKind.Manual, one.Kind);
            Assert.Equal("/docs/user/inv/slow-moving-inventory.html", one.Url);

            // 手冊點得進去，不需要把整頁再送一次到瀏覽器。
            Assert.Equal(string.Empty, one.Text);
        }

        [Fact]
        public void 擷取物就地展開_不畫連結()
        {
            var given = new[] { Hit("rules/currency-04.html", "匯率 › 換算方向", "本幣金額＝外幣金額 × 匯率") };

            var one = Assert.Single(
                QaCitationPresenter.Build(new[] { "rules/currency-04.html" }, given));

            Assert.Equal(QaCitationKind.Rule, one.Kind);
            Assert.Equal("匯率 › 換算方向", one.HeadingPath);
            Assert.Equal("本幣金額＝外幣金額 × 匯率", one.Text);

            // **空字串代表畫面不得畫 <a>**。rules/*.html 磁碟上沒有這個檔，
            // 而且刻意不產（定案 #47：wwwroot 匿名可讀）——畫成連結就是死連結。
            Assert.Equal(string.Empty, one.Url);
        }

        [Fact]
        public void 沒給過的代號判為無法查證_而且不能被丟掉()
        {
            var given = new[] { Hit("inv/stock-ledger.html", "庫存 › 庫存帳本", "全文") };

            var result = QaCitationPresenter.Build(
                new[] { "inv/stock-ledger.html", "inv/這頁不存在.html" }, given);

            Assert.Equal(2, result.Count);
            Assert.Equal(QaCitationKind.Unverifiable, result[1].Kind);
            Assert.Equal("inv/這頁不存在.html", result[1].PageCode);

            // 不展開也不連結：展開一個空白區塊會把「模型編了一個頁碼」
            // 偽裝成「這頁剛好沒內容」。
            Assert.Equal(string.Empty, result[1].Text);
            Assert.Equal(string.Empty, result[1].Url);

            // **保留而不是丟掉**：丟掉的話，「模型編了頁碼」與「模型只引了一頁」
            // 在畫面上完全一樣，而前者正是使用者最該提高警覺的訊號。
        }

        // ── 與評測的等價性 ──────────────────────────────────────────────────

        [Fact]
        public void 大小寫不同視為沒給過_與評測的幻覺判定一致()
        {
            var given = new[] { Hit("inv/stock-ledger.html", "庫存 › 庫存帳本", "全文") };

            var one = Assert.Single(
                QaCitationPresenter.Build(new[] { "INV/Stock-Ledger.html" }, given));

            // ManualGenerationBenchmarkTests 判幻覺用的是 given.Contains(c)（序數、區分大小寫）。
            // 這裡若放寬成不分大小寫，畫面會把評測記為幻覺的那一則當成可查證的引文放行，
            // 於是**量到的品質與使用者看到的東西從此不是同一件事**。
            Assert.Equal(QaCitationKind.Unverifiable, one.Kind);
        }

        [Fact]
        public void 依據的是進了prompt的那幾塊_不是檢索回傳的全部()
        {
            // 模擬「檢索給了兩塊，但字數預算只放得下第一塊」：呼叫端傳進來的 given
            // 只有第一塊，第二塊模型根本沒看到——它就該被判成無法查證。
            var hits = new[]
            {
                Hit("inv/stock-ledger.html", "庫存 › 庫存帳本", new string('字', 5900)),
                Hit("inv/inventory-aging.html", "庫存 › 庫齡", new string('字', 500)),
            };
            var given = QaPromptBuilder.SelectWithinBudget(hits);
            Assert.Single(given);

            var result = QaCitationPresenter.Build(
                new[] { "inv/stock-ledger.html", "inv/inventory-aging.html" }, given);

            Assert.Equal(QaCitationKind.Manual, result[0].Kind);
            Assert.Equal(QaCitationKind.Unverifiable, result[1].Kind);
        }

        // ── 語系與順序 ──────────────────────────────────────────────────────

        [Fact]
        public void 英文答覆的手冊連結要帶語系參數_擷取物不帶()
        {
            var given = new[]
            {
                Hit("inv/stock-ledger.html", "Inventory › Ledger", "text", "en"),
                Hit("rules/currency-04.html", "Currency › Direction", "text", "en"),
            };

            var result = QaCitationPresenter.Build(
                new[] { "inv/stock-ledger.html", "rules/currency-04.html" }, given, "en");

            Assert.Equal("/docs/user/inv/stock-ledger.html?lang=en", result[0].Url);

            // 擷取物就地展開，內容早已依語系檢索過（§10.4 硬過濾）——
            // 沒有網址可帶，也不需要帶。
            Assert.Equal(string.Empty, result[1].Url);
        }

        [Fact]
        public void 引文順序維持模型輸出的順序()
        {
            var given = new[]
            {
                Hit("a/one.html", "A", "1"),
                Hit("b/two.html", "B", "2"),
            };

            var result = QaCitationPresenter.Build(new[] { "b/two.html", "a/one.html" }, given);

            Assert.Equal("b/two.html", result[0].PageCode);
            Assert.Equal("a/one.html", result[1].PageCode);
        }

        // ── 傳輸格式 ────────────────────────────────────────────────────────

        [Fact]
        public void Kind序列化成字串而不是數字()
        {
            // 畫面比對的是 c.kind === 'Rule'。本專案沒有全域註冊字串列舉轉換器，
            // 少了 QaCitationKind 上的 [JsonConverter] 就會序列化成 0/1/2 ——
            // 數字永遠不等於 'Rule'，於是**每一則引文都掉進「無法查證」那一支**，
            // 把正常的手冊連結全部標成幻覺。沒有例外、沒有紅燈，只有一頁看起來像模型在亂編。
            var json = JsonSerializer.Serialize(
                new QaCitationView("rules/x.html", "H", "T", string.Empty, QaCitationKind.Rule));

            Assert.Contains("\"Rule\"", json, StringComparison.Ordinal);
        }
    }
}

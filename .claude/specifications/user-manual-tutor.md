# 教學系統（Guided Tutorial）討論稿 v0.1

> 狀態：**主體已實作（2026-08-12，PR #91）**——`wwwroot/docs/user/tutor/`（`index.html`／`player.html`／
> `tutor.js`／`hotspots.json`／`hotspot-selectors.json`）＋守門測試 `TutorCourseFacts`、
> `UserManualTutorTests`、`HotspotSelectorTests`。原「討論中，尚未實作」已過期，於此更正。
>
> 本文的目的是把「依系統作業程序逐步教學＋畫面操作演示＋語音說明」
> 這個目標拆成可裁決的項目，標示每一項的取捨與我的建議。
> 讀完後需要您裁決的項目集中在最後一節〈待裁決〉——
> **截至 2026-08-13 僅剩 #1 未決**（#4 經查證已隨手機雙語一併刪除，見該項），其餘已結案並就地註記。

---

## 1. 背景：教學內容其實已經寫過了

探索既有手冊後，最關鍵的發現是——**這個教學系統要教的東西，九成已經以機器可讀的形式存在**，
只是散在三個地方：

| 教學系統需要的東西 | 手冊裡既有的載體 | 現況數量 |
| --- | --- | --- |
| 「一條作業程序有哪些站別、順序為何」 | 12 頁模組目錄的流程圖 `<div class="flow-row">` | **71 條**（69 + 2 條 alt 替代路徑），共 **322 個節點**，每條 3–7 站 |
| 「每一站要做什麼，逐步驟」 | 147 頁手冊內文的 `.step-row` + `.step-badge` | **480 個雙語步驟** |
| 「畫面長什麼樣」 | 手冊截圖（中英各一套） | **276 張**雙語截圖 |
| 「中英切換」 | `doc-i18n.js` + 純 CSS 後代選擇器 | 已有，複製節點即免費繼承 |

真正缺的只有一維：**「這個步驟指向畫面上的哪個元素」**（熱點座標）。

因此本案的定位不是「寫一套教材」，而是**「把既有手冊在執行期組裝成課程」**。
這也決定了第 2 節的決策 2（內容完全衍生自手冊）——教材只有一份真相來源，
手冊改了教學就跟著改，不會出現第三份會漂移的副本。

### 為什麼不做真畫面導覽

先講清楚被排除的路：業界常見做法是在真實 MES 畫面上掛 `data-tour` 錨點、用導覽套件逐步高亮。
本案不走這條，理由有三：

1. 教學若跑在真畫面上，「新增→儲存」這種步驟會**寫進真資料**；要避開就得做沙箱資料集或唯讀模式，那是另一個量級。
2. 全站要埋錨點，會踩到 Cx8（零容忍禁止 inline `on*=`）與 R4 等一致性規則，且每次改版面就斷。
3. 手冊截圖已經存在且是雙語的，等於錨點的替代品——只是錨在圖片座標而不是 DOM。

---

## 2. 已定案的四個決策（您已裁決）

| # | 決策 | 同時排除了什麼 |
| --- | --- | --- |
| 1 | **截圖模擬演示**：教學頁在手冊區，用既有雙語截圖＋熱點高亮＋逐步字幕 | 不碰真實 MES 畫面、不埋 `data-tour`、不寫任何真資料 |
| 2 | **內容完全衍生自手冊**：不新寫任何一句步驟文案 | 沒有第三份教材、不需要教材編輯後台 |
| 3 | **語音用瀏覽器內建 `speechSynthesis`**，可開關 | 不產生音檔、不呼叫外部 TTS（也就不必動 CSP 的 `connect-src`） |
| 4 | **先做一條 pilot**：`pur/index.html` 的「採購主流程」 | 不一次上 71 條 |

Pilot 這條的實際內容（已從檔案量測）：

```
MRP 執行(MFG,跨模組)  →  請購單  →  採購單  →  廠商交貨(外部事件,無畫面)  →  進貨單
    14 步驟/5 截圖        5 步驟/1 截圖   6 步驟/1 截圖         —              5 步驟/1 截圖
```

5 站、30 個步驟、8 張截圖（中英皆存在）。其中 1 站跨模組、1 站沒有畫面——
這正好是第 3 節陷阱 C的縮影。

> **2026-08-11 更新**：MRP 執行原本是 8 步驟／1 張列表截圖，閘門量測顯示它的可高亮步驟是
> **0–1／8**——8 個步驟講的全是側欄與明細頁裡的事，而截圖是列表頁。已補拍
> 「新增側欄／匯入訂單／草稿的執行 MRP／展開結果明細」四張（中英共 8 檔）並改寫步驟，
> 現為 14 步驟／5 截圖。補拍過程另外發現原步驟**描述了不存在的功能**（見第 3 節陷阱 D）。

---

## 3. 四個會決定成敗的陷阱（討論事項 A–D）

前三點是探索過程中「我原本的假設被檔案推翻」的地方，若不先設計進去，做出來會是錯的。

### A. 流程圖不是線性序列，有「擇一」與「分支條件」

`flow-steps` 底下不是只有 `.flow-node`，還有：

* `.flow-plus`（**擇一**）：全站 **56 處**，光 `inv/index.html` 就 15 處。
  例如「進貨單｜委外收貨｜完工入庫｜銷退單 → 上架入庫」——四選一，不是四個都要做。
* `.flow-cond`（**分支條件**）：例如「合格 →」「不合格 →」。
* `.flow-node.evt`（**外部事件**，無 `href`）：例如「廠商交貨」，沒有畫面可演示。
* `.flow-row.alt`（**替代路徑**）：2 條。

**若照我原本的想法「篩出所有 `.flow-node` 依序播放」，會教出一條系統裡不存在的流程，而且完全沒有訊號。**

> **建議做法**：把 `flow-steps` 的子節點當成一個**序列文法**來解析（node / plus / arrow / cond 都是 token），
> 而不是 `querySelectorAll('.flow-node')` 一把抓。遇到 `.flow-plus` 群組時，播放器停下來讓學員選一條分支；
> 遇到 `.evt` 則出一張「外部事件卡」（無截圖、只有字幕與語音），明白告訴學員「這一步不在系統裡發生」。

### B. 中英截圖不是同一張版面，一組座標無法共用

實測 `pur/img/purchase-order.png`（中）與 `pur/img/en/purchase-order.png`（英），兩張都是 1440×900，但：

* 英文側欄標籤換行成兩行，導致主內容 **y 位移最多 +156px**；
* 中文顯示 3 筆資料列、英文 4 筆；
* 表頭 x 座標差約 **21px**。

**一組座標套兩張圖，英文版的高亮框會指錯地方。**

另外 `doc-i18n.js` 對英文圖有 `onerror` 退回中文圖的機制——
**英文圖缺檔時畫面上顯示的其實是中文圖，此時必須用中文座標**，否則又錯一次。

> **建議做法**：`hotspots.json` 每個步驟存 `{ zh: {...}, en: {...} }` 兩組座標，
> 且座標一律**正規化為 0–1 比例**（不存像素），播放器再乘上實際 render 尺寸；
> 語言切換時同時換圖與換座標；並在 `img.onerror`（或 `naturalWidth` 比對）觸發時強制回落到 `zh` 座標。

### C. 大多數步驟其實「沒有東西可以高亮」

147 頁裡有 **133 頁只有 1 張截圖**，而且一律是該畫面的 Index 列表頁。
以 `pur/goods-receipt.html` 為例，4 個作業步驟裡**只有 1 個**指向列表頁上看得到的控制項——
其餘分別發生在 offcanvas 側欄內、另一個畫面、或另一個模組。

**如果把賣點定位成「逐步驟高亮」，那 70–80% 的步驟會退化成「同一張圖播四次配字幕」，體感很差。**

> **建議做法**：把價值主張改成 **「跨畫面的流程串接」**——
> 教學系統真正提供、而手冊沒提供的是「請購→採購→進貨這條路怎麼一路走下來、哪一步接哪一步」。
> 高亮只在該步驟**確實指向列表截圖上的某個控制項**時才標；pilot 預估只會有 **約 8 組座標**。
> 沒有熱點的步驟就正常播圖＋字幕＋語音，不硬湊框。

### D. 手冊的步驟文字可能描述不存在的功能（補 MRP 截圖時發現）

替 MRP 執行補截圖時，為了讓步驟與畫面一字不差而去讀了 `_Form.cshtml` 與 resx，
結果發現原本的三個步驟裡有兩個對不上實作：

| 原手冊文字 | 實際 |
| --- | --- |
| 「選擇需求來源（**訂單/區間**）後點『**執行**』」 | 沒有「區間」；來源只有「匯入訂單」，按鈕字是「**執行 MRP**」 |
| 「**勾選要落實的建議並『轉單』**，系統建立正式工單/請購單/委外採購單」 | **沒有轉單這個動作**。勾選發生在「匯入訂單」視窗（挑要排的訂單明細），而「執行 MRP」直接展開成生產批/工單/派工，需外購的部分**自動**建請購單 |

也就是說手冊描述的是一套「先出建議、再人工轉單」的兩段式 MRP，而實作是一段式直接展開。
已依實際 UI 改寫。

**這對教學系統的意義**：教學完全衍生自手冊（決策 2），所以**手冊錯，教學就會照著錯**，
而且因為畫面上找不到「轉單」，學員只會覺得是自己找不到。
補截圖的副作用是它會逼出這類落差——標熱點時選擇器選不到元素就是硬訊號。
其餘 70 條課程鋪開前，應該預期還會撞到同類問題。

**另發現一處手冊缺口**：`_ExecuteResult` 的訊息指向「請購轉採購」畫面，
但手冊 `pur/` 底下沒有這一頁（11 頁裡沒有 `pr-to-po.html`），所以只能寫成純文字不能連結。

---

## 4. 這件事會動到 RAG，而且不是選配（討論事項 E）

教學頁放在 `wwwroot/docs/user/tutor/`，而 `DocIndexBuilder` 掃的是 `docs/user/**/*.html`，
**沒有排除機制、也不吞例外**；`ManualDocParser` 在找不到 `main.content` 時會**丟例外**。

也就是說：**只要在 docs/user 底下放一個沒有 `main.content` 的頁面，整個索引建置會中斷，Q&A 跟著掛掉。**
這不是「要不要順便索引」的問題，是「不處理就會壞」。

因此：

1. 教學頁一律**靜態優先、動態其次**：`player.html` 內要有真實的 `main.content`（課程說明、站別清單），
   JS 只是在其上疊互動層。這樣 parser 有東西可解，索引到的內容也有意義。
2. `tutor/` 這個目錄沒有對應的 MVC controller，`MenuKey` 會是空的，
   會落進權限過濾的「模組目錄頁」分支——而該分支的可讀模組集合由使用者 MenuKey 推得，
   **永遠不可能包含 `tutor`**。結果是索引得進去、每個使用者都檢索不到，且毫無訊號。
   這與 `mobile/` 當初遇到的是同一個坑，已經有白名單機制：`ManualCorpusPolicy.PublicModule`。

**必要的程式改動（5 個檔案）：**

| 檔案 | 改動 | 為什麼會卡住 |
| --- | --- | --- |
| ✅ `Modules/QA/Contracts/ManualCorpusPolicy.cs` | `PublicModule` 單一 `const string` → `PublicModules` 集合 | 陣列不能是 `const`，須改 `static readonly` |
| ✅ `Modules/QA/Data/DocChunkFilter.cs` | 第 4 個 OR 分支 `Module = 'mobile'` → `IN ('mobile','tutor')` | `LangAndPermission` 原本是 `const string`，已降級為 `static readonly string`。**不可改用 `FromSqlRaw` 參數**：`{0}`～`{2}` 的編號是與所有呼叫端的約定，多塞一個會把每個呼叫端往後推一位。raw 字串仍用 `$$"""`（`{{expr}}` 插值、`{0}` 保持字面）；改成單 `$` 會 CS9006 |
| ✅ `SmartFactory.MES.Tests/QA/ManualReachabilityTests.cs` | 3 處（分支 4 比對、不重疊斷言、目錄存在斷言）改用 `Contains`；另補「無重複／無空字串／小寫英數字／長度 ≤10」 | `DocChunk.Module` 是 **varchar(10)**，`tutor` 剛好 5 字元，但新目錄可能超長；成員是字面拼進 SQL 的 |
| `SmartFactory.MES.IntegrationTests/QA/ManualIndexIntegrationTests.cs` | `ManualPages = 147 → 149`；`ManualChunks` 目前 619（2026-08-11 補 MRP 截圖後）→ 加 tutor 頁後的實測值 | 這兩個常數是**刻意寫死的絆索**，值須取自 `ManualCorpusFacts.量測_切塊粒度與字數分佈` |
| `wwwroot/docs/user/index.html` | 加教學系統入口 | 只改這一頁，避免動到 147 份側欄 |

> SQL 參數編號 `{0}{1}{2}` 不變（呼叫端從 `{3}` 開始編號），所以 `ManualSearchService.cs` **不需要改**。

---

## 5. 三個我已有建議的分岔（討論事項 F–H）

### F. 要不要替 71 個 `.flow-row` 加 `id`？——**建議：要**〔已完成 2026-08-11〕

課程 URL 需要穩定識別碼（`player.html?flow=pur-main-purchasing-flow`）。原本 `.flow-row` 沒有 `id`，
只能靠「第幾條」定位，手冊一改順序課程連結就全錯。
這是**唯一一項需要動既有手冊 HTML 的改動**：12 頁、71 個元素加一個屬性，不影響版面與既有測試。

**已採用的命名**：`flow-{模組目錄}-{英文標籤 kebab}`，根目錄總覽用 `main`
（例：`flow-pur-main-purchasing-flow`、`flow-mfg-run-the-plan`、`flow-main-order-to-cash`）。
中文標籤不能當 id，故取英文標籤；開頭的虛詞（from/to/and…）剝掉才讀得順。
`act/index.html` 的四條課程英文標籤是「Month-end ①～④」，**圈號是它們唯一的區別**，
直接剝非 ASCII 會撞成同一個 id，故先把 ①→`-1`。71 條全數唯一，`TutorCourseFacts` 已加不變式斷言
（id 數 = 課程數 = 相異 id 數）——這不是把首測值凍成門檻，是「少一個或撞一個就有課程永遠打不開」。

代價：id 由英文標籤導出，改標籤就會改 id，外部書籤會失效。可接受，因為課程清單是**執行期從 id 推導**的
（沒有第二份硬編清單會漂移），而 `hotspots.json` 以 id 為鍵的部分由守門測試比對，改名會紅燈而非靜默。

### G. 步驟要怎麼挑？——**建議：全收，並附段落標籤**

我原本打算用 `<h2>作業流程</h2>` 這個標題挑步驟。實測分布：

```
作業流程 177 (37%) ／ 查詢 131 (27%) ／ 操作步驟 87 (18%) ／ 其餘 85 散在 40+ 種標題
```

**照標題挑會靜默漏掉 63%。** 而且該 h2 的 `textContent` 是「作業流程Process」（雙語 span 並存），
字串比對本來就不會中。

> 改為：依文件順序收集**全部** `.step-row`，以最近的前一個 `h2`/`h3` 作為段落標籤顯示。
> 註：`.step-badge` 每個段落會從 1 重新編號，所以**步驟的鍵必須用文件順序索引**，不能用 badge 上的數字。

**取單一語系的字面不能用「抓第一個 `[lang=zh]`」**（我第一版就是這樣寫的，已修）。
手冊的一句話常被 `<b>`／`<a>` 切成好幾段 zh／en 交錯的 span，例如 mrp-run.html：

```html
<span lang="zh">系統會依該料</span><span lang="en">The system uses…</span>
<b><span lang="zh">計量單位的小數位無條件進位</span>…</b><span lang="zh">：PCS 為 0 位…</span>
```

只取第一段，旁白就唸成「系統會依該料」然後結束。
正解是照 `doc-i18n.css` 的顯示語意做：**移除／跳過 `[lang=en]`，其餘全部保留**
（不在任何 lang span 內的裸文字如 `111`、標點、空白，兩種語系都要顯示）。
`Modules/QA/Services/ManualDocParser.ParseOneLang` 是既有的正確參考實作（clone 後把另一語系整棵 `Remove()` 再走訪）；
RAG 語料沒有這個問題，`tutor.js` 要照同一套語意寫。

### H. 熱點座標怎麼來？——**建議：Playwright 產生，不手工維護**

座標若是手工填的輸入檔，畫面一改就**靜默錯位**（框還在，只是指錯地方），沒有任何測試會紅。

> 改為：人類維護 `hotspot-selectors.json`（CSS 選擇器，語意穩定），
> 由 `SmartFactory.MES.UITests` 內的一支 Playwright 測試在**同一次執行**中截圖並輸出 `hotspots.json`（正規化 0–1）。
> 選擇器選不到元素就**測試失敗**——把「靜默錯位」換成「明確的紅燈」。
> 放在 UITests 而不是獨立工具，是為了直接沿用 `UiTestFixture`（登入、強制改密碼、BaseUrl、page 回收都已處理）。

---

## 6. 檔案清單與實作順序

**新增 8 個檔案：**

```
wwwroot/docs/user/tutor/index.html              課程總覽（靜態，含 main.content）
wwwroot/docs/user/tutor/player.html             播放器（靜態骨架 + main.content）
wwwroot/docs/user/tutor/tutor.page.css
wwwroot/docs/user/tutor/tutor.js
wwwroot/docs/user/tutor/hotspots.json           ← Playwright 產出，勿手改
wwwroot/docs/user/tutor/hotspot-selectors.json  ← 人類維護
SmartFactory.MES.Tests/QA/TutorCourseFacts.cs           量測（不擋）
SmartFactory.MES.Tests/Services/UserManualTutorTests.cs 守門（擋）
SmartFactory.MES.UITests/Tutor/HotspotCaptureTests.cs   座標產生器
```

**修改 5 個檔案：**如第 4 節表格。

**順序（第 1 步是閘門）：**

1. ✅ **先寫 `TutorCourseFacts` 量測**：71 條課程的節點型別分布、每站步驟數、
   以及最關鍵的**「可高亮步驟佔比」**。
   **看到這個數字再決定要不要往下做**——若 pilot 的可高亮步驟低到只剩 1–2 個，
   代表決策 1（截圖模擬演示）的價值不成立，應該回頭重新選演示形式，而不是硬做完。
   〔閘門結果：pilot 30 步驟中約 8–9 步有可高亮的具名控制項（≈30%），過關〕
2. ✅ `.flow-row` 加 `id`（12 頁 71 條）＋ `ManualCorpusPolicy` / `DocChunkFilter` 改集合。
   〔白名單目前仍只有 `mobile`；`tutor` 要**和 `tutor/` 目錄同一批**進去，
   否則 `PublicModules_DirectoriesExist` 會紅——那是既有的綠燈守門，不該為了搶順序弄紅〕
3. ✅ 守門測試 `UserManualTutorTests`（8 條）。
4. ✅ `tutor/index.html` + `player.html` 靜態骨架（含 `main.content`）＋ `tutor.page.css`
   ＋ `tutor` 進白名單 ＋ 手冊首頁入口 ＋ 絆索常數 147/619 → **149/652**。
   〔總覽頁是**產生**的（`~/.cache/manual-tutor/gen_tutor_index.py`，該目錄有 README）。
   刻意靜態產生而非 JS 現渲染：索引器解的是 HTML 原始碼，看不到 JS 產物；
   而空殼頁會讓 `ManualDocParser` 丟例外、整個索引建置中斷。
   產生物與流程圖之間沒有執行期關聯，靠上面兩條守門綁住——測試紅了就是叫人重跑產生器〕
5. ✅ `tutor.js`：流程圖解析（序列文法）＋ 步驟抽取 ＋ 雙語 ＋ 語音 ＋ 熱點疊圖（座標檔缺席時自動略過）。
   〔三件實作上必須照著做的事寫在檔頭注釋。另加一個原規劃沒有的機制：
   **步驟↔截圖的對應本質上是推斷**（手冊慣例是截圖跟在它說明的那一步後面，
   規則＝同段落內往後找最近的截圖，找不到往前找，再找不到用該頁第一張），
   猜不準的個案用 `.step-row` 的 `data-shot="img/xxx.png"` 明確指定；
   已用於 mrp-run 的「儲存草稿」那一步。守門測試驗 `data-shot` 指到的中英兩版檔案都存在。
   驗證工具 `~/.cache/manual-tutor/tutorcheck`（有 README）——守門測試只驗靜態 HTML，
   播放器的失敗模式在 HTML 上一個都看不出來〕
6. ✅ `hotspot-selectors.json` ＋ 產生器產出 `hotspots.json`。
   **125 張截圖設定 × 中英＝238 張有座標、共 626 個框**；
   實測 71 條課程 1457 格中 **394 格有熱點（27.0%），71 條課程全部都有**
   （原本只配 pilot 8 張時是 72 格／15 條）。這個數字與第 1 步閘門估的「約 30% 可高亮」吻合。

   **改了原規劃兩處**：
   (a) 鍵是「截圖 × 控制項名稱」而不是「頁面 × 步驟序號」——用序號的話產生器得跟 `tutor.js` 一樣
   會解析步驟並推斷步驟配哪張截圖（同一套規則寫兩遍），而且手冊插入一步就全體位移。
   (b) 產生器（會寫檔）放在 `~/.cache/manual-tutor/hotspotgen`，**驗證**則放進
   `SmartFactory.MES.UITests/Tutor/HotspotSelectorTests.cs`（只驗不寫檔）。兩者共用同一份選擇器檔。

   **一筆移出 `shots` 進 `shotsNeedingFixture`**（2026-08-11 清理 dev 庫時）：
   `mfg/img/mrp-run-execute.png` 需要「一張有需求列的草稿」當前置資料才量得到
   （`_Form.cshtml` 用 `@if (!isNew && isDraft)` 包住「執行 MRP」，新增側欄裡永遠沒有那顆鈕）。
   當初建的 `MRP-2608-00016` 已依使用者要求刪除，留著設定會讓產生器與 UITests 一起紅。
   **截圖仍在、教學照播，只是這一步沒有高亮框**（pilot 從 12/31 變 11/31）。
   復原方式寫在該筆的 `_why` 裡：用 panelshot 的 `setup-shots.json` 重建草稿即可。

   **`shots` 的 121 筆 Index 是機械產生的**：`HelpDocResolver` 的 controller→模組表決定手冊頁與截圖檔名，
   route 就是 `/{Controller}`；121 個 controller 全部都有手冊頁與中英截圖。
   另外 4 筆是側欄／彈窗，人工維護。

   **defaults 的「找不到」不當錯**：`新增` 命中 102、缺 140（報表頁沒有新增鈕），
   `查詢` 命中 228、缺 14，`操作` 命中 172、缺 68——那是內容事實。
   但**一個 default 若哪裡都選不到就是選擇器壞了**，那個要當錯。這條分流讓廣泛套用不會犧牲掉壞掉的訊號。

   守門分三層：靜態（`UserManualTutorTests`：座標在 0–1 內、鍵指向存在的截圖、選擇器檔格式完整、
   覆蓋率只量測不擋）／瀏覽器（`~/.cache/manual-tutor/tutorcheck`：框畫在圖上）／
   真畫面（`HotspotSelectorTests`：選擇器還選得到、位置偏移 ≤1%）。
7. ✅ 熱點疊圖 ＋ `speechSynthesis` 開關（含 `getVoices()` 為空時的降級）。
8. 更新兩支索引測試常數、跑整合測試。

### 6.1 上線後回報的三件事（2026-08-12）

**(a) `doc-i18n.js` 的 `decorateLinks()` 吃掉連結自帶的 query — 教學課程卡全部失效。**
使用者回報「課程總覽裡沒有 `?flow=` 的連結」。靜態 HTML 是對的（71 個連結都帶 `flow=`），
但 `decorateLinks()` 為了讓站內連結沿用語言而重寫 href，舊寫法 `href.split('?')[0]` 取 base 後
接上 `?lang=en`，於是 `player.html?flow=…` 變成 `player.html`。
**教學課程卡是全手冊第一批自己就帶參數的連結**，所以這個既有寫法一直沒被觸發。
修法：只置換 `lang`、其餘參數原封保留（濾掉舊 `lang` 才不會重複套用時累積）。

*為什麼三層守門一個都沒攔到*：靜態測試看的是原始碼（正確），`tutorcheck` 每條課程都直接
`goto player.html?flow=…`——**繞過了使用者真正走的入口**，而繞過的那一段正是壞掉的地方。
已在 `tutorcheck` 開頭補「從總覽點卡片進來」（中英各一次：驗 href、驗導向後 URL、驗播放器有渲染），
並實測把 bug 放回去會紅（6 個問題）。

**(b) `/docs/**` 沒有 `Cache-Control`，改版後新舊混搭。**
`UseStaticFiles` 預設只送 ETag/Last-Modified，瀏覽器改用啟發式快取——**愈久沒改的檔案被快取愈久**。
上面 (a) 修好後使用者仍看到舊行為，就是這個原因。
`Modules/_Shared/ManualDocsCacheModule.cs`（`IAppModule`，**不改 `Program.cs`**）對 `/docs` 送 `no-cache`：
仍會快取、仍走 ETag 重驗（實測回 304、傳輸 0 bytes），只是不准直接用快取。
可行的關鍵是 `Program.cs` 先跑完所有模組的 `Configure`（行 283）才 `UseStaticFiles`（行 340）。

**(c) 連續播放。**
課程卡另補了播放圖示與連結色——原本 `.tutor-card-title` 是一般深色粗體字，不 hover 看不出可點。

播放器新增「連續播放」。**節拍有兩種來源，開了語音就等它唸完**：固定秒數必然跟朗讀打架
（短句乾等、長句被切斷）；沒有語音時才用字數估閱讀時間（`2200 + 字數 × 120ms`，夾在 3–15 秒）。
**任何手動操作都停**（上一步／下一步／點站別／方向鍵）——規則單純才好預期。

兩個實作上的坑：
* **世代計數 `state.gen` 是必要的**。每次排程都留下兩個未來會觸發的東西（utterance 的 `end`、
  保險用的 `setTimeout`），而 `speechSynthesis.cancel()` 在多數瀏覽器**也會觸發 `end`**。
  少了世代檢查，手動切格或關語音時舊回呼會補一腳，畫面表現為「自己跳了兩格」，且當下沒有任何錯誤。
  `invalidate()` 必須在 `render()` 開頭呼叫——早於 `narrate()` 裡的 `cancel()`。
* **`end` 不保證會來**（分頁切到背景、文字過長時瀏覽器可能靜默中止朗讀），所以仍留 40 秒保險上限，
  否則連續播放會停在半路而看不出原因。

**(d) 手機（直式）截圖在播放器裡過大。**
手機截圖是 390×844 以 2 倍截成 780×1688；`.tutor-stage img{width:100%}` 撐滿內容欄後高達 **1660px**，
一格看不完、控制列被推到捲軸外。手冊的 `mobile/screen.page.css` 早就為手冊頁解過同一題（壓到 300px 寬），
但播放器沒有繼承那條規則。

修法與三個判斷：
* **依實際長寬比判斷，不看路徑有沒有 `mobile`**：路徑規則會在有人把手機截圖放到別的模組時靜默失效，
  而長寬比就是「這張圖是不是手機畫面」本身。`fitStage()` 在 `setShot()` 與 img 的 `load` 都呼叫
  （前者處理已快取的圖，後者處理首次載入）。
* **上限套在 `.tutor-stage` 而不是 `img`**：熱點是 stage 內的絕對定位、座標以 `img.clientWidth` 換算，
  只縮 img 會讓圖與 stage 不再重合，框整個偏掉。
* **綁高度不綁寬度**：手冊頁只要能捲就好，播放器不行（控制列在圖下方）。
  300px 寬換算仍有 649px 高，過不了這關。取 `min(52vh,500px)` 是為了對齊桌機截圖的約 480px——
  同一條課程裡直式橫式交替，高度差太多控制列會一格一格上下跳。

`tutorcheck` 每格加驗「stage 高度 ≤ 視窗六成」（桌機實測 48%、手機 50%、未壓縮 165%，分得很開）。

`tutorcheck` 補了對應守門，兩條都實測會紅。**第二條一開始是沉默通過的**：
它等 9 秒後檢查格數有沒有再前進，但單格停留上限是 15 秒——長步驟本來就還沒到換格時間，
等於在量一段什麼都不會發生的空窗。改成「先驗按鈕狀態（即時、確定），再等 16 秒驗格數」。

---

## 7. 守門測試的取捨原則

規劃 14 支測試，分兩類，界線是明確的：

* **使用者看得到的錯 → 擋 build**：流程圖結構壞掉、`.flow-row` 缺 `id`、
  `hotspots.json` 有座標落在 0–1 之外、選擇器選不到元素、`tutor` 沒進白名單、
  頁面缺 `main.content`（會炸掉整個索引建置）。
* **規模變化 → 絆索常數**：課程數、頁數、chunk 數——改變時要求人明確更新數字。
* **內容品質（如熱點覆蓋率）→ 只量測不擋**：覆蓋率低是內容問題，不是程式缺陷，
  擋 build 只會逼人塞假座標。

測試方法名用中文（與既有 `ManualCorpusFacts` 一致）；
`ManualReachabilityTests` 維持英文方法名（該檔既有慣例）。

---

## 8. 驗證計畫

| 層 | 內容 |
| --- | --- |
| **L0** | 靜態守門測試（第 7 節） |
| **L1** | Playwright 開啟播放器，斷言 **console 無 CSP 違規**。<br>提醒：CSP 是 `style-src 'self'` 且**完全沒有 nonce**，所以動態尺寸一律用 **CSSOM 屬性指派**（`el.style.left = ...`），不受 style-src 管；不可注入 `<style>` 或 style 屬性字串。 |
| **L2** | 端到端跑 `http://127.0.0.1:5111/docs/user/tutor/player.html?flow=pur-main`（`/docs/**` 匿名可讀，**不需登入**）：<br>① 走完 5 站；<br>② 每張截圖 `naturalWidth > 0`（這一條專門抓 **DOMParser 沒有 base URL** 的坑——解析出來的文件相對路徑會對著播放器頁解析成 `/docs/user/tutor/img/x.png` 而 404，必須改用 `getAttribute('src')` + `new URL(raw, new URL(pagePath, location.href))`，且**不要注入 `<base>`**）；<br>③ 「廠商交貨」出現外部事件卡；<br>④ 切語言後**圖片 src 與熱點 boundingBox().X 同時改變**；<br>⑤ 熱點位置與 `hotspots.json`×render 尺寸誤差 < 2px；<br>⑥ `getVoices()` 為空時（CI 無語音引擎）優雅降級不報錯。 |
| **L3** | `ManualIndexIntegrationTests` 綠、Q&A 檢索得到 tutor 內容 |

---

## 9. 待裁決

> 📌 **本節由 R29 與 `STATUS.md`〈真正未完成〉雙向對帳**：沒劃掉的必須在那份清單上，
> 劃掉的不得留在那份清單上。**序號即識別碼**（`STATUS.md` 是以「§9 #1」這種寫法引用的），
> 所以結案的項目一律**原地劃掉、保留序號與理由**，不要刪除或重新編號。

<!-- open-items ref="user-manual-tutor.md §9" -->

1. **第 1 步閘門的數字若不理想怎麼辦**——我會停下來回報，不會硬做完。請確認這個處理方式可接受。
2. ~~**要不要在系統主選單放教學入口**~~〔2026-08-12 已裁決：**不放進系統 navbar**〕
   教學入口維持只在 `wwwroot/docs/user/index.html`。
   連帶效果：不動 **Core 的 `_Layout.cshtml`**，因此不需要 Core-first 流程，
   漂移基準線也不受影響——這正是原本建議 pilot 階段先不做的理由。
   （原文保留：若日後改為放進 navbar，須先 commit+push 到 Core → 部署專案 `git fetch upstream main` → 再 commit。）
3. ~~**既有 56 個未提交檔案**（手機手冊 13 頁 + i18n）要不要先提交？~~
   〔2026-08-12 已結案：**照原建議做了，而且沒有等到 S3 跑完**——`8285eca`
   （手冊教學系統＋手機手冊 13 頁）一次提交 49 個相關檔案。原本擔心的「污染評測資料」
   沒有發生，因為評測讀的是索引後的 chunk，不是工作樹狀態。〕
   原文保留：我先前的建議是等 S3 選型評測跑完再併 main，以免污染評測資料。
4. ~~`wwwroot/mobile/js/home.page.js`、`wwwroot/mobile/js/index.page.js` 兩支死檔要不要刪。~~
   〔2026-08-13 查證已結案：兩支**早在 `a0641be`（手機 PWA 全站中英雙語）就一併刪掉了**，本項無事可做。〕
   ⚠️ 別跟 `wwwroot/mobile/home.page.js`、`wwwroot/mobile/index.page.js` 搞混——
   那兩支**沒有 `js/` 這層**、目前仍活著，由 `home.html:95`／`index.html:93` 引用
   （`79ae1ec` 為相容 nonce CSP 把 inline script 外部化到那裡）。誤刪會讓兩個手機入口頁直接失去行為。
5. ~~**合併順序**~~〔2026-08-11 已結案〕：[PR #89](https://github.com/TsaiFisher/ProjectZ/pull/89) 已進 main，
   **用 merge commit 合的、沒有壓縮**（merge commit `ad51ac7`，分支頂端 `661917d` 仍是 `origin/main` 的祖先），
   所以原本擔心的 squash 斷祖先沒有發生——`git rebase main` 就能正常算 base，不必動用 `--onto`。
   `ManualIndexIntegrationTests.cs` 那幾個 hunk（commit `129d943`：專屬角色 `QA-INDEX-TEST` ＋ 每 5 題重驗前提）
   現在在 main 上，rebase 若仍跳衝突就取 theirs。對方已改用專用測試庫，`SmartFactoryMES_Test` 不需要再閃避。
   另外 PR #88 也合了（`67301bc`）：CI 與 nightly 的 Jest 執行環境升到 Node 24，拉 main 後 CI 就是 24。
6. ~~熱點選擇器失效不會在 CI 自動紅~~〔2026-08-11 已補上，採選項 (b)〕：
   `SmartFactory.MES.UITests/Tutor/HotspotSelectorTests.cs` 只驗不寫檔——
   逐一開畫面、確認選擇器仍選得到，並比對框的位置與記錄值偏移 ≤1%。
   本機實測 **256 個熱點、1m15s 全過**（`BaseUrl=http://127.0.0.1:5111 dotnet test --filter HotspotSelectorTests`；
   UITests 的授權指紋／`DefaultEncrypted` 前置由「被測 app 已經跑得起來」滿足，測試端只需 `BaseUrl`）。
   測試本身有一條 `checkedCount > 0` 的斷言——**通過但一個都沒檢查等於沒在保護任何東西**。
7. ~~**「查詢」vs「搜尋」用字不一致**（產出熱點時撞到的）。~~
   〔2026-08-12 已裁決並補守門：**一律用「查詢」**，由一致性規則 **R27**
   （`ConventionTests.R27_ZhResx_UsesQueryWordConsistently`）守住——zh-TW resx 的值
   不得再出現「搜尋」。下面那段「還沒解掉本項」的分析是裁決前的狀態，保留為決策脈絡。〕
   我原本的計數（Index.zh-TW.resx 70 vs 45）只是切片，另一個 session 量了全庫，事實更尖銳：
   **同一個 resx 鍵 `Search` 有兩種中譯**——72 支譯「查詢」、58 支譯「搜尋」，
   另有 20 支用 `SearchLabel` 鍵譯「搜尋」，**12 支檔案內部兩種譯法並存**；
   placeholder 也分裂（26 檔「按『搜尋』」／15 檔「按查詢」）。手冊則有 116 頁真的在內文教「點『查詢』」。

   **但檢索沒有壞**——對方在 563 chunk 的實際索引上量過名次：「查詢按鈕在哪裡」與「搜尋按鈕在哪裡」
   的 top-1／top-3 集合**完全相同**，只差 0.04–0.05 分。原因是 `snowflake-arctic-embed2` 是多語模型，
   兩詞在它的空間裡本來就近，而且預設 `semantic-first`（定案 #27），詞彙通道只在語意湊不滿 topK 時補位。
   → 他們不加同義詞層（定案 #45）。

   所以這一條**留著，但理由是單純的產品一致性，不是「不改會壞掉檢索」**。
   教學這邊用別名繞過（兩種寫法查得到同一個框），影響面是 58 支 resx 或 116 頁手冊
   （英文兩者都是 `Search`，無此問題）。

   **〔2026-08-12 部分裁決〕手機版與電腦版有差異時，一律以電腦版的內容為準。**
   這條原則涵蓋手冊、教學與介面用字，手機端不另立一套說法。

   ⚠️ **但它還沒解掉本項**：分裂發生在**電腦版自己內部**（同一個 `Search` 鍵，72 支 resx 譯
   「查詢」、58 支譯「搜尋」，12 支檔案內部兩種並存），不是手機與電腦之間的差異。
   「以電腦版為準」在這裡指不到唯一答案，仍須挑定一個詞再全庫收斂。
   **現有證據偏向「查詢」**：手冊有 116 頁在內文教「點『查詢』」，選它的話手冊那一半不用動。
   （後續：**裁決結果正是「查詢」**，理由與這裡的推論一致。）

<!-- /open-items -->

---

### 附註：不需要處理的事

* `wwwroot/docs/**` **在所有 R/Cx 一致性掃描範圍之外**（R4 只掃 `wwwroot/js/*.js` 頂層、非遞迴），
  也**不在 Core 漂移基準線內**——教學系統住在這裡不需要 Core-first、不會觸發框架摩擦。
* `/docs/**` 由 `UseStaticFiles()`（`Program.cs:340`）在 `UseAuthentication()`（:415）之前處理，
  無 FallbackPolicy、無全域 AuthorizeFilter，**匿名可讀**。原始 HTML 本來就是公開的，
  教學系統不增加任何新的資訊暴露面。

# 跟上更新

本專案的預期用法是：**你 fork 一份，照自己的需要改，然後定期把上游的更新拉進來。**

上游會修 bug、補功能；**你自己改的部分由你自己維護**，合併衝突也由你自己解。
這是刻意的分工——沒有人比你更清楚你為什麼要那樣改。

---

## 第一次設定

```bash
git clone https://github.com/<你的帳號>/gemba-mes.git
cd gemba-mes
git remote add upstream https://github.com/TsaiFisher/gemba-mes.git
```

---

## 拉更新

```bash
git fetch upstream
git merge upstream/main          # 或 git rebase upstream/main
```

上游的發布是**一個 release 一個 commit**（不是逐筆同步），所以：

- 你會看到一個很大的 commit，但 commit 訊息裡有該版的 CHANGELOG 摘要
- 衝突集中在你改過、上游也改過的檔案
- 你完全沒動過的檔案不會有衝突

---

## 怎麼改衝突會最少

這套系統本身就是為了「上游持續演進、下游各自客製」而設計的，照著它的結構走
可以把衝突降到很低：

### ✅ 盡量這樣做

- **新功能放在 `Modules/<你的模組>/` 底下的新檔案。** 新檔案永遠不會衝突。
- **服務註冊走 `IAppModule`**，不要改 `Program.cs`。
  實作一個 `IAppModule` 放進你的模組，啟動時會自動掃描載入。
- **新的資料表走 `Data/ApplicationDbContextExtensions.cs`**，
  不要改 `Data/ApplicationDbContext.cs`。
- **新增畫面照既定步驟登錄**（MenuContributor + 兩份 resx + 權限鍵）。
  漏掉會出現「畫面做好了但選單沒有、或欄位驗證錯誤不顯示」這類難查的症狀。

### ⚠️ 會製造衝突

- 直接改 `Program.cs`、`Data/ApplicationDbContext.cs`、`Views/Shared/_Layout.cshtml`
- 修改既有 Controller 或 Service 的內部邏輯（而不是繼承或替換註冊）

真的需要改核心檔案時，把改動縮到最小、集中在一處，並在該處留下註解說明原因——
下次合併衝突時，那段註解就是你自己需要的說明。

---

## 資料庫移轉

你自己的 migration 與上游的 migration 會在
`Migrations/ApplicationDbContextModelSnapshot.cs` 上衝突。

**解衝突時不要挑「哪一行看起來對」，要重新產生 snapshot：**

```bash
# 先解掉 Migrations/*.cs 的衝突（各自保留兩邊的 migration 檔）
# 再讓 EF 重新產生 snapshot：
dotnet ef migrations add MergeUpstream --project SmartFactory.MES.csproj
```

手動編輯 snapshot 幾乎一定會留下看不出來的偏差，而症狀會延遲到下一次
`dotnet ef database update` 才出現。

---

## 送修改回上游

你不必回饋任何修改——Elastic License 2.0 不要求。

但如果你修掉的是**上游本身的 bug**，歡迎送 PR，那會讓下一次合併更輕鬆。
送出前請先看 [`CONTRIBUTING.md`](CONTRIBUTING.md)（本專案要求 CLA）。

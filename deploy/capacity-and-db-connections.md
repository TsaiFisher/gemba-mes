# 尖峰容量與資料庫連線設定

> 2026-10-21 換班尖峰壓測（`SmartFactory.MES.IntegrationTests/Load/ShiftChangeLoadTests.cs`）的結論與部署建議。
> 量測環境：WSL 開發機（PostgreSQL 16、`max_connections = 100`）、WebApplicationFactory 走完整 HTTP 管線。
> 數字是**同一秒全部起跑**的最壞情況；真實換班會分散在幾分鐘內，餘裕比這裡大。

## 一句話

**Npgsql 的 `Maximum Pool Size` 一定要比 PostgreSQL 的 `max_connections` 小，而且要扣掉同一台資料庫上其他連線池。**
預設兩者都是 100，尖峰時 app 自己就把資料庫連線用光，請求不是排隊而是直接 500（`53300: too many clients already`），
連管理者用 psql 都連不進去。

## 建議設定

連線字串加上 `Maximum Pool Size`（加密連線字串見 `encrypted-connection-string.md`，要重新加密）：

```
Host=…;Database=…;Username=…;Password=…;Maximum Pool Size=40
```

估算方式：

```
Maximum Pool Size ≤ (max_connections − superuser_reserved_connections − 其他程式的連線) ÷ 同一台資料庫上的連線池數
```

- 同一個 app 有兩個 DbContext 就是兩個池（ApplicationDbContext；手冊問答 QaDbContext 有自己的連線字串時另成一池）。
- 穩定版與開發版共用同一台 PostgreSQL 時兩邊都要算進去。
- 池滿時請求會**排隊**等連線（`Timeout`，預設 15 秒），不會失敗；排隊比失敗好，失敗會讓現場重按、產生重複。

## 量測結果

| 情境（同一秒起跑） | 連線池 | 5xx | 重送後仍失敗 | 總耗時 | 最慢一類 p95 |
|---|---:|---:|---:|---:|---:|
| 60 人（10 線×每班 3 人），修正前 | 200 | 20 | 19 | 28.6 s | 24 s |
| 60 人，修正前 | 40 | 0 | 1 | 43.8 s | 14 s |
| 60 人，ETA 合併後 | 100 | 0 | 0 | 2.3 s | 0.8 s |
| 300 人（30 線×每班 5 人），ETA 合併後 | 100 | 74 | 104 | 36.7 s | 11 s |
| 300 人，＋使用者非同步載入 | 40 | 0 | 59 | 16.0 s | 2.4 s |
| 300 人，＋伺服器端並發重試 | 40 | 0 | **0** | 13.2 s | 6.5 s |
| 60 人，＋伺服器端並發重試 | 40 | 0 | 0 | 2.3 s | 1.4 s |

死結在所有情境都是 0；派工完成數與所有被接受的報工數量逐張相符（沒有遺失更新、沒有重複計數）。

## 壓測找到並已修正的三件事

1. **報工後的 ETA 重算每筆各開一個背景工作**，而且會寫回同一張派工：作業員緊接著的下一個寫入撞 xmin 衝突（單人也會），
   每筆報工多佔一條連線。→ `ReportEtaNotifier` 改為同一張派工合併、單一背景迴圈依序處理（ProjectZ）。
2. **每個請求都在中介層同步查使用者**（`UserContext` 的屬性 getter），高並發時塞住執行緒池。
   → `UserContext.EnsureLoadedAsync`，`UserPreferenceMiddleware` 先 await（Core 462deb2）。
3. **同一條生產線多人同時報工，第一次送出四到五成撞 409**；手機直接送出時 409 不會自動重送，現場看到的是一片
   「資料已被其他人修改」。→ 上工／下工／報工／完工在伺服器端以最新資料重做最多 3 次（`MobileMfgController.WithConcurrencyRetryAsync`）。

## 評估後暫不做的（2026-10-21）

| 項目 | 結論 | 理由 |
|---|---|---|
| 桌機報工／完工也做伺服器端重試 | 暫不做 | 單人也會撞的主因（ETA 背景寫回）已修，且桌機手機共用；剩下要兩人同一秒用桌機改同一張派工，桌機多為組長補登。撞到時回 409 加友善訊息、資料沒存進去、重按不會重複 |
| 派工完成數改原子遞增 | 不建議 | 同一次存檔還會改狀態、更新時間、報廢量，那一列仍會換版本，只改這欄消不掉衝突；要消就得把報工整段改成不經 EF 的 SQL，牽動超報檢查、工序順序守門、自動完工、委外進度——核心路徑、改錯是靜默的數量錯。300 人同一秒已 0 失敗 |

**改做：把衝突記下來，用數字決定要不要回頭做。**

- 伺服器日誌（固定 EventId）：`7101` 手機重試後成功、`7102` 手機重試 3 次仍衝突（回 409，手機佇列會再重送）、`7103` 桌機衝突。
  ```bash
  journalctl --user -u mes-stable --since today | grep -c "伺服器重試.*後成功"
  journalctl --user -u mes-stable --since today | grep "仍衝突\|桌機"
  ```
- 現場遙測儀表板：API 表多一欄「衝突」（手機收到 409 的次數）、KPI 卡附「重試後仍衝突 N 次」。
- 回頭做的門檻（建議）：`7102` 每天都有、集中在某幾條線 → 做完成數原子化；`7103` 每天都有 → 桌機也重試。

## 怎麼重跑

```bash
LOAD_TEST=1 LOAD_LINES=30 LOAD_OPS_PER_LINE=5 \
ConnectionStrings__DefaultConnection="Host=localhost;Database=<專屬測試庫>;Username=dev;Password=…;Maximum Pool Size=40" \
dotnet test SmartFactory.MES.IntegrationTests --filter ShiftChangeLoadTests --logger "console;verbosity=detailed"
```

報告印在測試輸出，也寫到測試輸出目錄的 `load-report-*.md`。不要對共用測試庫跑：別人的測試同時在跑時，鎖等待與耗時都沒有意義。

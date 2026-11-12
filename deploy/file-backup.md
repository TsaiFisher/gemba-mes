# 檔案備份（報表檔與上傳檔）

> ~~**狀態：建議方案，尚未實作。**~~ → ✅ **2026-08-31 已實作於
> [`scripts/backup-db.sh`](../scripts/backup-db.sh)**（待決 #20 結案）。開發區仍不需要跑它。
> 用法：設 `REPORTS_PATH`（＝組態 `Reports:StoragePath`）與 `UPLOADS_PATH`
> （預設 `wwwroot/uploads`，設 `-` 可跳過），兩者收進同一個 `-files.tar.gz`，
> 與 DB dump 共用時間戳，加密／異地／保留期全部沿用既有開關。
>
> ⚠️ **與本文原文的一處刻意偏離**：原文寫「把 `pg_dump` 產物與檔案目錄收進**同一個壓縮包**」，
> 實作改成「同一次執行、同一個時間戳的**兩個**產物」。理由是包起來就不能直接
> `pg_restore --list`，而那道完整性驗證是腳本現有的守門。原文要的性質（還原天然對齊同一時點、
> 不必維護對照表）由共用時間戳達成。
>
> ⚠️ **剩下的是佈署動作，不是程式**：正式區要把 `REPORTS_PATH` 與 `RETENTION_DAYS`
> 寫進 crontab，並讓後者與 `Reports:RetentionDays` 一致
> （四個值的完整清單見〈部署組態清單〉一節；現況兩個預設值的方向已查證為安全）。
> 對應 [`llm-qa-and-report-tools.md`](../.claude/specifications/llm-qa-and-report-tools.md) §7.5 待決 #20。

## 一、現況：只有資料庫有備份

[`scripts/backup-db.sh`](../scripts/backup-db.sh) 只做 `pg_dump`（可選 GPG 加密＋rclone 異地），
**檔案系統完全不在備份範圍**——包含目前唯一的落地點 `wwwroot/uploads`（公司 logo）。

今天沒問題，因為落地檔只有 logo，遺失了重新上傳即可。
但 P2 開燈後報表會落地成檔，屆時會同時冒出三個問題：

| 問題 | 說明 |
|------|------|
| 備份與異地缺口 | DB 這條線已有加密與異地，檔案這條線一項都沒有 |
| 保留期形同失效 | `Reports:RetentionDays` 把機密報表清掉了，備份裡還留著 |
| **還原後 DB 與檔案不同步** | DB 還原到昨天、檔案是今天的 → `ReportRun.FilePath` 指向不存在的檔，使用者只看到下載失敗 |

第三項是最容易被漏掉的：它不會在備份當下出事，只在**真的要還原的那一天**才爆，
而那天通常沒有餘裕除錯。

## 二、建議方案：DB ＋ 檔案目錄同一次打包

擴充 `backup-db.sh`，把 `pg_dump` 產物與檔案目錄收進**同一個壓縮包**。

這個做法的關鍵好處不是省事，而是**還原天然對齊同一時點**——兩者取自同一次執行，
不需要另外維護「哪份 dump 對應哪批檔案」的對照。

### 納入備份的目錄

| 目錄 | 內容 | 備註 |
|------|------|------|
| `wwwroot/uploads` | 公司 logo 等上傳檔 | 現況唯一落地點 |
| 報表輸出目錄 | `ReportRun.FilePath` 指向的產物 | P2 上線後才存在；路徑須與 `Reports:` 設定一致，且**不得在 `wwwroot` 下**（機密報表一律經 Controller 驗權後 `File()` 回傳，不可由靜態檔中介軟體直出） |

### 保留期必須與 `Reports:RetentionDays` 一致

備份腳本現行的 `RETENTION_DAYS`（預設 14）是為 DB dump 訂的。
檔案納入後，**若備份保留期長於 `Reports:RetentionDays`，機密報表的保留期就形同虛設**——
線上清掉了，備份裡還躺著。兩者必須一起設定，且應在部署文件中成對記錄。

> #### ✅ 2026-09-17 查證：**現況兩個預設值的方向是安全的**
>
> | 值 | 來源 | 預設 |
> |---|---|---|
> | `RETENTION_DAYS`（備份） | `scripts/backup-db.sh` 第 57 行 | **14** |
> | `Reports:RetentionDays`（線上） | `ReportRetentionJobService.DefaultRetentionDays` | **90** |
>
> 14 < 90 ⇒ **備份先過期，線上還留著**。也就是說兩者都不設時，
> 不會出現「線上清掉了、備份裡還躺著」的洩漏——方向是對的。
>
> ⚠️ 但**不要靠這個預設值**：`appsettings.json` 裡**根本沒有 `Reports:` 這一段**
> （2026-09-17 查證），90 天是寫死在程式裡的 fallback。
> 哪天有人把 `RETENTION_DAYS` 調大（例如為了多留幾份 DB dump 調到 180）
> 卻沒動 `Reports:RetentionDays`，方向就翻過來了，**而且沒有任何訊號**——
> 備份腳本不讀應用程式組態，應用程式也不讀 crontab。所以下一節的四個值要一起明寫。

### 部署組態清單：四個值要一起設，缺一個都有靜默失效

| 值 | 設在哪 | 要設成什麼 | 不設會怎樣 |
|---|---|---|---|
| `Reports:StoragePath` | `appsettings.Production.json` 或環境變數 | **ContentRoot 之外的絕對路徑** | 退回 `App_Data/reports`（相對於行程工作目錄，通常落在 ContentRoot 內）。安全性上沒破口——它不經靜態檔中介軟體——但**重新部署會連同已產生的報表一起被覆蓋掉**，理由見 `Modules/RPS/Services/ReportFileStore.cs` 檔頭 |
| `Reports:RetentionDays` | 同上 | **明寫**，不要吃 90 天預設 | 退回程式內建的 90。不會壞，但它與備份保留期的關係從此只存在於口耳之間 |
| `REPORTS_PATH` | crontab（`backup-db.sh` 的環境變數） | **與 `Reports:StoragePath` 同一個路徑** | **報表檔完全不進備份**，而備份照樣成功、照樣有 DB dump——症狀要到真的要還原那天才看得到 |
| `RETENTION_DAYS` | 同上 | **≤ `Reports:RetentionDays`** | 退回 14（與 90 相比方向安全），但只要有人調大就會翻向 |

🚨 前兩個與後兩個分別由**應用程式組態**與 **crontab** 承載，兩邊互相看不見對方。
`REPORTS_PATH` 打錯字或指到別的目錄時，`backup-db.sh` 只會跳過該目錄，
不會把備份判成失敗——所以部署後請以「還原演練抽查 `ReportRun.FilePath`」實際驗一次
（見下方〈還原程序的追加步驟〉第 2 點），那是唯一會真的紅的檢查。

### 加密與異地沿用既有機制

打包後的產物走既有的 `BACKUP_GPG_RECIPIENT` 與 `BACKUP_RCLONE_REMOTE` 兩個可選開關即可，
不需要為檔案另建一套。**報表檔含金額等機密資料，正式區務必至少啟用加密與異地其一。**

### 還原程序的追加步驟

現行 runbook（見 `backup-db.sh` 檔頭）在 `pg_restore` 之外，須追加：

1. 解開壓縮包後，**先還原檔案目錄再啟動應用程式**，避免啟動後使用者點到尚未還原的檔。
2. 還原後抽查數筆 `ReportRun.FilePath`，確認檔案確實存在——這是驗證 DB 與檔案同步的最短路徑。
3. 還原演練建議與 DB 一起排入季度，且**演練必須包含檔案這一半**，否則等於沒測到本文件要解決的問題。

## 三、替代方案：報表檔存進 DB（`bytea`）

一次解決上述三個問題（備份、保留期、同步全部由 DB 承擔），代價是
**備份體積與 `pg_dump` 時間隨報表量線性成長**。

適合報表量小、或不希望維護第二套備份路徑的部署。若採用此方案，本文件第二節整段不適用，
但仍須確認 `Reports:RetentionDays` 的清理邏輯改為刪 DB 資料列而非刪檔。

## 四、決策時機

**必須在 P2 檔位開燈前決定**，不是 P3 的問題。理由是這類缺口屬於
「開燈那一刻就開始累積、事後補救成本高」——等到發現備份缺了報表檔時，
缺的已經是過去數個月的資料。

# 日誌保留與清理

全系統日誌分三層，各層的上限機制、保留期與設定位置整理如下。

## 一、應用層（Serilog）

| 項目 | 設定 | 位置 |
|------|------|------|
| 滾動週期 | 每日一檔 | `appsettings.json` → `Serilog:WriteTo[1]:Args` |
| 單檔上限 | 100 MB，超過開 `_001`、`_002` 續寫 | 同上（`fileSizeLimitBytes` + `rollOnFileSizeLimit`）|
| 保留檔數 | 30 個檔 | 同上（`retainedFileCountLimit`）|
| 保留天數 | 30 天（保底層） | `/etc/cron.daily/smartfactory-logs` |

### 為什麼一定要設 `fileSizeLimitBytes`

`Serilog.Sinks.File` 的預設是 `fileSizeLimitBytes = 1GB` 且 `rollOnFileSizeLimit = false`。
兩者搭配的行為是：**當日檔案寫到 1GB 之後就靜默停止寫入**——不丟例外、不寫警告、
應用程式完全無感，該日剩餘時間的日誌全部消失。事故當下往往正是日誌暴量的時候，
等於最需要日誌的那幾小時反而斷檔。因此必須明確設定上限並開啟 `rollOnFileSizeLimit`。

### Development 覆寫的索引合併陷阱

`appsettings.Development.json` 的 `Serilog:WriteTo` **不是整段取代 base，而是按陣列索引合併**。
只寫 `WriteTo[0] = Console` 時，被覆蓋的只有 base 的 Console，base 的 `WriteTo[1] = File`
原封不動保留並照常生效。過去因此讓 `MinimumLevel: Debug` 與
`Microsoft.EntityFrameworkCore.Database.Command: Information` 的內容全數落檔，
實測單日產生 119 MB / 136,016 筆 SQL 命令。

現行做法是明確覆寫索引 1 的 File sink 並加上 `restrictedToMinimumLevel: "Warning"`：
Console 保留完整 Debug 與 SQL 供即時觀察，檔案只留 Warning 以上。

> 注意：不能改用 `Information` 來擋 SQL。EF 的 `Database.Command` 本身就是 Information 級，
> 設 Information 擋不掉。

## 二、資料庫層

所有清理都以 `ExecuteDeleteAsync` 直接下 SQL（不經 `SaveChanges` → 不觸發 `AuditInterceptor`，
清理本身不再產生稽核），並一律 `IgnoreQueryFilters()`——背景服務沒有 HttpContext，
廠別與資料權限過濾會把結果濾成 0 筆。

| 資料表 | 保留政策 | 設定鍵 | 排程 | 實作 |
|--------|---------|--------|------|------|
| `AuditLogs` | 730 天 | `Security:AuditLogRetentionDays` | 03:00 | `AuditLogRetentionJobService` |
| `PasswordHistories` | 每人最近 K 筆 | `Security:PasswordHistoryRetentionKeep` | 04:00 | `PasswordHistoryRetentionJobService` |
| `ApprovalRequests` + `ApprovalLogs` | 730 天，Pending 不刪 | `Security:ApprovalHistoryRetentionDays` | 05:00 | `ApprovalHistoryRetentionJobService` |
| `MrpRuns` + `MrpRunLines` | 180 天，且無下游引用 | `Maintenance:MrpRunRetentionDays` | 06:00 | `MrpRunRetentionJobService` |
| `DataAuditRuns` + `DataAuditFindings` | 90 天 | （常數）| 02:00 | `DataAuditRunService.RetentionDays` |

全部位於 `Modules/ACT/Services/Jobs/`，由 `ACTModule.RegisterServices` 註冊。
每支排程時刻錯開一小時，避免同時段互相搶連線。

### 簽核歷程為什麼刪父不刪子

`FK_ApprovalLogs_ApprovalRequests_RequestId` 是 DB 層 `ON DELETE CASCADE`。
若直接對 `ApprovalLogs` 下時間條件，會出現「申請單還在、簽核歷程被砍掉一半」的破碎狀態。
改成刪 `ApprovalRequests` 由 CASCADE 帶走子層，任一申請單的歷程只會整份在或整份不在。

Pending 狀態一律不刪：不論多舊，只要還卡在流程上（常見於離職未轉派、代理人未設定），
刪掉會讓該單永遠無法結案。

### MRP 展開單為什麼只刪「無下游產出」的

DB 層只有 `FK_MrpRunLines_MrpRuns_MrpRunId` 一條真外鍵。以下四處都以純字串欄位記著
`MrpRunId`，**沒有任何外鍵保護**：

- `PurchaseRequest.SourceMrpRunId` / `PurchaseRequestLine.SourceMrpRunId`（MRP 轉請購的去重鍵）
- `ProductionBatch.MrpRunId`
- `OutsourceDemand.MrpRunId`
- `MrpPurchaseVarianceRecord.MrpRunId`

因為沒有外鍵，刪除當下資料庫不會擋也不會報錯，但事後這些單據的來源追溯欄位就成了
指向不存在展開單的死指標（點不開、對帳查無來源）。所以刪除前逐一反查，任一處仍引用
就整張保留。

代價是「有產出的展開單會長期留著」——這是刻意取捨：佔量的本來就是無產出的重複試跑，
可追溯性不該為了省空間犧牲。

## 三、作業系統層

| 項目 | 現況 | 措施 |
|------|------|------|
| `systemd-journald` | 預設無上限（`/var/log` 檔案系統 10%，上限 4GB）| 安裝 `journald.conf.example` → 500M / 1 個月 |
| `/var/log/syslog` | 已由 `/etc/logrotate.d/rsyslog` 納管 | 無須處理 |
| PostgreSQL | `log_statement=none`、`log_min_duration_statement=-1`，僅記錯誤與啟動；`/etc/logrotate.d/postgresql-common` 週輪 10 份 | 無須處理 |

### 安裝

```bash
# journald 上限
sudo cp deploy/journald.conf.example /etc/systemd/journald.conf
sudo systemctl restart systemd-journald
sudo journalctl --vacuum-size=500M          # 立即回收既有超量部分

# 應用日誌每日清理（檔名不可有副檔名，run-parts 會略過含 . 的檔案）
sudo cp deploy/smartfactory-logs.cron.example /etc/cron.daily/smartfactory-logs
sudo chmod 755 /etc/cron.daily/smartfactory-logs
sudo chown root:root /etc/cron.daily/smartfactory-logs

# 驗證（先試跑不刪檔）
SMARTFACTORY_LOG_RETENTION_DAYS=30 sh -x /etc/cron.daily/smartfactory-logs
```

部署路徑與保留天數可用環境變數覆寫：`SMARTFACTORY_LOG_DIR`、`SMARTFACTORY_LOG_RETENTION_DAYS`。

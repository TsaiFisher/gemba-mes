# 生產環境連線字串祕密管理（加密連線字串）

> 安全修正（secret management，對應 `security-assessment-and-plan.md`）。目標：**資料庫密碼永不以明文存在於映像檔、版控檔或設定檔**。
> 本文為 ProjectZ 部署專案的操作手冊；加密演算法與工具的權威說明見 Core `DEVELOPMENT.md`「首次環境設定（Phase 2）」。

---

## 0. 兩種可用的祕密管理模式（擇一）

| 模式 | 適用 | 密碼位置 | 需 SmartFactory 授權套件 |
|------|------|----------|--------------------------|
| **A. 環境變數注入** | 授權明示 `allowPlainConnection` 時（正式授權一般不開放） | 主機 `.env` 的 `DB_PASSWORD`（已 gitignore） | 是（`license.json` 一律需要；Security DLL 可省） |
| **B. 加密連線字串** | 授權制地端部署，需連版控/設定檔都不得見明文 | 加密字串（機器指紋＋LicenseId＋主機板識別碼綁定），寫入未進版控的 `appsettings.Production.json` | 是（`license.json` 由 SmartFactory 依申請碼核發；Security DLL 從 GitHub Releases 下載同版本） |

> 兩者都能讓**版控中的 `appsettings.json` 不含真實密碼**（現況為 `Password=CHANGE_ME` 佔位，安全）。
> 應用程式解析邏輯（`Program.cs` `ResolveConnectionStringAndContext`）：`DefaultEncrypted` 非空→走模式 B 解密；否則退回明文 `DefaultConnection`（模式 A 由環境變數提供）。
> **模式 B 的加密值與機器指紋＋LicenseId＋主機板識別碼綁定，不可跨機器複製**；換機、或換成不同授權編號的授權，都須重新產生。

---

## 1. 模式 B 操作步驟（加密連線字串）

### 前置（一次性）
- `Security/SmartFactory.MES.Security.dll`（**不進版控**，`/Security/` 在 `.gitignore`；從 GitHub Releases 下載與系統同版本的檔案——系統拒收舊版 DLL，升級時一併更換；`SmartFactory.MES.csproj` 建置時複製到輸出目錄，換 DLL 後須重新建置）。
- `license.json`（**不進版控**）——由 SmartFactory 依申請碼核發，綁定機器指紋與主機板識別碼。

### 步驟 1：在**目標機器**取得申請碼並申請授權
```bash
dotnet run --project SmartFactory.MES.MachineId -- --init   # 首次：產生機器錨定碼
dotnet run --project SmartFactory.MES.MachineId             # 印出環境指紋、主機識別碼、申請碼與登記網址
```
- 開啟登記網址送出申請（或把申請碼交給導入顧問）；收到授權檔後改名 `license.json` 放到應用程式目錄，記下其中的 `licenseId`。
- `tools/get-fingerprint.sh` 只算得出指紋、沒有主機識別碼，**不能用來申請**（步驟 2 仍可用它取得指紋）。
- **Docker**：見 §2（工具在映像內，容器一律每 90 天確認、不綁主機）。

### 步驟 2：產生加密連線字串
由企業 IT 在**目標機器**、於方案根目錄執行 `tools/EncryptConnectionString`（Docker 部署改用映像內附的版本，見 §2）
（`--license-id` 必須是步驟 1 收到的授權檔中的 `licenseId`，一字不差）：
```bash
FINGERPRINT=$(bash tools/get-fingerprint.sh | grep -oP '[0-9A-F]{64}')
dotnet run --project tools/EncryptConnectionString -- \
  --fingerprint "$FINGERPRINT" \
  --license-id  <授權檔的 licenseId，如 LIC-ACME-20261019> \
  --plaintext   "Host=<db-host>;Port=5432;Database=SmartFactoryMES;Username=<user>;Password=<實際密碼>" \
  --this-host \
  --verify
```
- 輸出形如 `ENC:v3:{salt}:{nonce}:{cipher}:{tag}`；每次結果不同（隨機 salt/nonce）屬正常。
- `--this-host` 把**本機主機板 UUID** 納入金鑰：授權有列主機識別碼時系統只接受 v3，複製到別台主機解不開。
  主機不符或讀不到主機識別碼時，系統**不連資料庫**、只顯示授權頁（含申請碼）。換主機板須重新申請授權並在新主機上重新加密。
  容器走 lease 授權（沒有主機識別碼）時改用 `--no-host-binding`（輸出 v2）。
- **SmartFactory 不需也不應知道資料庫密碼**——加密由企業 IT 自行執行。

### 步驟 3：寫入未進版控的 `appsettings.Production.json`
於目標機器建立（**切勿 commit**，已由 `.gitignore` 排除 `appsettings.Development.json`；`appsettings.Production.json` 請同樣不納入版控）：
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "",
    "DefaultEncrypted": "ENC:v3:...（步驟 2 輸出）"
  }
}
```
- `DefaultEncrypted` 非空時自動進入模式 B；`DefaultConnection` 必須清空，避免任何殘留明文。

### 步驟 4：驗證
啟動後日誌應出現授權載入訊息（如 `⚠️ SmartFactory Internal License`）；未出現代表仍在明文模式（DLL 未載入或 `DefaultEncrypted` 為空）。`/health` 的 `license` 檢查應為 Healthy。

---

## 2. 與 docker-compose 的關係

`docker-compose.yml` 預設**模式 B**：`ConnectionStrings__DefaultEncrypted` 由 `.env` 的 `DB_ENCRYPTED` 注入，`DefaultConnection` 清空。
客戶正式授權不允許明文連線字串；只有授權明示 `allowPlainConnection` 時才能改回模式 A（compose 內有註解範例）。

2026-10-19 以桌機 WSL＋Docker 29.1.3 實測（Core SECURITY-OPERATIONS §18.15.10）：

1. 錨定檔與授權確認結果（`lease.json`）在 `anchor` volume（`/var/lib/smartfactory`），容器重建不變；**不要**設 `hostname` 或
   `SMARTFACTORY_INSTANCE_ID`（指紋不看 hostname；環境變數錨定碼只限 CI，客戶授權會拒絕）。
2. 工具在映像內，以 `--entrypoint dotnet` 執行（否則會跑成主程式）：
   ```bash
   docker compose run --rm --no-deps --entrypoint dotnet app tools/MachineId/SmartFactory.MES.MachineId.dll --init
   docker compose run --rm --no-deps --entrypoint dotnet app tools/MachineId/SmartFactory.MES.MachineId.dll   # 申請碼
   docker compose run --rm --no-deps --entrypoint dotnet app tools/EncryptConnectionString/EncryptConnectionString.dll \
     --license-id <licenseId> --plaintext "Host=db;Port=5432;Database=SmartFactoryMES;Username=dev;Password=<DB_PASSWORD>" \
     --no-host-binding --verify
   ```
   容器一律是「每 90 天確認」的授權（不綁主機、不讀主機識別碼），所以加密用 `--no-host-binding`（ENC:v2）；指紋自動取 volume 裡的錨定檔。
3. 輸出寫進 `.env`：`DB_ENCRYPTED=ENC:v2:...`。`license.json` 與 `Security/SmartFactory.MES.Security.dll` 放在 compose 同目錄（唯讀掛載），**啟動前必須已存在**。
4. `docker compose up -d`：Migration 自動套用。第一次啟動為「尚未完成授權確認」（唯讀、`/health` Unhealthy），
   管理員到「授權狀態」頁按「確認授權」後恢復；之後每 90 天一次。

> 兩模式不可混用於同一環境：`DefaultEncrypted` 一旦非空即優先，忽略明文注入。

---

## 3. 稽核檢查點（上線前）
- [ ] 版控中的 `appsettings.json` 不含真實密碼（維持 `CHANGE_ME` 佔位或空字串）。
- [ ] `appsettings.Production.json`、`license.json`、`.env` 均**未** commit（`git status` 應無此三者）。
- [ ] 模式 A：`.env` 的 `DB_PASSWORD` 為強密碼、檔案權限 600。
- [ ] 模式 B：`DefaultConnection` 清空、`DefaultEncrypted` 已設；Docker 的 `anchor` volume 已納入備份。
- [ ] 映像檔內不含任何明文連線字串（`docker history` / 映像掃描確認）。

> 禁改邊界：本流程不修改 `LicenseValidator.cs`、`EnvironmentFingerprintService.cs` 或 `Program.cs` 的授權/解析邏輯——僅為部署操作與設定。

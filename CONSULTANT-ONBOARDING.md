# SmartFactory MES 顧問開發環境建置指南

> **閱讀對象**：顧問（外部開發人員）
> **這是顧問唯一需要閱讀的說明文件。** 如需額外說明，請聯繫 SmartFactory。

## 概覽

SmartFactory MES 採用 **路線 B（Upstream Tracking Model）**：

- **SmartFactory Core Repo**：平台核心，由 SmartFactory 維護
- **企業 Repo**：每個客戶獨立 Repo，顧問在此工作
- 顧問只能在 `Modules/{ModuleName}/` 目錄下開發（依業務功能命名，如 `WorkOrder`、`QualityControl`）
- 核心更新透過 `git merge upstream/main` 取得

---

## 執行者角色

本文件涉及三個角色，各步驟會標示由誰負責執行：

| 角色 | 說明 |
|------|------|
| **SmartFactory** | SmartFactory 核心團隊；負責核發授權（license.json）、發布 Security DLL、維護核心平台 |
| **顧問** | 派駐開發功能模組的顧問；在企業 Repo 的 `Modules/{ModuleName}/` 工作 |
| **企業 IT** | 客戶端 IT 人員；負責生產環境部署與基礎設施維護 |

---

## 環境需求

| 工具 | 最低版本 | 說明 |
|------|----------|------|
| .NET SDK | 8.0 | `dotnet --version` |
| Git | 2.38+ | `git --version` |
| PostgreSQL | 15+ | 或使用 Docker |
| Docker & Docker Compose | 24+ | 選用，用於本機 DB |
| VS Code 或 Rider | 最新版 | 推薦 IDE |

---

## 步驟一：複製企業 Repo `[顧問]`

```bash
git clone https://github.com/{YourOrg}/SmartFactory.MES.{ClientCode}.git
cd SmartFactory.MES.{ClientCode}
```

## 步驟二：設定 upstream（連結至 SmartFactory Core） `[顧問]`

```bash
git remote add upstream https://github.com/TsaiFisher/SmartFactory.MES.git
git remote -v  # 驗證
```

## 步驟三：設定 Git Hooks（邊界保護） `[顧問]`

```bash
git config core.hooksPath .githooks
```

> 這會啟用 pre-commit hook，防止顧問意外修改核心區域。

## 步驟四：取得申請碼並申請授權 `[顧問 → SmartFactory]`

授權綁定這台設備（環境指紋＋主機板識別碼），必須先在**要執行系統的那台設備**取得申請碼，SmartFactory 才能核發授權套件。
全新環境還沒有授權、系統起不來，申請碼由 MachineId 工具產生：

```bash
dotnet run --project SmartFactory.MES.MachineId -- --init   # 首次：產生機器錨定碼
dotnet run --project SmartFactory.MES.MachineId             # 顯示環境指紋、主機識別碼、申請碼與登記網址
```

用能上網的瀏覽器開啟輸出的**登記網址**，填寫公司、廠區、聯絡人、Email 送出（授權免費，首次登記人工審核，約數個工作天，
授權檔以 Email 寄出）；或把**申請碼**整串交給 SmartFactory 代為登記。

> 🚨 只提供申請碼或指紋，**不要提供錨定碼**（`--raw` 才會顯示的那串，長得像 UUID）——它是連線字串加密的金鑰材料。
> 申請碼內的指紋與主機識別碼都是雜湊值。

> **注意**：換設備、換主機板，或把 WSL2 整顆搬到另一台 Windows 主機，主機識別碼會改變，系統只會顯示授權頁，需重新申請授權。
> 若輸出顯示「主機識別碼：讀不到」，Linux 實體機請先完成使用手冊「授權與防複製」第 4 節的 systemd 設定。

> **Windows 正式環境（非 WSL）**：改用 SmartFactory 提供的 `SmartFactory.MES.MachineId.exe`：
>
> ```powershell
> SmartFactory.MES.MachineId.exe --init   # 首次：產生錨定碼並顯示指紋
> SmartFactory.MES.MachineId.exe          # 之後：顯示指紋、主機識別碼、申請碼與登記網址
> ```
>
> 請確認輸出的錨定檔位置為 `%ProgramData%\SmartFactory\instance-id`；若顯示的是個人目錄，
> 代表系統位置無法寫入，以服務帳號執行的應用程式會讀不到。
>
> Windows 上的錨定檔預設以 DPAPI 封裝（內容為 `DPAPI:v1:…`），**複製到其他電腦無法使用**，
> 此時 `tools/get-fingerprint.sh` 會報錯而不會算出指紋，屬預期行為。
> ⚠️ **OS 重灌後錨定檔無法再解開，需重新執行 `--init` 並申請換發授權。**
> 既有的純文字錨定檔可用 `--protect` 就地封裝，指紋不變、既有授權照常可用。

---

## 步驟五：取得並安裝授權套件 `[SmartFactory 核發 → 顧問 安裝]`

SmartFactory 審核通過後，以 Email（**不透過 Git**）寄出 `license.json`；Security DLL 從 [GitHub Releases](https://github.com/TsaiFisher/gemba-mes/releases) 下載**與系統同版本**的檔案（系統會拒收舊版 DLL，升級系統時一併更換）：

| 項目 | 放置位置 | 說明 |
|------|----------|------|
| `license.json` | `SmartFactory.MES/license.json` | 企業授權檔，含授權編號、設備指紋、主機識別碼（皆在簽章內，不可修改） |
| `SmartFactory.MES.Security.dll` | `SmartFactory.MES/Security/SmartFactory.MES.Security.dll` | Security Anchor DLL，授權驗證與連線字串解密（從 Releases 下載） |

```
SmartFactory.MES/
├── license.json                        ← 放這裡
└── Security/
    └── SmartFactory.MES.Security.dll  ← 放這裡（需先建立 Security/ 目錄）
```

> **重要**：
> - 這兩個檔案均已加入 `.gitignore`，**絕對不得**提交至版控
> - 綁定特定設備，**不可跨設備共用**；改動授權檔任何內容都會變成「簽章無效」
> - 換設備必須重新向 SmartFactory 申請新的授權套件
> - 下一步（步驟七）的連線字串加密要用這份授權的 `licenseId`，且必須在**這台設備上**執行（金鑰含主機板識別碼原值）

## 步驟六：設定資料庫 `[顧問（開發）／企業 IT（生產）]`

### 選項 A：Docker（推薦）

```bash
# 使用提供的 docker-compose.yml
docker compose up -d db

# 或僅啟動 PostgreSQL
docker run -d --name mes-db \
  -e POSTGRES_DB=SmartFactoryMES \
  -e POSTGRES_USER=dev \
  -e POSTGRES_PASSWORD=Dev12345 \
  -p 5432:5432 postgres:15
```

### 選項 B：本機 PostgreSQL

建立資料庫：
```sql
CREATE USER dev WITH PASSWORD 'Dev12345';
CREATE DATABASE "SmartFactoryMES" OWNER dev;
```

## 步驟七：加密並設定連線字串 `[顧問（開發）／企業 IT（生產）自行執行]`

連線字串由顧問/企業 IT **在自己的機器上自行加密**，SmartFactory 不需要知道資料庫密碼。
加密工具已包含在 Core Repo 的 `tools/EncryptConnectionString/` 中。

### 開發環境（顧問）

```bash
# 1. 確認 license.json 已放置（步驟五），取得 licenseId
cat SmartFactory.MES/license.json | grep licenseId

# 2. 取得本機指紋
bash tools/get-fingerprint.sh

# 3. 加密連線字串（--verify 可立即驗證解密結果）
dotnet run --project tools/EncryptConnectionString -- \
  --fingerprint <步驟二輸出的指紋> \
  --license-id <license.json 中的 licenseId> \
  --plaintext "Host=localhost;Database=SmartFactoryMES;Username=dev;Password=Dev12345" \
  --this-host \
  --verify
```

將輸出的 `ENC:v3:...` 值建立 `appsettings.Development.json`（**此檔案不進版控**）：

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "",
    "DefaultEncrypted": "ENC:v3:{上面指令輸出的加密值}"
  }
}
```

> - `DefaultEncrypted` 非空時，應用程式自動載入 Security DLL 解密連線字串
> - `DefaultConnection` 設為空字串，確保不走明文路徑
> - 此加密值與**本機指紋**、`licenseId` 及**本機主機板 UUID**（`--this-host`，ENC:v3）綁定，**不可複製到其他設備使用**
> - 授權有列主機識別碼（`hostIds`）時系統只接受 ENC:v3；主機不符或讀不到主機識別碼時，系統不連資料庫、只顯示授權頁
> - 換電腦或主機板：重新申請授權，並**在新主機上**重新執行加密（主機板 UUID 原值不離機）
> - **虛擬機（VM）**的授權不綁主機（改為每 90 天確認），加密時把 `--this-host` 換成 `--no-host-binding`（輸出 ENC:v2）；容器見步驟 P6

### 生產環境（企業 IT）

在**生產伺服器（或容器）上**執行，使用生產環境的連線字串和指紋：

```bash
# 1. 取得生產環境指紋（必須在生產伺服器或容器內執行）
bash tools/get-fingerprint.sh

# 2. 加密生產連線字串
dotnet run --project tools/EncryptConnectionString -- \
  --fingerprint <生產環境指紋> \
  --license-id <license.json 中的 licenseId> \
  --plaintext "Host=db.internal;Database=MES;Username=app;Password=生產密碼" \
  --this-host \
  --verify
```

將輸出值填入 `appsettings.Production.json`（**不進版控**）：

```json
{
  "ConnectionStrings": {
    "DefaultEncrypted": "ENC:v3:{生產環境加密值}"
  }
}
```

> **重要**：加密必須在目標機器上執行（指紋綁定）。在開發機加密後複製到生產伺服器**無法解密**。

## 步驟八：執行 Migration `[顧問]`

```bash
dotnet ef database update
```

## 步驟九：啟動應用程式 `[顧問]`

```bash
dotnet run
# 瀏覽器開啟 http://localhost:5110（https 需 dotnet run --launch-profile https → https://localhost:7054）
# 預設帳號：admin / Dev12345（首次登入需改密）
```

---

## 開發工作流程

### 建立新功能模組

每個業務功能獨立一個目錄，以功能名稱命名（PascalCase）：

```
Modules/{ModuleName}/          ← 例如 WorkOrder/、QualityControl/
├── Controllers/               ← 功能控制器
├── Models/                    ← 資料模型（使用 [Table(Schema="{modulename}")] 小寫）
├── Views/                     ← Razor Views
│   └── {Controller}/
│       └── Index.cshtml
├── Services/                  ← 業務服務（選用）
├── Migrations/                ← 功能模組 DB Migration
└── CLAUDE.md                  ← 由 _TEMPLATE/CLAUDE.md 複製並填寫
```

### 必要的 RBAC 模式

```csharp
[Authorize]
public class WorkOrderController : Controller
{
    private const string MenuKey = "WorkOrder.List";   // {ModuleName}.{Feature}

    public async Task<IActionResult> Index()
    {
        if (!await _permissionService.CanRead(MenuKey)) return Forbid();
        // ...
    }
}
```

### 參考範本

複製 `Modules/_TEMPLATE/` 作為新功能模組的起點：

```bash
cp -r Modules/_TEMPLATE Modules/WorkOrder
# 編輯 Modules/WorkOrder/CLAUDE.md，將 {ModuleName} 替換為 WorkOrder
```

---

## 核心更新 `[顧問]`

當 SmartFactory 發布核心更新時：

```bash
./update-core.sh            # 互動式更新（建議）
./update-core.sh --dry-run  # 先預覽再決定
```

或手動執行：

```bash
git fetch upstream
git merge upstream/main --no-ff
dotnet build
dotnet test
git push
```

---

## 授權套件說明

應用程式啟動時會自動驗證授權。開發與生產環境均需：

| 項目 | 位置 | 說明 |
|------|------|------|
| `license.json` | 專案根目錄 | 由 SmartFactory 核發，綁定設備指紋 |
| `SmartFactory.MES.Security.dll` | `Security/` 目錄 | 從 GitHub Releases 下載與系統同版本的檔案，不進版控 |

缺少任一檔案，應用程式將拒絕啟動。

---

## CI/CD 設定

企業 Repo 建議在 `.github/workflows/` 中設定 CI/CD，參考：
`Modules/_TEMPLATE/.github/workflows/cicd.yml`

---

## 開發環境建立程序（完整步驟） `[顧問]`

> 新機器或新顧問**第一次**建立開發環境時，依序執行以下步驟。完成後日常開發只需 `dotnet watch run`。

### 前置確認

```bash
dotnet --version   # 需 8.0 以上
git --version      # 需 2.38 以上
docker --version   # 選用，用於本機 DB
```

---

### 步驟 D1　複製企業 Repo

```bash
git clone https://github.com/{YourOrg}/SmartFactory.MES.{ClientCode}.git
cd SmartFactory.MES.{ClientCode}
```

---

### 步驟 D2　連結 SmartFactory Core（upstream）

```bash
git remote add upstream https://github.com/TsaiFisher/SmartFactory.MES.git
git fetch upstream
git remote -v      # 確認出現 origin 與 upstream 兩行
```

---

### 步驟 D3　啟用邊界保護 Hook

```bash
git config core.hooksPath .githooks
```

> 此後若意外修改核心區域，commit 會被自動阻擋。

---

### 步驟 D4　啟動本機資料庫

**方法 A：Docker（推薦）**

```bash
docker compose up -d db
docker compose ps   # 確認 db 狀態為 running
```

**方法 B：本機 PostgreSQL**

```sql
-- 以 postgres 超級使用者執行
CREATE USER dev WITH PASSWORD 'Dev12345';
CREATE DATABASE "SmartFactoryMES" OWNER dev;
```

---

### 步驟 D5　取得申請碼並申請授權

```bash
dotnet run --project SmartFactory.MES.MachineId -- --init   # 首次
dotnet run --project SmartFactory.MES.MachineId             # 印出申請碼與登記網址
```

開啟登記網址送出申請（或把申請碼交給 SmartFactory，見步驟四）。
審核通過後 SmartFactory 會以 Email 寄回 `license.json`；`SmartFactory.MES.Security.dll` 從 [GitHub Releases](https://github.com/TsaiFisher/gemba-mes/releases) 下載與系統同版本的檔案。

---

### 步驟 D6　安裝授權套件

```bash
# 建立 Security 目錄
mkdir -p SmartFactory.MES/Security

# 授權信附的 license.json 與從 Releases 下載的 DLL 放到對應位置
cp ~/Downloads/license.json              SmartFactory.MES/license.json
cp ~/Downloads/SmartFactory.MES.Security.dll  SmartFactory.MES/Security/

# 確認檔案存在
ls SmartFactory.MES/license.json SmartFactory.MES/Security/SmartFactory.MES.Security.dll
```

> 這兩個檔案均已在 `.gitignore` 中排除，**不得提交至版控**。

---

### 步驟 D7　加密連線字串

```bash
# 取得 licenseId（從 license.json）
LICENSEID=$(cat SmartFactory.MES/license.json | python3 -c "import sys,json; print(json.load(sys.stdin)['licenseId'])")
echo $LICENSEID

# 取得本機指紋
FINGERPRINT=$(bash tools/get-fingerprint.sh | grep -oP '[0-9A-Fa-f]{64}')
echo $FINGERPRINT

# 加密連線字串（--verify 會立即驗證解密結果）
dotnet run --project tools/EncryptConnectionString -- \
  --fingerprint "$FINGERPRINT" \
  --license-id "$LICENSEID" \
  --plaintext "Host=localhost;Port=5432;Database=SmartFactoryMES;Username=dev;Password=Dev12345" \
  --this-host \
  --verify
# 輸出：ENC:v3:xxxx:xxxx:xxxx:xxxx
```

將輸出值填入 `SmartFactory.MES/appsettings.Development.json`（**此檔不進版控**）：

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "",
    "DefaultEncrypted": "ENC:v3:{上方輸出值}"
  }
}
```

---

### 步驟 D8　執行資料庫 Migration

```bash
cd SmartFactory.MES
dotnet ef database update
# 出現 "Done." 表示成功
```

---

### 步驟 D9　建置並啟動

```bash
dotnet build   # 確認無錯誤
dotnet watch run --urls "http://localhost:5110;https://localhost:7054"
```

啟動後日誌應出現（顧問開發授權）：
```
[INF] License validated: ClientCode=xxx, Expiry=yyyy-mm-dd
```

瀏覽器開啟 `http://localhost:5110`，使用預設帳號登入：
- 帳號：`admin`
- 密碼：`Dev12345`（首次登入請修改）

---

### 開發環境建立 Checklist

| # | 動作 | 指令 / 說明 | 完成 |
|---|------|------------|:----:|
| D1 | 複製企業 Repo | `git clone ...` | ☐ |
| D2 | 連結 upstream | `git remote add upstream ...` | ☐ |
| D3 | 啟用 Hook | `git config core.hooksPath .githooks` | ☐ |
| D4 | 啟動資料庫 | `docker compose up -d db` | ☐ |
| D5 | 取得申請碼申請授權 | `dotnet run --project SmartFactory.MES.MachineId` → 開啟登記網址 | ☐ |
| D6 | 安裝授權套件 | 放置 `license.json` 與 DLL | ☐ |
| D7 | 加密連線字串 | `dotnet run --project tools/EncryptConnectionString` | ☐ |
| D8 | 執行 Migration | `dotnet ef database update` | ☐ |
| D9 | 啟動應用程式 | `dotnet watch run` | ☐ |

---

## 正式環境建立程序（完整步驟） `[企業 IT]`

> 生產伺服器**首次部署**時依序執行。後續版本更新只需 `docker compose pull && docker compose up -d`。

### 前置確認

```bash
docker --version          # 需 24 以上
docker compose version    # 需 2.20 以上
curl --version
```

---

### 步驟 P1　準備部署目錄

```bash
sudo mkdir -p /opt/smartfactory-mes/Security
sudo chown -R $USER:$USER /opt/smartfactory-mes
cd /opt/smartfactory-mes
```

---

### 步驟 P2　取得部署檔案

從顧問或 SmartFactory 取得以下檔案，放至部署目錄：

```
/opt/smartfactory-mes/
├── docker-compose.yml       ← 顧問提供
├── Dockerfile               ← 顧問提供（或使用預建 Image）
└── .env                     ← 自行建立（見步驟 P3）
```

---

### 步驟 P3　建立環境變數檔

套件內含 `.env.example` 作為範本，可擇一方式建立 `.env`：

**方式一：從範本複製再編輯**
```bash
cp /opt/smartfactory-mes/.env.example /opt/smartfactory-mes/.env
nano /opt/smartfactory-mes/.env   # 將 DB_PASSWORD 改為真實密碼
```

**方式二：直接建立**
```bash
cat > /opt/smartfactory-mes/.env << 'EOF'
DB_PASSWORD=請設定強密碼（至少16字元，含大小寫數字特殊字元）
EOF
```

```bash
chmod 600 /opt/smartfactory-mes/.env   # 僅擁有者可讀
```

---

### 步驟 P4　產生錨定碼並取得申請碼

容器的環境指紋來自**機器錨定檔**，存在 `docker-compose.yml` 的 `anchor` volume（`/var/lib/smartfactory`），容器重建不會改變；
**刪掉這個 volume＝換一個身分**，要重新申請授權，請納入備份。容器部署一律不綁主機，授權為**每 90 天由管理員確認一次**。

映像附帶工具，以 `--entrypoint dotnet` 執行（否則會跑成主程式）：

```bash
cd /opt/smartfactory-mes
docker compose build app        # 或使用預建 Image
docker compose run --rm --no-deps --entrypoint dotnet app tools/MachineId/SmartFactory.MES.MachineId.dll --init
docker compose run --rm --no-deps --entrypoint dotnet app tools/MachineId/SmartFactory.MES.MachineId.dll
# 印出：部署類型「容器」、申請碼、登記網址
```

開啟登記網址申請（或把申請碼交給 SmartFactory，見步驟四），取得：
- `license.json`（生產授權，授權信附件）
- `SmartFactory.MES.Security.dll`（從 [GitHub Releases](https://github.com/TsaiFisher/gemba-mes/releases) 下載與映像同版本的檔案）

---

### 步驟 P5　安裝授權套件

```bash
cp license.json                  /opt/smartfactory-mes/license.json
cp SmartFactory.MES.Security.dll /opt/smartfactory-mes/Security/
chmod 644 /opt/smartfactory-mes/license.json /opt/smartfactory-mes/Security/SmartFactory.MES.Security.dll
```

> 兩個檔案以唯讀方式掛載進容器（容器內以 uid 1000 的 appuser 執行，必須讀得到）。
> **啟動 app 前兩者都要已存在**——不存在時 Docker 會把掛載點建成空目錄，系統報「找不到授權檔案」。

---

### 步驟 P6　在容器內加密生產連線字串

```bash
LICENSEID=$(python3 -c "import json;print(json.load(open('license.json'))['licenseId'])")
docker compose run --rm --no-deps --entrypoint dotnet app tools/EncryptConnectionString/EncryptConnectionString.dll \
  --license-id "$LICENSEID" \
  --plaintext "Host=db;Port=5432;Database=SmartFactoryMES;Username=dev;Password=<.env 的 DB_PASSWORD>" \
  --no-host-binding --verify
# 指紋自動取自 anchor volume 裡的錨定檔；容器授權沒有主機識別碼，所以用 --no-host-binding（輸出 ENC:v2）
```

把輸出的 `ENC:v2:...` 寫進 `.env`：

```bash
echo 'DB_ENCRYPTED=ENC:v2:{上方輸出值}' >> /opt/smartfactory-mes/.env
```

> 加密值與錨定檔、授權編號綁定：換授權（授權編號不同）或刪掉 anchor volume，都要重新加密。

---

### 步驟 P7　確認 docker-compose.yml 的 app 設定

```yaml
services:
  app:
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      ConnectionStrings__DefaultConnection: ""
      ConnectionStrings__DefaultEncrypted: ${DB_ENCRYPTED}
    volumes:
      - anchor:/var/lib/smartfactory          # 錨定檔＋授權確認結果（lease.json）
      - ./license.json:/app/license.json:ro
      - ./Security:/app/Security:ro
volumes:
  anchor:
```

> 授權確認結果（`lease.json`）也寫在 `anchor` volume，容器重建後不必重新確認。
> 不要再設定 `hostname` 或 `SMARTFACTORY_INSTANCE_ID`：指紋已不看 hostname，環境變數錨定碼則只限 CI，客戶授權會拒絕。

---

### 步驟 P8　啟動並完成第一次授權確認

```bash
cd /opt/smartfactory-mes
docker compose up -d                 # 首次啟動會自動套用資料庫 Migration
docker compose logs -f app
```

第一次啟動時日誌會出現「⛔ 尚未完成授權確認」、`/health` 為 Unhealthy，系統唯讀——這是預期行為。
以管理員登入，開啟「授權狀態」頁按「確認授權」（瀏覽器代為向授權伺服器確認，系統本身不連外），完成後即可寫入；
之後每 90 天確認一次，到期前 14 天開始提醒。客戶允許系統直接連外時，可加環境變數 `License__Transport: direct` 改為自動確認。

---

### 步驟 P9　驗證健康狀態

```bash
curl -s http://localhost:8080/health
# 預期（完成確認後）：{"status":"Healthy"}
```

---

### 正式環境建立 Checklist

| # | 動作 | 執行者 | 完成 |
|---|------|--------|:----:|
| P1 | 建立部署目錄 | 企業 IT | ☐ |
| P2 | 取得部署檔案 | 企業 IT + 顧問 | ☐ |
| P3 | 建立 `.env`（DB 密碼）| 企業 IT | ☐ |
| P4 | 產生錨定碼、取得申請碼申請授權 | 企業 IT → SmartFactory | ☐ |
| P5 | 安裝授權套件 | 企業 IT | ☐ |
| P6 | 在容器內加密生產連線字串，寫入 `.env` | 企業 IT | ☐ |
| P7 | 確認 docker-compose.yml 掛載設定 | 企業 IT | ☐ |
| P8 | 啟動服務，完成第一次「確認授權」 | 企業 IT | ☐ |
| P9 | 驗證 `/health` 端點 | 企業 IT | ☐ |
| — | 備份 `anchor` volume、`license.json`、資料庫 | 企業 IT | ☐ |

---

### 版本更新（日後例行）

```bash
cd /opt/smartfactory-mes
docker compose pull        # 拉取新版 Image
docker compose up -d       # 滾動更新（舊容器停止，新容器啟動）；新 Migration 於啟動時自動套用
```

版本回滾：

```bash
APP_VERSION=v1.1.0 docker compose up -d   # 指定舊版 tag
```

---

## 常見問題

### Q: 執行時出現「找不到授權檔案」

確認 `license.json` 已放在專案根目錄（`SmartFactory.MES.csproj` 同層）並重新建置——系統只讀執行目錄裡的 `license.json`，
Security DLL 不看 `License:Path`。還沒有授權時系統無法啟動，
申請碼請用 `SmartFactory.MES.MachineId` 取得（步驟四）。

### Q: 開啟網址只看到授權頁（「此授權不屬於這台電腦」或「無法確認本機是授權機器」）

主機識別碼不符或讀不到，系統不連資料庫（防止整台被複製後直接使用）。若是換了設備或主機板，
用授權頁顯示的申請碼重新申請，收到新授權後在新設備上以新授權編號重新加密連線字串（步驟七）再重新啟動；
若是讀不到，依頁面訊息修復（WSL：`sudo systemctl restart systemd-binfmt` 或重開 WSL；Linux：systemd 設定）。

### Q: 出現「授權檔案簽章無效，可能已被竄改」

授權檔被修改過（包含手動補欄位），或 `bin/` 裡還是舊的 Security DLL。換回 SmartFactory 寄來的原檔；換過 DLL 要重新建置。

### Q: Docker 容器授權失敗（指紋不符）

容器的指紋只看 `anchor` volume 裡的錨定檔（不看 hostname）。指紋不符通常是 volume 被刪掉或換了（例如 `docker compose down -v`、
換了 compose 專案名稱導致 volume 名稱不同）：

1. `docker compose run --rm --no-deps --entrypoint dotnet app tools/MachineId/SmartFactory.MES.MachineId.dll` 看目前指紋與錨定碼來源
2. 能從備份還原原本的 volume 就還原；否則用畫面上的申請碼重新申請授權，並以新授權編號重新加密連線字串（步驟 P6）

### Q: 容器裡出現「無法確認本機是授權機器」的授權頁

拿到的是綁主機的授權。容器部署一律不綁主機（不讀主機識別碼），請用容器內 MachineId 印出的申請碼重新申請（會核發每 90 天確認的授權）。

### Q: WSL2 換了 Windows 主機後只看到授權頁

授權綁定 Windows 主機的主機板識別碼；WSL2 搬到另一台主機後識別碼不同，系統只顯示授權頁（預期行為）。
在新主機上執行 `dotnet run --project SmartFactory.MES.MachineId` 取得申請碼，重新申請授權，
收到後以新授權編號重新加密連線字串。

### Q: 建置出現 CS0117 或找不到類型

檢查是否在核心路徑下新增了需要核心修改的程式碼。功能模組只能使用核心提供的介面。

### Q: pre-commit hook 阻止了提交

Hook 偵測到您嘗試修改核心路徑。請確認只修改 `Modules/{ModuleName}/` 目錄下的檔案。

### Q: 合併 upstream 時有衝突

1. 執行 `git status` 查看衝突檔案
2. 如果衝突在核心路徑（非 Modules/）→ 接受 upstream 版本（`git checkout --theirs <file>`）
3. 如果衝突在 Modules/ → 手動解決並保留您的業務邏輯
4. 完成後 `git add . && git commit`

---

## 聯絡方式

- **License 申請/更新**：以申請碼至登記頁申請（`SmartFactory.MES.MachineId` 或系統「授權狀態」頁會給登記網址）；其他授權問題請洽系統維護者（勿在公開 Issue 貼申請碼以外的機器資訊）
- **技術支援**：https://github.com/TsaiFisher
- **Core Repo Issues**：https://github.com/TsaiFisher/SmartFactory.MES/issues

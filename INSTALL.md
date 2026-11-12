# 安裝指南

從零到系統跑起來。授權條款與免責請先看 [LICENSING.md](LICENSING.md)。

---

## 1. 系統需求

| 項目 | 需求 |
|---|---|
| .NET | 10 SDK（執行用 ASP.NET Core Runtime 10 即可，但要建置與跑測試需 SDK）。Ubuntu 24.04／26.04：`sudo apt install dotnet-sdk-10.0` |
| 資料庫 | PostgreSQL 15 以上 |
| OS | Linux、Windows、或 WSL2 皆可 |
| 記憶體 | 建議 4 GB 以上 |

---

## 2. 取得程式碼

```bash
git clone https://github.com/TsaiFisher/gemba-mes.git
cd gemba-mes
```

---

## 3. 取得授權檔（必要，且要先做）

**本系統在任何環境都需要有效的 `license.json` 才能啟動，開發環境也一樣。**
先把這一步做完，否則後面每一步都會卡在同一個錯誤。

### 3.1 產生機器錨定碼

```bash
dotnet run --project SmartFactory.MES.MachineId -- --init
```

這會產生一個一次性的隨機識別碼，寫到：

| | 路徑 |
|---|---|
| Linux（系統層） | `/etc/smartfactory/instance-id` |
| Linux（使用者層，無 root 時） | `~/.config/smartfactory/instance-id` |
| Windows | `%ProgramData%\SmartFactory\instance-id` 或 `%LocalAppData%\...` |

> **⚠️ 這個檔案等同金鑰材料，請比照密碼保護。** 它是環境指紋的唯一輸入。
> 正式環境請放系統層並收緊權限：
>
> ```bash
> sudo install -d -m 0755 -o root -g root /etc/smartfactory
> sudo cp ~/.config/smartfactory/instance-id /etc/smartfactory/instance-id
> sudo chown root:<服務帳號> /etc/smartfactory/instance-id
> sudo chmod 0640 /etc/smartfactory/instance-id
> ```
>
> **備份它。** 弄丟了就換一個身分——授權會失效，而且如果你用過連線字串加密，
> 那份加密值也再也解不開。
>
> 程式**不會**自動重建這個檔案。這是刻意的：自動產生會讓「授權失效」偽裝成
> 「悄悄換了一個身分」。

### 3.2 取得申請碼

在**要執行系統的那台機器**上：

```bash
dotnet run --project SmartFactory.MES.MachineId
```

輸出包含：

| 項目 | 說明 |
|---|---|
| 環境指紋 | `SHA256("v2:" + 錨定碼)`，64 個大寫十六進位字元 |
| 部署類型、主機識別碼 | 授權綁定這台機器的主機板（WSL 綁 Windows 主機）；虛擬機／容器讀不到時改為每 90 天確認一次 |
| **申請碼**、**登記網址** | 申請碼＝上面幾項（皆為雜湊）的編碼；登記網址已帶入申請碼 |

> Linux 實體機若顯示「主機識別碼：讀不到」：一般帳號讀不到主機板識別碼，請以 root 執行，
> 或先依使用手冊「授權與防複製」第 4 節設定 systemd 的 `ExecStartPre`。
> `tools/get-fingerprint.sh` 只算得出指紋、沒有主機識別碼，**不能用來申請**。

### 3.3 申請

用任何能上網的瀏覽器開啟**登記網址**，填寫組織與聯絡資料送出；或把**申請碼**整串交給你的導入顧問。

> 🚨 **提供申請碼，不要提供錨定碼。** 錨定碼長得像 UUID（`--raw` 才會顯示）。
> 它是金鑰材料，交出去就等於讓別人能冒用這台機器的身分。

授權免費、永久有效、需登記。每個組織第一次登記由維護者人工核准，授權檔以 Email 寄出。
簽章在維護者本機離線完成（私鑰不上雲端），所以不是自動回覆，請給幾天時間。

### 3.4 放置

把收到的授權檔改名為 `license.json`，放在與 `SmartFactory.MES.csproj` 同層——建置時會複製到執行目錄，
系統只讀**執行目錄**裡的 `license.json`（Security DLL 不看 `License:Path`）。換授權檔後要重新建置（`dotnet build SmartFactory.MES.csproj`）；
系統已在運作時，也可由管理員在「授權狀態」頁（選單「管理系統 → 授權狀態」，或網址 `/License`，例如 `http://localhost:5110/License`）匯入。**不要編輯它**——所有欄位都在簽章內，改了就是「簽章無效」。

> 換了機器或主機板（包含把 WSL 整顆搬到另一台 Windows 主機），系統會只顯示授權頁、不連資料庫。
> 授權頁上有新機器的申請碼，用它重新申請即可。

### 3.5 下載 Security DLL

從 [Releases](https://github.com/TsaiFisher/gemba-mes/releases) 下載與你的程式碼**同版本**的
`SmartFactory.MES.Security.dll`，放到 `Security/` 目錄（與 `SmartFactory.MES.csproj` 同層）：

```bash
mkdir -p Security
cp ~/Downloads/SmartFactory.MES.Security.dll Security/
```

或用 GitHub CLI 下載並驗證（**一定要寫版本 tag**——預先發行版〔如 `v1.0.0-rc1`〕不算「最新版」，
不寫 tag 的 `gh release view`／`download` 找不到它）：

```bash
gh release download <版本tag> --repo TsaiFisher/gemba-mes -D Security
(cd Security && sha256sum -c SmartFactory.MES.Security.dll.sha256)
```

它負責授權驗證與資料庫連線字串解密（§5）。授權信只附授權檔，DLL 一律從 Releases 下載。

> 建置時才會把 `Security/` 裡的 DLL 複製到輸出目錄——換過 DLL 一定要重新 `dotnet build SmartFactory.MES.csproj`。
> 系統會拒收舊版 DLL：**升級系統時一併換成同版本的 DLL**（§8）。

---

## 4. 資料庫

用隨附的 compose（先在同目錄建立 `.env`，填 `DB_PASSWORD=<強密碼>`）：

```bash
docker compose up -d db
```

它會建立資料庫 `SmartFactoryMES`、帳號 `dev`，只綁本機 `127.0.0.1:5432`。

或自備 PostgreSQL（15 以上），建立一個空資料庫即可——schema 由應用程式在首次啟動時建立。
以 Ubuntu／WSL 為例：

```bash
sudo apt install -y postgresql
sudo -u postgres createuser --pwprompt dev        # 互動輸入密碼；不要把密碼寫在指令裡（會留在 shell 歷史）
sudo -u postgres createdb -O dev SmartFactoryMES
```

> **5432 已被佔用時**（同一台已有別的 PostgreSQL；**WSL 的所有發行版共用同一組網路埠**，
> 另一個發行版裡的 PostgreSQL 也算），把這一套改到別的埠，例如 5433：
> 改 `/etc/postgresql/<版本>/main/postgresql.conf` 的 `port = 5433` 後 `sudo systemctl restart postgresql`，
> 上面兩行指令加 `-p 5433`，§5 的連線字串加 `Port=5433;`。

---

## 5. 設定（加密連線字串）

資料庫連線字串一律加密，明文不落地。用 §3.4 的授權編號（`license.json` 裡的 `licenseId`）
在**要執行系統的那台機器**上加密：

```bash
dotnet run --project tools/EncryptConnectionString -- \
  --license-id <licenseId> \
  --plaintext "Host=localhost;Database=SmartFactoryMES;Username=dev;Password=<密碼>" \
  --this-host --verify
```

`--verify` 會當場解密一次並只印出 ✅／❌（不顯示解密內容），貼錯或選錯參數在這一步就看得出來。
直接把密碼寫在 `--plaintext` 裡會留在 shell 歷史。建議先互動讀進變數，歷史只會記下 `$PGPW`：

```bash
read -rsp '資料庫密碼：' PGPW; echo
dotnet run --project tools/EncryptConnectionString -- \
  --license-id <licenseId> \
  --plaintext "Host=localhost;Database=SmartFactoryMES;Username=dev;Password=$PGPW" \
  --this-host --verify
unset PGPW
```

最後一個參數依部署類型（§3.2 MachineId 印出的「部署類型」）選擇：

| 部署類型 | 參數 | 輸出 |
|---|---|---|
| 實體機、WSL | `--this-host`（綁這台的主機板） | `ENC:v3:...` |
| 虛擬機、容器 | `--no-host-binding`（這類授權不綁主機，改為每 90 天確認） | `ENC:v2:...` |

選錯會在啟動時被拒（「此授權綁定主機，連線字串必須以 ENC:v3 重新加密」或
「連線字串為 ENC:v3…但此授權沒有列主機識別碼」）。Linux 實體機讀主機板識別碼需要 root，
一般帳號請先依 §3.2 的說明設定，或以 `sudo` 執行。

輸出的加密值填進 `appsettings.Development.json`（正式環境用 `appsettings.Production.json`）：

只需要建一個只含連線字串的小檔，其餘設定沿用 `appsettings.json`（不要整份複製再改，容易漏改或改錯地方）：

```bash
cat > appsettings.Development.json <<'JSON'
{
  "ConnectionStrings": {
    "DefaultConnection": "",
    "DefaultEncrypted": "ENC:v3:...（貼上加密指令的輸出）"
  }
}
JSON
```

- 金鑰由環境指紋、授權編號與主機板識別碼導出：**換授權編號或換機器就要重新加密**；
  整台複製到別台機器時解不開，這正是它的用途。
- `DefaultConnection` 留空。明文連線字串只有授權明示允許時才能使用，一般授權不允許。
- 檔案權限仍建議收緊——正式環境 `chmod 0640`，擁有者為服務帳號。

---

## 6. 啟動

```bash
dotnet run
```

首次啟動會自動套用 EF migration、建立系統種子資料（代碼主檔、權限、預設角色）。

預設網址 `http://localhost:5110`（要 HTTPS：`dotnet run --launch-profile https`，網址 `https://localhost:7054`）。
首次啟動要建立全部資料表，約半分鐘。畫面出現 `Now listening on:` 就表示可以連線；
也可以在另一個視窗反覆執行 `curl http://localhost:5110/health`，回 `{"status":"Healthy"}` 即完成。
啟動失敗時程式會印出原因並以錯誤碼 1 結束，對照 §9 處理。

**5110／7054 已被佔用時**（同一台已有另一套系統在跑；WSL 的所有發行版共用同一組網路埠），改用別的埠啟動。
`--no-launch-profile` 會連同環境設定一起略過，所以要自己帶 `ASPNETCORE_ENVIRONMENT`：

```bash
ASPNETCORE_ENVIRONMENT=Development dotnet run --no-launch-profile --urls http://localhost:5210
```

之後本節的網址一律換成 `http://localhost:5210`。
初始管理員帳號 `admin`、密碼 `Dev12345`，**首次登入會要求變更密碼**。

確認授權正常：

1. `curl http://localhost:5110/health` 回 `{"status":"Healthy"}`。主機識別碼不符或讀不到時回 503、網頁只顯示授權頁；
   授權檔缺少、簽章無效、指紋不符則根本啟動不了，錯誤訊息見 §9。
2. 以管理員登入，開「授權狀態」頁（選單「管理系統 → 授權狀態」，或網址 `/License`）：授權編號與收到的相同、「主機比對」為符合（虛擬機／容器另依頁面指示按一次「確認授權」）。

---

## 7. 正式環境

- **反向代理 + HTTPS**：本系統不自帶憑證管理，請放在 nginx / IIS / Caddy 後面。
- **檔案權限**：`appsettings*.json`（含連線字串）與錨定檔都要收緊。
- **備份三樣東西**：資料庫、`license.json`、**機器錨定檔**。
  只備份資料庫的話，機器重灌後系統起不來。
- **不要用 `ASPNETCORE_ENVIRONMENT=Development` 跑正式環境**——
  它會開啟詳細錯誤頁與開發用的寬鬆設定。
- **防火牆放行**：系統每天讀一次 `raw.githubusercontent.com`（`TsaiFisher/smartfactory-licensing` 的終止聲明，
  只讀不送資料）；申請授權與虛擬機／容器每 90 天的確認，由管理員的**瀏覽器**連
  `license.gemba-mes.workers.dev`，伺服器本身不必對它放行。會送出什麼見 [LICENSING.md](LICENSING.md)。

---

## 8. 升級

見 [SYNCING.md](SYNCING.md)。摘要：`git fetch upstream && git merge upstream/main`，
衝突自己解，然後從 [Releases](https://github.com/TsaiFisher/gemba-mes/releases) 下載**同版本**的
`SmartFactory.MES.Security.dll` 換掉 `Security/` 裡的舊檔，再：

```bash
dotnet build SmartFactory.MES.csproj
dotnet ef database update      # 若該版有新 migration
```

> 建置一律指定 `SmartFactory.MES.csproj`。根目錄同時有 `.sln` 與 `.csproj`，只打 `dotnet build`
> 會建方案檔，連測試專案一起建、多花時間；rc3 以前的方案檔還有缺陷，會「0 錯誤」卻什麼都沒建，
> 跑的仍是舊程式。

**升級前備份資料庫。** migration 不保證可逆。授權檔與加密連線字串不必重做。

---

## 9. 疑難排解

下表提到的「授權狀態」頁：選單「管理系統 → 授權狀態」（只有目前身份為 Admin 時出現），或直接開網址 `/License`。
授權不屬於這台電腦時，系統只會顯示這一頁。

啟動失敗的訊息與對應處置：

| 訊息 | 意思 | 怎麼辦 |
|---|---|---|
| `找不到機器錨定碼，無法計算環境指紋` | 還沒跑過 `--init`，或錨定檔權限讀不到 | 執行 §3.1；已存在則檢查權限 |
| `找不到授權檔案：<路徑>` | 執行目錄沒有 `license.json` | 放在 `SmartFactory.MES.csproj` 同層後重新 `dotnet build SmartFactory.MES.csproj`（§3.4） |
| `授權檔案簽章無效，可能已被竄改` | 檔案被改過，或簽發時用錯私鑰 | 重新申請，不要手動編輯授權檔 |
| `目前環境未在授權清單中` | 指紋不符——換過機器、重灌、或錨定檔被覆寫 | **重新申請授權**（申請碼見 §3.2） |
| 網頁只顯示「此授權不屬於這台電腦」 | 主機識別碼不符：換了機器／主機板，或整台被複製 | 用授權頁上的申請碼重新申請 |
| 網頁只顯示「無法確認本機是授權機器」 | 讀不到主機識別碼 | 依頁面訊息修復（WSL：`sudo systemctl restart systemd-binfmt`；Linux：systemd 設定），再重新啟動 |
| `授權已於 <日期> 到期` | 有效期限的授權（非永久授權）效期屆滿，系統轉為唯讀 | 重新申請 |
| `授權確認已於 <日期> 到期` | 虛擬機／容器超過 90 天未確認，系統轉為唯讀 | 管理員到「授權狀態」頁按「確認授權」 |
| `此授權以舊版指紋演算法（v1）核發` | 2026-09-14 之前的舊授權 | 重新申請 |
| `授權有效（…），但尚未設定加密連線字串`（舊版：`目前授權不允許使用明文連線字串`） | 授權沒問題，只是還沒有 `DefaultEncrypted`（例如 `appsettings.Development.json` 還沒建） | 依 §5 加密並建檔，`DefaultConnection` 留空。**不需要重新申請授權** |
| `找不到 Security DLL…但 DefaultEncrypted 已設定` | `Security/` 沒有 DLL，或放了之後沒重新建置 | 依 §3.5 放好 DLL，再 `dotnet build SmartFactory.MES.csproj` |
| `Security DLL 版本過舊` | DLL 比系統舊 | 從 Releases 下載與系統同版本的 DLL（§3.5） |
| `連線字串必須以 ENC:v3 重新加密`、`連線字串格式錯誤` | 加密值是舊格式或貼錯 | 依 §5 以 `tools/EncryptConnectionString` 重新加密 |

> **「指紋不符」只能換發授權，不要想辦法跳過。** 繞過驗證違反
> [Elastic License 2.0](LICENSE) 的第二條限制。申請是免費的，也很快。

其他問題請開 [Issue](https://github.com/TsaiFisher/gemba-mes/issues)，
資安問題請走 [SECURITY.md](SECURITY.md) 的私密管道。

# Windows／IIS 部署（測試環境）

> 本文為 ProjectZ 部署專案的操作手冊；IIS 託管的可行性判斷與架構取捨見 `DEVELOPMENT.md` §3.6。
> 2026-09-25 於測試筆電實作完成，以下數值、指令與陷阱**均為實測**，非推論。
> 搭配腳本：`deploy/iis-setup.ps1`（只做需要管理員權限的部分，可重複執行）。

> ⚠️ **2026-10-01 起，日常測試環境已改用 WSL**（穩定版服務＋開發模式切換，見 `deploy/wsl-stable-release.md`）。
> 測試筆電上的這份 IIS 部署測試完成後即可移除；本文保留作為日後部署到客戶 Windows 主機時的參考。
>
> **移除前請注意**：這個 IIS 站台連的是桌機的 `ProjectZ` 資料庫，且集區設為一直執行——
> 只要筆電開機，它就會與 WSL 穩定版**對同一個資料庫各跑一次每日排程**。
> 要移除的項目就是 `iis-setup.ps1` 建立的那些：站台與集區 `SmartFactoryMES`、7054 的 SSL 繫結、
> `C:\inetpub\SmartFactoryMES`、`C:\inetpub\SmartFactoryMES-keys`、`C:\inetpub\SmartFactoryMES-backup-*`、
> `C:\ProgramData\SmartFactory`（錨定檔）、防火牆規則 `SmartFactoryMES-IIS`、`mes-dev` 伺服器憑證與開發用根 CA。
> **不要動**同一台 IIS 上的舊版 Web Forms `ProjectZ` 站台（`E:\Data\PZ`，88／443）與 `Default Web Site`（80）。
> Hosting Bundle 是否移除視日後是否還要在這台部署 Windows 版而定。
>
> 手機測試不因改用 WSL 而改變：目前的手機與筆電都被政策禁裝 CA，仍需 Cloudflare Tunnel（憑證限制見 §6）。

---

## 0. 為什麼要放 IIS

| 好處 | 說明 |
|------|------|
| 手機直連 | 站台掛在 Windows 網路堆疊，手機連開發機 IP 即可。不需要 mirrored 模式、`netsh portproxy` 或 Cloudflare Tunnel |
| Ollama 方向反轉 | `localhost:11434` 經 WSL relay 連到 WSL 內的 Ollama，組態不用改。原本「app 在 WSL、Ollama 在 Windows」連不到的痛點消失 |
| 貼近正式環境 | 驗到 IIS 託管路徑、集區身分、DataProtection 金鑰等只有 IIS 才有的問題 |

代價是集區回收、金鑰持久化、權限這幾個 WSL 上永遠踩不到的坑，下面逐一處理。

---

## 1. 前置

### 1.1 ⚠️ 要裝的是 Hosting Bundle，不是 Runtime

**這是本次部署唯一真正卡住的一步，而且三個訊號會一起騙你。**

IIS 託管 ASP.NET Core 需要 **ANCM（ASP.NET Core Module V2）**，它**只**由 Hosting Bundle 提供。
共用框架（`Microsoft.AspNetCore.App`）不含 ANCM。

| 假訊號 | 為什麼會誤判 |
|--------|------------|
| `dotnet --list-runtimes` 看得到 `Microsoft.AspNetCore.App 10.0.x` | 那是共用框架，不含 ANCM |
| 解除安裝清單有「Microsoft ASP.NET Core Module V2 **for IIS Express**」 | 名字幾乎一樣，但 IIS 用不到那一份 |
| 在下載頁點了「Runtime」或「SDK」 | 兩者與 Hosting Bundle 在同一區塊 |

**三項判準（要全綠才算裝好）：**

```powershell
# 1) 解除安裝清單必須有 "Windows Server Hosting"（注意：DisplayName 不含 "Hosting Bundle" 字樣，
#    用那個字串去搜尋不管裝沒裝都會是空的——這條查錯過一次）
Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
                 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' |
    Where-Object { $_.DisplayName -match 'Windows Server Hosting' } |
    Select-Object DisplayName, DisplayVersion

# 2) 模組檔案存在
Test-Path 'C:\Program Files\IIS\Asp.Net Core Module\V2\aspnetcorev2.dll'

# 3) 已註冊進 IIS
Select-String 'AspNetCoreModuleV2' C:\Windows\System32\inetsrv\config\applicationHost.config
```

**下載檔名 `dotnet-hosting-10.0.x-win.exe`，約 112 MB。純 runtime 只有 ~11 MB
——用檔案大小一眼就分得出來。** 裝完必須 `iisreset`，模組是 IIS 啟動時載入的。

`deploy/iis-setup.ps1` 的步驟 0 會把這三項的狀態逐條印出來，不會只說「尚未安裝」。

### 1.2 先看現有站台與可用埠

```powershell
Import-Module WebAdministration
Get-Website | Format-Table Name, State, PhysicalPath,
    @{n='Bindings';e={$_.bindings.Collection -join '; '}}
```

測試筆電上的實況（2026-09-25）：

| 站台 | 實體路徑 | 繫結 |
|------|---------|------|
| Default Web Site | `C:\inetpub\wwwroot` | http 80 |
| **ProjectZ**（舊版 Web Forms，**與本專案無關，勿動**） | `E:\Data\PZ` | http 88、https **443** |
| SmartFactoryMES | `C:\inetpub\SmartFactoryMES` | http 5110、https 7054 |

**443 已被舊系統佔走**，所以 https 用 **7054**（與既有防火牆規則 `5110,7054` 一致）。

### 1.3 實體路徑：`C:\inetpub\SmartFactoryMES`

- **不要放在 `C:\Users\<user>\` 底下**：使用者設定檔的 ACL 不會讓
  `IIS AppPool\SmartFactoryMES` 穿透，站台會在讀檔時失敗，而畫面上通常只給你 500.30。
- **不要用 UNC／`\\wsl.localhost` 路徑**：IIS 透過 UNC 取檔需要認證，而且會隨 WSL 起停而斷。
- `C:\inetpub` 底下有 IIS 預期的權限繼承。

### 1.4 ⚠️ 必須是站台根目錄，不可掛成虛擬路徑子應用程式

手機 PWA 的路徑是寫死的根目錄絕對路徑：

```
wwwroot/mobile/manifest.json   start_url: "/mobile/index.html"
wwwroot/mobile/index.page.js   register('/mobile/sw.js', { scope: '/mobile/' })
```

掛在 `/mes` 這類虛擬路徑下時實際網址變成 `/mes/mobile/...`，但這些字串不會跟著變：
Service Worker 註冊 404、manifest 圖示 404、`start_url` 指到站外，而且 SW 作用域比對是
**字串前綴**，即使檔案找得到 scope 也對不上。**這些失敗都不報錯，只會安靜地少掉離線佇列、
PWA 安裝與推播。**

### 1.5 不要在 IIS 管理員裡手動建站台

手動建出來的集區是全預設（`managedRuntimeVersion` 繼承 `v4.0`、沒有任何回收設定），
四項守門一個都沒有。用腳本建，或建完再逐項改。

---

## 2. 產生發布包（在開發機上做，不要在目標機建置）

```bash
dotnet publish SmartFactory.MES.csproj -c Release -o <輸出目錄>
```

portable（非 self-contained、不指定 RID）即可，Hosting Bundle 已提供 runtime。
`dotnet publish` 會自動產生 `web.config`（`AspNetCoreModuleV2` / `hostingModel="inprocess"`），
**不要手寫**。

發布包裡另外放：`SmartFactory.MES.MachineId.exe`（`-r win-x64 --self-contained false
-p:PublishSingleFile=true`）、`mes-dev.pfx`、`rootCA.cer`、`appsettings.Production.json.template`、
`iis-setup.ps1`。

**刻意不要放進發布包：**

| 檔案 | 原因 |
|------|------|
| `license.json` | 綁機器指紋，別台的解不開；目標機的要等錨定碼產生後才核發得出來 |
| `appsettings.Development.json` | 裡面的 `DefaultEncrypted` 綁原主機 `anchorId`，帶過去只會製造混淆 |
| `node_modules/` | 只有 jest 在用，執行期完全不需要（66 MB） |

實測大小：發布產物 **158 MB**（其中 `wwwroot` 75 MB，`wwwroot/docs` 手冊占 57 MB）。

**首次部署建議把 `web.config` 的 `stdoutLogEnabled` 改成 `true`**——500.30 常常畫面上
什麼都沒有，靠 `logs\stdout_*.log` 才看得到真因。確認站台正常後改回 `false`（它會持續寫入且不滾檔）。

---

## 3. 執行 `iis-setup.ps1`

```powershell
Start-Process powershell -Verb RunAs -ArgumentList `
    '-ExecutionPolicy Bypass -NoExit -File <發布包>\iis-setup.ps1'
```

預設從腳本所在目錄讀發布包，所以把它複製到發布包根目錄再跑最省事。可重複執行（會整個重建同名站台與集區）。

### ⚠️ 集區的四項設定是守門，不是效能調校

`Program.cs` 註冊了 **13 支 `AddHostedService`**（授權看門狗、六支每日排程、稽核等）。
IIS 預設**閒置 20 分鐘就關閉集區**、**每 29 小時固定回收**，集區一關背景服務就停；
排程的作法是「算出下一個固定時刻再 `Delay`」，重啟後重算，補跑又只在每次啟動跑一次
→ **排程看起來有註冊、實際永遠不執行，而且完全無聲**（同一類缺陷在 WSL 上踩過一次）。

| 設定 | 值 |
|------|-----|
| .NET CLR 版本 (`managedRuntimeVersion`) | **空字串**＝沒有受控碼（選錯是 500.30） |
| 啟動模式 (`startMode`) | `AlwaysRunning` |
| 閒置逾時 (`processModel.idleTimeout`) | `00:00:00` |
| 定期回收 (`recycling.periodicRestart.time`) | `00:00:00`（停用 29 小時回收） |
| 站台 `applicationDefaults.preloadEnabled` | `True` |

**漏設的話任何排程相關的驗收都是假綠。**

### 集區身分需要的權限

| 目錄 | 權限 | 沒有的症狀 |
|------|------|-----------|
| `<站台>\logs\` | 寫 | 沒有日誌檔，診斷只剩 Windows 事件檢視器 |
| `<站台>\App_Data\reports\` | 寫 | 報表下載 500（`App_Data/reports` 是相對路徑） |
| `<站台>\wwwroot\uploads\` | 寫 | 上傳 500 |
| `C:\inetpub\SmartFactoryMES-keys\` | 寫 | DataProtection 金鑰，見 §5 |
| `C:\ProgramData\SmartFactory\` | 讀 | 讀不到錨定檔，站台起不來 |

---

## 4. 錨定碼與授權

```
SmartFactory.MES.MachineId.exe --init
SmartFactory.MES.MachineId.exe          # 顯示指紋、錨定碼來源、主機識別碼與申請碼
```

錨定檔應落在 `C:\ProgramData\SmartFactory\instance-id`。
**若顯示的錨定碼來源是 `%LocalAppData%` 底下的路徑**，代表 `ChooseWritablePath()` 退位了，
以集區身分執行會讀不到——先把 `C:\ProgramData\SmartFactory` 建好再重跑 `--init`。

`ApplyRestrictivePermissions()` 在 Windows 直接 return（不做任何 ACL 收緊），檔案沿用
`%ProgramData%` 的繼承 ACL，因此集區身分讀得到，不需額外授權。驗證方式：

```powershell
icacls C:\ProgramData\SmartFactory\instance-id
# 應看到 IIS APPPOOL\SmartFactoryMES:(I)(R)
```

### ⚠️ 核發時不可用 `--internal`

兩個獨立的理由，各自都足以讓站台起不來：

1. `--internal` 會**自動抓「執行簽發工具那台機器」的指紋**。在 WSL 上簽發就會綁到 WSL 的
   錨定碼，不是 Windows 那顆。
2. `--internal` 把 `clientCode` 設成 `SMARTFACTORY-INTERNAL`，而 `LicenseValidator` 有
   `isInternal && !_env.IsDevelopment()` 的擋門 —— **IIS 跑的正是 Production**，直接拒絕啟動。

正確作法是以目標機印出的**申請碼**核發非 internal 的授權（`--request` 依部署類型自動帶入指紋與主機識別碼；
非 internal 的 offline 授權沒有主機識別碼會被工具拒絕）：

```bash
dotnet run --project SmartFactory.MES.LicenseAuthority -- issue \
  --request      "<MachineId 印出的申請碼>" \
  --license-id   "LIC-DEV-<機器>-IIS" \
  --client-code  "SMARTFACTORY-DEV" \
  --client-name  "<用途>" \
  --private-key  "Resources/license-private.pem" \
  --allow-plain \
  --output       "<輸出路徑>"
```

`--allow-plain` 是必要的：`DefaultEncrypted` 的密文綁原主機 `anchorId`，新機器解不開，
所以測試部署走明文連線字串。`allowPlainConnection` 在 RSA 簽章 payload 內，**部署端無法自行開啟**。

把核發的 `license.json` 放到站台根目錄。

> 搬運授權檔時注意：JSON 裡的 `\u002B` 是轉義的 `+`，**不可手動改回 `+`**，
> 那是簽章的一部分，改了驗簽就過不了。

---

## 5. 連線字串

站台根目錄放 `appsettings.Production.json`（IIS 預設 `ASPNETCORE_ENVIRONMENT=Production`）：

```jsonc
{
  "ConnectionStrings": {
    // Host=localhost 經 wslrelay 接到 WSL 的 5432
    "DefaultConnection": "Host=localhost;Port=5432;Database=<庫名>;Username=<帳號>;Password=<密碼>",
    "DefaultEncrypted": ""          // 留空＝走明文路徑，由授權的 allowPlainConnection 放行
  },
  "AllowedHosts": "*",
  "DataProtection": {
    "KeysDirectory": "C:\\inetpub\\SmartFactoryMES-keys"
  },
  "Database": { "AutoMigrate": true }
}
```

### IIS（Windows）→ PostgreSQL（WSL）

**已實測可行**，先確認再往下：

```powershell
Test-NetConnection -ComputerName localhost -Port 5432
# TcpTestSucceeded : True
```

- **PG 維持只綁 `127.0.0.1` 就好**——WSL2 的 localhost 轉送會進到 distro 內的 loopback。
  **不要為此把 PG 改綁 `0.0.0.0`。**
- 方向很重要：**Windows → WSL 是 WSL2 內建就通的那一邊**；會痛的是反方向（WSL → Windows），
  那個才需要 SSH 反向通道。
- **`pg_hba.conf` 不用改**，但要注意兩條規則的差異：
  `local all all peer`（WSL 內免密碼）vs `host all all 127.0.0.1 scram-sha-256`
  ——經 relay 進來的是 **host** 規則，**連線字串必須帶密碼**。在 WSL 裡 `psql` 不用密碼
  不代表 Windows 端也不用。
- **WSL 必須先起來。** IIS 隨開機自動啟動，WSL 不會；WSL 沒跑時站台啟動就連不到 5432。
  `wsl --shutdown` 或 WSL 閒置自動關機也會斷線。

### `DataProtection:KeysDirectory` 不可省

不設的話金鑰落在應用程式集區身分的 `%LOCALAPPDATA%`，集區回收或改身分就**全員被登出、
antiforgery token 失效**。這是 IIS 特有、在 WSL 上永遠踩不到的坑。

#### 金鑰加密：`ProtectKeysWithDpapi`（2026-09-27 已修）

`PersistKeysToFileSystem()` 單獨使用時，金鑰是**明文**落地：`key-*.xml` 裡的
`<descriptor>/<value>` 就是主金鑰本體，拿到那個檔的人可以偽造登入 cookie 與
antiforgery token。啟動日誌會出現：

```
WRN Microsoft.AspNetCore.DataProtection.KeyManagement.XmlKeyManager:
    No XML encryptor configured. Key {...} may be persisted to storage in unencrypted form.
```

這**不是可以忽略的雜訊**。2026-09-25 首次部署時實測確認：金鑰目錄下
`encryptedSecret` 數量為 0，唯一的保護是目錄 ACL（實測只有 `IIS APPPOOL\<集區>`、
`SYSTEM`、`Administrators`、`TrustedInstaller`，`BUILTIN\Users` 不在清單內）。
單層防護，而且**檔案備份、磁碟映像、任何以系統管理員身分執行的東西都會把可偽造的
憑材一起帶走**。

`Program.cs` 已補上（commit `041f3f28`）：

```csharp
if (OperatingSystem.IsWindows())
    dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: true);
```

三個判斷值得記下來：

- **只在 Windows 加密**是刻意的。那段的原始設計目標是 Linux 容器（金鑰放掛載 volume
  以跨容器重建存活），DPAPI 在那裡不存在；即使存在，加密後 volume 也會綁死單一主機。
- **`protectToLocalMachine: true`**（而非 `CurrentUser`）的理由與機器錨定檔相同：
  以服務帳號執行時不需要載入使用者設定檔，且管理員重設該帳號密碼不會讓既有金鑰解不開。
- **用明確的 `if` 區塊而非三元式**，否則平台相容性分析器追不到，會誤報 CA1416。

#### ⚠️ 改完程式還不夠：舊的明文金鑰不會自動加密

`ProtectKeysWithDpapi` 只影響**之後產生**的金鑰。既有的 `key-*.xml` 會原封不動留在
磁碟上，明文照樣可讀，而金鑰輪替預設 90 天才一次。要讓明文真的消失，必須刪掉舊金鑰檔
讓它重新產生。

代價：**所有登入中的使用者被登出、待用的重設密碼／確認信連結失效**。不會毀掉持久化
資料——本專案 `CreateProtector` 使用點為 0，DataProtection 只服務框架自身。

`apply-dpapi-and-qa.ps1` 會一併處理（換組件 → 刪舊金鑰 → 重啟 → 驗證新金鑰含
`<encryptedSecret>` 且警告消失）。

### 資料庫選哪一個

| 選擇 | 適用 | 代價 |
|------|------|------|
| 沿用既有開發庫 | 要測掃碼／報表等需要真實資料的功能 | 與 WSL 上的開發 app 共用，同時跑會互相覆蓋；對「乾淨部署」類缺陷**零證據力** |
| 另開新庫 | 要驗乾淨部署 | 只有基礎種子，沒有可掃的料 |

---

## 6. 憑證（手機掃碼必須）

`getUserMedia`（相機）需要 secure context。兩個層次：

| 需求 | 自簽憑證點掉警告 | 受信任的 CA |
|------|-----------------|------------|
| 登入、API、**相機掃碼** | ✅ 可行（`isSecureContext` 仍為 `true`） | ✅ |
| Service Worker、離線佇列、PWA 安裝、推播 | ❌ **憑證鏈有錯就一律拒絕註冊**，點掉警告救不了 | ✅ |

所以優先**沿用既有的本機 CA**（`~/.cache/mes-certs/rootCA.pem`），手機已經裝過就不必重裝，
而且連 SW 那一層一起拿到：

```bash
# IP 要全部列出來，腳本不會自動累加（累加的是 start-lan.sh）
tools/make-dev-cert.sh <目前IP> <舊IP...>
openssl x509 -in ~/.cache/mes-certs/rootCA.pem -outform DER -out rootCA.cer
```

`iis-setup.ps1` 會把 `rootCA.cer` 匯入 `LocalMachine\Root`、`mes-dev.pfx` 匯入
`LocalMachine\My`，再建立 https 繫結。

- 效期必須 **≤ 398 天**，否則 Chrome 直接拒絕（`ERR_CERT_VALIDITY_TOO_LONG`）。腳本簽 397 天。
- `dotnet dev-certs` 的憑證因 `CA:FALSE` **不能用**。
- **LAN IP 是 DHCP，換了就要重簽**（新 IP 不在 SAN 內手機會擋）。CA 不變，手機不用重裝。
- 裝置政策可能全面禁裝 CA，見 `reference-mobile-cert-secure-context`。

---

## 7. 驗證

| # | 檢查 | 預期 |
|---|------|------|
| 1 | `http://localhost:5110/health` | 200 |
| 2 | `icacls C:\ProgramData\SmartFactory\instance-id` | 含 `IIS APPPOOL\<集區>:(I)(R)` |
| 3 | `Test-NetConnection localhost -Port 5432` | `TcpTestSucceeded : True` |
| 4 | 資料庫 | WSL 內 `\dt` 看得到表 |
| 5 | 集區設定 | `applicationHost.config` 內四項守門齊全 |
| 6 | 登入 | 需 secure context：`https://<IP>:7054` 或 `http://localhost:5110` |
| 7 | **排程** | **隔天**看日誌確認每日排程真的跑過——只看「有沒有註冊」驗不到這一條 |
| 8 | 手機 | `https://<IP>:7054/mobile/index.html`，掃碼與 SW 註冊 |

### 2026-09-25 測試筆電首次部署實測結果

| # | 結果 |
|---|------|
| 1 | `http://localhost:5110/health` → **200** `{"status":"Healthy"}` |
| 1b | `https://localhost:7054/health` → **200** |
| 2 | `IIS APPPOOL\SmartFactoryMES:(I)(R)` ✅ |
| 3 | `TcpTestSucceeded : True` ✅ |
| 4 | 沿用既有 `ProjectZ` 庫（186 表），`RolePermissionSeeder` 正確略過既有權限 |
| 5 | 四項守門齊全 ✅ |
| 6 | 授權驗證通過，到期 2026-12-24 |
| 7 | ⏸ 待隔天確認排程真的執行（日誌已印出各服務的下次執行時刻） |
| 8 | `https://<LAN IP>:7054/mobile/index.html` → **200**（實機掃碼待測） |

啟動日誌零 `ERR`／`FTL`。另注意 `Features:QaAssistant:Enabled=false`——
手冊 Q&A 在本部署未啟用，要測需另外開啟並確認 Ollama 連得到。

---

## 8. 疑難排解

| 症狀 | 原因 |
|------|------|
| HTTP 500.30 | 集區 CLR 版本沒選「沒有受控碼」，或 Hosting Bundle 沒裝 |
| HTTP 500.19 | `web.config` 壞了／ANCM 沒註冊 |
| 腳本說「連接埠已被佔用」但明明沒有 | 舊版腳本沒排除自己的站台。已修；衝突時會印出是哪個站台 |
| 「找不到授權檔案」 | `license.json` 還沒放進站台根目錄 |
| 「目前授權不允許使用明文連線字串」 | 核發時漏了 `--allow-plain` |
| 授權驗證失敗但檔案存在 | 指紋不符（綁到別台），或 `\u002B` 被改成 `+` 破壞簽章 |
| 「找不到機器錨定碼」 | `--init` 寫到 `%LocalAppData%` 而集區身分讀不到 |
| 內部授權在 Production 被拒 | 用了 `--internal`，見 §4 |
| 「載入 Security DLL 失敗：Exception has been thrown by the target of an invocation.」 | 真因包在兩層 `InnerException` 下。Core 的 `feat/anchor-dpapi` 已修（`SecurityDllErrors`） |
| 全員被登出／antiforgery 失效 | `DataProtection:KeysDirectory` 沒設或集區寫不進去 |
| 排程從來沒跑 | 集區四項設定漏了 |
| 啟動時連不到 5432 | WSL 沒起來，或連線字串沒帶密碼（`host` 規則是 scram-sha-256） |
| 手機連得到但相機不能用 | 走了 `http://<IP>`，不構成 secure context |
| 手機 SW 註冊失敗但其他正常 | 憑證未受信任，或目前 IP 不在憑證 SAN 內 |
| 排程整天沒跑，但站台一直是活的 | **機器睡眠**。見 §9 —— 這不是集區回收，四項守門擋不住 |
| PowerShell 腳本報一堆「缺少 `}`」且訊息是亂碼 | `.ps1` 沒有 UTF-8 BOM。Windows PowerShell 5.1 會當 Big5 讀，中文前導位元組把引號吃掉 |

---

## 9. ⚠️ 會睡眠的機器上，固定時刻排程不會執行

2026-09-26 在測試筆電實測到的。**這不是 IIS 集區回收**——§3 那四項守門運作正常，w3wp 從 09/25 17:16 一路活到 09/27 14:12，同一個進程、沒被回收過。

### 現象

| | |
|---|---|
| ETA 全量校準 | 09/25 17:17 排定 09/26 01:00 執行 → 到 09/27 14:12 為止**一次都沒跑** |
| 生產準備提醒 | 09/25 17:17 排定 09/26 17:00 執行 → 同樣沒跑 |
| 報表派工器 | 正常每 64 秒輪詢（它用的是短週期 Delay，不受影響） |

判讀關鍵：這兩支服務**每一輪迴圈都會無條件記一行「將於 … 執行」**，所以「日誌裡沒有這一行」只可能是「`Task.Delay` 沒回來」，不是「跑了但例外被吞掉」。

> ⚠️ 這個判讀手法**只適用於修正前的版本**。修正後（見下方「處置」）改成五分鐘輪詢，逐輪那行降成 `Debug`、`Information` 一天只剩一行，改以「遲到 Warning」判讀。

### 原因

`Task.Delay` 在 Windows 上**不會在系統睡眠期間前進**。對照電源事件：

| 睡眠 | 喚醒 | 時長 |
|---|---|---|
| 09/25 17:52:59 | 09/26 16:23:40 | 22h31m |
| 09/26 18:01:55 | 09/27 14:05:36 | 20h04m |

進程存活 45 小時，但**實際清醒只有約 2 小時 21 分**（36m + 1h38m + 7m）。ETA 校準要等 7h43m —— 清醒時間根本沒走到那裡。

更麻煩的是：喚醒後它**不會補跑，也不會提早觸發**，只是繼續等剩下的清醒時間。所以症狀是「完全無聲」：沒有錯誤、沒有警告、站台一切正常。

查電源事件的指令：

```powershell
Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Microsoft-Windows-Power-Troubleshooter'} |
  ForEach-Object { $d=@{}; ([xml]$_.ToXml()).Event.EventData.Data | % { $d[$_.Name]=$_.'#text' }
                   '{0} -> {1}' -f $d['SleepTime'], $d['WakeTime'] }
```

### 波及範圍

以下是**當時（修正前）**的狀態：

| 服務 | 啟動補跑 | 睡眠下的實際行為 |
|---|---|---|
| 6 支 `DailyJobServiceBase` 衍生（稽核、各類保留清理、報表保留） | ✅ 有 | 每次進程啟動補跑一次。機器睡覺但集區不重啟時，**整天不會跑** |
| `EtaRecalibrationBackgroundService` | ❌ 無 | 完全不跑 |
| `PreparationAlertBackgroundService` | ❌ 無 | 完全不跑 |
| `QueueTimeBackfillService` | ❌ 無 | 完全不跑 |

PR #113 的啟動補跑只加在基底類別上，那三支 MFG 服務沒有跟上——這是既存的不一致，睡眠只是把它放大成「永遠不跑」。

### 處置

**部署面（本手冊範圍）**：目標機若會睡眠，固定時刻排程一定失效。地端 Windows 部署必須關掉睡眠：

```powershell
powercfg /change standby-timeout-ac 0
powercfg /change hibernate-timeout-ac 0
```

正式環境用的 Linux 伺服器不睡眠，所以**這不是上線阻斷項**；但在筆電或會被暫停的 VM 上做排程驗收，結果一律不可信。

**程式面（2026-09-29 已修，分支 `fix/daily-job-sleep-resilient`，尚未合併）**：

- `DailyJobServiceBase` 的等待迴圈改成每輪最多睡五分鐘，醒來重讀牆鐘。判斷的單位是「最近一格已過的觸發時刻是哪一天」而不是「現在幾點」——後者在跨日的情境下看不到漏跑（16:00 睡、隔天 09:00 醒，17:00 的工作此刻「還沒到今天的 17:00」，昨天那一格就遺失了）。六支衍生排程全部受惠。
- 三支 MFG 服務全部改掛基底：ETA 全量校準（01:00）、QueueTime 回填（02:00）、生產準備提醒（17:00）。
- 新增 `ScheduledJobRun` 執行紀錄表（一支 migration），衍生排程可據此判斷「今天跑過了沒」。**啟動補跑於是變成免費的**——先前每次應用程式重啟都會讓 ETA 全廠重算一次、生產準備提醒重推一次，現在同一天只做第一次。
- 生產準備提醒另有一道「17:00 之前不送」的下限：它的內容是「明天的排程」，早上補送用的是半天前的快照，而且會把當天唯一的機會用掉。

> 部署面要知道的一件事：**這張表會在啟動時由 `db.Database.Migrate()` 自動建立**，不需要額外動作；一天最多十幾列，不需要保留期清理。要確認排程有沒有跑，直接查它比翻日誌快：
>
> ```sql
> SELECT "JobName", "JobDate", "Trigger", "Succeeded" FROM "ScheduledJobRuns"
> ORDER BY "StartedAtUtc" DESC LIMIT 20;
> ```

> 即使程式面修好，**地端 Windows 部署仍應關掉睡眠**：輪詢只保證「醒來後會補跑」，不保證「在 17:00 準時跑」。機器整晚睡著，17:00 的提醒還是要等到隔天有人開機才會送出。

# WSL 穩定版與開發模式切換（測試環境）

> 本文說明在開發機的 WSL 裡，如何同時擁有「隨時可測的穩定版」與「改完程式立刻測的開發模式」，以及兩者怎麼切換。
> 2026-10-01 於桌機 WSL（Ubuntu 24.04）實作完成，文中指令與錯誤訊息**均為實測**。
> 搭配檔案：`deploy/wsl/mes`（切換指令）、`deploy/wsl/mes-stable.service`（systemd 使用者服務）。
> Windows／IIS 版的部署見 `deploy/iis-deployment.md`。

---

## 1. 兩種模式

| | 穩定版 | 開發模式 |
|---|---|---|
| 用途 | 不受改程式影響、隨時可直接測 | 改完程式立刻測 |
| 怎麼跑 | systemd 使用者服務，開機自動啟動，當掉自動重開 | 照平常：VS Code F5 或 `dotnet watch` |
| 程式來源 | **已推上 GitHub** 的某個 commit（預設 `origin/main`） | 你的工作樹 `~/ProjectZ`（含未 commit 的改動） |
| 網址 | https://localhost:7054（http 5110 會轉到 https） | 同左 |
| 資料庫 | `ProjectZ` | 同左 |
| 環境 | Development | Development |

**兩種模式一次只能跑一種。** 它們用同一組埠與同一個資料庫；同時跑的話，每日排程會對同一個資料庫各執行一次。
`mes` 指令負責切換，並在衝突時擋下來。

登入狀態兩邊共用（同一組 DataProtection 金鑰），切換後不必重新登入。

Web Server 是 ASP.NET Core 內建的 **Kestrel**，不需要另外安裝 nginx 或 IIS。

---

## 2. 日常操作

### 2.1 看目前是哪個模式

```bash
mes status
```

會顯示模式、穩定版是哪個 commit、比 GitHub 的 main 落後幾個 commit、誰佔著 5110/7054、健康檢查結果。

### 2.2 改了程式，要用 F5 測

```bash
mes dev          # 停掉穩定版，並取消它的開機自動啟動
```

看到「✓ 已切到開發模式」後，照平常按 F5 或執行 `dotnet watch`。

> 忘了先 `mes dev` 就按 F5，會出現 `Failed to bind to address https://127.0.0.1:7054: address already in use`。
> 這不是程式壞了，是穩定版還佔著埠。

### 2.3 測完了，切回穩定版

1. **先停掉 F5／`dotnet watch`**
2. 執行：

```bash
mes stable
```

看到「✓ 穩定版已就緒：https://localhost:7054」即完成。

> 如果 F5 還沒停，`mes stable` 會列出佔著埠的進程並停止，**不會替你結束它**（以免誤殺正在用的東西）。

### 2.4 程式合併進 main 之後，更新穩定版

```bash
mes publish            # 發布 origin/main 最新的 commit
mes publish --test     # 先跑單元測試，全過才發布（多約 1 分鐘）
```

約 1–2 分鐘。若目前是穩定版模式，會自動重啟並做健康檢查；**新版起不來會自動退回上一版**。
若目前是開發模式，只會準備好新版，等你下次 `mes stable` 才啟用。

也可以指定要發布的版本（必須已推上 GitHub）：

```bash
mes publish origin/main~3        # 往前第 3 個 commit
mes publish 9d65520f             # 指定 commit
```

### 2.5 新版有問題，退回上一版

```bash
mes rollback
```

每次只往更早一版退；連續執行會一路往前退。最多保留 5 個發布。

```bash
mes list        # 列出保留中的發布，* 是目前使用的那一版
```

### 2.6 看日誌

```bash
mes logs         # 穩定版最近 80 行應用程式日誌＋systemd 紀錄
mes logs 300     # 最近 300 行
```

---

## 3. 第一次安裝（換機器或重灌 WSL 時）

本機（桌機 WSL）已安裝完成，以下步驟只在新環境需要。

### 3.1 前置

| 項目 | 確認方式 |
|---|---|
| WSL 有開 systemd | `systemctl --user is-system-running` 回 `running`（需 `/etc/wsl.conf` 有 `[boot] systemd=true`） |
| 使用者服務可常駐 | `loginctl show-user $(whoami) -p Linger` 回 `Linger=yes`；若否執行 `loginctl enable-linger $(whoami)`（**不需要 root**） |
| `~/bin` 在 PATH | `echo $PATH` 含 `/home/<你>/bin` |
| 開發環境本身可跑 | `~/ProjectZ` 能用 F5 正常啟動 |
| 這三個檔案存在 | `~/ProjectZ/license.json`、`~/ProjectZ/appsettings.Development.json`、`~/ProjectZ/Security/SmartFactory.MES.Security.dll` |

### 3.2 安裝

在 `~/ProjectZ` 執行：

```bash
install -m 755 deploy/wsl/mes ~/bin/mes
mkdir -p ~/.config/systemd/user
cp deploy/wsl/mes-stable.service ~/.config/systemd/user/
systemctl --user daemon-reload

mes publish --test     # 第一次發布
mes stable             # 啟動穩定版
```

### 3.3 更新 `mes` 指令本身

`deploy/wsl/` 下的檔案改了之後，重跑 3.2 的前三行即可（`mes publish` 不會自動更新指令本身）。

---

## 4. 檔案與目錄

| 位置 | 內容 |
|---|---|
| `~/bin/mes` | 切換指令（原始檔 `deploy/wsl/mes`） |
| `~/.config/systemd/user/mes-stable.service` | 服務設定（原始檔 `deploy/wsl/mes-stable.service`） |
| `~/mes-stable/releases/<時間>-<commit>/` | 每次發布的完整產物，內含 `RELEASE_INFO`（commit、標題、發布時間） |
| `~/mes-stable/current` | 指向目前使用的發布 |
| `~/mes-stable/current/logs/` | 穩定版自己的應用程式日誌（與 `~/ProjectZ/logs/` 分開） |
| `~/mes-stable/last-publish.log` | 最近一次 `dotnet publish` 的完整輸出，發布失敗時看這裡 |
| `~/.cache/wt-pz-release/` | 發布專用的 git worktree，不會動到你的 `~/ProjectZ` 工作樹 |

發布時會從 `~/ProjectZ` 複製 `license.json`、`appsettings.Development.json` 與 Security DLL 進發布目錄——
**之後改 `appsettings.Development.json` 不會影響已發布的穩定版**，要重新 `mes publish` 才會帶進去。

**換授權時**（重新核發、或整顆 WSL 搬到另一台電腦後改綁那台）：先把 `~/ProjectZ` 的三個檔換好——
`license.json`、以新 LicenseId 重新加密的 `DefaultEncrypted`、Security DLL——並以
`dotnet build SmartFactory.MES.csproj` 確認 `dotnet run` 能啟動，再執行 `mes publish`。穩定版不需另外處理授權。
搬到別台電腦後、還沒換授權前，穩定版開啟只會看到授權頁（主機識別碼不符），屬預期行為。

---

## 5. 資源用量（實測）

| 項目 | 用量 |
|---|---|
| 每個發布 | 硬碟 159 MB（保留 5 個 ≈ 800 MB） |
| 穩定版執行中 | 記憶體約 460 MB |
| 穩定版日誌 | 平常每天數 MB；設定上限每檔 100 MB、保留 30 檔 |

PostgreSQL、Ollama、.NET 執行環境與開發模式共用，不另外占用。

---

## 6. 限制與注意事項

- **只發布已推上 GitHub 的 commit。** 想讓穩定版包含某個改動，必須先 commit 並推上去（建議合併進 main 後再發布）。
  指定本機才有的 commit 會被拒絕：`…還沒推上 GitHub。穩定版只發布已推上去的 commit。`
- **兩種模式共用 `ProjectZ` 資料庫。** 在穩定版建的資料，切到開發模式看得到，反之亦然——這是刻意的（都是測試資料）。
- **WSL 重開機後**，若上次是穩定版模式，它會自動啟動；若上次是開發模式（`mes dev`），不會自動啟動。
- **手機測試**：本環境只綁 localhost，手機連不到。手機要測請用筆電 IIS 或 Cloudflare Tunnel
  （目前的手機與筆電都被政策禁裝 CA，見 `deploy/iis-deployment.md`）。
- 穩定版跑在 **Development** 環境：開發授權只允許此環境，開發用的「登入免 2FA」也只在此環境成立。**不可作為正式環境使用。**

---

## 7. 疑難排解

| 症狀 | 原因 | 處理 |
|---|---|---|
| F5 出現 `address already in use` | 穩定版還在跑 | `mes dev` 後再按 F5 |
| `mes stable` 說埠被佔用 | F5／`dotnet watch` 還在跑 | 停掉它再執行 `mes stable` |
| `mes publish` 說「發布失敗」 | 建置錯誤 | 看 `~/mes-stable/last-publish.log` |
| `mes publish` 說「找不到 …Security.dll」 | `~/ProjectZ/Security/` 沒有 DLL | 這個 DLL 不進版控，需從原開發環境複製過來 |
| 發布後「新版 120 秒內未就緒，自動退回」 | 新版啟動失敗 | `mes logs` 看原因；舊版已自動恢復 |
| `mes status` 健康檢查失敗、但服務 active | 啟動中（約 10–60 秒），或資料庫沒起來 | 稍等再看；仍失敗就 `mes logs` |
| 日誌出現 `ArgumentNullException … connectionString` | 發布目錄缺 Security DLL，加密連線字串解不開 | 確認 3.1 的 DLL 存在後重新 `mes publish` |

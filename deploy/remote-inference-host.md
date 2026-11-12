# 推論服務（Ollama）改放到另一台主機

> 適用對象：要把手冊 Q&A／自然語言查詢的推論服務，從「與 ProjectZ 同一台」搬到「內網另一台主機」的部署人員。
> 本文件整理自 2026-10-07 的 LLM 安全性評估（第 4 項）。

這裡講的是**內網中自己架設的另一台推論主機**，不是改接外部 LLM 供應商（OpenAI 等）。
外部供應商在規格中已裁決不做（`.claude/specifications/llm-qa-and-report-tools.md` 定案 #49），本文件不改變該裁決。

---

## 一、先知道的三件事

1. **Ollama 本身沒有任何身分驗證。** 任何連得到它埠號的人都能呼叫模型，
   也能看到送進去的內容（手冊段落、使用者提問，以及開啟資料查詢後的**營運資料**）。
   所以安全邊界完全靠「誰連得到」，Ollama 自己不會擋。
2. **ProjectZ 目前不會對推論端點送任何認證標頭**（沒有 API 金鑰設定）。
   因此現階段能用的限制手段是 **HTTPS＋來源 IP 白名單＋防火牆**。
   要改用 API 金鑰需要先修改程式，見〈七、已知限制〉。
3. **預設單機部署時什麼都不用做。** Ollama 預設只聽 `127.0.0.1:11434`，外部連不進來。
   最常見的錯誤，是為了讓別台連得到而把 `OLLAMA_HOST` 設成 `0.0.0.0`——
   這等於把模型和所有送進去的資料開放給整個網段。

---

## 二、建議架構

```
 ┌──────────── App 主機 ────────────┐          ┌──────────── 推論主機 ─────────────────┐
 │ ProjectZ                         │  HTTPS   │ nginx（443）                          │
 │  Features:QaAssistant:OllamaUrl ─┼─────────▶│  · 只允許 App 主機 IP                 │
 │  = https://infer.corp.local      │          │  · TLS 終結                           │
 └──────────────────────────────────┘          │        │ http://127.0.0.1:11434       │
                                               │        ▼                              │
                                               │ Ollama（只綁 127.0.0.1）              │
                                               └───────────────────────────────────────┘
          防火牆：推論主機只開 443，且只開給 App 主機；11434 不對外
```

原則：**Ollama 永遠只綁本機**，對外一律經過反向代理。

---

## 三、推論主機設定

### 3.1 Ollama 只綁 loopback

**Linux（systemd 安裝）**

```bash
sudo systemctl edit ollama.service
```

寫入：

```ini
[Service]
Environment="OLLAMA_HOST=127.0.0.1:11434"
```

```bash
sudo systemctl daemon-reload && sudo systemctl restart ollama
ss -ltn | grep 11434        # 必須是 127.0.0.1:11434，不可以是 0.0.0.0 或 *
```

**Windows**：確認使用者與系統環境變數都**沒有**設定 `OLLAMA_HOST`（或設為 `127.0.0.1:11434`），重新啟動 Ollama 後檢查：

```cmd
netstat -ano | findstr :11434
```

結果必須是 `127.0.0.1:11434`。

### 3.2 反向代理（nginx 範例）

```nginx
server {
    listen 443 ssl;
    server_name infer.corp.local;

    ssl_certificate     /etc/ssl/infer/fullchain.pem;
    ssl_certificate_key /etc/ssl/infer/privkey.pem;

    # 只允許 App 主機；其他一律拒絕
    allow 10.0.10.21;          # ← App 主機 IP，多台就列多行
    deny  all;

    # 嵌入是批次送出的，留一點餘裕
    client_max_body_size 10m;

    location / {
        proxy_pass http://127.0.0.1:11434;

        # 以本機名稱轉送。2026-10-07 實測 Ollama 0.32.14 不檢查 Host 標頭（任何值都回 200），
        # 此行是為了相容日後版本若加上 Host 檢查；保留無害。
        proxy_set_header Host localhost:11434;

        # ⚠️ 串流回答（AskStream）必須關掉緩衝，否則逐字輸出會被攢成一次全出，
        #    畫面看起來像卡住，而且不會有任何錯誤訊息。
        proxy_buffering off;
        proxy_cache off;
        proxy_http_version 1.1;

        # 首字前的 prefill 可能要數十秒（CPU 推論實測約 50 秒／題），逾時要比應用程式端寬鬆
        proxy_read_timeout 300s;
        proxy_send_timeout 300s;
    }
}

# 不提供明文入口；也不要在 80 埠轉送到 Ollama
```

### 3.3 防火牆

- 推論主機只開 **443**，來源限定為 App 主機。
- **11434 不得對外開放**（即使 Ollama 已綁 loopback，也在防火牆層再擋一次）。

### 3.4 憑證

- ProjectZ 的 HttpClient 會驗證憑證，**自簽憑證會連線失敗**。
- 優先使用 App 主機**已經信任**的 CA 簽發的憑證（企業內部 CA，或公開 CA）。
- 要讓 App 主機信任新的私有 CA，需要系統管理員權限（Linux：`update-ca-certificates`；Windows：本機電腦憑證存放區）。
  部分受管控的機器政策上禁止安裝 CA，規劃前要先確認。
- **不要**為了省事關閉憑證驗證。

---

## 四、ProjectZ 端設定

`appsettings.json`（或環境專屬檔）：

```json
"Features": {
  "QaAssistant": {
    "OllamaUrl": "https://infer.corp.local",
    "TrustedInferenceHosts": [ "infer.corp.local" ]
  }
}
```

用環境變數設定時（IIS 的 `web.config`、systemd 的 `Environment=`），階層改用雙底線：

```
Features__QaAssistant__OllamaUrl=https://infer.corp.local
Features__QaAssistant__TrustedInferenceHosts__0=infer.corp.local
```

### 4.1 `TrustedInferenceHosts` 是一次明確的「資料出境」決定

系統內建出境閘門：推論端點**不是本機**、也**不在** `TrustedInferenceHosts` 清單內時，
**自然語言資料查詢會被整個停用**（一列業務資料都不會查），啟動 log 會出現：

> 推論端點 … 不在信任清單內，**自然語言查詢營運資料（Phase 2）已停用**

把主機加進清單，就代表同意營運資料離開 App 主機、送到那台推論主機。
清單可以只寫 host（`infer.corp.local`），也可以寫 `host:port`。

### 4.2 其他相關設定

| 設定 | 預設 | 搬到遠端時要注意 |
|---|---|---|
| `TimeoutSeconds` | 180 | 單次推論的逾時。網路多一跳之後，若常見逾時再調高；nginx 的 `proxy_read_timeout` 要比它大 |
| `ToolQueryBudgetSeconds` | 420 | 一次資料查詢（選工具＋摘要）的總預算 |
| `MaxConcurrentInference` | 1 | 應用程式端的同時推論數。若推論主機的 `OLLAMA_NUM_PARALLEL` 調高，這裡才跟著調高 |
| `ChatModel`／`EmbeddingModel` | （空） | 模型要先在**推論主機上** `ollama pull`。換嵌入模型＝手冊索引要全量重嵌 |

### 4.3 修改後必須重新啟動

出境閘門與 HttpClient 的目標網址都只在**應用程式啟動時判定一次**。
改了 `OllamaUrl` 或 `TrustedInferenceHosts`，不重新啟動不會生效。

---

## 五、驗收清單

| # | 檢查 | 預期 |
|---|---|---|
| 1 | 推論主機上 `ss -ltn \| grep 11434`（或 `netstat`） | 只有 `127.0.0.1:11434` |
| 2 | 從 **App 主機** `curl https://infer.corp.local/api/version` | 回 200 與版本號 |
| 3 | 從**其他任一台**同網段電腦執行同一個指令 | 403 或連不上 |
| 4 | 從其他電腦 `curl http://<推論主機IP>:11434/api/version` | 連不上 |
| 5 | ProjectZ 啟動 log | 出現「不在本機，但已由 Features:QaAssistant:TrustedInferenceHosts 明示信任：營運資料會離開這台機器。」，結尾**沒有**「⚠️ 且走的是明文 HTTP。」 |
| 6 | QA 助理首頁 | 沒有「尚未就緒」提示 |
| 7 | 手冊問答問一題 | 正常回答，且是逐字串流出現（不是等很久後一次全出） |
| 8 | 資料查詢問一題（例如「上個月的不良率」） | 回表格＋摘要，不是「推論端點不被信任」的訊息 |

第 7 項如果是一次全出，回頭檢查 nginx 的 `proxy_buffering off`。
第 2 項回 403，通常是 App 主機的 IP 不在 nginx 的 `allow` 清單內（App 主機有多張網卡、或經過 NAT 時，來源 IP 可能不是你以為的那個——看 nginx 的 access log 就知道實際來源）。

---

## 六、不要做的事

- ❌ 把 `OLLAMA_HOST` 設成 `0.0.0.0` 讓 App 主機直連（沒有加密、也沒有任何存取控制）。
- ❌ 開放 11434 埠給整個網段，即使「只是暫時測一下」。
- ❌ `OllamaUrl` 用 `http://` 指向另一台主機——資料會以明文在網路上傳送（啟動 log 會警告）。
- ❌ 為了自簽憑證而關閉 HttpClient 的憑證驗證。
- ❌ 把 `TrustedInferenceHosts` 設成萬用字元，或填入不受控制的主機。

---

## 七、已知限制（後續若要強化需修改程式）

1. **不支援 API 金鑰／認證標頭。** 目前只能靠 IP 白名單。若推論主機需要讓多個系統共用、
   或來源 IP 不固定，需要先在程式中加入設定鍵（例如 `Features:QaAssistant:ApiKey`），
   並在 `QAModule` 註冊具名 HttpClient 時帶入 `Authorization` 標頭，nginx 端再比對該標頭。
2. **出境閘門只管資料查詢，不管手冊問答。** 手冊問答送出的是手冊段落與使用者提問（規格裁定為內部級），
   即使端點不在信任清單內也照常送出。如果使用者的提問本身可能含機密，
   推論主機一樣要比照本文件的方式保護。
3. **沒有 mTLS（用戶端憑證）支援。** 需要的話同第 1 點，要修改 HttpClient 的設定。

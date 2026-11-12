# .claude 目錄結構說明

ProjectZ 的 AI 開發指引與業務規格存放在本目錄下：

## 目錄說明

### 1. **`specifications/`** — 業務領域規格文件
存放各模組的業務需求、架構設計、API 契約定義等。與代碼實作密切相關，需納入版本控制。

> 📋 **先查 [`STATUS.md`](specifications/STATUS.md)**：每份規格的完成度索引（✅完成/🟡部分完成/⚠️文件過期），2026-07-16 全量稽核建立。要確認某規格是否已完成、或找剩餘待辦項，先查這份索引，不需要整份重讀。

| 檔案 | 用途 |
|------|------|
| `mfg-domain.md` | 製造執行（MFG）模組的領域設計、Phase 規劃、API 定義、測試計劃 |
| `pur-domain.md` | 採購與委外管理（PUR）領域設計、Phase 規劃、API 定義 |
| `qcr-domain.md` | 品質管理（QCR）領域設計：檢驗規格/檢驗單/MIL-STD-1916 抽樣/NCR/儀器校正 |
| `bas-master-data.md` | 基礎主檔（BAS）：公司/幣別/稅別/單位/產品/倉儲/班別/單號規則等分類與欄位 |
| `ord-module.md` | 訂單模組（ORD）的資料模型、業務邏輯、並發控制規範 |
| `inventory.md` | 庫存模組（INV）基礎規格 |
| `inventory-advanced.md` | 庫存模組進階特性 |
| `mobile-pwa.md` | 手機 PWA 應用規格、前後端接口、離線能力設計、Phase 規劃（實作現況：`wwwroot/mobile/`） |
| `currency.md` | 匯率換算（方向、快照、RateType、ICurrencyService API） |
| `management-reports.md` | 管理報表規格 |
| `business_data_flow_reconciliation.md` | 跨模組業務資料流與對帳（P2P / O2C / WIP） |
| `reconciliation-decisions.md` | P-1/P-2/P-3 前置的會計語意決策（材料成本/傳票工單維度/ERP拋轉範圍） |
| `roadmap-fin-cst-sub.md` | FIN / CST / SUB 模組路線圖 |
| `consistency-framework.md` | 一致性框架規則索引（R0–R41 + Cx1–8；新規則必補〈規則索引〉，否則 R0 讓 build 紅） |
| `security-assessment-and-plan.md` | 資安評估與改善計畫 |
| `plant-scoped-system-params.md` | 系統參數廠別化設計與盤點（逐項裁決、`PlantParam` 覆寫表、三層 fallback、Core 上送邊界） |
| `llm-qa-and-report-tools.md` | LLM 問答（S1–S3 檢索／擷取物語料）＋報表工具（常用查詢、報表排程）規格 |
| `report-permission-inventory.md` | 報表權限盤點（Phase 0-A0 交付物，R23–R26 守門的依據） |
| `test-hardening-plan.md` | 測試強化計畫（Phase 0–8，已全數完成） |
| `user-manual-tutor.md` | 使用手冊教學系統（截圖模擬演示）的設計 |
| `manual-test-plan.md` | 人工驗收測試作業書（Wave 0–5、Wave S、PWA 實機 §8.4a） |
| `erp-bridge-handover.md` | ERP 拋轉橋接交接說明（本系統刻意不實作拋轉；要接時從哪裡下手） |
| `archive/` | 已完成章節與過期文件的封存（內容是當時紀錄、不是現況；QA 擷取把整個目錄列紅線） |

**特性**：
- 隨時更新，反映最新的業務決策
- 所有新增功能應先在此文件確認規格再進行編碼
- 自動納入 `merge=ours` 版本控制（見 `.gitattributes`；僅與 Core 共有的檔案需要此設定，ProjectZ 專屬檔案 Core 無同名檔不會衝突）

### 2. **`reviews/`** — 一次性評估與盤點文件

架構／排程／CSP／參照有效性等評估與行動計畫。完成度索引見 [`reviews/STATUS.md`](reviews/STATUS.md)；已結案的仍留在原位，因為產品程式碼註解直接引用它們的路徑。QA 擷取把整個 `reviews/` 列為紅線。

### 3. **`qa-extracts/`** — Q&A 擷取物語料

從規格與程式碼註解擷取的使用者規則（manifest JSON），人工逐則核可後才進檢索；裁決紀錄在 `qa-extracts/DECISIONS.md`。產生與對帳工具在 `scripts/qa-extract-*.py`。

### 4. **`commands/`** — AI 工具命令規格
存放 Claude Code 工具命令（如 `/new-screen`、`/new-report`）的完整規格與執行步驟。

| 檔案 | 用途 | 來源 |
|------|------|------|
| `new-screen.md` | 畫面自動產生規格（Core 基礎版） | Core 同步（ProjectZ 加了一致性框架段落，已登錄漂移 baseline） |
| `new-screen-projectz.md` | ProjectZ 補充規格（覆蓋 Core 對應段落） | ProjectZ（merge=ours） |
| `new-report.md` | 單據列印自動產生規格（QR Code / PDF，個別單據） | Core 同步（同上，已登錄 baseline） |
| `webapp-testing.md` | E2E UI 測試自動產生規格（Playwright） | Core 同步 |
| `xlsx.md` | Excel 匯出自動產生規格（ClosedXML） | Core 同步 |
| `new-job.md` | 背景作業／排程任務（IHostedService / Hangfire） | ProjectZ（merge=ours） |
| `new-analytics.md` | 資料分析儀表板（KPI / 趨勢圖 / 彙總，非單據列印） | ProjectZ（merge=ours） |

**特性**：
- 與 Core 同步更新（除 `*-projectz.md`、`new-job.md`、`new-analytics.md` 外，這幾支為 ProjectZ 專屬）
- 定義 AI 工具的輸出品質標準
- 通常不頻繁變動

**選擇哪個 skill？**

| 使用者描述 | 應觸發 |
|----------|--------|
| 「新增 XXX 維護畫面」「CRUD 頁面」「主檔管理」 | `/new-screen` + `/new-screen-projectz` |
| 「列印一張 XXX 單」「QR Code 單據」「PDF 下載」 | `/new-report` |
| 「匯出 Excel」「下載報表（單一表格）」 | `/xlsx` |
| 「儀表板」「Dashboard」「KPI」「趨勢圖」「彙總統計」 | `/new-analytics` |
| 「每天 02:00 跑」「每 N 分鐘輪詢」「批次處理」 | `/new-job` |
| 「寫一個 E2E 測試」「自動化測試流程」 | `/webapp-testing` |

### 5. **`LOCAL_RULES.md`** — ProjectZ 專屬規則
> 見檔案內容

### 6. **`settings.json` + `hooks/`** — Claude Code 行為設定（範本，依環境決定是否啟用）

`.claude/settings.json` 是 ProjectZ 團隊共用的 Claude Code 設定（committed），內含一條 `UserPromptSubmit` hook，預計每次使用者送出 prompt 時自動注入「ProjectZ 工作守則」提醒。

| 檔案 | 用途 |
|------|------|
| `settings.json` | UserPromptSubmit hook 註冊 |
| `hooks/projectz-reminder.json` | hook 注入的固定 JSON（含 `hookSpecificOutput.additionalContext`） |

**注入內容**：
- 動工前先讀 `LOCAL_RULES.md`
- `CLAUDE.md` 與 `LOCAL_RULES.md` 衝突時以後者為準
- 新規格／/new-job ／/new-analytics 在 `commands/` 與 `specifications/`

#### ⚠️ 啟用條件

Hook 啟用流程要求**在 terminal 內透過 Claude Code CLI 走一次信任流程**（VS Code extension 內按 `/hooks` 會跳「Continue in Terminal」）。因此需要：
- 該機器**已安裝 Claude Code CLI**（`which claude` 找得到）
- 在 ProjectZ 目錄下跑 `claude` 進入 CLI，並在 `/hooks` 介面信任此 hook

若機器**受 IT 管控不能安裝 Node.js/Claude Code CLI**（部分企業環境），則 hook 永遠不會被觸發——此時規則執行**改靠**：
- `LOCAL_RULES.md` 頂部紅字「AI 助理請於每次工作前讀完本檔」
- AI 助理的 memory（`project-projectz-docs-structure` 等記錄）
- 顧問人工 review

#### 操作備忘

| 動作 | 方式 |
|------|------|
| 修改提醒內容 | 編輯 `hooks/projectz-reminder.json` 的 `additionalContext` 字串，**不需**動 `settings.json` |
| 停用 hook（個人） | 在能用 CLI 的機器跑 `/hooks` 暫停；或在 `.claude/settings.local.json` 覆寫（gitignore，不影響團隊） |
| 停用 hook（團隊） | 刪掉 `.claude/settings.json` 的 hooks 區塊 + 移除 `hooks/projectz-reminder.json` |
| 版本控制 | `settings.json` 與 `hooks/` 兩者皆 commit；個人覆寫請放 `settings.local.json` |
| Hook 載入時機 | 須在 Claude Code 啟動時就存在 — 新加 hook 後須在能用 CLI 的環境中走信任流程才生效 |

## 使用指南

### 編碼時如何查閱業務規格？

⚠️ **重要**：業務規格檔案（`specifications/` 下）**不會自動納入 Claude Code 上下文**，編碼時需**主動查閱**。

| 編碼場景 | 應查閱檔案 | 原因 |
|---------|----------|------|
| 開發 MFG 模組（工單、排程、派工） | `specifications/mfg-domain.md` | 確認業務邏輯、Phase 進度、API 設計 |
| 開發手機 PWA 或 Mobile API | `specifications/mobile-pwa.md` | 確認前後端接口、離線同步、權限模型 |
| 開發 ORD 模組（訂單、銷售） | `specifications/ord-module.md` | 確認資料模型、並發控制策略 |
| 開發庫存模組（INV） | `specifications/inventory.md` 或 `-advanced.md` | 確認庫存邏輯、單據流程 |
| 新增金額欄位（UnitPrice、TotalAmount 等） | `specifications/currency.md` 或 CLAUDE.md 「金額欄位」章節 | 確認小數點精度設定 |
| 新增數量欄位（Qty、Weight 等） | CLAUDE.md 「數量欄位 UOM」章節 | 確認單位轉換、四捨五入邏輯 |

**查閱方式**：
1. 在 IDE 中開啟相應規格檔案（可用 Ctrl+P 搜尋檔名）
2. 搜尋相關關鍵字（如 Phase、API 路由、Entity 名稱）
3. 必要時詢問 AI：「根據 `mfg-domain.md` 的規格，請實作...」（AI 會讀取該檔案）

### 實作新功能時
1. 確認涉及的模組 → 查閱對應 `specifications/` 檔案
2. 遵循 CLAUDE.md 的通用規範（編碼風格、多語、小數點等）
3. 若涉及畫面產生，套用 `commands/new-screen.md` + `commands/new-screen-projectz.md`
4. 若涉及 E2E 測試，套用 `commands/webapp-testing.md`

### 更新規格文件
- 業務規格變更 → 更新 `specifications/` 對應文件
- 新增模組 → 在 `specifications/` 建立對應 `.md`
- 工具命令更新 → 更新 `commands/` 對應文件（與 Core 同步）

### 版本控制
- `specifications/*` 檔案自動 `merge=ours`，防止 Core 覆蓋
- `commands/*` 基礎檔案（`new-screen.md` 等）接受 Core 更新
- `commands/new-screen-projectz.md` 自動 `merge=ours`

---

**最後更新**：2026-10-08

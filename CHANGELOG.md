# Changelog

本文件記錄 **ProjectZ**（SmartFactory.MES 部署專案）的版本變更。
格式參考 [Keep a Changelog](https://keepachangelog.com/zh-TW/1.0.0/)。
版本號遵循 [語意版本（SemVer）](https://semver.org/lang/zh-TW/)：`MAJOR.MINOR.PATCH`。

> ProjectZ 的版本線獨立於 Core（`upstream/main`）。基礎平台更新經 `./update-core.sh` 同步，
> 不單獨在此列版；本檔聚焦 ProjectZ 自身的營運模組與部署變更。
>
> **維護方式（半自動）**：`scripts/changelog-draft.sh [起點tag]` 會彙整自上一版 tag 以來的
> conventional commits，依 Keep a Changelog 章節分組產出草稿（feat→Added、fix→Fixed、
> security→Security、docs→Docs；refactor/chore/test 等列為註解僅供參考、merge 與 Core 同步不列入），
> 人工挑選/潤飾後貼入 `[Unreleased]`；發版時將 `[Unreleased]` 改為版號＋日期並打 tag。

---

## [Unreleased]

### Added — 營運功能
- **跨期 WIP 人工/製費結轉＋金額恆等式稽核（reconciliation-decisions.md D4，P-1 收口）**：期末實際費率重估改採期初快照結轉法——本期人工/製費＝前期 `WipSnapshot` 凍結層＋本期工時×本期實際費率，WIP 快照與完工回刷兩腿一致套用；關帳前稽核新增 `WIP_IDENTITY_DRIFT` 金額恆等式（Σ期末＝Σ期初＋發料淨值＋部門人工/製費−完工成本，結構守門全過才評估）與 `WIP_VOID_LEAKAGE`（前期在製工單本期作廢的成本洩漏）。
- **模具保養排程（mfg-domain.md §14.1 第二階段）**：`Tooling` 新增累計打次/保養間隔打次/每穴產品組合欄位；報工（桌機/手機共用 `ToolingShotService`）自動回寫打次，跨越門檻自動建立 HI 保養停機單（機台轉保養中＋排程副作用）、HO 事件（來源單號=模具代號）歸零重啟週期；派工清單/報工面板/模具主檔「模具需保養」警示。
- **工單列印（WorkOrder Print）**：`WorkOrderController.Print` + `Modules/MFG/Views/WorkOrder/Print.cshtml`——抬頭（品號/數量/途程/狀態/計畫起迄/母工單/重工來源）＋工序途程表＋標準成本，含 QR Code，Index 列印按鈕（依 CanPrint 權限）。

### Security — 資安強化（ProjectZ 自身模組；平台層修正經 update-core.sh 同步不另列）
- **密碼歷史保留清理排程**（`Modules/ACT` `PasswordHistoryRetentionJobService`，每日 04:00）：每人保留最近 K 筆（K=max(`Security:PasswordHistoryRetentionKeep`, `Security:PasswordHistoryCount`)），刻意非時間式以免削弱「禁用最近 N 次」重用檢查。（密碼歷史比對機制本身屬 Core 平台，經同步引入）
- **弱密碼黑名單驗證器**（`Services/Security/BlocklistPasswordValidator`）：拒絕含帳號名/Email 本地部分及常見弱密碼（`Security:PasswordBlocklist` 可擴充）；依 NIST SP 800-63B 特殊字元要求改為可選。
- **兩步驟驗證（TOTP 2FA）**：自助啟用 `TwoFactorController`（QR＋復原碼）＋強制中介（可設定角色，預設 Admin）＋管理員重置畫面。
- **AuditLog 保留清理排程**（`Modules/ACT` `AuditLogRetentionJobService`，每日 03:00，預設保留 730 天）。
- **安全事件告警**（`Services/Security/SecurityAlertService`）：速率限制器拒絕＝暴力破解信號 → 結構化 `SECURITY_ALERT` 日誌＋可選 webhook，`(kind:ip)` 冷卻去重。
- **備份加密/異地**（`scripts/backup-db.sh`）：可選 gpg 公鑰加密＋rclone 異地同步。

### Fixed
- **倉庫／儲位預設值在多廠環境指向他廠（`plant-scoped-system-params.md` §2 ① / §5 P1）**：
  `mfg_fg_warehouse`／`mfg_fg_location`／`mfg_default_staging_wh`／`mfg_default_staging_location`
  是**全域單值**，存的卻是倉庫代碼；而 `Warehouse` 主鍵為 `WhCode` 單欄（全域唯一）且屬 IPlantEntity，
  一個代碼只屬於一個廠——在其他廠，這些預設值指到的是**別廠的倉庫**（完工入庫表單預選一個下拉選單裡
  不存在的值而靜默失去預設；備料單則把他廠倉帶進 `stagingWhCode`）。新增
  `IPlantDefaultLocationResolver` 作為「參數值 → 該廠可用值」的單一語意點，驗不過即視同未設定；
  備料倉三層解析（開單指定 > 工作中心 > 系統參數）**每一層**都經此收斂，工作中心層的倉庫停用或
  被改到別廠時改為往下一層找。倉庫可用而儲位不可用時只清儲位、保留倉庫。

### Docs
- 清理規格/評估文件過期「待實作/待補」標記（2026-07-09 複查，均已落地）。
- 新增 [`plant-scoped-system-params.md`](.claude/specifications/plant-scoped-system-params.md)：
  系統參數廠別化設計與盤點（26 個參數逐項裁決、`SystemParam` 複合 PK 與三層 fallback、Core 上送邊界）。
- 更正兩處誤導註解：`MfgStandbySchedulable` 原寫「支援廠別隔離」、`MfgDefaultIssueMethod` 原寫
  「廠別預設發料方式」，實作皆為全域值（後者且無任何讀取端）。

---

## [1.0.0] - 2026-06-22

首個生產就緒版本。於 Core 平台之上以零衝突模組架構完成採購到製造、品質、財務的完整營運閉環；
單元測試 1503 項全綠，ConventionTests 一致性框架（R0–R13 + Cx）建置期把關。

### Added — 營運模組
- **採購（PUR）**：請購、採購單、進貨/退貨、委外採購與委外進貨、超耗扣款接入確認流程
- **庫存（INV）**：庫存異動帳本、移撥、調整、盤點、呆滯分析、跨廠移撥、採購分攤費用（Landed Cost）、AVCO 移動平均、備料單 + MRP 在途修正
- **訂單（ORD）**：銷售訂單、出貨、銷退、內部訂單
- **製造（MFG）**：工單、派工、三階日曆排程（料況閘口 / 雙重預約 / 凍結 / ETA 倒推）、MRP 良率展開、委外需求/報價/領料、WIP 完工成本結算、Release/Firming 閘口
- **品質（QCR）**：IPQC/FQC/RQC 檢驗引擎、MIL-STD-1916 抽樣計畫、檢驗失敗自動暫停派工、NCR、儀器校驗
- **財務（FIN / ACT）**：應收應付、JournalEntry 自動過帳（AP 付款 / AR 收款 / 庫存異動 / Landed Cost）、外幣評價、ERP 拋轉
- **分析報表（RPT）**：損益表、資產負債表、毛利分析、跨模組管理儀表板（17 支報表畫面）
- **行動端（Mobile）**：手機 PWA 全落地（生產準備看板、IPQC 執行、語系 Cookie 同步）
- **基礎資料（BAS）**：產品、UOM、幣別、供應商/客戶主檔；UOM/Currency 小數精度雙軌套用全模組

### Added — 平台與品質
- ConventionTests 一致性框架（R0–R13 + Cx1–7）建置期把關，per-rule baseline 機制
- UOM（數量）+ Currency（金額）兩套 step/round 小數精度對應
- 樂觀並發控制（PostgreSQL `xmin`）
- 全模組查詢列控件統一為 select2 / select 下拉

### Security
- 全模組寫入 POST 補伺服端權限檢查（148+ 處）+ 簽核人身分驗證，default-deny
- 行動 API 補 MobilePermission 檢查 + Push 訂閱防劫持
- 期間鎖接入、跨廠 IDOR 防護、負庫存守門、空 catch 清除
- 安全標頭 / CSP（移除 `'unsafe-eval'`）、Cookie `SecurePolicy=Always`、`/health` 匿名收斂
- 報表載入安全上限，防無上限 `ToListAsync` OOM

### Fixed
- 進貨/委外進貨/出貨確認過帳包單一交易
- 匯率方向統一（本幣 = 外幣 × Rate）
- 物料備齊閘口、ETA 層級與機台 Standby 參數化

---

## [0.1.0] - 2026-03-05 — Core 平台基礎

ProjectZ 所 fork 的 SmartFactory.MES Core 平台基礎（保留於此作為起點）。

### Added
- 核心 RBAC 權限系統（`IPermissionService`、`IUserContext`）
- 多廠區（Plant）、組織架構（Organization）、角色與使用者管理
- 審批流程（ApprovalService）、代碼維護（CodeEntry）、稽核日誌（AuditLog）
- 多語系支援（繁體中文 / English）
- 零衝突模組架構（`IAppModule` + `partial ApplicationDbContext`）
- License 驗證（Security Anchor DLL + 加密連線字串 + 設備指紋綁定）
- Docker Compose 部署、CI/CD（GitHub Actions：build + test + 邊界驗證）

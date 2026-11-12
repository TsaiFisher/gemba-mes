# Gemba MES

**中小型製造業的製造執行系統（MES）。** 從接單、採購、庫存、工單排程、
品質檢驗到成本結算與帳務傳票，是一套完整、可實際上線的系統，不是範例專案。

「現場（Gemba／げんば）」是精實生產裡指「創造價值的那個地方」。
這套系統是給現場用的。

> **授權：[Elastic License 2.0](LICENSE)**（source available，非 OSI 開源）。
> 你可以免費取得、修改、部署來跑自己的生意，也可以收費替別人導入；
> 但不能代管成服務賣給第三方，也不能拆掉授權檔的驗證。
> 白話說明與**免責聲明**見 **[LICENSING.md](LICENSING.md)**——上線前請務必讀。

---

## 功能模組

| 模組 | 說明 |
|------|------|
| 採購（PUR） | 請購、採購單、進貨、退貨、委外採購、超耗扣款、供應商評鑑 |
| 庫存（INV） | 異動帳本、移撥、調整、盤點、呆滯分析、採購分攤費用（Landed Cost）、AVCO 均價 |
| 訂單（ORD） | 銷售訂單、出貨、銷退、內部訂單 |
| 製造（MFG） | 工單、派工、排程（料況閘口／雙重預約／ETA）、MRP、委外、WIP 成本結算 |
| 品質（QCR） | IPQC／FQC／RQC 檢驗、MIL-STD-1916 抽樣計畫、NCR、儀器校驗 |
| 財務（FIN／ACT） | 應收應付、JournalEntry 自動過帳、外幣評價、期間鎖與月結 |
| 分析報表（RPT） | 損益表、資產負債表、毛利分析、SPC／OEE、跨模組管理儀表板 |
| 行動端 | 手機 PWA（生產報工、領料、IPQC、條碼掃描） |
| 平台基礎 | 多廠區、組織架構、RBAC 細粒度權限（default-deny）、多步驟簽核、稽核日誌、繁中／English |

---

## 技術架構

- **框架**：ASP.NET Core 10 MVC + Razor Views
- **資料庫**：PostgreSQL 15+（EF Core，樂觀並發使用 `xmin`）
- **UI**：Bootstrap 5 + Select2
- **測試**：xUnit + Moq（單元 4,400+ 項）、WebApplicationFactory（整合）、Playwright（UI）
- **品質把關**：建置期一致性框架（R0–R14 + Cx 規則），違規會擋下建置
- **部署**：Docker Compose

---

## 快速開始

完整步驟見 **[INSTALL.md](INSTALL.md)**。摘要：

```bash
# 1. 授權檔（任何環境都需要，包含開發環境）
dotnet run --project SmartFactory.MES.MachineId -- --init   # 產生機器錨定碼
dotnet run --project SmartFactory.MES.MachineId             # 顯示申請碼與登記網址
#    開啟登記網址申請授權（免費、永久、需登記，見 INSTALL.md §3.3）
#    🚨 提供申請碼，不要提供機器錨定碼（長得像 UUID）

#    收到授權檔改名 license.json；從 Releases 下載同版本的 Security DLL 放到 Security/（INSTALL.md §3.5）

# 2. 資料庫（先在 .env 設 DB_PASSWORD）
docker compose up -d db

# 3. 加密連線字串與啟動
dotnet run --project tools/EncryptConnectionString -- \
  --license-id <licenseId> --plaintext "Host=localhost;Database=SmartFactoryMES;Username=dev;Password=..." --this-host
#    虛擬機／容器改用 --no-host-binding（INSTALL.md §5）
cp appsettings.json appsettings.Development.json
#    把輸出的加密值填入 ConnectionStrings:DefaultEncrypted（DefaultConnection 留空）
dotnet run                     # http://localhost:5110，admin / Dev12345（首次登入需改密）
```

首次啟動會自動套用 migration 並建立系統種子資料。

---

## 文件

| 文件 | 說明 |
|------|------|
| **[LICENSING.md](LICENSING.md)** | 授權白話說明、取得授權檔、Security DLL、會連到哪裡、**免責聲明** |
| [SYNCING.md](SYNCING.md) | fork 之後怎麼跟上更新、怎麼改衝突最少 |
| [CONTRIBUTING.md](CONTRIBUTING.md) | 貢獻指南與 CLA |
| [SECURITY.md](SECURITY.md) | 資安問題回報（請勿開公開 Issue） |
| **[INSTALL.md](INSTALL.md)** | 安裝、正式環境設定、升級、**疑難排解對照表** |
| [DEVELOPMENT.md](DEVELOPMENT.md) | 開發規範、測試、UI 慣例、新增模組 checklist、Docker 部署 |
| [.claude/specifications/](.claude/specifications/) | 業務領域規格——系統「應該」怎麼運作，讀原始碼讀不出來的那部分 |
| [CHANGELOG.md](CHANGELOG.md) | 版本變更紀錄 |

---

## 這套系統不是什麼

寫在前面比讓你自己撞上好：

- **沒有取得任何產業認證**，也未針對特定法規（食品、醫材、航太）的法遵要求驗證。
- **不是 SaaS，也沒有託管版本。** 你得自己有一台機器、一個資料庫，
  以及願意維護它的人。
- **帳務與成本邏輯必須自行驗證。** 它做的是一般製造業的通用作法，
  不等於符合貴公司的會計政策。導入前請找會計人員對過。
- **啟動需要授權檔與 Security DLL。** 兩者都免費：授權檔向維護者登記取得，
  DLL 從 Releases 下載。步驟見 [INSTALL.md](INSTALL.md) §3。
- **由一個人維護**，沒有 SLA。

---

## 與 SmartFactory MES 的關係

Gemba MES 是 **SmartFactory MES 平台的公開發行版**。兩者共用同一套核心，
差別在授權條款與連線字串的保護方式。

`SmartFactory`、`ProjectZ` 為作者保留之標識，不隨本授權授出。

---

## 授權

Copyright (c) 2026 Fisher Tsai ([@TsaiFisher](https://github.com/TsaiFisher))

依 [Elastic License 2.0](LICENSE) 授權。第三方元件見 [NOTICE](NOTICE)。

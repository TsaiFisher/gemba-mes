# 基礎資料領域規格（bas-master-data.md）

> BAS（Basic / 基礎主檔）模組彙整全系統共用的主檔與系統參數。
> 本檔描述「已實作」現況，作為編碼與同步的依據。各主檔的廠別歸屬判斷遵循
> `CLAUDE.md`〈Entity 資料範圍規範（IPlantEntity vs 全域）〉。
>
> ⚠️ 重要：新增主檔或修改既有主檔欄位前請主動查閱本檔（不會自動納入上下文）。

---

## 1. 模組結構與分類總覽

```
Modules/BAS/
├── BASModule.cs                # IAppModule：DI 註冊（IUomService 等）+ View 路徑
├── Models/                     # 全 BAS 主檔 entity
├── Controllers/                # 各主檔 CRUD（多數依 /new-screen 規格）
├── Services/                   # UomService / CurrencyStepHelper / UomStepHelper /
│                               # BASCodeEntryReferenceChecker
└── Views/{Controller}/…
```

**廠別歸屬分類（決定 PlantId 過濾與 Unique Index）：**

| 類別 | 主檔 |
|------|------|
| **全域主檔**（不實作 IPlantEntity，全公司共用） | Company / CompanyContact、Currency / CurrencyRate、Tax、Uom / UomConversion、Product / ProductCategory / ProductCategoryTag、Container、InventoryTransactionType、WarehouseType、DocNumberConfig |
| **廠別主檔**（IPlantEntity，各廠獨立） | Warehouse、WorkCenter / WorkCenterMachine、Shift、ShiftGroup / ShiftGroupDay、SpecialDay、Tooling |
| **廠別傳遞**（經 FK 隔離，非直接 IPlantEntity） | StorageLocation（經 `WhCode` → Warehouse） |
| **系統計數**（非使用者維護） | DocNumberSequence（每廠 + 每週期原子計數） |

> 對應舊系統代碼附於各章標題（如 COMP / CRN / TAX / UM / PROD …）。

---

## 2. 往來公司（Company / CompanyContact）— 全域，對應 COMP

### 2.1 業務說明

客戶與供應商共用同一主檔，以旗標區分角色：`IsCustomer` / `IsSupplier`（以及 `SupplierType` "C"/"S" 供查詢列過濾，見 CLAUDE.md〈查詢列控件選用規範〉）。

### 2.2 關鍵欄位

| 欄位 | 說明 |
|------|------|
| `CompanyCode` / `CompanyName` | 代碼 / 名稱 |
| `IsCustomer` / `IsSupplier` | 角色旗標 |
| `PaymentType` | 收付款方式 → CodeEntry `payment_type` |
| `CurrencyCode` | 預設幣別 → FK Currency |
| `TaxCode` | 預設稅別 → FK Tax |
| `QualificationStatus` | 供應商資格狀態（見 pur-domain.md §3） |

`CompanyContact`（COMP_D）：同一公司下多聯絡人，`SeqNo` / 職稱 / 姓名 / 電話 / Email。

---

## 3. 幣別與匯率（Currency / CurrencyRate）— 全域，對應 CRN

- `Currency`：`CurrencyCode` / `CurrencyName` / `Symbol`（$、€、NT$）/ `DecimalPlaces`（小數位數，驅動金額 step）/ `IsBaseCurrency`。
- `CurrencyRate`（CRN_D，複合 PK `CurrencyCode` + `RateDate`）：`BuyRate` / `SellRate`。
- 匯率方向與換算規格詳見 **`currency.md`**（ExchangeRate = 本幣/外幣，本幣 = 外幣 × Rate）。
- `ICurrencyService` 提供 `GetBaseCurrencyAsync` / `GetAllDecimalPlacesAsync` / `RoundAsync`；快取於增刪改後 `InvalidateCache()`。

---

## 4. 稅別（Tax）— 全域，對應 TAX

`TaxCode` / `TaxName` / `Rate`（0.05 = 5%）/ `IsActive`。採購/銷售單據稅額計算來源。

---

## 5. 計量單位（Uom / UomConversion）— 全域，對應 UM

- `Uom`：`UomCode` / `UomName` / `DecimalPlaces`（小數位數，驅動數量 step，見 CLAUDE.md〈數量欄位 UOM 小數點〉）。
- `UomConversion`：來源↔目標單位換算（`Numerator` / `Denominator` 分子分母）。
- `IUomService`（`Services/UomService.cs`）：`GetAllDecimalPlacesAsync` / `RoundQtyAsync`；快取於增刪改後 `InvalidateDecimalPlacesCache()`。`UomStepHelper` 供 View 取 step/min。

---

## 6. 產品主檔（Product / ProductCategory / ProductCategoryTag）— 全域，對應 PROD / PC

### 6.1 `Product`

`ProductCode` / `ProductName` / `RoutingCode`（途程，暫文字）；三組單位 `SalesUom` / `PurchaseUom` / **`StockUom`（庫存基準單位，建檔必填：庫存/成本/換算基準）**；`StockMode` → CodeEntry `stock_mode`；`SafetyStock` 安全存量。

### 6.2 `ProductCategory`（含庫存預設）

`CategoryCode` / `CategoryName` + 庫存管理預設：`DefaultLotControlled`（預設啟用批號）/ `DefaultWarehouse` / `DefaultLocation` / `DefaultStockUom` / `CostMethod`（成本計算方式）。新增產品時可帶入此類別預設。

`ProductCategoryTag`：類別標籤（多對多分類輔助）。

---

## 7. 倉儲設施（Warehouse / WarehouseType / StorageLocation / Container）— 對應 WH / CONT

| 主檔 | 歸屬 | 說明 |
|------|------|------|
| `Warehouse`（WH） | **廠別** | `WhCode` / `WhName` / `WarehouseType`（決定系統行為）/ `IncludeInMrp`（納入 MRP）。Unique Index 含 PlantId |
| `WarehouseType` | 全域 | 倉庫類型系統分類表 |
| `StorageLocation` | 經 Warehouse FK 隔離 | `WhCode` + `LocationCode`；`LocationType` → CodeEntry `location_type` |
| `Container`（CONT） | 全域 | `ContainerCode` / 名稱 / `MaxCapacity` 最大容量 |

---

## 8. 工作中心與機台（WorkCenter / WorkCenterMachine）— 廠別，對應 WC

- `WorkCenter`：`WorkCenterCode` / 名稱 / `DepartmentCode`（組織圖）/ `ShiftGroupCode`（→ ShiftGroup）/ `IsProductionLine`（是否生產線）/ `OverheadRate` 附加費率 / **`LaborHourlyRate`（人工費率，元/時）** / **`MachineHourlyRate`（機台費率，元/時）**（成本計算見 mfg-domain.md §16.2）。
- `WorkCenterMachine`（WC_M）：非生產線工作中心可設定多台機台。

---

## 9. 班別與日曆（Shift / ShiftGroup / ShiftGroupDay / SpecialDay）— 廠別，對應 SH / SI / SD

- `Shift`（SH）：`ShiftCode` / `StartTime` / `EndTime` / `BreakStart` / `BreakEnd`（午休，供甘特圖跳過）/ `WorkMinutes`（淨工時 = 班距 − 休息）。
- `ShiftGroup`（SI，班制）：定義一週各天排班；`IsDefault`（每廠至多一筆，批次產生工作日曆未指定班制時採此預設，mfg-domain.md §3.1）。
- `ShiftGroupDay`：`DayOfWeek`(1=週一…7=週日) + `Shift1~4`（最多 4 班）+ 本日整體 `WorkStart`。
- `SpecialDay`（SD）：`Date`(PK) + `Type` → CodeEntry `special_day_type`（Holiday/Overtime/Compensation）+ 補班對應 `DayOfWeek` + `Year`。

> 工作日曆物化與排程使用詳見 mfg-domain.md §3（產能日曆）。

---

## 10. 模具/治具（Tooling）— 廠別，對應 MOLD

`ToolingCode` / 名稱 / `Cavity`（穴數，治具亦表容納數量）/ `Type` → CodeEntry `tooling_type`（模具/治具/夾具）。排程模具清單與閘口見 mfg-domain.md §23。

---

## 11. 庫存異動類別（InventoryTransactionType）— 全域，對應 IY

`TypeCode` / `TypeName` / `SystemCategory`（來自 `MovementCategory` enum，決定單號前綴）/ `Direction`（+1 入庫 / −1 出庫）/ `IsBuiltIn`（系統內建不可刪）/ 強制填批號 / 強制填來源單號 / `SourceTypeCode`（自我參照 FK）。INV 各單據類型以此為配置來源。

---

## 12. 單號規則（DocNumberConfig / DocNumberSequence）— 對應單號產生器

### 12.1 `DocNumberConfig`（全域，全系統共用不套廠別過濾）

每個單據類型一筆：`DocType`（"GoodsReceipt"…）/ `DisplayName` / `Prefix`（"GR"/"II"/"PR"…）/ 日期段格式 / `SeqDigits`（流水碼位數 1–9）/ `Separator`（分隔符，空 = 無）。

### 12.2 `DocNumberSequence`（每廠 + 每週期原子計數）

複合鍵 `DocType` + `PlantId` + `PeriodKey`；`PeriodKey` 規則：Yearly=`"2025"`、Monthly=`"202503"`、Never=`"----"`。`LastSeq` 原子遞增。`DocNumberService` 依此產生不重複單號（並發安全）。

---

## 13. 服務與共用元件

| 元件 | 用途 |
|------|------|
| `IUomService` / `UomService` | 單位小數位快取、`RoundQtyAsync` |
| `UomStepHelper` / `CurrencyStepHelper` | View 取數量/金額 step、min |
| `ICurrencyService`（Core） | 本位幣、小數位、`RoundAsync`、換算 |
| `BASCodeEntryReferenceChecker` | 刪除 CodeEntry 前檢查 BAS 主檔是否引用（`ICodeEntryReferenceChecker` 擴充點） |

---

## 14. 編碼注意事項

- 全域主檔：不加 `PlantId`，Controller 不得以 `_userContext.PlantId` 過濾；Unique Index 僅用代碼本身；`GetApprovalConfig` 的 plantId 傳 `null`。
- 廠別主檔：`ApplyGlobalValues()` 自動填 `PlantId`（`??=`），Controller 不手動指定；Unique Index 含 `PlantId`。
- 含數量欄位 → 套用 UOM 三層小數規範；含金額欄位 → 套用 Currency 三層小數規範（CLAUDE.md）。
- 下拉多語一律走 `CodeEntryDisplayExtensions`（CLAUDE.md），Controller/View 不自行判斷語系。

---

## 15. 關聯文件

- `CLAUDE.md`〈Entity 資料範圍規範〉〈數量/金額小數點規範〉〈查詢列控件選用規範〉
- `currency.md`（幣別換算）；`inventory.md`（倉庫/儲位/異動類別於庫存的使用）
- `mfg-domain.md` §3（班別/日曆）、§16.2（工作中心費率）、§23（模具清單）
- `pur-domain.md` §3（供應商資格狀態 QualificationStatus）

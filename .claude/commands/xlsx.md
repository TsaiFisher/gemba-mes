---
description: 處理所有 Excel (.xlsx) 匯出需求，使用 ClosedXML 產生帶標頭、凍結列、自動篩選的報表。觸發時機：使用者要求匯出 Excel、下載報表、產生 .xlsx 檔案。
---

# /xlsx — SmartFactory MES Excel 報表產生器

> 處理所有 Excel (.xlsx) 匯出需求。
> 觸發時機：使用者要求匯出 Excel、下載報表、產生 .xlsx 檔案。

---

## 套件需求

本專案使用 **ClosedXML**（MIT License）：

```bash
dotnet add package ClosedXML --version 0.104.2
```

> 確認 `.csproj` 中已有此套件再繼續。若無則提示使用者先安裝。

---

## Step 1：確認需求

詢問使用者：

```
Excel 匯出需求確認：

1. 資料來源（Controller / 資料表名稱）：
2. 要匯出的欄位（若留空則匯出所有欄位）：
3. 是否需要多個工作表（Sheet）？[Y/N，預設 N]：
4. 是否套用篩選條件（同 Index 頁面的查詢條件）？[Y/N，預設 Y]：
5. 檔案命名規則（預設：{功能名稱}_{廠別}_{日期}.xlsx）：
```

---

## Step 2：Controller 加入 Export Action

在現有 Controller 新增 `Export` Action（**不產生新 Controller**）：

```csharp
[HttpGet]
public async Task<IActionResult> Export(string? searchTerm, string? sortOrder)
{
    if (!await _permissionService.CanPrint(MenuKey)) return Forbid();

    // 與 Index 相同的查詢邏輯（含廠別過濾）
    var query = _context.{DbSet}
        .Where(x => x.PlantId == _userContext.PlantId)
        .AsNoTracking();

    if (!string.IsNullOrEmpty(searchTerm))
        query = query.Where(x => x.{SearchField}.Contains(searchTerm));

    var data = await query.ToListAsync();

    // 檔名含廠別 + 日期（本地時間）
    var localNow = _dateTimeService.Now;
    var fileName = $"{_userContext.PlantId}_{localNow:yyyyMMdd_HHmm}.xlsx";

    using var wb = new XLWorkbook();
    var ws = wb.Worksheets.Add("{SheetName}");

    // 標題列（第 1 列）
    var headers = new[] { {欄位名稱陣列} };
    for (int i = 0; i < headers.Length; i++)
    {
        ws.Cell(1, i + 1).Value = headers[i];
        ws.Cell(1, i + 1).Style.Font.Bold = true;
        ws.Cell(1, i + 1).Style.Fill.BackgroundColor = XLColor.LightBlue;
    }

    // 資料列（第 2 列起）
    int row = 2;
    foreach (var item in data)
    {
        ws.Cell(row, 1).Value = item.{欄位1};
        ws.Cell(row, 2).Value = _dateTimeService.ToLocalTime(item.{時間欄位}).ToString("yyyy-MM-dd HH:mm");
        // ... 其他欄位
        row++;
    }

    // 自動調整欄寬
    ws.ColumnsUsed().AdjustToContents();

    // 凍結標題列
    ws.SheetView.FreezeRows(1);

    // 自動篩選
    ws.RangeUsed()?.SetAutoFilter();

    using var ms = new MemoryStream();
    wb.SaveAs(ms);
    ms.Position = 0;

    return File(ms.ToArray(),
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        Uri.EscapeDataString(fileName));
}
```

---

## Step 3：View 加入匯出按鈕

在 Index.cshtml 搜尋列的列印按鈕旁加入：

```html
@if (canPrint)  // 共用 CanPrint 權限
{
    <a asp-action="Export"
       asp-route-searchTerm="@ViewBag.SearchTerm"
       asp-route-sortOrder="@ViewBag.CurrentSort"
       class="btn btn-outline-success ms-1">
        <i class="bi bi-file-earmark-excel"></i> @Localizer["ExportExcel"]
    </a>
}
```

---

## Step 4：Resource 加入翻譯

在 `Index.zh-TW.resx` 加入：
```xml
<data name="ExportExcel" xml:space="preserve"><value>匯出 Excel</value></data>
```

在 `Index.en-US.resx` 加入：
```xml
<data name="ExportExcel" xml:space="preserve"><value>Export Excel</value></data>
```

---

## 多工作表範本（HAS_MULTI_SHEET=Y）

```csharp
var ws1 = wb.Worksheets.Add("主表");
var ws2 = wb.Worksheets.Add("明細");
// 各自填入資料
```

---

## 常見欄位格式

| 資料類型 | ClosedXML 寫法 |
|---------|---------------|
| 字串 | `ws.Cell(r,c).Value = item.Name;` |
| 日期時間（UTC→本地）| `ws.Cell(r,c).Value = _dateTimeService.ToLocalTime(item.CreatedAt).ToString("yyyy-MM-dd HH:mm");` |
| 數字 | `ws.Cell(r,c).Value = item.Qty;` |
| 布林 | `ws.Cell(r,c).Value = item.IsActive ? "是" : "否";` |
| 多語欄位 | `ws.Cell(r,c).Value = isZh ? item.NameZh : item.NameEn;` |
| Enum 狀態 | `ws.Cell(r,c).Value = item.ApprovalStatus == DocumentApprovalStatus.Approved ? "已核准" : "待審核";` |

---

## 注意事項

- **時間欄位必須轉換**：`_dateTimeService.ToLocalTime()` 不可省略
- **權限共用 CanPrint**：匯出使用 `CanPrint` 控管，不需新增獨立權限
- **禁止大量資料不分頁匯出**：超過 10 萬筆須加入警示或強制 TOP N
- **檔名用 `Uri.EscapeDataString`**：避免中文檔名亂碼

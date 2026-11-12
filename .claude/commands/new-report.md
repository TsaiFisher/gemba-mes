---
description: 為業務單據產生列印 View（依 PRINT-ARCHITECTURE.md 規格，支援 QR Code 與 PDF 下載）。觸發時機：要求實作列印、產生列印畫面、加入 QR Code 單據。
---

# /new-report — SmartFactory MES 單據列印 View 產生器

> 依 PRINT-ARCHITECTURE.md 規格，為業務單據產生專屬列印 View。
> 觸發時機：要求實作列印、產生列印畫面、加入 QR Code 單據。

---

## 現有框架（已完成，直接使用）

| 檔案 | 說明 |
|------|------|
| `Services/PrintHelper.cs` | `GenerateQrSvg(content)` — 產生 SVG QR Code |
| `Pages/Shared/_PrintLayout.cshtml` | 最小化 Layout（無 navbar/sidebar） |
| `wwwroot/css/print.css` | A4 版面 + 完整 CSS Class |

---

## Step 1：確認需求

```
列印單據需求確認：

1. 功能名稱（如 WorkOrder、SalesOrder）：
2. 中文單據名稱（如 工作單、銷售訂單）：
3. QR Code 內容（預設：記錄的完整 URL）[URL / 單號 / 自訂]：
4. 版面欄位數：[2欄 / 3欄，預設 2欄]：
5. 是否有明細表格（如訂單明細、工序步驟）？[Y/N，預設 N]：
6. 是否需要簽核欄位（製作/審核/核准）？[Y/N，預設 Y]：
7. 輸出方式：[browser（瀏覽器列印）/ pdf（QuestPDF 下載），預設 browser]：
```

---

## Step 2：Controller 加入 Print Action

在現有 Controller（不建新 Controller）加入：

```csharp
[HttpGet]
public async Task<IActionResult> Print(string id)
{
    if (!await _permissionService.CanPrint(MenuKey)) return Forbid();

    var item = await _context.{DbSet}
        .AsNoTracking()
        .FirstOrDefaultAsync(x => x.Id == id && x.PlantId == _userContext.PlantId);
    if (item == null) return NotFound();

    // QR Code：指向本筆記錄的詳情 URL
    var qrContent = Url.Action("Details", "{ControllerName}", new { id }, Request.Scheme)!;
    ViewBag.QrSvg = PrintHelper.GenerateQrSvg(qrContent);

    // 若有明細（HAS_DETAIL=Y）
    // ViewBag.Details = await _context.{DetailDbSet}
    //     .Where(d => d.{ForeignKey} == id)
    //     .OrderBy(d => d.Sequence)
    //     .AsNoTracking()
    //     .ToListAsync();

    return View("Print", item);
}
```

---

## Step 3：Print View（必須嚴格遵守 CSS Class）

檔案：`Views/{ControllerName}/Print.cshtml`

```html
@model {ModelType}
@{
    Layout = "~/Pages/Shared/_PrintLayout.cshtml";
    ViewData["Title"] = "{單據中文名稱}";
    var isEn = System.Globalization.CultureInfo.CurrentUICulture.Name == "en-US";
}

<!-- ① 表頭（QR + 標題）必須用 print-header -->
<div class="print-header">
    <div class="print-qr">@Html.Raw(ViewBag.QrSvg)</div>
    <div>
        <div class="print-title">{公司名稱或標題}</div>
        <div class="print-subtitle">{單據中文名稱}</div>
        <div class="print-doc-number">單號：@Model.{DocNo} ／ 日期：@DateTimeSvc.ToLocalTime(Model.CreatedAt).ToString("yyyy-MM-dd")</div>
    </div>
</div>

<!-- ② 基本資料區塊 -->
<div class="print-section">
    <div class="print-section-title">{區塊標題，如「基本資料」}</div>
    <div class="print-field-grid">  <!-- 2欄 / print-field-grid print-field-grid-3 for 3欄 -->
        <div class="print-field-label">{欄位名稱}</div>
        <div class="print-field-value">@Model.{Field1}</div>

        <div class="print-field-label">{欄位名稱}</div>
        <div class="print-field-value">@Model.{Field2}</div>

        <!-- 日期欄位 -->
        <div class="print-field-label">建立時間</div>
        <div class="print-field-value">@DateTimeSvc.ToLocalTime(Model.CreatedAt).ToString("yyyy-MM-dd HH:mm")</div>
    </div>
</div>

<!-- ③ 明細表格（HAS_DETAIL=Y）-->
@if (ViewBag.Details is IEnumerable<{DetailModel}> details && details.Any())
{
    <div class="print-section">
        <div class="print-section-title">明細</div>
        <table class="print-table">
            <thead>
                <tr>
                    <th>序號</th>
                    <th>{欄位1}</th>
                    <th>{欄位2}</th>
                </tr>
            </thead>
            <tbody>
                @foreach (var d in details)
                {
                    <tr>
                        <td>@d.Sequence</td>
                        <td>@d.{Field1}</td>
                        <td>@d.{Field2}</td>
                    </tr>
                }
            </tbody>
        </table>
    </div>
}

<!-- ④ 簽核欄位（HAS_SIGN=Y）-->
<div class="print-section mt-4">
    <div class="print-sign-area">
        <div><div class="print-field-label">製作</div><div class="print-field-value">&nbsp;</div></div>
        <div><div class="print-field-label">審核</div><div class="print-field-value">&nbsp;</div></div>
        <div><div class="print-field-label">核准</div><div class="print-field-value">&nbsp;</div></div>
        <div><div class="print-field-label">日期</div><div class="print-field-value">&nbsp;</div></div>
    </div>
</div>

<!-- ⑤ 列印按鈕（螢幕顯示，列印時隱藏）-->
<div class="d-print-none text-center mt-4">
    <button class="btn btn-primary" onclick="window.print()">
        <i class="bi bi-printer"></i> 列印
    </button>
    <a asp-action="Index" class="btn btn-secondary ms-2">返回</a>
</div>
```

---

## Step 4：Index View 加入列印連結

在資料列的操作按鈕區加入：

```html
@if (canPrint)
{
    <a asp-action="Print" asp-route-id="@item.Id"
       target="_blank" class="btn btn-sm btn-outline-secondary">
        <i class="bi bi-printer"></i>
    </a>
}
```

---

## Step 5：Resource 加入翻譯

在 `Index.zh-TW.resx`：
```xml
<data name="Print" xml:space="preserve"><value>列印</value></data>
```

---

## Phase 2：PDF 下載（輸出方式 = pdf）

套件：**QuestPDF**（已在 PRINT-ARCHITECTURE.md 規劃）

```bash
dotnet add package QuestPDF
```

Controller 加入 PDF Action：

```csharp
[HttpGet]
public async Task<IActionResult> DownloadPdf(string id)
{
    if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
    var item = await _context.{DbSet}.FindAsync(id);
    if (item == null) return NotFound();

    var pdf = Document.Create(container =>
    {
        container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(1.5f, Unit.Centimetre);
            page.Content().Column(col =>
            {
                col.Item().Text("{單據標題}").FontSize(18).Bold();
                col.Item().Text($"單號：{item.{DocNo}}");
                // ... 其他欄位
            });
        });
    });

    var bytes = pdf.GeneratePdf();
    var fileName = $"{item.{DocNo}}.pdf";
    return File(bytes, "application/pdf", Uri.EscapeDataString(fileName));
}
```

---

## 注意事項

- **所有時間欄位必須用 `DateTimeSvc.ToLocalTime()`**（已在 `_ViewImports.cshtml` 全域注入）
- **QR Code 內容建議使用完整 URL**，方便現場掃描後直接開啟系統
- **Core `Pages/Shared/_PrintLayout.cshtml` 禁止修改**，若需調整樣式在 `print.css` 新增 class
- **列印按鈕使用 `target="_blank"`**，在新分頁開啟列印，不影響目前操作頁面

---

## 一致性框架（列印多語，必用 — 覆蓋上方 Step 2/3）

ProjectZ 新單據列印**必須**用本地化樣板，不得整頁硬編碼中文（建置期 R3 把關）：

- `Print.cshtml` 改 `Layout = "~/Views/Shared/_PrintLayoutZ.cshtml";`（語系感知 `<html lang>`、內建本地化「列印/關閉」與頁尾；**不改 Core `_PrintLayout.cshtml`**）
- 簽核區改 `@await Html.PartialAsync("_PrintBase")`（製作/審核/核准/日期 已本地化），刪除原 Step 3 ④與⑤的手寫區塊
- 標題/區塊標題/表頭一律 `@inject IViewLocalizer Localizer`；共用字串重用既有 `_Form` resx key 或 `ProjectZSharedResource` 的 `Print_*` key（zh-TW＋en-US 同步）
- 控制器 Print action：控制器繼承 `ModuleControllerBase`，用 `await PreparePrintViewBagAsync(qrUrl, new[]{ item.CreatedBy })` 統一帶入 `QrSvg/PrintTime/UserNames`，取代手抄

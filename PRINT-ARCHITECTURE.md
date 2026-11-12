# 單據列印架構規劃

> 狀態：框架已完成 ✅ / **各單據 Print View 已大量實作（22 張，2026-07-09 校正）**；工單列印已完成（`WorkOrderController.Print` + `Modules/MFG/Views/WorkOrder/Print.cshtml`，抬頭＋工序途程＋標準成本，2026-07-09）；僅簽核單（Approval Print，屬 Core Approval 控制器）尚未實作
> 日期：2026-03-13（2026-07-03 依程式實況校正狀態與第 6/7 節）

---

## 1. 需求背景

系統中的業務單據（工單、採購單、簽核單等）需要支援實體列印，且表單上方須印出 QR Code，方便現場掃描追蹤。

目前現況：
- 列表頁有 `window.print()` 按鈕，但直接印完整 HTML（含 sidebar/navbar）
- 無專屬列印版面
- 無 QR Code 功能

---

## 2. 技術選型

### 2.1 QR Code 產生方式

| 方式 | 套件 | 優點 | 缺點 |
|------|------|------|------|
| **Server-side（採用）** | `QRCoder` 1.6.0 (MIT) | 跨瀏覽器一致、無 JS 依賴、向量不失真 | 需安裝 NuGet |
| Client-side | `qrcode.js` | 不需後端 | 仰賴 JS 執行，存 PDF 時可能消失 |

**決策：`SvgQRCode`（向量 SVG）**
- 列印任意 DPI 不失真
- 直接內嵌 `<svg>` 標籤，無需 Base64 或外部圖檔
- 不依賴 `System.Drawing.Common`，相容 Linux / Docker

```csharp
// Services/PrintHelper.cs（已建立）
public static string GenerateQrSvg(string content, int pixelsPerModule = 5)
{
    using var gen = new QRCodeGenerator();
    var data = gen.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
    return new SvgQRCode(data).GetGraphic(pixelsPerModule);
}
```

View 嵌入：
```html
<div class="print-qr">@Html.Raw(ViewBag.QrSvg)</div>
```

### 2.2 列印輸出方式

| 方式 | 說明 |
|------|------|
| **瀏覽器列印（Phase 1）** | 專屬列印 View + `@media print` CSS + `window.print()` |
| PDF 下載（Phase 2） | QuestPDF server-side 產生，直接下載 .pdf |

---

## 3. 共用框架（已完成 ✅）

| 檔案 | 說明 |
|------|------|
| `Services/PrintHelper.cs` | `GenerateQrSvg(content)` 靜態方法 |
| `Pages/Shared/_PrintLayout.cshtml` | 最小化 Layout（無 navbar/sidebar） |
| `wwwroot/css/print.css` | A4 版面、QR SVG 容器、欄位網格、明細表格、簽核區、頁尾 |

### 列印版面 CSS Class 總覽

| CSS Class | 用途 |
|-----------|------|
| `.print-header` | 表頭區（QR + 標題，flex 排列） |
| `.print-qr` | QR Code 容器（80×80px，SVG 填滿） |
| `.print-title` | 單據大標題（20pt，間距字） |
| `.print-subtitle` / `.print-doc-number` | 副標題 / 單號日期列 |
| `.print-section` | 各區塊容器 |
| `.print-section-title` | 區塊標題（下底線） |
| `.print-field-grid` | 2 欄欄位 grid |
| `.print-field-grid-3` | 3 欄欄位 grid（modifier） |
| `.print-field-label` | 欄位名稱（粗體，固定寬度） |
| `.print-field-value` | 欄位值（下底線） |
| `.print-table` | 明細表格（全框線） |
| `.print-sign-area` | 4 欄簽核區 |
| `.print-sign-box` / `.print-sign-line` | 簽核方塊 |
| `.print-footer` | 頁尾（列印時間、系統名稱） |
| `.no-print` | 螢幕顯示、列印時隱藏 |

---

## 4. 工單列印設計（WorkOrder）

### 4.1 版面草圖

```
┌──────────────────────────────────────────────────────┐
│ [QR 80×80]   SmartFactory MES                        │
│              工　　　單                               │
│              WO-20260313-001   列印：2026-03-13 09:00 │
├──────────────────────────────────────────────────────┤
│ 基本資料                                              │
│  品    名：███████████   工  單  號：WO-20260313-001 │
│  預計數量：100 pcs        預計完工：2026-03-20        │
│  所屬廠別：台北廠          建立日期：2026-03-13        │
├──────────────────────────────────────────────────────┤
│  製  表：___________   審  核：___________            │
│  核  准：___________   日  期：___________            │
└──────────────────────────────────────────────────────┘
```

> 工單目前 Model 無製程步驟欄位（`WorkOrder` 僅含基本欄位），
> 若未來擴充製程步驟，在基本資料與簽核區之間加入明細表格。

### 4.2 資料查詢

```csharp
// WorkOrder 屬性（Models/WorkOrder.cs）
// Id (int), OrderNumber (string, max 20), ProductName (string, max 50)
// Quantity (int), ScheduledDate (DateTime)
// PlantId (string?), CreatedAt (DateTime)

var workOrder = await _db.WorkOrders
    .IgnoreQueryFilters()
    .FirstOrDefaultAsync(w => w.Id == id);
```

### 4.3 Controller Action

**檔案：** `Controllers/WorkOrderPrintController.cs`（或在現有 API Controller 旁加 MVC Controller）

```csharp
[Authorize]
public class WorkOrderPrintController : Controller
{
    private readonly ApplicationDbContext _db;

    public WorkOrderPrintController(ApplicationDbContext db) => _db = db;

    // GET /WorkOrderPrint/5
    public async Task<IActionResult> Index(int id)
    {
        var wo = await _db.WorkOrders
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(w => w.Id == id);

        if (wo == null) return NotFound();

        var qrUrl = $"{Request.Scheme}://{Request.Host}/api/workorders/{id}";
        ViewBag.QrSvg = PrintHelper.GenerateQrSvg(qrUrl);
        ViewBag.PrintedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");

        return View(wo);
    }
}
```

### 4.4 Print View

**檔案：** `Views/WorkOrderPrint/Index.cshtml`

```razor
@model SmartFactory.MES.Models.WorkOrder
@{
    Layout = "_PrintLayout";
    ViewData["Title"] = $"工單 {Model.OrderNumber}";
}

<!-- 操作按鈕（列印時隱藏） -->
<div class="print-actions no-print">
    <button onclick="window.print()" class="btn btn-primary btn-sm">
        <i class="bi bi-printer-fill"></i> 列印
    </button>
    <button onclick="window.close()" class="btn btn-outline-secondary btn-sm">關閉</button>
</div>

<!-- 表頭 -->
<div class="print-header">
    <div class="print-qr">@Html.Raw(ViewBag.QrSvg)</div>
    <div class="print-title-block">
        <div class="print-title">工　　　單</div>
        <div class="print-doc-number">
            單號：<strong>@Model.OrderNumber</strong>
            &emsp;列印時間：@ViewBag.PrintedAt
        </div>
    </div>
</div>

<!-- 基本資料 -->
<div class="print-section">
    <div class="print-section-title">基本資料</div>
    <div class="print-field-grid">
        <div class="print-field">
            <span class="print-field-label">品　　名：</span>
            <span class="print-field-value">@Model.ProductName</span>
        </div>
        <div class="print-field">
            <span class="print-field-label">工  單  號：</span>
            <span class="print-field-value">@Model.OrderNumber</span>
        </div>
        <div class="print-field">
            <span class="print-field-label">預計數量：</span>
            <span class="print-field-value">@Model.Quantity</span>
        </div>
        <div class="print-field">
            <span class="print-field-label">預計完工：</span>
            <span class="print-field-value">@Model.ScheduledDate.ToString("yyyy-MM-dd")</span>
        </div>
        <div class="print-field">
            <span class="print-field-label">建立日期：</span>
            <span class="print-field-value">@Model.CreatedAt.ToString("yyyy-MM-dd")</span>
        </div>
    </div>
</div>

<!-- 簽核區 -->
<div class="print-sign-area">
    @foreach (var label in new[] { "製　表", "審　核", "核　准", "收　料" })
    {
        <div class="print-sign-box">
            <div class="print-sign-line"></div>
            <div>@label</div>
        </div>
    }
</div>

<!-- 頁尾 -->
<div class="print-footer">
    <span>SmartFactory MES</span>
    <span>@DateTime.Now.ToString("yyyy-MM-dd HH:mm") 列印</span>
</div>
```

### 4.5 觸發按鈕（加入清單頁）

```html
<!-- 在 WorkOrder 清單列的操作欄加入 -->
<a href="/WorkOrderPrint/@item.Id" target="_blank"
   class="btn btn-sm btn-outline-secondary" title="列印">
    <i class="bi bi-printer"></i>
</a>
```

---

## 5. 簽核單列印設計（ApprovalRequest）

### 5.1 設計說明

簽核單是所有單據中**最複雜**的列印設計，因為它需要：

1. 顯示**被審核的單據**資訊（DocumentType + DocumentId）
2. 顯示**完整審核流程**（所有 ApprovalStep，含順序、核准類型、條件）
3. 根據目前狀態顯示**已審核歷程**（ApprovalLog，含核准人、時間、意見）
4. 未完成的步驟提供**手寫簽名欄位**
5. 整體狀態徽章（待審 / 已核准 / 已拒絕）

### 5.2 版面草圖

```
┌──────────────────────────────────────────────────────┐
│ [QR 80×80]   SmartFactory MES                        │
│              簽　　核　　單                           │
│              類型：WorkOrder  單號：WO-001            │
│              狀態：[ 待審核 ]  列印：2026-03-13       │
├──────────────────────────────────────────────────────┤
│ 基本資訊                                              │
│  單據類型：WorkOrder     單  據  號：WO-001           │
│  申請人員：王小明         申請日期：2026-03-13        │
│  目前狀態：待審核         目前關卡：第 2 關            │
├──────────────────────────────────────────────────────┤
│ 審核流程                                              │
│  ┌────┬────────────────────┬──────────┬────────────┐  │
│  │ 關 │ 核准人             │ 核准條件 │ 結果       │  │
│  ├────┼────────────────────┼──────────┼────────────┤  │
│  │ 1  │ [角色] 主管        │ 無       │ ✓ 已核准   │  │  ← ApprovalLog 有紀錄
│  │    │ 核准人：李主管     │          │ 2026-03-13 │  │
│  │    │ 意見：同意         │          │            │  │
│  ├────┼────────────────────┼──────────┼────────────┤  │
│  │ 2  │ [角色] 副理        │ 無       │ → 待審     │  │  ← 目前關卡
│  │    │ 簽名：___________  │          │            │  │
│  ├────┼────────────────────┼──────────┼────────────┤  │
│  │ 3  │ [角色] 總經理      │ 無       │ — 待審     │  │  ← 尚未到達
│  │    │ 簽名：___________  │          │            │  │
│  └────┴────────────────────┴──────────┴────────────┘  │
├──────────────────────────────────────────────────────┤
│  申請人：___________   列印日期：2026-03-13           │
└──────────────────────────────────────────────────────┘
```

### 5.3 資料查詢

需要 JOIN 四個表：

```csharp
// 1. ApprovalRequest 基本資訊
// 2. ApprovalStep（透過 TemplateId 取得該文件類型的所有步驟）
// 3. ApprovalLog（該 Request 的所有審核紀錄，含審核人姓名）
// 4. ApplicationUser（取申請人姓名、審核人姓名）

public class ApprovalPrintViewModel
{
    public ApprovalRequest Request { get; set; } = null!;
    public string RequesterName { get; set; } = string.Empty;

    // 步驟清單（含對應的 Log，無 Log 則為 null）
    public List<ApprovalStepRow> Steps { get; set; } = new();

    public class ApprovalStepRow
    {
        public ApprovalStep Step { get; set; } = null!;
        public string StepLabel { get; set; } = string.Empty;  // "角色：主管" 或 "人員：王小明"
        public ApprovalLog? Log { get; set; }                  // 已審核則有值
        public string? ApproverName { get; set; }              // 已審核者姓名
        public bool IsCurrent { get; set; }                    // 是否為目前關卡
    }
}
```

### 5.4 Controller Action

**檔案：** `Controllers/ApprovalController.cs`（新增 Print action）

```csharp
// GET /Approval/Print/5
[Authorize]
public async Task<IActionResult> Print(string id)
{
    // ── 1. 載入 ApprovalRequest ──────────────────────────────
    var request = await _db.ApprovalRequests
        .IgnoreQueryFilters()
        .FirstOrDefaultAsync(r => r.Id == id);

    if (request == null) return NotFound();

    // ── 2. 申請人姓名 ─────────────────────────────────────────
    var requester = await _db.Users.FindAsync(request.RequesterId);
    var requesterName = requester?.UserName ?? request.RequesterId ?? "-";

    // ── 3. 取得審核樣板的所有步驟（依 DocumentType 找 Template）──
    var template = await _db.ApprovalTemplates
        .Include(t => t.Steps)
        .IgnoreQueryFilters()
        .FirstOrDefaultAsync(t => t.TargetDocumentType == request.DocumentType);

    var steps = template?.Steps
        .OrderBy(s => s.Sequence)
        .ToList() ?? new List<ApprovalStep>();

    // ── 4. 取得該 Request 的所有審核紀錄 ─────────────────────
    var logs = await _db.ApprovalLogs
        .Where(l => l.RequestId == id)
        .OrderBy(l => l.ActionTime)
        .ToListAsync();

    // ── 5. 審核人姓名對照表 ───────────────────────────────────
    var approverIds = logs.Select(l => l.ApproverId).Distinct().ToList();
    var approvers = await _db.Users
        .Where(u => approverIds.Contains(u.Id))
        .ToDictionaryAsync(u => u.Id, u => u.UserName ?? u.Id);

    // ── 6. 角色名稱對照表 ─────────────────────────────────────
    var roleIds = steps
        .Where(s => s.TargetRoleId != null)
        .Select(s => s.TargetRoleId!)
        .Distinct()
        .ToList();
    var roles = await _db.Roles
        .Where(r => roleIds.Contains(r.Id))
        .ToDictionaryAsync(r => r.Id, r => r.Name ?? r.Id);

    // ── 7. 組合 ViewModel ─────────────────────────────────────
    // logs 依步驟 Sequence 對應（一個步驟最多一筆 log）
    var logBySeq = new Dictionary<int, ApprovalLog>();
    for (int i = 0; i < logs.Count && i < steps.Count; i++)
        logBySeq[steps[i].Sequence] = logs[i];

    var stepRows = steps.Select(s =>
    {
        logBySeq.TryGetValue(s.Sequence, out var log);
        return new ApprovalPrintViewModel.ApprovalStepRow
        {
            Step = s,
            StepLabel = s.ApproverType switch
            {
                ApproverType.Role => $"角色：{(s.TargetRoleId != null && roles.TryGetValue(s.TargetRoleId, out var rn) ? rn : s.TargetRoleId ?? "-")}",
                ApproverType.User => $"人員：{(s.TargetUserId != null && approvers.TryGetValue(s.TargetUserId, out var un) ? un : s.TargetUserId ?? "-")}",
                ApproverType.Supervisor => "直屬主管",
                _ => "-"
            },
            Log = log,
            ApproverName = log != null && approvers.TryGetValue(log.ApproverId, out var n) ? n : null,
            IsCurrent = s.Id == request.CurrentStepId
        };
    }).ToList();

    var vm = new ApprovalPrintViewModel
    {
        Request = request,
        RequesterName = requesterName,
        Steps = stepRows
    };

    // ── 8. QR Code（掃描可直接開啟簽核畫面） ──────────────────
    var qrUrl = $"{Request.Scheme}://{Request.Host}/Approval/Process/{id}";
    ViewBag.QrSvg = PrintHelper.GenerateQrSvg(qrUrl);
    ViewBag.PrintedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");

    return View(vm);
}
```

> ViewModel 建議放置路徑：`ViewModels/ApprovalPrintViewModel.cs`

### 5.5 Print View

**檔案：** `Views/Approval/Print.cshtml`

```razor
@model SmartFactory.MES.ViewModels.ApprovalPrintViewModel
@using SmartFactory.MES.Models
@{
    Layout = "_PrintLayout";
    var statusText = Model.Request.Status switch {
        ApprovalStatus.Pending  => "待審核",
        ApprovalStatus.Approved => "已核准",
        ApprovalStatus.Rejected => "已拒絕",
        _ => "-"
    };
    ViewData["Title"] = $"簽核單 {Model.Request.DocumentId}";
}

<!-- 操作按鈕 -->
<div class="print-actions no-print">
    <button onclick="window.print()" class="btn btn-primary btn-sm">
        <i class="bi bi-printer-fill"></i> 列印
    </button>
    <button onclick="window.close()" class="btn btn-outline-secondary btn-sm">關閉</button>
</div>

<!-- 表頭 -->
<div class="print-header">
    <div class="print-qr">@Html.Raw(ViewBag.QrSvg)</div>
    <div class="print-title-block">
        <div class="print-title">簽　核　單</div>
        <div class="print-doc-number">
            類型：<strong>@Model.Request.DocumentType</strong>
            &emsp;單號：<strong>@Model.Request.DocumentId</strong>
        </div>
        <div class="print-subtitle">
            狀態：
            <span style="font-weight:bold">@statusText</span>
            &emsp;列印：@ViewBag.PrintedAt
        </div>
    </div>
</div>

<!-- 基本資訊 -->
<div class="print-section">
    <div class="print-section-title">基本資訊</div>
    <div class="print-field-grid">
        <div class="print-field">
            <span class="print-field-label">單據類型：</span>
            <span class="print-field-value">@Model.Request.DocumentType</span>
        </div>
        <div class="print-field">
            <span class="print-field-label">單  據  號：</span>
            <span class="print-field-value">@Model.Request.DocumentId</span>
        </div>
        <div class="print-field">
            <span class="print-field-label">申請人員：</span>
            <span class="print-field-value">@Model.RequesterName</span>
        </div>
        <div class="print-field">
            <span class="print-field-label">目前狀態：</span>
            <span class="print-field-value">@statusText</span>
        </div>
    </div>
</div>

<!-- 審核流程 -->
<div class="print-section">
    <div class="print-section-title">審核流程</div>
    <table class="print-table">
        <thead>
            <tr>
                <th style="width:5%">關卡</th>
                <th style="width:28%">核准人</th>
                <th style="width:20%">觸發條件</th>
                <th style="width:15%">結果</th>
                <th style="width:32%">核准人簽名 / 意見</th>
            </tr>
        </thead>
        <tbody>
            @foreach (var row in Model.Steps)
            {
                <tr>
                    <!-- 關卡號 -->
                    <td>
                        @row.Step.Sequence
                        @if (row.IsCurrent && Model.Request.Status == ApprovalStatus.Pending)
                        {
                            <br /><small>▶ 目前</small>
                        }
                    </td>

                    <!-- 核准人類型與名稱 -->
                    <td class="text-left">@row.StepLabel</td>

                    <!-- 觸發條件 -->
                    <td>
                        @if (!string.IsNullOrWhiteSpace(row.Step.Condition))
                        {
                            <small>@row.Step.Condition</small>
                        }
                        else
                        {
                            <span class="text-muted">—</span>
                        }
                    </td>

                    <!-- 審核結果 -->
                    <td>
                        @if (row.Log != null)
                        {
                            if (row.Log.Action == ApprovalStatus.Approved)
                            {
                                <span>✓ 核准</span><br />
                                <small>@row.Log.ActionTime.ToString("MM/dd HH:mm")</small>
                            }
                            else
                            {
                                <span>✗ 拒絕</span><br />
                                <small>@row.Log.ActionTime.ToString("MM/dd HH:mm")</small>
                            }
                        }
                        else if (row.IsCurrent && Model.Request.Status == ApprovalStatus.Pending)
                        {
                            <span>⋯ 待審</span>
                        }
                        else
                        {
                            <span class="text-muted">— 待審</span>
                        }
                    </td>

                    <!-- 簽名 / 意見 -->
                    <td class="text-left">
                        @if (row.Log != null)
                        {
                            <!-- 已審核：顯示審核人姓名與意見 -->
                            <strong>@row.ApproverName</strong>
                            @if (!string.IsNullOrWhiteSpace(row.Log.Comment))
                            {
                                <br /><small>@row.Log.Comment</small>
                            }
                        }
                        else
                        {
                            <!-- 未審核：提供手寫簽名空間 -->
                            <div style="height:36px; border-bottom:1px solid #999; margin-bottom:2px;"></div>
                            <small class="text-muted">簽名 / 日期</small>
                        }
                    </td>
                </tr>
            }
        </tbody>
    </table>
</div>

<!-- 底部申請人簽名 -->
<div class="print-sign-area" style="grid-template-columns: repeat(2, 1fr);">
    <div class="print-sign-box">
        <div class="print-sign-line"></div>
        <div>申請人</div>
    </div>
    <div class="print-sign-box">
        <div class="print-sign-line"></div>
        <div>列印日期</div>
    </div>
</div>

<!-- 頁尾 -->
<div class="print-footer">
    <span>SmartFactory MES — 簽核單</span>
    <span>@DateTime.Now.ToString("yyyy-MM-dd HH:mm") 列印</span>
</div>
```

### 5.6 ViewModel

**檔案：** `ViewModels/ApprovalPrintViewModel.cs`（需建立）

```csharp
using SmartFactory.MES.Models;

namespace SmartFactory.MES.ViewModels
{
    public class ApprovalPrintViewModel
    {
        public ApprovalRequest Request { get; set; } = null!;
        public string RequesterName { get; set; } = string.Empty;
        public List<ApprovalStepRow> Steps { get; set; } = new();

        public class ApprovalStepRow
        {
            public ApprovalStep Step { get; set; } = null!;
            /// <summary>核准人描述文字，例如「角色：主管」或「直屬主管」</summary>
            public string StepLabel { get; set; } = string.Empty;
            /// <summary>已完成此關卡時，對應的 ApprovalLog；否則為 null</summary>
            public ApprovalLog? Log { get; set; }
            /// <summary>已審核者的顯示名稱</summary>
            public string? ApproverName { get; set; }
            /// <summary>是否為 Request.CurrentStepId 指向的當前關卡</summary>
            public bool IsCurrent { get; set; }
        }
    }
}
```

### 5.7 觸發按鈕

```html
<!-- PendingIndex.cshtml 與 AllRequests.cshtml 的操作欄加入 -->
<a href="/Approval/Print/@item.Id" target="_blank"
   class="btn btn-sm btn-outline-secondary" title="列印簽核單">
    <i class="bi bi-printer"></i>
</a>
```

---

## 6. 適用單據清單

> **2026-07-03 校正**：本表初版僅列 3 張規劃單據，實際已實作 21 張 Print View（依本架構的 `_PrintLayout` + `print.css` + QR 慣例）。

**已實作（21 張 `Print.cshtml`，依模組）**：

| 模組 | 單據 Print View |
|------|----------------|
| PUR | PurchaseRequest（請購）、PurchaseOrder（採購）、GoodsReceipt（進貨）、GoodsReturn（退貨） |
| ORD | SalesOrder（銷售訂單）、InternalOrder（內部訂單）、GoodsShipment（出貨）、SalesReturn（銷退） |
| INV | StockTransfer（調撥）、StockAdjustment（調整）、ScrapDoc（報廢）、SampleDoc（樣品）、LoanDoc（借出）、OutsourceMaterial（委外發退料）、ReworkMaterial（重工發退料）、StockMovement（庫存異動）、InventoryBalance（庫存餘額）、StockLedger（庫存分類帳） |
| RPT | ProfitLoss（損益）、BalanceSheet（資產負債）、GrossMargin（毛利） |

**尚未實作**：

| 單據 | Controller Action | 優先級 | 狀態 |
|------|------------------|--------|------|
| 工單 (WorkOrder) | `WorkOrderPrintController.Index` | P1 | 待實作 |
| 簽核單 (ApprovalRequest) | `ApprovalController.Print` | P1 | 待實作 |

---

## 7. 實作 Checklist

### Phase 1：基礎框架（已完成 ✅）

- [x] 安裝 `QRCoder` 1.6.0 NuGet
- [x] 建立 `Services/PrintHelper.cs`（SVG 輸出）
- [x] 建立 `Pages/Shared/_PrintLayout.cshtml`
- [x] 建立 `wwwroot/css/print.css`（A4、SVG QR、簽核區、欄位 grid）

### Phase 1.5：各業務單據列印（已完成 ✅，2026-07-03 校正補記）

- [x] PUR/ORD/INV/RPT 共 21 張 `Print.cshtml`（清單見第 6 節），均循 `_PrintLayout` + `print.css` + QR 慣例

### Phase 2：工單列印

- [ ] 建立 `Controllers/WorkOrderPrintController.cs`
- [ ] 建立 `Views/WorkOrderPrint/Index.cshtml`
- [ ] 在工單清單頁加入列印按鈕

### Phase 3：簽核單列印

- [ ] 建立 `ViewModels/ApprovalPrintViewModel.cs`
- [ ] 在 `ApprovalController` 新增 `Print(string id)` action
- [ ] 建立 `Views/Approval/Print.cshtml`
- [ ] 在 `PendingIndex` 與 `AllRequests` 加入列印按鈕

### Phase 4：PDF 下載（選配）

- [ ] 評估 QuestPDF vs DinkToPdf
- [ ] 建立通用 PDF 下載 Action
- [ ] 整合至列印按鈕（增加「存 PDF」選項）

---

## 8. 注意事項

1. **中文字型**：`Pages/Shared/_PrintLayout.cshtml` 使用 `"Noto Sans TC", "Microsoft JhengHei"`，Windows/Linux 均須安裝對應字型
2. **QRCoder + Docker**：使用 `SvgQRCode`（非 `QRCode` class），不依賴 `System.Drawing.Common`，相容 Linux container
3. **權限控管**：每個 Print action 均需 `[Authorize]`，未來可細化為 `CanPrint(MenuKey)`
4. **新分頁開啟**：觸發按鈕一律 `target="_blank"`，列印後關閉分頁不影響操作流程
5. **簽核單步驟對 Log 的對應**：目前以「步驟順序 vs Log 時間序」對應；若 ApprovalService 有記錄 StepId 欄位，可改為精確對應
6. **未審核步驟**：顯示空白手寫欄，讓實體單據仍可手寫補簽（混合流程場景）

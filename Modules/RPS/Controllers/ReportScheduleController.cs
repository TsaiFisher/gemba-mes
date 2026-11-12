using Microsoft.AspNetCore.Authorization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SmartFactory.MES.Modules.RPS.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.RPS.Models;
using SmartFactory.MES.Modules.RPS.Services;
using SmartFactory.MES.Modules.RPS.ViewModels;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.RPS.Controllers;

/// <summary>
/// 報表排程維護（S-1，§16.1）。
///
/// <para><b>兩層權限，不可混為一談</b>（§16.2 權限動詞的宣告方式）：
/// 本畫面自己的五動詞查 <see cref="MenuKey"/>（誰能維護排程設定），
/// 而「這筆排程要跑哪一支報表」另外檢查**目標報表的 MenuKey**（誰能看那份資料）。
/// 混用會出現「有排程維護權限就能跑任何報表」的越權路徑。</para>
/// </summary>
[Authorize]
public class ReportScheduleController : ModuleControllerBase
{
    private const string MenuKey = "ReportSchedule";

    /// <summary>廠別篩選選「全部」時送的值。不用空字串：空字串＝沒送＝預設目前廠別。</summary>
    internal const string AllPlants = "*";

    private readonly ApplicationDbContext _context;
    private readonly IPermissionService _permissionService;
    private readonly IReportToolCatalog _catalog;
    private readonly IStringLocalizer _localizer;

    public ReportScheduleController(
        ApplicationDbContext context,
        IPermissionService permissionService,
        IReportToolCatalog catalog,
        IStringLocalizerFactory localizerFactory)
    {
        _context = context;
        _permissionService = permissionService;
        _catalog = catalog;
        _localizer = localizerFactory.Create(typeof(ProjectZSharedResource));
    }

    // GET: /ReportSchedule
    public async Task<IActionResult> Index(
        string? searchTerm, string? toolKey, bool? activeOnly, string? plantFilter,
        int? pageNumber, int? pageSize, string? sortOrder, bool searched = false)
    {
        if (!await _permissionService.CanRead(MenuKey)) return Forbid();

        // 本畫面是管理視角，看得到全部廠別的排程（規格 §17.4 REPORT_ADMIN）。
        // 預設只列目前廠別，免得不同廠別的排程混在一起；要看全部選「全部廠別」。
        plantFilter = string.IsNullOrWhiteSpace(plantFilter) ? UserContext.PlantId : plantFilter;
        ViewBag.PlantFilter = plantFilter;
        var plants = await ReportPlantDisplay.LoadAsync(_context);
        ViewBag.Plants = plants;

        ViewBag.SearchTerm = searchTerm;
        ViewBag.ToolKey = toolKey;
        ViewBag.ActiveOnly = activeOnly;
        ViewBag.Searched = searched;
        ViewBag.CurrentPageSize = pageSize ?? 20;
        ViewBag.CurrentSort = sortOrder;
        ViewBag.NameSort = sortOrder == "name_asc" ? "name_desc" : "name_asc";
        ViewBag.NextRunSort = sortOrder == "next_asc" ? "next_desc" : "next_asc";

        await PrepareIndexPermissionsAsync(MenuKey);
        await PrepareFormDropdownsAsync();

        if (!searched) return View((PaginatedList<ReportDefinition>?)null);

        // 只列排程（ScheduleKind != null）。常用查詢是同一張表的另一種狀態，入口在報表畫面上。
        var query = _context.ReportDefinitions.AsNoTracking()
            .Where(d => d.ScheduleKind != null);

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var lower = searchTerm.ToLower();
            query = query.Where(d => d.Name.ToLower().Contains(lower));
        }

        if (!string.IsNullOrWhiteSpace(toolKey))
            query = query.Where(d => d.ToolKey == toolKey);

        if (activeOnly == true)
            query = query.Where(d => d.IsActive);

        if (plantFilter != AllPlants)
            query = query.Where(d => d.PlantId == plantFilter);

        query = sortOrder switch
        {
            "name_asc"  => query.OrderBy(d => d.Name),
            "name_desc" => query.OrderByDescending(d => d.Name),
            "next_desc" => query.OrderByDescending(d => d.NextRunAt),
            _           => query.OrderBy(d => d.NextRunAt),
        };

        var ps = pageSize ?? 20;
        var page = await PaginatedList<ReportDefinition>.CreateAsync(query, pageNumber ?? 1, ps);

        // 清單直接顯示 OwnerUserId 對讀者沒有意義（那是 GUID）。
        ViewBag.OwnerNames = await _context.Users.AsNoTracking()
            .Where(u => page.Select(d => d.OwnerUserId).Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.UserName ?? u.Id);

        // 下次執行時刻依排程所屬時區顯示（與派工器計算時刻用的是同一個時區）。
        var timeZones = new Dictionary<int, string?>();
        foreach (var d in page)
            timeZones[d.Id] = await ReportScheduleTimeZoneResolver.ResolveAsync(_context, d);
        ViewBag.TimeZones = timeZones;

        return View(page);
    }

    // GET: /ReportSchedule/Create
    [HttpGet]
    public async Task<IActionResult> Create()
    {
        if (!await _permissionService.CanCreate(MenuKey)) return Forbid();

        await PrepareFormDropdownsAsync();
        ViewBag.IsEdit = false;
        var model = new ReportScheduleFormVm();
        await FillPlantDisplayAsync(model, UserContext.PlantId);
        return FormView(model);
    }

    // POST: /ReportSchedule/Create
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ReportScheduleFormVm model)
    {
        if (!await _permissionService.CanCreate(MenuKey)) return Forbid();

        var failure = await ValidateAsync(model);
        if (failure is not null) return failure;

        var userId = UserContext.UserId ?? "";
        var roleId = UserContext.ActiveRoleId ?? "";

        var definition = new ReportDefinition
        {
            Name            = model.Name.Trim(),
            OwnerUserId     = userId,
            OwnerRoleId     = roleId,
            CreatedByRoleId = roleId,
            PlantId         = UserContext.PlantId,
            ToolKey         = model.ToolKey,
            // 空物件是初始值而已——期間由下面的 ApplyAsync 統一寫入（Create 與 Edit 同一條路）。
            // 在這裡也寫一次的話，兩個寫入點遲早會有一個被改漏，而症狀是排程少了參數。
            ParamsJson      = "{}",
            CreatedAt       = DateTime.UtcNow,
        };

        await ApplyAsync(model, definition);
        _context.ReportDefinitions.Add(definition);
        await _context.SaveChangesAsync();

        await SaveRecipientsAsync(definition.Id, model.RecipientUserIds);
        await _context.SaveChangesAsync();

        return JsonOk(message: _localizer["SaveSuccess"].Value);
    }

    // GET: /ReportSchedule/Edit/5
    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();

        var definition = await _context.ReportDefinitions.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id && d.ScheduleKind != null);
        if (definition is null) return NotFound();

        await PrepareFormDropdownsAsync();
        ViewBag.IsEdit = true;

        var recipients = await _context.ReportRecipients.AsNoTracking()
            .Where(r => r.DefinitionId == id)
            .Select(r => r.UserId)
            .ToListAsync();

        var model = new ReportScheduleFormVm
        {
            Id                 = definition.Id,
            Name               = definition.Name,
            ToolKey            = definition.ToolKey,
            ScheduleKind       = definition.ScheduleKind!.Value,
            Period             = ReadPeriod(definition.ParamsJson),
            ScheduleHour       = definition.ScheduleHour ?? 8,
            ScheduleMinute     = definition.ScheduleMinute ?? 0,
            ScheduleDayOfWeek  = definition.ScheduleDayOfWeek ?? 1,
            ScheduleDayOfMonth = definition.ScheduleDayOfMonth ?? 1,
            OutputFormat       = definition.OutputFormat,
            IsActive           = definition.IsActive,
            NextRunAt          = definition.NextRunAt,
            RecipientUserIds   = recipients,
            OwnerUserName      = await _context.Users.AsNoTracking()
                .Where(u => u.Id == definition.OwnerUserId)
                .Select(u => u.UserName).FirstOrDefaultAsync(),
        };
        await FillPlantDisplayAsync(model, definition.PlantId,
            await ReportScheduleTimeZoneResolver.ResolveAsync(_context, definition));
        return FormView(model);
    }

    // POST: /ReportSchedule/Edit/5
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, ReportScheduleFormVm model)
    {
        if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();

        var definition = await _context.ReportDefinitions
            .FirstOrDefaultAsync(d => d.Id == id && d.ScheduleKind != null);
        if (definition is null) return NotFound();

        model.Id = id;
        var failure = await ValidateAsync(model);
        if (failure is not null) return failure;

        await ApplyAsync(model, definition);
        definition.UpdatedAt = DateTime.UtcNow;

        await SaveRecipientsAsync(id, model.RecipientUserIds);
        await _context.SaveChangesAsync();

        return JsonOk(message: _localizer["SaveSuccess"].Value);
    }

    // POST: /ReportSchedule/Delete/5
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        if (!await _permissionService.CanDelete(MenuKey)) return Forbid();

        var definition = await _context.ReportDefinitions
            .FirstOrDefaultAsync(d => d.Id == id && d.ScheduleKind != null);
        if (definition is null) return NotFound();

        // 收件人子表由 cascade 一併刪除；ReportRun 刻意保留——
        // 執行紀錄是稽核軌跡，不該因為排程被刪掉就消失（見 ApplicationDbContextExtensions 註解）。
        _context.ReportDefinitions.Remove(definition);
        await _context.SaveChangesAsync();

        return JsonOk(message: _localizer["DeleteSuccess"].Value);
    }

    /// <summary>
    /// 立即執行一次。走的是派工器同一段程式碼，不是另一條路徑——
    /// 兩份實作會隨時間漂移，而排程這種無人看管的東西漂移了不會有人發現。
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RunNow(int id)
    {
        // ⚠️ 權限要擋在查資料**之前**，與本 controller 其餘四支一致。
        // 反過來寫不會少擋到誰，但會讓回應碼說話：存在的 id 回 403、不存在的回 404，
        // 於是沒有權限的人可以拿 id 逐個問「這筆排程在不在」。
        // 同模組的 ReportInbox.Download 為了同一件事，連「不是收件人」都刻意回 NotFound。
        if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();

        var definition = await _context.ReportDefinitions.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id && d.ScheduleKind != null);
        if (definition is null) return NotFound();

        // 手動觸發等於「跑一份報表」，所以還要查**目標報表**的權限，不只本畫面的。
        if (!await _catalog.IsAuthorizedAsync(definition.ToolKey)) return Forbid();

        // 把 NextRunAt 拉到現在，讓下一次掃描把它撈起來。不直接呼叫執行邏輯是刻意的：
        // 直接呼叫等於在請求執行緒上跑一份完整報表，大報表會讓瀏覽器等到逾時。
        var tracked = await _context.ReportDefinitions.FirstAsync(d => d.Id == id);
        tracked.NextRunAt = DateTime.UtcNow;

        // 派工器據此把產出標成「手動」。少了這個旗標它無從分辨——
        // 排程到期與有人按按鈕，在它眼裡都只是一筆 NextRunAt 到了的定義。
        tracked.ManualRunRequested = true;
        await _context.SaveChangesAsync();

        return JsonOk(message: _localizer["ReportScheduleQueued"].Value);
    }

    // ── 私有輔助 ────────────────────────────────────────────────────────

    private IActionResult FormView(ReportScheduleFormVm model)
        => IsAjax ? PartialView("_ReportScheduleForm", model) : View("_ReportScheduleForm", model);

    /// <summary>驗證失敗時回傳可直接 return 的結果；通過則回 <c>null</c>。</summary>
    private async Task<IActionResult?> ValidateAsync(ReportScheduleFormVm model)
    {
        // 目標報表的權限：沒有它就不該建得出這筆排程，否則等於用排程繞過報表權限。
        if (!await _catalog.IsAuthorizedAsync(model.ToolKey))
            ModelState.AddModelError(nameof(model.ToolKey), _localizer["ReportToolForbidden"].Value);

        // 同一人不得有兩筆同名（唯一鍵 (OwnerUserId, Name)）。先擋在這裡，
        // 讓使用者看到欄位級訊息，而不是存檔時吃一個 23505。
        var userId = UserContext.UserId ?? "";
        var duplicate = await _context.ReportDefinitions
            .AnyAsync(d => d.OwnerUserId == userId && d.Name == model.Name && d.Id != model.Id);
        if (duplicate)
            ModelState.AddModelError(nameof(model.Name), _localizer["DuplicateName"].Value);

        // 吃期間的報表少了期間，排程執行時 RelativePeriodResolver 會直接拋例外——
        // 而那個例外要等到下一次派工才看得到，使用者這邊只會看到「存檔成功」。
        // 擋在存檔這一刻，錯誤才落在按下按鈕的那個人面前。
        if (model.Period is null && _catalog.Find(model.ToolKey)?.RequiresPeriod == true)
            ModelState.AddModelError(nameof(model.Period), _localizer["PeriodRequired"].Value);

        if (ModelState.IsValid) return null;

        await PrepareFormDropdownsAsync();
        ViewBag.IsEdit = model.Id != 0;
        var existing = model.Id == 0 ? null
            : await _context.ReportDefinitions.AsNoTracking().FirstOrDefaultAsync(d => d.Id == model.Id);
        if (existing is null) await FillPlantDisplayAsync(model, UserContext.PlantId);
        else await FillPlantDisplayAsync(model, existing.PlantId,
            await ReportScheduleTimeZoneResolver.ResolveAsync(_context, existing));
        return ValidationPartial("_ReportScheduleForm", model);
    }

    /// <summary>
    /// 表單唯讀顯示「對象廠別」。新增＝目前所在廠別（Create 存的就是 <c>UserContext.PlantId</c>），
    /// 編輯＝排程原本的廠別（編輯不改廠別）。
    /// 不做成可選：在 ADM 替 P2 建排程，要另外檢查建立者對 P2 的權限，範圍會變大；
    /// 「切到哪個廠就建哪個廠的排程」本身沒問題，問題只在原本看不見。
    /// </summary>
    private async Task FillPlantDisplayAsync(ReportScheduleFormVm model, string? plantId, string? timeZoneId = null)
    {
        var plants = await ReportPlantDisplay.LoadAsync(_context);
        var info = plantId is null ? null : plants.GetValueOrDefault(plantId);
        model.PlantLabel  = info?.Label ?? plantId;
        model.TimeZoneId  = timeZoneId ?? info?.TimeZoneId;
    }

    private async Task ApplyAsync(ReportScheduleFormVm model, ReportDefinition definition)
    {
        definition.Name         = model.Name.Trim();
        definition.ToolKey      = model.ToolKey;

        // 期間併進既有 ParamsJson 而不是整個換掉：這一欄目前是排程唯一存得進去的參數，
        // 但將來多一個就會有人在這裡踩到「存了新的、舊的不見了」。
        definition.ParamsJson   = WriteParams(definition.ParamsJson, model.Period);
        definition.ScheduleKind = model.ScheduleKind;
        definition.OutputFormat = model.OutputFormat;
        definition.IsActive     = model.IsActive;

        definition.ScheduleHour   = model.ScheduleHour;
        definition.ScheduleMinute = model.ScheduleMinute;

        // 只寫該週期用得到的欄位，其餘設 null。留著舊值的話，
        // 從 Weekly 改成 Monthly 之後畫面仍會顯示「星期一」，看起來像設定沒生效。
        definition.ScheduleDayOfWeek  = model.ScheduleKind == ReportScheduleKind.Weekly
            ? model.ScheduleDayOfWeek : null;
        definition.ScheduleDayOfMonth = model.ScheduleKind == ReportScheduleKind.Monthly
            ? model.ScheduleDayOfMonth : null;

        // 設定一改就重算下一次執行時刻，否則改了時間仍會照舊時刻跑一次。
        // ⚠️ 時區必須與派工器用同一份邏輯（`ReportScheduleTimeZoneResolver`）。
        // 原本這裡傳 `timeZoneId: null`＝一律 Asia/Taipei，而派工器讀建立者的個人時區，
        // 兩條路徑各算各的——設定一改，下次執行時刻就跳到另一個時區算出來的時刻
        // （manual-test-plan §11 #10）。
        var timeZoneId = await ReportScheduleTimeZoneResolver.ResolveAsync(_context, definition);
        definition.NextRunAt = ReportScheduleCalculator.ComputeNextRunUtc(
            definition, DateTime.UtcNow, timeZoneId);
    }

    private async Task SaveRecipientsAsync(int definitionId, List<string> userIds)
    {
        var existing = await _context.ReportRecipients
            .Where(r => r.DefinitionId == definitionId)
            .ToListAsync();

        _context.ReportRecipients.RemoveRange(existing);

        foreach (var userId in userIds.Where(u => !string.IsNullOrWhiteSpace(u)).Distinct())
        {
            _context.ReportRecipients.Add(new ReportRecipient
            {
                DefinitionId = definitionId,
                UserId       = userId,
                // 語系留 null＝沿用 owner 語系。使用者自己的偏好在 ApplicationUser.Language，
                // 派工器產檔時才解析——設定當下寫死會讓之後改語系的人收到舊語系的報表。
                Language     = null,
            });
        }
    }

    /// <summary>與 <c>ReportToolRunner</c> 同一組設定：列舉存名稱不存序數。</summary>
    private static readonly JsonSerializerOptions ParamsJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// 把期間併進既有 <c>ParamsJson</c>，**保留其他欄位**。
    /// 解不開的舊內容一律從空物件重建——寧可丟掉一份本來就讀不出來的參數，
    /// 也不要讓存檔因為一筆壞資料而失敗（那會讓使用者連修都修不了）。
    /// </summary>
    private static string WriteParams(string? existing, RelativePeriod? period)
    {
        JsonObject root;
        try
        {
            root = string.IsNullOrWhiteSpace(existing)
                ? new JsonObject()
                : JsonNode.Parse(existing) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            root = new JsonObject();
        }

        if (period is null) root.Remove("period");
        else root["period"] = period.Value.ToString();

        return root.ToJsonString(ParamsJsonOptions);
    }

    /// <summary>讀回期間。讀不出來就當沒有——編輯畫面要開得起來，才有機會把它補上。</summary>
    private static RelativePeriod? ReadPeriod(string? paramsJson)
    {
        if (string.IsNullOrWhiteSpace(paramsJson)) return null;
        try
        {
            var value = (JsonNode.Parse(paramsJson) as JsonObject)?["period"]?.GetValue<string>();
            return Enum.TryParse<RelativePeriod>(value, out var period) ? period : null;
        }
        catch (JsonException) { return null; }
    }

    private async Task PrepareFormDropdownsAsync()
    {
        // 只列出目前使用者有權執行的工具。列出無權的等於告訴他系統有哪些報表，
        // 而且他建了也只會在執行時被 §7.8 S1 擋掉。
        var tools = await _catalog.GetAllWithAccessAsync();
        ViewBag.ToolOptions = tools
            .Where(t => t.IsAuthorized)
            .Select(t => new
            {
                t.Tool.ToolKey,
                t.Tool.Description,
                // 報表的顯示名稱與選單共用同一組鍵（ToolKey ＝ MenuKey，由 ReportToolCatalogTests
                // 守住 1:1），所以這裡直接查得到中英文。先前下拉印的是 ToolKey 本身，
                // 於是切成英文介面也還是顯示 "InventoryValuation" 這種內部識別字。
                DisplayName = _localizer[t.Tool.ToolKey].Value,
                // 畫面據此決定要不要顯示期間欄位。不吃期間的報表（庫存評估表這類看當下快照的）
                // 長出一個永遠不生效的期間下拉，比少一個欄位更難解釋。
                t.Tool.RequiresPeriod,
            })
            // 依顯示名稱排序而非 ToolKey：中文名的順序與英文識別字的字母序毫無關係，
            // 照 ToolKey 排在畫面上就是亂的。
            .OrderBy(x => x.DisplayName, StringComparer.CurrentCulture)
            .ToList();

        ViewBag.UserOptions = await _context.Users.AsNoTracking()
            .OrderBy(u => u.UserName)
            .Select(u => new { u.Id, Name = u.UserName ?? u.Id })
            .ToListAsync();
    }
}

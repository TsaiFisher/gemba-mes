using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;
using System.Globalization;
using SmartFactory.MES.Modules.ACT;
using SmartFactory.MES.Modules._Shared.Printing;

namespace SmartFactory.MES.Modules.ACT.Controllers
{
    [Authorize]
    public class JournalEntryController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IPermissionService _permissionService;
        private readonly IUserContext _userContext;
        private readonly IExportService _exportService;
        private readonly IDateTimeService _dateTimeService;
        private readonly Services.IJournalEntryService _journalService;
        private readonly Microsoft.Extensions.Localization.IStringLocalizer<ProjectZSharedResource> _localizer;

        private const string MenuKey = "JournalEntry";

        public JournalEntryController(
            ApplicationDbContext context, IPermissionService permissionService,
            IUserContext userContext, IExportService exportService, IDateTimeService dateTimeService,
            Services.IJournalEntryService journalService,
            Microsoft.Extensions.Localization.IStringLocalizer<ProjectZSharedResource> localizer)
        {
            _context = context; _permissionService = permissionService;
            _userContext = userContext; _exportService = exportService;
            _dateTimeService = dateTimeService;
            _journalService = journalService;
            _localizer = localizer;
        }

        public async Task<IActionResult> Index(
            string? searchPeriod, string? searchDocType, string? sortOrder,
            bool searched = false, int? pageNumber = 1, int? pageSize = 20)
        {
            // 機密分級改由 MenuPermission 資料表達（見 RolePermissionSeeder.ConfidentialReports）；閘門動詞一律 CanRead。
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();

            ViewBag.Searched        = searched;
            ViewBag.CurrentPageSize = pageSize ?? 20;
            ViewBag.SortOrder       = sortOrder;
            ViewBag.DateSort        = sortOrder == "date_asc" ? "date_desc" : "date_asc";
            ViewBag.SearchPeriod    = searchPeriod;
            ViewBag.SearchDocType   = searchDocType;
            ViewBag.CanPrint        = await _permissionService.CanPrint(MenuKey);
            ViewBag.CanCreate       = await _permissionService.CanCreate(MenuKey);
            ViewBag.PeriodOptions   = await GetPeriodOptionsAsync();
            ViewBag.DocTypeOptions  = await GetDocTypeOptionsAsync();

            if (!searched) return View((PaginatedList<JournalEntry>?)null);

            var query = _context.JournalEntries
                .Where(j => j.PlantId == _userContext.PlantId)
                .AsNoTracking();

            if (!string.IsNullOrWhiteSpace(searchPeriod))
                query = query.Where(j => j.PeriodId == searchPeriod);
            if (!string.IsNullOrWhiteSpace(searchDocType))
                query = query.Where(j => j.SourceDocType == searchDocType);

            query = sortOrder == "date_asc"
                ? query.OrderBy(j => j.EntryDate)
                : query.OrderByDescending(j => j.EntryDate);

            return View(await PaginatedList<JournalEntry>.CreateAsync(
                query, pageNumber ?? 1, pageSize ?? 20));
        }

        /// <summary>查詢列「期間」下拉：本廠日記帳實際出現過的期間（新到舊）。</summary>
        private async Task<List<string>> GetPeriodOptionsAsync() =>
            await _context.JournalEntries
                .Where(j => j.PlantId == _userContext.PlantId)
                .Select(j => j.PeriodId)
                .Distinct()
                .OrderByDescending(p => p)
                .AsNoTracking()
                .ToListAsync();

        /// <summary>查詢列「來源類型」下拉：本廠日記帳實際出現過的來源類型。</summary>
        private async Task<List<string>> GetDocTypeOptionsAsync() =>
            await _context.JournalEntries
                .Where(j => j.PlantId == _userContext.PlantId)
                .Select(j => j.SourceDocType)
                .Distinct()
                .OrderBy(t => t)
                .AsNoTracking()
                .ToListAsync();

        // ── 手動傳票 ────────────────────────────────────────────────

        /// <summary>
        /// 手動傳票的空白表單。預設兩行——複式簿記最少就是一借一貸，
        /// 給一行等於逼使用者按一次「新增行」才能開始。
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> Create()
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();

            await PrepareCreateFormAsync();
            var vm = new ViewModels.ManualJournalViewModel
            {
                // 廠別當地日，不是伺服器時區（R22）——差一天就可能落到已關帳的期間。
                EntryDate = _dateTimeService.GetLocalDate(),
                Lines = { new(), new() },
            };
            if (IsAjax) return PartialView("_CreateForm", vm);
            return View("_CreateForm", vm);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(ViewModels.ManualJournalViewModel model)
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();

            // 借貸欄位留白＝0。畫面把 0 渲染成空字串（一行只該填一邊），而 Debit／Credit 是非可空
            // decimal，空字串在 model binding 就被記成「必填」錯誤——於是**任何**合法傳票都存不進去，
            // 下面「完全空白的行不算數」也永遠走不到。控制器單元測試直接呼叫 action、繞過 binding，
            // 所以一直是綠的；是 JournalEntryActionSmokeTests 第一次真的從畫面送出才發現（2026-09-24）。
            // 只清「送上來的是空字串」那一種：打成 "abc" 之類的照樣是錯。
            for (int i = 0; i < (model.Lines?.Count ?? 0); i++)
            {
                foreach (var key in new[] { $"Lines[{i}].Debit", $"Lines[{i}].Credit" })
                {
                    if (ModelState.TryGetValue(key, out var entry) && string.IsNullOrEmpty(entry.AttemptedValue))
                        ModelState.Remove(key);
                }
            }

            // 完全空白的行不算數：畫面預設給兩行，只用到一行時另一行會原樣送上來。
            var lines = (model.Lines ?? new())
                .Where(l => !string.IsNullOrWhiteSpace(l.AccountCode) || l.Debit != 0m || l.Credit != 0m)
                .ToList();
            model.Lines = lines;

            // 科目必須是**可手動選用**的：主檔含自動分錄專用的科目（存貨、暫估應付…），
            // 手動打一筆進去帳面仍平衡，但會與子帳對不起來，且要等稽核跑過才看得到。
            var selectable = await SelectableAccountsAsync();

            for (int i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                if (string.IsNullOrWhiteSpace(l.AccountCode))
                    ModelState.AddModelError($"Lines[{i}].AccountCode", _localizer["Journal_AccountRequired"].Value);
                else if (!selectable.ContainsKey(l.AccountCode))
                    ModelState.AddModelError($"Lines[{i}].AccountCode", _localizer["Journal_AccountNotSelectable"].Value);

                if (l.Debit < 0m || l.Credit < 0m)
                    ModelState.AddModelError($"Lines[{i}].Debit", _localizer["Journal_AmountNegative"].Value);
                // 一行只能有一邊。兩邊都填時借貸合計仍可能平衡，卻表達不出任何會計事件，
                // 而且事後從報表上完全看不出是誤填。
                if (l.Debit != 0m && l.Credit != 0m)
                    ModelState.AddModelError($"Lines[{i}].Debit", _localizer["Journal_BothSidesFilled"].Value);
                if (l.Debit == 0m && l.Credit == 0m)
                    ModelState.AddModelError($"Lines[{i}].Debit", _localizer["Journal_AmountRequired"].Value);
            }

            // 成本中心＝本廠的組織單位（2026-10-05 使用者裁決）。存組織單位 Id，畫面顯示名稱。
            var costCenters = await CostCenterNamesAsync();
            for (int i = 0; i < lines.Count; i++)
            {
                var cc = lines[i].CostCenter;
                if (string.IsNullOrWhiteSpace(cc)) { lines[i].CostCenter = null; continue; }
                if (!costCenters.ContainsKey(cc))
                    ModelState.AddModelError($"Lines[{i}].CostCenter", _localizer["Journal_CostCenterInvalid"].Value);
            }

            if (lines.Count < 2)
                ModelState.AddModelError(string.Empty, _localizer["Journal_TwoLinesRequired"].Value);
            else if (lines.Sum(l => l.Debit) != lines.Sum(l => l.Credit))
                // 服務層也會擋（那才是閘門）；這裡先擋是為了把錯誤顯示在表單上而不是丟例外。
                ModelState.AddModelError(string.Empty, _localizer["Journal_Unbalanced"].Value);

            if (!ModelState.IsValid)
            {
                await PrepareCreateFormAsync();
                return ValidationPartial("_CreateForm", model);
            }

            var requests = lines.Select(l =>
            {
                var amount = l.Debit != 0m ? l.Debit : l.Credit;
                return new Services.JournalLineRequest(
                    l.AccountCode, selectable[l.AccountCode],
                    l.Debit, l.Credit,
                    // 手動傳票一律以本位幣輸入：多幣別手動分錄需要匯率快照與重評，
                    // 那是另一件事（FxRevaluationService 管的），不該從這個畫面偷偷長出來。
                    CurrencyCode: null, Amount: amount, ExchangeRate: 1m, AmountLocal: amount,
                    CostCenter: l.CostCenter, Description: l.Description);
            }).ToList();

            try
            {
                var entry = await _journalService.CreateManualEntryAsync(
                    periodId:   PeriodIdOf(model.EntryDate),
                    entryDate:  model.EntryDate,
                    plantId:    _userContext.PlantId ?? "",
                    lines:      requests,
                    description: model.Description);

                if (IsAjax) return JsonOk(new { id = entry.JournalId }, _localizer["CreateSuccess"].Value);
                return RedirectToAction(nameof(Index), new { searched = true });
            }
            catch (InvalidOperationException ex)
            {
                // 期間已關帳／月結中，以及服務層的借貸平衡守門，都是這一條路徑。
                // 訊息本身已在地化（AccountingPeriodService／JournalEntryService 各自產生）。
                if (IsAjax) return JsonFail(ex.Message);
                ModelState.AddModelError(string.Empty, ex.Message);
                await PrepareCreateFormAsync();
                return View("_CreateForm", model);
            }
        }

        /// <summary>
        /// 期間代碼由分錄日期推算。服務層會再用日期查一次真正的期間，
        /// 兩者不一致時以日期為準——這裡給的是查無期間列時的退路。
        /// </summary>
        private static string PeriodIdOf(DateOnly date) => $"{date.Year:D4}{date.Month:D2}";

        /// <summary>可手動選用的科目：代碼 → 名稱。</summary>
        private async Task<Dictionary<string, string>> SelectableAccountsAsync()
            => await _context.Set<Modules.BAS.Models.ChartOfAccount>().AsNoTracking()
                .Where(a => a.IsActive && a.AllowManualEntry)
                .ToDictionaryAsync(a => a.AccountCode, a => a.AccountName);

        private async Task PrepareCreateFormAsync()
        {
            ViewBag.AccountOptions = await _context.Set<Modules.BAS.Models.ChartOfAccount>().AsNoTracking()
                .Where(a => a.IsActive && a.AllowManualEntry)
                .OrderBy(a => a.AccountCode)
                .Select(a => new { a.AccountCode, a.AccountName })
                .ToListAsync();
            // 「貼上多行」要分辨略過的原因：科目根本不存在，還是存在但沒開放手動選用。
            // 只給下拉選單那份清單時兩者都是「設不進去」，訊息只能籠統地兩個一起講，
            // 使用者看不出該去會計科目勾「可手動選用」還是代碼打錯了（2026-10-05 回報）。
            ViewBag.AccountManualMap = await _context.Set<Modules.BAS.Models.ChartOfAccount>().AsNoTracking()
                .Where(a => a.IsActive)
                .ToDictionaryAsync(a => a.AccountCode, a => a.AllowManualEntry);
            ViewBag.CostCenterOptions = await CostCenterOptionsAsync();
        }

        /// <summary>
        /// 本廠組織單位，依組織樹順序（Path）排，名稱前加縮排表示層級。
        ///
        /// <para>「本廠」看的是**有效廠別**：自己沒填 PlantId 就往上找最近一個有填的祖先，
        /// 與組織畫面、內部訂單、工作中心、報廢單同一套規則。2026-10-07 前只比對自己的 PlantId，
        /// 而 ProjectZ 庫的部門（業務部、總務部）都沒填、是掛在「總經理」(ADM) 底下繼承的
        /// → 下拉只剩「總經理」一筆。</para>
        /// </summary>
        private async Task<List<(string Id, string Label)>> CostCenterOptionsAsync()
        {
            var units = await PlantUnitsAsync();
            var ids = units.Select(u => u.Id).ToHashSet();
            var byId = units.ToDictionary(u => u.Id);

            // 層級＝在本廠範圍內往上數得到幾個祖先（跨出本廠的上層不算，最上層從 0 開始）。
            int Depth(OrganizationUnit u)
            {
                var d = 0;
                for (var p = u.ParentId; p != null && ids.Contains(p); p = byId[p].ParentId) d++;
                return d;
            }

            return units
                .OrderBy(u => u.Path ?? u.Id, StringComparer.Ordinal).ThenBy(u => u.SortOrder)
                .Select(u => (u.Id, new string('\u3000', Depth(u)) + u.Name))
                .ToList();
        }

        private async Task<Dictionary<string, string>> CostCenterNamesAsync()
            => (await PlantUnitsAsync()).ToDictionary(u => u.Id, u => u.Name);

        private async Task<List<OrganizationUnit>> PlantUnitsAsync()
        {
            var all = await _context.OrganizationUnits.IgnoreQueryFilters().AsNoTracking().ToListAsync();
            var byId = all.ToDictionary(u => u.Id);
            string? EffectivePlant(OrganizationUnit u)
            {
                for (var cur = u; cur != null; cur = cur.ParentId != null ? byId.GetValueOrDefault(cur.ParentId) : null)
                    if (!string.IsNullOrEmpty(cur.PlantId)) return cur.PlantId;
                return null;
            }
            var plantId = _userContext.PlantId;
            return all.Where(u => EffectivePlant(u) == plantId).ToList();
        }

        public async Task<IActionResult> Details(string id)
        {
            if (!await CanReadDocumentAsync(MenuKey, id)) return Forbid();
            var entry = await _context.JournalEntries
                .Include(j => j.Lines.OrderBy(l => l.LineNo))
                .FirstOrDefaultAsync(j => j.JournalId == id);
            if (entry == null) return NotFound();
            ViewBag.IsReadOnly = true;
            ViewBag.CostCenterNames = await CostCenterNamesAsync();
            var reversedBy = await _context.JournalEntries.AsNoTracking()
                .Where(e => e.SourceDocType == Services.JournalEntryService.ManualReversalSourceType && e.SourceDocId == id)
                .Select(e => e.JournalId).FirstOrDefaultAsync();
            ViewBag.ReversedBy  = reversedBy;
            ViewBag.CanReverse  = entry.Type == JournalEntryType.Manual
                               && entry.SourceDocType != Services.JournalEntryService.ManualReversalSourceType
                               && reversedBy == null
                               && await _permissionService.CanCreate(MenuKey);
            ViewBag.Today = _dateTimeService.GetLocalDate();
            if (IsAjax) return PartialView("_Form", entry);
            return View("_Form", entry);
        }

        /// <summary>
        /// 沖銷手動傳票（2026-10-05 使用者裁決：傳票建立即正式帳，不改不刪，更正一律沖銷）。
        /// 產生借貸對調的新傳票、以來源單號連回原傳票；沖銷日期由使用者指定（預設今天），期間鎖照常生效。
        /// </summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Reverse(string id, DateOnly entryDate)
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            try
            {
                var rev = await _journalService.ReverseManualEntryAsync(id, entryDate, _userContext.PlantId ?? "");
                return JsonOk(new { id = rev.JournalId }, string.Format(_localizer["Journal_ReversedFmt"].Value, id, rev.JournalId));
            }
            catch (InvalidOperationException ex)
            {
                return JsonFail(ex.Message);
            }
        }

        [HttpGet]
        public async Task<IActionResult> Export(
            string? searchPeriod, string? searchDocType, string format = "xlsx")
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();

            var query = _context.JournalEntries
                .Where(j => j.PlantId == _userContext.PlantId)
                .Include(j => j.Lines)
                .AsNoTracking();

            if (!string.IsNullOrWhiteSpace(searchPeriod))
                query = query.Where(j => j.PeriodId == searchPeriod);
            if (!string.IsNullOrWhiteSpace(searchDocType))
                query = query.Where(j => j.SourceDocType == searchDocType);

            var data = await query.OrderByDescending(j => j.EntryDate).ToListAsync();
            var ccNames = await CostCenterNamesAsync();
            var isZh = CultureInfo.CurrentUICulture.Name != "en-US";
            var now  = _dateTimeService.Now;

            var rows = data.SelectMany(j => j.Lines.Select(l => new object?[]
            {
                j.JournalId, j.PeriodId, j.EntryDate.ToString("yyyy-MM-dd"),
                j.SourceDocType, j.SourceDocId ?? "",
                l.LineNo, l.AccountCode, l.AccountName,
                l.Debit, l.Credit, l.AmountLocal,
                // 成本中心存組織單位 Id；舊資料是手填文字，查不到名稱就照原文
                l.CostCenter == null ? "" : ccNames.GetValueOrDefault(l.CostCenter, l.CostCenter)
            }));

            var req = new ExportRequest
            {
                Title = "JournalEntry",
                Headers = isZh
                    ? ["傳票號", "期間", "日期", "來源類型", "來源單號", "序", "科目代碼", "科目名稱", "借方", "貸方", "本位幣金額", "成本中心"]
                    : ["JournalId", "Period", "Date", "DocType", "DocId", "Line", "AcctCode", "AcctName", "Debit", "Credit", "AmtLocal", "CostCtr"],
                Rows = rows
            };

            return format switch
            {
                "csv"  => File(_exportService.ToCsv(req), "text/csv",
                               Uri.EscapeDataString($"Journal_{now:yyyyMMdd}.csv")),
                "json" => File(_exportService.ToJson(req), "application/json",
                               Uri.EscapeDataString($"Journal_{now:yyyyMMdd}.json")),
                _      => File(_exportService.ToExcel(req),
                               "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                               Uri.EscapeDataString($"Journal_{_userContext.PlantId}_{now:yyyyMMdd_HHmm}.xlsx"))
            };
        }

        // GET: /JournalEntry/Print/{id}（2026-10-27：傳票）
        [HttpGet]
        public async Task<IActionResult> Print(string id)
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
            var doc = await _context.JournalEntries.AsNoTracking().Include(j => j.Lines)
                .FirstOrDefaultAsync(j => j.JournalId == id);
            if (doc == null) return NotFound();
            await PreparePrintViewBagAsync(Url.Action("Details", null, new { id }, Request.Scheme)!);
            await PreparePrintSignAsync(id, null);
            var n = await PrintNames.ForAsync(doc);
            var f = await PrintFormatsAsync();
            var L = PrintL;
            var p = new PrintDocument
            {
                Title = L["Print_Title_JournalEntry"], DocNo = doc.JournalId,
                DateLabel = L["Print_EntryDate"], DateValue = doc.EntryDate.ToString("yyyy-MM-dd"),
            }
                .Field(L["Print_Period"], doc.PeriodId)
                .Field(L["Print_JournalType"], ActEnumDisplay.Text(L, doc.Type))
                .Field(L["Print_SourceDoc"], $"{ActEnumDisplay.SourceDocTypeText(L, doc.SourceDocType)} {doc.SourceDocId}".Trim())
                .Field(L["Print_Description"], doc.Description);
            var tbl = new PrintTable()
                .Col(L["Print_LineNo"]).Col(L["Print_AccountCode"]).Col(L["Print_AccountName"]).Col(L["Print_Description"])
                .Col(L["Print_Currency"]).Col(L["Print_Amount"], true)
                .Col(L["Print_Debit"], true).Col(L["Print_Credit"], true);
            foreach (var l in doc.Lines.OrderBy(x => x.LineNo))
            {
                var acctName = n.Account(l.AccountCode);
                tbl.Row(l.LineNo.ToString(), l.AccountCode, string.IsNullOrEmpty(acctName) ? l.AccountName : acctName,
                    l.Description, n.CurrencyWith(l.CurrencyCode),
                    string.IsNullOrEmpty(l.CurrencyCode) ? null : f.Money(l.Amount, l.CurrencyCode),
                    l.Debit != 0 ? f.Local(l.Debit) : null, l.Credit != 0 ? f.Local(l.Credit) : null);
            }
            p.Tables.Add(tbl);
            p.Total($"{L["Print_Debit"]}：{f.Local(doc.Lines.Sum(x => x.Debit))}　{L["Print_Credit"]}：{f.Local(doc.Lines.Sum(x => x.Credit))}");
            return PrintDocumentView(p);
        }
    }
}

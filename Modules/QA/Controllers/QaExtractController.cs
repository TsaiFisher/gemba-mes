using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.QA.Contracts;
using SmartFactory.MES.Modules.QA.Data;
using SmartFactory.MES.Modules.QA.ViewModels;
using SmartFactory.MES.Modules._Shared;

namespace SmartFactory.MES.Modules.QA.Controllers;

/// <summary>
/// 擷取物一覽（唯讀，規格 §10.11.11）。回答三個問題：
/// **現在生效的規則說明有哪些、它們是從哪一份規格擷取的、有沒有因為來源改了而失效。**
///
/// <para><b>這是 QA 模組唯一適合做成畫面的東西。</b>核可本身刻意不做成畫面
/// （定案 #45：`QaDbContext` 無稽核攔截器、索引隨 DB 出貨、核可粒度是段落＝code review 等級），
/// 而「哪些生效」這件事沒有畫面就只存在於 DB 裡——§10.11.3 說過期段落是**靜默**排除的，
/// 沒有這一頁的話，症狀只會是「助理突然不知道這條規則了」，而且查不出原因。</para>
///
/// <para><b>未核可的段落只列出存在，不顯示內文。</b>它們照樣在索引裡（§10.11.4 刻意保留，
/// 讓審核者看得到「產出來但我沒放行」），但內文尚未被任何人審過——
/// §10.11.5 把安全邊界整個前移到內容審核，畫面若把未審內文印出來，
/// 等於在核可閘門旁邊另開一扇門，而且是對全體有本畫面權限的人開的。
/// 要看內文請看產生器的審核頁或 manifest 本身，那兩者都在版控與本機，不在執行期。</para>
///
/// <para><b>不做廠別過濾</b>：<c>DocChunk</c> 不是 <c>IPlantEntity</c>——規則說明是全公司共用的
/// 規格文字，沒有廠別語意。</para>
/// </summary>
[Authorize]
public class QaExtractController : ModuleControllerBase
{
    private const string MenuKey = "QaExtract";
    private const int MaxTextPreview = 400;

    // GET: /QaExtract
    public async Task<IActionResult> Index(
        string? sourcePath, string? state, string? searchTerm,
        int? pageNumber, int? pageSize, string? sortOrder, bool searched = false)
    {
        if (!await Permissions.CanRead(MenuKey)) return Forbid();

        // **功能關閉時 QaDbContext 根本沒有註冊**（QAModule 在 Features:QaAssistant:Enabled=false
        // 時直接 return），所以這裡不能用建構式注入——那會讓關燈部署的直接 URL 變成
        // 「解析不到服務」的 500，而正確的答覆是「沒有這個東西」。
        // 選單在關燈時也不會出現，這一段只在有人手打網址時才走到。
        var qa = HttpContext.RequestServices.GetService<QaDbContext>();
        if (qa is null) return NotFound();

        ViewBag.SourcePath = sourcePath;
        ViewBag.State = state;
        ViewBag.SearchTerm = searchTerm;
        ViewBag.Searched = searched;
        ViewBag.CurrentPageSize = pageSize ?? 20;
        ViewBag.CurrentSort = sortOrder;
        ViewBag.HeadingSort = sortOrder == "heading_asc" ? "heading_desc" : "heading_asc";
        ViewBag.IndexedAtSort = sortOrder == "indexedat_asc" ? "indexedat_desc" : "indexedat_asc";

        await PrepareIndexPermissionsAsync(MenuKey);

        // 來源檔下拉：可列舉的欄位一律用 select（查詢列控件選用規範）。
        // 取自索引本身而不是掃 .claude/qa-extracts/ ——正式機沒有那個目錄（§10.11.6），
        // 掃目錄的話這個下拉在正式機會是空的，而畫面看起來完全正常。
        ViewBag.SourceOptions = await qa.DocChunks
            .AsNoTracking()
            .Where(c => c.Corpus == DocCorpus.Extract)
            .Select(c => c.SourcePath)
            .Distinct()
            .OrderBy(p => p)
            .ToListAsync();

        // 生效／未核可／過期三種狀態的總數：這一頁存在的理由就是讓「有東西被排除了」
        // 一眼看得到，而分頁後的清單本身回答不了這件事。
        var counts = await qa.DocChunks
            .AsNoTracking()
            .Where(c => c.Corpus == DocCorpus.Extract)
            .GroupBy(c => new { c.Approved, c.IsStale })
            .Select(g => new { g.Key.Approved, g.Key.IsStale, Count = g.Count() })
            .ToListAsync();

        ViewBag.EffectiveCount = counts.Where(c => c.Approved && !c.IsStale).Sum(c => c.Count);
        ViewBag.PendingCount = counts.Where(c => !c.Approved).Sum(c => c.Count);
        ViewBag.StaleCount = counts.Where(c => c.IsStale).Sum(c => c.Count);

        if (!searched) return View((PaginatedList<ExtractChunkVm>?)null);

        var query = qa.DocChunks
            .AsNoTracking()
            .Where(c => c.Corpus == DocCorpus.Extract);

        if (!string.IsNullOrWhiteSpace(sourcePath))
            query = query.Where(c => c.SourcePath == sourcePath);

        query = state switch
        {
            "effective" => query.Where(c => c.Approved && !c.IsStale),
            "pending" => query.Where(c => !c.Approved),
            "stale" => query.Where(c => c.IsStale),
            _ => query,
        };

        // 關鍵字比對標題與頁碼，**不比對內文**——比對內文會讓未核可段落的文字
        // 透過「查得到／查不到」被試探出來，而那正是上面刻意不顯示的東西。
        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = searchTerm.Trim();
            query = query.Where(c => EF.Functions.ILike(c.HeadingPath, $"%{term}%")
                                     || EF.Functions.ILike(c.DocUrl, $"%{term}%"));
        }

        query = sortOrder switch
        {
            "heading_asc" => query.OrderBy(c => c.HeadingPath),
            "heading_desc" => query.OrderByDescending(c => c.HeadingPath),
            "indexedat_asc" => query.OrderBy(c => c.IndexedAt),
            "indexedat_desc" => query.OrderByDescending(c => c.IndexedAt),
            _ => query.OrderBy(c => c.FilePath).ThenBy(c => c.ChunkIndex),
        };

        var projected = query.Select(c => new ExtractChunkVm
        {
            PageCode = c.DocUrl,
            SourcePath = c.SourcePath,
            HeadingPath = c.HeadingPath,
            Lang = c.Lang,
            Approved = c.Approved,
            IsStale = c.IsStale,
            IndexedAt = c.IndexedAt,
            // 未核可＝不帶內文（見類別註解）。截斷是為了列表可讀，全文請看手冊 Q&A 本身。
            Text = c.Approved ? c.Text : string.Empty,
        });

        var page = await PaginatedList<ExtractChunkVm>.CreateAsync(projected, pageNumber ?? 1, pageSize ?? 20);

        foreach (var row in page)
        {
            row.PageCode = ToPageCode(row.PageCode);
            if (row.Text.Length > MaxTextPreview) row.Text = row.Text[..MaxTextPreview] + "…";
        }

        return View(page);
    }

    /// <summary>
    /// <c>/docs/user/rules/currency-04.html</c> → <c>rules/currency-04.html</c>。
    /// 與 <c>QaPromptBuilder.PageCode</c> 同一個形狀——畫面上顯示的字串必須就是
    /// 答案裡會出現的那一個，否則使用者拿著引文對不回這一頁。
    /// </summary>
    private static string ToPageCode(string docUrl)
    {
        const string root = "/docs/user/";
        return docUrl.StartsWith(root, StringComparison.Ordinal) ? docUrl[root.Length..] : docUrl;
    }
}

using Microsoft.AspNetCore.Mvc;
using SmartFactory.MES.Modules.BAS.Services;

namespace SmartFactory.MES.Modules.BAS.ViewComponents
{
    /// <summary>
    /// 出廠單據上的「出廠箱號」（container-tracking.md §9.10）：這張單據出庫的批號對到的箱子。
    /// 單據本身不存箱號——箱號在倉庫作業／手機下架時掃進來就換成批號，這裡從帳反推回來顯示。沒有箱子就不輸出任何東西。
    /// 用法：<c>@await Component.InvokeAsync("ContainerUnitsLeft", new { docNo })</c>。
    /// </summary>
    public class ContainerUnitsLeftViewComponent : ViewComponent
    {
        private readonly IContainerUnitService _units;

        public ContainerUnitsLeftViewComponent(IContainerUnitService units) => _units = units;

        public async Task<IViewComponentResult> InvokeAsync(string? docNo, bool print = false)
        {
            var rows = await _units.LeftWithDocAsync(docNo ?? "");
            if (rows.Count == 0) return Content(string.Empty);
            ViewData["Print"] = print;
            return View("~/Modules/BAS/Views/ContainerUnit/_LeftWithDoc.cshtml", rows);
        }
    }
}

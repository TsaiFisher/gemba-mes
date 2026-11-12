using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.BAS.Controllers
{
    /// <summary>
    /// 計量單位 JSON API（供表單前端依單位動態取得小數位數、更新 input step 用）。
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("api/uom")]
    public class UomApiController : ControllerBase
    {
        private readonly IUomService _uomService;

        public UomApiController(IUomService uomService)
        {
            _uomService = uomService;
        }

        /// <summary>取得所有啟用單位的小數位數對照表。</summary>
        [HttpGet("decimals")]
        [PermissionGateExempt("純顯示格式設定（單位→小數位數），不含業務資料；" +
            "呼叫端是全站共用的 uom-step.js 與手機 api.js，鎖任何 MenuKey 都會讓沒有該畫面的角色數量欄位 step 失準")]
        public async Task<IActionResult> GetAllDecimals()
        {
            var map = await _uomService.GetAllDecimalPlacesAsync();
            return Ok(map);
        }

        /// <summary>
        /// 品號可用的單位（單據明細選了品號之後，單位下拉只列這些；見 uom-step.js restrictUomToProduct）。
        /// 不存在的品號回 404。
        /// </summary>
        [HttpGet("product/{productCode}")]
        [PermissionGateExempt("單位清單是品號主檔的換算性質（庫存／採購／銷售單位＋換算表），不含數量、金額或單據資料；" +
            "呼叫端是全站單據明細共用的 uom-step.js，鎖任何單一 MenuKey 都會讓其他單據的單位下拉失效")]
        public async Task<IActionResult> GetProductUoms(string productCode)
        {
            var opts = await _uomService.GetProductUomsAsync(productCode);
            return opts == null ? NotFound() : Ok(new
            {
                productCode,
                stockUom    = opts.StockUom,
                purchaseUom = opts.PurchaseUom,
                salesUom    = opts.SalesUom,
                uoms        = opts.Uoms,
            });
        }

        /// <summary>取得單一單位的小數位數（用於動態切換產品時重設 step）。</summary>
        [HttpGet("{code}/decimals")]
        [PermissionGateExempt("同上：純顯示格式設定，回傳內容不隨角色而異")]
        public async Task<IActionResult> GetDecimals(string code)
        {
            var dp = await _uomService.GetDecimalPlacesAsync(code);
            return Ok(new { uomCode = code, decimalPlaces = dp });
        }
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.BAS.Controllers
{
    /// <summary>
    /// 幣別 JSON API（供表單前端依幣別代號 + 單據日期動態取得匯率，回填 ExchangeRate）。
    /// 匯率查詢規則：CurrencyRate.RateDate &lt;= documentDate 中最近的一筆。
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("api/currency")]
    public class CurrencyApiController : ControllerBase
    {
        private readonly ICurrencyService _currencyService;
        private readonly IPermissionService _permissionService;

        // 匯率查詢的合法呼叫端（R24）：幣別維護畫面 ＋ 會在表單上回填 ExchangeRate 的 8 張單據。
        // 只鎖 CurrencyMaster 會讓「有訂單／應付款但沒有 BAS」的角色開單時匯率帶不出來，
        // 且前端只是欄位留白、不報錯（靜默壞掉）。
        private static readonly string[] RateReaderKeys =
        {
            "CurrencyMaster",
            "SalesOrder", "GoodsShipment", "SalesReturn",
            "PurchaseOrder", "GoodsReceipt", "GoodsReturn",
            "APPayment", "ARReceipt",
        };

        public CurrencyApiController(ICurrencyService currencyService, IPermissionService permissionService)
        {
            _currencyService = currencyService;
            _permissionService = permissionService;
        }

        /// <summary>取得所有啟用幣別的小數位數對照表。</summary>
        [HttpGet("decimals")]
        [PermissionGateExempt("純顯示格式設定（幣別→小數位數），不含匯率或業務資料；" +
            "為全站表單金額欄位的共用 metadata")]
        public async Task<IActionResult> GetAllDecimals()
        {
            var map = await _currencyService.GetAllDecimalPlacesAsync();
            return Ok(map);
        }

        /// <summary>
        /// 取得指定幣別在 documentDate 當日或之前最近一筆的匯率快照。
        /// rateType: Buy（採購預設）/ Sell（銷售預設）/ Mid（成本評價）。
        /// 找不到時回 200 + rate=null，由前端決定處理方式（提示使用者或保留 1）。
        /// </summary>
        [HttpGet("rate")]
        public async Task<IActionResult> GetRate(
            [FromQuery] string currencyCode,
            [FromQuery] DateOnly documentDate,
            [FromQuery(Name = "rateType")] RateType? rateTypeArg = null)
        {
            // 選填的列舉參數寫成可空：非可空實值型別會被補上 [Required]，沒帶時是 400 而不是預設值
            // （見 OptionalValueTypeParameterTests）。
            var rateType = rateTypeArg ?? RateType.Buy;
            if (!await _permissionService.CanReadAny(RateReaderKeys)) return Forbid();

            if (string.IsNullOrWhiteSpace(currencyCode))
                return BadRequest(new { error = "currencyCode is required" });

            var snapshot = await _currencyService.GetSnapshotAsync(currencyCode, documentDate, rateType);
            if (snapshot == null)
            {
                return Ok(new
                {
                    currencyCode,
                    documentDate,
                    rateType = rateType.ToString(),
                    rate = (decimal?)null,
                    rateDate = (DateOnly?)null,
                    found = false
                });
            }

            return Ok(new
            {
                currencyCode = snapshot.CurrencyCode,
                documentDate,
                rateType = snapshot.RateType.ToString(),
                rate = snapshot.Rate,
                rateDate = snapshot.RateDate,
                found = true
            });
        }
    }
}

using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.BAS.Services
{
    /// <summary>
    /// 畫面顯示用的小數位數。可直接在 View 裡 <c>@inject</c>，不必每支 controller 都補一次 ViewBag。
    ///
    /// 為什麼需要它：全庫原本有 244 處寫死的 <c>ToString("N2")</c>／<c>"N4"</c>。其中比率、天數、
    /// 工時那些本來就不該跟幣別或單位走，但金額與數量那 134 處是真的錯——0 位數的幣別被印成
    /// 1,234.0000，以「箱」為單位（小數 0 位）的品項顯示成 12.0000。
    /// 要修這些，每支畫面都得先拿到位數對照；改 44 個 controller 的建構式是一種做法，
    /// 但那會讓每支 controller 為了「畫面怎麼顯示」而多背兩個相依。這裡改成 View 自己注入。
    ///
    /// 底層兩個服務都有 10 分鐘快取，逐列呼叫不會打資料庫。
    /// </summary>
    public interface IDisplayPrecision
    {
        /// <summary>本位幣的金額格式（"N0"／"N2"…）。查不到本位幣時回 "N2"。</summary>
        Task<string> BaseMoneyFormatAsync();

        /// <summary>幣別 → 小數位數對照，配 <see cref="CurrencyStepHelper.GetFormat(IReadOnlyDictionary{string,int}?, string?)"/> 使用。</summary>
        Task<IReadOnlyDictionary<string, int>> CurrencyDecimalsAsync();

        /// <summary>單位 → 小數位數對照，配 <see cref="UomStepHelper.GetFormat(IReadOnlyDictionary{string,int}?, string?)"/> 使用。</summary>
        Task<IReadOnlyDictionary<string, int>> UomDecimalsAsync();
    }

    public sealed class DisplayPrecision : IDisplayPrecision
    {
        private readonly ICurrencyService _currency;
        private readonly IUomService      _uom;

        public DisplayPrecision(ICurrencyService currency, IUomService uom)
        {
            _currency = currency;
            _uom      = uom;
        }

        public async Task<string> BaseMoneyFormatAsync()
        {
            var baseCurrency = await _currency.GetBaseCurrencyAsync();
            if (string.IsNullOrEmpty(baseCurrency)) return CurrencyStepHelper.GetFormat(2);
            var map = await _currency.GetAllDecimalPlacesAsync();
            return CurrencyStepHelper.GetFormat(map.TryGetValue(baseCurrency, out var dp) ? dp : 2);
        }

        public Task<IReadOnlyDictionary<string, int>> CurrencyDecimalsAsync() =>
            _currency.GetAllDecimalPlacesAsync();

        public Task<IReadOnlyDictionary<string, int>> UomDecimalsAsync() =>
            _uom.GetAllDecimalPlacesAsync();
    }
}

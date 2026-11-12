using SmartFactory.MES.Modules.BAS.Services;

namespace SmartFactory.MES.Modules._Shared.Printing;

/// <summary>列印單的數字格式：數量依單位、金額依幣別的小數位數（與畫面一致）。</summary>
public sealed class PrintFormats
{
    private readonly IReadOnlyDictionary<string, int> _uom;
    private readonly IReadOnlyDictionary<string, int> _currency;
    private readonly string _baseMoney;

    private PrintFormats(IReadOnlyDictionary<string, int> uom, IReadOnlyDictionary<string, int> currency, string baseMoney)
    {
        _uom = uom; _currency = currency; _baseMoney = baseMoney;
    }

    public static async Task<PrintFormats> LoadAsync(IDisplayPrecision precision) =>
        new(await precision.UomDecimalsAsync(), await precision.CurrencyDecimalsAsync(), await precision.BaseMoneyFormatAsync());

    public string Qty(decimal value, string? uom) => value.ToString(UomStepHelper.GetFormat(_uom, uom));
    public string? Qty(decimal? value, string? uom) => value?.ToString(UomStepHelper.GetFormat(_uom, uom));
    public string Money(decimal value, string? currency) => value.ToString(CurrencyStepHelper.GetFormat(_currency, currency));
    /// <summary>本幣金額（AmountLocal 等）。</summary>
    public string Local(decimal value) => value.ToString(_baseMoney);
}

using SmartFactory.MES.Modules._Shared;
namespace SmartFactory.MES.Modules.BAS.Services
{
    /// <summary>
    /// 兩個不同單位之間查不到任何換算比例時拋出。
    ///
    /// <para><b>為什麼要有專屬型別而不是 <see cref="InvalidOperationException"/>：</b>
    /// 呼叫端（採購收退貨、訂單出退貨）已經有一批 catch 在處理併發與業務規則，
    /// 用通用型別會被那些 catch 一起吞掉，於是又變回靜默——正是本例外要解決的問題。</para>
    ///
    /// <para>訊息刻意帶齊三個鍵值：維護人員看到訊息就知道要去單位維護畫面補哪一列。</para>
    ///
    /// <para><b>呼叫端必須接住並把訊息回到畫面上</b>，否則正式環境是一頁 500（訊息丟失）、
    /// 開發環境是整片堆疊。單據 controller 的做法是把既有的業務規則 catch 擴成
    /// <c>catch (Exception ex) when (ex is InvalidOperationException or UomConversionNotFoundException)</c>；
    /// 新增會走到換算的存檔／過帳入口時照做。</para>
    /// </summary>
    public class UomConversionNotFoundException : Exception
    {
        public string ProductCode { get; }
        public string FromUom { get; }
        public string ToUom { get; }

        public UomConversionNotFoundException(string productCode, string fromUom, string toUom)
            : base(PzText.Get("Bas_UomConversionNotFoundFmt", fromUom, toUom,
                   string.IsNullOrWhiteSpace(productCode) ? "" : PzText.Get("Bas_UomProductSuffixFmt", productCode)))
        {
            ProductCode = productCode;
            FromUom = fromUom;
            ToUom = toUom;
        }
    }
}

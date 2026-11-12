using System.ComponentModel.DataAnnotations;

namespace SmartFactory.MES.Modules.RPS.Models;

/// <summary>
/// 排程報表的收件人（§7.0.1-b，定案 #15）。
///
/// <para><b>為什麼是子表而不是 <c>ReportDefinition</c> 上的一個字串欄位：</b>
/// §7.8 S3 要求**逐一檢查每個收件人**是否仍有該工具的權限，S5 要求下載時再檢查一次。
/// 存成逗號字串或 <c>jsonb</c> 的話，這兩項檢查都得先把字串在記憶體裡展開，
/// 「誰是收件人」也就無法用查詢表達——收件人清單一長，權限檢查會退化成 N 次記憶體比對。</para>
///
/// <para><b><see cref="Language"/> 放在這裡是刻意的</b>（承接定案 #16）：
/// 語系是**唯一**會改變檔案內容的收件人屬性，所以產檔時依它去重分組
/// （十個中文收件人只產一個檔，中英混合產兩個檔）。把它放進本表就不必再開一張表。
/// <c>null</c> 表示沿用 owner 的語系。</para>
///
/// <para>與其他 RPS 實體一致：**不實作 <c>IPlantEntity</c>／<c>IDataAuthEntity</c>**（§7.0.1-a）。</para>
/// </summary>
public class ReportRecipient
{
    public int Id { get; set; }

    public int DefinitionId { get; set; }

    /// <summary>
    /// **系統內部使用者 Id，不是 email 字串**（§7.8 S3）。
    /// 允許填任意 email 等於在 RBAC 外開一條出口，機密報表會寄給系統管不到的地址。
    /// </summary>
    [Required]
    [StringLength(450)]
    public string UserId { get; set; } = "";

    /// <summary><c>zh-TW</c>／<c>en-US</c>；<c>null</c> ＝ 沿用 owner 語系。</summary>
    [StringLength(10)]
    public string? Language { get; set; }

    public ReportDefinition? Definition { get; set; }
}

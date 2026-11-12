using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <summary>
    /// 補種 PUR 採購側四張單的預設單號設定（DocNumberConfig）。
    ///
    /// 乾淨部署一直缺 PurchaseRequest / PurchaseOrder / GoodsReceipt / GoodsReturn 的
    /// 單號規則——其餘單據家族（INV/ORD/QCR/MFG）都有種，唯獨這四張漏了。缺了會讓
    /// DocNumberService.NextAsync 丟「找不到單號設定」，存檔在應用層被驗證擋下，
    /// 使用者在全新環境**完全無法建立這四張單**。過去沒被發現，是因為長期使用的
    /// 開發庫早已手動在「BAS → 單號設定」補過，乾淨庫才會現形。
    ///
    /// 值編碼比照最近一次補種（20260721 BAS_DocNumberConfig_ScopeKey）：
    /// DateFormat=2(YYMM), ResetCycle=1(Yearly), Separator="-", SeqLength=5,
    /// PlantId=""＋ScopeKey=""（預設規則，各廠共用），IsSystemType=true。
    /// 前置碼避開已被佔用者（GR 已屬 SalesReturn）：PR/PO/RC/RT。
    /// </summary>
    /// <inheritdoc />
    public partial class PUR_DocNumberConfig_Seed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ON CONFLICT DO NOTHING：長期使用的開發庫可能已透過「BAS → 單號設定」
            // 手動補過同樣四筆規則（PK 為 (DocType, ScopeKey)），此處用一般 InsertData
            // 會在該類環境撞 PK_DocNumberConfigs；改用 raw SQL 讓乾淨部署照常插入、
            // 已預先存在的環境則略過既有設定，不覆蓋使用者手動調整過的內容。
            migrationBuilder.Sql(@"
                INSERT INTO ""DocNumberConfigs"" (""DocType"", ""ScopeKey"", ""DateFormat"", ""DocTypeName"", ""IsActive"", ""IsSystemType"", ""PlantId"", ""Prefix"", ""ResetCycle"", ""Separator"", ""SeqLength"")
                VALUES
                    ('PurchaseRequest', '', 2, '請購單',   TRUE, TRUE, '', 'PR', 1, '-', 5),
                    ('PurchaseOrder',   '', 2, '採購單',   TRUE, TRUE, '', 'PO', 1, '-', 5),
                    ('GoodsReceipt',    '', 2, '進貨單',   TRUE, TRUE, '', 'RC', 1, '-', 5),
                    ('GoodsReturn',     '', 2, '進貨退出', TRUE, TRUE, '', 'RT', 1, '-', 5)
                ON CONFLICT (""DocType"", ""ScopeKey"") DO NOTHING;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var docType in new[] { "PurchaseRequest", "PurchaseOrder", "GoodsReceipt", "GoodsReturn" })
            {
                migrationBuilder.DeleteData(
                    table: "DocNumberConfigs",
                    keyColumns: new[] { "DocType", "ScopeKey" },
                    keyValues: new object[] { docType, "" });
            }
        }
    }
}

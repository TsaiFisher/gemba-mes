using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <summary>
    /// 補種 JournalEntry 與 WorkOrder 的預設單號設定（DocNumberConfig）。
    ///
    /// 同 20260724 PUR_DocNumberConfig_Seed 的缺陷家族，這兩筆的後果更廣：
    ///
    /// • <b>JournalEntry</b> —— <c>JournalEntryService.CreateEntryAsync</c> 取號用。
    ///   缺了它，乾淨部署上**任何會產生總帳分錄的動作**都會失敗：庫存過帳與作廢回沖、
    ///   AP 認列與退貨沖銷、AR 開票、付款／收款核銷、生產完工…… 全都在寫分錄那一步
    ///   丟「找不到單號設定：DocType='JournalEntry'」。使用者看到的是「確認出貨後
    ///   應收發票沒開出來」這類離真因很遠的症狀。
    ///
    /// • <b>WorkOrder</b> —— <c>NcrController</c> 由不合格品單開重工工單時取號用。
    ///
    /// 過去沒被發現的原因與 PUR 那次相同：長期使用的開發庫早已在「BAS → 單號設定」
    /// 手動補過；而整合測試是各自 <c>db.DocNumberConfigs.Add(...)</c> 補上 JournalEntry
    /// 再跑的——等於每支測試各自繞過這個缺口，沒有任何一層會發現乾淨部署開不出分錄。
    /// 是 2026-07-31 新增的 P2P／O2C 端到端流程測試（跑在全新資料庫、不自帶單號設定）
    /// 第一次讓它現形。
    ///
    /// 值編碼比照 20260724 PUR_DocNumberConfig_Seed：
    /// DateFormat=2(YYMM), ResetCycle=1(Yearly), Separator="-", SeqLength=5,
    /// PlantId=""＋ScopeKey=""（預設規則，各廠共用），IsSystemType=true。
    /// 前置碼 JE／WO 皆未被既有 38 筆設定佔用。
    ///
    /// 共用預設規則（ScopeKey=""）在多廠別下是安全的：序號 scope 取自
    /// <c>ScopeOf(config)</c> 而非廠別，故各廠共用同一條序號，不會兩廠同時產出
    /// JE-2607-00001 而撞 JournalEntry 的 PK。
    /// </summary>
    /// <inheritdoc />
    public partial class ACT_DocNumberConfig_Seed_JournalEntry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ON CONFLICT DO NOTHING：長期使用的環境多半已手動補過同名規則
            // （PK 為 (DocType, ScopeKey)），一般 InsertData 會在那類環境撞 PK。
            // 用 raw SQL 讓乾淨部署照常插入、既有環境保留使用者調整過的內容。
            migrationBuilder.Sql(@"
                INSERT INTO ""DocNumberConfigs"" (""DocType"", ""ScopeKey"", ""DateFormat"", ""DocTypeName"", ""IsActive"", ""IsSystemType"", ""PlantId"", ""Prefix"", ""ResetCycle"", ""Separator"", ""SeqLength"")
                VALUES
                    ('JournalEntry', '', 2, '日記帳分錄', TRUE, TRUE, '', 'JE', 1, '-', 5),
                    ('WorkOrder',    '', 2, '工單',       TRUE, TRUE, '', 'WO', 1, '-', 5)
                ON CONFLICT (""DocType"", ""ScopeKey"") DO NOTHING;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var docType in new[] { "JournalEntry", "WorkOrder" })
            {
                migrationBuilder.DeleteData(
                    table: "DocNumberConfigs",
                    keyColumns: new[] { "DocType", "ScopeKey" },
                    keyValues: new object[] { docType, "" });
            }
        }
    }
}

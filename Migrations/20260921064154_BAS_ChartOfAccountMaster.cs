using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <summary>
    /// 發票科目主檔（InvoiceAccounts）升級為會計科目主檔（ChartOfAccounts）。
    ///
    /// <para><b>是改名不是重建。</b><c>dotnet ef migrations add</c> 產出的是
    /// DropTable ＋ CreateTable——那會把使用者建的費用／收入科目整批刪掉，
    /// 而手動 AP／AR 發票明細存的是科目**代碼字串**、沒有外鍵擋，
    /// 刪完之後發票還在、科目查不到，帳面完全看不出異常。此處已改寫為
    /// RenameTable ＋ AddColumn ＋ 資料轉換。</para>
    ///
    /// <para><b>AccountType 必須轉換</b>：舊列舉是 <c>Expense = 1, Revenue = 2</c>，
    /// 新列舉對齊科目代碼首碼（<c>Asset = 1</c>、<c>Liability = 2</c>…）。
    /// 不轉的話，原本的費用科目會在資產負債表上變成資產。</para>
    /// </summary>
    public partial class BAS_ChartOfAccountMaster : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameTable(name: "InvoiceAccounts", newName: "ChartOfAccounts");

            // RenameTable 不會改主鍵與索引的名稱（PostgreSQL 保留原名），
            // 留著的話下一次 scaffold 會看到模型與資料庫不一致而再生一支多餘的 migration。
            migrationBuilder.Sql(@"ALTER TABLE ""ChartOfAccounts"" RENAME CONSTRAINT ""PK_InvoiceAccounts"" TO ""PK_ChartOfAccounts"";");
            migrationBuilder.RenameIndex(
                name: "IX_InvoiceAccounts_AccountCode",
                newName: "IX_ChartOfAccounts_AccountCode",
                table: "ChartOfAccounts");

            migrationBuilder.AddColumn<string>(
                name: "AccountNameEn", table: "ChartOfAccounts",
                type: "character varying(100)", maxLength: 100, nullable: true);
            migrationBuilder.AddColumn<bool>(
                name: "IsSystem", table: "ChartOfAccounts",
                type: "boolean", nullable: false, defaultValue: false);
            migrationBuilder.AddColumn<bool>(
                name: "AllowManualEntry", table: "ChartOfAccounts",
                type: "boolean", nullable: false, defaultValue: false);

            // 既有列一律可手動選用：這張表在升級之前**就是**「手動發票可選的科目」，
            // 不補這一行的話，所有既有的費用／收入科目會立刻從發票下拉消失，
            // 而且既有發票重新過帳時科目解不出來（ResolveExpenseAccountsAsync 濾掉）。
            migrationBuilder.Sql(@"UPDATE ""ChartOfAccounts"" SET ""AllowManualEntry"" = TRUE;");

            // AccountType 轉換。先把舊值搬到新語意（1 費用 → 6 營業費用、2 收入 → 4 營業收入），
            // 來源 {1,2} 與目標 {6,4} 不重疊，故兩道 UPDATE 的先後無關。
            migrationBuilder.Sql(@"UPDATE ""ChartOfAccounts"" SET ""AccountType"" = 6 WHERE ""AccountType"" = 1;");
            migrationBuilder.Sql(@"UPDATE ""ChartOfAccounts"" SET ""AccountType"" = 4 WHERE ""AccountType"" = 2;");

            // 再以科目代碼首碼覆蓋——那正是升級前兩張財報的分類方式，
            // 所以對既有資料這一步讓報表輸出保持不變（恆等變換）。
            migrationBuilder.Sql(@"
                UPDATE ""ChartOfAccounts""
                   SET ""AccountType"" = CAST(LEFT(""AccountCode"", 1) AS integer)
                 WHERE LEFT(""AccountCode"", 1) IN ('1','2','3','4','5','6','7','8');");

            // 系統科目種子（StandardAccounts）。AllowManualEntry 一律 FALSE：
            // 這些科目的餘額由自動分錄維護，手動打一筆進去帳面仍平衡，但與子帳對不起來。
            //
            // 撞到既有同代碼的列時**只補系統旗標與性質，不動名稱也不動 AllowManualEntry**：
            // 使用者可能早就建了 5001 並在用，把它的手動選用權限拿掉會讓既有作業當場中斷。
            migrationBuilder.Sql(@"
                INSERT INTO ""ChartOfAccounts""
                    (""AccountCode"", ""AccountName"", ""AccountNameEn"", ""AccountType"",
                     ""IsSystem"", ""AllowManualEntry"", ""IsActive"", ""CreatedAt"", ""UpdatedAt"")
                SELECT v.code, v.name_zh, v.name_en, v.acct_type,
                       TRUE, FALSE, TRUE, NOW() AT TIME ZONE 'UTC', NOW() AT TIME ZONE 'UTC'
                  FROM (VALUES
                    ('1130', '銀行存款', 'Cash and bank', 1),
                    ('1155', '進項稅額', 'Input VAT', 1),
                    ('1170', '應收帳款', 'Accounts receivable', 1),
                    ('1401', '原料存貨', 'Raw material inventory', 1),
                    ('1450', '廠際調撥往來', 'Inter-plant clearing', 1),
                    ('2100', '應付帳款', 'Accounts payable', 2),
                    ('2152', '進貨暫估應付', 'Purchase GR/IR accrual', 2),
                    ('2153', '暫估應付運費', 'Accrued freight', 2),
                    ('2155', '銷項稅額', 'Output VAT', 2),
                    ('4100', '銷貨收入', 'Sales revenue', 4),
                    ('4190', '匯兌利益', 'FX gain', 4),
                    ('4210', '存貨盤盈', 'Inventory gain', 4),
                    ('5001', '銷貨成本', 'Cost of goods sold', 5),
                    ('5110', '直接人工-已分配', 'Direct labor absorbed', 5),
                    ('5120', '製造費用-已分配', 'Manufacturing overhead absorbed', 5),
                    ('5130', '製造成本差異', 'Manufacturing cost variance', 5),
                    ('5151', '進料費用差異', 'Landed cost variance', 5),
                    ('5199', '匯兌損失', 'FX loss', 5),
                    ('5210', '報廢損失', 'Scrap loss', 5),
                    ('5220', '存貨盤損', 'Inventory loss', 5)
                  ) AS v(code, name_zh, name_en, acct_type)
                ON CONFLICT (""AccountCode"") DO UPDATE
                   SET ""IsSystem""      = TRUE,
                       ""AccountType""   = EXCLUDED.""AccountType"",
                       ""AccountNameEn"" = COALESCE(""ChartOfAccounts"".""AccountNameEn"", EXCLUDED.""AccountNameEn""),
                       ""UpdatedAt""     = NOW() AT TIME ZONE 'UTC';");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // ⚠️ 這一步會刪掉系統科目。若某個標準代碼在升級前就由使用者建立過，
            // 它會被一起刪掉——ON CONFLICT 之後已無從分辨「本來就有」與「這次插入」。
            // 種子型 migration 的 Down 本質上是有損的，寫清楚比假裝可逆好。
            migrationBuilder.Sql(@"DELETE FROM ""ChartOfAccounts"" WHERE ""IsSystem"" = TRUE;");

            // 新列舉還原成舊的：6/5/8 三種費用類都只能回到「費用(1)」，
            // 4/7 回到「收入(2)」；1/2/3 在舊列舉裡沒有對應，一併當費用處理。
            migrationBuilder.Sql(@"UPDATE ""ChartOfAccounts"" SET ""AccountType"" = 2 WHERE ""AccountType"" IN (4, 7);");
            migrationBuilder.Sql(@"UPDATE ""ChartOfAccounts"" SET ""AccountType"" = 1 WHERE ""AccountType"" <> 2;");

            migrationBuilder.DropColumn(name: "AllowManualEntry", table: "ChartOfAccounts");
            migrationBuilder.DropColumn(name: "IsSystem", table: "ChartOfAccounts");
            migrationBuilder.DropColumn(name: "AccountNameEn", table: "ChartOfAccounts");

            migrationBuilder.RenameIndex(
                name: "IX_ChartOfAccounts_AccountCode",
                newName: "IX_InvoiceAccounts_AccountCode",
                table: "ChartOfAccounts");
            migrationBuilder.Sql(@"ALTER TABLE ""ChartOfAccounts"" RENAME CONSTRAINT ""PK_ChartOfAccounts"" TO ""PK_InvoiceAccounts"";");
            migrationBuilder.RenameTable(name: "ChartOfAccounts", newName: "InvoiceAccounts");
        }
    }
}

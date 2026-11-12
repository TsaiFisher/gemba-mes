using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Sdk;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// FlowTestBase 收尾驗帳的自我檢測：故意弄出帳錯，自動檢查必須判失敗。
    ///
    /// <para>全部流程測試都綠，可能是帳都對，也可能是檢查根本沒在跑——這兩種情況看起來一模一樣。
    /// 這裡各做一種最典型的錯（傳票借貸不平衡、庫存列與異動對不上），確認它真的會叫。
    /// 每支最後把自己弄出來的錯清掉，收尾的自動檢查才會通過。</para>
    /// </summary>
    [Collection("Integration")]
    public class LedgerInvariantSelfTests : FlowTestBase
    {
        protected override string Plant => "ZZLEDGER";

        public LedgerInvariantSelfTests(TestWebApplicationFactory factory) : base(factory)
            => ResetAsync(("FLOW-LDG-P", "驗帳自我檢測品")).GetAwaiter().GetResult();

        [Fact]
        public async Task 傳票借貸不平衡_收尾驗帳會判失敗()
        {
            const string id = "FLOW-LDG-JE1";
            await ExecAsync($"""
                INSERT INTO "JournalEntries" ("JournalId","PeriodId","EntryDate","SourceDocType","Type","CreatedAt","PlantId")
                VALUES ('{id}','209901',DATE '2099-01-15','SelfTest',0,now(),'{Plant}');
                INSERT INTO "JournalLines" ("JournalId","LineNo","AccountCode","AccountName","Debit","Credit","Amount","ExchangeRate","AmountLocal")
                VALUES ('{id}',1,'1101','現金',100,0,100,1,100), ('{id}',2,'4101','銷貨收入',0,90,90,1,90);
                """);

            var ex = await Assert.ThrowsAnyAsync<XunitException>(() => AssertLedgerInvariantsAsync());
            Assert.Contains("借貸不平衡", ex.Message);
            Assert.Contains(id, ex.Message);

            await ExecAsync($"""DELETE FROM "JournalLines" WHERE "JournalId"='{id}'; DELETE FROM "JournalEntries" WHERE "JournalId"='{id}';""");
        }

        [Fact]
        public async Task 庫存列與異動對不上_收尾驗帳會判失敗()
        {
            // 帳面在手 5，卻沒有任何庫存異動支撐它
            await ExecAsync($"""
                INSERT INTO "Inventories" ("PlantId","ProductCode","LocationCode","LotNo","QtyOnHand","QtyReserved","UnitCost","LastUpdatedAt")
                VALUES ('{Plant}','FLOW-LDG-P','{Loc}','',5,0,0,now());
                """);

            var ex = await Assert.ThrowsAnyAsync<XunitException>(() => AssertLedgerInvariantsAsync());
            Assert.Contains("INV_ROLLFORWARD", ex.Message);

            await ExecAsync($"""DELETE FROM "Inventories" WHERE "PlantId"='{Plant}' AND "ProductCode"='FLOW-LDG-P';""");
        }

        private async Task ExecAsync(string sql)
        {
            using var scope = NewScope(out var db);
            await db.Database.ExecuteSqlRawAsync(sql);
        }
    }
}

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Controllers;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 人工驗收作業書 §7.1 的 4-11：**兩人同時對同一張單過帳**，應有一方被擋。
    ///
    /// 這一格原本被記成「已由既有測試涵蓋」，但逐支看過之後不成立。
    /// 全庫七支並發測試（<c>ScrapDocConcurrencyTests</c>、<c>InvDocHeaderXminTests</c>、
    /// <c>CrossModuleConcurrencyTests</c>…）模擬並發的方式一律是**手寫欄位**：
    ///
    ///   sA.Status = DocStatus.Posted;  await dbA.SaveChangesAsync();
    ///   sB.Status = DocStatus.Posted;  await Assert.ThrowsAsync&lt;DbUpdateConcurrencyException&gt;(...);
    ///
    /// 那證明的是「xmin 這個機制會擋」，不是「過帳這條路真的擋得住」。中間隔著三件事：
    ///   ① 控制器有沒有把表頭**追蹤載入**（`AsNoTracking` 會讓 xmin 根本不進 UPDATE 的 WHERE）
    ///   ② `catch (DbUpdateConcurrencyException)` 有沒有落在真的會拋的那一段
    ///      （<c>reference-concurrency-catch-audit</c> 記過這個坑：try 內實體若沒有 xmin，
    ///       那個 catch 就是死碼）
    ///   ③ **被擋下的那一方有沒有留下殘留**——過帳是「寫異動 → 更新在手量 → 建傳票」，
    ///      失敗在最後一步而前面已落地的話，帳上會多出一批沒有單據的庫存異動。
    ///
    /// ③ 才是 4-11 真正要問的（§7.1 那句「重點不是有沒有跳錯誤訊息，而是擋下之後
    /// 資料庫乾不乾淨」）。這支從**控制器**進去，兩個 scope、兩個 DbContext，
    /// 各自持有同一張草稿單的快照，一前一後真的呼叫 <c>Confirm</c>。
    ///
    /// 寫下去才發現**擋住第二個人的其實有兩套機制，而它們的覆蓋面不一樣**：
    ///
    ///   ① <c>PostDocSimplifiedAsync</c> 開頭的冪等性守門——查 StockMovements 有沒有
    ///      同單同行同類別的異動，有就丟「禁止重複過帳」。這是**確定性**的，
    ///      而且不依賴任何並發原語。走這條路的是報廢／調整／借出／樣品…等單據。
    ///   ② 單據表頭的 <c>xmin</c>——收尾 UPDATE 帶 WHERE xmin，後到者 0 列即拋
    ///      <c>DbUpdateConcurrencyException</c>，由控制器 catch 成 400。
    ///
    /// **①只長在 `PostDocSimplifiedAsync` 一支上**（全庫只有那一處有這段檢查），
    /// 移倉與跨廠調撥走的是 <c>PostSamePlantTransferAsync</c>／<c>PostCrossPlantShipAsync</c>，
    /// 完全沒有①，只剩②。所以兩條路要分開測——只測報廢單的話，②那條
    /// 「catch 是不是死碼」的問題根本不會被碰到。
    /// </summary>
    [Collection("Integration")]
    public class ConcurrentPostingFlowTests : FlowTestBase
    {
        protected override string Plant => "ZZCONC";

        private const string Mat  = "FLOW-CONC-MAT";
        private const string Doc  = "FLOW-CONC-SD1";
        private const string TrDoc = "FLOW-CONC-TR1";

        private static readonly DateOnly StockDate = new(2098, 7, 1);
        private static readonly DateOnly ScrapDate = new(2098, 7, 10);

        private const decimal OpenQty = 100m, UnitCost = 25m, ScrapQty = 30m, MoveQty = 20m;

        private string Loc2 => $"{Plant}-LOC2";

        public ConcurrentPostingFlowTests(TestWebApplicationFactory factory) : base(factory)
            => ResetAllAsync().GetAwaiter().GetResult();

        private async Task ResetAllAsync()
        {
            await ResetAsync((Mat, "並發過帳測試料"));
            using var scope = NewScope(out var db);
            await db.ScrapDocs.IgnoreQueryFilters().Where(d => d.PlantId == Plant).ExecuteDeleteAsync();
            await db.StockTransfers.IgnoreQueryFilters().Where(d => d.PlantId == Plant).ExecuteDeleteAsync();

            // 移倉要有第二個儲位；基底只建一個。
            if (!await db.StorageLocations.IgnoreQueryFilters()
                    .AnyAsync(l => l.WhCode == Wh && l.LocationCode == Loc2))
                db.StorageLocations.Add(new StorageLocation
                {
                    WhCode = Wh, LocationCode = Loc2, LocationName = "並發測試目的儲位",
                });
            await db.SaveChangesAsync();
        }

        /// <summary>
        /// 兩個人同時按「確認」：一方成功、一方被擋，而且**被擋的那一方一筆殘留都不能留**。
        ///
        /// 失效形狀會長什麼樣：兩邊的狀態守門都看到 Draft（各自的快照都是過帳前的），
        /// 於是兩邊都走進過帳，庫存被扣兩次、庫存異動兩筆、傳票兩張，
        /// 而**兩邊的畫面都顯示成功**。數量會少 60 而不是 30，
        /// 但每一張傳票都平衡、庫存捲動也自洽（異動與在手量是一起被扣兩次的），
        /// 沒有任何一支既有稽核比得出來。
        /// </summary>
        [Fact]
        public async Task TwoUsersConfirmingTheSameScrapDoc_SecondIsBlockedByIdempotencyGuard()
        {
            await SeedOpeningStockAsync();
            await SeedScrapDocAsync();

            // 兩個使用者各自打開了這張單——關鍵是**兩邊都在任何人送出之前就載入了**，
            // 各自持有 Status = Draft 的快照。後面 Confirm 內部的查詢會因為 EF 的
            // 識別解析（identity resolution）回傳這個已追蹤的實體，而不是覆寫成資料庫的新值，
            // 這正是現場「兩個人各自開著同一張單」的樣子。
            using var scopeA = NewScope(out var dbA);
            using var scopeB = NewScope(out var dbB);
            var docA = await dbA.ScrapDocs.Include(d => d.Lines).SingleAsync(d => d.ScrapDocId == Doc);
            var docB = await dbB.ScrapDocs.Include(d => d.Lines).SingleAsync(d => d.ScrapDocId == Doc);
            Assert.Equal(DocStatus.Draft, docA.Status);
            Assert.Equal(DocStatus.Draft, docB.Status);

            var resultA = await NewController(scopeA, dbA).Confirm(Doc);
            var resultB = await NewController(scopeB, dbB).Confirm(Doc);

            // 先到者成功
            Assert.IsType<OkObjectResult>(resultA);

            // 後到者必須被擋——而且是**被擋住**，不是靜默成功
            var bad = Assert.IsType<BadRequestObjectResult>(resultB);
            var message = (string?)bad.Value?.GetType().GetProperty("message")?.GetValue(bad.Value);
            // 擋住它的是冪等性守門（機制①），不是 xmin：B 的快照仍是 Draft，
            // 狀態守門放行，真正攔下來的是「同單同行已有庫存異動」。
            PzAssert.Message("Inv_LineAlreadyPostedFmt", message);

            using (var scope = NewScope(out var db))
            {
                // ① 在手量只被扣一次
                Assert.Equal(OpenQty - ScrapQty, await QtyOnHandAsync(db, Plant, Mat));

                // ② 庫存異動只有一筆（一行明細）——不是兩筆
                var movements = await db.StockMovements.IgnoreQueryFilters().AsNoTracking()
                    .Where(m => m.DocNo == Doc).ToListAsync();
                Assert.Single(movements);
                Assert.Equal(ScrapQty, movements[0].StockQty);

                // ③ 傳票不得重複；且既有的每一張都要平衡
                var journals = await db.JournalEntries.IgnoreQueryFilters().AsNoTracking()
                    .Where(j => j.SourceDocId == Doc).ToListAsync();
                Assert.True(journals.Count <= 1, $"同一張單產生了 {journals.Count} 張傳票");
                await AssertEveryEntryBalancedAsync(db, Plant);

                // ④ 單據狀態只前進一次
                var doc = await db.ScrapDocs.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(d => d.ScrapDocId == Doc);
                Assert.Equal(DocStatus.Posted, doc.Status);

                // ⑤ 稽核層面也乾淨：沒有孤兒過帳、庫存捲動不變量成立
                var audit = Svc<IDataAuditService>(scope);
                // 只看這張單：期初入庫是流程測試自己用假單號直接過帳的
                // （FlowTestBase 的 Dispose 註解記著這件事），它本來就會被報成孤兒。
                Assert.DoesNotContain(await audit.AuditOrphanPostingAsync(Plant), f => f.DocId == Doc);
                Assert.Empty(await audit.AuditInventoryRollForwardAsync(Plant));
                Assert.Empty(await audit.AuditDuplicateJournalAsync(Plant));
            }
        }

        /// <summary>
        /// 移倉單同樣被兩個人同時確認——這條路**沒有冪等性守門**，只剩表頭 xmin。
        ///
        /// 這才是真正驗證 <c>catch (DbUpdateConcurrencyException)</c> 不是死碼的那一格。
        /// 全庫既有的並發測試都停在「兩個 context 手動改 Status 再 SaveChanges」，
        /// 證明的是 xmin 這個機制本身；從控制器進去才問得出
        /// 「表頭有沒有被追蹤載入」「catch 落在會拋的那一段沒有」這兩件事——
        /// 任一沒接上，第二個人就會**靜默成功**：貨被搬兩次、WP/WI 各四筆，
        /// 而兩邊畫面都顯示成功，來源儲位少 40 而不是 20。
        /// </summary>
        [Fact]
        public async Task TwoUsersConfirmingTheSameTransfer_SecondIsBlockedByXmin()
        {
            await SeedOpeningStockAsync();
            await SeedTransferDocAsync();

            using var scopeA = NewScope(out var dbA);
            using var scopeB = NewScope(out var dbB);
            await dbA.StockTransfers.Include(d => d.Lines).SingleAsync(d => d.StockTransferId == TrDoc);
            await dbB.StockTransfers.Include(d => d.Lines).SingleAsync(d => d.StockTransferId == TrDoc);

            var resultA = await NewTransferController(scopeA, dbA).Confirm(TrDoc);
            var resultB = await NewTransferController(scopeB, dbB).Confirm(TrDoc);

            Assert.IsType<OkObjectResult>(resultA);

            var bad = Assert.IsType<BadRequestObjectResult>(resultB);
            var message = (string?)bad.Value?.GetType().GetProperty("message")?.GetValue(bad.Value);

            using (var scope = NewScope(out var db))
            {
                // 訊息必須是並發衝突那一句——不是「已過帳」或別的守門順手擋到。
                // 這條路上沒有冪等性守門，擋下來的只可能是 xmin。
                var expected = Svc<IStringLocalizer<ProjectZSharedResource>>(scope)["ConcurrencyConflict"].Value;
                Assert.Equal(expected, message);

                // ① 貨只搬一次
                Assert.Equal(OpenQty - MoveQty, await QtyAtAsync(db, Loc));
                Assert.Equal(MoveQty, await QtyAtAsync(db, Loc2));

                // ② WP/WI 各一筆——不是各兩筆
                var movements = await db.StockMovements.IgnoreQueryFilters().AsNoTracking()
                    .Where(m => m.DocNo == TrDoc).ToListAsync();
                Assert.Equal(2, movements.Count);
                Assert.Single(movements, m => m.TypeCode == "WP");
                Assert.Single(movements, m => m.TypeCode == "WI");

                Assert.DoesNotContain(await Svc<IDataAuditService>(scope).AuditOrphanPostingAsync(Plant),
                    f => f.DocId == TrDoc);
                await AssertEveryEntryBalancedAsync(db, Plant);
            }
        }

        // ── 佈景 ────────────────────────────────────────────────────────────

        private async Task SeedOpeningStockAsync()
        {
            using var scope = NewScope(out var db);
            await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
            {
                PlantId = Plant, DocNo = "FLOW-CONC-OPEN", LineNo = 1,
                ProductCode = Mat, LocationCode = Loc, LotNo = "",
                Qty = OpenQty, TypeCode = "PI", TypeCategory = "Purchase",
                MovementDate = StockDate, UomCode = Uom, StockUomCode = Uom, UnitPrice = UnitCost,
                UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
                CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                SetDocPosted               = () => { },
            });
            await db.SaveChangesAsync();
        }

        private async Task SeedScrapDocAsync()
        {
            using var scope = NewScope(out var db);
            var doc = new ScrapDoc
            {
                ScrapDocId = Doc, ScrapDate = ScrapDate,
                TypeCode = "IJ", TypeCategory = "Scrap",
                Status = DocStatus.Draft, ApprovalStatus = DocumentApprovalStatus.Approved,
                CreatedBy = UserId, CreatedByRoleId = RoleId, PlantId = Plant,
            };
            doc.Lines.Add(new ScrapDocLine
            {
                ScrapDocId = Doc, LineNo = 1, ProductCode = Mat,
                Qty = ScrapQty, StockQty = ScrapQty,
                UomCode = Uom, StockUomCode = Uom, UnitPrice = UnitCost,
                WhCode = Wh, LocationCode = Loc,
            });
            db.ScrapDocs.Add(doc);
            await db.SaveChangesAsync();
        }

        private async Task<decimal> QtyAtAsync(ApplicationDbContext db, string loc)
            => await db.Inventories.IgnoreQueryFilters().AsNoTracking()
                .Where(i => i.PlantId == Plant && i.ProductCode == Mat && i.LocationCode == loc)
                .SumAsync(i => (decimal?)i.QtyOnHand) ?? 0m;

        private async Task SeedTransferDocAsync()
        {
            using var scope = NewScope(out var db);
            var doc = new StockTransfer
            {
                StockTransferId = TrDoc, TransferDate = ScrapDate,
                PlantId = Plant, FromWhCode = Wh, ToWhCode = Wh,
                Status = DocStatus.Draft, ApprovalStatus = DocumentApprovalStatus.Approved,
                CreatedBy = UserId, CreatedByRoleId = RoleId,
            };
            doc.Lines.Add(new StockTransferLine
            {
                StockTransferId = TrDoc, LineNo = 1, ProductCode = Mat,
                Qty = MoveQty, StockQty = MoveQty, UomCode = Uom, StockUomCode = Uom,
                FromWhCode = Wh, FromLocationCode = Loc,
                ToWhCode = Wh, ToLocationCode = Loc2,
            });
            db.StockTransfers.Add(doc);
            await db.SaveChangesAsync();
        }

        private StockTransferController NewTransferController(IServiceScope scope, ApplicationDbContext db)
        {
            var sp = new TestControllerServices(scope.ServiceProvider, db, Plant, RoleId);
            return new StockTransferController(
                db,
                sp.GetRequiredService<IUserContext>(),
                sp.GetRequiredService<IPermissionService>(),
                sp.GetRequiredService<IDateTimeService>(),
                sp.GetRequiredService<IApprovalService>(),
                sp.GetRequiredService<IDocNumberService>(),
                sp.GetRequiredService<IInventoryPostingService>(),
                sp.GetRequiredService<IUomService>(),
                sp.GetRequiredService<ICurrencyService>(),
                sp.GetRequiredService<IStringLocalizer<ProjectZSharedResource>>())
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext { RequestServices = sp },
                },
            };
        }

        private ScrapDocController NewController(IServiceScope scope, ApplicationDbContext db)
        {
            var sp = new TestControllerServices(scope.ServiceProvider, db, Plant, RoleId);
            return new ScrapDocController(
                db,
                sp.GetRequiredService<IUserContext>(),
                sp.GetRequiredService<IPermissionService>(),
                sp.GetRequiredService<IDateTimeService>(),
                sp.GetRequiredService<IApprovalService>(),
                sp.GetRequiredService<IDocNumberService>(),
                sp.GetRequiredService<IInventoryPostingService>(),
                sp.GetRequiredService<IUomService>(),
                sp.GetRequiredService<ICurrencyService>(),
                sp.GetRequiredService<IStringLocalizer<ProjectZSharedResource>>())
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext { RequestServices = sp },
                },
            };
        }
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 箱子出廠與回收（container-tracking.md §9.4 第 3 期）。
    ///
    /// 釘的是：
    ///   - 「在廠外」從帳推導：綁定批號出完、且最後一筆出庫是出貨等離廠異動；被領料、報廢出完的是空箱
    ///   - 廠外的箱子不能再裝（不在現場），回收後才變空箱
    ///   - 跟跨廠調撥到別廠的箱子，在目的廠回收即改屬目的廠，批號在那邊有貨就直接是使用中
    /// </summary>
    [Collection("Integration")]
    public class ContainerOffsiteFlowTests : FlowTestBase
    {
        protected override string Plant => "ZZBOXOFF";
        protected override IEnumerable<string> ExtraPlants => new[] { ToPlant };

        private const string ToPlant = "ZZBOXOFB";
        private const string Fg      = "FLOW-BOXOFF-FG";
        private const string BoxType = "FLOW-BOXOFF-T";
        private const string Prefix  = "ZZOFFB";

        private static readonly DateOnly StockDate = new(2097, 8, 1);

        private string ToWh  => $"{ToPlant}-WH";
        private string ToLoc => $"{ToPlant}-LOC";

        public ContainerOffsiteFlowTests(TestWebApplicationFactory factory) : base(factory)
            => ResetAllAsync().GetAwaiter().GetResult();

        private async Task ResetAllAsync()
        {
            await ResetAsync((Fg, "箱子出廠流程成品"));
            using var scope = NewScope(out var db);
            await db.CrossPlantTransfers.IgnoreQueryFilters().Where(d => d.PlantId == Plant).ExecuteDeleteAsync();
            await db.ContainerUnitHistories.IgnoreQueryFilters().Where(h => h.ContainerUnitId.StartsWith(Prefix)).ExecuteDeleteAsync();
            await db.ContainerUnits.IgnoreQueryFilters().Where(u => u.ContainerUnitId.StartsWith(Prefix)).ExecuteDeleteAsync();
            if (!await db.Containers.AnyAsync(c => c.ContainerCode == BoxType))
                db.Containers.Add(new Container { ContainerCode = BoxType, ContainerName = "出廠流程週轉箱", MaxCapacity = 20m, IsActive = true });
            if (!await db.Warehouses.IgnoreQueryFilters().AnyAsync(w => w.WhCode == ToWh))
            {
                db.Warehouses.Add(new Warehouse { WhCode = ToWh, WhName = "對面廠倉", PlantId = ToPlant });
                db.StorageLocations.Add(new StorageLocation { WhCode = ToWh, LocationCode = ToLoc, LocationName = "對面廠儲位" });
            }
            await db.SaveChangesAsync();
        }

        /// <summary>建一個箱子、入庫一個批號 20 並綁上（等同完工入庫裝箱）。</summary>
        private async Task<string> FilledBoxAsync(string lot)
        {
            using var scope = NewScope(out var db);
            var svc = Svc<IContainerUnitService>(scope);
            var id = (await svc.CreateBatchAsync(BoxType, Prefix, 1, UserId))[0];
            await PostAsync(scope, $"FLOW-BOXOFF-{lot}", lot, 20m, "PI", "Purchase");
            var u = await db.ContainerUnits.SingleAsync(x => x.ContainerUnitId == id);
            svc.Bind(u, Fg, lot, 20m, $"FLOW-BOXOFF-{lot}", UserId);
            await db.SaveChangesAsync();
            return id;
        }

        private async Task PostAsync(IServiceScope scope, string docNo, string lot, decimal qty, string type, string category)
        {
            await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
            {
                PlantId = Plant, DocNo = docNo, LineNo = 1,
                ProductCode = Fg, LocationCode = Loc, LotNo = lot,
                Qty = qty, TypeCode = type, TypeCategory = category,
                MovementDate = StockDate, UomCode = Uom, StockUomCode = Uom, UnitPrice = 10m,
                UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
                CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                SetDocPosted               = () => { },
            });
            await scope.ServiceProvider.GetRequiredService<SmartFactory.MES.Data.ApplicationDbContext>().SaveChangesAsync();
        }

        private async Task<(ContainerUnit Unit, ContainerUnitOffsite? Offsite)> BoxAsync(string id)
        {
            using var scope = NewScope(out var db);
            var u = await db.ContainerUnits.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.ContainerUnitId == id);
            return (u, await Svc<IContainerUnitService>(scope).OffsiteAsync(u));
        }

        private async Task<IReadOnlyList<ContainerUnitLeft>> LeftAsync(string docNo)
        {
            using var scope = NewScope(out var db);
            db.CurrentPlantId = Plant;
            return await Svc<IContainerUnitService>(scope).LeftWithDocAsync(docNo);
        }

        [Fact]
        public async Task 出貨出完_箱子在廠外不能再裝_回收後變空箱()
        {
            var box = await FilledBoxAsync("OFF-LOT-GS");
            using (var scope = NewScope(out _))
                await PostAsync(scope, "FLOW-BOXOFF-GS1", "OFF-LOT-GS", 20m, "GS", "Sales");

            var (_, off) = await BoxAsync(box);
            Assert.NotNull(off);
            Assert.Equal(("GS", "FLOW-BOXOFF-GS1"), (off!.TypeCode, off.DocNo));
            Assert.Equal(new[] { new ContainerUnitLeft(box, Fg, "OFF-LOT-GS", 20m, false) }, await LeftAsync("FLOW-BOXOFF-GS1"));
            Assert.Empty(await LeftAsync("FLOW-BOXOFF-OFF-LOT-GS"));      // 入庫單不是出廠單據

            using (var scope = NewScope(out _))
            {
                var svc = Svc<IContainerUnitService>(scope);
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.TakeEmptyForBindingAsync(new[] { box }, BoxType));
                Assert.Contains("FLOW-BOXOFF-GS1", ex.Message);
                Assert.Empty(await svc.EmptyUnitIdsAsync(BoxType, 5));
            }

            using (var scope = NewScope(out var db))
            {
                db.CurrentPlantId = Plant;
                var r = Assert.Single(await Svc<IContainerUnitService>(scope).RecoverAsync(new[] { box.ToLowerInvariant() }, UserId));
                Assert.Null(r.Error);
            }
            var (u, after) = await BoxAsync(box);
            Assert.Null(after);
            Assert.Null(u.LotNo);
            using (var scope = NewScope(out var db))
            {
                Assert.Equal(new[] { box }, await Svc<IContainerUnitService>(scope).EmptyUnitIdsAsync(BoxType, 5));
                var h = await db.ContainerUnitHistories.IgnoreQueryFilters().Where(x => x.ContainerUnitId == box)
                    .OrderByDescending(x => x.Id).FirstAsync();
                Assert.Equal((ContainerUnitActions.Recover, "FLOW-BOXOFF-GS1"), (h.Action, h.DocNo));
            }
            Assert.True(Assert.Single(await LeftAsync("FLOW-BOXOFF-GS1")).Recovered);    // 出貨單上仍查得到這箱，標成已回收
        }

        [Fact]
        public async Task 報廢出完是空箱不是廠外_不在廠外的箱子不能回收()
        {
            var box = await FilledBoxAsync("OFF-LOT-IJ");
            using (var scope = NewScope(out _))
                await PostAsync(scope, "FLOW-BOXOFF-IJ1", "OFF-LOT-IJ", 20m, "IJ", "Scrap");

            Assert.Null((await BoxAsync(box)).Offsite);
            using var scope2 = NewScope(out var db);
            db.CurrentPlantId = Plant;
            var svc = Svc<IContainerUnitService>(scope2);
            Assert.Equal(new[] { box }, await svc.EmptyUnitIdsAsync(BoxType, 5));
            var r = Assert.Single(await svc.RecoverAsync(new[] { box }, UserId));
            Assert.NotNull(r.Error);
        }

        [Fact]
        public async Task 跨廠調撥帶著箱子_目的廠回收_改屬目的廠且仍是使用中()
        {
            var box = await FilledBoxAsync("OFF-LOT-XP");
            using (var scope = NewScope(out var db))
            {
                var post = Svc<IInventoryPostingService>(scope);
                await post.PostCrossPlantShipAsync(new PostCrossPlantShipRequest
                {
                    PlantId = Plant, DocNo = "FLOW-BOXOFF-XP1", MovementDate = StockDate, CreatedBy = UserId,
                    Lines = new() { new CrossPlantShipLine
                    {
                        LineNo = 1, ProductCode = Fg, FromLocationCode = Loc, LotNo = "OFF-LOT-XP", StockQty = 20m,
                        UomCode = Uom, StockUomCode = Uom, SetQtyShipped = _ => { },
                    } },
                    SetDocInTransit = () => { },
                });
                await post.PostCrossPlantReceiveAsync(new PostCrossPlantReceiveRequest
                {
                    ToPlantId = ToPlant, DocNo = "FLOW-BOXOFF-XP1", MovementDate = StockDate, CreatedBy = UserId,
                    Lines = new() { new CrossPlantReceiveLine
                    {
                        LineNo = 1, ProductCode = Fg, ToLocationCode = ToLoc, LotNo = "OFF-LOT-XP", StockQty = 20m,
                        UomCode = Uom, StockUomCode = Uom, SetQtyReceived = _ => { },
                    } },
                    SetDocPosted = () => { },
                });
                await db.SaveChangesAsync();
            }

            var (_, off) = await BoxAsync(box);
            Assert.Equal(("XP", "FLOW-BOXOFF-XP1"), (off!.TypeCode, off.DocNo));

            using (var scope = NewScope(out var db))
            {
                db.CurrentPlantId = ToPlant;
                var r = Assert.Single(await Svc<IContainerUnitService>(scope).RecoverAsync(new[] { box }, UserId));
                Assert.Null(r.Error);
            }

            var (u, after) = await BoxAsync(box);
            Assert.Equal((ToPlant, "OFF-LOT-XP"), (u.PlantId, u.LotNo));
            Assert.Null(after);
            using (var scope = NewScope(out _))
                Assert.Equal(20m, await Svc<IContainerUnitService>(scope).BoundQtyAsync(u));   // 目的廠的那 20 個就在箱裡
        }
    }
}

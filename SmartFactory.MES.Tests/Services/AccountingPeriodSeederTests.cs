using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.Services
{
    /// <summary>
    /// 會計期間 seeder 的補齊行為。
    ///
    /// <para>原本的條件是「只在期間表完全為空時建立」，實務上等於永遠不執行：
    /// UITests 的前置資料先寫進一筆期間，seeder 就從此短路。ProjectZ 庫因此在
    /// 2026-06 之後兩個月都沒有可用期間。這組測試把「表裡已經有東西」設成主要情境，
    /// 而不是邊界情境。</para>
    /// </summary>
    public class AccountingPeriodSeederTests : IDisposable
    {
        private const string Plant = "P001";

        private readonly ApplicationDbContext _db;
        private readonly IServiceProvider _sp;

        public AccountingPeriodSeederTests()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;

            _db = new ApplicationDbContext(options);
            _db.Database.EnsureCreated();

            // 期間自 2026-08-30 起是「每廠一份」，seeder 依廠別主檔展開，
            // 所以測試必須先有廠別；本組除另有說明外都只用一個廠，期數才對得上原本的斷言。
            _db.Plants.Add(new SmartFactory.MES.Models.Plant { Id = Plant, Name = "測試廠" });
            _db.SaveChanges();
            _db.CurrentPlantId = Plant;

            var services = new ServiceCollection();
            services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
            services.AddSingleton(_db);
            _sp = services.BuildServiceProvider();
        }

        public void Dispose() => _db.Dispose();

        private static string CurrentPeriodId()
        {
            var now = DateTime.UtcNow;
            return $"{now.Year:D4}{now.Month:D2}";
        }

        private AccountingPeriod Add(string periodId, PeriodStatus status = PeriodStatus.Open,
            string? plantId = null)
        {
            var year = int.Parse(periodId[..4]);
            var month = int.Parse(periodId[4..]);
            var start = new DateOnly(year, month, 1);
            var p = new AccountingPeriod
            {
                PeriodId = periodId,
                PlantId = plantId ?? Plant,
                Year = year,
                Month = month,
                StartDate = start,
                EndDate = start.AddMonths(1).AddDays(-1),
                Status = status,
            };
            _db.AccountingPeriods.Add(p);
            _db.SaveChanges();
            return p;
        }

        [Fact(DisplayName = "空表：建立當月起算 12 期")]
        public async Task Seeds_Twelve_Periods_When_Empty()
        {
            await AccountingPeriodSeeder.SeedAsync(_sp);

            Assert.Equal(12, await _db.AccountingPeriods.CountAsync());
            Assert.Contains(CurrentPeriodId(), await _db.AccountingPeriods.Select(p => p.PeriodId).ToListAsync());
        }

        [Fact(DisplayName = "表中已有一筆過去期間，仍補齊當月起算 12 期")]
        public async Task Seeds_Even_When_A_Stale_Period_Already_Exists()
        {
            // 這正是 ProjectZ 庫的實況：測試前置寫進一筆 202606 之後，
            // 舊版 seeder 因為 AnyAsync() 為真而直接 return，當期永遠不會出現。
            Add("202606");

            await AccountingPeriodSeeder.SeedAsync(_sp);

            var ids = await _db.AccountingPeriods.Select(p => p.PeriodId).ToListAsync();
            Assert.Contains(CurrentPeriodId(), ids);
            Assert.Equal(13, ids.Count); // 12 期 + 原本那筆 202606
        }

        [Fact(DisplayName = "重複執行不新增（冪等）")]
        public async Task Is_Idempotent()
        {
            await AccountingPeriodSeeder.SeedAsync(_sp);
            var afterFirst = await _db.AccountingPeriods.CountAsync();

            await AccountingPeriodSeeder.SeedAsync(_sp);

            Assert.Equal(afterFirst, await _db.AccountingPeriods.CountAsync());
        }

        [Fact(DisplayName = "不修改既有期間的狀態（已關帳的當期不會被重新打開）")]
        public async Task Does_Not_Reopen_Existing_Closed_Period()
        {
            Add(CurrentPeriodId(), PeriodStatus.Closed);

            await AccountingPeriodSeeder.SeedAsync(_sp);

            var current = await _db.AccountingPeriods.SingleAsync(p => p.PeriodId == CurrentPeriodId());
            Assert.Equal(PeriodStatus.Closed, current.Status);
        }

        [Fact(DisplayName = "不回填當月之前的缺口")]
        public async Task Does_Not_Backfill_Past_Periods()
        {
            await AccountingPeriodSeeder.SeedAsync(_sp);

            var earliest = await _db.AccountingPeriods.OrderBy(p => p.PeriodId).FirstAsync();
            Assert.Equal(CurrentPeriodId(), earliest.PeriodId);
        }

        [Fact(DisplayName = "每個廠各補一份，某廠已有的期間不影響其他廠")]
        public async Task Seeds_Per_Plant_Independently()
        {
            _db.Plants.Add(new SmartFactory.MES.Models.Plant { Id = "P002", Name = "測試廠二" });
            _db.SaveChanges();
            // P001 已經有當期，P002 什麼都沒有——缺口是「期 × 廠」的組合，不是只看期別。
            Add(CurrentPeriodId());

            await AccountingPeriodSeeder.SeedAsync(_sp);

            var all = await _db.AccountingPeriods.IgnoreQueryFilters().ToListAsync();
            Assert.Equal(12, all.Count(p => p.PlantId == Plant));
            Assert.Equal(12, all.Count(p => p.PlantId == "P002"));
        }

        [Fact(DisplayName = "起訖日涵蓋整個月份")]
        public async Task Period_Dates_Cover_Whole_Month()
        {
            await AccountingPeriodSeeder.SeedAsync(_sp);

            foreach (var p in await _db.AccountingPeriods.ToListAsync())
            {
                Assert.Equal(new DateOnly(p.Year, p.Month, 1), p.StartDate);
                Assert.Equal(p.StartDate.AddMonths(1).AddDays(-1), p.EndDate);
            }
        }
    }
}

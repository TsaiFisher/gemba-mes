using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Controllers;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests
{
    /// <summary>
    /// 稽核紀錄的鍵值、廠別與日期篩選（2026-10-21 以真實資料檢查發現的缺陷）。
    /// 資料庫產生主鍵的暫時負數只有真的 PostgreSQL 才會出現，見整合測試 AuditLogIntegrationTests。
    /// </summary>
    public class AuditInterceptorTests
    {
        private static ApplicationDbContext NewContext(string? currentPlantId = null)
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .AddInterceptors(new AuditInterceptor(new HttpContextAccessor()))
                .Options;
            return new ApplicationDbContext(options) { CurrentPlantId = currentPlantId };
        }

        private static List<AuditLog> Logs(ApplicationDbContext db, string entityType) =>
            db.AuditLogs.IgnoreQueryFilters().Where(l => l.EntityType == entityType).ToList();

        [Fact]
        public async Task 複合主鍵_EntityId記下所有主鍵欄_以直線串接()
        {
            using var db = NewContext("P9");
            db.RolePlantAssignments.Add(new RolePlantAssignment { RoleId = "R1", PlantId = "P1" });
            db.RolePlantAssignments.Add(new RolePlantAssignment { RoleId = "R1", PlantId = "P2" });
            await db.SaveChangesAsync();

            var ids = Logs(db, nameof(RolePlantAssignment)).Select(l => l.EntityId).OrderBy(x => x).ToList();
            Assert.Equal(new[] { "R1|P1", "R1|P2" }, ids);   // 原本兩筆都是 "R1"，分不出是哪一列
        }

        [Fact]
        public async Task 實體沒有廠別_記操作者目前所在的廠()
        {
            using var db = NewContext("P9");
            db.CodeEntries.Add(new CodeEntry { Category = "T", Code = "A", NameZh = "甲" });
            await db.SaveChangesAsync();

            Assert.Equal("P9", Assert.Single(Logs(db, nameof(CodeEntry))).PlantId);
        }

        [Fact]
        public async Task 實體有自己的廠別_以實體的為準()
        {
            using var db = NewContext("P9");
            db.RolePlantAssignments.Add(new RolePlantAssignment { RoleId = "R1", PlantId = "P1" });
            await db.SaveChangesAsync();

            Assert.Equal("P1", Assert.Single(Logs(db, nameof(RolePlantAssignment))).PlantId);
        }

        [Fact]
        public async Task 沒有登入情境也沒有目前廠別_廠別維持空白()
        {
            using var db = NewContext(currentPlantId: null);
            db.CodeEntries.Add(new CodeEntry { Category = "T", Code = "B", NameZh = "乙" });
            await db.SaveChangesAsync();

            Assert.Null(Assert.Single(Logs(db, nameof(CodeEntry))).PlantId);
        }

        // ── 稽核畫面的日期篩選：起訖日是使用者當地日期 ─────────────────────────────

        [Fact]
        public async Task 日期篩選以使用者時區的一天為界_台灣清晨的異動篩得到_隔天清晨的不混入()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
            using var db = new ApplicationDbContext(options);
            db.AuditLogs.AddRange(
                new AuditLog { Action = "Added", EntityType = "T", EntityId = "清晨", Timestamp = new DateTime(2026, 10, 20, 23, 30, 0, DateTimeKind.Utc) },  // 當地 10/21 07:30
                new AuditLog { Action = "Added", EntityType = "T", EntityId = "上午", Timestamp = new DateTime(2026, 10, 21, 1, 0, 0, DateTimeKind.Utc) },    // 當地 10/21 09:00
                new AuditLog { Action = "Added", EntityType = "T", EntityId = "隔天", Timestamp = new DateTime(2026, 10, 21, 16, 30, 0, DateTimeKind.Utc) },  // 當地 10/22 00:30
                new AuditLog { Action = "Added", EntityType = "T", EntityId = "前天", Timestamp = new DateTime(2026, 10, 20, 15, 59, 0, DateTimeKind.Utc) }); // 當地 10/20 23:59
            await db.SaveChangesAsync();

            var user = new Mock<IUserContext>();
            user.Setup(u => u.TimeZoneId).Returns("Asia/Taipei");
            var controller = new AuditLogController(db, user.Object, new Mock<IExportService>().Object, new DateTimeService(user.Object));

            var day = new DateTime(2026, 10, 21);
            var result = await controller.Index(null, null, null, day, day, 1, 50, null, searched: true);

            var model = Assert.IsAssignableFrom<PaginatedList<AuditLog>>(((Microsoft.AspNetCore.Mvc.ViewResult)result).Model);
            Assert.Equal(new[] { "上午", "清晨" }, model.Select(l => l.EntityId).OrderBy(x => x).ToArray());
        }
    }
}

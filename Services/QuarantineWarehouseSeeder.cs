using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.BAS.Models;

namespace SmartFactory.MES.Services
{
    /// <summary>
    /// 待驗倉 seed（收貨品質隔離：收貨一律先落待驗倉，IQC Pass 才放行至一般可用倉）。
    ///
    /// 冪等：僅為「尚無任何 WarehouseType.Quarantine 倉庫」的廠別各建一座；已有待驗倉的廠別
    /// （含使用者手動建立的）不動，避免覆蓋既有設定。啟動期 CurrentPlantId 為 null，
    /// Warehouse 受廠別全域過濾器套用，故存在性檢查與建立皆用 IgnoreQueryFilters
    /// （見 RolePermissionSeeder.EnsureDataScopeAsync 同一模式）。
    /// 預設所有環境執行，可用組態 "Security:SeedQuarantineWarehouse": false 關閉。
    /// </summary>
    public static class QuarantineWarehouseSeeder
    {
        public static async Task SeedAsync(IServiceProvider sp)
        {
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("QuarantineWarehouseSeeder");
            var db = sp.GetRequiredService<ApplicationDbContext>();

            var plantIds = await db.Plants.Select(p => p.Id).ToListAsync();
            var plantsWithQuarantine = (await db.Warehouses.IgnoreQueryFilters()
                    .Where(w => w.WarehouseType == WarehouseType.Quarantine)
                    .Select(w => w.PlantId)
                    .ToListAsync())
                .ToHashSet();

            foreach (var plantId in plantIds)
            {
                if (plantsWithQuarantine.Contains(plantId)) continue;

                var whCode = $"QC-{plantId}";
                if (whCode.Length > 20) whCode = whCode[..20];

                db.Warehouses.Add(new Warehouse
                {
                    WhCode = whCode,
                    WhName = "待驗倉",
                    WarehouseType = WarehouseType.Quarantine,
                    IsMrpWarehouse = false,   // 待驗品未放行前不可視為 MRP 可用庫存
                    IsActive = true,
                    PlantId = plantId,
                });
                // LocationCode 為全域唯一索引（非 WhCode+LocationCode 複合唯一），
                // 多廠別同批 seed 時不可用固定值 "Q01"，否則撞 IX_StorageLocations_LocationCode。
                var locationCode = $"{whCode}-Q01";
                if (locationCode.Length > 20) locationCode = locationCode[..20];

                db.StorageLocations.Add(new StorageLocation
                {
                    WhCode = whCode,
                    LocationCode = locationCode,
                    LocationName = "待驗區",
                    IsActive = true,
                });
                logger.LogInformation("廠別 {PlantId} 缺待驗倉，已建立 {WhCode}", plantId, whCode);
            }

            await db.SaveChangesAsync();
        }
    }
}

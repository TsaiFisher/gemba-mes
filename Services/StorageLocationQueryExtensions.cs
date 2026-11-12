using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.BAS.Models;

namespace SmartFactory.MES.Services
{
    /// <summary>
    /// 儲位查詢的廠別收斂。
    ///
    /// <para><b>為什麼需要</b>：<see cref="StorageLocation"/> 的主鍵是
    /// <c>(WhCode, LocationCode)</c>，本身**沒有 PlantId 欄位、也沒有實作 IPlantEntity</c>，
    /// 因此 <see cref="ApplicationDbContext"/> 那組「<c>PlantId == CurrentPlantId</c>」的全域查詢
    /// 過濾器對它完全不生效——直接 <c>_db.StorageLocations</c> 查出來的是**全公司所有廠**的儲位。
    /// 廠別只能透過所屬倉庫間接推導（<see cref="Warehouse"/> 是 <c>IPlantEntity</c>，有被過濾）。</para>
    ///
    /// <para>這個缺口在人工驗收 W4-F11 由跨廠調撥的來源儲位下拉先踩到（撥出端的倉庫選單會出現
    /// 別廠的倉），事後全庫掃描發現同一個寫法散在 15 處：庫存異動／調整／報廢／移轉／備料／盤點
    /// 的儲位下拉全都跨廠，行動端 <c>/api/warehouse/location/{code}</c> 掃碼驗證與揀貨過帳
    /// 更是直接放行別廠儲位。故收斂成單一語意點，並由慣例規則 R30 守住。</para>
    ///
    /// <para><b>刻意不涵蓋的情形</b>：跨廠調撥的目的端、主檔刪除守門、
    /// <see cref="Modules.BAS.Services.IPlantDefaultLocationResolver"/> 這類「本來就要看別廠」的
    /// 查詢，一律明寫 <c>IgnoreQueryFilters()</c> 表態，不套用本擴充。</para>
    /// </summary>
    public static class StorageLocationQueryExtensions
    {
        /// <summary>
        /// 只留下屬於**目前廠別**的儲位。
        /// 以 <c>db.Warehouses</c> 子查詢做存在性判定；該 DbSet 本身帶全域廠別過濾器，
        /// 故廠別條件由過濾器提供，呼叫端不必（也不該）自己傳 plantId。
        /// </summary>
        public static IQueryable<StorageLocation> ForCurrentPlant(
            this IQueryable<StorageLocation> query, ApplicationDbContext db)
            => query.Where(l => db.Warehouses.Any(w => w.WhCode == l.WhCode));
    }
}

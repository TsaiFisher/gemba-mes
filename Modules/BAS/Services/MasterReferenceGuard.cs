using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;

namespace SmartFactory.MES.Modules.BAS.Services
{
    /// <summary>外來參照代碼的檢核結果。</summary>
    public enum MasterRefStatus
    {
        /// <summary>合法（含「未填」——是否可空由欄位自己的 [Required] 決定，不歸本檢核管）。</summary>
        Ok,

        /// <summary>代碼不存在。</summary>
        NotFound,

        /// <summary>代碼存在，但屬於其他廠別。</summary>
        OtherPlant,

        /// <summary>代碼存在且屬於本廠，但已停用。</summary>
        Inactive,
    }

    /// <summary>
    /// 主檔外來參照的伺服端檢核。
    ///
    /// 畫面上這些欄位一律是 <c>&lt;select&gt;</c>，選項早已被廠別與 <c>IsActive</c> 過濾，
    /// 所以「非本廠代碼」「已停用代碼」這兩類無效輸入在 UI 上根本選不到——
    /// 也因此長期沒有人補測試，更沒有人補檢核。但下拉只是輸入輔助：
    /// 直接 POST 一組表單欄位就能繞過它，而 Save 端從頭到尾沒有攔過。
    ///
    /// 後果不是存檔失敗，是**存進去了**：產品類別的預設倉指到別廠的倉，
    /// 之後每一張用該類別開的單都會把料收到查不到的地方；工作中心掛到別廠的班別群組，
    /// 排程算產能時取到另一個廠的行事曆。兩者都不會報錯，只會算錯。
    ///
    /// 廠別主檔一律以 <c>IgnoreQueryFilters</c> 查詢：全域過濾器會讓他廠資料直接消失，
    /// 於是「屬於別廠」與「根本不存在」在錯誤訊息上分不開，使用者只會看到
    /// 「代碼不存在」而畫面上明明選得到——查起來會非常久。
    /// </summary>
    public static class MasterReferenceGuard
    {
        /// <summary>倉庫（廠別主檔）。</summary>
        public static Task<MasterRefStatus> CheckWarehouseAsync(
            ApplicationDbContext db, string? code, string? plantId, CancellationToken ct = default)
            => CheckPlantScopedAsync(
                db.Warehouses.IgnoreQueryFilters()
                    .Where(w => w.WhCode == code)
                    .Select(w => new Probe(w.PlantId, w.IsActive)),
                code, plantId, ct);

        /// <summary>
        /// 儲位（鍵為 倉庫＋儲位）。
        /// 儲位自己沒有 PlantId，廠別是跟著所屬倉庫走的，故廠別判定取倉庫那一列。
        /// </summary>
        public static Task<MasterRefStatus> CheckLocationAsync(
            ApplicationDbContext db, string? whCode, string? code, string? plantId, CancellationToken ct = default)
            => CheckPlantScopedAsync(
                db.StorageLocations.IgnoreQueryFilters()
                    .Where(l => l.LocationCode == code && (whCode == null || l.WhCode == whCode))
                    .Select(l => new Probe(
                        l.Warehouse != null ? l.Warehouse.PlantId : null,
                        l.IsActive && (l.Warehouse == null || l.Warehouse.IsActive))),
                code, plantId, ct);

        /// <summary>班別群組（廠別主檔）。</summary>
        public static Task<MasterRefStatus> CheckShiftGroupAsync(
            ApplicationDbContext db, string? code, string? plantId, CancellationToken ct = default)
            => CheckPlantScopedAsync(
                db.ShiftGroups.IgnoreQueryFilters()
                    .Where(g => g.GroupCode == code)
                    .Select(g => new Probe(g.PlantId, g.IsActive)),
                code, plantId, ct);

        /// <summary>計量單位（全域主檔，無廠別）。</summary>
        public static Task<MasterRefStatus> CheckUomAsync(
            ApplicationDbContext db, string? code, CancellationToken ct = default)
            => CheckGlobalAsync(
                db.Uoms.IgnoreQueryFilters()
                    .Where(u => u.UomCode == code)
                    .Select(u => u.IsActive),
                code, ct);

        /// <summary>產品類別（全域主檔，無廠別）。</summary>
        public static Task<MasterRefStatus> CheckProductCategoryAsync(
            ApplicationDbContext db, string? code, CancellationToken ct = default)
            => CheckGlobalAsync(
                db.ProductCategories.IgnoreQueryFilters()
                    .Where(c => c.CategoryCode == code)
                    .Select(c => c.IsActive),
                code, ct);

        // ── 共通判定 ──────────────────────────────────────────────────────

        private readonly record struct Probe(string? PlantId, bool IsActive);

        private static async Task<MasterRefStatus> CheckPlantScopedAsync(
            IQueryable<Probe> query, string? code, string? plantId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(code)) return MasterRefStatus.Ok;

            // 取一列而非 FirstOrDefault：Probe 是 record struct，它的 default 值
            // （PlantId=null、IsActive=false）恰好也是一種合法資料，用 == default 判「找不到」
            // 會把那種列誤判成不存在。
            var rows = await query.Take(1).ToListAsync(ct);
            if (rows.Count == 0) return MasterRefStatus.NotFound;
            var hit = rows[0];

            // 廠別為空的列視為共用（部分既有主檔在多廠啟用前建立，PlantId 尚未回填），
            // 擋掉它們會讓舊部署一存檔就報錯，而那不是這道檢核要抓的東西。
            if (!string.IsNullOrEmpty(hit.PlantId)
                && !string.IsNullOrEmpty(plantId)
                && hit.PlantId != plantId)
                return MasterRefStatus.OtherPlant;

            return hit.IsActive ? MasterRefStatus.Ok : MasterRefStatus.Inactive;
        }

        private static async Task<MasterRefStatus> CheckGlobalAsync(
            IQueryable<bool> activeQuery, string? code, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(code)) return MasterRefStatus.Ok;

            var rows = await activeQuery.Take(1).ToListAsync(ct);
            if (rows.Count == 0) return MasterRefStatus.NotFound;
            return rows[0] ? MasterRefStatus.Ok : MasterRefStatus.Inactive;
        }

        /// <summary>把檢核結果轉成訊息資源鍵；<see cref="MasterRefStatus.Ok"/> 回 null。</summary>
        public static string? MessageKey(MasterRefStatus status) => status switch
        {
            MasterRefStatus.NotFound   => "RefCodeNotFound",
            MasterRefStatus.OtherPlant => "RefCodeOtherPlant",
            MasterRefStatus.Inactive   => "RefCodeInactive",
            _                          => null,
        };
    }
}

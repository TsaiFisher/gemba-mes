using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFactory.MES.Models;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;

namespace SmartFactory.MES.Data
{
    public class AuditInterceptor : SaveChangesInterceptor
    {
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly ILogger<AuditInterceptor> _logger;

        /// <summary>
        /// 存檔前還拿不到真正值的新增列（資料庫產生的主鍵、或指向這類主鍵的外鍵），等存檔成功後再補寫稽核。
        ///
        /// <para>原本在 SavingChanges 當下就寫稽核，這時 EF 給 identity 主鍵的是<b>暫時負數</b>
        /// （-2147482645 之類），於是傳票明細、庫存、成本層、工作日曆等新增紀錄的 EntityId 全是負數，
        /// 稽核查不回是哪一筆。以 DbContext 為鍵：攔截器是 Scoped，但不保證一個 scope 只有一個 context。</para>
        /// </summary>
        private readonly ConditionalWeakTable<DbContext, List<(AuditLog Log, EntityEntry Entry)>> _deferred = new();

        /// <summary>
        /// 本次存檔當下就排入的稽核列。存檔失敗時要卸除：原本它們留在追蹤器裡是 Added，
        /// 呼叫端接住例外後下一次成功的存檔會把它們一起寫出去——替根本沒存進去的資料留下紀錄。
        /// </summary>
        private readonly ConditionalWeakTable<DbContext, List<AuditLog>> _queued = new();

        /// <param name="logger">可省略只為了讓直接 new 的測試不必改寫；DI 一律注入真的 logger。</param>
        public AuditInterceptor(IHttpContextAccessor httpContextAccessor, ILogger<AuditInterceptor>? logger = null)
        {
            _httpContextAccessor = httpContextAccessor;
            _logger = logger ?? NullLogger<AuditInterceptor>.Instance;
        }

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            UpdateAuditLogs(eventData.Context);
            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            UpdateAuditLogs(eventData.Context);
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
        {
            if (eventData.Context != null) _queued.Remove(eventData.Context);
            var pending = TakeDeferred(eventData.Context);
            if (pending != null) WriteDeferred(eventData.Context!, pending, ctx => ctx.SaveChanges());
            return base.SavedChanges(eventData, result);
        }

        public override async ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context != null) _queued.Remove(eventData.Context);
            var pending = TakeDeferred(eventData.Context);
            if (pending != null)
            {
                await WriteDeferredAsync(eventData.Context!, pending, cancellationToken);
            }
            return await base.SavedChangesAsync(eventData, result, cancellationToken);
        }

        // 存檔失敗：資料沒寫進去，暫存的稽核也一併丟掉（不然下一次成功的存檔會把它們補寫出去）。
        public override void SaveChangesFailed(DbContextErrorEventData eventData)
        {
            DiscardPending(eventData.Context);
            base.SaveChangesFailed(eventData);
        }

        public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            DiscardPending(eventData.Context);
            return base.SaveChangesFailedAsync(eventData, cancellationToken);
        }

        private void DiscardPending(DbContext? context)
        {
            TakeDeferred(context);
            if (context == null || !_queued.TryGetValue(context, out var queued)) return;
            _queued.Remove(context);
            foreach (var log in queued)
            {
                var e = context.Entry(log);
                if (e.State == EntityState.Added) e.State = EntityState.Detached;
            }
        }

        private List<(AuditLog Log, EntityEntry Entry)>? TakeDeferred(DbContext? context)
        {
            if (context == null || !_deferred.TryGetValue(context, out var list)) return null;
            _deferred.Remove(context);
            return list.Count > 0 ? list : null;
        }

        /// <summary>補上存檔後才有的真實鍵值，回填稽核。</summary>
        private void Complete(List<(AuditLog Log, EntityEntry Entry)> pending)
        {
            foreach (var (log, entry) in pending)
            {
                log.EntityId = GetPrimaryKeyValue(entry);
                log.Changes = JsonSerializer.Serialize(new { Action = "Create", Data = Snapshot(entry, original: false) });
            }
        }

        // 第二次存檔只含稽核列（AuditLog 本身不再被稽核，不會遞迴）。外層若有明確交易，
        // 這一次也在同一個交易裡；沒有的話資料已經提交，稽核寫不進去只能記 log——
        // 不能往外丟：呼叫端會以為整筆存檔失敗，但資料其實已經寫進去了。
        private void WriteDeferred(DbContext context, List<(AuditLog Log, EntityEntry Entry)> pending, Action<DbContext> save)
        {
            Complete(pending);
            context.Set<AuditLog>().AddRange(pending.Select(p => p.Log));
            if (HasOtherPendingChanges(context)) return;
            try { save(context); }
            catch (Exception ex) { OnDeferredFailed(context, pending, ex); }
        }

        private async Task WriteDeferredAsync(DbContext context, List<(AuditLog Log, EntityEntry Entry)> pending, CancellationToken ct)
        {
            Complete(pending);
            context.Set<AuditLog>().AddRange(pending.Select(p => p.Log));
            if (HasOtherPendingChanges(context)) return;
            try { await context.SaveChangesAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { OnDeferredFailed(context, pending, ex); }
        }

        // SaveChanges(acceptAllChangesOnSuccess: false) 存完後實體仍是 Added/Modified：
        // 這時再存一次會把資料重送。稽核列留在追蹤器，隨呼叫端下一次存檔一起寫入。
        private static bool HasOtherPendingChanges(DbContext context) =>
            context.ChangeTracker.Entries().Any(e => e.Entity is not AuditLog &&
                e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);

        private void OnDeferredFailed(DbContext context, List<(AuditLog Log, EntityEntry Entry)> pending, Exception ex)
        {
            // 卸除未寫入的稽核列，免得卡在追蹤器裡讓之後每一次存檔都重送、一起失敗。
            foreach (var (log, _) in pending) context.Entry(log).State = EntityState.Detached;
            _logger.LogError(ex, "資料已存檔，但 {Count} 筆新增紀錄的稽核補寫失敗（{Types}）",
                pending.Count, string.Join(",", pending.Select(p => p.Log.EntityType).Distinct()));
        }

        private void UpdateAuditLogs(DbContext? context)
        {
            if (context == null) return;

            var entries = context.ChangeTracker.Entries()
                .Where(e => e.State == EntityState.Added || 
                           e.State == EntityState.Modified || 
                           e.State == EntityState.Deleted)
                .Where(e => !(e.Entity is AuditLog))         // 不記錄 AuditLog 本身
                .Where(e => !(e.Entity is PasswordHistory))  // L4：密碼歷史（含雜湊）不進稽核
                .ToList();

            // 註：此處原有兩行 System.Diagnostics.Debug.WriteLine 逐筆印出待稽核實體。
            // 它們只在 DEBUG 組建存在（Release 由編譯器整段移除，不影響正式環境），但在開發機
            // 會被 journald 收走：每次 SaveChanges 印 1 + N 行，實測佔 journal 近八成流量，
            // 把真正該看的訊息稀釋掉。稽核本身寫進 AuditLogs 資料表即可追溯，毋須另外印。

            var userId = _httpContextAccessor.HttpContext?.User?.FindFirstValue(ClaimTypes.NameIdentifier);
            var userName = _httpContextAccessor.HttpContext?.User?.Identity?.Name;
            var ipAddress = _httpContextAccessor.HttpContext?.Connection?.RemoteIpAddress?.ToString();
            var userAgent = _httpContextAccessor.HttpContext?.Request?.Headers["User-Agent"].ToString();



            // 實體本身沒有廠別（明細行、共用主檔）時記操作者當下所在的廠：
            // 原本這類紀錄（實測約三成六）PlantId 是空的，稽核畫面預設的廠別過濾下看不到，
            // 勾「含無廠別」又會連其他廠的一起出現。
            var operatorPlantId = (context as ApplicationDbContext)?.CurrentPlantId;
            _queued.Remove(context);
            var queued = _queued.GetOrCreateValue(context);

            foreach (var entry in entries)
            {
                var auditLog = new AuditLog
                {
                    Timestamp = DateTime.UtcNow,
                    UserId = userId,
                    UserName = userName,
                    // 明確映射而不是 entry.State.ToString()：常數與 EntityState 同名是刻意的
                    // （既有資料就是這三個字串），但那份相等關係得寫下來才守得住。
                    Action = entry.State switch
                    {
                        EntityState.Added => AuditAction.Added,
                        EntityState.Modified => AuditAction.Modified,
                        EntityState.Deleted => AuditAction.Deleted,
                        _ => entry.State.ToString(),
                    },
                    EntityType = GetEntityName(entry.Entity),
                    EntityId = GetPrimaryKeyValue(entry),
                    IpAddress = ipAddress,
                    UserAgent = userAgent,
                    Changes = GetChanges(entry)
                };

                // 嘗試取得 PlantId（如果實體有此屬性）
                var plantIdProperty = entry.Properties.FirstOrDefault(p => p.Metadata.Name == "PlantId");
                auditLog.PlantId = plantIdProperty?.CurrentValue?.ToString() ?? operatorPlantId;

                if (entry.State == EntityState.Added && entry.Properties.Any(p => p.IsTemporary))
                {
                    // 鍵值（或指向新表頭的外鍵）還是暫時值：存檔成功後再寫，見 _deferred
                    _deferred.GetOrCreateValue(context).Add((auditLog, entry));
                    continue;
                }

                context.Set<AuditLog>().Add(auditLog);
                queued.Add(auditLog);
            }
        }

        private string GetEntityName(object entity)
        {
            var type = entity.GetType();
            var name = type.Name;

            // 處理 Proxy 類別 (EF Core Lazy Loading)
            if (name.Contains("Proxy"))
            {
                name = type.BaseType?.Name ?? name;
            }

            // 對應易讀名稱
            return name switch
            {
                "ApplicationUser" => "User",
                "ApplicationRole" => "Role",
                var n when n.StartsWith("IdentityUserRole") => "UserRole",
                var n when n.StartsWith("IdentityRoleClaim") => "RolePermission",
                var n when n.StartsWith("IdentityUserClaim") => "UserClaim",
                var n when n.StartsWith("IdentityUserLogin") => "UserExternalLogin",
                var n when n.StartsWith("IdentityUserToken") => "UserToken",
                _ => name
            };
        }

        /// <summary>
        /// 主鍵值；複合主鍵依鍵的欄位順序以「|」串接（例：<c>RC-2610-00001|2</c>）。
        ///
        /// <para>原本只取第一個主鍵欄，明細行只剩表頭單號；修改紀錄又只含改動的欄位，
        /// 同一張單兩行的修改分不出是哪一行。串接後以單號搜尋仍找得到（列表是「包含」比對）。
        /// 值一律以不受介面語言影響的格式輸出：日期原本隨文化變成 2119/4/23 或 4/23/2119。</para>
        /// </summary>
        internal static string? GetPrimaryKeyValue(EntityEntry entry)
        {
            var key = entry.Metadata.FindPrimaryKey();
            if (key == null) return null;
            var parts = key.Properties.Select(p => FormatKeyPart(entry.Property(p.Name).CurrentValue)).ToList();
            return parts.All(v => v == null) ? null : string.Join("|", parts.Select(v => v ?? ""));
        }

        private static string? FormatKeyPart(object? value) => value switch
        {
            null => null,
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateTime dt => dt.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };

        private Dictionary<string, object?> Snapshot(EntityEntry entry, bool original)
        {
            var data = new Dictionary<string, object?>();
            foreach (var property in entry.Properties)
            {
                if (ShouldTrackProperty(property.Metadata.Name))
                {
                    data[property.Metadata.Name] = AuditPiiMasker.Mask(property.Metadata.Name,
                        original ? property.OriginalValue : property.CurrentValue);
                }
            }
            return data;
        }

        private string? GetChanges(EntityEntry entry)
        {
            if (entry.State == EntityState.Added)
            {
                // 新增：記錄所有屬性的當前值
                return JsonSerializer.Serialize(new { Action = "Create", Data = Snapshot(entry, original: false) });
            }
            else if (entry.State == EntityState.Modified)
            {
                // 修改：記錄變更前後的值
                var before = new Dictionary<string, object?>();
                var after = new Dictionary<string, object?>();

                foreach (var property in entry.Properties)
                {
                    if (property.IsModified && ShouldTrackProperty(property.Metadata.Name))
                    {
                        before[property.Metadata.Name] = AuditPiiMasker.Mask(property.Metadata.Name, property.OriginalValue);
                        after[property.Metadata.Name] = AuditPiiMasker.Mask(property.Metadata.Name, property.CurrentValue);
                    }
                }

                if (before.Any())
                {
                    return JsonSerializer.Serialize(new { Action = "Update", Before = before, After = after });
                }
            }
            else if (entry.State == EntityState.Deleted)
            {
                // 刪除：記錄所有屬性的原始值
                return JsonSerializer.Serialize(new { Action = "Delete", Data = Snapshot(entry, original: true) });
            }

            return null;
        }

        private static bool ShouldTrackProperty(string propertyName)
        {
            // 排除不需要記錄的欄位
            var excludedProperties = new[]
            {
                "ConcurrencyStamp",
                "SecurityStamp",
                "PasswordHash",
                "NormalizedEmail",
                "NormalizedUserName"
            };

            return !excludedProperties.Contains(propertyName);
        }
    }
}

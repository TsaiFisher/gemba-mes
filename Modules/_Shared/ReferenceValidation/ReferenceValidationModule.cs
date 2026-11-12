using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules._Shared.ReferenceValidation
{
    /// <summary>
    /// 每次存檔前執行 <see cref="ReferenceValidator.EnsureValidAsync"/>：參照欄位不通過就丟
    /// <see cref="ReferenceValidationException"/>，整批不寫入。
    /// <para>透過 Core 的 <see cref="IModuleSaveChangesInterceptor"/> 擴充點掛上（排在稽核攔截器之前，
    /// 被擋下的存檔不會留稽核紀錄）。ApplyGlobalValues 在 SaveChanges 覆寫內、攔截器之前執行，
    /// 所以這裡看得到補好的 PlantId。</para>
    /// </summary>
    public sealed class ReferenceValidationInterceptor : SaveChangesInterceptor, IModuleSaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            if (eventData.Context is ApplicationDbContext db) ReferenceValidator.EnsureValidAsync(db).GetAwaiter().GetResult();
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context is ApplicationDbContext db) await ReferenceValidator.EnsureValidAsync(db, cancellationToken);
            return result;
        }
    }

    public sealed class ReferenceValidationModule : IAppModule
    {
        public void RegisterServices(IServiceCollection services, IConfiguration configuration)
        {
            services.AddScoped<IModuleSaveChangesInterceptor, ReferenceValidationInterceptor>();
            services.AddScoped<ReferenceValidationExceptionFilter>();
            services.Configure<MvcOptions>(options => options.Filters.AddService<ReferenceValidationExceptionFilter>());
        }

        public void Configure(WebApplication app) { }
    }
}

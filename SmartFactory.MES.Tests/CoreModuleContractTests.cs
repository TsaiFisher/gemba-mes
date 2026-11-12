using System;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests
{
    /// <summary>
    /// Core ↔ 模組 相容性合約測試。
    ///
    /// 鎖定「upstream Core 對外提供、ProjectZ 模組（Modules/）所依賴」的 API 介面與簽章。
    /// 當 Core 改動這些簽章時，本測試在 <b>常規 ci.yml</b> 即快速紅燈並明確指出破裂的契約成員，
    /// 不必等到 core-compat 排程 dry-run merge 才在 build 階段以晦澀錯誤爆出。
    ///
    /// 來源事件：<c>IApprovalService.ResolveSaveStatusAsync</c> 曾因僅就地修改 Core 檔而漂移
    /// （Core upstream 無此方法），直到上送 Core（PR#6）才收斂。本測試把這類「模組對 Core API
    /// 的依賴」明文登記成契約——新增依賴時請同步在此補一條，作為「此 API 是否已在 upstream Core」的強制檢查點。
    ///
    /// 註：此處只鎖 <b>Core 主體擁有</b>（存在於 upstream/main）的型別；ProjectZ 顧問側型別
    /// （CurrencyService / ModuleControllerBase / IUomService / IAccountingPeriodService 等）不在此契約內。
    /// </summary>
    public class CoreModuleContractTests
    {
        private const BindingFlags PublicInstance =
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;

        private static MethodInfo RequireMethod(Type t, string name, params Type[] paramTypes)
        {
            var candidates = t.GetMethods(PublicInstance).Where(m => m.Name == name).ToList();
            Assert.True(candidates.Count > 0,
                $"Core 契約破裂：{t.FullName} 缺少模組依賴的方法「{name}」。" +
                "若 Core upstream 已移除/改名，請上送回 Core 或調整模組依賴。");

            var match = candidates.FirstOrDefault(m =>
                m.GetParameters().Select(p => p.ParameterType).SequenceEqual(paramTypes));
            Assert.True(match != null,
                $"Core 契約破裂：{t.FullName}.{name} 參數簽章不符。" +
                $"期望 ({string.Join(", ", paramTypes.Select(p => p.Name))})，" +
                $"實際候選 [{string.Join(" | ", candidates.Select(c => "(" + string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name)) + ")"))}]");
            return match!;
        }

        // ── IAppModule：零衝突模組架構的核心擴充點（Program.cs 自動掃描呼叫）──
        [Fact]
        public void IAppModule_extension_point_contract()
        {
            RequireMethod(typeof(IAppModule), nameof(IAppModule.RegisterServices),
                typeof(IServiceCollection), typeof(IConfiguration));
            RequireMethod(typeof(IAppModule), nameof(IAppModule.Configure),
                typeof(WebApplication));
        }

        // ── IApprovalService：簽核契約（含曾漂移的 ResolveSaveStatusAsync）──
        [Fact]
        public void IApprovalService_contract()
        {
            var t = typeof(IApprovalService);

            // 8 個 ORD/PUR controller 在 Save 時呼叫，曾因僅就地改 Core 檔而漂移。
            var resolve = RequireMethod(t, "ResolveSaveStatusAsync", typeof(string), typeof(string));
            Assert.Equal(typeof(System.Threading.Tasks.Task<DocumentApprovalStatus>), resolve.ReturnType);

            RequireMethod(t, "GetApprovalConfig", typeof(string), typeof(string));
            RequireMethod(t, "SubmitForApproval", typeof(string), typeof(string), typeof(string), typeof(string));
            RequireMethod(t, "ProcessApproval", typeof(string), typeof(string), typeof(string), typeof(ApprovalStatus), typeof(string));
            RequireMethod(t, "WithdrawRequest", typeof(string), typeof(string));
            RequireMethod(t, "ReassignApprover", typeof(string), typeof(string), typeof(string), typeof(string));
            RequireMethod(t, "CancelOnDelete", typeof(string), typeof(string), typeof(string));
        }

        // ── IPermissionService：模組 default-deny 權限閘門（CanRead/Create/Update/Delete/Print）──
        [Fact]
        public void IPermissionService_contract()
        {
            var t = typeof(IPermissionService);
            foreach (var op in new[] { "CanRead", "CanCreate", "CanUpdate", "CanDelete", "CanPrint" })
            {
                var m = RequireMethod(t, op, typeof(string));
                Assert.Equal(typeof(System.Threading.Tasks.Task<bool>), m.ReturnType);
            }
            RequireMethod(t, "CanViewData", typeof(string));
        }

        // ── Core 服務介面存在性（模組大量注入；命名空間/型別不得消失）──
        [Theory]
        [InlineData(typeof(IUserContext))]
        [InlineData(typeof(IDateTimeService))]
        [InlineData(typeof(IApprovalSyncContributor))]
        public void Core_service_interface_must_exist(Type contract)
        {
            Assert.True(contract.IsInterface, $"{contract.FullName} 應為 Core 公開介面契約。");
        }

        // ── DocumentApprovalStatus：模組 Save/Delete/簽核流程 switch 依賴的列舉值與數值不得改變──
        [Fact]
        public void DocumentApprovalStatus_enum_values_are_stable()
        {
            Assert.Equal(0, (int)DocumentApprovalStatus.Draft);
            Assert.Equal(1, (int)DocumentApprovalStatus.PendingApproval);
            Assert.Equal(2, (int)DocumentApprovalStatus.Approved);
            Assert.Equal(3, (int)DocumentApprovalStatus.Rejected);
        }

        // ── ApplicationDbContext：partial 擴充鉤（顧問模組 DbSet/Fluent API 經此注入，禁改 Core 主體）──
        [Fact]
        public void ApplicationDbContext_partial_extension_hook_exists()
        {
            var hook = typeof(ApplicationDbContext).GetMethod(
                "OnModelCreatingPartial",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.True(hook != null,
                "Core 契約破裂：ApplicationDbContext 缺少 partial 擴充鉤 OnModelCreatingPartial(ModelBuilder)。" +
                "此為零衝突模組架構的 DbSet/Fluent 注入點，移除將迫使顧問改 Core 主體。");
            Assert.Equal(typeof(ModelBuilder), hook!.GetParameters().Single().ParameterType);
        }

        // ── BaseEntity：所有實體共同基底（Core 擁有）──
        [Fact]
        public void BaseEntity_type_exists()
        {
            Assert.NotNull(typeof(BaseEntity));
        }
    }
}

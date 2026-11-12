using Microsoft.AspNetCore.Mvc.Razor;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Services;
using static System.Reflection.Assembly;

namespace SmartFactory.MES.Modules.MFG
{
    /// <summary>
    /// MFG（製造執行）模組。
    /// 涵蓋：生產計劃(MRP)、工單(WO)、派工(Dispatch)、報工(Report)、設備狀態(MachineStatus)。
    /// </summary>
    public class MFGModule : IAppModule
    {
        public void RegisterServices(IServiceCollection services, IConfiguration configuration)
        {
            // MRP 引擎（依賴 INV 的 IInventoryReservationService，由 INVModule 註冊）
            services.AddScoped<IMrpEngine, MrpEngine>();
            services.AddScoped<IMaterialSlipService, MaterialSlipService>();
            // 重工工單建立：工單列表「建立重工」與 NCR 處置「重工」共用（2026-10-15）
            services.AddScoped<IReworkWorkOrderService, ReworkWorkOrderService>();

            // 委外報價 4-level 查價的單一來源。原本兩份實作行為不同（見該介面註解），
            // 委外價格分析報表是第三個呼叫端——再複製一次必然再漂移一次。
            services.AddScoped<IOutsourceQuotationResolver, OutsourceQuotationResolver>();

            // Phase C：排程引擎（mfg-domain.md §7）
            services.AddScoped<ISchedulingEngine, SchedulingEngine>();
            services.AddScoped<IWorkCalendarService, WorkCalendarService>();
            services.AddScoped<IOeeService, OeeService>();

            // 下工人工/機台成本累計，Web/Mobile 共用（生產線多人上工 overhead 防灌水，2026-07-15）
            services.AddScoped<IProductionCostAccrualService, ProductionCostAccrualService>();

            // 模具打次累計＋保養排程（§14.1 第二階段），桌機/手機報工共用
            services.AddScoped<IToolingShotService, ToolingShotService>();

            // 產線即時看板（一鍵看整條線在忙什麼，2026-07-15）
            services.AddScoped<LineStatusAnalyticsService>();

            // 多階料表/多階途程查詢（兩畫面共用同一次展開）
            services.AddScoped<IMultiLevelBomService, MultiLevelBomService>();

            // Phase E：ETA 增量重算引擎（mfg-domain.md §7.5）
            services.AddScoped<IEtaRecalculator, EtaRecalculator>();
            services.AddScoped<IEtaCalibrationService, EtaCalibrationService>();
            services.AddScoped<IQueueTimeSuggestionService, QueueTimeSuggestionService>();
            services.AddSingleton<IReportEtaNotifier, ReportEtaNotifier>();  // 自建 scope 的背景 ETA+Push（桌機/手機共用）

            // Phase F：完工入庫服務（mfg-domain.md §5.4 / §17.2）
            services.AddScoped<IProductionCompletionService, ProductionCompletionService>();

            // §16.3：工單成本月末結算（Preliminary → Final 鎖定）
            services.AddScoped<IWorkOrderCostFinalizationService, WorkOrderCostFinalizationService>();

            // §3.3 / §3.6：期末實際費率重估（部門實際費用 provider + 回刷引擎）
            services.AddScoped<IDepartmentActualCostProvider, PersistedDepartmentActualCostProvider>();
            services.AddScoped<IActualRateReestimationService, ActualRateReestimationService>();
            // 在製品的材料腿：月結快照與在製品報表畫面共用同一份讀取（見 W2-F7）
            services.AddScoped<IWorkOrderIssuedMaterialCostReader, WorkOrderIssuedMaterialCostReader>();

            // §21.5：物料備齊確認閘口
            services.AddScoped<IWorkOrderMaterialReadinessService, WorkOrderMaterialReadinessService>();

            // 備料單來源帶料（派工／工單／生產批 → 備料明細＋預設備料倉）
            services.AddScoped<IStagingSourceService, StagingSourceService>();

            // §24：投產定案服務（重算量／硬料閘／在途鎖／軟預留轉鎖；單工單與批層共用）
            services.AddScoped<IWorkOrderReleaseService, WorkOrderReleaseService>();
            services.AddScoped<IWorkOrderDemandReservation, WorkOrderDemandReservation>();

            // §25.5：MRP 展開＋請購單落地編排（畫面執行與訂單自動展開共用）
            services.AddScoped<IMrpRunExecutionService, MrpRunExecutionService>();

            // §25.5：訂單核准後自動展開 MRP（僅簡化模式；以狀態判定，重複呼叫安全）
            services.AddScoped<IMrpAutoExpandService, MrpAutoExpandService>();

            // 工單釋出簽核：中央同步 contributor（mfg-domain.md §18.2）
            services.AddScoped<IApprovalSyncContributor, WorkOrderApprovalSync>();

            // 手機簽核摘要（IApprovalSummaryProvider）。金額用「標準料＋工＋費」——
            // 那正是 WorkOrderReleaseService 拿去和 mfg_wo_approval_threshold 比的數字，
            // 顯示別的會讓簽核人看到的金額與「這張單為什麼會出現在他清單裡」對不起來。
            services.AddScoped<SmartFactory.MES.Modules._Shared.IApprovalSummaryProvider, Services.WorkOrderApprovalSummary>();

            // Phase G-H：抽樣計畫服務（MIL-STD-1916 VL 切換 + 選配跳批）
            services.AddScoped<ISamplingPlanService, SamplingPlanService>();

            // §26：首件檢查（FPI）閘口——以事實判定，桌機／手機共用同一份，避免雙軌漂移
            services.AddScoped<IFpiGateService, FpiGateService>();

            // 派工暫停／恢復／換機續做／剩餘量結案／作廢連帶取消（桌機、手機、模具保養共用）
            services.AddScoped<IDispatchLifecycleService, DispatchLifecycleService>();

            // 自動重排排程（廠別參數控制三種時機，皆預設關閉）
            services.AddScoped<IAutoScheduleService, AutoScheduleService>();
            services.AddHostedService<AutoScheduleBackgroundService>();

            // §25：製造簡化模式判定（開工放行狀態／現場待辦狀態）——同樣桌機／手機共用
            services.AddScoped<IMfgSimplifiedModeService, MfgSimplifiedModeService>();

            // §25.4：工序順序守門（免排程後後製程不得超前製程）——報工三條入口共用
            services.AddScoped<IOperationSequenceGuard, OperationSequenceGuard>();
            services.AddScoped<IOverQuantityPolicy, OverQuantityPolicy>();
            services.AddScoped<IOutsourceProgressService, OutsourceProgressService>();

            // 領域事件系統（mfg-domain.md §10.5 非同步協作）
            services.AddScoped<IDomainEventPublisher, InMemoryDomainEventPublisher>();
            services.AddScoped<IDomainEventHandler<WorkOrderCompletedEvent>, WorkOrderCompletedEventHandler>();

            // ETA 全量校準背景服務（每晚 01:00 執行，mfg-domain.md §7.5 層級 3）
            services.AddHostedService<EtaRecalibrationBackgroundService>();

            // T-1 生產準備提醒（每日 17:00，mfg-domain.md §23.3）
            services.AddHostedService<PreparationAlertBackgroundService>();

            // QueueTime 統計回填（每晚 02:00，mfg-domain.md §B-2）
            services.AddHostedService<QueueTimeBackfillService>();

            // Phase 0-B 報表工具（§5.3）：畫面／排程／未來的 LLM 共用同一份查詢邏輯。
            // 同時以 IReportToolDescriptor 登錄，供 ReportToolCatalog 列舉。
            services.AddScoped<Reporting.WorkOrderProgressTool>();
            services.AddScoped<Reporting.DefectRateTool>();
            services.AddScoped<Reporting.MrpAchievementTool>();
            services.AddScoped<SmartFactory.MES.Modules.RPS.Contracts.IReportToolDescriptor>(
                sp => sp.GetRequiredService<Reporting.WorkOrderProgressTool>());
            services.AddScoped<SmartFactory.MES.Modules.RPS.Contracts.IReportToolDescriptor>(
                sp => sp.GetRequiredService<Reporting.DefectRateTool>());
            services.AddScoped<SmartFactory.MES.Modules.RPS.Contracts.IReportToolDescriptor>(
                sp => sp.GetRequiredService<Reporting.MrpAchievementTool>());

            services.Configure<RazorViewEngineOptions>(options =>
            {
                options.ViewLocationFormats.Add(
                    "/Modules/MFG/Views/{1}/{0}" + RazorViewEngine.ViewExtension);
                options.ViewLocationFormats.Add(
                    "/Modules/MFG/Views/Shared/{0}" + RazorViewEngine.ViewExtension);
            });
        }

        public void Configure(WebApplication app) { }
    }
}

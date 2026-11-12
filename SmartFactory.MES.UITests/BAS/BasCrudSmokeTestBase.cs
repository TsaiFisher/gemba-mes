namespace SmartFactory.MES.UITests.BAS
{
    /// <summary>
    /// BAS 主檔 smoke test 基底：BAS 畫面採自洽 crudPanel 契約
    /// （非共用 crud-panel.js/sidePanel；R6 baseline 既定偏離，won't-fix），
    /// 差異僅在 DOM 選擇器，操作流程與斷言沿用 CrudSmokeTestBase。
    ///
    /// 契約對照：
    ///   面板  #sidePanel      → #crudPanel
    ///   內容  #sidePanelBody  → #crudPanelBody
    ///   標題  #sidePanelLabel → #crudPanelTitle
    ///   關閉  button.btn-secondary → [data-dismiss-panel]
    ///   Toast #successToast（相同）；驗證失敗＝partial 重繪（[data-valmsg-for] 有值，相同）
    /// </summary>
    public abstract class BasCrudSmokeTestBase : CrudSmokeTestBase
    {
        protected BasCrudSmokeTestBase(UiTestFixture fixture) : base(fixture) { }

        protected override string PanelSel       => "#crudPanel";
        protected override string PanelBodySel   => "#crudPanelBody";
        protected override string PanelLabelSel  => "#crudPanelTitle";
        protected override string CloseButtonSel => "#crudPanelBody [data-dismiss-panel]";
    }
}

using Xunit;

namespace SmartFactory.MES.IntegrationTests.Load
{
    /// <summary>
    /// 壓測只在 <c>LOAD_TEST=1</c> 時執行：一次要開上百個並行請求、跑一兩分鐘，放進每次的整合測試會拖慢所有人，
    /// 而且共用測試庫上別的測試正在跑時量出來的鎖等待沒有意義。沒開時清楚顯示 Skip 與原因，不是靜靜通過。
    /// </summary>
    public sealed class LoadTestFactAttribute : FactAttribute
    {
        public LoadTestFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("LOAD_TEST") != "1")
                Skip = "壓測：設 LOAD_TEST=1 才執行（建議用專屬測試庫，見 ShiftChangeLoadTests 說明）。";
        }
    }
}

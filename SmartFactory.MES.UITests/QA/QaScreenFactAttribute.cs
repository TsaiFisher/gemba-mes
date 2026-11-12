using Xunit;

namespace SmartFactory.MES.UITests.QA
{
    /// <summary>
    /// 需要**受測 app 有開啟手冊 Q&amp;A 功能**的 UI 測試。
    /// 沒有設 <c>QA_UI_SCREENS=1</c> 時回報為 skipped。
    ///
    /// <para><b>為什麼要一道開關。</b><c>Features:QaAssistant:Enabled</c> 預設是**關的**（§17.2），
    /// 關閉時 `QAModule` 連路由都不註冊——`/QaAssistant` 與 `/QaExtract` 是 404。
    /// 而 nightly 刻意不開這個功能（見 `nightly.yml` 對 `QaDbContext` migration 的註解）。
    /// 沒有這道開關的話，這幾支會在 nightly 上以 404 常態紅，而**真因是設定不是缺陷**——
    /// 紅久了就沒人看，等於把整個 UI 層的訊號一起賠掉。</para>
    ///
    /// <para>⚠️ 這與 `LiveOllamaFact` 是**兩件不同的事**，不要混用：那一支要的是推論服務，
    /// 這一支只要求那兩個畫面存在。下面的測試全部把 `/QaAssistant/Ask` 攔下來回合成資料，
    /// **一顆模型都不需要**。</para>
    ///
    /// <para>執行方式（app 需另外以同一個開關啟動）：</para>
    /// <code>
    /// # 1) 起 app（本機實測用編譯產物，不要用 dotnet run——空字串環境變數會被吃掉）
    /// Features__QaAssistant__Enabled=true ASPNETCORE_URLS=http://127.0.0.1:5110 \
    /// dotnet bin/Debug/net10.0/SmartFactory.MES.dll
    ///
    /// # 2) 跑測試
    /// QA_UI_SCREENS=1 dotnet test SmartFactory.MES.UITests --filter "FullyQualifiedName~UITests.QA"
    /// </code>
    /// </summary>
    public sealed class QaScreenFactAttribute : FactAttribute
    {
        public QaScreenFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("QA_UI_SCREENS") != "1")
                Skip = "需要受測 app 開啟 Features__QaAssistant__Enabled=true，並設 QA_UI_SCREENS=1。";
        }
    }
}

using Xunit;

namespace SmartFactory.MES.IntegrationTests.QA
{
    /// <summary>
    /// 需要**真實推論服務**的測試。沒有設 <c>QA_LIVE_OLLAMA=1</c> 時回報為 skipped。
    ///
    /// <para><b>為什麼是選用而不是常設</b>：CI 沒有 Ollama，也不該有——
    /// 把幾分鐘的 CPU 推論放進每一次 build，換來的是所有人都學會忽略它。
    /// 但這些量測又必須是**可重跑的程式**而不是一次性的手工紀錄，
    /// 否則下次改切塊策略時沒人能重現基準。</para>
    ///
    /// <para>執行方式（開發機，需先開好 SSH 反向通道，見規格 §17.11.2）：</para>
    /// <code>
    /// QA_LIVE_OLLAMA=1 \
    /// Features__QaAssistant__OllamaUrl=http://127.0.0.1:11435 \
    /// Features__QaAssistant__EmbeddingModel=embeddinggemma \
    /// dotnet test SmartFactory.MES.IntegrationTests --filter "FullyQualifiedName~Benchmark"
    /// </code>
    /// </summary>
    public sealed class LiveOllamaFactAttribute : FactAttribute
    {
        public LiveOllamaFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("QA_LIVE_OLLAMA") != "1")
                Skip = "需要真實推論服務：設 QA_LIVE_OLLAMA=1 ＋ Features__QaAssistant__OllamaUrl／EmbeddingModel。";
        }
    }
}

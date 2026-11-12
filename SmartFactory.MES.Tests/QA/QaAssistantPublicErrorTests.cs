using SmartFactory.MES.Modules.QA.Controllers;
using SmartFactory.MES.Modules.QA.Services;
using SmartFactory.MES.Modules.RPS.Services;
using Xunit;

namespace SmartFactory.MES.Tests.QA;

/// <summary>
/// 回給使用者的錯誤訊息不得帶出內部資訊（安全評估 2026-10-07 #5）。
/// 推論服務的原話含端點位址、模型名稱與組態鍵；完整內容只進 log。
/// </summary>
public class QaAssistantPublicErrorTests
{
    private static string Msg(string key) => $"[{key}]";

    private const string Internal = "無法連線到 http://10.0.0.5:11434（Features:QaAssistant:OllamaUrl），模型 gemma4:latest";

    public static TheoryData<Exception, string> Cases => new()
    {
        { new OllamaException(Internal), "[QaInferenceUnavailable]" },
        { new OllamaException(Internal, new HttpRequestException("refused")), "[QaInferenceUnavailable]" },
        { new DocIndexOutOfDateException(Internal), "[QaIndexOutOfDate]" },
        { new ReportParamsInvalidException("TrialBalance", Internal), "[QaParamsInvalid]" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void 內部例外_一律換成通用訊息(Exception ex, string expected)
    {
        var shown = QaAssistantController.PublicErrorOf(ex, Msg);

        Assert.Equal(expected, shown);
        Assert.DoesNotContain("11434", shown);
        Assert.DoesNotContain("gemma4", shown);
    }

    /// <summary>忙碌提示是操作指引（等上一題完成／改用報表畫面），照原話給使用者。</summary>
    [Fact]
    public void 推論忙碌_保留原本的操作指引()
    {
        var busy = new InferenceBusyException("你已經有一則提問正在推論中，請等它完成或按取消後再問下一題。");

        Assert.Equal(busy.Message, QaAssistantController.PublicErrorOf(busy, Msg));
    }
}

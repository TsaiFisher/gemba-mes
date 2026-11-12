using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.BAS;

/// <summary>
/// 測試 DocNumberService.Preview()（不需 DB，純格式邏輯）
/// NextAsync() 使用原生 PostgreSQL UPSERT，對真實 PostgreSQL 連線的行為見
/// SmartFactory.MES.IntegrationTests/DocNumberServiceIntegrationTests.cs。
/// </summary>
public class DocNumberServiceTests : TestBase
{
    private readonly DocNumberService _service;

    public DocNumberServiceTests()
    {
        _service = new DocNumberService(_context);
    }

    // ── Preview：格式輸出 ────────────────────────────────────────────────────

    [Fact]
    public void Preview_DateFormat_None_ReturnsPrefix_Sep_Seq()
    {
        var config = MakeConfig("GR", "-", DocDateFormat.None, seqLen: 5);
        var result = _service.Preview(config, "P001", new DateOnly(2025, 3, 27), sampleSeq: 1);
        Assert.Equal("GR-00001", result);
    }

    [Fact]
    public void Preview_DateFormat_YY_ReturnsCorrectFormat()
    {
        var config = MakeConfig("GR", "-", DocDateFormat.YY, seqLen: 5);
        var result = _service.Preview(config, "P001", new DateOnly(2025, 3, 27), sampleSeq: 42);
        Assert.Equal("GR-25-00042", result);
    }

    [Fact]
    public void Preview_DateFormat_YYMM_ReturnsCorrectFormat()
    {
        var config = MakeConfig("IO", "-", DocDateFormat.YYMM, seqLen: 4);
        var result = _service.Preview(config, "P001", new DateOnly(2025, 3, 27), sampleSeq: 7);
        Assert.Equal("IO-2503-0007", result);
    }

    [Fact]
    public void Preview_DateFormat_YYYYMMDD_ReturnsCorrectFormat()
    {
        var config = MakeConfig("PR", "-", DocDateFormat.YYYYMMDD, seqLen: 3);
        var result = _service.Preview(config, "P001", new DateOnly(2025, 3, 27), sampleSeq: 1);
        Assert.Equal("PR-20250327-001", result);
    }

    [Fact]
    public void Preview_EmptySeparator_ConcatenatesParts()
    {
        var config = MakeConfig("SO", "", DocDateFormat.YY, seqLen: 5);
        var result = _service.Preview(config, "P001", new DateOnly(2025, 1, 1), sampleSeq: 1);
        Assert.Equal("SO2500001", result);
    }

    [Fact]
    public void Preview_SeqLength_PadsCorrectly()
    {
        var config = MakeConfig("TF", "-", DocDateFormat.None, seqLen: 3);
        var result = _service.Preview(config, "P001", sampleSeq: 5);
        Assert.Equal("TF-005", result);
    }

    [Fact]
    public void Preview_SeqLength_DoesNotTruncateOverflow()
    {
        // seq=10000 超過 4 位，不應截斷
        var config = MakeConfig("GR", "-", DocDateFormat.None, seqLen: 4);
        var result = _service.Preview(config, "P001", sampleSeq: 10000);
        Assert.Equal("GR-10000", result);
    }

    [Fact]
    public void Preview_DateNull_UsesTodayDate()
    {
        var config = MakeConfig("GR", "-", DocDateFormat.YY, seqLen: 5);
        // date = null → 使用 UTC 今日
        var result = _service.Preview(config, "P001", date: null, sampleSeq: 1);
        Assert.NotNull(result);
        Assert.StartsWith("GR-", result);
    }

    // ── NextAsync：DB 相依，驗證設定不存在時拋例外 ──────────────────────────

    [Fact]
    public async Task NextAsync_ConfigNotFound_ThrowsInvalidOperationException()
    {
        // InMemory DB 沒有任何 DocNumberConfig → 應拋出說明性例外（供全站 filter 轉友善訊息）
        await Assert.ThrowsAsync<DocNumberConfigNotFoundException>(
            () => _service.NextAsync("NonExistentDocType", "P001"));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static DocNumberConfig MakeConfig(string prefix, string sep,
        DocDateFormat dateFormat, int seqLen = 5,
        DocSeqReset reset = DocSeqReset.Yearly)
        => new DocNumberConfig
        {
            DocType     = "TEST",
            DocTypeName = "Test",
            Prefix      = prefix,
            Separator   = sep,
            DateFormat  = dateFormat,
            SeqLength   = seqLen,
            ResetCycle  = reset,
            IsActive    = true
        };
}

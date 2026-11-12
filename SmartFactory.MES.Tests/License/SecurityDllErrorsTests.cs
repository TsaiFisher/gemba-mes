using System.Reflection;
using System.Security;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Tests.License;

/// <summary>
/// Security DLL 以反射呼叫，DLL 內拋出的例外會被 <see cref="TargetInvocationException"/> 包住。
///
/// <para>
/// 修正前，Program.cs 的 catch 只取外層 <c>ex.Message</c>，維運人員看到的第一行永遠是
/// 「載入 Security DLL 失敗：Exception has been thrown by the target of an invocation.」，
/// 而「錨定檔無法在本機解開」「授權已過期」這類真正的原因埋在兩層 InnerException 之下
/// （2026-09-24 Windows 實機驗收發現，見 SECURITY-OPERATIONS.md §17.9 步驟 4 補驗）。
/// </para>
///
/// <para>
/// 這裡刻意用真的反射呼叫製造例外，而不是手動 new 一個 TargetInvocationException ——
/// 要守的就是「反射那一層」的行為。
/// </para>
/// </summary>
public class SecurityDllErrorsTests
{
    private static Exception InvokeThrowing(Exception toThrow)
    {
        var method = typeof(SecurityDllErrorsTests).GetMethod(
            nameof(Thrower), BindingFlags.NonPublic | BindingFlags.Static)!;
        try
        {
            method.Invoke(null, [toThrow]);
        }
        catch (Exception ex)
        {
            return ex;
        }
        throw new InvalidOperationException("反射呼叫應該要拋出例外。");
    }

    private static void Thrower(Exception ex) => throw ex;

    [Fact]
    public void InvalidOperationFromDll_IsUnwrapped_SoItsMessageIsTheFirstLine()
    {
        var original = new InvalidOperationException("錨定檔無法在本機解開：可能是從其他電腦複製過來的");
        var caught = InvokeThrowing(original);
        Assert.IsType<TargetInvocationException>(caught);   // 前提：反射確實包了一層

        var result = SecurityDllErrors.Translate(caught);

        Assert.Same(original, result);
        Assert.DoesNotContain("target of an invocation", result.Message);
    }

    [Fact]
    public void SecurityExceptionFromDll_IsUnwrapped()
    {
        var original = new SecurityException("授權指紋不符");

        var result = SecurityDllErrors.Translate(InvokeThrowing(original));

        Assert.Same(original, result);
    }

    [Fact]
    public void OtherExceptionFromDll_IsWrapped_WithTheInnerMessage_NotTheReflectionMessage()
    {
        var caught = InvokeThrowing(new FormatException("Base64 無法解析"));

        var result = SecurityDllErrors.Translate(caught);

        var ioe = Assert.IsType<InvalidOperationException>(result);
        Assert.Equal("載入 Security DLL 失敗：Base64 無法解析", ioe.Message);
        Assert.Same(caught, ioe.InnerException);   // 完整鏈保留給 log
    }

    [Fact]
    public void NonReflectionException_IsWrapped_WithItsOwnMessage()
    {
        // 例如 Assembly.LoadFrom 讀到壞檔：沒有經過反射呼叫，不應被誤拆
        var original = new BadImageFormatException("檔案不是有效的組件");

        var result = SecurityDllErrors.Translate(original);

        var ioe = Assert.IsType<InvalidOperationException>(result);
        Assert.Equal("載入 Security DLL 失敗：檔案不是有效的組件", ioe.Message);
        Assert.Same(original, ioe.InnerException);
    }
}

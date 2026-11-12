using System.Reflection;
using System.Security;

namespace SmartFactory.MES.Services;

/// <summary>
/// 把載入／呼叫 Security DLL 時捕捉到的例外，轉成維運人員第一行就看得懂的例外。
///
/// <para>
/// DLL 以反射呼叫，其內拋出的例外一律被 <see cref="TargetInvocationException"/> 包住。
/// 原本的 catch 只取外層訊息，結果不論是錨定檔解不開、授權過期或指紋不符，
/// 第一行都只剩「Exception has been thrown by the target of an invocation.」。
/// </para>
///
/// <para>
/// 規則：先拆掉反射層；DLL 自己拋的 <see cref="InvalidOperationException"/>／
/// <see cref="SecurityException"/> 原樣回傳（訊息本來就是寫給維運看的）；
/// 其餘包成「載入 Security DLL 失敗：{真正原因}」，完整鏈保留在 InnerException。
/// </para>
/// </summary>
internal static class SecurityDllErrors
{
    public static Exception Translate(Exception ex)
    {
        var inner = ex;
        while (inner is TargetInvocationException { InnerException: { } next })
            inner = next;

        if (inner is InvalidOperationException or SecurityException)
            return inner;

        return new InvalidOperationException($"載入 Security DLL 失敗：{inner.Message}", ex);
    }
}

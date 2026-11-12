using System.Reflection;
using Microsoft.AspNetCore.Mvc;

namespace SmartFactory.MES.Tests.Api;

/// <summary>
/// [ApiController] 的 action 不可有「非可空實值型別＋預設值」的參數（如 <c>decimal lotSize = 1</c>）。
///
/// <para>Core 的 <c>LocalizableDefaultMessageProvider</c> 為了讓表單的隱含必填有在地化訊息，
/// 替<b>所有</b>非可空實值型別補上 <c>[Required]</c>——action 參數也包含在內。於是呼叫端沒帶該參數時
/// 不會取預設值，而是被 [ApiController] 的自動驗證擋成 400「xxx 為必填欄位」，而且回應是
/// ProblemDetails（沒有 message），手機端只看得到「操作失敗(400)」。</para>
///
/// <para>2026-10-05 手機實測：首件／巡檢頁 <c>GET ipqc/spec?dispatchId=…</c> 不帶 lotSize，
/// 規格從上線起就沒載入過。控制器單元測試直接呼叫方法、繞過模型繫結，所以一直是綠的。
/// 選填參數請寫成可空（<c>decimal? lotSize = null</c>）再於方法內給預設值。</para>
/// </summary>
public class OptionalValueTypeParameterTests
{
    [Fact]
    public void ApiController參數_非可空實值型別不可帶預設值()
    {
        var offenders = typeof(Program).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract
                        && t.GetCustomAttribute<ApiControllerAttribute>(inherit: true) != null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttribute<NonActionAttribute>() == null)
                .SelectMany(m => m.GetParameters()
                    .Where(p => p.ParameterType.IsValueType
                                && Nullable.GetUnderlyingType(p.ParameterType) == null
                                && p.HasDefaultValue
                                && p.ParameterType != typeof(CancellationToken)
                                && p.GetCustomAttribute<FromRouteAttribute>() == null)
                    .Select(p => $"{t.Name}.{m.Name}({p.ParameterType.Name} {p.Name})")))
            .ToList();

        Assert.True(offenders.Count == 0,
            "下列參數沒帶時會被當成必填回 400，請改成可空型別：\n  " + string.Join("\n  ", offenders));
    }
}

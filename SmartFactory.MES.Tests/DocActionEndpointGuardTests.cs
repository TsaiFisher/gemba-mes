using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using SmartFactory.MES.Tests.Services;

namespace SmartFactory.MES.Tests;

/// <summary>
/// 單據面板的 <c>data-doc-action="X"</c> 按鈕，畫面 JS 一律送到 <c>/{Controller}/X/{id}</c>——
/// 同名 Controller 上必須真的有 X 這個 action。
///
/// <para>沒有時按下去是 404，<c>r.json()</c> 解析失敗被吞掉：不跳訊息、單據不動，看起來像「按了沒反應」。
/// 2026-11-04 E2E 抓到同廠調撥的「過帳」鈕送 <c>Post</c>、Controller 卻叫 <c>Confirm</c>
/// （自 6 月以前就如此）；沒設簽核流程時存檔即過帳、根本看不到這顆鈕，所以一直沒人發現。</para>
/// </summary>
public class DocActionEndpointGuardTests
{
    [Fact]
    public void 單據動作鈕送去的action在Controller上都存在()
    {
        var root = ManualPaths.RepoRoot();
        var files = new[] { "Modules", "Views" }
            .Select(d => Path.Combine(root, d)).Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cshtml", SearchOption.AllDirectories))
            .ToList();
        Assert.True(files.Count > 100, $"只掃到 {files.Count} 個 cshtml，掃描路徑已失效。");

        var controllers = typeof(Program).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract && t.Name.EndsWith("Controller"))
            .GroupBy(t => t.Name[..^"Controller".Length])
            .ToDictionary(g => g.Key, g => g.ToList());

        var offenders = new List<string>();
        var probed = 0;
        foreach (var f in files)
        {
            var actions = Regex.Matches(File.ReadAllText(f), "data-doc-action=\"([A-Za-z]+)\"")
                .Select(m => m.Groups[1].Value).Distinct().ToList();
            if (actions.Count == 0) continue;

            var name = Path.GetFileName(Path.GetDirectoryName(f)!);
            if (!controllers.TryGetValue(name, out var types))
            {
                offenders.Add($"{Path.GetRelativePath(root, f)}：找不到 {name}Controller");
                continue;
            }
            foreach (var a in actions)
            {
                probed++;
                var exists = types.Any(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Any(m => m.Name == a || m.GetCustomAttribute<ActionNameAttribute>()?.Name == a));
                if (!exists) offenders.Add($"{Path.GetRelativePath(root, f)}：{name}Controller 沒有 {a}");
            }
        }
        Assert.True(probed >= 3, $"只找到 {probed} 個單據動作鈕，比對方式已失效。");
        Assert.True(offenders.Count == 0,
            "下列單據動作鈕送去的 action 不存在（按下去 404、畫面無反應）：\n  " + string.Join("\n  ", offenders));
    }
}

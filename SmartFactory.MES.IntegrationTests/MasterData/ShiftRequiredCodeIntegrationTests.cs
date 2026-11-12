using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.IntegrationTests.MFG;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.MasterData
{
    /// <summary>
    /// 班制代碼必填的伺服端守門（2026-11-09 nightly ShiftSmokeTests 回報「清空代碼仍存檔成功」）：
    /// 走真的 model binding／[Bind]／AntiForgery，代碼空白必須回表單 partial，且不得寫入。
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class ShiftRequiredCodeIntegrationTests
    {
        private readonly AuthedScheduleFactory _factory;
        public ShiftRequiredCodeIntegrationTests(AuthedScheduleFactory factory) => _factory = factory;

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task Create_BlankShiftCode_IsRejected_AndNothingSaved(string code)
        {
            var client = _factory.CreateClient();
            var html = await client.GetStringAsync("/Shift");
            var m = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
            Assert.True(m.Success, "未能取得 AntiForgery token");

            var name = "IT-REQ-" + Guid.NewGuid().ToString("N")[..8];
            var req = new HttpRequestMessage(HttpMethod.Post, "/Shift/Create")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["ShiftCode"] = code, ["ShiftName"] = name,
                    ["StartTime"] = "08:00", ["EndTime"] = "17:00", ["IsActive"] = "true",
                }),
            };
            req.Headers.Add("RequestVerificationToken", m.Groups[1].Value);
            req.Headers.Add("X-Requested-With", "XMLHttpRequest");
            var res = await client.SendAsync(req);
            var body = await res.Content.ReadAsStringAsync();

            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var saved = await db.Shifts.IgnoreQueryFilters().Where(s => s.ShiftName == name).ToListAsync();
            if (saved.Count > 0) { db.Shifts.RemoveRange(saved); await db.SaveChangesAsync(); }

            Assert.Empty(saved);
            Assert.DoesNotContain("\"success\":true", body);
        }
    }
}

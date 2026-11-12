using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace SmartFactory.MES.UITests.SYS
{
    /// <summary>
    /// 簽核範本的步驟維護（<c>/ApprovalTemplate</c> 的側面板與「修改步驟」視窗）。
    ///
    /// <para><b>為什麼非瀏覽器層不可</b>：這一頁的行為幾乎全靠 JS，而且是
    /// <b>被 DOMPurify 洗過之後</b>的 JS——面板內容是 <c>fetch</c> 回來以後
    /// <c>DOMPurify.sanitize()</c> 再塞進 DOM 的，<c>onchange</c>／<c>onclick</c>
    /// 會被整個移除（Index.cshtml 的註解就是這樣寫的），所以改用事件委派重綁。
    /// 「重綁有沒有真的接上」是整合測試看不到的：它拿到的是 partial 的原始 HTML，
    /// 那份 HTML 無論如何都長得一樣。</para>
    ///
    /// <para>另外，兩個目標欄位的顯示／隱藏走的是
    /// <c>classList.toggle('u-display-none', …)</c>。同一批畫面裡已經出現過
    /// 三次「class 隱藏 + style.display 切換」的回歸（QCR 跳批欄位、手機 IPQC
    /// 警告橫幅、作業類型簽核設定的範本欄），症狀一律是<b>那一欄永遠打不開，
    /// 而且不報錯</b>。Playwright 依 computed style 判定，<c>!important</c> 也擋不住它，
    /// 是唯一分得出「藏起來」與「根本沒渲染」的一層。</para>
    ///
    /// <para>🚨 全部唯讀：不新增步驟、不刪步驟、不按上移下移、不存檔。
    /// :5110 與 ProjectZ 庫是多 session 共用的。</para>
    /// </summary>
    [Collection("UI Tests")]
    public class ApprovalTemplateStepUiTests
    {
        private readonly UiTestFixture _fixture;
        private readonly ITestOutputHelper _out;

        // ⚠️ ApproverType 是 **1-based** 的（Supervisor=1／Role=2／User=3），
        //    不是 0-based。第一版照慣例寫成 0/1/2，結果是
        //    SelectOptionAsync 報「did not find some options」，
        //    而「修改步驟」那支更陰險——它照樣選得到值（2 存在，只是那是 Role 不是 User），
        //    斷言於是比對了錯的一欄。寫成具名常數就是為了不再猜。
        private const int Supervisor = 1;
        private const int RoleApprover = 2;
        private const int UserApprover = 3;

        public ApprovalTemplateStepUiTests(UiTestFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _out = output;
        }

        /// <summary>
        /// 開一個「有步驟」的範本的側面板。
        ///
        /// <para>刻意不寫死範本 Id：開發庫的範本是人手建的，
        /// nightly 的乾淨測試庫則是 <c>seed-e2e-fixture.sql</c> ⑰ 段建的
        /// （<c>E2E-簽核範本</c>，三個步驟各一種簽核者類型），兩邊 Id 不一樣。
        /// 逐列試到找出有步驟的那一個為止；<b>一個都找不到就讓測試紅</b>——
        /// 「沒有資料所以每一項都通過」與「功能正常」長得一模一樣，
        /// 那正是這份測試最需要避免的假綠。</para>
        /// </summary>
        private async Task<(IPage page, string templateLabel)> OpenTemplateWithStepsAsync()
        {
            var page = await _fixture.NewPageAsync();
            await _fixture.SignInAsync(page);
            await page.GotoAsync($"{_fixture.BaseUrl}/ApprovalTemplate?searched=true",
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            var detailButtons = page.Locator("button[data-action='openPanel'][data-url*='/Details/']");
            var count = await detailButtons.CountAsync();
            Assert.True(count > 0,
                "/ApprovalTemplate 一個範本都沒有——種子沒建起來，這份測試沒有任何證據力。");

            for (int i = 0; i < count; i++)
            {
                var button = detailButtons.Nth(i);
                var url = await button.GetAttributeAsync("data-url") ?? "?";
                await button.ClickAsync();

                // 等新增步驟表單掛上來——它是每一份範本都有的，步驟清單則可能是空的。
                await Assertions.Expect(page.Locator("#crudPanelBody #approverType"))
                    .ToBeVisibleAsync(new() { Timeout = 15_000 });

                var steps = await page.Locator("#crudPanelBody [data-edit-step]").CountAsync();
                _out.WriteLine($"範本 {url} → 步驟 {steps} 個");
                if (steps > 0) return (page, url);

                await page.Locator("#crudPanel [data-bs-dismiss='offcanvas']").First.ClickAsync();
                await Assertions.Expect(page.Locator("#crudPanelBody #approverType"))
                    .Not.ToBeVisibleAsync(new() { Timeout = 10_000 });
            }

            Assert.Fail($"{count} 個範本沒有一個帶步驟——步驟相關的斷言全部無法執行。");
            throw new InvalidOperationException("unreachable");
        }

        /// <summary>
        /// 新增步驟表單：換簽核者類型時，該露的那一欄才露出來。
        ///
        /// <para>三種都測，而且<b>兩個欄位各驗一次</b>。只驗「選角色時角色欄看得見」的話，
        /// 一個永遠顯示的使用者欄照樣通過；只測一種類型的話，toggle 寫成常數也全綠。</para>
        ///
        /// <para>主管簽核那一格特別重要——它是<b>兩欄都不該出現</b>的唯一情形。
        /// 這也是這個畫面最容易被誤解的地方：主管型的對象是靠簽核當下的組織階層算出來的，
        /// 表單上本來就沒有東西可以指定。</para>
        /// </summary>
        [Theory]
        [InlineData(Supervisor, false, false)]
        [InlineData(RoleApprover, true, false)]
        [InlineData(UserApprover, false, true)]
        public async Task 換簽核者類型時_該露的那一欄才露出來(
            int approverType, bool roleDivVisible, bool userDivVisible)
        {
            var (page, _) = await OpenTemplateWithStepsAsync();

            await page.Locator("#crudPanelBody #approverType")
                .SelectOptionAsync(new SelectOptionValue { Value = approverType.ToString() });

            var roleDiv = page.Locator("#crudPanelBody #targetRoleDiv");
            var userDiv = page.Locator("#crudPanelBody #targetUserDiv");

            if (roleDivVisible) await Assertions.Expect(roleDiv).ToBeVisibleAsync();
            else await Assertions.Expect(roleDiv).Not.ToBeVisibleAsync();

            if (userDivVisible) await Assertions.Expect(userDiv).ToBeVisibleAsync();
            else await Assertions.Expect(userDiv).Not.ToBeVisibleAsync();
        }

        /// <summary>
        /// 「修改步驟」視窗帶回來的，必須是<b>那一列自己的</b>簽核者類型與對象。
        ///
        /// <para>🚨 為什麼這支值得寫：<c>openEditStepModal</c> 的選單選項是從側面板
        /// <b>複製</b>過來的（<c>srcApprover.innerHTML</c>）。複製失敗的話
        /// <c>editApprover.value = data.approverType</c> 會靜靜地把值設成空字串
        /// ——指定了不存在的 option，瀏覽器不報錯——送出時 <c>approverType</c> 是空的，
        /// 後端繫結成 <c>0</c>，也就是 <b>Supervisor</b>。
        /// 淨效果是：改一個「指定角色」的步驟，存完變成「主管簽核」，而且沒有任何訊息。</para>
        ///
        /// <para>只開視窗、比對帶進來的值，<b>不按儲存</b>。</para>
        /// </summary>
        [Fact]
        public async Task 修改步驟的視窗_帶回的是那一列自己的簽核者類型()
        {
            var (page, _) = await OpenTemplateWithStepsAsync();

            var editButtons = page.Locator("#crudPanelBody [data-edit-step]");
            var stepCount = await editButtons.CountAsync();

            for (int i = 0; i < stepCount; i++)
            {
                var button = editButtons.Nth(i);
                var expectedType = await button.GetAttributeAsync("data-approver-type") ?? "";
                var expectedRole = await button.GetAttributeAsync("data-target-role-id") ?? "";
                var expectedUser = await button.GetAttributeAsync("data-target-user-id") ?? "";

                await button.ClickAsync();
                await Assertions.Expect(page.Locator("#editStepModal"))
                    .ToBeVisibleAsync(new() { Timeout = 10_000 });

                var actualType = await page.Locator("#editApproverType").InputValueAsync();
                _out.WriteLine($"步驟 {i + 1}：列上是 {expectedType}，視窗裡是 {actualType}");
                Assert.Equal(expectedType, actualType);

                // 選單真的有選項——空選單也會讓 InputValueAsync 回空字串，
                // 而那時 expectedType 若剛好是 Supervisor（"0"）就會假綠。
                Assert.True(await page.Locator("#editApproverType option").CountAsync() >= 3,
                    "修改視窗的簽核者類型選單沒有複製到選項——存檔會把步驟悄悄改成主管簽核。");

                // 該露的那一欄，以及帶回來的對象。
                var roleDiv = page.Locator("#editTargetRoleDiv");
                var userDiv = page.Locator("#editTargetUserDiv");
                if (expectedType == RoleApprover.ToString())
                {
                    await Assertions.Expect(roleDiv).ToBeVisibleAsync();
                    await Assertions.Expect(userDiv).Not.ToBeVisibleAsync();
                    Assert.Equal(expectedRole, await page.Locator("#editTargetRoleId").InputValueAsync());
                }
                else if (expectedType == UserApprover.ToString())
                {
                    await Assertions.Expect(userDiv).ToBeVisibleAsync();
                    await Assertions.Expect(roleDiv).Not.ToBeVisibleAsync();
                    Assert.Equal(expectedUser, await page.Locator("#editTargetUserId").InputValueAsync());
                }
                else
                {
                    await Assertions.Expect(roleDiv).Not.ToBeVisibleAsync();
                    await Assertions.Expect(userDiv).Not.ToBeVisibleAsync();
                }

                await page.Locator("#editStepModal [data-bs-dismiss='modal']").First.ClickAsync();
                await Assertions.Expect(page.Locator("#editStepModal"))
                    .Not.ToBeVisibleAsync(new() { Timeout = 10_000 });
            }
        }

        /// <summary>
        /// 步驟順序：序號從 1 連號排下來，而且第一步不能再往上、最後一步不能再往下。
        ///
        /// <para>兩端的 disabled 是 <c>@(step.Sequence == 1 ? "disabled" : "")</c> 與
        /// <c>@(step.Sequence == sortedSteps.Count ? "disabled" : "")</c> 算出來的——
        /// 典型的差一錯誤落點，而按下去的後果是 <c>MoveStep</c> 真的改資料。
        /// 這裡<b>只看有沒有 disabled，不按</b>。</para>
        /// </summary>
        [Fact]
        public async Task 步驟序號連號_而且兩端的移動鈕是關掉的()
        {
            var (page, _) = await OpenTemplateWithStepsAsync();

            var badges = page.Locator("#crudPanelBody .list-group-item .badge");
            var n = await badges.CountAsync();
            Assert.True(n > 0, "步驟清單渲染不出序號徽章。");

            for (int i = 0; i < n; i++)
            {
                var text = (await badges.Nth(i).InnerTextAsync()).Trim();
                Assert.Equal((i + 1).ToString(), text);
            }
            _out.WriteLine($"步驟序號：1..{n}");

            // 上移／下移各自是一個 form，順序是「上、下」交錯排的。
            var upButtons = page.Locator("#crudPanelBody form[action*='MoveStep'] button[title]")
                .Filter(new() { Has = page.Locator("i.bi-arrow-up") });
            var downButtons = page.Locator("#crudPanelBody form[action*='MoveStep'] button[title]")
                .Filter(new() { Has = page.Locator("i.bi-arrow-down") });

            Assert.Equal(n, await upButtons.CountAsync());
            Assert.Equal(n, await downButtons.CountAsync());

            await Assertions.Expect(upButtons.First).ToBeDisabledAsync();
            await Assertions.Expect(downButtons.Last).ToBeDisabledAsync();

            if (n > 1)
            {
                // 中間那些必須是開著的，否則「全部關掉」也會讓上面兩條斷言通過。
                await Assertions.Expect(downButtons.First).ToBeEnabledAsync();
                await Assertions.Expect(upButtons.Last).ToBeEnabledAsync();
            }
        }
    }
}

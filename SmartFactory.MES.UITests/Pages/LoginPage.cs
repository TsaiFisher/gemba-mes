using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.Pages
{
    public class LoginPage : BasePage
    {
        public LoginPage(IPage page, string baseUrl) : base(page, baseUrl) { }

        public async Task GotoAsync()
        {
            await base.GotoAsync("/Identity/Account/Login");
        }

        public async Task LoginAsync(string email, string password)
        {
            await _page.FillAsync("input[name='Input.Email']", email);
            await _page.FillAsync("input[name='Input.Password']", password);
            // 用 #login-submit 避免 button[type='submit'] 誤抓到 navbar 的其他 submit 按鈕
            await _page.ClickAsync("#login-submit");
        }

        public async Task<bool> IsErrorMessageVisibleAsync()
        {
             // Adjust selector based on actual validation summary or error message element
            return await _page.IsVisibleAsync(".validation-summary-errors") || 
                   await _page.IsVisibleAsync(".field-validation-error");
        }
    }
}

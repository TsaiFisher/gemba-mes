using Microsoft.Extensions.Localization;

namespace SmartFactory.MES.Services
{
    public class UserSettingsMenuContributor : IMenuContributor
    {
        private readonly IStringLocalizer<SharedResource> _localizer;

        public int Order => 999;

        public UserSettingsMenuContributor(IStringLocalizer<SharedResource> localizer)
        {
            _localizer = localizer;
        }

        public List<MenuItem> GetMenuItems()
        {
            return new List<MenuItem>
            {
                new MenuItem { Title = _localizer["UserSettings"], Controller = "UserSettings", Action = "Index", Icon = "bi-person-circle", MenuKey = "UserSettings" }
            };
        }
    }
}

using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using SmartFactory.MES.Controllers;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests
{
    /// <summary>
    /// 系統參數畫面的 Core 部分（公司名稱／簡稱、Logo）——2026-10-20 總檢時發現兩個 repo 都沒有測試。
    /// Logo 上傳有五道防護：大小、MIME、副檔名白名單（排除 SVG）、檔頭簽章、刪檔不出 uploads；
    /// 簽章本身由 <see cref="ImageSignatureValidatorTests"/> 驗，這裡驗 Controller 有沒有真的接上。
    /// 目前的 Logo 一律以資料庫存的值為準，表單隱藏欄位 LogoPath 不採用（可被竄改）。
    /// </summary>
    public class SystemParamControllerTests : TestBase, IDisposable
    {
        private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 };

        private readonly Mock<ISystemParamService> _svc = new();
        private readonly string _webRoot = Path.Combine(Path.GetTempPath(), "sp-tests-" + Guid.NewGuid().ToString("N"));
        private Dictionary<string, string?>? _saved;
        private string? _storedLogo;

        public SystemParamControllerTests()
        {
            Directory.CreateDirectory(Path.Combine(_webRoot, "uploads"));
            _svc.Setup(s => s.SetBatchAsync(It.IsAny<Dictionary<string, string?>>()))
                .Callback<Dictionary<string, string?>>(d => _saved = d)
                .Returns(Task.CompletedTask);
            _svc.Setup(s => s.GetAllAsync())
                .ReturnsAsync(() => new Dictionary<string, string?> { [SystemParamKeys.CompanyLogo] = _storedLogo });
        }

        public new void Dispose()
        {
            try { Directory.Delete(_webRoot, recursive: true); } catch { }
            base.Dispose();
        }

        private SystemParamController Ctrl()
        {
            var env = new Mock<IWebHostEnvironment>();
            env.SetupGet(e => e.WebRootPath).Returns(_webRoot);
            return new SystemParamController(_svc.Object, env.Object)
            {
                TempData = new TempDataDictionary(new DefaultHttpContext(), Mock.Of<ITempDataProvider>()),
            };
        }

        private static IFormFile File(string name, string contentType, byte[] content)
        {
            var ms = new MemoryStream(content);
            return new FormFile(ms, 0, content.Length, "LogoFile", name)
            {
                Headers = new HeaderDictionary(), ContentType = contentType,
            };
        }

        private string Upload(string name) { var p = Path.Combine(_webRoot, "uploads", name); System.IO.File.WriteAllBytes(p, Png); return p; }

        [Fact]
        public void Controller_GatedByPermissionMatrix()
        {
            // 2026-11-12 起依權限矩陣：讀取 SystemParam、存檔要 Update（原本寫死 Admin，選單看得到卻點不進去）
            Assert.NotNull(typeof(SystemParamController).GetCustomAttribute<AuthorizeAttribute>());
            var read = typeof(SystemParamController).GetCustomAttribute<SmartFactory.MES.Services.RequireMenuPermissionAttribute>();
            Assert.Equal(("SystemParam", SmartFactory.MES.Services.MenuPermissionAction.Read), (read!.MenuKey, read.Action));
            var save = typeof(SystemParamController).GetMethod(nameof(SystemParamController.Save))!
                .GetCustomAttribute<SmartFactory.MES.Services.RequireMenuPermissionAttribute>();
            Assert.Equal(SmartFactory.MES.Services.MenuPermissionAction.Update, save!.Action);
        }

        [Fact]
        public async Task Save_TrimsNames_AndKeepsExistingLogoWhenNoUpload()
        {
            _storedLogo = "/uploads/logo-old.png";
            var result = await Ctrl().Save(new SystemParamViewModel
            {
                CompanyNameZh = "  智慧工廠  ", CompanyNameEn = " Smart ", CompanyShortZh = " 智工 ", CompanyShortEn = null,
            });

            Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("智慧工廠", _saved![SystemParamKeys.CompanyNameZh]);
            Assert.Equal("Smart", _saved[SystemParamKeys.CompanyNameEn]);
            Assert.Equal("智工", _saved[SystemParamKeys.CompanyShortZh]);
            Assert.Null(_saved[SystemParamKeys.CompanyShortEn]);
            Assert.Equal("/uploads/logo-old.png", _saved[SystemParamKeys.CompanyLogo]);
        }

        [Fact]
        public async Task Save_ValidPng_StoredUnderUploads_AndOldLogoDeleted()
        {
            var old = Upload("logo-old.png");
            _storedLogo = "/uploads/logo-old.png";

            var result = await Ctrl().Save(new SystemParamViewModel
            {
                LogoFile = File("brand.PNG", "image/png", Png),
            });

            Assert.IsType<RedirectToActionResult>(result);
            var path = _saved![SystemParamKeys.CompanyLogo]!;
            Assert.StartsWith("/uploads/logo-", path);
            Assert.EndsWith(".png", path);
            Assert.True(System.IO.File.Exists(Path.Combine(_webRoot, path.TrimStart('/'))));
            Assert.False(System.IO.File.Exists(old));
        }

        /// <summary>每一道擋下都必須「不存檔」：原本的 Logo 與公司名稱一個字都不動。</summary>
        [Theory]
        [InlineData("logo.png", "text/plain", false, "Msg_LogoNotImage")]
        [InlineData("logo.svg", "image/svg+xml", false, "Msg_LogoBadExtension")]   // SVG 可內嵌 script
        [InlineData("logo.exe", "image/png", false, "Msg_LogoBadExtension")]
        [InlineData("logo.png", "image/png", true, "Msg_LogoBadContent")]           // 副檔名與 MIME 都對、內容不是影像
        public async Task Save_RejectedLogo_ReturnsForm_AndPersistsNothing(string name, string mime, bool fakeContent, string key)
        {
            var content = fakeContent ? "<script>alert(1)</script>"u8.ToArray() : Png;

            var result = await Ctrl().Save(new SystemParamViewModel
            {
                CompanyNameZh = "改了", LogoFile = File(name, mime, content),
            });

            var view = Assert.IsType<ViewResult>(result);
            Assert.Equal("Index", view.ViewName);
            Assert.Null(_saved);
            Assert.Contains(CoreText.Get(key),
                view.ViewData.ModelState["LogoFile"]!.Errors.Select(e => e.ErrorMessage));
        }

        [Fact]
        public async Task Save_LogoOver2MB_Rejected()
        {
            var big = new byte[2 * 1024 * 1024 + 1];
            Png.CopyTo(big, 0);

            var view = Assert.IsType<ViewResult>(await Ctrl().Save(new SystemParamViewModel
            {
                LogoFile = File("big.png", "image/png", big),
            }));
            Assert.Null(_saved);
            Assert.Contains(CoreText.Get("Msg_LogoTooLarge"),
                view.ViewData.ModelState["LogoFile"]!.Errors.Select(e => e.ErrorMessage));
        }

        [Fact]
        public async Task Save_LogoExactly2MB_Accepted()
        {
            var edge = new byte[2 * 1024 * 1024];
            Png.CopyTo(edge, 0);

            Assert.IsType<RedirectToActionResult>(await Ctrl().Save(new SystemParamViewModel
            {
                LogoFile = File("edge.png", "image/png", edge),
            }));
        }

        [Fact]
        public async Task Save_RemoveLogo_DeletesFile_AndClearsParam()
        {
            var old = Upload("logo-old.png");
            _storedLogo = "/uploads/logo-old.png";

            await Ctrl().Save(new SystemParamViewModel { RemoveLogo = true });

            Assert.False(System.IO.File.Exists(old));
            Assert.Null(_saved![SystemParamKeys.CompanyLogo]);
        }

        /// <summary>
        /// 第二道防護：即使資料庫裡存的路徑本身指到 uploads 之外（直接改庫、舊資料），刪檔也不出 uploads。
        /// </summary>
        [Theory]
        [InlineData("/../outside.txt")]
        [InlineData("/uploads/../outside.txt")]
        public async Task Save_RemoveLogo_StoredPathOutsideUploads_DoesNotDelete(string stored)
        {
            var outside = Path.Combine(_webRoot, "outside.txt");
            System.IO.File.WriteAllText(outside, "keep");
            _storedLogo = stored;

            await Ctrl().Save(new SystemParamViewModel { RemoveLogo = true });

            Assert.True(System.IO.File.Exists(outside));
        }

        /// <summary>
        /// 回歸（2026-10-20 探針實證）：竄改隱藏欄位 LogoPath 指向 uploads 內的別的檔案，
        /// 移除 Logo 時原本會把它刪掉；現在只刪資料庫記錄的那張。
        /// </summary>
        [Fact]
        public async Task Save_RemoveLogo_TamperedHiddenField_DeletesOnlyStoredLogo()
        {
            var other = Upload("other.png");
            var mine  = Upload("logo-mine.png");
            _storedLogo = "/uploads/logo-mine.png";

            await Ctrl().Save(new SystemParamViewModel { LogoPath = "/uploads/other.png", RemoveLogo = true });

            Assert.True(System.IO.File.Exists(other));
            Assert.False(System.IO.File.Exists(mine));
        }

        /// <summary>
        /// 回歸（2026-10-20 探針實證）：沒上傳新檔時，竄改的 LogoPath（例如外部網址）原本會原樣存成 Logo、
        /// 出現在每一頁版頭；現在一律存回資料庫原值。上傳新檔時也不得依竄改值去刪別的檔。
        /// </summary>
        [Theory]
        [InlineData("https://evil.example/pixel.png")]
        [InlineData("/uploads/other.png")]
        public async Task Save_TamperedHiddenField_IsIgnored(string tampered)
        {
            var other = Upload("other.png");
            _storedLogo = "/uploads/logo-mine.png";

            await Ctrl().Save(new SystemParamViewModel { LogoPath = tampered });
            Assert.Equal("/uploads/logo-mine.png", _saved![SystemParamKeys.CompanyLogo]);

            _saved = null;
            await Ctrl().Save(new SystemParamViewModel { LogoPath = tampered, LogoFile = File("n.png", "image/png", Png) });
            Assert.True(System.IO.File.Exists(other));
            Assert.StartsWith("/uploads/logo-", _saved![SystemParamKeys.CompanyLogo]);
        }
    }

    /// <summary>公司名稱／簡稱的語系與退回順序（版頭、列印、隱私頁共用）。</summary>
    public class SystemParamCompanyNameTests : TestBase
    {
        private SystemParamService Svc(params (string Key, string? Value)[] rows)
        {
            foreach (var (k, v) in rows) _context.SystemParams.Add(new SystemParam { Key = k, Value = v });
            _context.SaveChanges();
            return new SystemParamService(_context, new MemoryCache(new MemoryCacheOptions()));
        }

        [Theory]
        // 簡稱：本語系簡稱 → 另一語系簡稱 → 本語系全名 → 另一語系全名 → 預設
        [InlineData(false, "智工", "SF", "智慧工廠", "Smart Factory", "智工")]
        [InlineData(true,  "智工", "SF", "智慧工廠", "Smart Factory", "SF")]
        [InlineData(true,  "智工", " ",  "智慧工廠", "Smart Factory", "智工")]          // 空白等同沒填
        [InlineData(false, null,   null, "智慧工廠", "Smart Factory", "智慧工廠")]
        [InlineData(true,  null,   null, "智慧工廠", null,            "智慧工廠")]
        [InlineData(false, null,   null, null,       null,            "SmartFactory")]
        public async Task CompanyShort_FallbackChain(bool en, string? sZh, string? sEn, string? zh, string? enName, string expected)
        {
            var svc = Svc((SystemParamKeys.CompanyShortZh, sZh), (SystemParamKeys.CompanyShortEn, sEn),
                          (SystemParamKeys.CompanyNameZh, zh), (SystemParamKeys.CompanyNameEn, enName));
            Assert.Equal(expected, await svc.GetCompanyShortAsync(en));
        }
    }
}

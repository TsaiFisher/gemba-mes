using Microsoft.AspNetCore.Mvc;
using SmartFactory.MES.Controllers;
using SmartFactory.MES.Models;
using Xunit;

namespace SmartFactory.MES.Tests
{
    public class DocumentApprovalConfigControllerTests : TestBase
    {
        private readonly DocumentApprovalConfigController _controller;

        public DocumentApprovalConfigControllerTests()
        {
            _controller = new DocumentApprovalConfigController(_context, GetMockExportService().Object, GetMockDateTimeService().Object);
            _controller.ControllerContext = GetControllerContext();
        }

        // ── Index ─────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Index_NoSearch_ReturnsNullModel()
        {
            var result = await _controller.Index(null, null, null, searched: false);

            var viewResult = Assert.IsType<ViewResult>(result);
            Assert.Null(viewResult.Model);
        }

        [Fact]
        public async Task Index_SearchByDocumentType_ReturnsMatchingOnly()
        {
            _context.DocumentApprovalConfigs.AddRange(
                new DocumentApprovalConfig { Id = "C1", DocumentType = "PurchaseOrder", DocumentTypeName = "請購單" },
                new DocumentApprovalConfig { Id = "C2", DocumentType = "WorkOrder",     DocumentTypeName = "工單" }
            );
            await _context.SaveChangesAsync();

            var result = await _controller.Index("Purchase", null, null, searched: true);

            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsAssignableFrom<IEnumerable<DocumentApprovalConfig>>(viewResult.Model);
            Assert.Single(model);
            Assert.Equal("C1", model.First().Id);
        }

        [Fact]
        public async Task Index_SearchByDocumentTypeName_ReturnsMatchingOnly()
        {
            _context.DocumentApprovalConfigs.AddRange(
                new DocumentApprovalConfig { Id = "C1", DocumentType = "PurchaseOrder", DocumentTypeName = "請購單" },
                new DocumentApprovalConfig { Id = "C2", DocumentType = "WorkOrder",     DocumentTypeName = "工單" }
            );
            await _context.SaveChangesAsync();

            var result = await _controller.Index("工單", null, null, searched: true);

            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsAssignableFrom<IEnumerable<DocumentApprovalConfig>>(viewResult.Model);
            Assert.Single(model);
            Assert.Equal("C2", model.First().Id);
        }

        [Fact]
        public async Task Index_EmptySearch_ReturnsAll()
        {
            _context.DocumentApprovalConfigs.AddRange(
                new DocumentApprovalConfig { Id = "C1", DocumentType = "PurchaseOrder", DocumentTypeName = "請購單" },
                new DocumentApprovalConfig { Id = "C2", DocumentType = "WorkOrder",     DocumentTypeName = "工單" }
            );
            await _context.SaveChangesAsync();

            var result = await _controller.Index(null, null, null, searched: true);

            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsAssignableFrom<IEnumerable<DocumentApprovalConfig>>(viewResult.Model);
            Assert.Equal(2, model.Count());
        }

        // ── Create ────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Create_POST_ValidModel_SavesAndRedirects()
        {
            var config = new DocumentApprovalConfig
            {
                Id = "C1",
                DocumentType = "PurchaseOrder",
                DocumentTypeName = "請購單",
                IsApprovalRequired = false
            };

            var result = await _controller.Create(config);

            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(DocumentApprovalConfigController.Index), redirect.ActionName);
            Assert.NotNull(await _context.DocumentApprovalConfigs.FindAsync("C1"));
        }

        [Fact]
        public async Task Create_POST_IsApprovalRequired_False_ClearsTemplateId()
        {
            var config = new DocumentApprovalConfig
            {
                Id = "C1",
                DocumentType = "PurchaseOrder",
                DocumentTypeName = "請購單",
                IsApprovalRequired = false,
                ApprovalTemplateId = "T1"   // 應被清除
            };

            await _controller.Create(config);

            var saved = await _context.DocumentApprovalConfigs.FindAsync("C1");
            Assert.Null(saved!.ApprovalTemplateId);
        }

        [Fact]
        public async Task Create_POST_AjaxRequest_ReturnsSuccessJson()
        {
            SetAjaxHeader();
            var config = new DocumentApprovalConfig
            {
                Id = "C1",
                DocumentType = "PurchaseOrder",
                DocumentTypeName = "請購單"
            };

            var result = await _controller.Create(config);

            var json = Assert.IsType<JsonResult>(result);
            dynamic data = json.Value!;
            Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        }

        // ── Edit ──────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Edit_GET_ExistingConfig_ReturnsView()
        {
            _context.DocumentApprovalConfigs.Add(
                new DocumentApprovalConfig { Id = "C1", DocumentType = "PO", DocumentTypeName = "請購單" });
            await _context.SaveChangesAsync();

            var result = await _controller.Edit("C1");

            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<DocumentApprovalConfig>(viewResult.Model);
            Assert.Equal("C1", model.Id);
        }

        [Fact]
        public async Task Edit_GET_NotFound_Returns404()
        {
            var result = await _controller.Edit("nonexistent");
            Assert.IsType<NotFoundResult>(result);
        }

        [Fact]
        public async Task Edit_POST_ValidModel_UpdatesFields()
        {
            _context.DocumentApprovalConfigs.Add(
                new DocumentApprovalConfig { Id = "C1", DocumentType = "PO", DocumentTypeName = "老名稱", IsApprovalRequired = false });
            await _context.SaveChangesAsync();
            _context.ChangeTracker.Clear();

            var updated = new DocumentApprovalConfig
            {
                Id = "C1",
                DocumentType = "PO",
                DocumentTypeName = "新名稱",
                IsApprovalRequired = false
            };

            var result = await _controller.Edit("C1", updated);

            Assert.IsType<RedirectToActionResult>(result);
            var saved = await _context.DocumentApprovalConfigs.FindAsync("C1");
            Assert.Equal("新名稱", saved!.DocumentTypeName);
        }

        [Fact]
        public async Task Edit_POST_IdMismatch_Returns404()
        {
            var updated = new DocumentApprovalConfig { Id = "C99", DocumentType = "PO", DocumentTypeName = "X" };
            var result = await _controller.Edit("C1", updated);
            Assert.IsType<NotFoundResult>(result);
        }

        [Fact]
        public async Task Edit_POST_IsApprovalRequired_False_ClearsTemplateId()
        {
            _context.DocumentApprovalConfigs.Add(
                new DocumentApprovalConfig { Id = "C1", DocumentType = "PO", DocumentTypeName = "請購單", IsApprovalRequired = true, ApprovalTemplateId = "T1" });
            await _context.SaveChangesAsync();
            _context.ChangeTracker.Clear();

            var updated = new DocumentApprovalConfig
            {
                Id = "C1",
                DocumentType = "PO",
                DocumentTypeName = "請購單",
                IsApprovalRequired = false,
                ApprovalTemplateId = "T1"   // 應被清除
            };

            await _controller.Edit("C1", updated);

            var saved = await _context.DocumentApprovalConfigs.FindAsync("C1");
            Assert.Null(saved!.ApprovalTemplateId);
        }

        // ── 「要簽核」⇒「一定要有範本」 ────────────────────────────────────────────
        //
        // 🚨 這三支補的是一個**整份檔案從來沒踩過的狀態**：在此之前，本檔每一支
        //    Create/Edit 測試的 IsApprovalRequired 都是 false 或不設，所以
        //    「要簽核」這條路徑一次都沒跑過，缺少驗證也就沒有任何訊號。
        //
        //    為什麼這個缺口特別危險：下游一律寫成
        //    IsApprovalRequired == true && !IsNullOrEmpty(ApprovalTemplateId)，
        //    「要簽核但沒範本」被當成未設線上簽核 → 單據直接 Approved。
        //    也就是說，這種設定不會壞給人看，它會**安靜地把簽核關掉**。

        [Fact]
        public async Task Create_POST_ApprovalRequiredWithoutTemplate_IsRejected()
        {
            var config = new DocumentApprovalConfig
            {
                Id = "C1",
                DocumentType = "PurchaseOrder",
                DocumentTypeName = "請購單",
                IsApprovalRequired = true,
                ApprovalTemplateId = null
            };

            var result = await _controller.Create(config);

            // 回的是表單而不是轉址，而且**沒有存進去**。
            Assert.IsType<ViewResult>(result);
            Assert.Null(await _context.DocumentApprovalConfigs.FindAsync("C1"));
            Assert.True(_controller.ModelState.ContainsKey(nameof(DocumentApprovalConfig.ApprovalTemplateId)));
        }

        [Fact]
        public async Task Create_POST_ApprovalRequiredWithTemplate_Saves()
        {
            var config = new DocumentApprovalConfig
            {
                Id = "C1",
                DocumentType = "PurchaseOrder",
                DocumentTypeName = "請購單",
                IsApprovalRequired = true,
                ApprovalTemplateId = "T1"
            };

            var result = await _controller.Create(config);

            // 正向也要驗，否則「一律擋掉」這種過度收斂的實作照樣全綠。
            Assert.IsType<RedirectToActionResult>(result);
            var saved = await _context.DocumentApprovalConfigs.FindAsync("C1");
            Assert.Equal("T1", saved!.ApprovalTemplateId);
        }

        [Fact]
        public async Task Edit_POST_ApprovalRequiredWithoutTemplate_IsRejected()
        {
            _context.DocumentApprovalConfigs.Add(
                new DocumentApprovalConfig { Id = "C1", DocumentType = "PO", DocumentTypeName = "請購單", IsApprovalRequired = false });
            await _context.SaveChangesAsync();
            _context.ChangeTracker.Clear();

            var updated = new DocumentApprovalConfig
            {
                Id = "C1",
                DocumentType = "PO",
                DocumentTypeName = "請購單",
                IsApprovalRequired = true,
                ApprovalTemplateId = "   "   // 空白字串也算沒指定
            };

            var result = await _controller.Edit("C1", updated);

            Assert.IsType<ViewResult>(result);
            var saved = await _context.DocumentApprovalConfigs.FindAsync("C1");
            Assert.False(saved!.IsApprovalRequired);   // 原值沒有被改掉
        }

        // ── Delete ────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Delete_ExistingConfig_RemovesIt()
        {
            _context.DocumentApprovalConfigs.Add(
                new DocumentApprovalConfig { Id = "C1", DocumentType = "PO", DocumentTypeName = "請購單" });
            await _context.SaveChangesAsync();

            var result = await _controller.DeleteConfirmed("C1");

            Assert.IsType<RedirectToActionResult>(result);
            Assert.Null(await _context.DocumentApprovalConfigs.FindAsync("C1"));
        }

        [Fact]
        public async Task Delete_AjaxRequest_ReturnsSuccessJson()
        {
            _context.DocumentApprovalConfigs.Add(
                new DocumentApprovalConfig { Id = "C1", DocumentType = "PO", DocumentTypeName = "請購單" });
            await _context.SaveChangesAsync();
            SetAjaxHeader();

            var result = await _controller.DeleteConfirmed("C1");

            var json = Assert.IsType<JsonResult>(result);
            dynamic data = json.Value!;
            Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        }

        [Fact]
        public async Task Delete_NonExistent_StillReturnsRedirect()
        {
            var result = await _controller.DeleteConfirmed("nonexistent");
            Assert.IsType<RedirectToActionResult>(result);
        }

        // ── helpers ───────────────────────────────────────────────────────────────

        private void SetAjaxHeader() =>
            _controller.ControllerContext.HttpContext.Request.Headers["X-Requested-With"] = "XMLHttpRequest";
    }
}

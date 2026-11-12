using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.INV.Models;
using Xunit;

namespace SmartFactory.MES.Tests
{
    /// <summary>
    /// 測試 ApplicationDbContext 的層級式資料授權過濾器（IDataAuthEntity global query filter）。
    /// 使用 ApprovalTemplate 作為測試資料實體（同樣實作 IDataAuthEntity）。
    /// </summary>
    public class DataAuthTests
    {
        private DbContextOptions<ApplicationDbContext> CreateOptions(string dbName)
        {
            return new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(databaseName: dbName)
                .Options;
        }

        [Fact]
        public async Task HierarchicalFilter_SuperiorSeesSubordinateData()
        {
            // Arrange
            var options = CreateOptions("DataAuth_SuperiorSeesSubordinate");
            using var context = new ApplicationDbContext(options);

            context.ApprovalTemplates.AddRange(
                new ApprovalTemplate { Id = "M-001", Name = "M-001", OrganizationUnitPath = "Root/A" },      // Manager's data
                new ApprovalTemplate { Id = "O-001", Name = "O-001", OrganizationUnitPath = "Root/A/B" },    // Operator's data
                new ApprovalTemplate { Id = "X-001", Name = "X-001", OrganizationUnitPath = "Root/X" }       // Other branch
            );
            await context.SaveChangesAsync();

            // Act - Manager Login (Path: Root/A)
            context.CurrentRolePath = "Root/A";
            var managerVisible = await context.ApprovalTemplates.ToListAsync();

            // Assert
            Assert.Equal(2, managerVisible.Count);
            Assert.Contains(managerVisible, t => t.Name == "M-001");
            Assert.Contains(managerVisible, t => t.Name == "O-001");
        }

        [Fact]
        public async Task HierarchicalFilter_PeerIsolation()
        {
            // Arrange
            var options = CreateOptions("DataAuth_PeerIsolation");
            using var context = new ApplicationDbContext(options);

            context.ApprovalTemplates.AddRange(
                new ApprovalTemplate { Id = "B1-001", Name = "B1-001", OrganizationUnitPath = "Root/A/B1" },
                new ApprovalTemplate { Id = "B2-001", Name = "B2-001", OrganizationUnitPath = "Root/A/B2" }
            );
            await context.SaveChangesAsync();

            // Act - Peer B1 Login
            context.CurrentRolePath = "Root/A/B1";
            var b1Visible = await context.ApprovalTemplates.ToListAsync();

            // Assert
            Assert.Single(b1Visible);
            Assert.Equal("B1-001", b1Visible[0].Name);
        }

        [Fact]
        public async Task FallbackFilter_RoleWithoutOrgOnlySeesOwnData()
        {
            // Arrange
            var options = CreateOptions("DataAuth_Fallback");
            using var context = new ApplicationDbContext(options);

            context.ApprovalTemplates.AddRange(
                new ApprovalTemplate { Id = "IND-1", Name = "Independent-1", CreatedByRoleId = "RoleC", OrganizationUnitPath = null },
                new ApprovalTemplate { Id = "IND-2", Name = "Independent-2", CreatedByRoleId = "RoleD", OrganizationUnitPath = null },
                new ApprovalTemplate { Id = "ORG-1", Name = "InOrg-1",       CreatedByRoleId = "RoleB", OrganizationUnitPath = "Root/A" }
            );
            await context.SaveChangesAsync();

            // Act - Role C (Path: null) Login
            context.CurrentRoleId = "RoleC";
            context.CurrentRolePath = null;
            var cVisible = await context.ApprovalTemplates.ToListAsync();

            // Assert
            Assert.Single(cVisible);
            Assert.Equal("Independent-1", cVisible[0].Name);
        }

        [Fact]
        public async Task ApplyGlobalValues_AutoFillsAuthFields()
        {
            // Arrange
            var options = CreateOptions("DataAuth_AutoFill");
            using var context = new ApplicationDbContext(options);
            context.CurrentRoleId = "RoleM";
            context.CurrentRolePath = "Root/M";

            // Act
            var tmpl = new ApprovalTemplate { Id = "AUTO-001", Name = "Auto Template" };
            context.ApprovalTemplates.Add(tmpl);
            await context.SaveChangesAsync();

            // Assert
            Assert.Equal("RoleM", tmpl.CreatedByRoleId);
            Assert.Equal("Root/M", tmpl.OrganizationUnitPath);
        }

        // ────────────────────────────────────────────────────────────────────
        // 新增測試：DataAuth 啟用前的歷史資料（OrganizationUnitPath = null）
        // 對任何有 RolePath 的角色都應該可見（避免啟用日當天既有資料瞬間消失）。
        // ────────────────────────────────────────────────────────────────────
        [Fact]
        public async Task LegacyData_NullPath_VisibleToOrgRoles()
        {
            // Arrange — 混合舊資料（Path 為 null）與新資料
            var options = CreateOptions("DataAuth_Legacy_NullPath_Visible");
            using var context = new ApplicationDbContext(options);

            context.ApprovalTemplates.AddRange(
                new ApprovalTemplate { Id = "LEG-1", Name = "Legacy-1", OrganizationUnitPath = null },          // 啟用 DataAuth 前的舊資料
                new ApprovalTemplate { Id = "LEG-2", Name = "Legacy-2", OrganizationUnitPath = null },
                new ApprovalTemplate { Id = "NEW-1", Name = "New-OrgA", OrganizationUnitPath = "Root/A" },
                new ApprovalTemplate { Id = "NEW-2", Name = "New-OrgX", OrganizationUnitPath = "Root/X" }
            );
            await context.SaveChangesAsync();

            // Act — 以 Root/A 角色查詢
            context.CurrentRolePath = "Root/A";
            var visible = await context.ApprovalTemplates.ToListAsync();

            // Assert — 兩筆舊資料 + 一筆自己組織內新資料 = 3 筆
            Assert.Equal(3, visible.Count);
            Assert.Contains(visible, t => t.Name == "Legacy-1");
            Assert.Contains(visible, t => t.Name == "Legacy-2");
            Assert.Contains(visible, t => t.Name == "New-OrgA");
            Assert.DoesNotContain(visible, t => t.Name == "New-OrgX");
        }

        // ────────────────────────────────────────────────────────────────────
        // 新增測試：DataAuth filter 套用到業務單據（PurchaseOrder）
        // 驗證本次補強將 IDataAuthEntity 擴展到 PUR/ORD/INV/MFG 後行為一致。
        // ────────────────────────────────────────────────────────────────────
        [Fact]
        public async Task PurchaseOrder_RespectsDataAuthFilter()
        {
            var options = CreateOptions("DataAuth_PO_Filter");
            using var context = new ApplicationDbContext(options);

            context.PurchaseOrders.AddRange(
                new PurchaseOrder { PurchaseOrderId = "PO-LEG", OrderDate = new DateOnly(2025, 1, 1),
                    CreatedBy = "u1", SupplierCode = "S001", PlantId = "P1", OrganizationUnitPath = null },
                new PurchaseOrder { PurchaseOrderId = "PO-A",   OrderDate = new DateOnly(2025, 6, 1),
                    CreatedBy = "u2", SupplierCode = "S002", PlantId = "P1", OrganizationUnitPath = "Root/Sales/A" },
                new PurchaseOrder { PurchaseOrderId = "PO-B",   OrderDate = new DateOnly(2025, 6, 1),
                    CreatedBy = "u3", SupplierCode = "S003", PlantId = "P1", OrganizationUnitPath = "Root/Sales/A/Sub" },
                new PurchaseOrder { PurchaseOrderId = "PO-X",   OrderDate = new DateOnly(2025, 6, 1),
                    CreatedBy = "u4", SupplierCode = "S004", PlantId = "P1", OrganizationUnitPath = "Root/HR" }
            );
            await context.SaveChangesAsync();

            // 設定 PlantId 過濾匹配，再以 Root/Sales 主管查詢
            context.CurrentPlantId = "P1";
            context.CurrentRolePath = "Root/Sales";

            var visible = await context.PurchaseOrders.ToListAsync();

            // PO-LEG (null path, 視為公開) + PO-A (前綴匹配) + PO-B (深層匹配) = 3 筆
            Assert.Equal(3, visible.Count);
            Assert.Contains(visible, p => p.PurchaseOrderId == "PO-LEG");
            Assert.Contains(visible, p => p.PurchaseOrderId == "PO-A");
            Assert.Contains(visible, p => p.PurchaseOrderId == "PO-B");
            Assert.DoesNotContain(visible, p => p.PurchaseOrderId == "PO-X");
        }

        // ────────────────────────────────────────────────────────────────────
        // 新增測試：ApplyGlobalValues 對業務單據（StockTransfer）也能自動回填
        // ────────────────────────────────────────────────────────────────────
        [Fact]
        public async Task ApplyGlobalValues_AutoFillsBusinessDocFields()
        {
            var options = CreateOptions("DataAuth_BusinessDoc_AutoFill");
            using var context = new ApplicationDbContext(options);
            context.CurrentPlantId   = "P1";
            context.CurrentRoleId    = "RoleM";
            context.CurrentRolePath  = "Root/Manufacturing";

            var doc = new StockTransfer
            {
                StockTransferId = "ST-001",
                TransferDate    = new DateOnly(2026, 5, 20),
                CreatedBy       = "u1",
                // 故意不指定 PlantId / CreatedByRoleId / OrganizationUnitPath
            };
            context.StockTransfers.Add(doc);
            await context.SaveChangesAsync();

            Assert.Equal("P1", doc.PlantId);
            Assert.Equal("RoleM", doc.CreatedByRoleId);
            Assert.Equal("Root/Manufacturing", doc.OrganizationUnitPath);
        }
    }
}

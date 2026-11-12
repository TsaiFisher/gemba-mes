using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Xunit;

namespace SmartFactory.MES.Tests;

/// <summary>
/// 驗證所有 FormVm 的 [MaxLength] 不得小於對應 Model 的 [MaxLength]。
/// 若 VM 比 Model 嚴，ModelState 驗證會在存入 DB 之前就失敗，讓使用者看到
/// 「欄位長度不得超過 N 個字元」但實際上 DB 可以容納更長的值——典型例子：
/// 責任部門 DepartmentCode (VM=30, Model=100)。
/// </summary>
public class ViewModelMaxLengthTests
{
    // ViewModel → 對應的 Model 型別清單（可對多）
    private static readonly Dictionary<Type, Type[]> VmToModels = new()
    {
        [typeof(Modules.INV.ViewModels.ScrapDocFormVm)]             = [typeof(Modules.INV.Models.ScrapDoc),             typeof(Modules.INV.Models.ScrapDocLine)],
        [typeof(Modules.INV.ViewModels.StockAdjustmentFormVm)]      = [typeof(Modules.INV.Models.StockAdjustment),      typeof(Modules.INV.Models.StockAdjustmentLine)],
        [typeof(Modules.INV.ViewModels.LoanDocFormVm)]              = [typeof(Modules.INV.Models.LoanDoc),              typeof(Modules.INV.Models.LoanDocLine)],
        [typeof(Modules.INV.ViewModels.SampleDocFormVm)]            = [typeof(Modules.INV.Models.SampleDoc),            typeof(Modules.INV.Models.SampleDocLine)],
        [typeof(Modules.INV.ViewModels.OutsourceMaterialDocFormVm)] = [typeof(Modules.INV.Models.OutsourceMaterialDoc), typeof(Modules.INV.Models.OutsourceMaterialDocLine)],
        [typeof(Modules.INV.ViewModels.ReworkMaterialDocFormVm)]    = [typeof(Modules.INV.Models.ReworkMaterialDoc),    typeof(Modules.INV.Models.ReworkMaterialDocLine)],
        [typeof(Modules.INV.ViewModels.StockTransferFormVm)]        = [typeof(Modules.INV.Models.StockTransfer),        typeof(Modules.INV.Models.StockTransferLine)],
        [typeof(Modules.INV.ViewModels.CrossPlantTransferFormVm)]   = [typeof(Modules.INV.Models.CrossPlantTransfer),   typeof(Modules.INV.Models.CrossPlantTransferLine)],
        [typeof(Modules.ORD.ViewModels.GoodsShipmentFormVm)]        = [typeof(Modules.ORD.Models.GoodsShipment),        typeof(Modules.ORD.Models.GoodsShipmentLine)],
        [typeof(Modules.ORD.ViewModels.InternalOrderFormVm)]        = [typeof(Modules.ORD.Models.InternalOrder),        typeof(Modules.ORD.Models.InternalOrderLine)],
        [typeof(Modules.ORD.ViewModels.SalesOrderFormVm)]           = [typeof(Modules.ORD.Models.SalesOrder),           typeof(Modules.ORD.Models.SalesOrderLine)],
        [typeof(Modules.ORD.ViewModels.SalesReturnFormVm)]          = [typeof(Modules.ORD.Models.SalesReturn),          typeof(Modules.ORD.Models.SalesReturnLine)],
        [typeof(Modules.PUR.ViewModels.GoodsReceiptFormVm)]         = [typeof(Modules.PUR.Models.GoodsReceipt),         typeof(Modules.PUR.Models.GoodsReceiptLine)],
        [typeof(Modules.PUR.ViewModels.GoodsReturnFormVm)]          = [typeof(Modules.PUR.Models.GoodsReturn),          typeof(Modules.PUR.Models.GoodsReturnLine)],
        [typeof(Modules.PUR.ViewModels.PurchaseOrderFormVm)]        = [typeof(Modules.PUR.Models.PurchaseOrder),        typeof(Modules.PUR.Models.PurchaseOrderLine)],
        [typeof(Modules.PUR.ViewModels.PurchaseRequestFormVm)]      = [typeof(Modules.PUR.Models.PurchaseRequest),      typeof(Modules.PUR.Models.PurchaseRequestLine)],
    };

    [Fact]
    public void FormVm_MaxLength_MustNotExceedModel_MaxLength()
    {
        var violations = new List<string>();

        foreach (var (vmType, modelTypes) in VmToModels)
        {
            // 收集所有對應 Model 欄位的最大 MaxLength
            var modelMaxLengths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var modelType in modelTypes)
            {
                foreach (var prop in modelType.GetProperties())
                {
                    var attr = prop.GetCustomAttribute<MaxLengthAttribute>();
                    if (attr == null) continue;
                    var key = prop.Name;
                    if (!modelMaxLengths.TryGetValue(key, out var existing) || attr.Length > existing)
                        modelMaxLengths[key] = attr.Length;
                }
            }

            // 對比 ViewModel 欄位
            foreach (var vmProp in vmType.GetProperties())
            {
                var vmAttr = vmProp.GetCustomAttribute<MaxLengthAttribute>();
                if (vmAttr == null) continue;
                if (!modelMaxLengths.TryGetValue(vmProp.Name, out var modelMax)) continue;
                if (vmAttr.Length < modelMax)
                    violations.Add(
                        $"{vmType.Name}.{vmProp.Name}: VM MaxLength={vmAttr.Length} < Model MaxLength={modelMax}");
            }
        }

        Assert.True(violations.Count == 0,
            "以下 ViewModel 欄位的 MaxLength 小於對應 Model，會導致 ModelState 驗證提早失敗：\n" +
            string.Join("\n", violations));
    }
}

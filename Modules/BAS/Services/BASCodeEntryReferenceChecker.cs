using SmartFactory.MES.Modules._Shared;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.BAS.Services
{
    /// <summary>
    /// BAS 模組的 CodeEntry 引用檢查。
    /// 檢查 Category + Code 是否被 BAS 模組的各 Entity 欄位使用中。
    /// </summary>
    public class BASCodeEntryReferenceChecker : ICodeEntryReferenceChecker
    {
        private readonly ApplicationDbContext _context;

        public BASCodeEntryReferenceChecker(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<List<string>> CheckReferencesAsync(CodeEntry entry)
        {
            var reasons = new List<string>();
            var category = entry.Category;
            var code = entry.Code;

            switch (category)
            {
                case "payment_type":
                    if (await _context.Companies.AnyAsync(c => c.PaymentType == code))
                        reasons.Add(PzText.Get("Bas_CodeInUse_CompanyPayment"));
                    break;

                case "abc_grade":
                    if (await _context.Companies.AnyAsync(c => c.AbcGrade == code))
                        reasons.Add(PzText.Get("Bas_CodeInUse_CompanyGrade"));
                    break;

                case "stock_mode":
                    if (await _context.Products.AnyAsync(p => p.StockMode == code))
                        reasons.Add(PzText.Get("Bas_CodeInUse_ProductStockMode"));
                    break;

                case "mrp_mode":
                    if (await _context.Products.AnyAsync(p => p.MrpMode == code))
                        reasons.Add(PzText.Get("Bas_CodeInUse_ProductMrpMode"));
                    break;

                case "tooling_type":
                    if (await _context.Toolings.IgnoreQueryFilters().AnyAsync(t => t.ToolingType == code))
                        reasons.Add(PzText.Get("Bas_CodeInUse_ToolingType"));
                    break;

                case "special_day_type":
                    if (await _context.SpecialDays.IgnoreQueryFilters().AnyAsync(s => s.DayType == code))
                        reasons.Add(PzText.Get("Bas_CodeInUse_SpecialDay"));
                    break;

                case "location_type":
                    if (await _context.StorageLocations.IgnoreQueryFilters().AnyAsync(s => s.LocationType == code))
                        reasons.Add(PzText.Get("Bas_CodeInUse_LocationType"));
                    break;
            }

            return reasons;
        }
    }
}

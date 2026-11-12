using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Modules.BAS.Models
{
    /// <summary>
    /// 產品分類標籤（多對多），CategoryCode → ProductCategory
    /// </summary>
    public class ProductCategoryTag
    {
        [Required, MaxLength(30)]
        public string ProductCode { get; set; } = string.Empty;

        [Required, MaxLength(20)]
        public string CategoryCode { get; set; } = string.Empty;

        // Navigation
        [ForeignKey(nameof(ProductCode))]
        public Product? Product { get; set; }

        [ForeignKey(nameof(CategoryCode))]
        public ProductCategory? Category { get; set; }
    }
}

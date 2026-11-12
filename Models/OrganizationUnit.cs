using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Models
{
    public class OrganizationUnit : IPlantEntity
    {
        [Key]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [Required]
        [StringLength(100)]
        public string Name { get; set; } = string.Empty;

        public int SortOrder { get; set; }

        public string? ParentId { get; set; }

        [ForeignKey("ParentId")]
        public virtual OrganizationUnit? Parent { get; set; }

        public virtual ICollection<OrganizationUnit> Children { get; set; } = new List<OrganizationUnit>();

        public string? PlantId { get; set; } // 廠別代碼 (例如: P1, P2)

        // 用於權限路徑，例如 "Root/Department/Section"
        public string? Path { get; set; }
    }
}

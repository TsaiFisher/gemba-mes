using Microsoft.AspNetCore.Identity;

namespace SmartFactory.MES.Models
{
    public class ApplicationRole : IdentityRole
    {
        public string? NameEn { get; set; }
        public string? NameZh { get; set; }
        public string? OrganizationUnitId { get; set; }
        public virtual OrganizationUnit? OrganizationUnit { get; set; }
        public virtual ICollection<RolePlantAssignment> PlantAssignments { get; set; } = new List<RolePlantAssignment>();
    }
}

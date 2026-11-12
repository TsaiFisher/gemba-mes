namespace SmartFactory.MES.Models
{
    /// <summary>角色與廠別的多對多關聯，含預設廠別標記</summary>
    public class RolePlantAssignment
    {
        public string RoleId { get; set; } = string.Empty;
        public string PlantId { get; set; } = string.Empty;
        public bool IsDefault { get; set; }

        public virtual ApplicationRole? Role { get; set; }
        public virtual Plant? Plant { get; set; }
    }
}

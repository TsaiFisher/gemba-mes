namespace SmartFactory.MES.Services
{
    public interface IMenuContributor
    {
        int Order { get; }
        List<MenuItem> GetMenuItems();
    }
}

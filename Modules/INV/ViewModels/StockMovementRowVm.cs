namespace SmartFactory.MES.Modules.INV.ViewModels
{
    public class StockMovementRowVm
    {
        public Guid   MovementId     { get; set; }
        public DateOnly MovementDate { get; set; }
        public DateTime CreatedAt    { get; set; }
        public string TypeCode       { get; set; } = "";
        public string TypeName       { get; set; } = "";
        public string? TypeCategory  { get; set; }
        public int    Direction      { get; set; }
        public string DocNo          { get; set; } = "";
        public int    DocLineNo      { get; set; }
        public string? SourceDocNo   { get; set; }
        public string ProductCode    { get; set; } = "";
        public string ProductName    { get; set; } = "";
        public string WhCode         { get; set; } = "";
        public string LocationCode   { get; set; } = "";
        public string LotNo          { get; set; } = "";
        public decimal Qty           { get; set; }
        public string UomCode        { get; set; } = "";
        public decimal StockQty      { get; set; }
        public string StockUomCode   { get; set; } = "";
        public decimal? UnitCost     { get; set; }
        public decimal? TotalCost    { get; set; }
        public string? CreatedBy     { get; set; }
        public string? Remark        { get; set; }
    }
}

using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.INV.Models
{
    /// <summary>
    /// 所有庫存異動單據（GoodsReceipt/GoodsReturn/LoanDoc/SampleDoc/ScrapDoc…）的共用介面。
    /// 供 WarehouseApiController 以統一方式進行待處理查詢與過帳調度。
    /// </summary>
    public interface IInventoryDocument
    {
        string DocNo        { get; }
        DateOnly DocDate    { get; }
        string TypeCode     { get; }
        string? TypeCategory{ get; }
        DocStatus Status    { get; set; }
        DocumentApprovalStatus ApprovalStatus { get; }
        string? PlantId     { get; }

        IReadOnlyList<IInventoryDocumentLine> GetLines();
    }

    /// <summary>庫存異動單據明細的共用介面。</summary>
    public interface IInventoryDocumentLine
    {
        int      LineNo        { get; }
        string   ProductCode   { get; }
        decimal  StockQty      { get; }
        decimal  QtyPutAway    { get; set; }
        string?  StockUomCode  { get; }
        string?  UomCode       { get; }
        string?  WhCode        { get; }
        decimal? UnitPrice     { get; }

        // Source tracking
        string?  SourceDocId   { get; }
        int?     SourceLineNo  { get; }
        decimal  QtySourceCleared { get; set; }
    }
}

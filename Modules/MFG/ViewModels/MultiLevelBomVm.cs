using SmartFactory.MES.Modules.MFG.Models;

namespace SmartFactory.MES.Modules.MFG.ViewModels
{
    /// <summary>多階料表 — 攤平列（依 DFS 展開順序，Level 1 起）。</summary>
    public class MultiLevelBomRowVm
    {
        public int Level { get; set; }
        public string ParentProductCode { get; set; } = "";
        public string ProductCode { get; set; } = "";
        public string ProductName { get; set; } = "";
        public string UomCode { get; set; } = "";

        /// <summary>單位用量（每 1 單位父件）。</summary>
        public decimal Quantity { get; set; }

        /// <summary>損耗率 %（RoutingBom.ScrapRate）。</summary>
        public decimal ScrapRate { get; set; }

        /// <summary>下角率 %（RoutingBom.OffcutRate）。</summary>
        public decimal OffcutRate { get; set; }

        /// <summary>累計需求量（每 1 單位最上階成品，含各階下角率／損耗率連乘）。</summary>
        public decimal RequiredQty { get; set; }

        /// <summary>來源：Make（有有效途程，往下展開）/ Buy（採購件，葉節點）。</summary>
        public string SourceType { get; set; } = "Buy";

        /// <summary>發料方式代碼（RoutingBom.IssueMethod；null=依下層 fallback）。</summary>
        public MaterialIssueMethod? IssueMethod { get; set; }
    }

    /// <summary>多階途程 — 產品節點（Level 0=最上階成品；僅自製件有節點）。</summary>
    public class MultiLevelRoutingNodeVm
    {
        public int Level { get; set; }
        public string ProductCode { get; set; } = "";
        public string ProductName { get; set; } = "";
        public string? UomCode { get; set; }
        public string RoutingId { get; set; } = "";
        public string RoutingName { get; set; } = "";

        /// <summary>累計需求量（每 1 單位最上階成品）。</summary>
        public decimal RequiredQty { get; set; }

        public List<MultiLevelRoutingOpVm> Operations { get; set; } = new();
    }

    /// <summary>多階途程 — 工序列。</summary>
    public class MultiLevelRoutingOpVm
    {
        public int OperationSeq { get; set; }
        public string OperationName { get; set; } = "";
        public string WorkCenterId { get; set; } = "";
        public decimal SetupTime { get; set; }
        public decimal UnitTime { get; set; }
        public OperationTimeUnit TimeUnit { get; set; }
        public OperationTimeUnit SetupTimeUnit { get; set; }
        public decimal YieldRate { get; set; }
        public bool IsOutsource { get; set; }
        public List<MultiLevelRoutingOpBomVm> Boms { get; set; } = new();
    }

    /// <summary>多階途程 — 工序用料列（料表）。</summary>
    public class MultiLevelRoutingOpBomVm
    {
        public int BomSeq { get; set; }
        public string ProductCode { get; set; } = "";
        public string ProductName { get; set; } = "";
        public string UomCode { get; set; } = "";
        public decimal Quantity { get; set; }
        public decimal ScrapRate { get; set; }
        public decimal OffcutRate { get; set; }

        /// <summary>此用料是否為自製件（會在下一階出現自己的途程節點）。</summary>
        public bool IsMake { get; set; }
    }

    /// <summary>多階展開結果（多階料表與多階途程共用一次展開）。</summary>
    public class MultiLevelExplosionResult
    {
        public string RootProductCode { get; set; } = "";
        public string RootProductName { get; set; } = "";
        public string RootUomCode { get; set; } = "";

        /// <summary>多階料表攤平列（DFS 順序）。</summary>
        public List<MultiLevelBomRowVm> BomRows { get; set; } = new();

        /// <summary>多階途程節點（DFS 順序，含最上階；僅自製件）。</summary>
        public List<MultiLevelRoutingNodeVm> RoutingNodes { get; set; } = new();

        /// <summary>展開警告（循環、超過深度上限等）。</summary>
        public List<string> Warnings { get; set; } = new();
    }
}

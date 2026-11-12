using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Modules.RPS;

namespace SmartFactory.MES.Services
{
    public class ProjectZMenuContributor : IMenuContributor
    {
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;
        private readonly IConfiguration _configuration;

        public int Order => 10;

        public ProjectZMenuContributor(
            IStringLocalizer<ProjectZSharedResource> localizer, IConfiguration configuration)
        {
            _localizer = localizer;
            _configuration = configuration;
        }

        public List<MenuItem> GetMenuItems()
        {
            var items = new List<MenuItem>
            {
                new MenuItem
                {
                    Title = _localizer["BasManagement"],
                    Icon = "bi-archive",
                    MenuKey = "BasManagement",
                    SubItems = new List<MenuItem>
                    {
                        new MenuItem { Title = _localizer["CompanyMaster"],             Controller = "Company",                  Action = "Index", Icon = "bi-building",         MenuKey = "CompanyMaster" },
                        new MenuItem { Title = _localizer["ProductCategoryMaster"],     Controller = "ProductCategory",           Action = "Index", Icon = "bi-tags",              MenuKey = "ProductCategory" },
                        new MenuItem { Title = _localizer["ProductMaster"],             Controller = "Product",                  Action = "Index", Icon = "bi-box-seam",          MenuKey = "ProductMaster" },
                        new MenuItem { Title = _localizer["TaxMaster"],                Controller = "Tax",                      Action = "Index", Icon = "bi-receipt-cutoff",     MenuKey = "TaxMaster" },
                        new MenuItem { Title = _localizer["CurrencyMaster"],           Controller = "Currency",                 Action = "Index", Icon = "bi-currency-exchange",  MenuKey = "CurrencyMaster" },
                        new MenuItem { Title = _localizer["UomMaster"],                Controller = "Uom",                      Action = "Index", Icon = "bi-rulers",             MenuKey = "UomMaster" },
                        new MenuItem { Title = _localizer["ContainerMaster"],          Controller = "Container",                Action = "Index", Icon = "bi-box",               MenuKey = "ContainerMaster" },
                        new MenuItem { Title = _localizer["ContainerUnit"],            Controller = "ContainerUnit",            Action = "Index", Icon = "bi-qr-code",           MenuKey = "ContainerUnit" },
                        new MenuItem { Title = _localizer["ToolingMaster"],            Controller = "Tooling",                  Action = "Index", Icon = "bi-tools",             MenuKey = "ToolingMaster" },
                        new MenuItem { Title = _localizer["InventoryTransactionType"], Controller = "InventoryTransactionType", Action = "Index", Icon = "bi-arrow-left-right",  MenuKey = "InventoryTransactionType" },
                        new MenuItem { Title = _localizer["WorkCenterMaster"],         Controller = "WorkCenter",               Action = "Index", Icon = "bi-cpu",               MenuKey = "WorkCenterMaster" },
                        new MenuItem { Title = _localizer["WarehouseMaster"],          Controller = "Warehouse",                Action = "Index", Icon = "bi-house-door",        MenuKey = "WarehouseMaster" },
                        new MenuItem { Title = _localizer["ShiftGroupMaster"],         Controller = "ShiftGroup",               Action = "Index", Icon = "bi-calendar-week",     MenuKey = "ShiftGroupMaster" },
                        new MenuItem { Title = _localizer["ShiftMaster"],              Controller = "Shift",                    Action = "Index", Icon = "bi-clock",             MenuKey = "ShiftMaster" },
                        new MenuItem { Title = _localizer["SpecialDayMaster"],         Controller = "SpecialDay",               Action = "Index", Icon = "bi-calendar-event",    MenuKey = "SpecialDayMaster" },
                        new MenuItem { Title = _localizer["DocNumberConfig"],           Controller = "DocNumberConfig",           Action = "Index", Icon = "bi-123",              MenuKey = "DocNumberConfig" },
                    }
                },
                new MenuItem
                {
                    Title = _localizer["MfgManagement"],
                    Icon = "bi-gear",
                    MenuKey = "MfgManagement",
                    SubItems = new List<MenuItem>
                    {
                        new MenuItem { Title = _localizer["Routing"],     Controller = "Routing",     Action = "Index", Icon = "bi-diagram-3",        MenuKey = "Routing"     },
                        new MenuItem { Title = _localizer["MultiLevelBom"],     Controller = "MultiLevelBom",     Action = "Index", Icon = "bi-list-nested", MenuKey = "MultiLevelBom"     },
                        new MenuItem { Title = _localizer["MultiLevelRouting"], Controller = "MultiLevelRouting", Action = "Index", Icon = "bi-diagram-2",   MenuKey = "MultiLevelRouting" },
                        new MenuItem { Title = _localizer["WorkCalendar"], Controller = "WorkCalendar", Action = "Index", Icon = "bi-calendar-event",   MenuKey = "WorkCalendar" },
                        new MenuItem { Title = _localizer["PlannedDowntime"], Controller = "PlannedDowntime", Action = "Index", Icon = "bi-cone-striped", MenuKey = "PlannedDowntime" },
                        new MenuItem { Title = _localizer["MrpRun"],      Controller = "MrpRun",      Action = "Index", Icon = "bi-cpu-fill",         MenuKey = "MrpRun"      },
                        new MenuItem { Title = _localizer["WorkOrder"],   Controller = "WorkOrder",   Action = "Index", Icon = "bi-card-checklist",   MenuKey = "WorkOrder"   },
                        new MenuItem { Title = _localizer["ProductionBatch"], Controller = "ProductionBatch", Action = "Index", Icon = "bi-collection", MenuKey = "ProductionBatch" },
                        new MenuItem { Title = _localizer["BatchRelease"], Controller = "BatchRelease", Action = "Index", Icon = "bi-box-arrow-up", MenuKey = "BatchRelease" },
                        new MenuItem { Title = _localizer["Schedule"],          Controller = "Schedule",          Action = "Index", Icon = "bi-bar-chart-steps", MenuKey = "Schedule"          },
                        new MenuItem { Title = _localizer["ScheduleQuery"],      Controller = "ScheduleQuery",      Action = "Index", Icon = "bi-search",             MenuKey = "ScheduleQuery"      },
                        new MenuItem { Title = _localizer["PreparationBoard"],   Controller = "PreparationBoard",   Action = "Index", Icon = "bi-list-check",         MenuKey = "PreparationBoard"   },
                        new MenuItem { Title = _localizer["Dispatch"],            Controller = "Dispatch",            Action = "Index", Icon = "bi-person-check",         MenuKey = "Dispatch"            },
                        new MenuItem { Title = _localizer["LineStatus"],          Controller = "LineStatus",          Action = "Index", Icon = "bi-broadcast",            MenuKey = "LineStatus"          },
                        new MenuItem { Title = _localizer["ProductionCompletion"],  Controller = "ProductionCompletion",  Action = "Index", Icon = "bi-box-arrow-in-down-right", MenuKey = "ProductionCompletion"  },
                        new MenuItem { Title = _localizer["MachineStatusOrder"],    Controller = "MachineStatusOrder",    Action = "Index", Icon = "bi-tools",                  MenuKey = "MachineStatusOrder"    },
                        new MenuItem { Title = _localizer["MaterialSlip"],        Controller = "MaterialSlip",        Action = "Index", Icon = "bi-boxes",                  MenuKey = "MaterialSlip"          },
                        new MenuItem { Title = _localizer["WorkOrderMaterialStatus"], Controller = "WorkOrderMaterialStatus", Action = "Index", Icon = "bi-clipboard-data", MenuKey = "WorkOrderMaterialStatus" },
                        new MenuItem { Title = _localizer["WorkOrderVariance"],   Controller = "WorkOrderVariance",   Action = "Index", Icon = "bi-graph-up-arrow",         MenuKey = "WorkOrderVariance"     },
                        new MenuItem { Title = _localizer["OrderChangeDecision"], Controller = "OrderChangeDecision", Action = "Index", Icon = "bi-exclamation-diamond",     MenuKey = "OrderChangeDecision"   },
                        new MenuItem { Title = _localizer["MrpVarianceDecision"], Controller = "MrpVarianceDecision", Action = "Index", Icon = "bi-clipboard-check",       MenuKey = "MrpVarianceDecision"   },
                        new MenuItem { Title = _localizer["WorkOrderProgress"],  Controller = "WorkOrderProgress",  Action = "Index", Icon = "bi-bar-chart-steps",        MenuKey = "WorkOrderProgress"     },
                        new MenuItem { Title = _localizer["DefectRate"],            Controller = "DefectRate",            Action = "Index", Icon = "bi-bug",              MenuKey = "DefectRate"            },
                        new MenuItem { Title = _localizer["WipReport"],             Controller = "WipReport",             Action = "Index", Icon = "bi-gear",             MenuKey = "WipReport"             },
                        new MenuItem { Title = _localizer["Utilization"],           Controller = "Utilization",           Action = "Index", Icon = "bi-speedometer2",     MenuKey = "Utilization"           },
                        new MenuItem { Title = _localizer["ProductionEfficiency"],  Controller = "ProductionEfficiency",  Action = "Index", Icon = "bi-graph-up",         MenuKey = "ProductionEfficiency"  },
                        new MenuItem { Title = _localizer["Oee"],                   Controller = "Oee",                   Action = "Index", Icon = "bi-speedometer",      MenuKey = "Oee"                   },
                        new MenuItem { Title = _localizer["MachineDowntime"],       Controller = "MachineDowntime",       Action = "Index", Icon = "bi-cone-striped",     MenuKey = "MachineDowntime"       },
                        new MenuItem { Title = _localizer["MaterialSlipProgress"],  Controller = "MaterialSlipProgress",  Action = "Index", Icon = "bi-boxes",            MenuKey = "MaterialSlipProgress"  },
                        new MenuItem { Title = _localizer["MrpAchievement"],        Controller = "MrpAchievement",        Action = "Index", Icon = "bi-check2-all",       MenuKey = "MrpAchievement"        },
                    }
                },
                new MenuItem
                {
                    Title = _localizer["PurManagement"],
                    Icon = "bi-cart3",
                    MenuKey = "PurManagement",
                    SubItems = new List<MenuItem>
                    {
                        new MenuItem { Title = _localizer["PurchaseRequest"], Controller = "PurchaseRequest", Action = "Index", Icon = "bi-file-earmark-text",  MenuKey = "PurchaseRequest" },
                        new MenuItem { Title = _localizer["PurchaseOrder"],   Controller = "PurchaseOrder",   Action = "Index", Icon = "bi-bag-check",          MenuKey = "PurchaseOrder"   },
                        new MenuItem { Title = _localizer["GoodsReceipt"],      Controller = "GoodsReceipt",      Action = "Index", Icon = "bi-box-arrow-in-down",   MenuKey = "GoodsReceipt"      },
                        new MenuItem { Title = _localizer["GoodsReturn"],       Controller = "GoodsReturn",       Action = "Index", Icon = "bi-box-arrow-up",          MenuKey = "GoodsReturn"       },
                        new MenuItem { Title = _localizer["VendorPriceList"],   Controller = "VendorPriceList",   Action = "Index", Icon = "bi-tag",                   MenuKey = "VendorPriceList"   },
                        new MenuItem { Title = _localizer["VendorEvaluation"],  Controller = "VendorEvaluation",  Action = "Index", Icon = "bi-star-half",             MenuKey = "VendorEvaluation"  },
                        new MenuItem { Title = _localizer["PurchaseOverdue"],       Controller = "PurchaseOverdue",      Action = "Index", Icon = "bi-exclamation-triangle", MenuKey = "PurchaseOverdue"      },
                        new MenuItem { Title = _localizer["SupplierDeliveryRate"], Controller = "SupplierDeliveryRate", Action = "Index", Icon = "bi-truck-check",         MenuKey = "SupplierDeliveryRate" },
                        new MenuItem { Title = _localizer["PurchaseAmount"],       Controller = "PurchaseAmount",       Action = "Index", Icon = "bi-cash-stack",          MenuKey = "PurchaseAmount"       },
                        new MenuItem { Title = _localizer["PurchasePrice"],        Controller = "PurchasePrice",        Action = "Index", Icon = "bi-tag",                 MenuKey = "PurchasePrice"        },
                    }
                },
                // 委外管理（OSP）— pur-domain.md §4.4 Phase F 選單重組：
                // 委外採購/報價自 MFG 遷入、發退料自 INV 遷入（INV 保留查詢入口）、進貨/重工自 PUR 遷入。
                // 2026-07-28 補遷：委外進度追蹤（OutsourceProgress）自採購管理遷入——其資料來源為
                // OutsourcePurchaseOrder + OutsourceReceipt，與一般採購稽催（PO/GR）不同鏈；
                // Phase F 只遷單據型畫面、Phase G 報表又不動選單，故當初漏網。MenuKey 不變，權限不受影響。
                new MenuItem
                {
                    Title = _localizer["OspManagement"],
                    Icon = "bi-diagram-3",
                    MenuKey = "OspManagement",
                    SubItems = new List<MenuItem>
                    {
                        new MenuItem { Title = _localizer["OutsourceQuotation"],     Controller = "OutsourceQuotation",     Action = "Index", Icon = "bi-cash-coin",           MenuKey = "OutsourceQuotation"     },
                        new MenuItem { Title = _localizer["OutsourcePurchaseOrder"], Controller = "OutsourcePurchaseOrder", Action = "Index", Icon = "bi-truck",               MenuKey = "OutsourcePurchaseOrder" },
                        new MenuItem { Title = _localizer["OutsourceMaterial"],      Controller = "OutsourceMaterial",      Action = "Index", Icon = "bi-boxes",               MenuKey = "OutsourceMaterial"      },
                        new MenuItem { Title = _localizer["OutsourceReceipt"],       Controller = "OutsourceReceipt",       Action = "Index", Icon = "bi-box-arrow-in-right",  MenuKey = "OutsourceReceipt"       },
                        new MenuItem { Title = _localizer["OutsourceRework"],        Controller = "OutsourceRework",        Action = "Index", Icon = "bi-arrow-repeat",        MenuKey = "OutsourceRework"        },
                        new MenuItem { Title = _localizer["OutsourceProgress"],      Controller = "OutsourceProgress",      Action = "Index", Icon = "bi-building",            MenuKey = "OutsourceProgress"      },
                        // 委外價格分析（2026-09-23）。委外在交期面與金額面都已被既有報表涵蓋，
                        // 價格面則完全空白——採購有 PurchasePrice，委外有報價主檔卻沒有對應的比價。
                        new MenuItem { Title = _localizer["OutsourcePrice"],         Controller = "OutsourcePrice",         Action = "Index", Icon = "bi-graph-up-arrow",      MenuKey = "OutsourcePrice"         },
                    }
                },
                new MenuItem
                {
                    Title = _localizer["OrdManagement"],
                    Icon = "bi-cart-check",
                    MenuKey = "OrdManagement",
                    SubItems = new List<MenuItem>
                    {
                        new MenuItem { Title = _localizer["SalesOrder"],    Controller = "SalesOrder",    Action = "Index", Icon = "bi-file-earmark-text",  MenuKey = "SalesOrder"    },
                        new MenuItem { Title = _localizer["GoodsShipment"], Controller = "GoodsShipment", Action = "Index", Icon = "bi-truck",               MenuKey = "GoodsShipment" },
                        new MenuItem { Title = _localizer["SalesReturn"],   Controller = "SalesReturn",   Action = "Index", Icon = "bi-arrow-return-left",   MenuKey = "SalesReturn"   },
                        new MenuItem { Title = _localizer["InternalOrder"], Controller = "InternalOrder", Action = "Index", Icon = "bi-box-seam",            MenuKey = "InternalOrder" },
                        new MenuItem { Title = _localizer["OrderOverdue"],      Controller = "OrderOverdue",    Action = "Index", Icon = "bi-exclamation-triangle", MenuKey = "OrderOverdue"     },
                        new MenuItem { Title = _localizer["SalesRanking"],      Controller = "SalesRanking",    Action = "Index", Icon = "bi-trophy",              MenuKey = "SalesRanking"     },
                        new MenuItem { Title = _localizer["SalesTrend"],        Controller = "SalesTrend",      Action = "Index", Icon = "bi-graph-up",            MenuKey = "SalesTrend"       },
                        new MenuItem { Title = _localizer["DeliveryRate"],      Controller = "DeliveryRate",    Action = "Index", Icon = "bi-truck-check",         MenuKey = "DeliveryRate"     },
                        new MenuItem { Title = _localizer["ReturnRate"],        Controller = "ReturnRate",      Action = "Index", Icon = "bi-arrow-return-left",   MenuKey = "ReturnRate"       },
                        new MenuItem { Title = _localizer["SalesForecast"],     Controller = "SalesForecast",   Action = "Index", Icon = "bi-graph-up-arrow",      MenuKey = "SalesForecast"    },
                    }
                },
                new MenuItem
                {
                    Title = _localizer["QcrManagement"],
                    Icon = "bi-shield-check",
                    MenuKey = "QcrManagement",
                    SubItems = new List<MenuItem>
                    {
                        new MenuItem { Title = _localizer["DefectCode"],       Controller = "DefectCode",       Action = "Index", Icon = "bi-bug",              MenuKey = "DefectCode"       },
                        new MenuItem { Title = _localizer["InspectionSpec"],  Controller = "InspectionSpec",  Action = "Index", Icon = "bi-clipboard2-pulse", MenuKey = "InspectionSpec"   },
                        new MenuItem { Title = _localizer["InspectionOrder"], Controller = "InspectionOrder", Action = "Index", Icon = "bi-clipboard2-check", MenuKey = "InspectionOrder" },
                        new MenuItem { Title = _localizer["Ncr"],             Controller = "Ncr",             Action = "Index", Icon = "bi-shield-x",          MenuKey = "Ncr"             },
                        new MenuItem { Title = _localizer["Instrument"],      Controller = "Instrument",      Action = "Index", Icon = "bi-thermometer-half", MenuKey = "Instrument"       },
                        new MenuItem { Title = _localizer["SpcAnalysis"],     Controller = "SpcAnalysis",     Action = "Index", Icon = "bi-graph-up-arrow",   MenuKey = "SpcAnalysis"      },
                    }
                },
                new MenuItem
                {
                    Title = _localizer["InvManagement"],
                    Icon = "bi-boxes",
                    MenuKey = "InvManagement",
                    SubItems = new List<MenuItem>
                    {
                        new MenuItem { Title = _localizer["InventoryBalance"], Controller = "InventoryBalance", Action = "Index", Icon = "bi-clipboard-data",    MenuKey = "InventoryBalance" },
                        new MenuItem { Title = _localizer["StockMovement"],    Controller = "StockMovement",    Action = "Index", Icon = "bi-arrow-left-right",  MenuKey = "StockMovement"    },
                        new MenuItem { Title = _localizer["StockLedger"],      Controller = "StockLedger",      Action = "Index", Icon = "bi-journal-text",      MenuKey = "StockLedger"      },
                        new MenuItem { Title = _localizer["WarehouseOps"],     Controller = "WarehouseOps",     Action = "Index", Icon = "bi-box-arrow-in-down", MenuKey = "WarehouseOps"     },
                        new MenuItem { Title = _localizer["MaterialStaging"],   Controller = "MaterialStaging",   Action = "Index", Icon = "bi-box2-heart",          MenuKey = "MaterialStaging"   },
                        new MenuItem { Title = _localizer["ScrapDoc"],          Controller = "ScrapDoc",          Action = "Index", Icon = "bi-trash3",             MenuKey = "ScrapDoc"          },
                        new MenuItem { Title = _localizer["LoanDoc"],           Controller = "LoanDoc",           Action = "Index", Icon = "bi-arrow-left-right",  MenuKey = "LoanDoc"           },
                        new MenuItem { Title = _localizer["SampleDoc"],         Controller = "SampleDoc",         Action = "Index", Icon = "bi-box-seam",          MenuKey = "SampleDoc"         },
                        new MenuItem { Title = _localizer["InventoryCount"],    Controller = "InventoryCount",    Action = "Index", Icon = "bi-clipboard2-check", MenuKey = "InventoryCount"    },
                        new MenuItem { Title = _localizer["StockAdjustment"],   Controller = "StockAdjustment",   Action = "Index", Icon = "bi-pencil-square",     MenuKey = "StockAdjustment"   },
                        // ⚠️ OutsourceMaterial 原本登錄在這裡（2026-04-24 初版）。2026-07-11 的
                        //    #62「委外管理選單重組」把它加進 OspManagement 群組，卻**沒有把這一筆移掉**，
                        //    於是同一個 MenuKey 登錄了兩次。MenuService.GetFullMenu 是 SelectMany 不去重，
                        //    後果是側邊欄與權限矩陣各出現兩筆，而權限矩陣的兩列會共用同一個
                        //    permissions[i]（索引查的是 MenuKey）——兩列彼此不獨立，改一列另一列跟著變。
                        //    留在 OspManagement：那是 #62 刻意建立的委外鏈分群，本筆屬於該鏈。
                        //    守門在 R39（扁平選單的 MenuKey 不得重複）。
                        new MenuItem { Title = _localizer["ReworkMaterial"],    Controller = "ReworkMaterial",    Action = "Index", Icon = "bi-arrow-repeat",      MenuKey = "ReworkMaterial"    },
                        new MenuItem { Title = _localizer["StockTransfer"],     Controller = "StockTransfer",     Action = "Index", Icon = "bi-arrow-left-right",  MenuKey = "StockTransfer"     },
                        new MenuItem { Title = _localizer["CrossPlantTransfer"], Controller = "CrossPlantTransfer", Action = "Index", Icon = "bi-arrow-left-right", MenuKey = "CrossPlantTransfer" },
                        new MenuItem { Title = _localizer["InventoryValuation"], Controller = "InventoryValuation", Action = "Index", Icon = "bi-bar-chart-line",   MenuKey = "InventoryValuation" },
                        new MenuItem { Title = _localizer["LandedCostAllocation"], Controller = "LandedCostAllocation", Action = "Index", Icon = "bi-truck-flatbed", MenuKey = "LandedCostAllocation" },
                        new MenuItem { Title = _localizer["StockShortage"],     Controller = "StockShortage",     Action = "Index", Icon = "bi-box-seam",           MenuKey = "StockShortage"      },
                        new MenuItem { Title = _localizer["SlowMovingInventory"], Controller = "SlowMovingInventory", Action = "Index", Icon = "bi-clock-history",      MenuKey = "SlowMovingInventory" },
                        new MenuItem { Title = _localizer["InventoryAging"],      Controller = "InventoryAging",      Action = "Index", Icon = "bi-hourglass-split",    MenuKey = "InventoryAging"      },
                        new MenuItem { Title = _localizer["InventoryValue"],      Controller = "InventoryValue",      Action = "Index", Icon = "bi-bar-chart",         MenuKey = "InventoryValue"      },
                        new MenuItem { Title = _localizer["InventoryTurnover"],   Controller = "InventoryTurnover",   Action = "Index", Icon = "bi-arrow-repeat",      MenuKey = "InventoryTurnover"   },
                        new MenuItem { Title = _localizer["StockVariance"],       Controller = "StockVariance",       Action = "Index", Icon = "bi-exclamation-circle", MenuKey = "StockVariance"      },
                    }
                },
                new MenuItem
                {
                    Title = _localizer["FinManagement"],
                    Icon = "bi-bank",
                    MenuKey = "FinManagement",
                    SubItems = new List<MenuItem>
                    {
                        // 會計科目表。2026-09-22 由 BasManagement 移來（業主指定「財務管理」）。
                        // ⚠️ 群組決定權限模組（RolePermissionSeeder.GroupToModule：BasManagement→BAS、
                        //    FinManagement→FIN），所以這不只是換位置——乾淨部署時它改吃 FIN 的角色矩陣。
                        //    既有部署不受影響：ComputeBackfill 只補全系統從未出現過的鍵，
                        //    已存在的 MenuPermission 列不會被改也不會被刪。
                        //    主檔排在交易單據之前。
                        new MenuItem { Title = _localizer["ChartOfAccount"], Controller = "ChartOfAccount", Action = "Index", Icon = "bi-list-columns-reverse", MenuKey = "ChartOfAccount" },
                        new MenuItem { Title = _localizer["APInvoice"],   Controller = "APInvoice",   Action = "Index", Icon = "bi-receipt",          MenuKey = "APInvoice"   },
                        new MenuItem { Title = _localizer["ARInvoice"],   Controller = "ARInvoice",   Action = "Index", Icon = "bi-receipt-cutoff",   MenuKey = "ARInvoice"   },
                        new MenuItem { Title = _localizer["ARCreditNote"], Controller = "ARCreditNote", Action = "Index", Icon = "bi-arrow-return-left", MenuKey = "ARCreditNote" },
                        new MenuItem { Title = _localizer["APDebitNote"],  Controller = "APDebitNote",  Action = "Index", Icon = "bi-arrow-return-right", MenuKey = "APDebitNote"  },
                        new MenuItem { Title = _localizer["APPayment"],   Controller = "APPayment",   Action = "Index", Icon = "bi-cash-coin",        MenuKey = "APPayment"   },
                        new MenuItem { Title = _localizer["ARReceipt"],   Controller = "ARReceipt",   Action = "Index", Icon = "bi-cash-stack",       MenuKey = "ARReceipt"   },
                        new MenuItem { Title = _localizer["BankAccount"], Controller = "BankAccount", Action = "Index", Icon = "bi-bank2",            MenuKey = "BankAccount" },
                        new MenuItem { Title = _localizer["APAging"],     Controller = "APAging",     Action = "Index", Icon = "bi-calendar2-week",   MenuKey = "APAging"     },
                        new MenuItem { Title = _localizer["ARAging"],     Controller = "ARAging",     Action = "Index", Icon = "bi-calendar2-range",  MenuKey = "ARAging"     },
                    }
                },
                new MenuItem
                {
                    Title = _localizer["ActManagement"],
                    Icon = "bi-calendar-check",
                    MenuKey = "ActManagement",
                    SubItems = new List<MenuItem>
                    {
                        new MenuItem { Title = _localizer["AccountingPeriod"], Controller = "AccountingPeriod", Action = "Index", Icon = "bi-calendar3",         MenuKey = "AccountingPeriod" },
                        new MenuItem { Title = _localizer["JournalEntry"],     Controller = "JournalEntry",     Action = "Index", Icon = "bi-journal-bookmark",  MenuKey = "JournalEntry"     },
                        // 試算表（2026-09-23）。排在傳票之後、月結之前——它是**關帳前的檢查**：
                        // DataAuditService 驗的是逐傳票借貸平衡，而每張傳票都平衡不等於科目餘額是對的。
                        new MenuItem { Title = _localizer["TrialBalance"],    Controller = "TrialBalance",     Action = "Index", Icon = "bi-list-columns",      MenuKey = "TrialBalance"     },
                        new MenuItem { Title = _localizer["MonthEnd"],         Controller = "MonthEnd",         Action = "Index", Icon = "bi-check2-square",     MenuKey = "MonthEnd"         },
                        new MenuItem { Title = _localizer["DepartmentActualCost"], Controller = "DepartmentActualCost", Action = "Index", Icon = "bi-cash-stack",    MenuKey = "DepartmentActualCost" },
                        new MenuItem { Title = _localizer["NrvAssessment"],    Controller = "NrvAssessment",    Action = "Index", Icon = "bi-shield-x",          MenuKey = "NrvAssessment"    },
                        new MenuItem { Title = _localizer["EclAllowance"],      Controller = "Ecl",              Action = "Index", Icon = "bi-percent",  MenuKey = "EclAllowance"      },
                        new MenuItem { Title = _localizer["BadDebtProvision"],  Controller = "BadDebtProvision", Action = "Index", Icon = "bi-shield-x", MenuKey = "BadDebtProvision"  },
                    }
                },
                new MenuItem {
                    Title = _localizer["RptManagement"], Icon = "bi-bar-chart-fill", MenuKey = "RptManagement",
                    SubItems = new List<MenuItem> {
                        new MenuItem { Title = _localizer["Dashboard"],    Controller = "Dashboard",    Action = "Index", Icon = "bi-speedometer2",       MenuKey = "Dashboard"    },
                        new MenuItem { Title = _localizer["ProfitLoss"],   Controller = "ProfitLoss",   Action = "Index", Icon = "bi-graph-up-arrow",     MenuKey = "ProfitLoss"   },
                        new MenuItem { Title = _localizer["GrossMargin"],  Controller = "GrossMargin",  Action = "Index", Icon = "bi-bar-chart-line",     MenuKey = "GrossMargin"  },
                        new MenuItem { Title = _localizer["MarginAnalysis"], Controller = "MarginAnalysis", Action = "Index", Icon = "bi-cash-stack",       MenuKey = "MarginAnalysis" },
                        new MenuItem { Title = _localizer["ContributionMargin"], Controller = "ContributionMargin", Action = "Index", Icon = "bi-graph-up", MenuKey = "ContributionMargin" },
                        new MenuItem { Title = _localizer["BalanceSheet"], Controller = "BalanceSheet", Action = "Index", Icon = "bi-bank",               MenuKey = "BalanceSheet" },
                        new MenuItem { Title = _localizer["ConsolidatedStatement"], Controller = "ConsolidatedStatement", Action = "Index", Icon = "bi-building-fill-gear", MenuKey = "ConsolidatedStatement" },
                        new MenuItem { Title = _localizer["OutsourcePerformance"], Controller = "OutsourcePerformance", Action = "Index", Icon = "bi-diagram-3", MenuKey = "OutsourcePerformance" },
                        new MenuItem { Title = _localizer["DataAudit"],    Controller = "DataAudit",    Action = "Index", Icon = "bi-shield-check",       MenuKey = "DataAudit"    },
                        // 現場遙測（2026-10-21）：手機回報的錯誤、API 耗時、斷線、未送出佇列。與資料稽核同為「系統健康」類
                        new MenuItem { Title = _localizer["FieldTelemetry"], Controller = "FieldTelemetry", Action = "Index", Icon = "bi-broadcast",      MenuKey = "FieldTelemetry" },
                    }
                },
                new MenuItem {
                    Title = _localizer["AdminManagement"], Icon = "bi-shield-lock", MenuKey = "AdminManagement",
                    SubItems = new List<MenuItem> {
                        // 各廠參數覆寫一覽（2026-10-27 由基本資料搬來）：它看的是系統參數的逐廠覆寫，
                        // 唯一的編輯入口「廠別管理 → 參數設定」與「系統參數」都在管理系統下，放在一起才找得到。
                        // 能掛進 Core 的群組，是因為 MenuService 會把同 MenuKey 的群組合併成一個。
                        new MenuItem { Title = _localizer["PlantParamOverview"], Controller = "PlantParamOverview", Action = "Index", Icon = "bi-diagram-3", MenuKey = "PlantParamOverview" },
                        // ⚠️ TwoFactorAdmin **不可**再放這裡：Core 已於 2026-09-01 把同一筆加進
                        // CoreMenuContributor 自己的 AdminManagement 群組，同鍵群組合併後會在側邊欄與
                        // 權限矩陣各出現兩筆（R39 會擋）。QA 開啟時另以 items.First(MenuKey == "AdminManagement")
                        // 在這裡掛 QaExtract。
                    }
                }
            };

            // 手冊 Q&A 助理的擷取物一覽（唯讀，§10.11.11）。開關關閉時連選單都不出現——
            // 理由與報表工具同一條，另外多一個：關燈時 QaDbContext 根本沒註冊，
            // 選單若還在，點下去會是解析不到服務的 500 而不是「沒有這個功能」。
            if (Modules.QA.QAModule.IsEnabled(_configuration))
            {
                // 對話畫面（S-7）自成一個群組，**不併進 AdminManagement**：
                // 群組決定權限矩陣（GroupToModule），而助理是給人用的、擷取物一覽是給維護者看的，
                // 兩者的受眾與該給的角色本來就不同。掛在一起等於用同一列矩陣同時決定兩件事。
                items.Add(new MenuItem
                {
                    Title = _localizer["QaManagement"],
                    Icon = "bi-chat-dots",
                    MenuKey = "QaManagement",
                    SubItems = new List<MenuItem>
                    {
                        new MenuItem
                        {
                            // Read＝問問題；Update＝手動重建索引（§12.7）。
                            // 沒有 Create/Delete——這個畫面不產生也不刪除任何資料列。
                            Title = _localizer["QaAssistant"], Controller = "QaAssistant", Action = "Index",
                            Icon = "bi-chat-dots", MenuKey = "QaAssistant",
                            SupportedActions = new List<string> { "Read", "Update" },
                        },
                    }
                });

                var admin = items.First(i => i.MenuKey == "AdminManagement");
                admin.SubItems.Add(new MenuItem
                {
                    // 唯讀畫面：核可走版控（定案 #45），這裡沒有任何寫入動作，
                    // SupportedActions 就只宣告 Read——多宣告會長出永遠沒有作用的勾選框。
                    Title = _localizer["QaExtract"], Controller = "QaExtract", Action = "Index",
                    Icon = "bi-journal-check", MenuKey = "QaExtract",
                    SupportedActions = new List<string> { "Read" },
                });
            }

            // 報表工具（Phase 0-B）。開關關閉時**連選單都不出現**（§三、§17.8）——
            // 沒有選單就沒有 MenuPermission 種子，直接 URL 也會被 default-deny 擋下，
            // 不必在 Controller 再寫一次開關判斷。日後開啟時由 seeder 的 ComputeBackfill 自動補權限。
            if (ReportToolsFeature.IsEnabled(_configuration))
            {
                var rpt = items.First(i => i.MenuKey == "RptManagement");
                rpt.SubItems.AddRange(new[]
                {
                    // 排程維護是 CRUD，但沒有列印；收件匣與執行紀錄是唯讀畫面。
                    // SupportedActions 要照實宣告：多宣告的動詞會在權限維護畫面長出永遠沒有作用的
                    // 勾選框，也會讓「某角色全站唯讀」這類跨畫面不變式被一筆假的寫入權打破。
                    new MenuItem { Title = _localizer["ReportSchedule"],   Controller = "ReportSchedule",   Action = "Index", Icon = "bi-calendar-check", MenuKey = "ReportSchedule",
                                   SupportedActions = new List<string> { "Read", "Create", "Update", "Delete" } },
                    new MenuItem { Title = _localizer["ReportInbox"],      Controller = "ReportInbox",      Action = "Index", Icon = "bi-inbox",          MenuKey = "ReportInbox",
                                   SupportedActions = new List<string> { "Read" } },
                    new MenuItem { Title = _localizer["ReportRunHistory"], Controller = "ReportRunHistory", Action = "Index", Icon = "bi-clock-history",  MenuKey = "ReportRunHistory",
                                   SupportedActions = new List<string> { "Read" } },
                });
            }

            ApplyReportActions(items);

            return items;
        }

        // ── 報表／查詢畫面的動詞宣告（52 支）─────────────────────────────────
        //
        // 為什麼需要這張表：<see cref="MenuItem.SupportedActions"/> 的預設值是五個動詞全開，
        // 報表因此在權限維護畫面長出 Create／Update／Delete 三個**永遠沒有作用**的勾選框。
        // 報表沒有那三種語意——它只能被檢視，以及（若有匯出）被列印。
        //
        // 2026-08-14 裁決（覆寫 §8.1-終，見 report-permission-inventory.md §四之三）：
        //   ① 宣告了其他動詞就必須同時宣告 Read。「有 Print 沒 Read」在本系統不可能成立：
        //      畫面閘門是 CanRead，沒有它連 Index 都進不去，Export 按鈕也就永遠不會出現。
        //   ② 報表不得拿 CanCreate 當「收窄可見範圍」的代理。原本 7 支這樣做的報表
        //      （StockShortage／DefectRate／WorkOrderProgress／DeliveryRate／OrderOverdue／
        //      ReturnRate／PurchaseOverdue）閘門已改為 CanRead——**這是放寬**；
        //      可見範圍改由 MenuPermission 逐角色表達，與 2026-08-04 機密分級同一原則。
        //
        // 為什麼用一張表而不是逐行加在 MenuItem 上：52 支散在 8 個群組裡，逐行宣告時
        // 「漏了一支」與「宣告錯一支」都看不出來。集中成一張表之後，
        // <c>ReportSupportedActionsTests</c> 才能逐支比對 Controller Index 的實際閘門，
        // 漏登錄與動詞不符都會在單元測試就紅。
        //
        // ⚠️ 這張表只收「報表／查詢型」畫面（該 Controller 無 POST，或其 POST 僅為設定/執行）。
        //    有單據 CRUD 的畫面不在此列，維持預設五動詞全開。
        private static readonly Dictionary<string, string[]> ReportActions =
            new(StringComparer.Ordinal)
            {
                // 唯讀 ＋ 匯出（Index 閘門 CanRead、Export 閘門 CanPrint）
                ["APAging"]                 = ["Read", "Print"],
                ["ARAging"]                 = ["Read", "Print"],
                ["BadDebtProvision"]        = ["Read", "Print"],
                ["BalanceSheet"]            = ["Read", "Print"],
                // 合併財報刻意與 BalanceSheet 分開授權：它跨廠加總，可見範圍比使用者本廠大。
                ["ConsolidatedStatement"]   = ["Read", "Print"],
                ["DefectRate"]              = ["Read", "Print"],
                ["DeliveryRate"]            = ["Read", "Print"],
                ["GrossMargin"]             = ["Read", "Print"],
                ["InventoryAging"]          = ["Read", "Print"],
                ["InventoryBalance"]        = ["Read", "Print"],
                ["InventoryTurnover"]       = ["Read", "Print"],
                ["InventoryValuation"]      = ["Read", "Print"],
                ["InventoryValue"]          = ["Read", "Print"],
                // 2026-09-21 起這支不只是報表：手動傳票畫面掛在它底下，走 CanCreate。
                // 少宣告 Create 的話那個權限授不出去，且下次存檔角色時會被靜默設成 false。
                ["JournalEntry"]            = ["Read", "Create", "Print"],
                ["MachineDowntime"]         = ["Read", "Print"],
                ["MarginAnalysis"]          = ["Read", "Print"],
                ["MaterialSlipProgress"]    = ["Read", "Print"],
                ["MrpAchievement"]          = ["Read", "Print"],
                ["MultiLevelBom"]           = ["Read", "Print"],
                ["MultiLevelRouting"]       = ["Read", "Print"],
                ["Oee"]                     = ["Read", "Print"],
                ["OrderOverdue"]            = ["Read", "Print"],
                ["OutsourcePerformance"]    = ["Read", "Print"],
                ["OutsourceProgress"]       = ["Read", "Print"],
                ["ProductionEfficiency"]    = ["Read", "Print"],
                ["ProfitLoss"]              = ["Read", "Print"],
                ["PurchaseAmount"]          = ["Read", "Print"],
                ["PurchaseOverdue"]         = ["Read", "Print"],
                ["PurchasePrice"]           = ["Read", "Print"],
                ["ReturnRate"]              = ["Read", "Print"],
                ["SalesForecast"]           = ["Read", "Print"],
                ["SalesRanking"]            = ["Read", "Print"],
                ["SalesTrend"]              = ["Read", "Print"],
                ["SlowMovingInventory"]     = ["Read", "Print"],
                ["SpcAnalysis"]             = ["Read", "Print"],
                ["StockLedger"]             = ["Read", "Print"],
                ["StockMovement"]           = ["Read", "Print"],
                ["StockShortage"]           = ["Read", "Print"],
                ["StockVariance"]           = ["Read", "Print"],
                ["SupplierDeliveryRate"]    = ["Read", "Print"],
                ["Utilization"]             = ["Read", "Print"],
                ["WipReport"]               = ["Read", "Print"],
                ["WorkOrderMaterialStatus"] = ["Read", "Print"],
                ["WorkOrderProgress"]       = ["Read", "Print"],

                // 純唯讀（沒有匯出動作）
                ["Dashboard"]           = ["Read"],
                ["FieldTelemetry"]      = ["Read"],
                ["LineStatus"]          = ["Read"],
                ["OrderChangeDecision"] = ["Read"],
                ["PreparationBoard"]    = ["Read"],
                ["ScheduleQuery"]       = ["Read"],

                // 報表本體唯讀，但畫面上另有一個會寫入的動作——動詞照實宣告，不可省略
                ["ContributionMargin"] = ["Read", "Update", "Print"],  // 邊際貢獻設定（POST，CanUpdate）
                ["DataAudit"]          = ["Read", "Update"],           // 立即執行稽核（POST，CanUpdate）
                ["WarehouseOps"]       = ["Read", "Create", "Update"], // 倉庫作業看板：畫面內建單/改單
                ["WorkOrderVariance"]  = ["Read", "Create", "Print"],  // Finalize 鎖定成本（POST，CanCreate）
            };

        /// <summary>
        /// 把 <see cref="ReportActions"/> 套到選單樹上。
        /// 找不到對應鍵的畫面維持預設（五動詞全開）——這裡刻意不丟例外：
        /// 選單是啟動路徑，為了一支拼錯的鍵讓整個站起不來並不划算，
        /// 該由 <c>ReportSupportedActionsTests</c> 在建置期擋下。
        /// </summary>
        private static void ApplyReportActions(IEnumerable<MenuItem> items)
        {
            foreach (var item in items)
            {
                if (!string.IsNullOrEmpty(item.MenuKey)
                    && ReportActions.TryGetValue(item.MenuKey, out var actions))
                    item.SupportedActions = [.. actions];

                ApplyReportActions(item.SubItems);
            }
        }
    }
}

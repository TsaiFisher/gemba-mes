using System.Collections;
using System.Globalization;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules._Shared.Printing;

/// <summary>
/// 列印單上的代碼配名稱（2026-10-27 驗收：列印單只印品號、廠商代碼、單位代碼、幣別代碼，
/// 紙本拿到現場或寄給客戶看不懂）。
///
/// <para>用法（Print.cshtml）：<c>@inject PrintNameService PrintNameSvc</c>，
/// <c>var names = await PrintNameSvc.ForAsync(Model);</c>，再以 <c>names.Product(l.ProductCode)</c>、
/// <c>names.Company(Model.SupplierCode)</c>、<c>names.Uom(l.UomCode)</c> 取名稱。</para>
///
/// <para><b>代碼依屬性名稱收集</b>：走訪單據本身與其集合屬性（明細）一層，屬性名以
/// <c>ProductCode</c>／<c>SupplierCode</c>／<c>UomCode</c>／<c>WhCode</c> 等結尾者歸到對應主檔，
/// 一次查回——新單據不必在 Controller 另外準備名稱字典。查不到的代碼原樣顯示（不會空白）。</para>
/// </summary>
public sealed class PrintNameService(ApplicationDbContext db)
{
    /// <summary>從單據（含明細）收集代碼並查回名稱；<paramref name="extra"/> 可再補其他物件（例如另外載入的明細清單）。</summary>
    public async Task<PrintNames> ForAsync(object? document, params object?[] extra)
    {
        var codes = new CodeBag();
        codes.CollectFrom(document, depth: 0);
        foreach (var e in extra) codes.CollectFrom(e, depth: 0);
        return await LoadAsync(codes);
    }

    private async Task<PrintNames> LoadAsync(CodeBag c)
    {
        var names = new PrintNames();
        // 依序 await：同一個 DbContext 不可並行查詢
        if (c.Products.Count > 0)
            names.Products = await db.Products.AsNoTracking().Where(p => c.Products.Contains(p.ProductCode))
                .ToDictionaryAsync(p => p.ProductCode, p => p.ProductName);
        if (c.Companies.Count > 0)
            names.Companies = await db.Companies.AsNoTracking().Where(x => c.Companies.Contains(x.CompanyCode))
                .ToDictionaryAsync(x => x.CompanyCode, x => x.CompanyName);
        if (c.Uoms.Count > 0)
            names.Uoms = await db.Uoms.AsNoTracking().Where(x => c.Uoms.Contains(x.UomCode))
                .ToDictionaryAsync(x => x.UomCode, x => x.UomName);
        if (c.Currencies.Count > 0)
            names.Currencies = await db.Currencies.AsNoTracking().Where(x => c.Currencies.Contains(x.CurrencyCode))
                .ToDictionaryAsync(x => x.CurrencyCode, x => x.CurrencyName);
        if (c.Taxes.Count > 0)
            names.Taxes = await db.Taxes.AsNoTracking().Where(x => c.Taxes.Contains(x.TaxCode))
                .ToDictionaryAsync(x => x.TaxCode, x => x.TaxName);
        if (c.Warehouses.Count > 0)
            names.Warehouses = (await db.Warehouses.AsNoTracking().Where(x => c.Warehouses.Contains(x.WhCode))
                .Select(x => new { x.WhCode, x.WhName }).ToListAsync())
                .GroupBy(x => x.WhCode).ToDictionary(g => g.Key, g => g.First().WhName);
        if (c.Locations.Count > 0)
            // 儲位代碼只在倉內唯一；列印單的儲位欄位幾乎都跟著倉庫欄位，同代碼不同倉的名稱極少不同，取第一筆即可
            names.Locations = (await db.StorageLocations.AsNoTracking().ForCurrentPlant(db).Where(x => c.Locations.Contains(x.LocationCode))
                .Select(x => new { x.LocationCode, x.LocationName }).ToListAsync())
                .GroupBy(x => x.LocationCode).ToDictionary(g => g.Key, g => g.First().LocationName);
        if (c.WorkCenters.Count > 0)
            names.WorkCenters = (await db.WorkCenters.AsNoTracking().Where(x => c.WorkCenters.Contains(x.WorkCenterCode))
                .Select(x => new { x.WorkCenterCode, x.WorkCenterName }).ToListAsync())
                .GroupBy(x => x.WorkCenterCode).ToDictionary(g => g.Key, g => g.First().WorkCenterName);
        if (c.Machines.Count > 0)
            names.Machines = (await db.WorkCenterMachines.AsNoTracking().Where(x => c.Machines.Contains(x.MachineCode))
                .Select(x => new { x.MachineCode, x.MachineName }).ToListAsync())
                .GroupBy(x => x.MachineCode).ToDictionary(g => g.Key, g => g.First().MachineName);
        if (c.Accounts.Count > 0)
        {
            var isZh = CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            names.Accounts = (await db.ChartOfAccounts.AsNoTracking().Where(x => c.Accounts.Contains(x.AccountCode))
                .Select(x => new { x.AccountCode, x.AccountName, x.AccountNameEn }).ToListAsync())
                .GroupBy(x => x.AccountCode)
                .ToDictionary(g => g.Key, g => !isZh && !string.IsNullOrWhiteSpace(g.First().AccountNameEn)
                    ? g.First().AccountNameEn! : g.First().AccountName);
        }
        if (c.TxTypes.Count > 0)
            names.TxTypes = await db.InventoryTransactionTypes.AsNoTracking().Where(x => c.TxTypes.Contains(x.TypeCode))
                .ToDictionaryAsync(x => x.TypeCode, x => x.TypeName);
        if (c.ScrapReasons.Count > 0)
            names.Reasons = await ReasonNamesAsync("PZ.ScrapReason", c.ScrapReasons, names.Reasons);
        if (c.AdjustReasons.Count > 0)
            names.Reasons = await ReasonNamesAsync("PZ.AdjustReason", c.AdjustReasons, names.Reasons);
        return names;
    }

    /// <summary>代碼維護的原因代碼：名稱依語系（CodeEntryDisplayExtensions），不同分類的代碼不會互相覆蓋。</summary>
    private async Task<Dictionary<string, string>> ReasonNamesAsync(string category, HashSet<string> codes, Dictionary<string, string> into)
    {
        var map = await db.CodeEntries.AsNoTracking()
            .Where(e => e.Category == category && codes.Contains(e.Code))
            .ToDisplayDictionaryAsync();
        foreach (var (k, v) in map) into[category + "|" + k] = v;
        return into;
    }

    /// <summary>依屬性名稱把代碼分到各主檔。</summary>
    internal sealed class CodeBag
    {
        public HashSet<string> Products { get; } = new();
        public HashSet<string> Companies { get; } = new();
        public HashSet<string> Uoms { get; } = new();
        public HashSet<string> Currencies { get; } = new();
        public HashSet<string> Taxes { get; } = new();
        public HashSet<string> Warehouses { get; } = new();
        public HashSet<string> Locations { get; } = new();
        public HashSet<string> WorkCenters { get; } = new();
        public HashSet<string> Machines { get; } = new();
        public HashSet<string> Accounts { get; } = new();
        public HashSet<string> TxTypes { get; } = new();
        public HashSet<string> ScrapReasons { get; } = new();
        public HashSet<string> AdjustReasons { get; } = new();

        /// <summary>屬性名結尾 → 分類。順序有意義：先比較長的結尾。</summary>
        private static readonly (string Suffix, Func<CodeBag, HashSet<string>> Target)[] Rules =
        {
            ("WorkCenterCode", b => b.WorkCenters),
            ("WorkCenterId", b => b.WorkCenters),   // 工序的 WorkCenterId 存的是工作中心代碼
            ("ScrapReasonCode", b => b.ScrapReasons),
            ("AdjustReasonCode", b => b.AdjustReasons),
            ("ProductCode", b => b.Products),
            ("MaterialCode", b => b.Products),
            ("ComponentCode", b => b.Products),
            ("SupplierCode", b => b.Companies),
            ("CustomerCode", b => b.Companies),
            ("CompanyCode", b => b.Companies),
            ("VendorCode", b => b.Companies),
            ("UomCode", b => b.Uoms),
            ("CurrencyCode", b => b.Currencies),
            ("TaxCode", b => b.Taxes),
            ("WarehouseCode", b => b.Warehouses),
            ("WhCode", b => b.Warehouses),
            ("LocationCode", b => b.Locations),
            ("MachineCode", b => b.Machines),
            ("AccountCode", b => b.Accounts),
            // 單據的 TypeCode 一律是庫存異動類別（PI／OI／NO／NR…，FK 到 InventoryTransactionType）
            ("TypeCode", b => b.TxTypes),
        };

        private static readonly Dictionary<Type, (PropertyInfo Prop, Func<CodeBag, HashSet<string>> Target)[]> StringProps = new();
        private static readonly Dictionary<Type, PropertyInfo[]> CollectionProps = new();

        /// <summary>走訪物件的字串屬性；集合屬性（明細）再往下一層。導覽屬性的單一物件不走，避免沿著關聯爬整張圖。</summary>
        public void CollectFrom(object? obj, int depth)
        {
            if (obj == null) return;
            if (obj is IEnumerable seq and not string)
            {
                foreach (var item in seq) CollectFrom(item, depth);
                return;
            }
            var (props, collections) = Describe(obj.GetType());
            foreach (var (prop, target) in props)
            {
                if (prop.GetValue(obj) is string v && !string.IsNullOrWhiteSpace(v)) target(this).Add(v);
            }
            if (depth >= 1) return;
            foreach (var cp in collections)
                if (cp.GetValue(obj) is IEnumerable children) CollectFrom(children, depth + 1);
        }

        private static ((PropertyInfo, Func<CodeBag, HashSet<string>>)[] Props, PropertyInfo[] Collections) Describe(Type t)
        {
            lock (StringProps)
            {
                if (!StringProps.TryGetValue(t, out var props))
                {
                    var all = t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0).ToArray();
                    props = all.Where(p => p.PropertyType == typeof(string))
                        .Select(p => (Prop: p, Rule: Rules.FirstOrDefault(r => p.Name.EndsWith(r.Suffix, StringComparison.Ordinal))))
                        .Where(x => x.Rule.Target != null)
                        .Select(x => (x.Prop, x.Rule.Target))
                        .ToArray();
                    StringProps[t] = props;
                    CollectionProps[t] = all.Where(p => p.PropertyType != typeof(string)
                        && typeof(IEnumerable).IsAssignableFrom(p.PropertyType)
                        && p.PropertyType.IsGenericType
                        && !p.PropertyType.GetGenericArguments()[0].IsPrimitive
                        && p.PropertyType.GetGenericArguments()[0] != typeof(string)).ToArray();
                }
                return (props, CollectionProps[t]);
            }
        }
    }
}

/// <summary>列印單用的代碼→名稱查表。查不到時名稱為空字串，<c>…With</c> 系列則只回代碼。</summary>
public sealed class PrintNames
{
    internal Dictionary<string, string> Products { get; set; } = new();
    internal Dictionary<string, string> Companies { get; set; } = new();
    internal Dictionary<string, string> Uoms { get; set; } = new();
    internal Dictionary<string, string> Currencies { get; set; } = new();
    internal Dictionary<string, string> Taxes { get; set; } = new();
    internal Dictionary<string, string> Warehouses { get; set; } = new();
    internal Dictionary<string, string> Locations { get; set; } = new();
    internal Dictionary<string, string> WorkCenters { get; set; } = new();
    internal Dictionary<string, string> Machines { get; set; } = new();
    internal Dictionary<string, string> Accounts { get; set; } = new();
    internal Dictionary<string, string> TxTypes { get; set; } = new();
    internal Dictionary<string, string> Reasons { get; set; } = new();

    private static string Get(Dictionary<string, string> map, string? code) =>
        code != null && map.TryGetValue(code, out var n) ? n : "";

    /// <summary>「代碼 名稱」；名稱與代碼相同或查無時只回代碼。</summary>
    private static string With(Dictionary<string, string> map, string? code)
    {
        if (string.IsNullOrEmpty(code)) return "";
        var n = Get(map, code);
        return string.IsNullOrWhiteSpace(n) || n == code ? code : $"{code} {n}";
    }

    public string Product(string? code) => Get(Products, code);
    public string Company(string? code) => Get(Companies, code);
    public string CompanyWith(string? code) => With(Companies, code);
    public string UomWith(string? code) => With(Uoms, code);
    public string CurrencyWith(string? code) => With(Currencies, code);
    public string TaxWith(string? code) => With(Taxes, code);
    public string WarehouseWith(string? code) => With(Warehouses, code);
    public string LocationWith(string? code) => With(Locations, code);
    /// <summary>倉庫＋儲位合成一格「W01 原料倉 / A-01 A 區」；兩者都空回空字串。</summary>
    public string PlaceWith(string? wh, string? loc) =>
        string.Join(" / ", new[] { WarehouseWith(wh), LocationWith(loc) }.Where(x => x.Length > 0));
    public string WorkCenterWith(string? code) => With(WorkCenters, code);
    public string MachineWith(string? code) => With(Machines, code);
    public string Account(string? code) => Get(Accounts, code);
    public string TxTypeWith(string? code) => With(TxTypes, code);
    public string ScrapReasonWith(string? code) =>
        string.IsNullOrEmpty(code) ? "" : Reasons.TryGetValue("PZ.ScrapReason|" + code, out var n) && n != code ? $"{code} {n}" : code;
    public string AdjustReasonWith(string? code) =>
        string.IsNullOrEmpty(code) ? "" : Reasons.TryGetValue("PZ.AdjustReason|" + code, out var n) && n != code ? $"{code} {n}" : code;
}

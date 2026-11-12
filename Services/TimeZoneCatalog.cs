using System.ComponentModel.DataAnnotations;

namespace SmartFactory.MES.Services
{
    /// <summary>下拉的一列：儲存值（IANA ID）、顯示字串、排序用的標準偏移。</summary>
    /// <param name="Id">儲存進資料庫的值。一律是 IANA ID（Asia/Taipei），不是 Windows ID。</param>
    /// <param name="Display">畫面顯示，例：<c>Asia/Taipei (UTC+08:00)</c>。</param>
    /// <param name="BaseUtcOffset">
    /// <b>標準</b>偏移，不是此刻的偏移——夏令時間國家的兩者不同。
    /// 只用來排序；拿它做換算是錯的（換算一律走 <see cref="TimeZoneInfo"/>）。
    /// </param>
    public sealed record TimeZoneOption(string Id, string Display, TimeSpan BaseUtcOffset);

    /// <summary>
    /// 時區下拉的來源。
    ///
    /// <para><b>為什麼是 <see cref="TimeZoneInfo.GetSystemTimeZones()"/> 而不是一張資料表</b>：
    /// 時區清單是 IANA 維護的外部事實，不是業務分類——tzdata 每年都在改（新分裂的時區、
    /// 廢止的夏令時規則）。抄進資料表就是一份沒有人會更新的快照，而且「使用者打錯字」
    /// 會變成一個沒有任何回饋的輸入面：<see cref="TimeZoneInfo.FindSystemTimeZoneById"/>
    /// 對 <c>""</c>／<c>"   "</c>／<c>"Asia/Taipeh"</c> 一律丟例外，而
    /// <see cref="DateTimeService"/> 接住後只會退回預設時區——報表日期因此差一天，
    /// 但沒有任何一則訊息提到時區。</para>
    ///
    /// <para><b>為什麼要正規化成 IANA</b>：同一份程式部署在 Windows 主機上時，
    /// <c>GetSystemTimeZones()</c> 回的是 Windows ID（<c>Taipei Standard Time</c>）。
    /// 若照收存進 <c>Plant.DefaultTimeZone</c>，這筆資料換到 Linux 主機就解不開了——
    /// **儲存值會隨部署 OS 漂移**。故一律以
    /// <see cref="TimeZoneInfo.TryConvertWindowsIdToIanaId(string, out string?)"/> 收斂到 IANA，
    /// 而 .NET 在兩種 OS 上都認得 IANA ID。現有資料本來就是 IANA，不需要 migration。</para>
    /// </summary>
    public interface ITimeZoneCatalog
    {
        /// <summary>全部可選時區，依標準偏移排序（UTC−11 → UTC+14）。</summary>
        IReadOnlyList<TimeZoneOption> Options { get; }

        /// <summary>
        /// 下拉要顯示的選項，並保證 <paramref name="currentId"/> 一定在其中。
        ///
        /// <para><b>這一層是必要的</b>：資料庫裡的值可能是匯入／SQL 直改寫進來的、
        /// 或是舊部署留下的 Windows ID。若它不在清單裡，瀏覽器會把 select 顯示成
        /// 第一個選項，使用者只是來改個廠名、按下存檔——**時區就被靜靜換掉了**。</para>
        /// </summary>
        IReadOnlyList<TimeZoneOption> OptionsFor(string? currentId);

        /// <summary>清單顯示用。認不得的值原樣回傳，不吞掉也不改寫。</summary>
        string DisplayOf(string? id);
    }

    /// <inheritdoc />
    public sealed class TimeZoneCatalog : ITimeZoneCatalog
    {
        /// <summary>
        /// 清單在行程存續期間只建一次。tzdata 更新需要重啟——這是刻意的取捨：
        /// 每次開啟廠別維護都掃一次系統時區（本機實測 419 筆）換不到任何東西。
        /// </summary>
        private static readonly Lazy<IReadOnlyList<TimeZoneOption>> Cached =
            new(BuildOptions, LazyThreadSafetyMode.ExecutionAndPublication);

        public IReadOnlyList<TimeZoneOption> Options => Cached.Value;

        public IReadOnlyList<TimeZoneOption> OptionsFor(string? currentId)
        {
            if (string.IsNullOrWhiteSpace(currentId)) return Options;

            var normalized = Normalize(currentId);
            if (Options.Any(o => o.Id == normalized)) return Options;

            // 認不得的值也要出現在下拉裡，而且要看得出它是現存值——
            // 不然使用者無從發現「這個廠的時區是個壞字串」。
            var extra = new TimeZoneOption(currentId, currentId, TimeSpan.MinValue);
            return new[] { extra }.Concat(Options).ToList();
        }

        public string DisplayOf(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return "-";
            var normalized = Normalize(id);
            return Options.FirstOrDefault(o => o.Id == normalized)?.Display ?? id;
        }

        /// <summary>
        /// 這個代碼是否解得開。判準就是<b>實際去解一次</b>——與
        /// <see cref="DateTimeService"/> 執行期會做的事完全相同，不另外維護一份白名單
        /// （白名單與實際行為遲早會分岔，而分岔的症狀是無聲的）。
        /// </summary>
        public static bool IsResolvable(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            try
            {
                TimeZoneInfo.FindSystemTimeZoneById(id);
                return true;
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                return false;
            }
        }

        /// <summary>Windows ID 收斂成 IANA；已是 IANA 或認不得就原樣回傳。</summary>
        public static string Normalize(string id)
            => TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana) ? iana : id;

        private static IReadOnlyList<TimeZoneOption> BuildOptions()
        {
            var seen = new Dictionary<string, TimeZoneOption>(StringComparer.Ordinal);

            foreach (var tz in TimeZoneInfo.GetSystemTimeZones())
            {
                // HasIanaId 在 Linux 上恆真；Windows 上才需要轉換。
                var id = tz.HasIanaId ? tz.Id : Normalize(tz.Id);

                // 轉不出 IANA 的（Windows 的少數虛擬時區）刻意不列入：
                // 存進去換一台主機就解不開，比少一個選項糟。
                if (!tz.HasIanaId && id == tz.Id) continue;

                seen.TryAdd(id, new TimeZoneOption(id, Describe(id, tz.BaseUtcOffset), tz.BaseUtcOffset));
            }

            // "UTC" 在兩種 OS 上都解得開，但 Windows 會把它正規化成 "Etc/UTC"。
            // 現有資料用的是 "UTC"，保留這一列才不會讓既有廠別落進 OptionsFor 的例外分支。
            seen.TryAdd("UTC", new TimeZoneOption("UTC", Describe("UTC", TimeSpan.Zero), TimeSpan.Zero));

            return seen.Values
                .OrderBy(o => o.BaseUtcOffset)
                .ThenBy(o => o.Id, StringComparer.Ordinal)
                .ToList();
        }

        private static string Describe(string id, TimeSpan offset)
        {
            var sign = offset < TimeSpan.Zero ? "-" : "+";
            return $"{id} (UTC{sign}{Math.Abs(offset.Hours):00}:{Math.Abs(offset.Minutes):00})";
        }
    }

    /// <summary>
    /// 時區代碼欄位的驗證。判準與執行期完全一致（<see cref="TimeZoneCatalog.IsResolvable"/>）。
    ///
    /// <para><b>換掉下拉只是一半</b>：下拉擋不住匯入、SQL 直改與 API 寫進來的值，
    /// 而壞值的症狀是「日期差一天」這種查不到源頭的現象。空值仍然合法——
    /// 時區欄位本來就可以不填（由上一層的預設接手）。</para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
    public sealed class ValidTimeZoneAttribute : ValidationAttribute
    {
        // 無 ErrorMessage 的驗證屬性由 LocalizedValidationExtensions 把這個預設訊息
        // 回填成 resx 查找 key（見該檔 LocalizableDefaultMessageProvider）。
        public ValidTimeZoneAttribute()
            : base("The {0} field must be a valid time zone identifier.") { }

        public override bool IsValid(object? value)
            => value is not string s || string.IsNullOrWhiteSpace(s) || TimeZoneCatalog.IsResolvable(s);
    }
}

using System.ComponentModel.DataAnnotations;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.Services
{
    /// <summary>
    /// 時區下拉的來源與驗證（<see cref="TimeZoneCatalog"/>）。
    ///
    /// <para><b>取代的是什麼</b>：四個畫面各自硬編 <c>Asia/Taipei</c>／<c>Asia/Shanghai</c>／
    /// <c>UTC</c> 三個選項，從第一個 commit 起就是這樣，而後端完全沒有白名單。
    /// 三個選項的實際後果是：跨時區部署（美洲廠、歐洲廠）在畫面上根本設定不出來。</para>
    ///
    /// <para><b>這一組刻意不驗「剛好 419 筆」</b>：那是本機 tzdata 的數字，
    /// 換一個基底映像就會變，而且變了也不代表壞掉。驗的是性質——
    /// 覆蓋到各大洲、每一筆都解得開、排序穩定、現存值不會被靜靜換掉。</para>
    /// </summary>
    public class TimeZoneCatalogTests
    {
        private readonly ITimeZoneCatalog _catalog = new TimeZoneCatalog();

        [Fact]
        public void Options_CoverFarMoreThanTheLegacyThreeChoices()
        {
            // 舊畫面只給得出 UTC 與 UTC+8 兩種偏移，Wave T「兩個廠在不同時區」
            // 的人工驗收因此配不出美洲／歐洲的案例。
            Assert.True(_catalog.Options.Count > 100, $"只取到 {_catalog.Options.Count} 筆");

            var ids = _catalog.Options.Select(o => o.Id).ToHashSet();
            Assert.Contains("Asia/Taipei", ids);
            Assert.Contains("America/New_York", ids);
            Assert.Contains("Europe/London", ids);
            Assert.Contains("UTC", ids);   // 現有資料用的就是這個字串
        }

        [Fact]
        public void Options_AreAllResolvable()
        {
            // 清單裡出現、存下去卻解不開，等於把壞值的產生器搬進畫面。
            var broken = _catalog.Options.Where(o => !TimeZoneCatalog.IsResolvable(o.Id)).ToList();
            Assert.True(broken.Count == 0, "解不開：" + string.Join(", ", broken.Select(b => b.Id)));
        }

        [Fact]
        public void Options_AreOrderedByUtcOffset()
        {
            var offsets = _catalog.Options.Select(o => o.BaseUtcOffset).ToList();
            Assert.Equal(offsets.OrderBy(o => o).ToList(), offsets);
        }

        [Fact]
        public void Display_ShowsTheUtcOffset()
        {
            var taipei = _catalog.Options.Single(o => o.Id == "Asia/Taipei");
            Assert.Equal("Asia/Taipei (UTC+08:00)", taipei.Display);
        }

        [Fact]
        public void Display_KeepsTheSignOfWesternOffsets()
        {
            // 「-05:00」被寫成「+05:00」的話，畫面看起來仍然完全正常。
            var newYork = _catalog.Options.Single(o => o.Id == "America/New_York");
            Assert.Contains("(UTC-05:00)", newYork.Display);
        }

        /// <summary>
        /// 資料庫裡的值不在清單中時，**它仍然必須出現在下拉裡**。
        /// 少了這一條，使用者只是來改個廠名、按下存檔，時區就被瀏覽器選成第一個選項送出去了——
        /// 沒有任何提示，而症狀要等到報表日期差一天才被發現。
        /// </summary>
        [Fact]
        public void OptionsFor_KeepsAnUnknownStoredValue()
        {
            var options = _catalog.OptionsFor("Mars/Olympus_Mons");

            Assert.Equal("Mars/Olympus_Mons", options[0].Id);
            Assert.Equal(_catalog.Options.Count + 1, options.Count);
        }

        [Fact]
        public void OptionsFor_DoesNotDuplicateAKnownValue()
        {
            var options = _catalog.OptionsFor("Asia/Taipei");

            Assert.Equal(_catalog.Options.Count, options.Count);
            Assert.Single(options.Where(o => o.Id == "Asia/Taipei"));
        }

        [Fact]
        public void OptionsFor_TreatsWindowsIdAsTheSameZone()
        {
            // 舊部署或匯入資料可能是 Windows ID。它解得開，也對應得到清單裡的 IANA 那一列，
            // 所以不該被當成「未知值」再加一列出來。
            var options = _catalog.OptionsFor("Taipei Standard Time");

            Assert.Equal(_catalog.Options.Count, options.Count);
        }

        [Theory]
        [InlineData(null, "-")]
        [InlineData("", "-")]
        [InlineData("Mars/Olympus_Mons", "Mars/Olympus_Mons")]   // 認不得就原樣顯示，不吞掉
        public void DisplayOf_HandlesMissingAndUnknownValues(string? id, string expected)
        {
            Assert.Equal(expected, _catalog.DisplayOf(id));
        }

        [Fact]
        public void DisplayOf_ResolvesWindowsIdToTheIanaRow()
        {
            Assert.Equal("Asia/Taipei (UTC+08:00)", _catalog.DisplayOf("Taipei Standard Time"));
        }

        [Theory]
        [InlineData("Asia/Taipei", true)]
        [InlineData("UTC", true)]
        [InlineData("Taipei Standard Time", true)]   // Windows ID 在 Linux 上也解得開
        [InlineData("Asia/Taipeh", false)]           // 打錯一個字母
        [InlineData("   ", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsResolvable_MatchesWhatTheRuntimeWillDo(string? id, bool expected)
        {
            Assert.Equal(expected, TimeZoneCatalog.IsResolvable(id));
        }
    }

    /// <summary>
    /// 欄位驗證（<see cref="ValidTimeZoneAttribute"/>）。
    ///
    /// <para><b>下拉只是一半</b>：換掉畫面擋不住匯入、SQL 直改與 API 寫進來的值，
    /// 而那些路徑產生的壞值不會讓任何操作失敗——只會讓時間換算靜靜退回預設時區。</para>
    /// </summary>
    public class ValidTimeZoneAttributeTests
    {
        private readonly ValidTimeZoneAttribute _attribute = new();

        [Theory]
        [InlineData(null)]        // 未填＝由上一層的預設接手，合法
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("Asia/Taipei")]
        [InlineData("America/New_York")]
        [InlineData("UTC")]
        public void Accepts(object? value) => Assert.True(_attribute.IsValid(value));

        [Theory]
        [InlineData("Asia/Taipeh")]
        [InlineData("GMT+8")]
        [InlineData("台北")]
        public void Rejects(object? value) => Assert.False(_attribute.IsValid(value));

        /// <summary>
        /// 預設訊息必須帶著 <c>{0}</c>：<c>LocalizedValidationExtensions</c> 會把它
        /// 回填成 resx 查找 key，欄位名由 <c>{0}</c> 代入。寫死欄位名的訊息在另一個語系會變成中英夾雜。
        /// </summary>
        [Fact]
        public void DefaultMessage_IsALocalizableTemplate()
        {
            Assert.Contains("{0}", _attribute.FormatErrorMessage("{0}"));
            Assert.Contains("預設時區", _attribute.FormatErrorMessage("預設時區"));
        }
    }
}

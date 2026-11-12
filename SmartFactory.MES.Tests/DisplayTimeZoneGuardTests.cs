using System.Text.RegularExpressions;

namespace SmartFactory.MES.Tests
{
    /// <summary>
    /// 顯示用時間戳不得走 <c>DateTime.ToLocalTime()</c>（BCL 實例方法）。
    ///
    /// <para><b>為什麼這不是吹毛求疵</b>：BCL 的那個多載換算到的是**伺服器**時區，
    /// 而本系統的顯示語意一律是**廠別**時區（<see cref="Services.IDateTimeService.ToLocalTime"/>，
    /// 解析 <see cref="Services.IUserContext.TimeZoneId"/> 的「廠別 → 個人 → 預設」鏈）。
    /// 兩者在開發機與 CI 上剛好一致——容器與 <c>launchSettings.json</c> 都跑
    /// <c>TZ=UTC</c>，而欄位存的就是 UTC，於是換算前後的數字一樣，**測試與畫面都不會有任何異狀**。
    /// 差別只有在正式主機的時區不是 UTC 時才現形，而那時症狀是「時間少了八小時」，
    /// 沒有例外、沒有日誌，看起來像資料本身就錯了。</para>
    ///
    /// <para>R22(b)（部署專案側）守的是「不得讀伺服器時<b>鐘</b>」——
    /// <c>DateTime.Now</c>／<c>Today</c> 那一類。這一條守的是另一半：**不得用伺服器時<b>區</b>換算**。
    /// 兩者是不同的錯誤，樣式也不同，所以掃描要分開。</para>
    ///
    /// <para><b>為什麼掃描範圍是「排除式」而不是列出目錄</b>：原本列的是
    /// Views／Pages／Controllers／Services／Models／Areas。那份清單在本專案剛好涵蓋了
    /// 全部產品程式碼，所以「零例外」是真的；但**同一份檔案到了部署專案只掃得到
    /// 174 / 1943 個檔**——它的程式碼大多在 <c>Modules/</c> 之下，而那不在清單裡。
    /// 違規 25 處，守門照樣回綠。白名單的問題不在於漏了哪一個目錄，
    /// 在於**它察覺不到自己不完整**：新目錄、fork 的不同結構，都不會讓它紅。
    /// 改成「掃全部、扣掉測試與工具專案」之後，清單要維護的是例外而不是範圍，
    /// 而例外少掉一個只會讓規則變嚴、不會讓它變瞎。</para>
    ///
    /// <para><b>自我檢查</b>：掃描器若因為改了副檔名或目錄結構而讀到的檔案量異常地少，
    /// 它會永遠是綠的——那比沒有規則更糟，因為會讓人以為有人在看。故一併斷言掃描量。</para>
    /// </summary>
    public class DisplayTimeZoneGuardTests
    {
        /// <summary>實例方法、無引數＝BCL 版。服務版一律帶引數（<c>DateTimeSvc.ToLocalTime(x)</c>）。</summary>
        private static readonly Regex BclToLocalTime = new(@"\.\s*ToLocalTime\s*\(\s*\)", RegexOptions.Compiled);

        private static string RepoRoot()
        {
            var dir = AppContext.BaseDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (File.Exists(Path.Combine(dir, "SmartFactory.MES.sln"))) return dir;
                dir = Path.GetDirectoryName(dir);
            }
            throw new InvalidOperationException("找不到 SmartFactory.MES.sln，無法定位 Repo 根目錄。");
        }

        /// <summary>
        /// 不掃的頂層目錄。測試與工具專案不是產品的顯示程式碼；
        /// 部署專案獨有的那幾個（LicenseAuthority／Security）在這裡也列著，
        /// 這樣兩邊是同一份檔案，不必為了範圍差異製造 fork 漂移。
        /// </summary>
        private static readonly HashSet<string> SkippedTopLevel = new(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", "node_modules", ".git", ".vs", "wwwroot",
            "SmartFactory.MES.Tests", "SmartFactory.MES.IntegrationTests",
            "SmartFactory.MES.UITests", "SmartFactory.MES.MachineId",
            "SmartFactory.MES.LicenseAuthority", "SmartFactory.MES.Security",
        };

        private static IEnumerable<string> SourceFiles()
        {
            var root = RepoRoot();
            foreach (var f in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            {
                if (!f.EndsWith(".cs") && !f.EndsWith(".cshtml")) continue;

                var rel = Path.GetRelativePath(root, f);
                var segments = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                // 任一層落在排除清單就跳過（bin／obj 出現在專案子目錄底下，不只頂層）。
                if (segments.Any(SkippedTopLevel.Contains)) continue;

                yield return f;
            }
        }

        [Fact]
        public void NoDisplayCode_UsesTheServerTimeZoneForConversion()
        {
            var offenders = new List<string>();
            int probed = 0;

            foreach (var file in SourceFiles())
            {
                probed++;
                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (BclToLocalTime.IsMatch(lines[i]))
                        offenders.Add($"{Path.GetRelativePath(RepoRoot(), file)}:{i + 1}  {lines[i].Trim()}");
                }
            }

            // 門檻對著本專案的實際規模訂（撰寫時本專案 215 檔；部署專案更多，同一個門檻
            // 對它偏鬆，但這個斷言要抓的是「掃描器整個失效」，不是精確計數）。
            // 訂太低就等於沒訂：先前的 50 在只掃到 174 / 1943 個檔時照樣通過。
            Assert.True(probed >= 150,
                $"掃描器只讀到 {probed} 個檔案，遠少於預期——規則可能已經失效（目錄改名？排除清單誤擴大？），" +
                "此時它會永遠是綠的。請先修掃描範圍，不要放過這個訊號。");

            Assert.True(offenders.Count == 0,
                "以下位置用 BCL 的 DateTime.ToLocalTime() 換算顯示時間，那是伺服器時區；\n" +
                "顯示一律走 IDateTimeService.ToLocalTime(x)（檢視內可直接用全域注入的 DateTimeSvc）。\n" +
                "若該值本來就是「廠別壁鐘」而非真 UTC，正確做法是不換算，不是換一個方法。\n\n" +
                string.Join("\n", offenders));
        }
    }
}

using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SmartFactory.MES.Services
{
    public interface IDateTimeService
    {
        DateTime Now { get; }
        DateTime ToLocalTime(DateTime utcTime);
        DateTime ToUtcTime(DateTime localTime);

        /// <summary>
        /// 取得指定時區的今日日期。若未指定則使用當前使用者時區。
        /// 用於建單時以廠別時區（Plant.DefaultTimeZone）決定 MovementDate。
        /// </summary>
        DateOnly GetLocalDate(string? timeZoneId = null);
    }

    public class DateTimeService : IDateTimeService
    {
        /// <summary>時區代碼解不開時的最後退路。與 <see cref="IUserContext.TimeZoneId"/> 回 null 時的行為一致。</summary>
        public const string FallbackTimeZoneId = "Asia/Taipei";

        private readonly IUserContext _userContext;
        private readonly ILogger<DateTimeService> _logger;

        /// <param name="logger">
        /// 可省略**僅為了讓既有的測試呼叫端不必全部改寫**（DI 一律會注入真的 logger）。
        /// 省略時走 <see cref="NullLogger{T}"/>，只影響有沒有 log，不影響任何換算結果。
        /// </param>
        public DateTimeService(IUserContext userContext, ILogger<DateTimeService>? logger = null)
        {
            _userContext = userContext;
            _logger = logger ?? NullLogger<DateTimeService>.Instance;
        }

        public DateTime Now => ToLocalTime(DateTime.UtcNow);

        public DateTime ToLocalTime(DateTime utcTime)
        {
            // 確保 DateTime 為 UTC
            if (utcTime.Kind != DateTimeKind.Utc)
            {
                utcTime = DateTime.SpecifyKind(utcTime, DateTimeKind.Utc);
            }

            return TimeZoneInfo.ConvertTimeFromUtc(utcTime, Resolve(_userContext.TimeZoneId));
        }

        public DateTime ToUtcTime(DateTime localTime)
        {
            // 已經是 UTC 就不必換算。
            //
            // 這一條原本是靠「ConvertTimeToUtc 對 Kind=Utc 的輸入會拋例外」加上 catch 達成的，
            // 而同一個 catch 也接住「時區代碼解不開」——兩件完全不同的事共用一個處理：
            // 代碼打錯時它回的是 SpecifyKind(localTime, Utc)，也就是**把當地時間當成 UTC**，
            // 直接偏移一個時差存進資料庫，而且沒有任何訊息。兩件事現在分開處理。
            if (localTime.Kind == DateTimeKind.Utc) return localTime;

            var tz = Resolve(_userContext.TimeZoneId);

            // Kind=Local 的輸入會讓 ConvertTimeToUtc 改用「本機時區」而不是 tz——
            // 那是伺服器的時區，不是使用者的。一律先抹成 Unspecified。
            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified), tz);
        }

        public DateOnly GetLocalDate(string? timeZoneId = null)
        {
            var tz = Resolve(!string.IsNullOrWhiteSpace(timeZoneId) ? timeZoneId : _userContext.TimeZoneId);
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz));
        }

        /// <summary>
        /// 解出時區，解不開就退回 <see cref="FallbackTimeZoneId"/>——**並且留下一筆 log**。
        ///
        /// <para>原本三個方法各自 <c>catch { }</c> 後靜靜退回預設時區，連 log 都沒有。
        /// 於是「這份報表的日期差一天」在現場永遠查不到源頭：畫面一切正常、沒有錯誤、
        /// 資料庫裡那個壞掉的時區代碼也沒有人會去看。</para>
        /// </summary>
        private TimeZoneInfo Resolve(string? timeZoneId)
        {
            var id = string.IsNullOrWhiteSpace(timeZoneId) ? FallbackTimeZoneId : timeZoneId;
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                _logger.LogWarning(ex,
                    "時區代碼「{TimeZoneId}」無法解析，本次時間換算退回 {FallbackTimeZoneId}。" +
                    "請檢查廠別主檔的預設時區或該使用者的個人時區設定。", id, FallbackTimeZoneId);
                return TimeZoneInfo.FindSystemTimeZoneById(FallbackTimeZoneId);
            }
        }
    }
}

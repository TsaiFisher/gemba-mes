using System.Collections.Concurrent;
using System.Reflection;
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: Xunit.TestFramework("SmartFactory.MES.UITests.OutcomeTrackingTestFramework", "SmartFactory.MES.UITests")]

namespace SmartFactory.MES.UITests
{
    /// <summary>
    /// 讓 fixture 知道「現在跑的是哪一支」與「剛跑完那支是紅是綠」——
    /// xUnit 2 的 fixture／<c>BeforeAfterTestAttribute</c> 都拿不到這兩件事，
    /// 而 <see cref="UiTestFixture"/> 需要它們來決定 Playwright trace 要不要留。
    ///
    /// <para><b>為什麼不用 Playwright.Xunit 內建的 <c>TestOk</c></b>：它靠
    /// <c>FirstChanceException</c> 判定，被 catch 掉的例外也算。本套件好幾處刻意
    /// 吞 <c>TimeoutException</c>（選單走訪、CrudPanelPage），照那套判定會把綠的也當紅。
    /// 這裡讀的是 xUnit 自己送出的 <c>ITestPassed</c>／<c>ITestFailed</c>，就是報表上那個結果。</para>
    ///
    /// <para><b>為什麼強制同步回報</b>：xUnit 預設的訊息匯流排是非同步派送，
    /// 「A 失敗」可能晚於「B 開始」才送到——B 的操作就會被記到 A 的 trace 裡，
    /// 或 A 在結果還沒到時就被當成綠的丟掉。同步匯流排保證訊息在測試執行緒上依序送達。</para>
    /// </summary>
    public static class TestOutcomeTracker
    {
        /// <summary>只追蹤這個 collection：它的測試序列執行，「目前那一支」才有唯一答案。</summary>
        public const string TrackedCollection = "UI Tests";

        private static readonly ConcurrentDictionary<string, bool> _failed = new();
        private static volatile CurrentTest? _current;

        /// <summary><see cref="TrackedCollection"/> 內正在執行的測試；尚未開始時為 null。</summary>
        public static CurrentTest? Current => _current;

        /// <summary>已結束的測試是否失敗；尚無結果時回傳 null。</summary>
        public static bool? Failed(string key) => _failed.TryGetValue(key, out var f) ? f : null;

        internal static void OnMessage(IMessageSinkMessage message)
        {
            switch (message)
            {
                case ITestStarting m when IsTracked(m):
                    _current = new CurrentTest(KeyOf(m.Test), m.Test.DisplayName);
                    break;
                case ITestFailed m when IsTracked(m):
                    _failed[KeyOf(m.Test)] = true;
                    break;
                case ITestPassed m when IsTracked(m):
                    _failed[KeyOf(m.Test)] = false;
                    break;
                case ITestSkipped m when IsTracked(m):
                    _failed[KeyOf(m.Test)] = false;
                    break;
            }
        }

        private static bool IsTracked(ITestMessage m) => m.TestCollection.DisplayName == TrackedCollection;

        // Theory 的每一列共用 TestCase.UniqueID 的機率不為零（非序列化資料列會合併），加上顯示名稱才唯一。
        private static string KeyOf(ITest test) => test.TestCase.UniqueID + "|" + test.DisplayName;

        public sealed record CurrentTest(string Key, string DisplayName);
    }

    public class OutcomeTrackingTestFramework : XunitTestFramework
    {
        public OutcomeTrackingTestFramework(IMessageSink messageSink) : base(messageSink) { }

        protected override ITestFrameworkExecutor CreateExecutor(AssemblyName assemblyName)
            => new Executor(assemblyName, SourceInformationProvider, DiagnosticMessageSink);

        private sealed class Executor : XunitTestFrameworkExecutor
        {
            public Executor(AssemblyName assemblyName, ISourceInformationProvider sip, IMessageSink diagnosticMessageSink)
                : base(assemblyName, sip, diagnosticMessageSink) { }

            protected override void RunTestCases(
                IEnumerable<IXunitTestCase> testCases,
                IMessageSink executionMessageSink,
                ITestFrameworkExecutionOptions executionOptions)
            {
                executionOptions.SetValue("xunit.execution.SynchronousMessageReporting", true);
                base.RunTestCases(testCases, new RecordingSink(executionMessageSink), executionOptions);
            }
        }

        private sealed class RecordingSink : LongLivedMarshalByRefObject, IMessageSink
        {
            private readonly IMessageSink _inner;
            public RecordingSink(IMessageSink inner) => _inner = inner;

            public bool OnMessage(IMessageSinkMessage message)
            {
                TestOutcomeTracker.OnMessage(message);
                return _inner.OnMessage(message);
            }
        }
    }
}

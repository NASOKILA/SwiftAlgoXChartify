using System.Net;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using bybit.net.api;
using bybit.net.api.ApiServiceImp;
using bybit.net.api.Models;
using bybit.net.api.Models.Trade;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

var directory = Path.Combine(Path.GetTempPath(), "chartify-demo-checks-" + Guid.NewGuid().ToString("N"));
var journalPath = Path.Combine(directory, "executions.json");
var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["Bybit:Enabled"] = "true",
    ["Bybit:AllowedEventType"] = "signal",
    ["Bybit:ApiKey"] = "mock-key-not-a-real-credential",
    ["Bybit:ApiSecret"] = "mock-secret-not-a-real-credential"
}).Build();
var handler = new DemoHandler();
var timestamp = DateTimeOffset.UtcNow;
JsonElement Signal(int sequence, string type = "signal", string stop = "774.568") =>
    JsonSerializer.SerializeToElement(new
    {
        type, symbol = "QQQ", signalType = "buy", timeframe = "5",
        time = timestamp.AddSeconds(sequence).ToString("O"),
        longEntry = "778.46", longTp = "782.352", longSl = stop
    });

void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    Console.WriteLine("PASS: " + name);
}

try
{
    using (var trader = new BybitDemoTrader(configuration, NullLogger<BybitDemoTrader>.Instance,
        journalPath, new HttpClient(handler, disposeHandler: false)))
    {
        Check((await trader.ProcessAsync(Signal(0, "test"))).StartsWith("Skipped:"), "Test event blocked");
        var spySignal = JsonSerializer.SerializeToElement(new
        {
            type = "signal", symbol = "SPY", signalType = "buy", timeframe = "5",
            time = timestamp.ToString("O"), longEntry = "778.46", longTp = "782.352", longSl = "774.568"
        });
        Check((await trader.ProcessAsync(spySignal)).StartsWith("Skipped:"), "SPY signal rejected after QQQ switch");
        var unsupportedSignal = JsonSerializer.SerializeToElement(new
        {
            type = "signal", symbol = "AAPL", signalType = "buy", timeframe = "5",
            time = timestamp.ToString("O"), longEntry = "778.46", longTp = "782.352", longSl = "774.568",
            tradeQualityScore = "95.5"
        });
        Check((await trader.ProcessAsync(unsupportedSignal)).StartsWith("Skipped:"), "Unapproved symbol rejected");
        Check((await trader.ProcessAsync(Signal(0, "signal", "NaN"))).StartsWith("Skipped:"), "Invalid stop blocked");
        Check((await trader.ProcessAsync(Signal(-600))).StartsWith("Skipped:"), "Stale signal blocked");
        Check(handler.Requests == 0, "Rejected events made no API requests");
        handler.LastPrice = "600";
        Check((await trader.ProcessAsync(Signal(0))).Contains("more than 2%"), "Different venue price blocked");
        handler.LastPrice = "783";
        Check((await trader.ProcessAsync(Signal(0))).Contains("already crossed"), "Already crossed take profit blocked");
        handler.LastPrice = "778.46";
        Check((await trader.ProcessAsync(Signal(0))).StartsWith("Demo market fill"), "Demo market fill and protection verified");
        Check(handler.Orders == 1, "Exactly one order submitted");
        Check((await trader.ProcessAsync(Signal(0))).Contains("already attempted"), "Duplicate delivery blocked");
        Check((await trader.ProcessAsync(Signal(1))).Contains("position is already open"), "Market position blocks new signal");
        handler.OrderStatus = "Filled";
        handler.PositionSize = "2.56";
        Check((await trader.ProcessAsync(Signal(2))).Contains("position is already open"), "Open position blocks new signal");
        handler.PositionSize = "0";
        handler.ActiveOrders = true;
        Check((await trader.ProcessAsync(Signal(3))).Contains("active QQQUSDT"), "Active QQQUSDT order blocks new signal");
        handler.ActiveOrders = false;
    }

    using (var trader = new BybitDemoTrader(configuration, NullLogger<BybitDemoTrader>.Instance,
        journalPath, new HttpClient(handler, disposeHandler: false)))
    {
        Check((await trader.ProcessAsync(Signal(0))).Contains("already attempted"), "Duplicate protection survives restart");
        handler.FailSubmission = true;
        Check((await trader.ProcessAsync(Signal(4))).StartsWith("Paused:"), "Uncertain submission pauses execution");
        Check((await trader.ProcessAsync(Signal(5))).StartsWith("Paused:"), "No automatic retry after uncertainty");
    }

    using (var trader = new BybitDemoTrader(configuration, NullLogger<BybitDemoTrader>.Instance,
        journalPath, new HttpClient(handler, disposeHandler: false)))
    {
        Check((await trader.ProcessAsync(Signal(6))).StartsWith("Paused:"), "Uncertain submission stays blocked after restart");
    }
    Check(handler.Orders == 2, "Only initial submission and simulated uncertain attempt reached order API");

    // TEMPORARY DEMO TEST: remove the opt-in checks below when the exception is removed; keep default rejection tests.
    configuration["Bybit:AllowTestOrders"] = "true";
    configuration["Bybit:Enabled"] = "false";
    var testJournalPath = Path.Combine(directory, "test-executions.json");
    var testHandler = new DemoHandler();
    using (var trader = new BybitDemoTrader(configuration, NullLogger<BybitDemoTrader>.Instance,
        testJournalPath, new HttpClient(testHandler, disposeHandler: false)))
    {
        Check((await trader.ProcessAsync(Signal(0, "test"))).StartsWith("Dry run"), "Opted-in disabled test is dry run only");
        Check(testHandler.Requests == 2 && testHandler.Orders == 0, "Dry run uses only public quotes and contract limits");
        configuration["Bybit:Enabled"] = "true";
        Check((await trader.ProcessAsync(Signal(0))).Contains("test-only"), "Temporary mode rejects real signals");
        Check((await trader.ProcessAsync(Signal(-600, "test"))).Contains("stale"), "Test retains timestamp guard");
        testHandler.PositionSize = "2.56";
        Check((await trader.ProcessAsync(Signal(0, "test"))).Contains("position is already open"), "Test blocked by existing QQQUSDT position");
        testHandler.PositionSize = "0";
        testHandler.ActiveOrders = true;
        Check((await trader.ProcessAsync(Signal(0, "test"))).Contains("active QQQUSDT"), "Test blocked by pending QQQUSDT order");
        testHandler.ActiveOrders = false;
        var results = await Task.WhenAll(trader.ProcessAsync(Signal(0, "test")),
            trader.ProcessAsync(Signal(1, "test")));
        Check(results.Count(result => result.StartsWith("Demo market fill")) == 1 && testHandler.Orders == 1,
            "Only one concurrent test submits an order with capped risk and TP/SL");
        testHandler.PositionSize = "0";
        for (var sequence = 2; sequence <= 5; sequence++)
        {
            var result = await trader.ProcessAsync(Signal(sequence, "test"));
            Check(result.StartsWith("Demo market fill"), $"Test trade {sequence} of five can submit: {result}");
            testHandler.PositionSize = "0";
        }
        Check(testHandler.Orders == 5, "Exactly five distinct test trades can submit");
        Check((await trader.ProcessAsync(Signal(6, "test"))).Contains("allowance consumed"),
            "Sixth distinct test trade is blocked");
    }
    using (var trader = new BybitDemoTrader(configuration, NullLogger<BybitDemoTrader>.Instance,
        testJournalPath, new HttpClient(testHandler, disposeHandler: false)))
    {
        Check((await trader.ProcessAsync(Signal(7, "test"))).Contains("allowance consumed"),
            "Five-trade test allowance survives restart");
    }
    testHandler.Dispose();

    var failedTestHandler = new DemoHandler { FailSubmission = true };
    var failedTestJournalPath = Path.Combine(directory, "failed-test-executions.json");
    using (var trader = new BybitDemoTrader(configuration, NullLogger<BybitDemoTrader>.Instance,
        failedTestJournalPath, new HttpClient(failedTestHandler, disposeHandler: false)))
    {
        Check((await trader.ProcessAsync(Signal(0, "test"))).StartsWith("Paused:"), "Failed test submission pauses execution");
    }
    using (var trader = new BybitDemoTrader(configuration, NullLogger<BybitDemoTrader>.Instance,
        failedTestJournalPath, new HttpClient(failedTestHandler, disposeHandler: false)))
    {
        Check((await trader.ProcessAsync(Signal(1, "test"))).Contains("unresolved submission") && failedTestHandler.Orders == 1,
            "Uncertain test remains durably blocked without retry");
    }
    failedTestHandler.Dispose();

    var rejectedHandler = new DemoHandler { PrivateErrorCode = 10003 };
    using (var trader = new BybitDemoTrader(configuration, NullLogger<BybitDemoTrader>.Instance,
        Path.Combine(directory, "rejected-executions.json"), new HttpClient(rejectedHandler)))
    {
        var result = await trader.ProcessAsync(Signal(0, "test"));
        Check(result.Contains("Bybit code 10003") && result.Contains("checking open QQQUSDT orders"),
            "Structured SDK error identifies code and failed check: " + result);
        Check(!result.Contains("mock-key") && !result.Contains("sensitive-error") && rejectedHandler.Orders == 0,
            "SDK diagnostic omits raw error and credentials; no order submitted");
        Check((await trader.ProcessAsync(Signal(1, "test"))).StartsWith("Paused:"),
            "API check failure remains paused without automatic retry");
    }

    using (var authHandler = new DemoHandler { ServerTimeOffsetMilliseconds = -1530 })
    using (var authClient = new HttpClient(authHandler))
    {
        var result = await BybitDemoAuthenticationCheck.CheckAsync(configuration, authClient);
        Check(result.Success && authHandler.Requests == 1 && authHandler.Orders == 0,
            "Read-only authentication check succeeds with a signed GET and no orders");
    }
    var skewedClockHandler = new DemoHandler { ServerTimeOffsetMilliseconds = -1530 };
    using (var correctedClient = new HttpClient(new BybitTimestampSigningHandler(
        "mock-secret-not-a-real-credential", -2000, skewedClockHandler)))
    {
        var trade = new BybitTradeService(correctedClient, apiKey: "mock-key-not-a-real-credential",
            apiSecret: "mock-secret-not-a-real-credential", url: "https://api-demo.bybit.com",
            recvWindow: "20000");
        await trade.GetOpenOrders(Category.LINEAR, symbol: "QQQUSDT", limit: 1);
        Check(skewedClockHandler.LastPrivateTimestamp.HasValue &&
            Math.Abs(skewedClockHandler.LastPrivateTimestamp.Value -
                (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1530)) < 1000,
            "SDK request timestamp is corrected and re-signed for a clock 1.53 seconds ahead");
        var orderResult = await trade.PlaceOrder(Category.LINEAR, "QQQUSDT", Side.BUY,
            OrderType.MARKET, qty: "2.14", timeInForce: TimeInForce.IOC, positionIdx: 0,
            orderLinkId: "sax-timestamp-handler-test", takeProfit: "782.35", stopLoss: "774.57",
            tpTriggerBy: TriggerBy.LastPrice, slTriggerBy: TriggerBy.LastPrice,
            tpslMode: TpslMode.Full, tpOrderType: OrderType.MARKET, slOrderType: OrderType.MARKET);
        Check(orderResult?.Contains("mock-order-id") == true && skewedClockHandler.Orders == 1,
            "SDK handler re-signs the order body with the corrected timestamp");
    }
    using (var authHandler = new DemoHandler { PrivateErrorCode = 10004, PrivateHttpStatus = HttpStatusCode.Unauthorized })
    using (var authClient = new HttpClient(authHandler))
    {
        var result = await BybitDemoAuthenticationCheck.CheckAsync(configuration, authClient);
        Check(!result.Success && result.HttpStatus == 401 && result.BybitCode == 10004,
            "Read-only check preserves Bybit error code even on HTTP 401");
        var output = JsonSerializer.Serialize(result);
        Check(!output.Contains("mock-key") && !output.Contains("mock-secret") && !output.Contains("sensitive-error")
            && authHandler.Orders == 0, "Authentication diagnostic excludes credentials and raw errors");
    }
    using (var authHandler = new DemoHandler
    {
        PrivateResponseBody = "", HeaderErrorCode = 10003, PrivateHttpStatus = HttpStatusCode.Unauthorized
    })
    using (var authClient = new HttpClient(authHandler))
    {
        var result = await BybitDemoAuthenticationCheck.CheckAsync(configuration, authClient);
        Check(!result.Success && result.HttpStatus == 401 && result.BybitCode == 10003 && result.ResponseFormat == "empty",
            "Empty HTTP 401 preserves numeric Ret_code response header");
    }
    using (var authHandler = new DemoHandler
    {
        PrivateResponseBody = "<html>sensitive-error mock-secret</html>", PrivateHttpStatus = HttpStatusCode.Unauthorized
    })
    using (var authClient = new HttpClient(authHandler))
    {
        var result = await BybitDemoAuthenticationCheck.CheckAsync(configuration, authClient);
        Check(!result.Success && result.BybitCode is null && result.ResponseFormat == "non-JSON" &&
            !JsonSerializer.Serialize(result).Contains("sensitive-error"),
            "Non-JSON HTTP 401 is classified without exposing response text");
    }

    configuration["Bybit:AllowTestOrders"] = "false";
    async Task MarketCase(string name, DemoHandler marketHandler, JsonElement payload,
        string expectedResult, int expectedOrders)
    {
        using (marketHandler)
        using (var trader = new BybitDemoTrader(configuration, NullLogger<BybitDemoTrader>.Instance,
            Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json"), new HttpClient(marketHandler)))
        {
            var result = await trader.ProcessAsync(payload);
            Check(result.Contains(expectedResult) && marketHandler.Orders == expectedOrders, name + ": " + result);
            if (expectedResult == "Paused:")
                Check((await trader.ProcessAsync(Signal(2))).StartsWith("Paused:"), name + " blocks further trades");
        }
    }
    await MarketCase("Bybit ask difference resizes risk without using Chartify limit entry",
        new DemoHandler { LastPrice = "779.73", ExpectedQuantity = "1.68" }, Signal(0), "Demo market fill", 1);
    await MarketCase("BUY uses ask rather than last price",
        new DemoHandler { AskPrice = "779.73", ExpectedQuantity = "1.68", FillPrice = "779.73" },
        Signal(0), "Demo market fill", 1);
    var sellSignal = JsonSerializer.SerializeToElement(new
    {
        type = "signal", symbol = "QQQ", signalType = "sell", timeframe = "5", time = timestamp.ToString("O"),
        shortEntry = "778.46", shortTp = "774.568", shortSl = "782.352"
    });
    await MarketCase("SELL uses bid and preserves short TP/SL",
        new DemoHandler { ExpectedSide = "Sell", ExpectedStop = "782.35", ExpectedTarget = "774.57" },
        sellSignal, "Demo market fill", 1);
    await MarketCase("Market quantity uses maxMktOrderQty",
        new DemoHandler { MaximumMarketQuantity = "0.50", ExpectedQuantity = "0.50" },
        Signal(0), "Demo market fill", 1);
    await MarketCase("Zero-fill IOC cancellation does not retry",
        new DemoHandler { OrderStatus = "Cancelled", FilledQuantity = "0" },
        Signal(0), "without a fill", 1);
    await MarketCase("Partial IOC fill verifies actual quantity and protection",
        new DemoHandler { OrderStatus = "Cancelled", FilledQuantity = "1" },
        Signal(0), "Demo market fill", 1);
    await MarketCase("Actual fill exceeding risk pauses for manual review",
        new DemoHandler { FillPrice = "780" }, Signal(0), "Paused:", 1);
    await MarketCase("Unverified TP/SL pauses for manual review",
        new DemoHandler { ProtectionPresent = false }, Signal(0), "Paused:", 1);
    await MarketCase("Missing executable quote rejects entry",
        new DemoHandler { AskPrice = "0" }, Signal(0), "invalid Bybit bid/ask", 0);
    await MarketCase("Stale Bybit quote rejects entry",
        new DemoHandler { QuoteAgeMilliseconds = 30000 }, Signal(0), "quote timestamp", 0);
    configuration["Bybit:MarketRiskBufferPercent"] = "10";
    await MarketCase("Invalid market sizing buffer rejects entry", new DemoHandler(), Signal(0), "risk buffer", 0);
    configuration["Bybit:MarketRiskBufferPercent"] = null;

    var legacySpyJournal = Path.Combine(directory, "legacy-spy-executions.json");
    await File.WriteAllTextAsync(legacySpyJournal, JsonSerializer.Serialize(new[]
    {
        new { orderLinkId = "legacy-spy-order", symbol = "SPYUSDT", state = "Unknown", isTest = false }
    }));
    var qqqHandler = new DemoHandler();
    using (qqqHandler)
    using (var trader = new BybitDemoTrader(configuration, NullLogger<BybitDemoTrader>.Instance,
        legacySpyJournal, new HttpClient(qqqHandler)))
    {
        Check((await trader.ProcessAsync(Signal(1, "signal"))).StartsWith("Demo market fill"),
            "Legacy unresolved SPY record does not block QQQ execution");
    }

    var nvdaJournal = Path.Combine(directory, "nvda-executions.json");
    await File.WriteAllTextAsync(nvdaJournal, JsonSerializer.Serialize(new[]
    {
        new { orderLinkId = "old-qqq-order", symbol = "QQQUSDT", state = "Unknown", isTest = false }
    }));
    var nvdaHandler = new DemoHandler { TargetSymbol = "NVDAUSDT" };
    var nvdaSignal = JsonSerializer.SerializeToElement(new
    {
        type = "signal", symbol = "NVDA/USDT", signalType = "buy", timeframe = "5",
        time = timestamp.ToString("O"), longEntry = "778.46", longTp = "782.352", longSl = "774.568",
        tradeQualityScore = "95.5"
    });
    using (nvdaHandler)
    using (var trader = new BybitDemoTrader(configuration, NullLogger<BybitDemoTrader>.Instance,
        nvdaJournal, new HttpClient(nvdaHandler)))
    {
        var result = await trader.ProcessAsync(nvdaSignal);
        Check(result.StartsWith("Demo market fill and TP/SL verified:") && result.Contains(" NVDA at ") &&
            nvdaHandler.Orders == 1,
            "NVDA/USDT routes to NVDAUSDT; unresolved QQQ journal entry does not block it");
    }

    var metaSignal = JsonSerializer.SerializeToElement(new
    {
        type = "signal", symbol = "META", signalType = "buy", timeframe = "5",
        time = DateTimeOffset.UtcNow.ToString("O"), longEntry = "709.40", longTp = "713.00", longSl = "706.00"
    });
    var metaHandler = new DemoHandler
    {
        TargetSymbol = "METAUSDT", LastPrice = "707.42", BidPrice = "707.40", AskPrice = "707.46",
        ExpectedQuantity = "2.82", ExpectedStop = "706.00", ExpectedTarget = "713.00",
        MaximumMarketQuantity = "500"
    };
    using (metaHandler)
    using (var trader = new BybitDemoTrader(configuration, NullLogger<BybitDemoTrader>.Instance,
        Path.Combine(directory, "meta-executions.json"), new HttpClient(metaHandler)))
    {
        var result = await trader.ProcessAsync(metaSignal);
        Check(result.StartsWith("Demo market fill and TP/SL verified:") && result.Contains(" META at 707.42") &&
            metaHandler.Orders == 1 && metaHandler.SubmittedQuantity == "2.82",
            "META routes to METAUSDT and accepts a 0.28% Chartify/Bybit entry gap using Bybit sizing");
    }

    var googlSignal = JsonSerializer.SerializeToElement(new
    {
        type = "signal", symbol = "GOOGL/USDT", signalType = "buy", timeframe = "5",
        time = DateTimeOffset.UtcNow.ToString("O"), longEntry = "340.09", longTp = "345.00", longSl = "335.00"
    });
    var googlHandler = new DemoHandler
    {
        TargetSymbol = "GOOGLUSDT", LastPrice = "340.09", BidPrice = "340.08", AskPrice = "340.09",
        ExpectedQuantity = "1.83", ExpectedStop = "335.00", ExpectedTarget = "345.00",
        MaximumMarketQuantity = "1000"
    };
    using (googlHandler)
    using (var trader = new BybitDemoTrader(configuration, NullLogger<BybitDemoTrader>.Instance,
        Path.Combine(directory, "googl-executions.json"), new HttpClient(googlHandler)))
    {
        var result = await trader.ProcessAsync(googlSignal);
        Check(result.StartsWith("Demo market fill and TP/SL verified:") && result.Contains(" GOOGL at 340.09") &&
            googlHandler.Orders == 1 && googlHandler.SubmittedQuantity == "1.83",
            "GOOGL/USDT routes to GOOGLUSDT and verifies an independent trade");
    }
}
finally
{
    handler.Dispose();
    if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
}

sealed class DemoHandler : HttpMessageHandler
{
    public int Requests { get; private set; }
    public int Orders { get; private set; }
    public string OrderStatus { get; set; } = "Filled";
    public string PositionSize { get; set; } = "0";
    public string LastPrice { get; set; } = "778.46";
    public bool ActiveOrders { get; set; }
    public bool FailSubmission { get; set; }
    public int PrivateErrorCode { get; set; }
    public HttpStatusCode PrivateHttpStatus { get; set; } = HttpStatusCode.OK;
    public string? PrivateResponseBody { get; set; }
    public int? HeaderErrorCode { get; set; }
    public string TargetSymbol { get; set; } = "QQQUSDT";
    public int ServerTimeOffsetMilliseconds { get; set; }
    public long? LastPrivateTimestamp { get; private set; }
    public string? SubmittedLinkId { get; private set; }
    public string SubmittedQuantity { get; private set; } = "0";
    public string ExpectedQuantity { get; set; } = "2.14";
    public string ExpectedSide { get; set; } = "Buy";
    public string ExpectedStop { get; set; } = "774.57";
    public string ExpectedTarget { get; set; } = "782.35";
    public string? BidPrice { get; set; }
    public string? AskPrice { get; set; }
    public string? FillPrice { get; set; }
    public string? FilledQuantity { get; set; }
    public string MaximumMarketQuantity { get; set; } = "1300";
    public bool ProtectionPresent { get; set; } = true;
    public int QuoteAgeMilliseconds { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests++;
        if (request.RequestUri?.Host != "api-demo.bybit.com")
            throw new InvalidOperationException("Non-demo API host used.");
        var path = request.RequestUri.AbsolutePath;
        if (!path.StartsWith("/v5/market/", StringComparison.Ordinal) &&
            (!request.Headers.Contains("X-BAPI-SIGN") || !request.Headers.Contains("X-BAPI-API-KEY")))
            throw new InvalidOperationException("Unsigned private request.");
        if (!path.StartsWith("/v5/market/", StringComparison.Ordinal))
        {
            var apiKey = request.Headers.GetValues("X-BAPI-API-KEY").Single();
            var timestamp = request.Headers.GetValues("X-BAPI-TIMESTAMP").Single();
            var receiveWindow = request.Headers.GetValues("X-BAPI-RECV-WINDOW").Single();
            if (receiveWindow != "20000")
                throw new InvalidOperationException("Unexpected SDK receive window.");
            var serverTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ServerTimeOffsetMilliseconds;
            if (!long.TryParse(timestamp, out var milliseconds) ||
                milliseconds < serverTime - long.Parse(receiveWindow, CultureInfo.InvariantCulture) ||
                milliseconds >= serverTime + 1000)
                throw new InvalidOperationException("Invalid SDK timestamp units or format.");
            LastPrivateTimestamp = milliseconds;
            var parameters = request.Method == HttpMethod.Get ? request.RequestUri.Query.TrimStart('?')
                : await request.Content!.ReadAsStringAsync(cancellationToken);
            var signature = Convert.ToHexString(HMACSHA256.HashData(
                Encoding.UTF8.GetBytes("mock-secret-not-a-real-credential"),
                Encoding.UTF8.GetBytes(timestamp + apiKey + receiveWindow + parameters))).ToLowerInvariant();
            if (apiKey != "mock-key-not-a-real-credential" ||
                request.Headers.GetValues("X-BAPI-SIGN").Single() != signature)
                throw new InvalidOperationException("Invalid SDK authentication signature.");
        }
        if (PrivateResponseBody is not null && !path.StartsWith("/v5/market/", StringComparison.Ordinal))
        {
            var response = new HttpResponseMessage(PrivateHttpStatus)
            {
                Content = new StringContent(PrivateResponseBody, Encoding.UTF8, "text/plain")
            };
            if (HeaderErrorCode.HasValue) response.Headers.Add("Ret_code", HeaderErrorCode.Value.ToString());
            return response;
        }
        if (PrivateErrorCode != 0 && !path.StartsWith("/v5/market/", StringComparison.Ordinal))
            return new HttpResponseMessage(PrivateHttpStatus)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    retCode = PrivateErrorCode, retMsg = "sensitive-error mock-key-not-a-real-credential",
                    result = new { }
                }), Encoding.UTF8, "application/json")
            };
        object result;
        switch (path)
        {
            case "/v5/order/create":
                Orders++;
                using (var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)))
                {
                    var order = document.RootElement;
                    if (order.GetProperty("symbol").GetString() != TargetSymbol ||
                        order.GetProperty("category").GetString() != "linear" ||
                        order.GetProperty("orderType").GetString() != "Market" ||
                        order.GetProperty("qty").GetString() != ExpectedQuantity ||
                        order.GetProperty("side").GetString() != ExpectedSide ||
                        order.TryGetProperty("price", out _) ||
                        order.GetProperty("timeInForce").GetString() != "IOC" ||
                        order.GetProperty("stopLoss").GetString() != ExpectedStop ||
                        order.GetProperty("takeProfit").GetString() != ExpectedTarget ||
                        order.GetProperty("positionIdx").GetInt32() != 0 ||
                        order.GetProperty("tpslMode").GetString() != "Full")
                        throw new InvalidOperationException("Unexpected order parameters.");
                    SubmittedLinkId = order.GetProperty("orderLinkId").GetString();
                    SubmittedQuantity = order.GetProperty("qty").GetString()!;
                }
                if (FailSubmission) throw new HttpRequestException("Simulated connection loss after submission.");
                PositionSize = FilledQuantity ?? SubmittedQuantity;
                result = new { orderId = "mock-order-id" };
                break;
            case "/v5/order/history":
                result = new { list = new[] { new { orderStatus = OrderStatus, orderLinkId = SubmittedLinkId,
                    cumExecQty = FilledQuantity ?? SubmittedQuantity, avgPrice = FillPrice ?? LastPrice } } };
                break;
            case "/v5/order/realtime":
                result = new { list = ActiveOrders ? new[] { new { symbol = TargetSymbol, orderStatus = "New" } } : [], nextPageCursor = "" };
                break;
            case "/v5/position/list":
                result = new { category = "linear", list = new[] { new { symbol = TargetSymbol, size = PositionSize,
                    positionIdx = 0, side = ExpectedSide, stopLoss = ProtectionPresent ? ExpectedStop : "0",
                    takeProfit = ProtectionPresent ? ExpectedTarget : "0" } }, nextPageCursor = "" };
                break;
            case "/v5/market/instruments-info":
                result = new
                {
                    list = new[] { new
                    {
                        symbol = TargetSymbol, status = "Trading", contractType = "LinearPerpetual", settleCoin = "USDT",
                        priceFilter = new { tickSize = "0.01" },
                        lotSizeFilter = new { qtyStep = "0.01", minOrderQty = "0.01", maxOrderQty = "5200",
                            maxMktOrderQty = MaximumMarketQuantity, minNotionalValue = "5" }
                    } }
                };
                break;
            case "/v5/market/tickers":
                result = new { list = new[] { new { symbol = TargetSymbol, lastPrice = LastPrice,
                    bid1Price = BidPrice ?? LastPrice, ask1Price = AskPrice ?? LastPrice } } };
                break;
            default:
                throw new InvalidOperationException("Unexpected API endpoint: " + path);
        }
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { retCode = 0, retMsg = "OK", result,
                time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - QuoteAgeMilliseconds }),
                Encoding.UTF8, "application/json")
        };
    }
}
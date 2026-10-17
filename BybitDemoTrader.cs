using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using bybit.net.api;
using bybit.net.api.ApiServiceImp;
using bybit.net.api.Models;
using bybit.net.api.Models.Trade;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public sealed class BybitDemoTrader : IDisposable
{
    private const string DemoUrl = "https://api-demo.bybit.com";
    private readonly IConfiguration configuration;
    private readonly ILogger<BybitDemoTrader> logger;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly HttpClient client;
    private readonly string journalPath;
    private readonly JsonEventStore journal;
    private bool paused;

    public BybitDemoTrader(IConfiguration configuration, ILogger<BybitDemoTrader> logger,
        string journalPath, HttpClient? httpClient = null)
    {
        this.configuration = configuration;
        this.logger = logger;
        this.journalPath = journalPath;
        client = httpClient ?? new HttpClient(new BybitTimestampSigningHandler(
            configuration["Bybit:ApiSecret"] ?? "",
            configuration.GetValue("Bybit:TimestampOffsetMs", -2000)))
        { Timeout = TimeSpan.FromSeconds(15) };
        journal = new JsonEventStore(journalPath);
    }

    public async Task<string> ProcessAsync(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return "Skipped: not an object.";
        if (!DemoOrderPlanner.TryResolveBybitSymbol(payload, out var symbol, out var symbolReason))
            return "Skipped: " + symbolReason;
        // TEMPORARY DEMO TEST: remove the opt-in path after verification; always reject test events then.
        var isTest = payload.TryGetProperty("type", out var type) &&
            type.ValueKind == JsonValueKind.String &&
            string.Equals(type.GetString(), "test", StringComparison.OrdinalIgnoreCase);
        var allowTestOrders = configuration.GetValue("Bybit:AllowTestOrders", false);
        if (isTest && !allowTestOrders)
            return "Skipped: test event.";
        if (allowTestOrders && !isTest)
            return "Skipped: temporary test-only mode.";

        await gate.WaitAsync();
        var stage = "validating signal";
        try
        {
            if (paused) return "Paused: an earlier execution requires review.";
            var allowedType = isTest ? type.GetString()! : configuration["Bybit:AllowedEventType"] ?? "";
            var maximumNotional = configuration.GetValue("Bybit:MaximumNotionalUsdt", 2000m);
            var plan = DemoOrderPlanner.Create(payload, allowedType, 0.01m, 0.01m,
                5200m, 0.01m, 5m, maximumNotional, out var reason, allowTestOrders);
            if (plan is null) return "Skipped: " + reason;

            if (!configuration.GetValue("Bybit:Enabled", false))
            {
                stage = "planning market entry from public Bybit quotes";
                var preview = await MarketPlanAsync(payload, plan, symbol, allowedType, maximumNotional, allowTestOrders);
                if (preview.Plan is null) return "Skipped: " + preview.Reason;
                plan = preview.Plan;
                logger.LogInformation("DEMO DRY RUN: {Plan}", System.Text.Json.JsonSerializer.Serialize(plan));
                return "Dry run only; no order submitted.";
            }

            var key = configuration["Bybit:ApiKey"];
            var secret = configuration["Bybit:ApiSecret"];
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(secret))
                return "Skipped: Demo API credentials are not configured.";

            var records = JArray.Parse(await File.ReadAllTextAsync(journalPath));
            // TEMPORARY DEMO TEST: one-use protection; preserve historical journal records after removal.
            if (isTest && records.Any(record => record["isTest"]?.Value<bool>() == true))
                return "Skipped: one-time demo test already attempted; test allowance consumed.";

            var maximumAge = configuration.GetValue("Bybit:MaximumSignalAgeSeconds", 300);
            if (!payload.TryGetProperty("time", out var time) ||
                !TryTime(time, out var signalTime) || maximumAge <= 0 ||
                DateTimeOffset.UtcNow - signalTime > TimeSpan.FromSeconds(maximumAge) ||
                signalTime - DateTimeOffset.UtcNow > TimeSpan.FromSeconds(30))
                return "Skipped: missing, stale or future signal timestamp.";

            var identity = string.Join("|", symbol, allowedType, plan.Side,
                payload.TryGetProperty("timeframe", out var interval) ? interval.ToString() : "",
                signalTime.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
                Format(plan.Entry), Format(plan.TakeProfit), Format(plan.StopLoss));
            var linkId = "sax-" + Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..32];
            if (records.Any(record => record["orderLinkId"]?.Value<string>() == linkId))
                return "Skipped: signal already attempted; no automatic retry.";

            var receiveWindow = configuration.GetValue("Bybit:ReceiveWindowMs", 20000)
                .ToString(CultureInfo.InvariantCulture);
            var trade = new BybitTradeService(client, apiKey: key, apiSecret: secret,
                url: DemoUrl, recvWindow: receiveWindow);
            var positions = new BybitPositionService(client, apiKey: key, apiSecret: secret,
                url: DemoUrl, recvWindow: receiveWindow);
            foreach (var group in records
                .Where(record => record["symbol"]?.Value<string>() == symbol)
                .GroupBy(record => record["orderLinkId"]?.Value<string>()))
            {
                var last = group.Last();
                var state = last["state"]?.Value<string>();
                if (state is "Submitting" or "Unknown")
                    return "Paused: unresolved submission in execution journal; reconcile manually.";
                if (state != "Accepted") continue;
                stage = $"checking previous {symbol} order history";
                var history = Response(await trade.GetOrdersHistory(Category.LINEAR,
                    symbol: symbol, orderLinkId: group.Key));
                var order = history["result"]?["list"]?.First;
                var status = order?["orderStatus"]?.Value<string>();
                if (status is not ("Filled" or "Cancelled" or "Rejected" or "Deactivated" or "PartiallyFilledCanceled"))
                    return "Skipped: previous order is pending or not yet reconciled.";
            }

            string? cursor = null;
            stage = $"checking open {symbol} orders";
            do
            {
                var active = Response(await trade.GetOpenOrders(Category.LINEAR,
                    symbol: symbol, limit: 50, cursor: cursor));
                if (active["result"]?["list"] is not JArray orders)
                    throw new InvalidOperationException("Missing open-order data.");
                if (orders.Any(order => order["symbol"]?.Value<string>() == symbol))
                    return $"Skipped: an active {symbol} order already exists.";
                cursor = active["result"]?["nextPageCursor"]?.Value<string>();
            } while (!string.IsNullOrEmpty(cursor));

            cursor = null;
            stage = $"checking {symbol} positions";
            do
            {
                var response = await positions.GetPositionInfo(Category.LINEAR,
                    symbol: symbol, limit: 200, cursor: cursor);
                var positionData = Response(JsonConvert.SerializeObject(response));
                if (positionData["result"]?["list"] is not JArray list)
                    throw new InvalidOperationException("Missing position data.");
                foreach (var position in list)
                {
                    if (position["symbol"]?.Value<string>() != symbol) continue;
                    if (!decimal.TryParse(position["size"]?.ToString(), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var size))
                        throw new InvalidOperationException("Invalid position size.");
                    if (size != 0) return $"Skipped: a {symbol} position is already open.";
                    if (position["symbol"]?.Value<string>() == symbol &&
                        position["positionIdx"]?.Value<int>() != 0)
                        return $"Skipped: {symbol} must use one-way position mode.";
                }
                cursor = positionData["result"]?["nextPageCursor"]?.Value<string>();
            } while (!string.IsNullOrEmpty(cursor));

            stage = "planning market entry from Bybit bid/ask and contract limits";
            var marketPlan = await MarketPlanAsync(payload, plan, symbol, allowedType, maximumNotional, allowTestOrders);
            if (marketPlan.Plan is null) return "Skipped: " + marketPlan.Reason;
            plan = marketPlan.Plan;

            stage = "saving submission intent";
            await RecordAsync(linkId, "Submitting", plan, null, isTest, symbol);
            try
            {
                stage = $"submitting {symbol} demo order";
                var raw = await trade.PlaceOrder(Category.LINEAR, symbol,
                    plan.Side == "Buy" ? Side.BUY : Side.SELL, OrderType.MARKET,
                    qty: Format(plan.Quantity),
                    timeInForce: TimeInForce.IOC, positionIdx: 0, orderLinkId: linkId,
                    takeProfit: Format(plan.TakeProfit), stopLoss: Format(plan.StopLoss),
                    tpTriggerBy: TriggerBy.LastPrice, slTriggerBy: TriggerBy.LastPrice,
                    tpslMode: TpslMode.Full, tpOrderType: OrderType.MARKET,
                    slOrderType: OrderType.MARKET);
                var result = JObject.Parse(raw ?? throw new InvalidOperationException("Empty order response."));
                if (result["retCode"]?.Value<int>() != 0)
                {
                    paused = true;
                    await RecordAsync(linkId, "Unknown", plan, result, isTest, symbol);
                    return "Paused: Bybit returned an error; inspect the execution journal before retrying.";
                }
                await RecordAsync(linkId, "Accepted", plan, result, isTest, symbol);
                stage = "verifying market fill and attached TP/SL";
                return await VerifyMarketFillAsync(trade, positions, linkId, plan, isTest, symbol);
            }
            catch (Exception)
            {
                paused = true;
                await RecordAsync(linkId, "Unknown", plan, null, isTest, symbol);
                throw;
            }
        }
        catch (BybitClientException exception)
        {
            paused = true;
            logger.LogError("Demo execution paused while {Stage}: Bybit code {Code}, HTTP status {StatusCode}. " +
                "Raw SDK error omitted to protect authentication data.", stage, exception.Code, exception.StatusCode);
            return $"Paused: Bybit code {exception.Code}, HTTP status {exception.StatusCode} while {stage}; " +
                "check Demo API credentials, permissions, outbound IP restrictions and clock. Manual review required.";
        }
        catch (Exception exception)
        {
            paused = true;
            logger.LogError("Demo execution paused while {Stage} after {ExceptionType}; inspect account and execution journal.",
                stage, exception.GetType().Name);
            return "Paused: execution check or submission failed; manual review required.";
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<(DemoOrderPlan? Plan, string Reason)> MarketPlanAsync(JsonElement payload,
        DemoOrderPlan chartPlan, string symbol, string allowedType, decimal maximumNotional, bool allowTestOrders)
    {
        using var instrumentResponse = await client.GetAsync(
            DemoUrl + $"/v5/market/instruments-info?category=linear&symbol={symbol}");
        instrumentResponse.EnsureSuccessStatusCode();
        var instrument = Response(await instrumentResponse.Content.ReadAsStringAsync())["result"]?["list"]?.First;
        if (instrument?["symbol"]?.Value<string>() != symbol ||
            instrument["status"]?.Value<string>() != "Trading" ||
            instrument["contractType"]?.Value<string>() != "LinearPerpetual" ||
            instrument["settleCoin"]?.Value<string>() != "USDT")
            return (null, $"{symbol} is not an available USDT perpetual.");

        using var tickerResponse = await client.GetAsync(
            DemoUrl + $"/v5/market/tickers?category=linear&symbol={symbol}");
        tickerResponse.EnsureSuccessStatusCode();
        var tickerData = Response(await tickerResponse.Content.ReadAsStringAsync());
        var ticker = tickerData["result"]?["list"]?.First;
        if (ticker?["symbol"]?.Value<string>() != symbol ||
            !DecimalValue(ticker["bid1Price"], out var bid) || bid <= 0 ||
            !DecimalValue(ticker["ask1Price"], out var ask) || ask <= 0 || bid > ask)
            return (null, "missing or invalid Bybit bid/ask.");
        if (!long.TryParse(tickerData["time"]?.ToString(), out var quoteTime) ||
            Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - quoteTime) > 5000)
            return (null, "Bybit quote timestamp is missing or stale.");

        var quote = chartPlan.Side == "Buy" ? ask : bid;
        if (Math.Abs(chartPlan.Entry - quote) / quote > 0.02m)
            return (null, "chart entry differs from Bybit price by more than 2%.");
        if (chartPlan.Side == "Buy" ? !(chartPlan.StopLoss < quote && quote < chartPlan.TakeProfit)
            : !(chartPlan.TakeProfit < quote && quote < chartPlan.StopLoss))
            return (null, "Bybit price has already crossed the planned TP or SL.");

        var bufferPercent = configuration.GetValue("Bybit:MarketRiskBufferPercent", 0.10m);
        if (bufferPercent < 0.01m || bufferPercent > 1m)
            return (null, "market risk buffer must be between 0.01 and 1 percent.");
        var sizingPrice = quote * (chartPlan.Side == "Buy" ? 1 + bufferPercent / 100 : 1 - bufferPercent / 100);
        var plan = DemoOrderPlanner.Create(payload, allowedType,
            Number(instrument, "lotSizeFilter", "qtyStep"),
            Number(instrument, "lotSizeFilter", "minOrderQty"),
            Number(instrument, "lotSizeFilter", "maxMktOrderQty"),
            Number(instrument, "priceFilter", "tickSize"),
            Number(instrument, "lotSizeFilter", "minNotionalValue"), maximumNotional,
            out var reason, allowTestOrders, sizingPrice);
        return (plan, reason);
    }

    private async Task<string> VerifyMarketFillAsync(BybitTradeService trade,
        BybitPositionService positions, string linkId, DemoOrderPlan plan, bool isTest, string symbol)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var history = Response(await trade.GetOrdersHistory(Category.LINEAR,
                symbol: symbol, orderLinkId: linkId));
            var order = history["result"]?["list"]?.FirstOrDefault(item =>
                item["orderLinkId"]?.Value<string>() == linkId);
            var status = order?["orderStatus"]?.Value<string>();
            if (status is "Filled" or "Cancelled" or "Rejected" or "PartiallyFilledCanceled")
            {
                if (!DecimalValue(order?["cumExecQty"], out var filledQuantity) || filledQuantity < 0)
                    throw new InvalidOperationException("Missing fill quantity.");
                if (filledQuantity == 0 && status != "Filled")
                {
                    await RecordAsync(linkId, "NoFill", plan, null, isTest, symbol);
                    return "Demo market order ended without a fill; no automatic retry. Order link ID: " + linkId;
                }
                if (filledQuantity <= 0 || filledQuantity > plan.Quantity ||
                    !DecimalValue(order?["avgPrice"], out var averagePrice) || averagePrice <= 0)
                    throw new InvalidOperationException("Invalid fill details.");
                var actualRisk = filledQuantity * Math.Abs(averagePrice - plan.StopLoss);
                if (actualRisk > 10 || (plan.Side == "Buy" ?
                    !(plan.StopLoss < averagePrice && averagePrice < plan.TakeProfit) :
                    !(plan.TakeProfit < averagePrice && averagePrice < plan.StopLoss)))
                {
                    logger.LogError("Market fill requires manual review: gross stop-distance risk {Risk} USDT.", actualRisk);
                    throw new InvalidOperationException("Market fill exceeded planned risk or brackets.");
                }

                var positionData = Response(JsonConvert.SerializeObject(
                    await positions.GetPositionInfo(Category.LINEAR, symbol: symbol)));
                var position = positionData["result"]?["list"]?.FirstOrDefault(item =>
                    item["symbol"]?.Value<string>() == symbol && item["positionIdx"]?.Value<int>() == 0);
                if (position is not null && position["side"]?.Value<string>() == plan.Side &&
                    DecimalValue(position["size"], out var positionSize) && positionSize == filledQuantity &&
                    DecimalValue(position["stopLoss"], out var stop) && stop == plan.StopLoss &&
                    DecimalValue(position["takeProfit"], out var target) && target == plan.TakeProfit)
                {
                    var verification = JObject.FromObject(new { filledQuantity, averagePrice, actualGrossRiskUsdt = actualRisk,
                        stopLoss = stop, takeProfit = target, status });
                    await RecordAsync(linkId, "VerifiedFill", plan, verification, isTest, symbol);
                    var baseSymbol = symbol[..^4];
                    return $"Demo market fill and TP/SL verified: {Format(filledQuantity)} {baseSymbol} at {Format(averagePrice)}, " +
                        $"gross stop-distance risk {Format(actualRisk)} USDT. Order link ID: {linkId}";
                }
            }
            if (attempt < 7) await Task.Delay(250);
        }
        throw new InvalidOperationException("Market fill or position protection could not be verified.");
    }

    private static bool DecimalValue(JToken? value, out decimal number) =>
        decimal.TryParse(value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);

    private Task RecordAsync(string linkId, string state, DemoOrderPlan plan, JObject? response,
        bool isTest, string symbol) =>
        journal.AppendAsync(System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            orderLinkId = linkId, symbol, state, isTest, recordedAtUtc = DateTimeOffset.UtcNow, plan,
            response = response is null ? (JsonElement?)null :
                System.Text.Json.JsonSerializer.Deserialize<JsonElement>(response.ToString())
        }), CancellationToken.None);

    private static JObject Response(string? json)
    {
        var response = JObject.Parse(json ?? throw new InvalidOperationException("Empty API response."));
        if (response["retCode"]?.Value<int>() != 0)
            throw new BybitClientException("Bybit API check failed.")
            {
                Code = response["retCode"]?.Value<int>() ?? -1
            };
        return response;
    }

    private static decimal Number(JToken instrument, string section, string name) =>
        decimal.Parse(instrument[section]?[name]?.ToString()
            ?? throw new InvalidOperationException("Missing contract limit."), CultureInfo.InvariantCulture);

    private static string Format(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static bool TryTime(JsonElement value, out DateTimeOffset time)
    {
        time = default;
        if (value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(),
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out time)) return true;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var seconds)) return false;
        try { time = DateTimeOffset.FromUnixTimeSeconds(seconds); return true; }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    public void Dispose()
    {
        journal.Dispose();
        client.Dispose();
        gate.Dispose();
    }
}
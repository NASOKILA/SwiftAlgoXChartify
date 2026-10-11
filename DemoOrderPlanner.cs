using System.Globalization;
using System.Text.Json;

public sealed record DemoOrderPlan(string Side, decimal Entry, decimal TakeProfit,
    decimal StopLoss, decimal Quantity, decimal PlannedRisk);

public static class DemoOrderPlanner
{
    // TEMPORARY DEMO TEST: remove allowTestOrders and restore unconditional test rejection after verification.
    public static DemoOrderPlan? Create(JsonElement payload, string allowedEventType,
        decimal quantityStep, decimal minimumQuantity, decimal maximumQuantity,
        decimal tickSize, decimal minimumNotional, decimal maximumNotional,
        out string reason, bool allowTestOrders = false, decimal? executionPrice = null)
    {
        reason = "Unrecognized or test event.";
        if (payload.ValueKind != JsonValueKind.Object ||
            string.IsNullOrWhiteSpace(allowedEventType) ||
            !payload.TryGetProperty("type", out var eventType) ||
            eventType.ValueKind != JsonValueKind.String ||
            (!allowTestOrders && string.Equals(eventType.GetString(), "test", StringComparison.OrdinalIgnoreCase)) ||
            !string.Equals(eventType.GetString(), allowedEventType, StringComparison.Ordinal))
            return null;

        reason = "Only QQQ/QQQUSDT BUY or SELL signals are supported.";
        if (!payload.TryGetProperty("symbol", out var symbol) ||
            symbol.ValueKind != JsonValueKind.String ||
            (symbol.GetString() != "QQQ" && symbol.GetString() != "QQQUSDT") ||
            !payload.TryGetProperty("signalType", out var signalType) ||
            signalType.ValueKind != JsonValueKind.String)
            return null;
        var side = signalType.GetString()?.ToUpperInvariant();
        if (side != "BUY" && side != "SELL") return null;

        var prefix = side == "BUY" ? "long" : "short";
        reason = "Entry, TP and SL must be finite positive prices.";
        if (!Price(payload, prefix + "Entry", "entry", out var entry) ||
            !Price(payload, prefix + "Tp", "tp", out var takeProfit) ||
            !Price(payload, prefix + "Sl", "sl", out var stopLoss))
            return null;

        reason = "Invalid contract limits.";
        if (quantityStep <= 0 || tickSize <= 0 || maximumNotional <= 0) return null;
        if (executionPrice.HasValue)
        {
            if (executionPrice.Value <= 0) return null;
            entry = side == "BUY" ? decimal.Ceiling(executionPrice.Value / tickSize) * tickSize
                : decimal.Floor(executionPrice.Value / tickSize) * tickSize;
        }
        else
        {
            entry = decimal.Round(entry / tickSize, 0, MidpointRounding.AwayFromZero) * tickSize;
        }
        takeProfit = decimal.Round(takeProfit / tickSize, 0, MidpointRounding.AwayFromZero) * tickSize;
        stopLoss = decimal.Round(stopLoss / tickSize, 0, MidpointRounding.AwayFromZero) * tickSize;
        reason = "TP/SL must bracket the entry in the signal direction.";
        if (side == "BUY" ? !(stopLoss < entry && entry < takeProfit)
            : !(takeProfit < entry && entry < stopLoss)) return null;

        var distance = Math.Abs(entry - stopLoss);
        var quantity = decimal.Floor(Math.Min(10m / distance,
            Math.Min(maximumNotional / entry, maximumQuantity)) / quantityStep) * quantityStep;
        reason = "Risk-capped quantity does not meet exchange minimums.";
        if (quantity <= 0 || quantity < minimumQuantity || quantity * entry < minimumNotional)
            return null;
        reason = "Dry-run plan created; not submitted.";
        return new DemoOrderPlan(side == "BUY" ? "Buy" : "Sell", entry,
            takeProfit, stopLoss, quantity, quantity * distance);
    }

    private static bool Price(JsonElement payload, string plotName, string defaultName, out decimal price)
    {
        price = 0;
        if (!payload.TryGetProperty(plotName, out var value) &&
            !payload.TryGetProperty(defaultName, out value)) return false;
        return (value.ValueKind == JsonValueKind.Number ? value.TryGetDecimal(out price)
            : value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(),
                NumberStyles.Float, CultureInfo.InvariantCulture, out price)) && price > 0;
    }
}
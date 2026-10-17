using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public sealed record DemoAuthenticationResult(bool Success, int? HttpStatus, int? BybitCode, string Diagnosis,
    string? ResponseFormat = null, bool HasAuthenticationChallenge = false);

public static class BybitDemoAuthenticationCheck
{
    public static async Task<DemoAuthenticationResult> CheckAsync(IConfiguration configuration,
        HttpClient client)
    {
        var key = configuration["Bybit:ApiKey"];
        var secret = configuration["Bybit:ApiSecret"];
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(secret))
            return new(false, null, null, "Demo credentials are missing in this terminal.");
        if (key.Any(char.IsWhiteSpace) || secret.Any(char.IsWhiteSpace))
            return new(false, null, null, "Credentials contain whitespace; re-enter the original matching pair privately.");

        const string query = "category=linear&symbol=QQQUSDT&limit=1";
        var receiveWindow = configuration.GetValue("Bybit:ReceiveWindowMs", 20000)
            .ToString(CultureInfo.InvariantCulture);
        var timestamp = BybitTimestampSigningHandler.AdjustTimestamp(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            configuration.GetValue("Bybit:TimestampOffsetMs", -2000));
        var signature = BybitTimestampSigningHandler.CreateSignature(timestamp, key, receiveWindow, query, secret);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://api-demo.bybit.com/v5/order/realtime?" + query);
        request.Headers.Add("X-BAPI-API-KEY", key);
        request.Headers.Add("X-BAPI-TIMESTAMP", timestamp);
        request.Headers.Add("X-BAPI-RECV-WINDOW", receiveWindow);
        request.Headers.Add("X-BAPI-SIGN", signature);
        request.Headers.Add("X-BAPI-SIGN-TYPE", "2");

        try
        {
            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            int? code = null;
            var responseFormat = string.IsNullOrWhiteSpace(body) ? "empty" : "non-JSON";
            try
            {
                using var document = JsonDocument.Parse(body);
                responseFormat = "JSON";
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("retCode", out var value) &&
                    value.ValueKind == JsonValueKind.Number &&
                    value.TryGetInt32(out var number)) code = number;
            }
            catch (JsonException) { }

            if (code is null && response.Headers.TryGetValues("Ret_code", out var headerCodes))
            {
                foreach (var headerCode in headerCodes)
                {
                    if (int.TryParse(headerCode, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                    {
                        code = number;
                        break;
                    }
                }
            }

            var success = response.IsSuccessStatusCode && code == 0;
            var diagnosis = success ? "Demo authentication and open-order read succeeded. No orders placed."
                : code switch
                {
                    10002 or -1 => "Request expired or outside the time window; verify Windows UTC clock.",
                    10003 => "Invalid API key or environment mismatch; verify the matching Demo key pair.",
                    10004 => "Signature rejected; verify the original HMAC secret matches this API key.",
                    10005 => "Permission denied; verify Demo contract order-read permissions.",
                    10007 => "Authentication rejected; verify the original matching Demo key pair.",
                    10010 => "Outbound IP does not match the key's IP allowlist.",
                    33004 => "Derivatives API key expired.",
                    _ => "Request failed; share only this diagnostic output, not raw headers or credentials."
                };
            return new(success, (int)response.StatusCode, code, diagnosis, responseFormat,
                response.Headers.WwwAuthenticate.Any());
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return new(false, null, null, "Network or timeout failure; no orders placed.");
        }
    }
}
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

public sealed class BybitTimestampSigningHandler : DelegatingHandler
{
    private readonly string apiSecret;
    private readonly int timestampOffsetMilliseconds;

    public BybitTimestampSigningHandler(string apiSecret, int timestampOffsetMilliseconds = -2000,
        HttpMessageHandler? innerHandler = null)
        : base(innerHandler ?? new HttpClientHandler())
    {
        this.apiSecret = apiSecret;
        this.timestampOffsetMilliseconds = timestampOffsetMilliseconds;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!request.Headers.TryGetValues("X-BAPI-TIMESTAMP", out var timestamps))
            return await base.SendAsync(request, cancellationToken);

        if (string.IsNullOrWhiteSpace(apiSecret) ||
            !request.Headers.TryGetValues("X-BAPI-API-KEY", out var apiKeys) ||
            !request.Headers.TryGetValues("X-BAPI-RECV-WINDOW", out var receiveWindows) ||
            !long.TryParse(timestamps.SingleOrDefault(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var sdkTimestamp))
            throw new InvalidOperationException("Cannot correct an incomplete Bybit signed request.");

        var apiKey = apiKeys.Single();
        var receiveWindow = receiveWindows.Single();
        var timestamp = checked(sdkTimestamp + timestampOffsetMilliseconds)
            .ToString(CultureInfo.InvariantCulture);
        var parameters = request.Method == HttpMethod.Get
            ? request.RequestUri?.Query.TrimStart('?') ?? string.Empty
            : request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
        var signature = CreateSignature(timestamp, apiKey, receiveWindow, parameters, apiSecret);

        request.Headers.Remove("X-BAPI-TIMESTAMP");
        request.Headers.Add("X-BAPI-TIMESTAMP", timestamp);
        request.Headers.Remove("X-BAPI-SIGN");
        request.Headers.Add("X-BAPI-SIGN", signature);
        return await base.SendAsync(request, cancellationToken);
    }

    public static string AdjustTimestamp(string timestamp, int offsetMilliseconds) =>
        checked(long.Parse(timestamp, NumberStyles.Integer, CultureInfo.InvariantCulture) + offsetMilliseconds)
            .ToString(CultureInfo.InvariantCulture);

    public static string CreateSignature(string timestamp, string apiKey, string receiveWindow,
        string parameters, string apiSecret) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(apiSecret),
            Encoding.UTF8.GetBytes(timestamp + apiKey + receiveWindow + parameters))).ToLowerInvariant();
}
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var checkBybitAuthentication = args.Contains("--check-bybit-auth", StringComparer.Ordinal);
var builder = WebApplication.CreateBuilder(args.Where(argument => argument != "--check-bybit-auth").ToArray());
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 64 * 1024);

if (checkBybitAuthentication)
{
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    var result = await BybitDemoAuthenticationCheck.CheckAsync(builder.Configuration, client);
    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    Environment.ExitCode = result.Success ? 0 : 1;
    return;
}

var token = builder.Configuration["Webhook:Token"];
if (string.IsNullOrWhiteSpace(token) || token.Length < 32)
{
    throw new InvalidOperationException("Set Webhook__Token to a random secret of at least 32 characters.");
}

var expectedTokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
var databasePath = builder.Configuration["Webhook:DatabasePath"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "data", "events.json");
using var eventStore = new JsonEventStore(databasePath);
builder.Services.AddSingleton<BybitDemoTrader>(services => new BybitDemoTrader(
    builder.Configuration, services.GetRequiredService<ILogger<BybitDemoTrader>>(),
    Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath))!, "executions.json")));

var app = builder.Build();

app.MapPost("/webhooks/chartify", async (HttpContext context, ILogger<Program> logger,
    BybitDemoTrader demoTrader) =>
{
    var suppliedToken = context.Request.Query["token"].ToString();
    var suppliedTokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(suppliedToken));
    if (!CryptographicOperations.FixedTimeEquals(expectedTokenHash, suppliedTokenHash))
    {
        return Results.Unauthorized();
    }

    JsonDocument document;
    try
    {
        document = await JsonDocument.ParseAsync(context.Request.Body,
            cancellationToken: context.RequestAborted);
    }
    catch (JsonException)
    {
        return Results.BadRequest(new { error = "The request body must be valid JSON." });
    }
    catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
    {
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    }

    using (document)
    {
        var receiptId = Guid.NewGuid().ToString("N");
        var capture = new
        {
            receiptId,
            receivedAtUtc = DateTimeOffset.UtcNow,
            contentType = context.Request.ContentType,
            payload = document.RootElement
        };

        try
        {
            await eventStore.AppendAsync(JsonSerializer.SerializeToElement(capture),
                context.RequestAborted);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogError(exception, "Unable to save webhook receipt {ReceiptId}", receiptId);
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        logger.LogInformation("Saved Chartify webhook receipt {ReceiptId}", receiptId);
        var payloadJson = JsonSerializer.Serialize(document.RootElement,
            new JsonSerializerOptions { WriteIndented = true });
        logger.LogInformation("Chartify JSON payload for {ReceiptId}:\n{PayloadJson}",
            receiptId, payloadJson);
        var execution = await demoTrader.ProcessAsync(document.RootElement);
        logger.LogInformation("Demo execution: {Execution}", execution);
        return Results.Ok(new { received = true, receiptId, execution });
    }
});

app.Run();
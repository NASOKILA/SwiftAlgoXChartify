# Chartify webhook receiver

An ASP.NET Core / .NET 10 webhook receiver with a local JSON event store and an optional Bybit Demo executor. Trading is disabled by default. Test events are blocked unless explicitly enabled for up to five demo submission attempts.

## Run locally

In PowerShell, from this folder:

```powershell
$env:Webhook__Token = [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
dotnet run --urls http://localhost:5080
```

Keep that terminal open. Preserve the token securely if you need it to remain stable across restarts; environment variables set this way last only for this shell session. Never commit a real token.

The endpoint is `POST /webhooks/chartify?token=YOUR_TOKEN`. Use the value of `Webhook__Token` in the URL. A successful request returns HTTP 200 only after the capture has been written.

Local synthetic test from another terminal, substituting your token:

```powershell
Invoke-RestMethod -Method Post `
  -Uri 'http://localhost:5080/webhooks/chartify?token=YOUR_TOKEN' `
  -ContentType 'application/json' `
  -Body '{"example":true,"symbol":"BLK","side":"buy"}'
```

This sample is invented test data, NOT Chartify's documented schema.

New events are appended to `data/events.json`, a JSON array that persists across restarts. Each entry contains `receiptId`, `receivedAtUtc`, `contentType`, and `payload`. The payload is preserved in structure, including test events; formatting is normalized. The API also prints each saved payload in its terminal.

The database starts as `[]` and is created automatically if missing. Set `Webhook__DatabasePath` before starting the API to use a different file location. The old `Webhook__CaptureDirectory` setting is no longer used. Existing files in `captures/` remain untouched and are not automatically imported.

To view all events from another PowerShell terminal:

```powershell
Get-Content .\data\events.json -Raw
```

To view only the latest payload:

```powershell
$events = @(Get-Content .\data\events.json -Raw | ConvertFrom-Json)
if ($events.Count -gt 0) {
  $events[-1].payload | ConvertTo-Json -Depth 64
}
```

Writes within one API instance are serialized. Each update writes a temporary file in the same directory, flushes it, and replaces the database. An exclusive `.lock` file prevents another local API instance from opening the same database concurrently. Do not manually edit the database while the API is running. Invalid database JSON causes requests to return 503 instead of discarding saved data.

This is a small local event store, not a production database: every append reads and rewrites the entire array. It has no automatic deduplication, retention, or backup. Keep backups with the API stopped, and use SQLite or a server database when volume grows. Treat events as private trading data; this workspace is inside OneDrive, so the default database may sync to the cloud. A local lock does not coordinate OneDrive copies on other computers. Configure an unsynced directory for more reliable storage.

## Connect Chartify

1. Make the running endpoint reachable over public HTTPS, using your hosting provider or a reputable HTTPS tunnel to local port 5080. Chartify cannot call your computer's `localhost`. This project does not configure a tunnel or hosting.
2. In Chartify's **Push Notifications**, expand the webhook form for the alert you want to receive.
3. Enter `https://YOUR_PUBLIC_HOST/webhooks/chartify?token=YOUR_TOKEN` as the webhook URL. If the platform rejects query parameters, stop and use its documented authentication mechanism instead of removing protection.
4. Enter an optional name and select **Generic JSON** explicitly.
5. Open **Edit message**. Inspect the default JSON and any variable/template picker. Keep the default initially; do not guess placeholder syntax. Save it if necessary.
6. Leave **Signing secret** blank for this initial capture setup. The screenshots do not specify the signing algorithm, header name, or signed bytes, so this receiver does not validate Chartify signatures.
7. Select **Add Webhook**. Use a test-send control if Chartify provides one; otherwise wait for the configured trade alert to fire. Check whether the webhook needs to be configured separately for each alert.
8. Inspect the resulting capture. Use the actual fields to design your C# trade DTO and distinguish entries, exits, cancellations, and other events. A redacted real payload or a screenshot of **Edit message** is needed to establish the schema.

## Bybit Demo trading

The executor uses Bybit's official `bybit.net.api` SDK and is restricted to `https://api-demo.bybit.com`, `QQQUSDT` and `NVDAUSDT` USDT linear perpetuals, and one-way position mode. No live-money endpoint is configurable. Demo is different from Testnet: create keys inside **Demo Trading** on your mainnet Bybit account. See https://bybit-exchange.github.io/docs/v5/demo.

The supported Chartify tickers map as `QQQ`/`QQQUSDT` -> `QQQUSDT` and `NVDA`/`NVDAUSDT`/`NVDA/USDT` -> `NVDAUSDT`. BUY opens a long; SELL opens a short and does not close an existing long. An open position or active order blocks additional entries for that same contract; QQQ and NVDA are checked independently. SPY positions/orders are not changed and do not block either supported contract. Checks are paginated. Use a dedicated demo account with no competing trading applications; remote manual activity cannot be locked atomically by this API.

### Start with dry run

First capture an actual Chartify entry alert and confirm its `type` is an entry event, not an exit or cancellation. The value `signal` in our mock tests is invented, not a verified Chartify event type. Set the exact confirmed entry-event type below; unrecognized types and `test` are skipped.

```powershell
$env:Bybit__AllowedEventType = 'REPLACE_WITH_CONFIRMED_ENTRY_EVENT_TYPE'
$env:Bybit__Enabled = 'false'
$env:Bybit__MaximumNotionalUsdt = '2000'
dotnet run --urls http://localhost:5080
```

Keep the existing `Webhook__Token` set in this terminal. For a recognized event, the API prints a **DEMO DRY RUN** market plan using public Bybit contract limits and fresh bid/ask data. No authenticated API calls or orders are made during a dry run. The plan's `Entry` is an adverse-price sizing estimate, not a limit price or promised fill. Old saved events are not replayed.

The Chartify payload must use one of the supported symbol forms above and include `signalType` (buy/sell), `type`, and `time`. A Trade Quality Score is not required or checked yet. Other symbols are rejected by the planner. For custom templates it reads `longEntry`, `longTp`, `longSl` on BUY, or `shortEntry`, `shortTp`, `shortSl` on SELL. The default payload's `entry`, `tp`, and `sl` are also supported. Quoted decimal prices are accepted; `NaN`, missing stops, and invalid price brackets are rejected. Use price and signal data for the same underlying as the Chartify symbol; the app compares its signal entry with the selected Bybit contract's quote.

### Read-only authentication diagnostic

To investigate HTTP 401 without attempting a trade, stop the API and run this in the terminal containing your Demo key and secret:

```powershell
$env:Bybit__Enabled = 'false'
dotnet run -- --check-bybit-auth
```

This command sends exactly one independently HMAC-signed GET to the demo open-orders endpoint, prints only success, HTTP status, Bybit code, and a fixed diagnostic, then exits. It does not start the webhook server, place or cancel orders, touch the event/execution databases, or consume the temporary test allowance. A webhook token is not required. A failure exits with code 1.

Only share this sanitized output. Do not share raw response bodies, authentication headers, signatures, API keys or secrets. Success verifies the key pair and order-read access, not all trade permissions or account readiness. If this direct check succeeds while SDK requests fail, investigate the SDK HTTP path rather than changing credentials repeatedly.

### Enable demo submission

Only after verifying a real QQQ or NVDA entry payload, switch each intended contract to **one-way mode** in Bybit Demo. Configure leverage and margin there yourself; the app does not change them. Confirm sufficient demo balance and verify that liquidation would not occur before the stop. Use a demo API key with read-position/order and contract-trading permissions only, not withdrawal permissions.

Stop the API, then run these commands yourself in its PowerShell 7 terminal. The masked prompts keep secrets out of pasted commands; do not share them with the assistant.

```powershell
$env:Bybit__ApiKey = Read-Host 'Bybit DEMO API key' -MaskInput
$env:Bybit__ApiSecret = Read-Host 'Bybit DEMO API secret' -MaskInput
$env:Bybit__Enabled = 'true'
dotnet run --urls http://localhost:5080
```

The allowed entry-event type and webhook token must still be set in this same shell. Keep ngrok running. Changing environment variables requires restarting the API. To stop new automatic submissions, restart with `Bybit__Enabled=false`; this does NOT cancel already submitted orders or close positions.

### TEMPORARY ONLY: five Chartify test trades

This exception exists only to verify demo order submission. Remove it after testing; normal operation must reject all test events and execute only explicitly allowed real entry events. Code markers use `TEMPORARY DEMO TEST` so the exception is easy to find. The test-only allowance is five distinct submission attempts total across QQQ and NVDA.

Removal checklist:

- Remove `Bybit:AllowTestOrders`, the test-only routing, and the five-attempt test guard in `BybitDemoTrader.cs`; restore unconditional rejection of `type: "test"` and real-event routing via `Bybit:AllowedEventType`.
- Remove the optional `allowTestOrders` argument from `DemoOrderPlanner.Create` and both trader calls; restore unconditional test rejection in the planner.
- Remove the marked opt-in mock checks in `Tests/Program.cs`, but retain and run the default test-event rejection checks and all real-trade safety checks.
- Remove this temporary setup section and unset `Bybit__AllowTestOrders` in the launch terminal. Confirm tests cannot submit even with `Bybit__Enabled=true`.
- Keep per-symbol QQQUSDT/NVDAUSDT position/pending-order blocking, risk sizing, timestamp/price guards, duplicate protection, and uncertain-submission handling. Preserve execution history, including legacy SPY records and historical `isTest` metadata; do not delete records to remove the temporary feature. Legacy records without a `symbol` are not treated as QQQ or NVDA records.

For the temporary demo test, keep your existing webhook token and Demo API credentials in the same PowerShell terminal. Stop the API with Ctrl+C first. Ensure the contract in the test alert is in one-way mode, demo funds are available, and no position or pending order exists for that contract. This test-only exception is restricted to QQQ and NVDA.

First inspect a test plan without submitting:

```powershell
$env:Bybit__AllowTestOrders = 'true'
$env:Bybit__Enabled = 'false'
dotnet run --urls http://localhost:5080
```

Send Chartify's Test Webhook. Confirm the printed dry-run direction, entry, TP, SL, quantity, and planned risk. Dry runs do not consume the five-attempt allowance. `Bybit__AllowedEventType` is not needed for this test mode; non-test alerts are blocked while the switch is true.

To permit the one actual demo submission attempt, stop the API and restart:

```powershell
$env:Bybit__AllowTestOrders = 'true'
$env:Bybit__Enabled = 'true'
dotnet run --urls http://localhost:5080
```

Keep ngrok running and send up to five distinct Chartify test alerts, one at a time. The API will attempt a market order only if all validation, timestamp, price, position, and pending-order checks pass. Check the terminal's execution status and verify each fill and attached TP/SL in Bybit Demo. HTTP 200 alone is not proof of order submission or fill. The allowance is shared by QQQ and NVDA and is not reset when switching execution types; do not reset or delete the journal to get more attempts.

Before sending to Bybit, an `isTest: true` submission marker is durably saved in the execution journal. Each distinct marker consumes one of the five attempts, even if Bybit rejects the request or the connection fails. Duplicate deliveries do not consume another attempt; validation skips and dry runs do not consume an attempt. The count survives restarts. Do not delete the journal, change its path, or remove a marker to retry an uncertain order.

After testing, stop and restart with both switches disabled:

```powershell
$env:Bybit__AllowTestOrders = 'false'
$env:Bybit__Enabled = 'false'
dotnet run --urls http://localhost:5080
```

This does not cancel existing orders or close a filled position; manage those in Bybit Demo. In particular, a previously submitted limit order is NOT converted or cancelled by changing the app to market execution. Existing SPY orders/positions remain untouched; pending orders block new entries only for their own symbol. The consumed marker remains in the journal intentionally. Automatic real-signal execution requires separate explicit configuration as described above.

Stale test timestamps remain blocked. Your custom `"time": "{{time}}"` may refer to an older candle. For a newly sent demo test you can try `"time": "{{timenow}}"` using the placeholder picker, then inspect the dry-run capture to confirm it substitutes a current ISO UTC timestamp or numeric Unix seconds. Keep the original plot prices and the venue-price guards; changing the timestamp does not make stale prices current. Do not fabricate or replay an old signal to bypass these checks.

### Risk and execution behavior

- New entries are market orders using IOC behavior, with full-position market TP/SL attached using LastPrice triggers. There is no entry limit price. Chartify's absolute TP/SL levels are retained and rounded to the exchange tick size; they are not shifted to preserve Chartify's original reward/risk ratio. Market orders can partially fill or cancel without filling.
- BUY sizing uses Bybit's ask; SELL sizing uses its bid. Quotes require valid bid/ask and a server timestamp within 5 seconds of the local clock. `Bybit__MarketRiskBufferPercent` defaults to 0.10 percent (valid range 0.01 to 1): BUY estimates an adverse fill above the ask, SELL below the bid, rounded outward to the tick. This is a sizing buffer, NOT a custom exchange-enforced slippage tolerance. Bybit's default market-order protection applies; no custom tolerance is sent with attached TP/SL.
- Authenticated Bybit requests use a 20,000 ms receive window by default; override it with `Bybit__ReceiveWindowMs`. Signed timestamps are adjusted by `Bybit__TimestampOffsetMs`, which defaults to `-2000` ms for the observed local clock skew; set this per host. The handler recomputes the HMAC after adjusting the timestamp. Bybit still rejects timestamps more than 1,000 ms ahead of server time.
- Quantity is rounded DOWN from `10 USDT / abs(bufferedBybitEntry - stopLoss)`, capped by `maxMktOrderQty` and `Bybit__MaximumNotionalUsdt` (default 2000). Exposure is estimated at the sizing price, not guaranteed at the actual fill. If the capped quantity falls below exchange minimums, the order is skipped, not rounded up.
- The 10 USDT limit is planned gross entry-to-stop risk, NOT a guaranteed maximum loss of $10. Fees, funding, slippage, gaps, and liquidation can increase actual loss. USDT is not guaranteed to equal USD. The exposure cap may reduce planned risk below 10 USDT.
- Enabled execution requires a signal timestamp within the last 300 seconds and no more than 30 seconds into the future. ISO UTC strings or numeric Unix seconds are accepted. `Bybit__MaximumSignalAgeSeconds` changes the age limit. If `{{time}}` is the candle opening time, delayed or longer-timeframe signals may be skipped; verify its semantics before changing the limit.
- A Chartify entry more than 2% from Bybit's executable quote, or a quote/buffered entry outside the proposed TP/SL bracket, is skipped. Market execution addresses missed limit fills but does not make an equity feed equivalent to a perpetual feed. Different sessions and adjustments still matter.
- Any pending order for the selected QQQUSDT or NVDAUSDT contract blocks new signals for that same contract, including old limit orders. The app does not automatically cancel them or convert them to market. A previous accepted order for that contract must have a terminal status in Bybit order history before another can proceed. Orders and journal history for other symbols do not block it. Missing history fails closed.
- No automatic retries follow a submission attempt. A deterministic order link ID and durable `data/executions.json` journal prevent duplicate attempts across restarts. The identity uses symbol, side, event type, timeframe, signal timestamp, and rounded entry/TP/SL prices; changing these can create a different signal identity.
- A submission intent is saved before sending. Timeouts, exchange errors, or an unresolved intent pause execution. Inspect Bybit's order history and positions using the order link ID before any manual recovery; NEVER delete the journal to force retries. Preserve a backup and reconcile uncertain records before restarting. The journal is stored alongside the configured event database.
- After market acceptance, the app checks order history and position protection up to 8 times with 250 ms between checks (network time is additional). It verifies filled quantity, average price, gross stop-distance risk at or below 10 USDT, one-way position size/direction, and the expected full-position TP/SL. A partial terminal fill can pass using its actual quantity; a zero-fill terminal cancellation is recorded as `NoFill` and never retried automatically.
- Verified fills are recorded as `VerifiedFill` with actual quantity, average price and gross risk. Risk overruns, missing TP/SL, delayed visibility, or unconfirmed fills pause execution and record `Unknown` for manual review. An existing position is NOT automatically closed, reduced or repaired when verification fails. Inspect Bybit Demo immediately. A fast close before position verification can also pause execution conservatively.
- HTTP 200 acknowledges event storage, NOT order fill. Read the `execution` result and confirm the position in Bybit Demo. Fill verification is bounded and occurs only just after submission; this prototype has no continuous position monitoring or automatic stop repair. Fees, stop-execution slippage and subsequent market movement are not included in verified gross risk.
- Existing orders/positions are not managed on shutdown. Keep the journal/event files on persistent, unsynced local storage and keep API credentials private. Do not use this demo prototype for live-money trading.

### Repeatable mock checks

These use fake credentials and a mock HTTP handler; no real Bybit orders or requests are sent:

```powershell
dotnet run --project .\Tests\DemoChecks.csproj --property:RestoreSources=https://api.nuget.org/v3/index.json
```

If restore hits an unrelated private NuGet feed, use `dotnet restore --source https://api.nuget.org/v3/index.json` and build/run with `--no-restore`. Do not change your machine-wide feeds or send private-feed credentials to the assistant.

## Security and production limits

- The URL token is an interim shared-secret authentication mechanism, not a webhook signature. Always use public HTTPS. URLs can appear in Chartify settings and proxy/access logs; redact query strings there. ASP.NET Core informational request logging is suppressed here to avoid logging the token. Rotate the token if disclosed.
- Wrong or missing tokens return 401; malformed or empty JSON returns 400; bodies over 64 KiB return 413; storage failures return 503. JSON objects, arrays, and scalar values are accepted intentionally until the schema is known.
- Chartify's retries, timeout, event identifiers, and signing contract are not verified. Repeated deliveries create separate event database entries. Test events are stored and excluded from demo orders by default; the explicit five-attempt demo test switch is the only exception.
- Before production trade processing, verify Chartify's signature protocol, add rate limiting, durable database/queue storage, payload validation, event-ID-based deduplication, monitoring, and a data-retention policy. Use persistent storage when hosting in a container.
- Never execute arbitrary code from the incoming payload, and do not share tokens, API credentials, signing secrets, account identifiers, or sensitive trade details when requesting help.
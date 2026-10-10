using System.Text.Json;

public sealed class JsonEventStore : IDisposable
{
    private readonly string databasePath;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly FileStream instanceLock;
    private readonly JsonSerializerOptions serializerOptions = new() { WriteIndented = true };

    public JsonEventStore(string databasePath)
    {
        this.databasePath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(this.databasePath)!);
        instanceLock = new FileStream(this.databasePath + ".lock",
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            if (!File.Exists(this.databasePath))
            {
                File.WriteAllText(this.databasePath, "[]");
            }
        }
        catch
        {
            instanceLock.Dispose();
            throw;
        }
    }

    public async Task AppendAsync(JsonElement receipt, CancellationToken cancellationToken)
    {
        await writeGate.WaitAsync(cancellationToken);
        var temporaryPath = databasePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            List<JsonElement> events;
            await using (var input = new FileStream(databasePath,
                FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true))
            {
                events = await JsonSerializer.DeserializeAsync<List<JsonElement>>(
                    input, cancellationToken: cancellationToken)
                    ?? throw new JsonException("The event database must contain a JSON array.");
            }

            events.Add(receipt.Clone());
            await using (var output = new FileStream(temporaryPath,
                FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(output, events, serializerOptions, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, databasePath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            finally
            {
                writeGate.Release();
            }
        }
    }

    public void Dispose()
    {
        instanceLock.Dispose();
        writeGate.Dispose();
    }
}
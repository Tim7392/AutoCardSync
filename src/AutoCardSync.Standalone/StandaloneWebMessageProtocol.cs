using System.Text.Json;

namespace AutoCardSync.Standalone;

public static class StandaloneWebMessageProtocol
{
    public const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public static string Serialize(
        string type,
        object? payload = null,
        string? requestId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        return JsonSerializer.Serialize(new
        {
            schemaVersion = SchemaVersion,
            type,
            requestId,
            payload,
        }, WebJson);
    }
}

using System.Text.Json;
using AutoCardSync.Standalone;

namespace AutoCardSync.Standalone.Core.Tests.UI;

public sealed class StandaloneWebMessageProtocolTests
{
    [Fact]
    public void Native_messages_always_include_schema_type_request_and_payload()
    {
        string json = StandaloneWebMessageProtocol.Serialize(
            "ui.response",
            new { success = true, data = new { configured = false } },
            "request-1");

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("ui.response", root.GetProperty("type").GetString());
        Assert.Equal("request-1", root.GetProperty("requestId").GetString());
        Assert.True(root.GetProperty("payload").GetProperty("success").GetBoolean());
        Assert.False(root.GetProperty("payload").GetProperty("data").GetProperty("configured").GetBoolean());
    }

    [Theory]
    [InlineData("standalone.configuration")]
    [InlineData("standalone.status")]
    public void Push_messages_include_required_schema(string type)
    {
        string json = StandaloneWebMessageProtocol.Serialize(type, new { value = 1 });
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(type, document.RootElement.GetProperty("type").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("payload").GetProperty("value").GetInt32());
    }
}

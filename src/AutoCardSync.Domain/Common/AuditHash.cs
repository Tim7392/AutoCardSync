using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AutoCardSync.Domain.Common;

public sealed record AuditEnvelope(
    Guid Id,
    string EventType,
    Guid? ClientId,
    Guid? TaskId,
    Guid? CardId,
    string Subject,
    DateTime UtcTime,
    long SequenceNumber,
    string? SoftwareVersion,
    string? ConfigVersion,
    string PayloadJson,
    string PreviousHash);

public static class AuditHash
{
    public const string CurrentVersion = "acs-audit-v2";

    public static string Compute(AuditEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Id == Guid.Empty)
            throw new ArgumentException(nameof(envelope.Id));
        if (envelope.ClientId == Guid.Empty)
            throw new ArgumentException(nameof(envelope.ClientId));
        if (envelope.TaskId == Guid.Empty)
            throw new ArgumentException(nameof(envelope.TaskId));
        if (envelope.CardId == Guid.Empty)
            throw new ArgumentException(nameof(envelope.CardId));
        if (envelope.SequenceNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(envelope.SequenceNumber));
        if (string.IsNullOrWhiteSpace(envelope.EventType))
            throw new ArgumentException(nameof(envelope.EventType));
        if (string.IsNullOrWhiteSpace(envelope.Subject))
            throw new ArgumentException(nameof(envelope.Subject));

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "HashVersion", CurrentVersion);
        Append(hash, "Id", envelope.Id.ToString("D"));
        Append(hash, "EventType", envelope.EventType);
        Append(hash, "ClientId", envelope.ClientId?.ToString("D") ?? string.Empty);
        Append(hash, "TaskId", envelope.TaskId?.ToString("D") ?? string.Empty);
        Append(hash, "CardId", envelope.CardId?.ToString("D") ?? string.Empty);
        Append(hash, "Subject", envelope.Subject);
        Append(hash, "UtcTime", NormalizeUtc(envelope.UtcTime).ToString("O"));
        Append(hash, "SequenceNumber", envelope.SequenceNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(hash, "SoftwareVersion", envelope.SoftwareVersion ?? string.Empty);
        Append(hash, "ConfigVersion", envelope.ConfigVersion ?? string.Empty);
        Append(hash, "PayloadJson", CanonicalizeJson(envelope.PayloadJson));
        Append(hash, "PreviousHash", envelope.PreviousHash.ToUpperInvariant());
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static string CanonicalizeJson(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
            WriteCanonical(writer, document.RootElement);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var propertyNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonProperty property in element.EnumerateObject().OrderBy(value => value.Name, StringComparer.Ordinal))
                {
                    if (!propertyNames.Add(property.Name))
                        throw new JsonException("Duplicate JSON object property names are not canonical.");
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(CanonicalizeNumber(element.GetRawText()), skipInputValidation: true);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new JsonException(nameof(element.ValueKind));
        }
    }

    private static string CanonicalizeNumber(string raw)
    {
        int offset = raw[0] == '-' ? 1 : 0;
        int exponentIndex = raw.IndexOfAny(['e', 'E']);
        ReadOnlySpan<char> mantissa = raw.AsSpan(offset,
            (exponentIndex < 0 ? raw.Length : exponentIndex) - offset);
        int decimalIndex = mantissa.IndexOf('.');
        int fractionLength = decimalIndex < 0 ? 0 : mantissa.Length - decimalIndex - 1;
        string digits = string.Concat(mantissa.ToString().Where(char.IsAsciiDigit)).TrimStart('0');
        long exponent = exponentIndex < 0 ? 0 : long.Parse(
            raw.AsSpan(exponentIndex + 1), System.Globalization.CultureInfo.InvariantCulture);
        exponent -= fractionLength;
        if (digits.Length == 0)
            return "0";
        int trailing = digits.Length - digits.TrimEnd('0').Length;
        if (trailing != 0)
        {
            digits = digits[..^trailing];
            exponent = checked(exponent + trailing);
        }
        return string.Concat(offset == 1 ? "-" : string.Empty, digits, "e",
            exponent.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static void Append(IncrementalHash hash, string name, string value)
    {
        AppendBytes(hash, Encoding.UTF8.GetBytes(name));
        AppendBytes(hash, Encoding.UTF8.GetBytes(value));
    }

    private static void AppendBytes(IncrementalHash hash, byte[] value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        hash.AppendData(length);
        hash.AppendData(value);
    }
}

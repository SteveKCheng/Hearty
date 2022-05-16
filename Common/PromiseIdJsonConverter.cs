using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hearty.Common;

/// <summary>
/// Plug-in to the System.Text.Json framework to serializes and de-serialize PromiseId.
/// </summary>
public class PromiseIdJsonConverter : JsonConverter<PromiseId>
{
    /// <inheritdoc cref="JsonConverter{T}.Read" />
    public override PromiseId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString();
        if (!PromiseId.TryParse(s, out var result))
            throw new JsonException("The JSON value was expected to be interpretable as PromiseId, but is not. ");
        return result;
    }

    /// <inheritdoc cref="JsonConverter{T}.Write" />
    public override void Write(Utf8JsonWriter writer, PromiseId value, JsonSerializerOptions options)
    {
        Span<byte> asciiBuffer = stackalloc byte[PromiseId.MaxChars];
        int numBytes = value.FormatAscii(asciiBuffer);
        asciiBuffer = asciiBuffer[0..numBytes];
        writer.WriteStringValue(asciiBuffer);
    }

#if NET6_0_OR_GREATER

    /// <inheritdoc cref="JsonConverter{T}.ReadAsPropertyName" />
    public override PromiseId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => Read(ref reader, typeToConvert, options);

    /// <inheritdoc cref="JsonConverter{T}.WriteAsPropertyName" />
    public override void WriteAsPropertyName(Utf8JsonWriter writer, PromiseId value, JsonSerializerOptions options)
    {
        Span<byte> asciiBuffer = stackalloc byte[PromiseId.MaxChars];
        int numBytes = value.FormatAscii(asciiBuffer);
        asciiBuffer = asciiBuffer[0..numBytes];
        writer.WritePropertyName(asciiBuffer);
    }

#endif
}

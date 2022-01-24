using System.Buffers;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JobBank.Server.Mocks
{
    /// <summary>
    /// Output data for the mock pricing jobs implemented by
    /// <see cref="MockPricingWorker" />.
    /// </summary>
    /// <remarks>
    /// These outputs include the theoretical price of a European option,
    /// a financial derivatives, along with its basic 
    /// sensitivities.
    /// </remarks>
    public readonly struct MockPricingOutput
    {
        [JsonPropertyName("name")]
        public string? InstrumentName { get; init; }

        [JsonPropertyName("value")]
        public double Value { get; init; }

        [JsonPropertyName("delta")]
        public double NotionalDelta { get; init; }

        [JsonPropertyName("gamma")]
        public double NotionalGamma { get; init; }

        [JsonPropertyName("theta")]
        public double ThetaDay { get; init; }

        [JsonPropertyName("vega")]
        public double Vega { get; init; }

        /// <summary>
        /// De-serialize an instance from JSON using the conventions of this library.
        /// </summary>
        /// <param name="bytes">
        /// JSON encoded in UTF-8 representing an instance of this structure.
        /// </param>
        /// <returns>
        /// The de-serialized instance of this structure.
        /// </returns>
        public static MockPricingOutput DeserializeFromJsonUtf8Bytes(ReadOnlySequence<byte> bytes)
        {
            var jsonReader = new Utf8JsonReader(bytes,
                               new JsonReaderOptions
                               {
                                   AllowTrailingCommas = true,
                                   CommentHandling = JsonCommentHandling.Skip
                               });

            var output = JsonSerializer.Deserialize<MockPricingOutput>(ref jsonReader);

            if (jsonReader.Read())
                throw new InvalidDataException("Extra content is present at the end of the expected JSON payload. ");

            return output;
        }
    }
}

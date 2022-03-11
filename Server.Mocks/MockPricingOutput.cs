using System;
using System.Buffers;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hearty.Server.Mocks
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
    public readonly struct MockPricingOutput : IEquatable<MockPricingOutput>
    {
        /// <summary>
        /// The name of the instrument, echoed back from 
        /// <see cref="MockPricingInput.InstrumentName" />.
        /// </summary>
        [JsonPropertyName("name")]
        public string? InstrumentName { get; init; }

        /// <summary>
        /// The computed net present value of the instrument.
        /// </summary>
        [JsonPropertyName("value")]
        public double Value { get; init; }

        /// <summary>
        /// The "delta" (sensitivity to underlying spot price)
        /// expressed in notional terms (the currency amount
        /// of the underlying to hold to hedge the instrument).
        /// </summary>
        [JsonPropertyName("delta")]
        public double NotionalDelta { get; init; }

        /// <summary>
        /// The "gamma" (second-order sensitivity to underlying spot price)
        /// expressed in notional terms, measured for a 1%
        /// move in the spot price of the underlying.
        /// </summary>
        [JsonPropertyName("gamma")]
        public double NotionalGamma { get; init; }

        /// <summary>
        /// The "theta" (change in instrment value after one day,
        /// assuming all other variables are held fixed).
        /// </summary>
        [JsonPropertyName("theta")]
        public double ThetaDay { get; init; }

        /// <summary>
        /// The "vega" (sensitivity to volatility)
        /// measured for a 1% absolute move in implied log-normal volatility.
        /// </summary>
        [JsonPropertyName("vega")]
        public double Vega { get; init; }

        /// <summary>
        /// Get an instance of <see cref="JsonReaderOptions" /> suitable for JSON
        /// de-serialization of this structure.
        /// </summary>
        public static JsonReaderOptions GetJsonReaderOptions()
        {
            return new JsonReaderOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            };
        }

        /// <summary>
        /// De-serialize an instance from JSON using the conventions of this library,
        /// from byte buffers.
        /// </summary>
        /// <param name="bytes">
        /// JSON encoded in UTF-8 representing an instance of this structure.
        /// </param>
        /// <returns>
        /// The de-serialized instance of this structure.
        /// </returns>
        public static MockPricingOutput DeserializeFromJsonUtf8Bytes(ReadOnlySequence<byte> bytes)
        {
            var jsonReader = new Utf8JsonReader(bytes, GetJsonReaderOptions());

            var output = JsonSerializer.Deserialize<MockPricingOutput>(ref jsonReader);

            if (jsonReader.Read())
                throw new InvalidDataException("Extra content is present at the end of the expected JSON payload. ");

            return output;
        }

        /// <summary>
        /// De-serialize an instance from JSON using the conventions of this library,
        /// from an I/O stream.
        /// </summary>
        /// <param name="stream">
        /// I/O stream whose content is JSON encoded in UTF-8 
        /// representing an instance of this structure.
        /// </param>
        /// <returns>
        /// The de-serialized instance of this structure.
        /// </returns>
        public static MockPricingOutput DeserializeFromStream(Stream stream)
        {
            return JsonSerializer.Deserialize<MockPricingOutput>(stream, options: null);
        }

        /// <summary>
        /// Compares this instance with another for structural equality
        /// of all members.
        /// </summary>
        public bool Equals(MockPricingOutput other)
        {
            return InstrumentName == other.InstrumentName &&
                   Value == other.Value &&
                   NotionalDelta == other.NotionalDelta &&
                   NotionalGamma == other.NotionalGamma &&
                   ThetaDay == other.ThetaDay &&
                   Vega == other.Vega;
        }

        /// <summary>
        /// Compares this instance with another for structural equality
        /// of all members.
        /// </summary>
        public override bool Equals(object? obj)
            => obj is MockPricingOutput other && Equals(other);

        /// <see cref="object.GetHashCode" />
        public override int GetHashCode()
        {
            return HashCode.Combine(InstrumentName, 
                                    Value, 
                                    NotionalDelta, 
                                    NotionalGamma, 
                                    ThetaDay, 
                                    Vega);
        }
    }
}

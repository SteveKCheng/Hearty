using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JobBank.Server.Mocks
{
    /// <summary>
    /// Input data for the mock pricing jobs implemented by
    /// <see cref="MockPricingWorker" />.
    /// </summary>
    /// <remarks>
    /// These inputs represent a European option, a 
    /// financial derivative, to be priced.
    /// </remarks>
    public readonly struct MockPricingInput
    {
        [JsonPropertyName("name")]
        public string? InstrumentName { get; init; }

        [JsonPropertyName("T")]
        public DateTime MaturityDate { get; init; }

        [JsonPropertyName("t")]
        public DateTime ValuationDate { get; init; }

        [JsonPropertyName("r")]
        public double InterestRate { get; init; }

        [JsonPropertyName("sigma")]
        public double Volatility { get; init; }

        [JsonPropertyName("q")]
        public double DividendYield { get; init; }

        [JsonPropertyName("S")]
        public double SpotPrice { get; init; }

        [JsonPropertyName("K")]
        public double StrikePrice { get; init; }

        [JsonPropertyName("c")]
        public bool? IsCall { get; init; }

        [JsonPropertyName("u")]
        public double? Units { get; init; }

        public static MockPricingInput GenerateRandomSample(DateTime valuationDate, Random random)
        {
            // Generate random volatility
            double sigma = 0.10 * MathFunctions.InverseGaussianCdf(random.NextDouble()) + 0.30;
            sigma = Math.Max(Math.Min(sigma, 0.95), 0.05);
            sigma = Math.Round(sigma, 4);

            // Generate random time-to-maturity in buckets of 3 months,
            // up to 5 years.
            int maturityMonths = 3 * (1 + random.Next(4 * 5));
            DateTime maturityDate = valuationDate.AddMonths(maturityMonths);

            // Generate log-normal variable for spot price,
            // assuming it was S0 three months prior.
            double S0 = 100.0;
            double x = MathFunctions.InverseGaussianCdf(random.NextDouble());
            double S = S0 * Math.Exp((sigma * Math.Sqrt(0.25)) * x
                                      - 0.5 * (sigma * sigma) * 0.25);
            S = Math.Round(S, 2);

            string instrumentName = $"Call-{S:F2}-{sigma * 100.0:F2}-{maturityMonths}m";

            return new MockPricingInput
            {
                InstrumentName = instrumentName,
                DividendYield = 0.0,
                InterestRate = 0.0,
                Volatility = sigma,
                SpotPrice = S,
                StrikePrice = S0,
                ValuationDate = valuationDate,
                MaturityDate = maturityDate,
                IsCall = true,
                Units = 1.0
            };
        }

        public static IEnumerable<MockPricingInput> 
            GenerateRandomSamples(DateTime valuationDate, int seed, int count)
        {
            var random = new Random(seed);

            for (int i = 0; i < count; ++i)
                yield return GenerateRandomSample(valuationDate, random);
        }

        /// <summary>
        /// Serialize this instance into JSON using the conventions of this library.
        /// </summary>
        /// <returns>
        /// JSON encoded in UTF-8 bytes.
        /// </returns>
        public byte[] SerializeToJsonUtf8Bytes()
            => JsonSerializer.SerializeToUtf8Bytes(this);
    }
}

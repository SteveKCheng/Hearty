using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hearty.Server.Mocks
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
        /// <summary>
        /// The name of the instrument that has these
        /// pricing inputs, which is essentially arbitrary.
        /// </summary>
        [JsonPropertyName("name")]
        public string? InstrumentName { get; init; }

        /// <summary>
        /// The maturity date/time of the European option.
        /// </summary>
        [JsonPropertyName("T")]
        public DateTime MaturityDate { get; init; }

        /// <summary>
        /// The date/time for which to value the 
        /// European option on.
        /// </summary>
        [JsonPropertyName("t")]
        public DateTime ValuationDate { get; init; }

        /// <summary>
        /// The annual continuously-compounded 
        /// rate of interest, assumed to be constant.
        /// </summary>
        [JsonPropertyName("r")]
        public double InterestRate { get; init; }

        /// <summary>
        /// The annualized log-normal volatility
        /// of the price of the underlying asset, 
        /// assumed to be constant.
        /// </summary>
        [JsonPropertyName("sigma")]
        public double Volatility { get; init; }

        /// <summary>
        /// The annual continuously-compounded 
        /// (dividend) yield of the underlying asset, 
        /// assumed to be constant.
        /// </summary>
        [JsonPropertyName("q")]
        public double DividendYield { get; init; }

        /// <summary>
        /// The current price of the underlying asset.
        /// </summary>
        [JsonPropertyName("S")]
        public double SpotPrice { get; init; }

        /// <summary>
        /// The strike price of the European option.
        /// </summary>
        [JsonPropertyName("K")]
        public double StrikePrice { get; init; }

        /// <summary>
        /// Whether the European option is to be a
        /// call (true) or a put (false).
        /// </summary>
        /// <remarks>
        /// Null is treated the same as true;
        /// it is allowed for the sake of allowing
        /// the value to be omitted in the serialized
        /// representation of this structure.
        /// </remarks>
        [JsonPropertyName("c")]
        public bool? IsCall { get; init; }

        /// <summary>
        /// The number of units of the European option.
        /// </summary>
        /// <remarks>
        /// This property is just a multiplier on output
        /// values.  Null is treated the same as 1.0;
        /// it is allowed for the sake of allowing
        /// the value to be omitted in the serialized
        /// representation of this structure.
        /// </remarks>
        [JsonPropertyName("u")]
        public double? Units { get; init; }

        /// <summary>
        /// Generate an instance with randomized member values
        /// from prescribed probability distributions, for testing.
        /// </summary>
        /// <param name="valuationDate">
        /// The desired valuation date.
        /// </param>
        /// <param name="random">
        /// Generator providing uniformly distributed
        /// pseudo-random variates.
        /// </param>
        /// <returns>
        /// A randomly generated instance of this type.
        /// </returns>
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

        /// <summary>
        /// Generate many instances of this type for testing.
        /// </summary>
        /// <param name="valuationDate">
        /// The desired valuation date.
        /// </param>
        /// <param name="int">
        /// Seed to construct the <see cref="Random" /> object
        /// from which pseudo-random numbers are obtained.
        /// </param>
        /// <param name="count">
        /// Number of instances to generate.
        /// </param>
        /// <returns>
        /// A sequence of <paramref name="count" /> 
        /// random instances of this type.
        /// </returns>
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

        /// <summary>
        /// Make some checks on the validity of this input.
        /// </summary>
        /// <param name="messages">
        /// If there are validity errors, they are described in
        /// this container, which will be set on return.
        /// </param>
        /// <returns>
        /// True if there are no errors.  False when there are errors.
        /// </returns>
        public bool Validate([NotNullWhen(false)] out IList<string>? messages)
        {
            List<string>? m = null;

            static void AddMessage(ref List<string>? messages, string text)
            {
                messages ??= new List<string>(capacity: 4);
                messages.Add(text);
            }

            if (!(Volatility > 0.0))
                AddMessage(ref m, "Volatility must be positive. ");

            if (!(SpotPrice > 0.0))
                AddMessage(ref m, "Spot price must be positive. ");

            if (!(StrikePrice > 0.0))
                AddMessage(ref m, "Strike price must be positive. ");

            if (!(MaturityDate - ValuationDate > TimeSpan.Zero))
                AddMessage(ref m, "Maturity time must be after valuation time. ");

            messages = m;
            return m is null;
        }

        /// <summary>
        /// Calculate the outputs corresponding to this
        /// pricing input.
        /// </summary>
        /// <returns>
        /// The calculated results.
        /// </returns>
        public MockPricingOutput Calculate()
        {
            var S = SpotPrice;
            var K = StrikePrice;
            var σ = Volatility;
            var r = InterestRate;
            var q = DividendYield;
            var τ = (MaturityDate - ValuationDate).TotalDays / 365.25;
            var z = Math.Log(S / K);
            var s = σ * Math.Sqrt(τ);
            var v = σ * σ;
            var μ = r - q;
            var dₚ = (z + (μ + 0.5 * v) * τ) / s;
            var dₘ = (z + (μ - 0.5 * v) * τ) / s;

            var Fd = S * Math.Exp(-q * τ);
            var Kd = K * Math.Exp(-r * τ);

            var Φdₚ = MathFunctions.GaussianCdf(dₚ);
            var Φdₘ = MathFunctions.GaussianCdf(dₘ);
            var φdₚ = MathFunctions.GaussianPdf(dₚ);

            double V;
            double Δ;
            double Γ;
            double ϴ;
            double vega;

            if (IsCall != false)
            {
                V = Fd * Φdₚ - Kd * Φdₘ;
                Δ = Fd * Φdₚ;
                ϴ = ((-0.5 * Fd) * (s / τ) * Φdₚ
                  - r * Kd * Φdₘ
                  + q * Fd * Φdₚ) / 365.25;
            }
            else
            {
                V = Kd * (1.0 - Φdₘ) - Fd * (1.0 - Φdₚ);
                Δ = Fd * (Φdₚ - 1.0);
                ϴ = ((-0.5 * Fd) * (s / τ) * Φdₚ
                  + r * Kd * (1.0 - Φdₘ)
                  - q * Fd * (1.0 - Φdₚ)) / 365.25;
            }

            Γ = (Fd * 0.01 / s) * φdₚ;
            vega = (0.01 * Math.Sqrt(τ) * Fd) * φdₚ;

            var u = Units ?? 1.0;

            return new MockPricingOutput
            {
                InstrumentName = InstrumentName,
                Value = V * u,
                NotionalDelta = Δ * u,
                NotionalGamma = Γ * u,
                ThetaDay = ϴ * u,
                Vega = vega * u
            };
        }
    }
}

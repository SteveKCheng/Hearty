using JobBank.Work;
using System;
using System.Buffers;
using System.IO;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace JobBank.Server.Mocks
{
    /// <summary>
    /// Implementation of a mock pricing service that can be used
    /// as a worker for a job server.
    /// </summary>
    public class MockPricingWorker : IJobSubmission 
    {
        private static MockPricingOutput BlackScholesEuropeanOption(in MockPricingInput input)
        {
            var S = input.SpotPrice;
            var K = input.StrikePrice;
            var σ = input.Volatility;
            var r = input.InterestRate;
            var q = input.DividendYield;
            var τ = (input.MaturityDate - input.ValuationDate).TotalDays / 365.25;
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

            if (input.IsCall != false)
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

            var u = input.Units ?? 1.0;

            return new MockPricingOutput
            {
                InstrumentName = input.InstrumentName,
                Value = V * u,
                NotionalDelta = Δ * u,
                NotionalGamma = Γ * u,
                ThetaDay = ϴ * u,
                Vega = vega * u
            };
        }

        private const string JsonMediaType = "application/json";

        private static void ValidateJsonContentType(string contentType)
        {
            if (string.Equals(contentType, JsonMediaType, StringComparison.OrdinalIgnoreCase))
                return;

            if (!MediaTypeHeaderValue.TryParse(contentType, out var parsedContentType))
                throw new InvalidDataException("Content-Type is invalid. ");

            if (!string.Equals(parsedContentType.MediaType,
                               JsonMediaType,
                               StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The pricing request is not in JSON. ");

            if (parsedContentType.CharSet != null &&
                !string.Equals(parsedContentType.CharSet, "utf-8", 
                                StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("JSON data must be encoded in UTF-8. ");
        }

        private static MockPricingInput 
            DeserializePricingInput(RunJobRequestMessage request)
        {
            ValidateJsonContentType(request.ContentType);

            var jsonReader = new Utf8JsonReader(request.Data,
                               new JsonReaderOptions
                               {
                                   AllowTrailingCommas = true,
                                   CommentHandling = JsonCommentHandling.Skip
                               });

            var input = JsonSerializer.Deserialize<MockPricingInput>(ref jsonReader);

            if (jsonReader.Read())
                throw new InvalidDataException("Extra content is present at the end of the expected JSON payload. ");

            return input;
        }

        private readonly JsonSerializerOptions _jsonSerializerOptions = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true
        };

        private RunJobReplyMessage 
            SerializePricingOutput(MockPricingOutput output)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(output, 
                                                            _jsonSerializerOptions);

            return new RunJobReplyMessage
            {
                ContentType = JsonMediaType,
                Data = new ReadOnlySequence<byte>(bytes)
            };
        }

        /// <summary>
        /// Accepts and answers a request for mock pricing, after an artificial delay.
        /// </summary>
        /// <param name="request">
        /// Job request whose payload is the UTF-8 JSON representation 
        /// of <see cref="MockPricingInput" />.  The member
        /// <see cref="RunJobRequestMessage.InitialWait" /> specifies
        /// the artificial delay.
        /// </param>
        /// <param name="cancellationToken">
        /// Can be used to cancel the pricing.
        /// </param>
        /// <returns>
        /// The reply to the pricing request, with the result being
        /// <see cref="MockPricingOutput" /> represented as UTF-8 JSON.
        /// </returns>
        public async ValueTask<RunJobReplyMessage> 
            RunJobAsync(RunJobRequestMessage request, 
                        CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting job for execution ID {executionId} on mock pricing worker {workerName}",
                                   request.ExecutionId, _name);

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pricingInput = DeserializePricingInput(request);

                // Simulate working for some period of time
                if (request.InitialWait < 0 || request.InitialWait > 60 * 1000)
                    throw new ArgumentOutOfRangeException(
                        "InitialWait parameter of the job request is out of range. ",
                        (Exception?)null);
                await Task.Delay(request.InitialWait, cancellationToken)
                          .ConfigureAwait(false);

                var pricingOutput = BlackScholesEuropeanOption(pricingInput);
                var reply = SerializePricingOutput(pricingOutput);

                _logger.LogInformation("Ending job for execution ID {executionId}, on mock pricing worker {workerName}. " +
                                       "Instrument {instrument} priced at {value:F4}. ",
                                       request.ExecutionId, _name, pricingInput.InstrumentName ?? "-", pricingOutput.Value);

                return reply;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Job for execution ID {executionId} on mock pricing worker {workerName} has been cancelled. ",
                                       request.ExecutionId, _name);
                throw;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Job for execution ID {executionId} on mock pricing worker {workerName} failed. ",
                                 request.ExecutionId, _name);
                throw;
            }
        }

        private readonly ILogger _logger;
        private readonly string _name;

        /// <summary>
        /// Constructs the implementation of a worker that does mock pricing.
        /// </summary>
        /// <param name="logger">Logs the jobs accepted by the new worker. </param>
        /// <param name="name">The name of the new worker to be displayed in logs. </param>
        public MockPricingWorker(ILogger<MockPricingWorker> logger,
                                 string name)
        {
            _logger = logger;
            _name = name;
        }
    }
}

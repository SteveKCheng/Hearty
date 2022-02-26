using Hearty.Work;
using System;
using System.Buffers;
using System.IO;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Hearty.Server.Mocks
{
    /// <summary>
    /// Implementation of a mock pricing service that can be used
    /// as a worker for a job server.
    /// </summary>
    public class MockPricingWorker : IJobSubmission 
    {
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
            DeserializePricingInput(JobRequestMessage request)
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

        private JobReplyMessage 
            SerializePricingOutput(MockPricingOutput output)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(output, 
                                                            _jsonSerializerOptions);

            return new JobReplyMessage
            {
                ContentType = JsonMediaType,
                Data = new ReadOnlySequence<byte>(bytes)
            };
        }

        private readonly Random _random = new(79);

        private double GenerateRandomLogNormal(double mean, 
                                               double logStdDev, 
                                               double floor = 0.0,
                                               double cap = double.PositiveInfinity)
        {
            double z;
            lock (_random)
                z = _random.NextDouble();

            double x = Math.Exp(-0.5 * (logStdDev * logStdDev) 
                                + logStdDev * MathFunctions.InverseGaussianCdf(z));

            return Math.Max(Math.Min(mean * x, cap), floor);
        }

        /// <summary>
        /// Accepts and answers a request for mock pricing, after an artificial delay.
        /// </summary>
        /// <param name="request">
        /// Job request whose payload is the UTF-8 JSON representation 
        /// of <see cref="MockPricingInput" />.  The member
        /// <see cref="JobRequestMessage.EstimatedWait" /> specifies
        /// the artificial delay.
        /// </param>
        /// <param name="cancellationToken">
        /// Can be used to cancel the pricing.
        /// </param>
        /// <returns>
        /// The reply to the pricing request, with the result being
        /// <see cref="MockPricingOutput" /> represented as UTF-8 JSON.
        /// </returns>
        public async ValueTask<JobReplyMessage> 
            RunJobAsync(JobRequestMessage request, 
                        CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting job for execution ID {executionId} on mock pricing worker {workerName}",
                                   request.ExecutionId, Name);

            try
            {
                var pricingInput = DeserializePricingInput(request);

                if (!pricingInput.Validate(out _))
                    throw new InvalidDataException("The pricing input is invalid. ");

                // Simulate working for some period of time.
                int meanWaitTime = pricingInput.MeanWaitTime;
                if (meanWaitTime < 0 || meanWaitTime > 60 * 60 * 1000)
                {
                    throw new ArgumentOutOfRangeException(
                                paramName: nameof(MockPricingInput.MeanWaitTime),
                                message: "InitialWait parameter of the job request is out of range. ");
                }

                // The time taken is randomized on a log-normal distribution,
                // if greater than a certain threshold.
                int waitTime = (meanWaitTime > 100)
                                ? (int)Math.Ceiling(GenerateRandomLogNormal((double)meanWaitTime, 
                                                                            0.75, 
                                                                            cap: 1.5 * meanWaitTime))
                                : meanWaitTime;

                await Task.Delay(waitTime, cancellationToken)
                          .ConfigureAwait(false);

                var pricingOutput = pricingInput.Calculate();

                cancellationToken.ThrowIfCancellationRequested();

                var reply = SerializePricingOutput(pricingOutput);

                _logger.LogInformation("Ending job for execution ID {executionId}, on mock pricing worker {workerName}. " +
                                       "Instrument {instrument} priced at {value:F4}. ",
                                       request.ExecutionId, Name, pricingInput.InstrumentName ?? "-", pricingOutput.Value);

                return reply;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Job for execution ID {executionId} on mock pricing worker {workerName} has been cancelled. ",
                                       request.ExecutionId, Name);
                throw;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Job for execution ID {executionId} on mock pricing worker {workerName} failed. ",
                                 request.ExecutionId, Name);
                throw;
            }
        }

        private readonly ILogger _logger;
        
        /// <summary>
        /// The name of the new worker to be displayed in logs.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Constructs the implementation of a worker that does mock pricing.
        /// </summary>
        /// <param name="logger">Logs the jobs accepted by the new worker. </param>
        /// <param name="name">The name of the new worker to be displayed in logs. </param>
        public MockPricingWorker(ILogger<MockPricingWorker> logger,
                                 string name)
        {
            _logger = logger;
            Name = name;
        }
    }
}

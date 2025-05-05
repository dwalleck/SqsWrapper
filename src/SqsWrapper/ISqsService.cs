using Amazon.SQS.Model;
using System.Threading;
using System.Threading.Tasks;

namespace SqsWrapper
{
    /// <summary>
    /// Interface for a service that sends messages to Amazon SQS with resilience features.
    /// </summary>
    public interface ISqsService
    {
        /// <summary>
        /// Sends a message to the specified Amazon SQS queue with automatic retries and resilience.
        /// </summary>
        /// <param name="queueUrl">The URL of the Amazon SQS queue to which a message is sent.</param>
        /// <param name="message">The message to send.</param>
        /// <param name="token">A cancellation token that can be used to cancel the operation.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the response from the SendMessage service method.</returns>
        /// <exception cref="System.ArgumentException">Thrown when queueUrl or message is null or empty.</exception>
        /// <exception cref="System.InvalidOperationException">Thrown when the SQS client cannot be created.</exception>
        /// <exception cref="Amazon.SQS.AmazonSQSException">Thrown when an error occurs while communicating with the SQS service.</exception>
        /// <exception cref="Polly.CircuitBreaker.BrokenCircuitException">Thrown when the circuit breaker is open due to previous failures.</exception>
        Task<SendMessageResponse> SendSqsMessageAsync(string queueUrl, string message, CancellationToken token);
        
        /// <summary>
        /// Gets the current health status of the SQS service connection.
        /// </summary>
        /// <param name="token">A cancellation token that can be used to cancel the operation.</param>
        /// <returns>True if the service is healthy, false otherwise.</returns>
        Task<bool> IsHealthyAsync(CancellationToken token);
    }
}

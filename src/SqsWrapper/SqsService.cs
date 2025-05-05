using Amazon.SecurityToken;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;
using Polly.Wrap;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace SqsWrapper
{
    /// <summary>
    /// Service for sending messages to Amazon SQS with automatic credential refresh and resilience features.
    /// </summary>
    /// <remarks>
    /// This service ensures that the SQS client always has valid credentials by refreshing them
    /// before they expire. It also provides thread-safe access to the SQS client and implements
    /// resilience patterns using Polly.
    /// </remarks>
    public class SqsService : ISqsService
    {
        private readonly ISqsClientFactory _clientFactory;
        private IAmazonSQS? _sqsClient;
        /// <summary>
        /// Semaphore to ensure thread-safe access to the SQS client.
        /// </summary>
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
        /// <summary>
        /// The time when the current credentials will expire.
        /// </summary>
        private DateTime _credentialExpiration = DateTime.MinValue;
        private readonly ILogger<SqsService>? _logger;
        
        // Circuit breaker state
        private CircuitState _circuitState = CircuitState.Closed;
        
        // Resilience policies
        private readonly AsyncRetryPolicy _credentialRefreshPolicy;
        private readonly AsyncCircuitBreakerPolicy _circuitBreakerPolicy;
        private readonly AsyncTimeoutPolicy _timeoutPolicy;
        private readonly AsyncPolicyWrap _messageSendingPolicy;

        /// <summary>
        /// Initializes a new instance of the <see cref="SqsService"/> class.
        /// </summary>
        /// <param name="clientFactory">The factory used to create SQS clients with temporary credentials.</param>
        /// <param name="logger">Optional logger for diagnostic information.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="clientFactory"/> is null.</exception>
        public SqsService(ISqsClientFactory clientFactory, ILogger<SqsService>? logger = null)
        {
            ArgumentNullException.ThrowIfNull(clientFactory, nameof(clientFactory));
            _clientFactory = clientFactory;
            _logger = logger;
            
            // Define retry policy for credential refresh
            _credentialRefreshPolicy = Policy
                .Handle<Exception>(ex => IsTransientException(ex))
                .WaitAndRetryAsync(
                    3, // Retry 3 times
                    retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)), // Exponential backoff
                    (exception, timeSpan, retryCount, context) =>
                    {
                        _logger?.LogWarning(
                            exception,
                            "Error refreshing credentials. Retrying after {RetryTimeSpan}s. Attempt {RetryCount} of 3",
                            timeSpan.TotalSeconds,
                            retryCount);
                    });
            
            // Define circuit breaker policy
            _circuitBreakerPolicy = Policy
                .Handle<Exception>(ex => !IsUserCancellation(ex))
                .CircuitBreakerAsync(
                    exceptionsAllowedBeforeBreaking: 5,
                    durationOfBreak: TimeSpan.FromMinutes(1),
                    onBreak: (ex, breakDuration) =>
                    {
                        _circuitState = CircuitState.Open;
                        _logger?.LogError(
                            ex,
                            "Circuit breaker opened. SQS operations suspended for {BreakDuration}s",
                            breakDuration.TotalSeconds);
                    },
                    onReset: () =>
                    {
                        _circuitState = CircuitState.Closed;
                        _logger?.LogInformation("Circuit breaker reset. SQS operations resumed");
                    },
                    onHalfOpen: () =>
                    {
                        _circuitState = CircuitState.HalfOpen;
                        _logger?.LogInformation("Circuit breaker half-open. Testing SQS connectivity");
                    });
            
            // Define timeout policy
            _timeoutPolicy = Policy.TimeoutAsync(30, TimeoutStrategy.Pessimistic, onTimeoutAsync: (context, timespan, task) =>
            {
                _logger?.LogWarning("SQS operation timed out after {Timespan}s", timespan.TotalSeconds);
                return Task.CompletedTask;
            });
            
            // Combine policies for message sending
            _messageSendingPolicy = Policy.WrapAsync(
                _timeoutPolicy,
                _circuitBreakerPolicy,
                Policy
                    .Handle<AmazonSQSException>(ex => IsCredentialExpiredException(ex))
                    .RetryAsync(1, async (exception, retryCount, context) =>
                    {
                        _logger?.LogWarning(
                            exception,
                            "Credential-related exception detected. Forcing credential refresh before retry");
                            
                        await ForceCredentialRefreshAsync(
                            CancellationToken.None); // Use a new token since the context one might be canceled
                    })
            );
        }

        /// <summary>
        /// Gets an SQS client with valid credentials, creating a new one if necessary.
        /// </summary>
        /// <param name="token">A cancellation token that can be used to cancel the operation.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the Amazon SQS client.</returns>
        /// <remarks>
        /// This method is thread-safe and will refresh the client if the credentials are about to expire.
        /// </remarks>
        private async Task<IAmazonSQS> GetSqsClientAsync(CancellationToken token)
        {
            // Only allow one thread to access the critical section at a time
            await _semaphore.WaitAsync(token);
            try
            {
                // Check for cancellation before potentially expensive operations
                token.ThrowIfCancellationRequested();
                
                // Create or refresh client if needed
                if (_sqsClient == null || DateTime.UtcNow >= _credentialExpiration)
                {
                    _logger?.LogDebug("Creating new SQS client or refreshing credentials");
                    
                    // Execute with retry policy
                    _sqsClient = await _credentialRefreshPolicy.ExecuteAsync(
                        async () => await _clientFactory.CreateClientAsync(token));
                        
                    // Refresh before the 1-hour expiration
                    _credentialExpiration = DateTime.UtcNow.AddMinutes(55);
                    
                    _logger?.LogDebug("SQS client created. Credentials valid until {ExpirationTime}", 
                        _credentialExpiration);
                }
                return _sqsClient;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Sends a message to the specified Amazon SQS queue with automatic retries and resilience.
        /// </summary>
        /// <param name="queueUrl">The URL of the Amazon SQS queue to which a message is sent.</param>
        /// <param name="message">The message to send.</param>
        /// <param name="token">A cancellation token that can be used to cancel the operation.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the response from the SendMessage service method.</returns>
        /// <exception cref="ArgumentException">Thrown when queueUrl or message is null or empty.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the SQS client cannot be created.</exception>
        /// <exception cref="Amazon.SQS.AmazonSQSException">Thrown when an error occurs while communicating with the SQS service.</exception>
        /// <exception cref="Polly.CircuitBreaker.BrokenCircuitException">Thrown when the circuit breaker is open due to previous failures.</exception>
        public async Task<SendMessageResponse> SendSqsMessageAsync(
            string queueUrl,
            string message,
            CancellationToken token)
        {
            // Validate parameters
            if (string.IsNullOrEmpty(queueUrl))
            {
                throw new ArgumentException("Queue URL cannot be null or empty", nameof(queueUrl));
            }

            if (string.IsNullOrEmpty(message))
            {
                throw new ArgumentException("Message cannot be null or empty", nameof(message));
            }
            
            // Check circuit breaker state
            if (_circuitState == CircuitState.Open)
            {
                throw new BrokenCircuitException("Cannot send message to SQS. Circuit is open due to previous failures.");
            }

            // Create context with cancellation token
            var context = new Context("SendMessage");

            try
            {
                _logger?.LogDebug("Sending message to queue {QueueUrl}", queueUrl);
                
                // Execute with resilience policies
                return await _messageSendingPolicy.ExecuteAsync(async (ctx) =>
                {
                    var client = await GetSqsClientAsync(token);
                    return await client.SendMessageAsync(queueUrl, message, token);
                }, context);
            }
            catch (Exception ex) when (IsUserCancellation(ex))
            {
                _logger?.LogInformation("SQS operation was cancelled by user");
                throw; // Rethrow cancellation exceptions
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error sending message to SQS queue {QueueUrl}", queueUrl);
                throw; // Rethrow other exceptions
            }
        }

        /// <summary>
        /// Gets the current health status of the SQS service connection.
        /// </summary>
        /// <param name="token">A cancellation token that can be used to cancel the operation.</param>
        /// <returns>True if the service is healthy, false otherwise.</returns>
        public async Task<bool> IsHealthyAsync(CancellationToken token)
        {
            try
            {
                // Check if we can get a client
                var client = await GetSqsClientAsync(token);
                
                // Check circuit breaker state
                return _circuitState != CircuitState.Open;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Health check failed");
                return false;
            }
        }

        /// <summary>
        /// Forces a refresh of the SQS client's credentials.
        /// </summary>
        /// <param name="token">A cancellation token that can be used to cancel the operation.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        /// <remarks>
        /// This method is thread-safe and will create a new SQS client with fresh credentials.
        /// </remarks>
        private async Task ForceCredentialRefreshAsync(CancellationToken token)
        {
            await _semaphore.WaitAsync(token);
            try
            {
                // Check for cancellation before potentially expensive operations
                token.ThrowIfCancellationRequested();
                
                _logger?.LogInformation("Forcing credential refresh");
                
                // Force expiration to trigger refresh
                _credentialExpiration = DateTime.MinValue;
                
                // Execute with retry policy
                _sqsClient = await _credentialRefreshPolicy.ExecuteAsync(
                    async () => await _clientFactory.CreateClientAsync(token));
                    
                _credentialExpiration = DateTime.UtcNow.AddMinutes(55);
                
                _logger?.LogDebug("Credentials refreshed. Valid until {ExpirationTime}", 
                    _credentialExpiration);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Determines whether an exception is related to expired or invalid credentials.
        /// </summary>
        /// <param name="ex">The exception to check.</param>
        /// <returns>True if the exception is related to expired or invalid credentials; otherwise, false.</returns>
        private bool IsCredentialExpiredException(AmazonSQSException ex)
        {
            return ex.Message.Contains("security token", StringComparison.OrdinalIgnoreCase) &&
                   (ex.Message.Contains("expired", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("invalid", StringComparison.OrdinalIgnoreCase)) ||
                   ex.Message.Contains("service unavailable", StringComparison.OrdinalIgnoreCase);
        }
        
        /// <summary>
        /// Determines whether an exception is a transient error that can be retried.
        /// </summary>
        /// <param name="ex">The exception to check.</param>
        /// <returns>True if the exception is a transient error; otherwise, false.</returns>
        private bool IsTransientException(Exception ex)
        {
            if (ex is AmazonSQSException sqsEx)
            {
                return sqsEx.StatusCode == System.Net.HttpStatusCode.InternalServerError ||
                       sqsEx.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable ||
                       sqsEx.StatusCode == System.Net.HttpStatusCode.RequestTimeout ||
                       IsCredentialExpiredException(sqsEx);
            }
            
            if (ex is AmazonSecurityTokenServiceException stsEx)
            {
                return stsEx.StatusCode == System.Net.HttpStatusCode.InternalServerError ||
                       stsEx.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable ||
                       stsEx.StatusCode == System.Net.HttpStatusCode.RequestTimeout;
            }
            
            return false;
        }
        
        /// <summary>
        /// Determines whether an exception is due to user cancellation.
        /// </summary>
        /// <param name="ex">The exception to check.</param>
        /// <returns>True if the exception is due to user cancellation; otherwise, false.</returns>
        private bool IsUserCancellation(Exception ex)
        {
            return ex is OperationCanceledException || 
                   ex is TaskCanceledException;
        }
    }
}

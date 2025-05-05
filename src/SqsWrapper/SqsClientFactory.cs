using Amazon.SecurityToken.Model;
using Amazon.SecurityToken;
using Amazon.SQS;
using Amazon;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using System;
using System.Threading.Tasks;
using System.Threading;
using System.Net;

namespace SqsWrapper
{
    /// <summary>
    /// Factory for creating Amazon SQS clients with temporary credentials obtained by assuming a role.
    /// </summary>
    /// <remarks>
    /// This factory uses AWS Security Token Service (STS) to assume a role and obtain temporary
    /// credentials, which are then used to create an Amazon SQS client. This approach ensures
    /// that the SQS client always has valid credentials.
    /// </remarks>
    public class SqsClientFactory : ISqsClientFactory
    {
        private readonly IAmazonSecurityTokenService _stsClient;
        private readonly IOptions<DataSettings> _roleOptions;
        private readonly ILogger<SqsClientFactory>? _logger;
        private readonly AsyncRetryPolicy _stsRetryPolicy;

        /// <summary>
        /// Initializes a new instance of the <see cref="SqsClientFactory"/> class.
        /// </summary>
        /// <param name="stsClient">The AWS Security Token Service client used to assume roles.</param>
        /// <param name="roleOptions">The configuration options containing the role ARN and region.</param>
        /// <param name="logger">Optional logger for diagnostic information.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="stsClient"/> or <paramref name="roleOptions"/> is null.</exception>
        public SqsClientFactory(
            IAmazonSecurityTokenService stsClient,
            IOptions<DataSettings> roleOptions,
            ILogger<SqsClientFactory>? logger = null)
        {
            ArgumentNullException.ThrowIfNull(stsClient, nameof(stsClient));
            ArgumentNullException.ThrowIfNull(roleOptions, nameof(roleOptions));
            
            _stsClient = stsClient;
            _roleOptions = roleOptions;
            _logger = logger;
            
            // Define retry policy for STS operations
            _stsRetryPolicy = Policy
                .Handle<AmazonSecurityTokenServiceException>(ex => 
                    ex.StatusCode == HttpStatusCode.InternalServerError ||
                    ex.StatusCode == HttpStatusCode.ServiceUnavailable ||
                    ex.StatusCode == HttpStatusCode.RequestTimeout)
                .WaitAndRetryAsync(
                    3, // Retry 3 times
                    retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)), // Exponential backoff
                    (exception, timeSpan, retryCount, context) =>
                    {
                        _logger?.LogWarning(
                            exception,
                            "Error during STS operation. Retrying after {RetryTimeSpan}s. Attempt {RetryCount} of 3",
                            timeSpan.TotalSeconds,
                            retryCount);
                    });
        }

        /// <summary>
        /// Creates an Amazon SQS client with temporary credentials obtained by assuming a role.
        /// </summary>
        /// <param name="token">A cancellation token that can be used to cancel the operation.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the Amazon SQS client.</returns>
        /// <exception cref="ArgumentException">Thrown when required configuration settings are missing or invalid.</exception>
        /// <exception cref="InvalidOperationException">Thrown when credentials cannot be obtained.</exception>
        /// <exception cref="Amazon.SecurityToken.AmazonSecurityTokenServiceException">Thrown when an error occurs while communicating with the AWS Security Token Service.</exception>
        public async Task<IAmazonSQS> CreateClientAsync(CancellationToken token)
        {
            // Validate settings
            if (string.IsNullOrEmpty(_roleOptions.Value.RoleArn))
            {
                throw new ArgumentException("RoleArn must be specified in the DataSettings configuration.");
            }

            if (string.IsNullOrEmpty(_roleOptions.Value.Region))
            {
                throw new ArgumentException("Region must be specified in the DataSettings configuration.");
            }
            
            // Create a context with the cancellation token
            var context = new Context("AssumeRole");
            
            // Execute with retry policy
            return await _stsRetryPolicy.ExecuteAsync(async (ctx) =>
            {
                // Check for cancellation
                token.ThrowIfCancellationRequested();
                
                _logger?.LogDebug("Assuming role {RoleArn} in region {Region}", 
                    _roleOptions.Value.RoleArn, 
                    _roleOptions.Value.Region);
                
                // Assume the configured role
                var assumeRoleRequest = new AssumeRoleRequest
                {
                    RoleArn = _roleOptions.Value.RoleArn,
                    RoleSessionName = $"Payments-SQS-Session-{Guid.NewGuid()}"
                };

                var assumeRoleResponse = await _stsClient.AssumeRoleAsync(assumeRoleRequest, token);

                // Validate credentials
                if (assumeRoleResponse.Credentials == null)
                {
                    throw new InvalidOperationException("Null credentials received from AssumeRole operation.");
                }

                _logger?.LogDebug("Successfully assumed role. Credentials expire at {ExpirationTime}", 
                    assumeRoleResponse.Credentials.Expiration);

                return new AmazonSQSClient(
                    assumeRoleResponse.Credentials.AccessKeyId,
                    assumeRoleResponse.Credentials.SecretAccessKey,
                    assumeRoleResponse.Credentials.SessionToken,
                    new AmazonSQSConfig
                    {
                        RegionEndpoint = RegionEndpoint.GetBySystemName(_roleOptions.Value.Region)
                    }
                );
            }, context);
        }
    }
}

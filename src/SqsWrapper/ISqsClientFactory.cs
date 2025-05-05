using Amazon.SQS;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SqsWrapper
{
    /// <summary>
    /// Interface for a factory that creates Amazon SQS clients with temporary credentials.
    /// </summary>
    public interface ISqsClientFactory
    {
        /// <summary>
        /// Creates an Amazon SQS client with temporary credentials obtained by assuming a role.
        /// </summary>
        /// <param name="token">A cancellation token that can be used to cancel the operation.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the Amazon SQS client.</returns>
        /// <exception cref="System.ArgumentException">Thrown when required configuration settings are missing or invalid.</exception>
        /// <exception cref="System.InvalidOperationException">Thrown when credentials cannot be obtained.</exception>
        /// <exception cref="Amazon.SecurityToken.AmazonSecurityTokenServiceException">Thrown when an error occurs while communicating with the AWS Security Token Service.</exception>
        Task<IAmazonSQS> CreateClientAsync(CancellationToken token);
    }
}

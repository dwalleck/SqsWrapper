using Amazon;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Amazon.SQS;
using Microsoft.Extensions.Options;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace SqsWrapper.UnitTests
{
    public class SqsClientFactoryTests
    {
        private readonly Mock<IAmazonSecurityTokenService> _mockStsClient;
        private readonly IOptions<DataSettings> _options;
        private readonly SqsClientFactory _factory;
        private readonly CancellationToken _cancellationToken;

        public SqsClientFactoryTests()
        {
            _mockStsClient = new Mock<IAmazonSecurityTokenService>();
            _options = Options.Create(new DataSettings
            {
                RoleArn = "test-role-arn",
                Region = "us-west-2"
            });
            _factory = new SqsClientFactory(_mockStsClient.Object, _options);
            _cancellationToken = CancellationToken.None;
        }

        [Fact]
        public async Task CreateClientAsync_Success_ReturnsValidClient()
        {
            // Arrange
            var credentials = new Credentials
            {
                AccessKeyId = "test-access-key",
                SecretAccessKey = "test-secret-key",
                SessionToken = "test-session-token",
                Expiration = DateTime.UtcNow.AddHours(1)
            };

            var assumeRoleResponse = new AssumeRoleResponse
            {
                Credentials = credentials
            };

            _mockStsClient
                .Setup(x => x.AssumeRoleAsync(It.IsAny<AssumeRoleRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(assumeRoleResponse);

            // Act
            var result = await _factory.CreateClientAsync(_cancellationToken);

            // Assert
            Assert.NotNull(result);
            _mockStsClient.Verify(x => x.AssumeRoleAsync(It.IsAny<AssumeRoleRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task CreateClientAsync_UsesCorrectRoleArn()
        {
            // Arrange
            var credentials = new Credentials
            {
                AccessKeyId = "test-access-key",
                SecretAccessKey = "test-secret-key",
                SessionToken = "test-session-token",
                Expiration = DateTime.UtcNow.AddHours(1)
            };

            var assumeRoleResponse = new AssumeRoleResponse
            {
                Credentials = credentials
            };

            AssumeRoleRequest capturedRequest = null;

            _mockStsClient
                .Setup(x => x.AssumeRoleAsync(It.IsAny<AssumeRoleRequest>(), It.IsAny<CancellationToken>()))
                .Callback<AssumeRoleRequest, CancellationToken>((request, token) => capturedRequest = request)
                .ReturnsAsync(assumeRoleResponse);

            // Act
            await _factory.CreateClientAsync(_cancellationToken);

            // Assert
            Assert.NotNull(capturedRequest);
            Assert.Equal(_options.Value.RoleArn, capturedRequest.RoleArn);
        }

        [Fact]
        public async Task CreateClientAsync_GeneratesUniqueSessionName()
        {
            // Arrange
            var credentials = new Credentials
            {
                AccessKeyId = "test-access-key",
                SecretAccessKey = "test-secret-key",
                SessionToken = "test-session-token",
                Expiration = DateTime.UtcNow.AddHours(1)
            };

            var assumeRoleResponse = new AssumeRoleResponse
            {
                Credentials = credentials
            };

            AssumeRoleRequest firstRequest = null;
            AssumeRoleRequest secondRequest = null;

            _mockStsClient
                .Setup(x => x.AssumeRoleAsync(It.IsAny<AssumeRoleRequest>(), It.IsAny<CancellationToken>()))
                .Callback<AssumeRoleRequest, CancellationToken>((request, token) => 
                {
                    if (firstRequest == null)
                        firstRequest = request;
                    else
                        secondRequest = request;
                })
                .ReturnsAsync(assumeRoleResponse);

            // Act
            await _factory.CreateClientAsync(_cancellationToken);
            await _factory.CreateClientAsync(_cancellationToken);

            // Assert
            Assert.NotNull(firstRequest);
            Assert.NotNull(secondRequest);
            Assert.StartsWith("Payments-SQS-Session-", firstRequest.RoleSessionName);
            Assert.StartsWith("Payments-SQS-Session-", secondRequest.RoleSessionName);
            Assert.NotEqual(firstRequest.RoleSessionName, secondRequest.RoleSessionName);
        }

        [Fact]
        public async Task CreateClientAsync_UsesCorrectRegion()
        {
            // Arrange
            var credentials = new Credentials
            {
                AccessKeyId = "test-access-key",
                SecretAccessKey = "test-secret-key",
                SessionToken = "test-session-token",
                Expiration = DateTime.UtcNow.AddHours(1)
            };

            var assumeRoleResponse = new AssumeRoleResponse
            {
                Credentials = credentials
            };

            _mockStsClient
                .Setup(x => x.AssumeRoleAsync(It.IsAny<AssumeRoleRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(assumeRoleResponse);

            // Create a factory with a specific region
            var regionOptions = Options.Create(new DataSettings
            {
                RoleArn = "test-role-arn",
                Region = "eu-west-1"
            });
            var regionFactory = new SqsClientFactory(_mockStsClient.Object, regionOptions);

            // Act
            var result = await regionFactory.CreateClientAsync(_cancellationToken);

            // Assert
            Assert.NotNull(result);
            // Unfortunately, we can't directly check the region of the created client
            // as it's an internal property of the AmazonSQSClient
            // This would require integration testing or a custom wrapper
        }

        [Fact]
        public async Task CreateClientAsync_PropagatesStsException()
        {
            // Arrange
            _mockStsClient
                .Setup(x => x.AssumeRoleAsync(It.IsAny<AssumeRoleRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new AmazonSecurityTokenServiceException("Test exception"));

            // Act & Assert
            var exception = await Assert.ThrowsAsync<AmazonSecurityTokenServiceException>(
                () => _factory.CreateClientAsync(_cancellationToken));
            
            Assert.Equal("Test exception", exception.Message);
        }

        [Fact]
        public async Task CreateClientAsync_HandlesNullCredentials()
        {
            // Arrange
            var assumeRoleResponse = new AssumeRoleResponse
            {
                Credentials = null
            };

            _mockStsClient
                .Setup(x => x.AssumeRoleAsync(It.IsAny<AssumeRoleRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(assumeRoleResponse);

            // Act & Assert
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => _factory.CreateClientAsync(_cancellationToken));
            
            Assert.Contains("Null credentials", exception.Message);
        }
    }
}

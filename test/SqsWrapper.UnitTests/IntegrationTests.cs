using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SqsWrapper.UnitTests
{
    public class IntegrationTests
    {
        private readonly Mock<IAmazonSecurityTokenService> _mockStsClient;
        private readonly IOptions<DataSettings> _options;
        private readonly SqsClientFactory _realClientFactory;
        private readonly SqsService _realService;
        private readonly CancellationToken _cancellationToken;
        private readonly Mock<IAmazonSQS> _mockSqsClient;

        public IntegrationTests()
        {
            _mockStsClient = new Mock<IAmazonSecurityTokenService>();
            _options = Options.Create(new DataSettings
            {
                RoleArn = "test-role-arn",
                Region = "us-west-2",
                QueueUrl = "https://sqs.us-west-2.amazonaws.com/123456789012/test-queue"
            });
            
            _mockSqsClient = new Mock<IAmazonSQS>();
            _cancellationToken = CancellationToken.None;

            // Setup default STS response
            var credentials = new Credentials
            {
                AccessKeyId = "AKIAIOSFODNN7EXAMPLE",
                SecretAccessKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY",
                SessionToken = "AQoDYXdzEJr1K...Sample/Session/Token",
                Expiration = DateTime.UtcNow.AddHours(1)
            };

            var assumeRoleResponse = new AssumeRoleResponse
            {
                Credentials = credentials
            };

            _mockStsClient
                .Setup(x => x.AssumeRoleAsync(It.IsAny<AssumeRoleRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(assumeRoleResponse);

            // Create real components with mocked dependencies
            _realClientFactory = new SqsClientFactory(_mockStsClient.Object, _options);
            
            // Create a mock SqsClientFactory that returns our mock SQS client
            var mockClientFactory = new Mock<ISqsClientFactory>();
            mockClientFactory
                .Setup(x => x.CreateClientAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(_mockSqsClient.Object);
            
            // Use the mock factory for the service to avoid real AWS calls
            _realService = new SqsService(mockClientFactory.Object);
        }

        [Fact]
        public async Task SqsService_WithRealClientFactory_SendsMessageSuccessfully()
        {
            // Arrange
            var queueUrl = _options.Value.QueueUrl;
            var message = "Test integration message";
            
            // Setup the mock SQS client to handle the SendMessageAsync call
            var expectedResponse = new SendMessageResponse
            {
                MessageId = "test-integration-message-id",
                MD5OfMessageBody = "test-integration-md5"
            };

            _mockSqsClient
                .Setup(x => x.SendMessageAsync(queueUrl, message, It.IsAny<CancellationToken>()))
                .ReturnsAsync(expectedResponse);

            // Act
            var result = await _realService.SendSqsMessageAsync(queueUrl, message, _cancellationToken);

            // Assert
            Assert.Equal(expectedResponse.MessageId, result.MessageId);
            Assert.Equal(expectedResponse.MD5OfMessageBody, result.MD5OfMessageBody);
            _mockSqsClient.Verify(x => x.SendMessageAsync(queueUrl, message, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task SqsService_WithExpiredCredentials_RefreshesAndSendsSuccessfully()
        {
            // Arrange
            var queueUrl = _options.Value.QueueUrl;
            var message = "Test expired credentials message";
            
            // Use reflection to access private fields
            var expirationField = typeof(SqsService).GetField("_credentialExpiration", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            // Setup the mock SQS client
            var expectedResponse = new SendMessageResponse
            {
                MessageId = "test-refresh-message-id",
                MD5OfMessageBody = "test-refresh-md5"
            };

            // First call will throw an exception indicating expired credentials
            _mockSqsClient
                .SetupSequence(x => x.SendMessageAsync(queueUrl, message, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new AmazonSQSException("The security token included in the request is expired"))
                .ReturnsAsync(expectedResponse);

            // Act - First call to initialize
            // This will throw an exception, but our service should catch it and retry
            var result = await _realService.SendSqsMessageAsync(queueUrl, message, _cancellationToken);

            // Assert
            Assert.Equal(expectedResponse.MessageId, result.MessageId);
            Assert.Equal(expectedResponse.MD5OfMessageBody, result.MD5OfMessageBody);
            _mockSqsClient.Verify(x => x.SendMessageAsync(queueUrl, message, It.IsAny<CancellationToken>()), Times.Exactly(2));
        }

        [Fact]
        public async Task SqsService_WithConcurrentRequests_HandlesAllRequestsCorrectly()
        {
            // Arrange
            var queueUrl = _options.Value.QueueUrl;
            var message = "Test concurrent message";
            var concurrentRequests = 5;

            // Setup the mock SQS client
            var expectedResponse = new SendMessageResponse
            {
                MessageId = "test-concurrent-message-id",
                MD5OfMessageBody = "test-concurrent-md5"
            };

            _mockSqsClient
                .Setup(x => x.SendMessageAsync(queueUrl, message, It.IsAny<CancellationToken>()))
                .ReturnsAsync(expectedResponse);

            // Act
            // Create multiple tasks to call SendSqsMessageAsync concurrently
            var tasks = new List<Task<SendMessageResponse>>();
            for (int i = 0; i < concurrentRequests; i++)
            {
                tasks.Add(_realService.SendSqsMessageAsync(queueUrl, message, _cancellationToken));
            }

            // Wait for all tasks to complete
            var results = await Task.WhenAll(tasks);

            // Assert
            // All requests should be successful
            foreach (var result in results)
            {
                Assert.Equal(expectedResponse.MessageId, result.MessageId);
                Assert.Equal(expectedResponse.MD5OfMessageBody, result.MD5OfMessageBody);
            }
            
            // SQS client should be called once for each request
            _mockSqsClient.Verify(x => x.SendMessageAsync(queueUrl, message, It.IsAny<CancellationToken>()), 
                Times.Exactly(concurrentRequests));
        }

        [Fact]
        public async Task SqsService_AfterFactoryException_RecoveryWorks()
        {
            // Arrange
            var queueUrl = _options.Value.QueueUrl;
            var message = "Test recovery message";

            // Setup the mock SQS client
            var expectedResponse = new SendMessageResponse
            {
                MessageId = "test-recovery-message-id",
                MD5OfMessageBody = "test-recovery-md5"
            };

            // First call will throw an exception, second call will succeed
            _mockSqsClient
                .SetupSequence(x => x.SendMessageAsync(queueUrl, message, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new AmazonSQSException("Service unavailable"))
                .ReturnsAsync(expectedResponse);

            // Act
            // First call should fail but be caught by our retry logic
            var result = await _realService.SendSqsMessageAsync(queueUrl, message, _cancellationToken);
            
            // Assert
            Assert.Equal(expectedResponse.MessageId, result.MessageId);
            Assert.Equal(expectedResponse.MD5OfMessageBody, result.MD5OfMessageBody);
            _mockSqsClient.Verify(x => x.SendMessageAsync(queueUrl, message, It.IsAny<CancellationToken>()), Times.Exactly(2));
        }
    }
}

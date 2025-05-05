using Amazon.SQS;
using Amazon.SQS.Model;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SqsWrapper.UnitTests
{
    public class SqsServiceTests
    {
        private readonly Mock<ISqsClientFactory> _mockClientFactory;
        private readonly Mock<IAmazonSQS> _mockSqsClient;
        private readonly CancellationToken _cancellationToken;

        public SqsServiceTests()
        {
            _mockClientFactory = new Mock<ISqsClientFactory>();
            _mockSqsClient = new Mock<IAmazonSQS>();
            _cancellationToken = CancellationToken.None;

            // Default setup for the client factory
            _mockClientFactory
                .Setup(x => x.CreateClientAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(_mockSqsClient.Object);
        }

        [Fact]
        public void Constructor_NullClientFactory_ThrowsArgumentNullException()
        {
            // Act & Assert
            var exception = Assert.Throws<ArgumentNullException>(() => new SqsService(null));
            Assert.Equal("clientFactory", exception.ParamName);
        }

        [Fact]
        public async Task GetSqsClientAsync_CreatesNewClientOnFirstCall()
        {
            // Arrange
            var service = new SqsService(_mockClientFactory.Object);

            // Act
            // Use reflection to access the private method
            var getSqsClientMethod = typeof(SqsService).GetMethod("GetSqsClientAsync", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            var client = await (Task<IAmazonSQS>)getSqsClientMethod.Invoke(service, new object[] { _cancellationToken });

            // Assert
            Assert.Same(_mockSqsClient.Object, client);
            _mockClientFactory.Verify(x => x.CreateClientAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task GetSqsClientAsync_ReusesSameClientWithinExpirationWindow()
        {
            // Arrange
            var service = new SqsService(_mockClientFactory.Object);
            var getSqsClientMethod = typeof(SqsService).GetMethod("GetSqsClientAsync", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            // Act
            // Call twice
            var client1 = await (Task<IAmazonSQS>)getSqsClientMethod.Invoke(service, new object[] { _cancellationToken });
            var client2 = await (Task<IAmazonSQS>)getSqsClientMethod.Invoke(service, new object[] { _cancellationToken });

            // Assert
            Assert.Same(client1, client2);
            _mockClientFactory.Verify(x => x.CreateClientAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task GetSqsClientAsync_RefreshesClientAfterExpiration()
        {
            // Arrange
            var service = new SqsService(_mockClientFactory.Object);
            
            // Use reflection to access private fields and methods
            var getSqsClientMethod = typeof(SqsService).GetMethod("GetSqsClientAsync", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            var expirationField = typeof(SqsService).GetField("_credentialExpiration", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            // Act
            // First call to initialize
            var client1 = await (Task<IAmazonSQS>)getSqsClientMethod.Invoke(service, new object[] { _cancellationToken });
            
            // Set expiration to the past
            expirationField.SetValue(service, DateTime.UtcNow.AddMinutes(-5));
            
            // Second call should refresh
            var client2 = await (Task<IAmazonSQS>)getSqsClientMethod.Invoke(service, new object[] { _cancellationToken });

            // Assert
            _mockClientFactory.Verify(x => x.CreateClientAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        }

        [Fact]
        public async Task SendSqsMessageAsync_Success()
        {
            // Arrange
            var service = new SqsService(_mockClientFactory.Object);
            var queueUrl = "https://sqs.us-west-2.amazonaws.com/123456789012/test-queue";
            var message = "Test message";
            
            var expectedResponse = new SendMessageResponse
            {
                MessageId = "test-message-id",
                MD5OfMessageBody = "test-md5"
            };

            _mockSqsClient
                .Setup(x => x.SendMessageAsync(queueUrl, message, It.IsAny<CancellationToken>()))
                .ReturnsAsync(expectedResponse);

            // Act
            var result = await service.SendSqsMessageAsync(queueUrl, message, _cancellationToken);

            // Assert
            Assert.Equal(expectedResponse.MessageId, result.MessageId);
            Assert.Equal(expectedResponse.MD5OfMessageBody, result.MD5OfMessageBody);
            _mockSqsClient.Verify(x => x.SendMessageAsync(queueUrl, message, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task SendSqsMessageAsync_PropagatesExceptions()
        {
            // Arrange
            var service = new SqsService(_mockClientFactory.Object);
            var queueUrl = "https://sqs.us-west-2.amazonaws.com/123456789012/test-queue";
            var message = "Test message";
            
            _mockSqsClient
                .Setup(x => x.SendMessageAsync(queueUrl, message, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new AmazonSQSException("Test exception"));

            // Act & Assert
            var exception = await Assert.ThrowsAsync<AmazonSQSException>(
                () => service.SendSqsMessageAsync(queueUrl, message, _cancellationToken));
            
            Assert.Equal("Test exception", exception.Message);
        }

        [Fact]
        public async Task GetSqsClientAsync_ThreadSafety()
        {
            // Arrange
            var service = new SqsService(_mockClientFactory.Object);
            var getSqsClientMethod = typeof(SqsService).GetMethod("GetSqsClientAsync", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            // Setup factory to return a new mock client for each call
            var mockClients = new List<Mock<IAmazonSQS>>();
            _mockClientFactory
                .Setup(x => x.CreateClientAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => 
                {
                    var mockClient = new Mock<IAmazonSQS>();
                    mockClients.Add(mockClient);
                    return mockClient.Object;
                });

            // Act
            // Create multiple tasks to call GetSqsClientAsync concurrently
            var tasks = new List<Task<IAmazonSQS>>();
            for (int i = 0; i < 10; i++)
            {
                tasks.Add((Task<IAmazonSQS>)getSqsClientMethod.Invoke(service, new object[] { _cancellationToken }));
            }

            // Wait for all tasks to complete
            var clients = await Task.WhenAll(tasks);

            // Assert
            // All tasks should get the same client instance (the first one created)
            var firstClient = clients[0];
            foreach (var client in clients)
            {
                Assert.Same(firstClient, client);
            }
            
            // Factory should be called exactly once despite multiple concurrent requests
            _mockClientFactory.Verify(x => x.CreateClientAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task SendSqsMessageAsync_RefreshesExpiredClient()
        {
            // Arrange
            var service = new SqsService(_mockClientFactory.Object);
            var queueUrl = "https://sqs.us-west-2.amazonaws.com/123456789012/test-queue";
            var message = "Test message";
            
            // Use reflection to access private field
            var expirationField = typeof(SqsService).GetField("_credentialExpiration", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            // First call to initialize
            await service.SendSqsMessageAsync(queueUrl, message, _cancellationToken);
            
            // Set expiration to the past
            expirationField.SetValue(service, DateTime.UtcNow.AddMinutes(-5));
            
            // Second call should refresh
            await service.SendSqsMessageAsync(queueUrl, message, _cancellationToken);

            // Assert
            _mockClientFactory.Verify(x => x.CreateClientAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        }
    }
}

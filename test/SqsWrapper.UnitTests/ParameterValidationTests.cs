using Amazon.SQS;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace SqsWrapper.UnitTests
{
    public class ParameterValidationTests
    {
        private readonly Mock<ISqsClientFactory> _mockClientFactory;
        private readonly Mock<IAmazonSQS> _mockSqsClient;
        private readonly CancellationToken _cancellationToken;
        private readonly SqsService _service;

        public ParameterValidationTests()
        {
            _mockClientFactory = new Mock<ISqsClientFactory>();
            _mockSqsClient = new Mock<IAmazonSQS>();
            _cancellationToken = CancellationToken.None;

            // Default setup for the client factory
            _mockClientFactory
                .Setup(x => x.CreateClientAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(_mockSqsClient.Object);

            _service = new SqsService(_mockClientFactory.Object);
        }

        [Fact]
        public async Task SendSqsMessageAsync_NullQueueUrl_ThrowsArgumentException()
        {
            // Arrange
            string queueUrl = null;
            string message = "Test message";

            // Act & Assert
            var exception = await Assert.ThrowsAsync<ArgumentException>(
                () => _service.SendSqsMessageAsync(queueUrl, message, _cancellationToken));
            
            Assert.Equal("queueUrl", exception.ParamName);
        }

        [Fact]
        public async Task SendSqsMessageAsync_EmptyQueueUrl_ThrowsArgumentException()
        {
            // Arrange
            string queueUrl = string.Empty;
            string message = "Test message";

            // Act & Assert
            var exception = await Assert.ThrowsAsync<ArgumentException>(
                () => _service.SendSqsMessageAsync(queueUrl, message, _cancellationToken));
            
            Assert.Equal("queueUrl", exception.ParamName);
        }

        [Fact]
        public async Task SendSqsMessageAsync_NullMessage_ThrowsArgumentException()
        {
            // Arrange
            string queueUrl = "https://sqs.us-west-2.amazonaws.com/123456789012/test-queue";
            string message = null;

            // Act & Assert
            var exception = await Assert.ThrowsAsync<ArgumentException>(
                () => _service.SendSqsMessageAsync(queueUrl, message, _cancellationToken));
            
            Assert.Equal("message", exception.ParamName);
        }

        [Fact]
        public async Task SendSqsMessageAsync_EmptyMessage_ThrowsArgumentException()
        {
            // Arrange
            string queueUrl = "https://sqs.us-west-2.amazonaws.com/123456789012/test-queue";
            string message = string.Empty;

            // Act & Assert
            var exception = await Assert.ThrowsAsync<ArgumentException>(
                () => _service.SendSqsMessageAsync(queueUrl, message, _cancellationToken));
            
            Assert.Equal("message", exception.ParamName);
        }

        [Fact]
        public async Task SendSqsMessageAsync_ValidParameters_CallsClient()
        {
            // Arrange
            string queueUrl = "https://sqs.us-west-2.amazonaws.com/123456789012/test-queue";
            string message = "Test message";

            // Act
            await _service.SendSqsMessageAsync(queueUrl, message, _cancellationToken);

            // Assert
            _mockSqsClient.Verify(x => x.SendMessageAsync(queueUrl, message, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task IsCredentialExpiredException_WithExpiredTokenMessage_ReturnsTrue()
        {
            // Arrange
            var exception = new AmazonSQSException("The security token included in the request is expired");
            
            // Use reflection to access the private method
            var method = typeof(SqsService).GetMethod("IsCredentialExpiredException", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            // Act
            var result = (bool)method.Invoke(_service, new object[] { exception });

            // Assert
            Assert.True(result);
        }

        [Fact]
        public async Task IsCredentialExpiredException_WithInvalidTokenMessage_ReturnsTrue()
        {
            // Arrange
            var exception = new AmazonSQSException("The security token included in the request is invalid");
            
            // Use reflection to access the private method
            var method = typeof(SqsService).GetMethod("IsCredentialExpiredException", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            // Act
            var result = (bool)method.Invoke(_service, new object[] { exception });

            // Assert
            Assert.True(result);
        }

        [Fact]
        public async Task IsCredentialExpiredException_WithOtherMessage_ReturnsFalse()
        {
            // Arrange
            var exception = new AmazonSQSException("Some other error message");
            
            // Use reflection to access the private method
            var method = typeof(SqsService).GetMethod("IsCredentialExpiredException", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            // Act
            var result = (bool)method.Invoke(_service, new object[] { exception });

            // Assert
            Assert.False(result);
        }

        [Fact]
        public async Task ForceCredentialRefreshAsync_RefreshesClient()
        {
            // Arrange
            // Use reflection to access private fields and methods
            var forceRefreshMethod = typeof(SqsService).GetMethod("ForceCredentialRefreshAsync", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            var expirationField = typeof(SqsService).GetField("_credentialExpiration", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            // Set expiration to a future time
            expirationField.SetValue(_service, DateTime.UtcNow.AddHours(1));

            // Act
            await (Task)forceRefreshMethod.Invoke(_service, new object[] { _cancellationToken });

            // Assert
            // Factory should be called to create a new client
            _mockClientFactory.Verify(x => x.CreateClientAsync(It.IsAny<CancellationToken>()), Times.Once);
            
            // Expiration should be updated
            var newExpiration = (DateTime)expirationField.GetValue(_service);
            Assert.True(newExpiration > DateTime.UtcNow);
        }
    }
}

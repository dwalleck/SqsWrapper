using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Options;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace SqsWrapper.UnitTests
{
    public class CornerCaseTests
    {
        private readonly Mock<ISqsClientFactory> _mockClientFactory;
        private readonly Mock<IAmazonSQS> _mockSqsClient;
        private readonly CancellationToken _cancellationToken;

        public CornerCaseTests()
        {
            _mockClientFactory = new Mock<ISqsClientFactory>();
            _mockSqsClient = new Mock<IAmazonSQS>();
            _cancellationToken = CancellationToken.None;
        }

        [Fact]
        public async Task SqsService_WhenFactoryThrowsException_PropagatesException()
        {
            // Arrange
            _mockClientFactory
                .Setup(x => x.CreateClientAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Factory failed"));

            var service = new SqsService(_mockClientFactory.Object);
            var queueUrl = "https://sqs.us-west-2.amazonaws.com/123456789012/test-queue";
            var message = "Test message";

            // Act & Assert
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.SendSqsMessageAsync(queueUrl, message, _cancellationToken));
            
            Assert.Equal("Factory failed", exception.Message);
        }

        [Fact]
        public async Task SqsService_WhenSqsClientThrowsException_PropagatesException()
        {
            // Arrange
            _mockClientFactory
                .Setup(x => x.CreateClientAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(_mockSqsClient.Object);

            var service = new SqsService(_mockClientFactory.Object);
            var queueUrl = "https://sqs.us-west-2.amazonaws.com/123456789012/test-queue";
            var message = "Test message";

            _mockSqsClient
                .Setup(x => x.SendMessageAsync(queueUrl, message, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new AmazonSQSException("SQS service unavailable"));

            // Act & Assert
            var exception = await Assert.ThrowsAsync<AmazonSQSException>(
                () => service.SendSqsMessageAsync(queueUrl, message, _cancellationToken));
            
            Assert.Equal("SQS service unavailable", exception.Message);
        }

        [Fact]
        public async Task SqsService_WhenCredentialsExpireDuringOperation_RefreshesAndRetries()
        {
            // Arrange
            var queueUrl = "https://sqs.us-west-2.amazonaws.com/123456789012/test-queue";
            var message = "Test message";
            
            // First client will throw an exception indicating expired credentials
            var expiredClient = new Mock<IAmazonSQS>();
            expiredClient
                .Setup(x => x.SendMessageAsync(queueUrl, message, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new AmazonSQSException("The security token included in the request is expired"));

            // Second client will succeed
            var validClient = new Mock<IAmazonSQS>();
            var expectedResponse = new SendMessageResponse
            {
                MessageId = "test-message-id",
                MD5OfMessageBody = "test-md5"
            };
            validClient
                .Setup(x => x.SendMessageAsync(queueUrl, message, It.IsAny<CancellationToken>()))
                .ReturnsAsync(expectedResponse);

            // Setup factory to return expired client first, then valid client
            var callCount = 0;
            _mockClientFactory
                .Setup(x => x.CreateClientAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => 
                {
                    callCount++;
                    return callCount == 1 ? expiredClient.Object : validClient.Object;
                });

            var service = new SqsService(_mockClientFactory.Object);

            // Act
            // We need to modify the implementation of SqsService to handle this case
            // For now, we'll just verify the current behavior
            
            // First call will initialize with the expired client
            await service.SendSqsMessageAsync(queueUrl, message, _cancellationToken);
            
            // Assert
            // Factory should be called twice - once for initial client, once for refresh
            _mockClientFactory.Verify(x => x.CreateClientAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
            
            // Both clients should be used
            expiredClient.Verify(x => x.SendMessageAsync(queueUrl, message, It.IsAny<CancellationToken>()), Times.Once);
            validClient.Verify(x => x.SendMessageAsync(queueUrl, message, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task SqsClientFactory_WithNullStsClient_ThrowsArgumentNullException()
        {
            // Arrange & Act & Assert
            var options = Options.Create(new DataSettings
            {
                RoleArn = "test-role-arn",
                Region = "us-west-2"
            });

            var exception = Assert.Throws<ArgumentNullException>(
                () => new SqsClientFactory(null, options));
            
            Assert.Equal("stsClient", exception.ParamName);
        }

        [Fact]
        public async Task SqsClientFactory_WithNullOptions_ThrowsArgumentNullException()
        {
            // Arrange
            var mockStsClient = new Mock<IAmazonSecurityTokenService>();

            // Act & Assert
            var exception = Assert.Throws<ArgumentNullException>(
                () => new SqsClientFactory(mockStsClient.Object, null));
            
            Assert.Equal("roleOptions", exception.ParamName);
        }

        [Fact]
        public async Task SqsClientFactory_WithEmptyRoleArn_ThrowsArgumentException()
        {
            // Arrange
            var mockStsClient = new Mock<IAmazonSecurityTokenService>();
            var options = Options.Create(new DataSettings
            {
                RoleArn = string.Empty,
                Region = "us-west-2"
            });

            var factory = new SqsClientFactory(mockStsClient.Object, options);

            // Act & Assert
            var exception = await Assert.ThrowsAsync<ArgumentException>(
                () => factory.CreateClientAsync(_cancellationToken));
            
            Assert.Contains("RoleArn", exception.Message);
        }

        [Fact]
        public async Task SqsClientFactory_WithEmptyRegion_ThrowsArgumentException()
        {
            // Arrange
            var mockStsClient = new Mock<IAmazonSecurityTokenService>();
            var options = Options.Create(new DataSettings
            {
                RoleArn = "test-role-arn",
                Region = string.Empty
            });

            var factory = new SqsClientFactory(mockStsClient.Object, options);

            // Act & Assert
            var exception = await Assert.ThrowsAsync<ArgumentException>(
                () => factory.CreateClientAsync(_cancellationToken));
            
            Assert.Contains("Region", exception.Message);
        }

        [Fact]
        public async Task SqsService_WithCancellationToken_PropagatesCancellation()
        {
            // Arrange
            _mockClientFactory
                .Setup(x => x.CreateClientAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(_mockSqsClient.Object);

            var service = new SqsService(_mockClientFactory.Object);
            var queueUrl = "https://sqs.us-west-2.amazonaws.com/123456789012/test-queue";
            var message = "Test message";

            // Create a cancellation token that's already canceled
            var cts = new CancellationTokenSource();
            cts.Cancel();

            // Act & Assert
            // TaskCanceledException derives from OperationCanceledException, so we need to check for either
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.SendSqsMessageAsync(queueUrl, message, cts.Token));
            
            // Verify it's the right type of cancellation
            Assert.True(exception is TaskCanceledException || exception is OperationCanceledException);
        }
    }
}

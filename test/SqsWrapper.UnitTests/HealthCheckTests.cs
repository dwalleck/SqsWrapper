using Amazon.SQS;
using Microsoft.Extensions.Logging;
using Moq;
using Polly.CircuitBreaker;
using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SqsWrapper.UnitTests
{
    public class HealthCheckTests
    {
        private readonly Mock<ISqsClientFactory> _mockClientFactory;
        private readonly Mock<IAmazonSQS> _mockSqsClient;
        private readonly CancellationToken _cancellationToken;
        private readonly Mock<ILogger<SqsService>> _mockLogger;

        public HealthCheckTests()
        {
            _mockClientFactory = new Mock<ISqsClientFactory>();
            _mockSqsClient = new Mock<IAmazonSQS>();
            _cancellationToken = CancellationToken.None;
            _mockLogger = new Mock<ILogger<SqsService>>();

            // Default setup for the client factory
            _mockClientFactory
                .Setup(x => x.CreateClientAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(_mockSqsClient.Object);
        }

        [Fact]
        public async Task IsHealthyAsync_WhenClientCreationSucceeds_ReturnsTrue()
        {
            // Arrange
            var service = new SqsService(_mockClientFactory.Object, _mockLogger.Object);

            // Act
            var result = await service.IsHealthyAsync(_cancellationToken);

            // Assert
            Assert.True(result);
            _mockClientFactory.Verify(x => x.CreateClientAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task IsHealthyAsync_WhenClientCreationFails_ReturnsFalse()
        {
            // Arrange
            _mockClientFactory
                .Setup(x => x.CreateClientAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Failed to create client"));

            var service = new SqsService(_mockClientFactory.Object, _mockLogger.Object);

            // Act
            var result = await service.IsHealthyAsync(_cancellationToken);

            // Assert
            Assert.False(result);
            _mockClientFactory.Verify(x => x.CreateClientAsync(It.IsAny<CancellationToken>()), Times.Once);
            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => true),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception, string>>()),
                Times.Once);
        }

        [Fact]
        public async Task IsHealthyAsync_WhenCircuitBreakerIsOpen_ReturnsFalse()
        {
            // Arrange
            var service = new SqsService(_mockClientFactory.Object, _mockLogger.Object);
            
            // Use reflection to set the circuit state to Open
            var circuitStateField = typeof(SqsService).GetField("_circuitState", 
                BindingFlags.NonPublic | BindingFlags.Instance);
            circuitStateField.SetValue(service, CircuitState.Open);

            // Act
            var result = await service.IsHealthyAsync(_cancellationToken);

            // Assert
            Assert.False(result);
        }

        [Fact]
        public async Task IsHealthyAsync_WhenCircuitBreakerIsHalfOpen_ReturnsTrue()
        {
            // Arrange
            var service = new SqsService(_mockClientFactory.Object, _mockLogger.Object);
            
            // Use reflection to set the circuit state to HalfOpen
            var circuitStateField = typeof(SqsService).GetField("_circuitState", 
                BindingFlags.NonPublic | BindingFlags.Instance);
            circuitStateField.SetValue(service, CircuitState.HalfOpen);

            // Act
            var result = await service.IsHealthyAsync(_cancellationToken);

            // Assert
            Assert.True(result);
        }

        [Fact]
        public async Task IsHealthyAsync_WithCancellationToken_PropagatesCancellation()
        {
            // Arrange
            var service = new SqsService(_mockClientFactory.Object, _mockLogger.Object);
            
            // Create a cancellation token that's already canceled
            var cts = new CancellationTokenSource();
            cts.Cancel();

            // Act & Assert
            var result = await service.IsHealthyAsync(cts.Token);
            
            // Should return false when canceled
            Assert.False(result);
        }
    }
}

using System;

namespace SqsWrapper;

/// <summary>
/// Configuration settings for the SQS wrapper.
/// </summary>
public class DataSettings
{
    /// <summary>
    /// Gets or sets the Amazon Resource Name (ARN) of the role to assume.
    /// </summary>
    /// <remarks>
    /// This role must have permissions to send messages to the SQS queue.
    /// </remarks>
    public string RoleArn { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the AWS region where the SQS queue is located.
    /// </summary>
    /// <remarks>
    /// Example values: "us-east-1", "us-west-2", "eu-west-1", etc.
    /// </remarks>
    public string Region { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the URL of the SQS queue.
    /// </summary>
    /// <remarks>
    /// Example format: "https://sqs.us-west-2.amazonaws.com/123456789012/my-queue"
    /// </remarks>
    public string QueueUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a default message to send to the queue.
    /// </summary>
    /// <remarks>
    /// This is optional and can be overridden when calling SendSqsMessageAsync.
    /// </remarks>
    public string Message { get; set; } = string.Empty;
}

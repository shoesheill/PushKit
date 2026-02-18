namespace PushKit.Sample.Models;

public sealed record DataToTokenRequest(string DeviceToken, string Event, string JsonPayload);
public sealed record NotifyTokenRequest(string DeviceToken, string Event, string Title, string Body, string? ImageUrl = null, string? DeepLink = null);
public sealed record TopicRequest(string Topic, string Event, string? Title = null, string? Body = null);
public sealed record ConditionRequest(string Condition, string Event);
public sealed record BatchRequest(List<string> DeviceTokens, string Event, string JsonPayload);

public sealed record ApnAlertRequest(string DeviceToken, string Event, string Title, string Body, int? Badge = null, string? DeepLink = null, string? CollapseId = null);
public sealed record ApnDataRequest(string DeviceToken, string Event, string JsonPayload);
public sealed record ApnBatchRequest(List<string> DeviceTokens, string Event, string Title, string Body);

public sealed record OrderShippedRequest(string? FcmToken, string? ApnToken, string OrderId, string TrackingNumber);

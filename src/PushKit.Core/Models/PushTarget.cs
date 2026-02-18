namespace PushKit.Models;

/// <summary>Where a push message should be delivered.</summary>
public enum TargetType { Token, Topic, Condition }

/// <summary>
/// Immutable value object describing where to deliver a push message.
/// Use the static factory methods for clean creation.
/// </summary>
public sealed record PushTarget(TargetType Type, string Value)
{
    /// <summary>Send to a specific FCM device registration token.</summary>
    public static PushTarget Token(string token) => new(TargetType.Token, token);

    /// <summary>Send to all devices subscribed to the given FCM topic.</summary>
    public static PushTarget Topic(string topic) => new(TargetType.Topic, topic);

    /// <summary>
    /// Send using a boolean topic condition, e.g.
    /// <c>"'sports' in topics &amp;&amp; 'news' in topics"</c>
    /// </summary>
    public static PushTarget Condition(string condition) => new(TargetType.Condition, condition);

    /// <summary>Returns a log-safe version: device tokens are partially masked.</summary>
    public string Masked() =>
        Type == TargetType.Token && Value.Length > 12
            ? $"{Value[..6]}…{Value[^6..]}"
            : Value;
}

using FluentAssertions;
using PushKit.Models;
using Xunit;

namespace PushKit.Tests;

public class PushResultTests
{
    [Theory]
    [InlineData("UNREGISTERED",      true)]
    [InlineData("INVALID_ARGUMENT",  true)]
    [InlineData("BadDeviceToken",    true)]
    [InlineData("Unregistered",      true)]
    [InlineData("DeviceTokenNotForTopic", true)]
    [InlineData("UNAVAILABLE",       false)]
    [InlineData("INTERNAL",          false)]
    public void IsTokenInvalid_CorrectlyClassifiesErrorCodes(string code, bool expected)
    {
        var result = PushResult.Failure(PushTarget.Token("abc"), code, "error");
        result.IsTokenInvalid.Should().Be(expected);
    }

    [Theory]
    [InlineData("UNAVAILABLE",         true)]
    [InlineData("INTERNAL",            true)]
    [InlineData("QUOTA_EXCEEDED",      true)]
    [InlineData("TooManyRequests",     true)]
    [InlineData("InternalServerError", true)]
    [InlineData("ServiceUnavailable",  true)]
    [InlineData("UNREGISTERED",        false)]
    [InlineData("BadDeviceToken",      false)]
    public void IsRetryable_CorrectlyClassifiesErrorCodes(string code, bool expected)
    {
        var result = PushResult.Failure(PushTarget.Token("abc"), code, "error");
        result.IsRetryable.Should().Be(expected);
    }

    [Fact]
    public void Success_Result_HasCorrectProperties()
    {
        var target = PushTarget.Token("my-token");
        var result = PushResult.Success(target, "projects/app/messages/123", 200);

        result.IsSuccess.Should().BeTrue();
        result.MessageId.Should().Be("projects/app/messages/123");
        result.HttpStatus.Should().Be(200);
        result.ErrorCode.Should().BeNull();
    }

    [Fact]
    public void Failure_Result_HasCorrectProperties()
    {
        var target = PushTarget.Topic("news");
        var result = PushResult.Failure(target, "UNAVAILABLE", "Server down", 503);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("UNAVAILABLE");
        result.ErrorMessage.Should().Be("Server down");
        result.HttpStatus.Should().Be(503);
    }
}

public class BatchPushResultTests
{
    private static PushResult Ok(string token)  => PushResult.Success(PushTarget.Token(token), "msg-1");
    private static PushResult Bad(string token, string code) =>
        PushResult.Failure(PushTarget.Token(token), code, "error");

    [Fact]
    public void Counts_AreCorrect()
    {
        var batch = new BatchPushResult
        {
            Results = [Ok("t1"), Ok("t2"), Bad("t3", "UNREGISTERED"), Bad("t4", "UNAVAILABLE")]
        };

        batch.TotalCount.Should().Be(4);
        batch.SuccessCount.Should().Be(2);
        batch.FailureCount.Should().Be(2);
        batch.IsFullSuccess.Should().BeFalse();
        batch.IsPartialSuccess.Should().BeTrue();
        batch.IsFullFailure.Should().BeFalse();
    }

    [Fact]
    public void InvalidTokens_ReturnsOnlyInvalidOnes()
    {
        var batch = new BatchPushResult
        {
            Results =
            [
                Bad("t1", "UNREGISTERED"),
                Bad("t2", "UNAVAILABLE"),
                Bad("t3", "BadDeviceToken"),
                Ok("t4")
            ]
        };

        batch.InvalidTokens.Should().BeEquivalentTo(["t1", "t3"]);
    }

    [Fact]
    public void RetryableTokens_ReturnsOnlyRetryableOnes()
    {
        var batch = new BatchPushResult
        {
            Results =
            [
                Bad("t1", "UNAVAILABLE"),
                Bad("t2", "UNREGISTERED"),
                Bad("t3", "INTERNAL"),
            ]
        };

        batch.RetryableTokens.Should().BeEquivalentTo(["t1", "t3"]);
    }

    [Fact]
    public void IsFullFailure_WhenAllFail()
    {
        var batch = new BatchPushResult
        {
            Results = [Bad("t1", "UNREGISTERED"), Bad("t2", "INTERNAL")]
        };
        batch.IsFullFailure.Should().BeTrue();
        batch.IsPartialSuccess.Should().BeFalse();
    }
}

public class PushTargetTests
{
    [Fact]
    public void Masked_ShortensDeviceToken()
    {
        var target = PushTarget.Token("abc123456789xyz");
        target.Masked().Should().Contain("…");
        target.Masked().Should().NotBe(target.Value);
    }

    [Fact]
    public void Masked_LeavesTopicUnchanged()
    {
        var target = PushTarget.Topic("news");
        target.Masked().Should().Be("news");
    }

    [Fact]
    public void Masked_LeavesConditionUnchanged()
    {
        var cond = "'a' in topics";
        var target = PushTarget.Condition(cond);
        target.Masked().Should().Be(cond);
    }
}

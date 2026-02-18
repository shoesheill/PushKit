using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PushKit.Builders;
using PushKit.Configuration;
using PushKit.Interfaces;
using PushKit.Models;
using PushKit.Services;
using Xunit;

namespace PushKit.Tests;

/// <summary>
/// Tests FcmSender in isolation using a custom HttpMessageHandler.
/// No real FCM calls are made.
/// </summary>
public class FcmSenderTests
{
    private static FcmSender CreateSender(HttpResponseMessage httpResponse,string projectId = "test-project",string fakeToken = "fake-access-token")
    {
        var handler = new FakeHttpHandler(httpResponse);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://fcm.googleapis.com") };

        var tokenProviderMock = new Mock<IFcmTokenProvider>();
        tokenProviderMock
            .Setup(p => p.GetAccessTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(fakeToken);

        var options = Options.Create(new FcmOptions
        {
            ProjectId = projectId,
            MaxRetryAttempts = 0, // No retry in unit tests
            BatchParallelism = 10
        });

        return new FcmSender(httpClient, tokenProviderMock.Object, options,
            NullLogger<FcmSender>.Instance);
    }

    [Fact]
    public async Task SendToTokenAsync_SuccessResponse_ReturnsPushResultSuccess()
    {
        var fcmResponse = new { name = "projects/test-project/messages/msg-001" };
        var httpResp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(fcmResponse), Encoding.UTF8, "application/json")
        };

        var sender = CreateSender(httpResp);
        var msg = PushMessageBuilder.Create().WithData("event", "TEST").Build();

        var result = await sender.SendToTokenAsync("device-token-xyz", msg);

        result.IsSuccess.Should().BeTrue();
        result.MessageId.Should().Be("projects/test-project/messages/msg-001");
        result.HttpStatus.Should().Be(200);
    }

    [Fact]
    public async Task SendToTokenAsync_UnregisteredError_ReturnsFailureWithIsTokenInvalid()
    {
        var errorBody = """
            {
              "error": {
                "code": 404,
                "message": "Requested entity was not found.",
                "status": "NOT_FOUND",
                "details": [{ "@type": "type.googleapis.com/google.firebase.fcm.v1.FcmError", "errorCode": "UNREGISTERED" }]
              }
            }
            """;

        var httpResp = new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(errorBody, Encoding.UTF8, "application/json")
        };

        var sender = CreateSender(httpResp);
        var msg = PushMessageBuilder.Create().WithData("event", "TEST").Build();

        var result = await sender.SendToTokenAsync("stale-token", msg);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("UNREGISTERED");
        result.IsTokenInvalid.Should().BeTrue();
        result.IsRetryable.Should().BeFalse();
    }

    [Fact]
    public async Task SendToTopicAsync_Succeeds()
    {
        var httpResp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"name\":\"msg-topic-1\"}", Encoding.UTF8, "application/json")
        };

        var sender = CreateSender(httpResp);
        var msg = PushMessageBuilder.Create().WithData("x", "y").Build();

        var result = await sender.SendToTopicAsync("breaking-news", msg);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task SendToConditionAsync_Succeeds()
    {
        var httpResp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"name\":\"msg-cond-1\"}", Encoding.UTF8, "application/json")
        };

        var sender = CreateSender(httpResp);
        var msg = PushMessageBuilder.Create().WithData("x", "y").Build();

        var result = await sender.SendToConditionAsync("'sports' in topics", msg);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task SendBatchAsync_MultipleTokens_AllSucceed()
    {
        var httpResp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"name\":\"msg-1\"}", Encoding.UTF8, "application/json")
        };

        // FakeHttpHandler returns the same response for each call
        var sender = CreateSender(httpResp);
        var msg = PushMessageBuilder.Create().WithData("event", "BULK").Build();
        var tokens = Enumerable.Range(1, 5).Select(i => $"token-{i}").ToList();

        var batch = await sender.SendBatchAsync(tokens, msg);

        batch.TotalCount.Should().Be(5);
        batch.SuccessCount.Should().Be(5);
        batch.FailureCount.Should().Be(0);
        batch.IsFullSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task SendBatchAsync_EmptyTokenList_ReturnsEmptyBatch()
    {
        var sender = CreateSender(new HttpResponseMessage(HttpStatusCode.OK));
        var msg = PushMessageBuilder.Create().WithData("x", "y").Build();

        var batch = await sender.SendBatchAsync([], msg);

        batch.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task SendBatchAsync_DeduplicatesTokens()
    {
        var responses = new Queue<HttpResponseMessage>();
        // Enqueue 2 responses (we expect dedup to reduce 3 tokens to 2)
        responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"name\":\"m1\"}", Encoding.UTF8, "application/json") });
        responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"name\":\"m2\"}", Encoding.UTF8, "application/json") });

        var handler = new QueuedHttpHandler(responses);
        var httpClient = new HttpClient(handler);
        var tokenMock = new Mock<IFcmTokenProvider>();
        tokenMock.Setup(p => p.GetAccessTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync("tok");
        var options = Options.Create(new FcmOptions { ProjectId = "p", MaxRetryAttempts = 0, BatchParallelism = 10 });
        var sender = new FcmSender(httpClient, tokenMock.Object, options, NullLogger<FcmSender>.Instance);

        var msg = PushMessageBuilder.Create().WithData("x", "y").Build();
        var batch = await sender.SendBatchAsync(["tok-a", "tok-b", "tok-a"], msg); // tok-a duplicated

        batch.TotalCount.Should().Be(2); // deduped
    }

    [Fact]
    public async Task SendBatchAsync_InvalidTokens_ReturnsInvalidTokensList()
    {
        // First call ok, second call UNREGISTERED
        var responses = new Queue<HttpResponseMessage>(
        [
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"name\":\"m1\"}", Encoding.UTF8, "application/json")
            },
            new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(
                    "{\"error\":{\"status\":\"NOT_FOUND\",\"details\":[{\"errorCode\":\"UNREGISTERED\"}]}}",
                    Encoding.UTF8, "application/json")
            }
        ]);

        var handler = new QueuedHttpHandler(responses);
        var httpClient = new HttpClient(handler);
        var tokenMock = new Mock<IFcmTokenProvider>();
        tokenMock.Setup(p => p.GetAccessTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync("tok");
        var options = Options.Create(new FcmOptions { ProjectId = "p", MaxRetryAttempts = 0, BatchParallelism = 2 });
        var sender = new FcmSender(httpClient, tokenMock.Object, options, NullLogger<FcmSender>.Instance);

        var msg = PushMessageBuilder.Create().WithData("x", "y").Build();
        var batch = await sender.SendBatchAsync(["good-token", "bad-token"], msg);

        batch.InvalidTokens.Should().ContainSingle().Which.Should().Be("bad-token");
    }

    // ─── Fake HTTP Handlers ───────────────────────────────────────────────────

    private sealed class FakeHttpHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }

    private sealed class QueuedHttpHandler(Queue<HttpResponseMessage> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responses.Dequeue());
    }
}

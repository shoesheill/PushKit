using FluentAssertions;
using PushKit.Builders;
using PushKit.Models;
using Xunit;

namespace PushKit.Tests;

public class PushMessageBuilderTests
{
    [Fact]
    public void Build_WithDataOnly_Succeeds()
    {
        var msg = PushMessageBuilder.Create()
            .WithData("key", "value")
            .Build();

        msg.Data.Should().ContainKey("key").WhoseValue.Should().Be("value");
        msg.Notification.Should().BeNull();
        msg.ValidateOnly.Should().BeFalse();
    }

    [Fact]
    public void Build_WithNotificationOnly_Succeeds()
    {
        var msg = PushMessageBuilder.Create()
            .WithNotification("Hello", "World", "https://img.example.com/img.png")
            .Build();

        msg.Notification!.Title.Should().Be("Hello");
        msg.Notification.Body.Should().Be("World");
        msg.Notification.ImageUrl.Should().Be("https://img.example.com/img.png");
    }

    [Fact]
    public void Build_WithNeitherDataNorNotification_Throws()
    {
        var builder = PushMessageBuilder.Create();
        builder.Invoking(b => b.Build())
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*data*");
    }

    [Fact]
    public void Build_WithComplexJsonData_SerializesCorrectly()
    {
        var order = new { OrderId = "ORD-1", Status = "Shipped" };
        var msg = PushMessageBuilder.Create()
            .WithData("payload", order)
            .Build();

        msg.Data["payload"].Should().Contain("\"OrderId\"");
        msg.Data["payload"].Should().Contain("\"Shipped\"");
    }

    [Fact]
    public void Build_WithAndroidHighPriority_SetsFields()
    {
        var msg = PushMessageBuilder.Create()
            .WithData("k", "v")
            .WithAndroid(priority: AndroidPriority.High, ttlSeconds: 3600, collapseKey: "group-1")
            .Build();

        msg.Android!.Priority.Should().Be(AndroidPriority.High);
        msg.Android.TtlSeconds.Should().Be(3600);
        msg.Android.CollapseKey.Should().Be("group-1");
    }

    [Fact]
    public void Build_DryRun_SetsValidateOnly()
    {
        var msg = PushMessageBuilder.Create()
            .WithData("x", "y")
            .DryRun()
            .Build();

        msg.ValidateOnly.Should().BeTrue();
    }

    [Fact]
    public void Build_MergesDictionaryData()
    {
        var dict = new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" };
        var msg = PushMessageBuilder.Create()
            .WithData(dict)
            .Build();

        msg.Data.Should().ContainKeys("a", "b");
    }

    [Fact]
    public void Build_WithApns_ConfiguresHeaders()
    {
        var msg = PushMessageBuilder.Create()
            .WithData("x", "y")
            .WithApns(a =>
            {
                a.Headers["apns-priority"] = "10";
                a.Headers["apns-collapse-id"] = "order-99";
            })
            .Build();

        msg.Apns!.Headers.Should().ContainKey("apns-priority");
        msg.Apns.Headers["apns-collapse-id"].Should().Be("order-99");
    }
}

public class ApnMessageBuilderTests
{
    [Fact]
    public void Build_Alert_Succeeds()
    {
        var msg = ApnMessageBuilder.Create()
            .WithAlert("Title", "Body")
            .Build();

        msg.Aps.Alert!.Title.Should().Be("Title");
        msg.Aps.Alert.Body.Should().Be("Body");
        msg.PushType.Should().Be(ApnPushType.Alert);
    }

    [Fact]
    public void Build_Background_SetsPriority5()
    {
        var msg = ApnMessageBuilder.Create()
            .AsBackground()
            .Build();

        msg.Aps.ContentAvailable.Should().Be(1);
        msg.Priority.Should().Be(5);   // Apple requires priority 5 for background
        msg.PushType.Should().Be(ApnPushType.Background);
    }

    [Fact]
    public void Build_WithoutAlertOrContentAvailable_Throws()
    {
        ApnMessageBuilder.Create()
            .Invoking(b => b.Build())
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Build_WithCustomData_StoresValue()
    {
        var msg = ApnMessageBuilder.Create()
            .WithAlert("Hey")
            .WithCustomData("orderId", "ORD-123")
            .Build();

        msg.CustomData["orderId"].Should().Be("ORD-123");
    }

    [Fact]
    public void Build_WithCollapseId_Sets()
    {
        var msg = ApnMessageBuilder.Create()
            .WithAlert("test")
            .WithCollapseId("collapse-key-1")
            .Build();

        msg.CollapseId.Should().Be("collapse-key-1");
    }
}

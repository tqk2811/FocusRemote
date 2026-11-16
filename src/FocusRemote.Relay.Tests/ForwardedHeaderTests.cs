using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FocusRemote.Relay.Tests;

/// <summary>Behind Cloudflare the viewer rate limit must partition by CF-Connecting-IP, not by the proxy address.</summary>
public sealed class ForwardedHeaderTests
{
    private static WebApplicationFactory<Program> CreateFactory(params (string Key, string Value)[] settings)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Relay:ViewerConnectPerMinute", "1");
            foreach ((string key, string value) in settings)
                b.UseSetting(key, value);
        });

    /// <summary>GET /ws/viewer/... arriving from <paramref name="proxyIp"/> and carrying CF-Connecting-IP.</summary>
    private static async Task<HttpStatusCode> GetViewerAsync(WebApplicationFactory<Program> factory, string proxyIp, string clientIp)
    {
        HttpContext context = await factory.Server.SendAsync(c =>
        {
            c.Connection.RemoteIpAddress = IPAddress.Parse(proxyIp);
            c.Request.Method = "GET";
            c.Request.Path = "/ws/viewer/id_test";
            c.Request.Headers["CF-Connecting-IP"] = clientIp;
        });
        return (HttpStatusCode)context.Response.StatusCode;
    }

    [Fact]
    public async Task CfConnectingIp_PartitionsRateLimitPerClient()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory(
            ("Relay:UseForwardedHeaders", "true"),
            ("Relay:ForwardedForHeader", "CF-Connecting-IP"));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await GetViewerAsync(factory, "10.0.0.1", "203.0.113.1"));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await GetViewerAsync(factory, "10.0.0.1", "203.0.113.2"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await GetViewerAsync(factory, "10.0.0.1", "203.0.113.1"));
    }

    [Fact]
    public async Task CfConnectingIp_IgnoredWhenProxyNotTrusted()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory(
            ("Relay:UseForwardedHeaders", "true"),
            ("Relay:ForwardedForHeader", "CF-Connecting-IP"),
            ("Relay:TrustedProxies:0", "173.245.48.0/20"));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await GetViewerAsync(factory, "10.0.0.1" /* not a Cloudflare address */, "203.0.113.1"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await GetViewerAsync(factory, "10.0.0.1", "203.0.113.2"));
    }
}

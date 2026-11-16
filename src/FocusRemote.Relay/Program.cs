using System.Net;
using System.Threading.RateLimiting;
using FocusRemote.Relay.Relay;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.StaticFiles;

const string ViewerRateLimitPolicy = "viewer-connect";

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

RelayOptions relayOptions = builder.Configuration.GetSection(RelayOptions.SectionName).Get<RelayOptions>() ?? new RelayOptions();
builder.Services.Configure<RelayOptions>(builder.Configuration.GetSection(RelayOptions.SectionName));
builder.Services.AddSingleton<RelayService>();

if (relayOptions.UseForwardedHeaders)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        // Behind Cloudflare: "CF-Connecting-IP" carries the single real client IP.
        options.ForwardedForHeaderName = relayOptions.ForwardedForHeader;
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();
        // Empty = trust whatever proxy reaches us (the firewall must then allow only the proxy).
        foreach (string entry in relayOptions.TrustedProxies)
        {
            if (entry.Contains('/'))
            {
                string[] parts = entry.Split('/', 2);
                options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(IPAddress.Parse(parts[0]), int.Parse(parts[1])));
            }
            else
            {
                options.KnownProxies.Add(IPAddress.Parse(entry));
            }
        }
    });
}

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(ViewerRateLimitPolicy, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = relayOptions.ViewerConnectPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

WebApplication app = builder.Build();

if (relayOptions.UseForwardedHeaders)
    app.UseForwardedHeaders();

app.UseDefaultFiles();
var contentTypes = new FileExtensionContentTypeProvider();
contentTypes.Mappings[".js"] = "text/javascript";
contentTypes.Mappings[".mjs"] = "text/javascript";
contentTypes.Mappings[".wasm"] = "application/wasm";
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = contentTypes,
    OnPrepareResponse = ctx =>
    {
        if (string.Equals(ctx.File.Name, "index.html", StringComparison.OrdinalIgnoreCase))
            ctx.Context.Response.Headers.CacheControl = "no-cache";
    },
});

app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(relayOptions.KeepAliveSeconds),
});

app.UseRouting();
app.UseRateLimiter();

app.Map("/ws/host/{id}", (HttpContext context, string id, RelayService relay) => relay.HandleHostAsync(context, id));
app.Map("/ws/viewer/{id}", (HttpContext context, string id, RelayService relay) => relay.HandleViewerAsync(context, id))
    .RequireRateLimiting(ViewerRateLimitPolicy);

app.Run();

public partial class Program;

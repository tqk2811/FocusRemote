using System.Security.Cryptography;
using FocusRemote.Host.Core.Relay;
using FocusRemote.Host.Core.Settings;
using FocusRemote.Host.Core.Settings.Models;
using FocusRemote.Host.Core.Sources;
using FocusRemote.Host.Core.Sources.Enums;
using FocusRemote.Protocol.Helpers;

namespace FocusRemote.Host.Tests;

public sealed class SettingsAndIdTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "FocusRemoteTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public void Settings_RoundTrip_KeepsEveryFieldAndRootKey()
    {
        byte[] rootKey = RandomNumberGenerator.GetBytes(32);
        var store = new SettingsStore(Path.Combine(_directory, "settings.json"));
        var settings = new HostSettings
        {
            RelayUrl = "https://relay.example.com",
            Id = "my-host_1",
            ProtectedRootKey = RootKeyProtector.Protect(rootKey),
            LanEnabled = true,
            LanPort = 9443,
            StartWithWindows = true,
        };
        store.Save(settings);

        HostSettings loaded = store.Load();
        Assert.Equal(settings.RelayUrl, loaded.RelayUrl);
        Assert.Equal(settings.Id, loaded.Id);
        Assert.Equal(settings.LanEnabled, loaded.LanEnabled);
        Assert.Equal(settings.LanPort, loaded.LanPort);
        Assert.Equal(settings.StartWithWindows, loaded.StartWithWindows);
        Assert.Equal(rootKey, RootKeyProtector.TryUnprotect(loaded.ProtectedRootKey));

        string json = File.ReadAllText(store.FilePath);
        Assert.DoesNotContain(Convert.ToBase64String(rootKey), json);
    }

    [Fact]
    public void Settings_MissingOrCorruptFile_GivesDefaults()
    {
        var store = new SettingsStore(Path.Combine(_directory, "settings.json"));
        Assert.Equal(HostSettings.DefaultLanPort, store.Load().LanPort);
        Directory.CreateDirectory(_directory);
        File.WriteAllText(store.FilePath, "{ not json");
        Assert.Equal(string.Empty, store.Load().Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("AAAA")]
    public void RootKey_UnprotectInvalid_ReturnsNull(string? value)
    {
        Assert.Null(RootKeyProtector.TryUnprotect(value));
    }

    [Fact]
    public void Generator_OutputPassesValidator()
    {
        for (int i = 0; i < 100; i++)
        {
            Assert.True(CredentialValidator.IsValidId(CredentialGenerator.NewId()));
            Assert.True(CredentialValidator.IsValidPassword(CredentialGenerator.NewPassword()));
        }
    }

    [Theory]
    [InlineData("w:1234", SourceKind.Window, 1234)]
    [InlineData("w:9007199254740991", SourceKind.Window, 9007199254740991)]
    [InlineData("m:0", SourceKind.Monitor, 0)]
    [InlineData("m:12", SourceKind.Monitor, 12)]
    public void SourceId_ParsesValid(string id, SourceKind kind, long value)
    {
        Assert.True(SourceId.TryParse(id, out SourceKind k, out long v));
        Assert.Equal(kind, k);
        Assert.Equal(value, v);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("w:")]
    [InlineData("w:0")]
    [InlineData("w:-5")]
    [InlineData("w:+5")]
    [InlineData("w: 5")]
    [InlineData("x:5")]
    [InlineData("W:5")]
    [InlineData("m:abc")]
    [InlineData("m:99999999999")]
    [InlineData("w:99999999999999999999")]
    [InlineData("w5")]
    public void SourceId_RejectsMalformed(string? id)
    {
        Assert.False(SourceId.TryParse(id, out _, out _));
    }

    [Fact]
    public void SourceId_FormatRoundTrips()
    {
        Assert.Equal("w:4242", SourceId.ForWindow(new IntPtr(4242)));
        Assert.Equal("m:3", SourceId.ForMonitor(3));
    }

    [Theory]
    [InlineData("https://relay.example.com", "abc", "wss://relay.example.com/ws/host/abc")]
    [InlineData("https://relay.example.com/", "abc", "wss://relay.example.com/ws/host/abc")]
    [InlineData("http://10.0.0.2:5000/sub", "a_b-c", "ws://10.0.0.2:5000/sub/ws/host/a_b-c")]
    [InlineData("wss://relay.example.com:8443/x/", "abc", "wss://relay.example.com:8443/x/ws/host/abc")]
    public void RelayHostUri_IsBuiltFromBaseUrl(string relayUrl, string id, string expected)
    {
        Assert.Equal(expected, RelayHostClient.BuildHostUri(relayUrl, id).ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("relay.example.com")]
    [InlineData("ftp://relay.example.com")]
    public void RelayHostUri_RejectsInvalid(string relayUrl)
    {
        Assert.Throws<UriFormatException>(() => RelayHostClient.BuildHostUri(relayUrl, "abc"));
    }
}

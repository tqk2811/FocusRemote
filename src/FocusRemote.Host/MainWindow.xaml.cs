using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using FocusRemote.Host.Core;
using FocusRemote.Host.Core.Lan;
using FocusRemote.Host.Core.Logging.Interfaces;
using FocusRemote.Host.Core.Models;
using FocusRemote.Host.Core.Relay.Enums;
using FocusRemote.Host.Core.Settings;
using FocusRemote.Host.Core.Settings.Models;
using FocusRemote.Protocol.Crypto;
using FocusRemote.Protocol.Helpers;
using QRCoder;

namespace FocusRemote.Host;

/// <summary>Settings + status window. Owns the <see cref="HostService"/>.</summary>
public partial class MainWindow : Window
{
    private readonly IAppLog _log;
    private readonly SettingsStore _store = new(AppPaths.SettingsFile);
    private readonly HostService _service;
    private HostSettings _settings;
    private string? _qrUrl;
    private bool _closingAllowed;

    public MainWindow(IAppLog log, bool startMinimized)
    {
        _log = log;
        InitializeComponent();
        _service = new HostService(log, Path.Combine(AppContext.BaseDirectory, "wwwroot"));
        _service.StatusChanged += status => Dispatcher.BeginInvoke(() => RenderStatus(status));

        _settings = _store.Load();
        RelayUrlBox.Text = _settings.RelayUrl;
        IdBox.Text = _settings.Id;
        LanEnabledBox.IsChecked = _settings.LanEnabled;
        LanPortBox.Text = _settings.LanPort.ToString(CultureInfo.InvariantCulture);
        StartWithWindowsBox.IsChecked = _settings.StartWithWindows;
        TerminalEnabledBox.IsChecked = _settings.TerminalEnabled;
        UpdatePasswordHint();
        RenderStatus(_service.Status);
        if (startMinimized)
            WindowState = WindowState.Minimized;

        Loaded += async (_, _) =>
        {
            // Auto-start when the settings are complete (also covers "start with Windows").
            byte[]? rootKey = RootKeyProtector.TryUnprotect(_settings.ProtectedRootKey);
            if (rootKey is not null && CredentialValidator.IsValidId(_settings.Id))
                await StartServiceAsync(_settings, rootKey);
        };
    }

    private void UpdatePasswordHint()
    {
        PasswordHint.Text = RootKeyProtector.TryUnprotect(_settings.ProtectedRootKey) is null
            ? "Chưa có password đã lưu."
            : "Đã có password đã lưu (để trống để giữ nguyên; đổi Id thì phải nhập lại password).";
    }

    private void RandomId_Click(object sender, RoutedEventArgs e) => IdBox.Text = CredentialGenerator.NewId();

    private void RandomPassword_Click(object sender, RoutedEventArgs e) => PasswordBox.Text = CredentialGenerator.NewPassword();

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SettingsError.Text = string.Empty;
            string id = IdBox.Text.Trim();
            string password = PasswordBox.Text;
            string relayUrl = RelayUrlBox.Text.Trim();
            if (!CredentialValidator.IsValidId(id))
            {
                SettingsError.Text = "Id phải gồm 3–64 ký tự A-Z a-z 0-9 _ -.";
                return;
            }
            if (!int.TryParse(LanPortBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65535)
            {
                SettingsError.Text = "Cổng LAN không hợp lệ.";
                return;
            }
            if (relayUrl.Length > 0)
            {
                try
                {
                    Core.Relay.RelayHostClient.BuildHostUri(relayUrl, id);
                }
                catch (UriFormatException ex)
                {
                    SettingsError.Text = ex.Message;
                    return;
                }
            }
            bool lanEnabled = LanEnabledBox.IsChecked == true;
            if (relayUrl.Length == 0 && !lanEnabled)
            {
                SettingsError.Text = "Cần Relay URL hoặc bật chế độ LAN.";
                return;
            }

            byte[]? rootKey;
            string? protectedRootKey;
            if (password.Length > 0)
            {
                if (!CredentialValidator.IsValidPassword(password))
                {
                    SettingsError.Text = "Password tối thiểu 10 ký tự.";
                    return;
                }
                StartButton.IsEnabled = false;
                SettingsError.Text = "Đang suy khoá từ password (PBKDF2)…";
                rootKey = await Task.Run(() => KeyDerivation.DeriveRootKey(password, id));
                protectedRootKey = RootKeyProtector.Protect(rootKey);
                SettingsError.Text = string.Empty;
            }
            else
            {
                rootKey = id == _settings.Id ? RootKeyProtector.TryUnprotect(_settings.ProtectedRootKey) : null;
                protectedRootKey = _settings.ProtectedRootKey;
                if (rootKey is null)
                {
                    SettingsError.Text = id == _settings.Id ? "Nhập password." : "Đổi Id thì phải nhập lại password (khoá phụ thuộc Id).";
                    return;
                }
            }

            var settings = new HostSettings
            {
                RelayUrl = relayUrl,
                Id = id,
                ProtectedRootKey = protectedRootKey,
                LanEnabled = lanEnabled,
                LanPort = port,
                StartWithWindows = StartWithWindowsBox.IsChecked == true,
                TerminalEnabled = TerminalEnabledBox.IsChecked == true,
            };
            _store.Save(settings);
            _settings = settings;
            PasswordBox.Text = string.Empty;
            UpdatePasswordHint();
            try
            {
                StartupRegistration.Apply(settings.StartWithWindows, Environment.ProcessPath ?? string.Empty);
            }
            catch (Exception ex)
            {
                _log.Warn("Start with Windows registration failed", ex);
            }
            await StartServiceAsync(settings, rootKey);
        }
        catch (Exception ex)
        {
            _log.Error("Start failed", ex);
            SettingsError.Text = "Lỗi: " + ex.Message;
        }
        finally
        {
            StartButton.IsEnabled = true;
        }
    }

    private async Task StartServiceAsync(HostSettings settings, byte[] rootKey)
    {
        try
        {
            StartButton.IsEnabled = false;
            await _service.StartAsync(settings, rootKey);
        }
        catch (Exception ex)
        {
            _log.Error("Host service start failed", ex);
            SettingsError.Text = "Lỗi khởi động: " + ex.Message;
        }
        finally
        {
            StartButton.IsEnabled = true;
        }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _service.StopAsync();
        }
        catch (Exception ex)
        {
            _log.Error("Stop failed", ex);
        }
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.LogDirectory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Warn("Open log folder failed", ex);
        }
    }

    private void Firewall_Click(object sender, RoutedEventArgs e)
    {
        int port = _service.Status.LanPort;
        bool ok = FirewallHelper.TryAddRule(port);
        FirewallText.Text = ok ? $"Đã có firewall rule cho cổng {port}." : "Không thêm được firewall rule.";
        FirewallButton.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RenderStatus(HostStatus status)
    {
        StopButton.IsEnabled = status.IsRunning;
        RelayStatusText.Text = !status.IsRunning ? "Chưa chạy"
            : !status.RelayEnabled ? "Không dùng relay ngoài"
            : status.RelayState switch
            {
                RelayState.Connecting => "Đang kết nối " + status.RelayDetail,
                RelayState.Registered => "Đã đăng ký trên " + status.RelayDetail,
                RelayState.WaitingRetry => status.RelayDetail ?? "Chờ kết nối lại",
                _ => status.RelayDetail ?? "Dừng",
            };
        ViewerStatusText.Text = status.Viewer is null ? "Chưa có viewer"
            : "Đang kết nối: " + status.Viewer + (status.TerminalCount > 0 ? $" ({status.TerminalCount} terminal đang mở)" : "");
        EncoderStatusText.Text = status.Stream is { } s
            ? $"{s.EncoderName} {s.Width}x{s.Height}, {s.Fps:F1} fps, {s.BitrateKbps:F0} kbps (nguồn {s.SourceId}{(s.DroppedFrames > 0 ? $", bỏ {s.DroppedFrames} khung" : "")})"
            : "Chưa stream";

        if (!status.IsRunning || (!status.LanRunning && status.LanError is null))
        {
            LanStatusText.Text = "LAN chưa chạy.";
            LanUrlsBox.Text = string.Empty;
            SetQr(null);
            FirewallText.Text = string.Empty;
            FirewallButton.Visibility = Visibility.Collapsed;
            FirewallCommandBox.Visibility = Visibility.Collapsed;
            _firewallCheckedPort = 0;
            return;
        }
        if (status.LanError is not null)
        {
            LanStatusText.Text = "LAN lỗi: " + status.LanError;
            LanUrlsBox.Text = string.Empty;
            SetQr(null);
            return;
        }
        LanStatusText.Text = $"Đang phục vụ HTTPS trên cổng {status.LanPort}. Mở một trong các địa chỉ sau trên thiết bị cùng mạng:";
        LanUrlsBox.Text = string.Join(Environment.NewLine, status.LanUrls);
        SetQr(status.LanUrls.FirstOrDefault());
        // netsh is slow; status updates arrive every second while streaming.
        if (_firewallCheckedPort != status.LanPort)
        {
            _firewallCheckedPort = status.LanPort;
            RenderFirewall(status.LanPort);
        }
    }

    private int _firewallCheckedPort;

    private void RenderFirewall(int port)
    {
        if (FirewallHelper.RuleExists(port))
        {
            FirewallText.Text = $"Firewall: đã có rule cho cổng {port}.";
            FirewallButton.Visibility = Visibility.Collapsed;
            FirewallCommandBox.Visibility = Visibility.Collapsed;
        }
        else if (FirewallHelper.IsAdministrator())
        {
            FirewallText.Text = $"Firewall: chưa có rule cho cổng {port}.";
            FirewallButton.Visibility = Visibility.Visible;
            FirewallCommandBox.Visibility = Visibility.Collapsed;
        }
        else
        {
            FirewallText.Text = "Firewall: nếu thiết bị khác không vào được, chạy lệnh sau trong cửa sổ lệnh quyền admin (hoặc chạy FocusRemote bằng quyền admin):";
            FirewallCommandBox.Text = FirewallHelper.AddRuleCommand(port);
            FirewallCommandBox.Visibility = Visibility.Visible;
            FirewallButton.Visibility = Visibility.Collapsed;
        }
    }

    private void SetQr(string? url)
    {
        if (url == _qrUrl)
            return;
        _qrUrl = url;
        if (url is null)
        {
            QrImage.Source = null;
            return;
        }
        using var generator = new QRCodeGenerator();
        using QRCodeData data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        byte[] png = new PngByteQRCode(data).GetGraphic(8);
        var image = new BitmapImage();
        using (var stream = new MemoryStream(png))
        {
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
        }
        image.Freeze();
        QrImage.Source = image;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closingAllowed)
            return;
        // Stop the service (sockets, capture thread) before the window goes away, then close again.
        e.Cancel = true;
        IsEnabled = false;
        _ = StopThenCloseAsync();
    }

    private async Task StopThenCloseAsync()
    {
        try
        {
            await _service.DisposeAsync();
        }
        catch (Exception ex)
        {
            _log.Error("Stop on close failed", ex);
        }
        _closingAllowed = true;
        await Dispatcher.BeginInvoke(Close);
    }
}

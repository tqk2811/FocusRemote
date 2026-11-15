# Host

App Windows chia hai tầng: `FocusRemote.Host.Core` (toàn bộ logic, không phụ thuộc UI) và `FocusRemote.Host` (WPF, chỉ hiển thị và lưu cài đặt). Encode video/âm thanh nằm ở `FocusRemote.Host.Encoding` + DLL C++ `FocusRemote.Encoder.Native` (xem [Encoder-vi.md](Encoder-vi.md)).

## Kiến trúc Host.Core

| Thành phần | Vai trò |
|---|---|
| `HostService` | Điểm vào: nhận `HostSettings` + root key, dựng một `HostSessionManager` dùng chung cho kết nối relay ngoài và LAN server (chạy cùng lúc, tổng cộng một viewer). Nhận thêm factory `IInputInjector` (mặc định `SendInput`). |
| `Relay/RelayHostClient` | WebSocket `/ws/host/{id}` tới relay, tự nối lại, mỗi viewer ghép cặp thành một `IViewerLink`. |
| `Lan/LanServer` | Kestrel HTTPS (chứng chỉ tự ký, `LanCertificateManager`), phục vụ WebViewer và `/ws/viewer/{id}`. |
| `Sessions/HostSessionManager` | Bắt tay (Protocol §4), chống dò password, bảo đảm một viewer. |
| `Sessions/ViewerSession` | Vòng nhận Control/Input; một writer (`OutboundQueue`) seal và gửi theo thứ tự counter. Giữ `VideoStreamer` và `AudioStreamer` của nguồn đang xem. |
| `Sources/SourceCatalog` | Liệt kê cửa sổ (`EnumWindows`) và màn hình, lấy toạ độ hiện tại để quy đổi input. |
| `Video/VideoStreamer` | Thread riêng: `WinrtGraphicCapture` → `HardwareVideoEncoder` → Video Frame. |
| `Audio/AudioStreamer` | Thread riêng: WASAPI loopback (`TqkLibrary.AudioCapture`) → `OpusAudioEncoder` → Audio Config + Packet. |
| `Input/InputInjector` | `SendInput` (thật). `LoggingInputInjector` chỉ ghi log, dùng cho E2E. |

## Âm thanh

- Chỉ chạy khi `SelectSource.audio = true` **và** viewer đã báo `"opus"` trong `Capabilities.audioCodecs`.
- Nguồn là cửa sổ: process loopback của tiến trình sở hữu cửa sổ (gồm cả tiến trình con), `CaptureProcess(pid)`. Nguồn là màn hình: loopback endpoint phát mặc định (toàn hệ thống), `CaptureEndpoint(null)`.
- Xin WASAPI định dạng 48 kHz stereo float 32-bit (`AUTOCONVERTPCM` tự resample), không được thì PCM 16-bit. `PcmConverter` đổi về float xen kẽ, `AudioFrameAssembler` cắt khung 960 mẫu (20 ms) và giữ đồng hồ mẫu làm `timestampUs`.
- Loopback không trả dữ liệu khi máy im lặng. Quá 100 ms không có dữ liệu thì `AudioFrameAssembler.FillSilence` chèn khung im lặng theo đồng hồ thật, nên gói vẫn đi đều ~50 gói/s (gói Opus im lặng chỉ vài byte).
- Opus: `libopus` của bản FFmpeg LGPL (`application=lowdelay`, `frame_duration=20`, VBR, 96 kbps). Không có `libopus` thì DLL thử encoder `opus` gốc của FFmpeg (experimental).
- Lỗi capture/encode âm thanh chỉ ghi log và dừng âm thanh, không dừng video.

## Chạy E2E bằng trình duyệt thật

Thành phần:

- `src/FocusRemote.E2E`: console chạy `HostService` không cần WPF. Input mặc định là **dry-run** (`LoggingInputInjector` in dòng `INPUT ...`, không đụng desktop thật); `--real-input` mới gọi `SendInput`. Có manifest PerMonitorV2 như Host thật.
  `FocusRemote.E2E --id <id> --password <pwd> [--relay <url>] [--lan-port <port>] [--real-input]` — dừng khi stdin hết hoặc Ctrl+C.
- `src/FocusRemote.E2E/browser/run-e2e.mjs`: script Node (không cần gói npm, CDP qua `WebSocket` có sẵn của Node ≥ 22) chạy Relay + harness, mở Google Chrome headless với `--user-data-dir` riêng, đăng nhập, chọn nguồn, rồi kiểm:
  - `window.__frStats.videoFramesDrawn ≥ 10` (VideoDecoder thật sự ra khung), chụp ảnh `viewer-<mode>.png`;
  - gửi chuột/phím qua CDP và chờ dòng `INPUT MouseMove`, `INPUT MouseButton Left down=True`, `INPUT Key KeyA down=True` trong log harness;
  - bấm nút loa (cử chỉ thật) rồi chờ `audioFramesDecoded ≥ 5` (AudioDecoder ra dữ liệu).

```
dotnet build src/FocusRemote.sln
node src/FocusRemote.E2E/browser/run-e2e.mjs --mode relay --out <thư mục tạm>
node src/FocusRemote.E2E/browser/run-e2e.mjs --mode lan --out <thư mục tạm> --source first-window
```

- `--mode relay`: Relay `http://127.0.0.1:<cổng trống>` (localhost là secure context). `--mode lan`: harness bật LAN HTTPS tự ký, Chrome chạy với `--ignore-certificate-errors`.
- `--source m:0` (mặc định), `w:<hwnd>` hoặc `first-window`; `--no-audio`; `--headful` để xem cửa sổ Chrome.
- Kết quả ghi `result-<mode>.json` + log `relay.log`/`host.log`/`chrome.log` trong `--out`. Script chỉ dừng các tiến trình chính nó tạo.
- Cần desktop session và GPU D3D11 (capture + encoder phần cứng thật). Màn hình đứng yên thì WinRT chỉ đưa ~1 khung/s, nên đợi đủ 10 khung có thể mất 10–20 s.
- LAN listen `0.0.0.0` nên lần đầu Windows Firewall có thể hỏi quyền cho `FocusRemote.E2E.exe`; bấm Cancel vẫn chạy được qua loopback.

`window.__frStats` (getter, chỉ đọc) có trong WebViewer: `authenticated`, `streaming`, `sources`, `videoFramesReceived/Decoded/Drawn`, `videoDecoderErrors`, `audioPacketsReceived`, `audioFramesDecoded`, `audioDecoderErrors`… dùng được khi gỡ lỗi từ console.

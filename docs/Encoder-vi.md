# Encoder video (Host)

Encode khung hình từ D3D11 thẳng vào encoder phần cứng qua FFmpeg, không đọc khung về CPU.

| Thành phần | Vai trò |
|---|---|
| `src/FocusRemote.Encoder.Native` | DLL C++20 (x64). API C ở `FocusRemoteEncoder.h`, quy ước gọi `cdecl`. |
| `src/FocusRemote.Host.Encoding` | Thư viện C# `net8.0-windows`: lớp `HardwareVideoEncoder` bọc DLL, kéo gói FFmpeg native và chép DLL native ra output. |
| `src/FocusRemote.EncoderSmoke` | Console thử: capture màn hình chính, encode ~150 khung, ghi `.h264` Annex B. |

## Luồng xử lý

1. `FrEncoder_Init`: tạo D3D11 device trên **DXGI adapter 0** (`VIDEO_SUPPORT | BGRA_SUPPORT`, bật multithread protected), bọc vào `AVHWDeviceContext` loại D3D11VA.
   - Adapter 0 là adapter mà `D3D11CreateDevice(nullptr)` chọn, cũng là adapter `TqkLibrary.WindowCapture` dùng. Texture chia sẻ kiểu cũ (legacy shared handle) chỉ mở được trên device cùng adapter.
2. `FrEncoder_Open`: tạo texture BGRA `WxH` (`D3D11_RESOURCE_MISC_SHARED`, `BIND_RENDER_TARGET | BIND_SHADER_RESOURCE`) làm surface đầu vào, tạo `ID3D11VideoProcessor` BGRA → NV12, rồi mở encoder (xem mục chọn encoder).
3. Mỗi khung:
   1. `WinrtGraphicCapture.Render(encoder.InputSurface, isNewSurface, ref isNewTargetView)`: WindowCapture mở surface bằng shared handle trên device **của nó** và vẽ khung đã capture (co giãn theo kích thước surface) vào đó.
   2. `FrEncoder_Encode`: lấy một frame từ pool `AVHWFramesContext` (NV12, `BIND_RENDER_TARGET`), `VideoProcessorBlt` BGRA → NV12 trên GPU (RGB full range → BT.709 limited range), `avcodec_send_frame` / `avcodec_receive_packet`.
   3. Trả về gói Annex B (con trỏ thuộc encoder, hợp lệ tới lần gọi kế tiếp), kèm cờ keyframe và `pts` (= `timestampUs` truyền vào).
4. Keyframe nào cũng có SPS/PPS phía trước: không bật `AV_CODEC_FLAG_GLOBAL_HEADER`, và nếu encoder không lặp lại SPS/PPS thì DLL tự chèn bản đã nhớ từ keyframe trước.

## Chọn encoder

Theo `VendorId` của adapter 0, thử lần lượt tới khi mở được:

| Vendor | Thứ tự thử |
|---|---|
| NVIDIA `0x10DE` | `h264_nvenc` → `h264_mf` |
| Intel `0x8086` | `h264_qsv` → `h264_mf` |
| AMD `0x1002` | `h264_amf` → `h264_mf` |
| Khác | `h264_mf` |

- `VideoEncoderOptions.EncoderName` ép dùng một encoder cụ thể.
- Encoder nhận `AV_PIX_FMT_D3D11` thì chạy zero-copy (`IsZeroCopy = true`). Encoder không nhận (vd `h264_qsv` trong bản build này chỉ nhận `nv12` hoặc `qsv`; đường derive frame D3D11 → QSV chưa làm) thì NV12 được copy sang texture staging, `Map` rồi đưa frame bộ nhớ hệ thống vào encoder.
- `h264_mf` chạy zero-copy cần MFT phần cứng (`hw_encoding=1`). Nếu khung đầu tiên lỗi (MFT phần mềm báo `Failed to set D3D manager`) thì DLL tự mở lại encoder đó với frame bộ nhớ hệ thống.
- Thiết lập độ trễ thấp: không B-frame, `AV_CODEC_FLAG_LOW_DELAY`, VBV ≈ 1 khung, GOP = `GopSeconds × Fps` (`GopSeconds <= 0` thì GOP rất dài, chỉ keyframe khi ép).
  - nvenc: `preset=p1`, `tune=ull`, `zerolatency=1`, `delay=0`, `forced-idr=1`, `rc=vbr` với `maxrate = bitrate`. Không dùng `cbr` vì nvenc chèn filler data cho đủ bitrate cả khi màn hình đứng yên.
  - qsv: `preset=veryfast`, `async_depth=1`, `look_ahead=0`, `forced_idr=1`.
  - amf: `usage=ultralowlatency`, `quality=speed`, `rc=cbr`, `header_insertion_mode=idr`.
  - mf: `rate_control=cbr`, `scenario=display_remoting`.
- HEVC: `VideoCodec.Hevc` thay tiền tố `h264_` bằng `hevc_` (chưa thử).

## Đồng bộ giữa hai device (QUAN TRỌNG)

Surface được ghi bởi device của WindowCapture và đọc bởi device của encoder. Với legacy shared resource, bên ghi phải `Flush()` context sau khi vẽ thì lệnh vẽ mới được gửi xuống GPU trước khi bên đọc dùng.

- `TqkLibrary.WindowCapture` 1.0.8 **không Flush** sau `Draw` trong `RenderToSurface::Render` (`RenderToSurface.cpp`).
- Hậu quả đã đo trên máy dev: màn hình đứng yên thì khung vẽ cuối cùng nằm mãi trong command buffer, encoder đọc ra **khung đen** (YAVG = 16 suốt 150 khung). Khi màn hình đang thay đổi thì lệnh của khung sau đẩy lệnh khung trước đi, nên hình có nhưng có thể trễ/xé.
- Đã kiểm chứng: build lại `TqkLibrary.WindowCapture.Native.dll` chỉ thêm `deviceCtx->Flush();` sau `Draw` thì cùng kịch bản cho hình đúng ngay khung đầu.
- Phía encoder không ép được device khác Flush. Thử gọi `Capture()` (có `Map`) sau `Render` để mượn lần flush: **không** có tác dụng.
- **Cần sửa ở upstream** (TqkLibrary.WindowCapture): thêm `Flush()` cuối `RenderToSurface::Render`, tốt hơn nữa là dùng keyed mutex hoặc `ID3D11Fence` để bên đọc chờ đúng lúc. Trước khi có bản sửa, Host stream được khi màn hình thay đổi nhưng khung tĩnh có thể sai.

Các điểm khác của WindowCapture cần biết:

- `Size` trả `m_lastSize` = kích thước nội dung khung capture gần nhất (`frame.ContentSize()`), lúc đầu là `GraphicsCaptureItem.Size`.
- `Render` co giãn cả khung vào toàn bộ surface (không giữ tỉ lệ). Host nên tạo surface đúng tỉ lệ `Size` (làm chẵn) và mở lại encoder khi `Size` đổi.
- `Render` trả `false` khi không có khung mới (trừ khi `isNewSurface`/`isNewtargetView`); với `isNewSurface = true` nó trả `true` cả khi chưa có khung nào (vẽ texture rỗng).
- Khung capture đi đường GPU → texture **STAGING** (`_tmpFrame`, để `Capture()` đọc CPU được) → texture DEFAULT → vẽ. Staging nằm trong bộ nhớ hệ thống nên mỗi khung qua PCIe hai lần với GPU rời. Nên đề xuất upstream giữ bản sao DEFAULT cho đường `Render`.
- Callback `OnFrameArrived` (thread của frame pool) và `Render` (thread gọi) dùng chung immediate context mà device không bật multithread protected: có rủi ro race, cũng nên sửa upstream.

## Build

- Cần Visual Studio có workload C++ (toolset `v145`, Windows SDK 10).
- `dotnet build FocusRemote.sln` (hoặc build `FocusRemote.Host.Encoding`): target `BuildNativeEncoder` tìm MSBuild của Visual Studio bằng `vswhere` rồi build `FocusRemote.Encoder.Native.vcxproj` (x64, cùng Debug/Release). Truyền `-p:SkipNativeEncoderBuild=true` để dùng DLL đã build sẵn.
- Header/lib FFmpeg lấy từ gói `TqkLibrary.FFmpeg.Lgpl2.Native.Win.x64` 9.0.2 trong NuGet cache (`FFmpegPackageVersion` trong vcxproj phải khớp `PackageReference` trong `FocusRemote.Host.Encoding.csproj`). Chưa restore gói thì vcxproj báo lỗi hướng dẫn.
- Trong `.sln`, vcxproj chỉ có `ActiveCfg` (không `Build.0`) để `dotnet build` không cố build nó.
- Output: `FocusRemote.Encoder.Native.dll` được đặt ở `runtimes/win-x64/native/` cạnh các DLL FFmpeg, nên loader tìm thấy `avcodec-63.dll`/`avutil-61.dll` bên cạnh.

## Smoke test

```
FocusRemote.EncoderSmoke <out.h264> [frameCount] [encoderName]
```

Kết quả trên máy dev (RTX 4060 Laptop là adapter 0, màn hình 2560×1600, 30 fps, 8 Mbps):

| Encoder | Zero-copy | Render + encode trung bình / p95 |
|---|---|---|
| `h264_nvenc` | có | ~4.3 ms / ~7 ms |
| `h264_mf` (NVIDIA MFT) | có | ~5.2 ms / ~8 ms (khung đầu ~40 ms) |
| `h264_nvenc`, ép đường bộ nhớ hệ thống | không | ~6.6 ms / ~7.4 ms |

`ffprobe` xác nhận: H.264 High, 2560×1600, 150 khung, `color_range=tv`, `color_space=bt709`; keyframe thứ hai (ép ở khung 75) có SPS/PPS phía trước.

## Encoder âm thanh (Opus)

- API C `FrAudioEncoder_*` trong `FocusRemoteEncoder.h` (code ở `FocusRemoteAudioEncoder.cpp`), bọc C# là `OpusAudioEncoder`.
- Bản FFmpeg LGPL 9.0.2 có `--enable-libopus`: dùng `libopus` (`application=lowdelay`, `frame_duration=20`, `vbr=on`), vào float xen kẽ (`AV_SAMPLE_FMT_FLT`), mặc định 48 kHz stereo 96 kbps. Không mở được thì thử encoder `opus` gốc của FFmpeg (experimental).
- Mỗi lần gọi: `Send` đúng một khung (`GetFrameSamples` = 960 mẫu mỗi kênh với 20 ms), rồi `Receive` tới khi trả 0. Gói ra là Opus thô (không container), `pts` theo µs.
- Capture và cách dùng trong Host: xem [Host-vi.md](Host-vi.md#âm-thanh).

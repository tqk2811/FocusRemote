# FocusRemote – Kế hoạch

Phần mềm điều khiển máy tính Windows từ xa: chỉ xem/điều khiển đúng **một cửa sổ hoặc một màn hình** được chọn. Ghép kênh qua relay ASP.NET bằng Id, mã hoá đầu cuối bằng password (relay không đọc được).

## Quyết định đã chốt (2026-11-16)

- Tên project: **FocusRemote**, repo `D:\IT\Github\tqk2811\FocusRemote`.
- Viewer: **Web trước** (một trang chạy cả điện thoại + PC). Android làm sau.
- E2E: **PBKDF2 + AES-256-GCM** (WebCrypto hỗ trợ sẵn).
- Đóng khung gói (chốt 2026-11-16): **tự đóng khung theo message WebSocket**, không dùng `MultiStreamHelper`. Mỗi message = `[nonce 12B][ciphertext + tag]`, plaintext = `[channel 1B][type 1B][payload]`. Kênh nằm trong phần mã hoá; Host được bỏ khung video cũ khi nghẽn; gói input chen giữa video.
- Video: encode bằng **ffmpeg** (chốt 2026-11-16). **H.264 là codec bắt buộc** (mọi trình duyệt giải được). **H.265 là tuỳ chọn thương lượng**: viewer gọi `VideoDecoder.isConfigSupported` rồi báo Host danh sách codec giải được; Host chỉ dùng HEVC khi viewer hỗ trợ và có encoder phần cứng (`hevc_nvenc`/`hevc_qsv`/`hevc_amf`), không dùng `libx265` (chậm, GPL).
- Thư viện ưu tiên dùng: `TqkLibrary.WindowCapture`, `TqkLibrary.AudioCapture`, `TqkLibrary.StreamRelay`, `TqkLibrary.Streams`. Có NuGet thì dùng NuGet, không có thì thêm submodule vào `libs/`.

## Thành phần

1. **FocusRemote.Host** (app Windows, .NET 8, **WPF** – chốt 2026-11-16)
   - Liệt kê cửa sổ/màn hình (`TqkLibrary.WindowCapture`).
   - Capture khung hình → encode H.264.
   - Âm thanh (`TqkLibrary.AudioCapture`) – giai đoạn 2.
   - Nhận input từ viewer → `SendInput`, quy đổi toạ độ theo cửa sổ/màn hình đang xem.
   - Nhập Id + password, kết nối relay và duy trì kênh (tự nối lại, heartbeat).
2. **FocusRemote.Relay** (ASP.NET Core, WebSocket)
   - Ghép Host ↔ Viewer theo Id, chuyển tiếp byte nguyên vẹn, không giải mã.
   - Có thể dùng `TqkLibrary.StreamRelay` / `TqkLibrary.Streams` (xem khảo sát).
   - Rate limit chống dò Id. Relay chỉ biết Id, không bao giờ nhận password.
   - Phục vụ luôn trang WebViewer trong `wwwroot`.
3. **WebViewer** (trang tĩnh)
   - Nhập Id + password → suy khoá bằng WebCrypto.
   - WebCodecs `VideoDecoder` → canvas.
   - Input: chuột + phím (PC); trên điện thoại có 2 chế độ: chạm trực tiếp và kéo như touchpad (kiểu remote desktop Android).
   - Danh sách cửa sổ/màn hình của Host, bấm vào để xem riêng cái đó.
4. **FocusRemote.Protocol** (thư viện dùng chung)
   - Gói tin: `[nonce 12B][ciphertext + tag]`, plaintext `[channel 1B][type 1B][payload]` (type nằm trong phần mã hoá).
   - Khoá gốc = PBKDF2(password, salt từ Id, ≥ 600k vòng). Mỗi phiên trộn thêm số ngẫu nhiên qua HKDF để có khoá phiên riêng, tránh lặp nonce.
   - Bắt tay đầu phiên kiểm password đúng: viewer phải giải mã được gói thử thì Host mới gửi danh sách cửa sổ.
5. **App Android** – giai đoạn sau.

## Cấu trúc solution dự kiến

```
FocusRemote/
  src/FocusRemote.Protocol/
  src/FocusRemote.Host/
  src/FocusRemote.Relay/        (kèm wwwroot = WebViewer)
  src/FocusRemote.Protocol.Tests/   (mọi code, kể cả test/tool, nằm trong src/)
  docs/
```

## Thứ tự làm (mỗi bước một commit trên nhánh phụ)

1. Khảo sát 4 thư viện TqkLibrary: có NuGet không, API chính, `StreamRelay` có làm sẵn phần relay không, có encoder H.264 sẵn không. **Chỉ đọc, chưa ghi code.**
2. Dựng solution + project Protocol + test mã hoá.
3. Relay: ghép kênh theo Id.
4. Host: liệt kê cửa sổ/màn hình, capture + encode, gửi lên relay.
5. WebViewer: giải mã + hiển thị, chọn cửa sổ/màn hình.
6. Điều khiển chuột, phím, chạm.
7. Âm thanh, rồi app Android.

## Kết quả khảo sát bước 1 (2026-11-16)

| Thư viện | NuGet | Kết luận |
|---|---|---|
| `TqkLibrary.WindowCapture` | Có (1.0.8, netstandard2.0, native x64/x86) | Dùng. `WinrtGraphicCapture` (window + monitor, `MaxFps`, ẩn/hiện cursor, viền), `DesktopDuplicationCapture` (monitor), `HdcCapture` (dự phòng). Lấy khung qua `Capture()` → `Bitmap` (CPU) hoặc `Render(surface)` → D3D (GPU). `BaseCapture.Monitors` liệt kê màn hình. **Không** có liệt kê cửa sổ → Host tự `EnumWindows`. **Không** có encoder. |
| `TqkLibrary.AudioCapture` | Có (1.0.7, net8.0-windows7.0) | Dùng ở giai đoạn 2. WASAPI, `CaptureEndpoint` / `CaptureProcess(pid)` (bắt tiếng riêng tiến trình của cửa sổ đang xem) → `AudioCaptureStream` PCM. Chưa có encoder Opus/AAC. |
| `TqkLibrary.Streams` | Có (1.0.26) | Có thể dùng `MultiStreamHelper<TId>` để ghép nhiều kênh (video/input/điều khiển) trên một stream, nhưng WebViewer (JS) không có bản tương ứng → nhiều khả năng tự định nghĩa khung gói trong Protocol cho đơn giản. |
| `TqkLibrary.StreamRelay` | **Không** có NuGet (repo public) | **Không dùng.** Relay này demux bằng FFmpeg ở server, GOP buffer, phát một chiều theo GUID → trái với E2E (relay không được đọc nội dung) và không có kênh ngược cho input. Relay của FocusRemote tự viết: ghép 2 WebSocket theo Id, chuyển byte nguyên vẹn. Có thể tham khảo cách đóng khung/backpressure. |

Hệ quả:
- Encoder H.264 phải tự lo. Đề xuất: ffmpeg qua gói native `TqkLibrary.FFmpeg.*` đã có sẵn (ưu tiên encoder phần cứng `h264_nvenc`/`h264_qsv`/`h264_amf`/`h264_mf`, dự phòng `libx264` nếu dùng bản GPL). Cần kiểm bản lgpl/gpl nào có encoder nào trước khi chốt.
- Host target `net8.0-windows10.0.17763+` (WinRT capture cần Win10 1903).

## Rủi ro

- Password ngắn vẫn dò offline được nếu traffic qua relay bị lộ → bắt password tối thiểu 10 ký tự, PBKDF2 ≥ 600k vòng.
- WebCodecs cần HTTPS; Safari iOS cần ≥ 16.4.
- Không gửi được input vào cửa sổ chạy quyền admin / màn hình UAC nếu Host chạy quyền thường.

## Quyết định bổ sung (user trả lời 2026-11-16)

- H.265: bước 2 chỉ thêm trường codec vào bắt tay; encode HEVC làm sau khi H.264 chạy.
- Gọi ffmpeg: **DLL C++** (`FocusRemote.Encoder.Native`) đưa texture D3D11 thẳng vào encoder phần cứng (nvenc/qsv/amf, dự phòng `h264_mf`), zero-copy; không dùng `FFmpeg.AutoGen`. Link với `TqkLibrary.FFmpeg.Lgpl2.Native.Win.x64` 9.0.2.
- License: **LGPL**; chỉ chuyển GPL nếu bắt buộc (vd cần `libx264` làm dự phòng phần mềm).
- Relay: HTTPS coi như có sẵn. Deploy đa nền tảng (IIS, Docker, Linux); trước mắt **Windows IIS** → Relay không phụ thuộc gì riêng Windows.
- Id + password: **người dùng tự đặt cả hai**, có nút sinh ngẫu nhiên.
- **Một viewer** mỗi Host tại một thời điểm (vì có điều khiển). Viewer thứ hai bị từ chối.
- Host **có thể chạy admin**; về sau Host thành công cụ remote đa năng từ điện thoại về máy này (thêm chức năng khác) → Protocol chừa chỗ cho kênh mới.
- Âm thanh: Opus (WebCodecs `AudioDecoder`).
- Git: nhánh chính **`master`**; mỗi bước làm trên nhánh phụ.
- **Tự host trong LAN** (user thêm 2026-11-16): Host nhúng sẵn relay + WebViewer (Kestrel) để mở bằng trình duyệt trong LAN, không cần relay ngoài. Vướng: WebCrypto `subtle` và WebCodecs chỉ chạy trong secure context, nên `http://192.168.x.x` không dùng được. Hướng xử lý: Host tự sinh chứng chỉ self-signed và phục vụ HTTPS (người dùng chấp nhận cảnh báo một lần). Không ổn thì dùng app Android cho LAN.

## Việc còn mở

- (chưa có)

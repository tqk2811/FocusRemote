# Backlog

## [Vừa] Relay: báo rate limit bằng mã đóng 4029
Hiện vượt rate limit thì trả HTTP 429 trước khi nâng cấp WebSocket, trình duyệt không biết lý do. Cân nhắc nâng cấp rồi đóng 4029 như Protocol mục 1; thêm test rate limit.

## [Vừa] Relay: giới hạn hàng đợi theo tổng byte
Hàng đợi mỗi chiều giới hạn 64 message; với message 16 MiB có thể giữ ~1 GiB. Đổi sang giới hạn theo tổng byte.

## [Thấp] Relay: UseForwardedHeaders tin mọi proxy
Bật lên thì xoá KnownNetworks/KnownProxies. Thêm option danh sách proxy tin cậy.

## [Thấp] Relay: test lỗi kết nối
Chưa có test client rớt giữa chừng, bên nhận chậm.

## [Cao] WindowCapture: thiếu Flush sau Render (khung tĩnh ra đen)
`RenderToSurface::Render` của TqkLibrary.WindowCapture 1.0.8 không gọi `Flush()`, encoder ở device khác đọc surface chưa vẽ → màn hình đứng yên thì toàn khung đen. Đã thử thêm `deviceCtx->Flush()` sau `Draw` là đúng hình. Cần sửa upstream (tốt hơn: keyed mutex/fence) và bật multithread protected cho context. Chi tiết: docs/Encoder-vi.md.

## [Vừa] Encoder: h264_qsv chưa zero-copy
Bản FFmpeg này h264_qsv chỉ nhận nv12/qsv → máy Intel đi đường bộ nhớ hệ thống. Cần derive frame D3D11 → QSV. Chưa thử HEVC/qsv/amf.

## [Thấp] Encoder: nvenc tốn bitrate khi khung tĩnh
VBR không đặt `cq`, khung tĩnh ~3.5 MB/5 s. Tinh chỉnh sau.

## [Vừa] Host: viewer relay bị bỏ lơ khi đang có viewer LAN
Đang có viewer (qua LAN hoặc relay) thì viewer mới qua relay ngoài không nhận được gì (relay đã ghép cặp nên Host không đóng được socket của nó), viewer chỉ thấy treo tới khi tự hết giờ. Cân nhắc thêm thông báo "bận" trong Protocol (vd AuthFail kèm lý do) hoặc để relay biết Host đang bận.

## [Vừa] Host: toạ độ input của cửa sổ theo khung DWM, chưa tính phần thu nhỏ/che
Input cửa sổ quy đổi theo `DWMWA_EXTENDED_FRAME_BOUNDS` (đúng vùng WinRT capture, gồm cả thanh tiêu đề) chứ không phải client area. Cửa sổ thu nhỏ thì capture dừng nhưng stream vẫn mở; chưa báo viewer.

## [Thấp] Host: chưa thử LAN trên điện thoại thật
Chrome desktop qua chứng chỉ tự ký đã chạy (E2E `run-e2e.mjs --mode lan`, xem docs/Host-vi.md). Chưa thử Chrome Android/Safari iOS (chấp nhận cảnh báo chứng chỉ, WebCodecs), chưa thử firewall rule.

## [Thấp] E2E: LAN listen 0.0.0.0 làm Windows Firewall hỏi quyền
`LanServer` luôn bind `IPAddress.Any`, nên lần đầu chạy harness E2E (và test LAN) Windows hiện hộp thoại firewall trên desktop. Cân nhắc option bind loopback cho test/harness.

## [Vừa] Viewer: tắt tiếng vẫn nhận gói âm thanh
WebViewer mặc định tắt tiếng và bỏ gói Opus khi đang tắt, nhưng Host vẫn capture + gửi (~96 kbps). Cân nhắc message Control bật/tắt âm thanh, hoặc viewer chỉ gửi `audio:true` khi đã bật loa.

## [Thấp] Audio: chưa đồng bộ hình/tiếng, gói im lặng đều đặn
Viewer phát âm thanh theo đồng hồ AudioContext riêng (độ trễ ~60 ms), không khớp `timestampUs` với video. Khi máy im lặng Host vẫn gửi ~50 gói/s (vài byte mỗi gói) để giữ nhịp; có thể bật DTX của Opus.

## [Vừa] Video: màn hình đứng yên chỉ ~1 khung/s
WinRT capture chỉ đưa khung khi nội dung đổi, cộng với lỗi Flush ở trên, viewer chờ khá lâu mới đủ khung khi màn hình tĩnh (E2E đo 10 khung mất 10–20 s). Cân nhắc lặp lại khung cuối/keyframe định kỳ khi không có khung mới.

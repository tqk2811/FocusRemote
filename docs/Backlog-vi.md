# Backlog

## [Vừa] Relay: báo rate limit bằng mã đóng 4029
Hiện vượt rate limit thì trả HTTP 429 trước khi nâng cấp WebSocket, trình duyệt không biết lý do. Cân nhắc nâng cấp rồi đóng 4029 như Protocol mục 1; thêm test rate limit.

## [Vừa] Relay: giới hạn hàng đợi theo tổng byte
Hàng đợi mỗi chiều giới hạn 64 message; với message 16 MiB có thể giữ ~1 GiB. Đổi sang giới hạn theo tổng byte.

## [Thấp] Relay: UseForwardedHeaders tin mọi proxy
Bật lên thì xoá KnownNetworks/KnownProxies. Thêm option danh sách proxy tin cậy.

## [Thấp] Relay: test lỗi kết nối
Chưa có test client rớt giữa chừng, bên nhận chậm.

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

## [Vừa] Capture: chưa thử trên GPU Intel/AMD và laptop hybrid
`VideoProcessor` tạo enumerator với input = cỡ encoder, còn texture capture có thể lớn hơn (vd 4K → 1080p). NVIDIA chạy tốt, driver khác có thể từ chối `CreateVideoProcessorInputView`. Nếu lỗi thì tạo enumerator theo cỡ capture. Màn hình gắn vào adapter khác adapter 0 thì WGC tự copy chéo adapter (thêm một lượt qua PCIe).

## [Thấp] Capture: viền vàng có thể vẫn hiện
`IsBorderRequired(false)` cần quyền `GraphicsCaptureAccess` Borderless; app desktop không đóng gói có thể bị từ chối, lỗi đang bị nuốt. Cân nhắc gọi `RequestAccessAsync` và log kết quả.

## [Thấp] Video: cửa sổ thu nhỏ lúc chọn nguồn thì viewer chờ không báo gì
WGC không đưa khung khi cửa sổ thu nhỏ; `VideoStreamer` chờ tới khi có khung đầu mới gửi StreamStarted. Cân nhắc báo viewer (vd `IsIconic`).

## [Vừa] Web viewer: terminal trên điện thoại thiếu thanh phím Ctrl/Esc/Tab/mũi tên
Bàn phím ảo của điện thoại không có Ctrl, Esc, Tab, phím mũi tên nên khó dùng shell (vim, htop, lịch sử lệnh). Cân nhắc thêm thanh phím phụ phía trên bàn phím ảo trong panel terminal.

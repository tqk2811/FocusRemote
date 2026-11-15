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

## [Thấp] Host: chưa thử LAN bằng trình duyệt thật
LAN đã có test tích hợp (Kestrel HTTPS + viewer C#), nhưng chưa chạy WebViewer trong Chrome/Safari điện thoại qua chứng chỉ tự ký, chưa thử firewall rule.

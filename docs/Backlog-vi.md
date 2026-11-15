# Backlog

## [Vừa] Relay: báo rate limit bằng mã đóng 4029
Hiện vượt rate limit thì trả HTTP 429 trước khi nâng cấp WebSocket, trình duyệt không biết lý do. Cân nhắc nâng cấp rồi đóng 4029 như Protocol mục 1; thêm test rate limit.

## [Vừa] Relay: giới hạn hàng đợi theo tổng byte
Hàng đợi mỗi chiều giới hạn 64 message; với message 16 MiB có thể giữ ~1 GiB. Đổi sang giới hạn theo tổng byte.

## [Thấp] Relay: UseForwardedHeaders tin mọi proxy
Bật lên thì xoá KnownNetworks/KnownProxies. Thêm option danh sách proxy tin cậy.

## [Thấp] Relay: test lỗi kết nối
Chưa có test client rớt giữa chừng, bên nhận chậm.

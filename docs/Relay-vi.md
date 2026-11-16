# Relay: triển khai sau reverse proxy / Cloudflare

Relay giới hạn số lần viewer kết nối theo IP (`ViewerConnectPerMinute`). Đứng sau proxy thì relay chỉ thấy IP của proxy, nên phải bật đọc IP thật từ header.

## Cấu hình (`appsettings.json`, mục `Relay`)

| Khoá | Ý nghĩa |
|---|---|
| `UseForwardedHeaders` | `true` để đọc IP thật từ header (và scheme từ `X-Forwarded-Proto`). |
| `ForwardedForHeader` | Header chứa IP client. Mặc định `X-Forwarded-For`; sau Cloudflare dùng `CF-Connecting-IP`. |
| `TrustedProxies` | Danh sách IP hoặc CIDR của proxy được tin. Rỗng = tin mọi nơi gửi tới (khi đó firewall phải chỉ cho proxy vào). |

## Cloudflare

```json
"Relay": {
  "UseForwardedHeaders": true,
  "ForwardedForHeader": "CF-Connecting-IP",
  "TrustedProxies": [ "173.245.48.0/20", "103.21.244.0/22", "..." ]
}
```

- Dải IP Cloudflare lấy ở https://www.cloudflare.com/ips-v4 và https://www.cloudflare.com/ips-v6; dải này thỉnh thoảng thay đổi, cần cập nhật.
- Nên chặn firewall VPS chỉ cho dải IP Cloudflare vào cổng HTTPS, để không ai gọi thẳng IP gốc kèm header giả.
- SSL/TLS mode: **Full (strict)** với Cloudflare Origin CA cert trên VPS (hoặc **Full** nếu dùng chứng chỉ tự ký). Không dùng **Flexible**.
- Cổng HTTPS Cloudflare proxy được: 443, 2053, 2083, 2087, 2096, 8443.
- WebSocket bị cắt khi im lặng ~100 s; Host (15 s) và relay (`KeepAliveSeconds`) đã ping đều nên không ảnh hưởng.

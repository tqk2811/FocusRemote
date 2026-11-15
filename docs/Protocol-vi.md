# FocusRemote – Đặc tả giao thức (v1)

Tài liệu này là hợp đồng chung giữa Host (C#), Relay (ASP.NET Core) và WebViewer (JS). Mọi số nguyên đều **little-endian** trừ khi ghi khác.

## 1. Relay

- Host mở WebSocket `wss://<relay>/ws/host/{id}`, viewer mở `wss://<relay>/ws/viewer/{id}`.
- `id`: 3–64 ký tự `[A-Za-z0-9_-]`, so khớp phân biệt hoa thường.
- Relay chỉ ghép cặp và chuyển tiếp **message binary nguyên vẹn** giữa hai đầu. Relay không bao giờ nhận password.
- Message **text** (JSON UTF-8) chỉ do relay gửi, để báo trạng thái:
  - Gửi cho host: `{"type":"registered"}` khi đăng ký xong, `{"type":"peer-joined"}` khi có viewer, `{"type":"peer-left"}` khi viewer rời.
  - Gửi cho viewer: `{"type":"paired"}` khi đã ghép với host, `{"type":"peer-left"}` khi host rời.
  - Host/viewer gửi message text lên relay thì relay bỏ qua.
- Mã đóng WebSocket do relay dùng:
  - `4000` id không hợp lệ.
  - `4004` viewer kết nối mà chưa có host với id đó.
  - `4009` id đã có host khác (host thứ hai bị từ chối) hoặc host đã có viewer (mỗi host một viewer).
  - `4029` vượt rate limit.
- Một trong hai đầu rời thì relay đóng/báo đầu kia (`peer-left`); host giữ kết nối để chờ viewer mới.
- Message binary tối đa 16 MiB.

## 2. Khung binary ngoài cùng

Byte đầu tiên là `kind`:

| kind | Tên | Chiều | Nội dung sau byte kind |
|---|---|---|---|
| `0x01` | HelloViewer | viewer → host | `version u8` (=1), `nonceV` 32B ngẫu nhiên |
| `0x02` | HelloHost | host → viewer | `version u8` (=1), `nonceH` 32B ngẫu nhiên |
| `0x03` | AuthFail | host → viewer | rỗng; sau đó host không trả lời gì thêm cho viewer này |
| `0x10` | Sealed | hai chiều | `counter u64` (8B), `ciphertext + tag 16B` |

## 3. Khoá

- `salt = SHA-256(UTF8("FocusRemote/v1/salt/" + id))[0..16]`.
- `rootKey = PBKDF2-HMAC-SHA256(UTF8(password), salt, 600000 vòng, 32 byte)`. Host tính một lần khi đặt password; viewer tính khi đăng nhập.
- Khoá phiên: `okm = HKDF-SHA256(ikm = rootKey, salt = nonceV ‖ nonceH, info = UTF8("FocusRemote/v1/session"), 64 byte)`.
  - `keyH2V = okm[0..32]` (host → viewer), `keyV2H = okm[32..64]` (viewer → host).
- AES-256-GCM, tag 16 byte. `nonce (12B) = counter (u64 LE) ‖ 00 00 00 00`. `AAD = 1 byte 0x10`.
- Mỗi chiều có `counter` riêng, bắt đầu từ 0, tăng 1 sau mỗi gói. Bên nhận chỉ chấp nhận `counter` lớn hơn gói hợp lệ trước đó (chống phát lại); sai thì đóng phiên.
- Password tối thiểu 10 ký tự (Host và viewer đều kiểm).

## 4. Bắt tay

1. Relay báo `paired` cho viewer và `peer-joined` cho host.
2. Viewer gửi `HelloViewer`.
3. Host gửi `HelloHost`. Cả hai suy ra khoá phiên.
4. Viewer gửi gói Sealed đầu tiên: kênh Control, type `AuthProbe`, payload `UTF8("FocusRemote/v1/probe")`.
5. Host giải mã: lỗi hoặc payload sai → gửi `AuthFail`, bỏ phiên (relay vẫn giữ kết nối viewer; viewer tự đóng). Host chờ tăng dần (1s, 2s, 4s… tối đa 30s) giữa các lần thất bại liên tiếp để chống dò password.
6. Host gửi Control `AuthOk`, ngay sau đó Control `SourceList`.
7. Viewer gửi Control `Capabilities`, rồi `SelectSource` khi người dùng chọn.

Gói Sealed tới trước khi bắt tay xong thì bỏ qua.

## 5. Plaintext bên trong Sealed

`[channel u8][type u8][payload…]`

| channel | Tên |
|---|---|
| 0 | Control |
| 1 | Video |
| 2 | Input |
| 3 | Audio |
| 4–255 | dự trữ cho chức năng sau (bên nhận bỏ qua kênh không biết) |

### 5.1 Control (payload JSON UTF-8, tên trường camelCase, trừ khi ghi khác)

| type | Tên | Chiều | Payload |
|---|---|---|---|
| `0x01` | AuthProbe | V→H | `UTF8("FocusRemote/v1/probe")` (không phải JSON) |
| `0x02` | AuthOk | H→V | `{"hostName":"PC","version":"1.0.0"}` |
| `0x03` | Capabilities | V→H | `{"videoCodecs":["h264"],"audioCodecs":["opus"]}` (giá trị: `h264`, `h265`, `opus`) |
| `0x10` | ListSources | V→H | `{}` – yêu cầu gửi lại danh sách |
| `0x11` | SourceList | H→V | `{"sources":[{"id":"w:1234","kind":"window","title":"Notepad","width":800,"height":600}]}`; `kind` = `window`/`monitor`; `id` window = `w:<hwnd thập phân>`, monitor = `m:<chỉ số>` |
| `0x12` | SelectSource | V→H | `{"id":"w:1234","maxWidth":1920,"maxHeight":1080,"maxFps":30,"bitrateKbps":4000,"audio":true}` |
| `0x13` | StreamStarted | H→V | `{"sourceId":"w:1234","codec":"h264","width":1280,"height":720,"sourceWidth":2560,"sourceHeight":1440}` |
| `0x14` | StopStream | V→H | `{}` |
| `0x15` | StreamStopped | H→V | `{"sourceId":"w:1234","reason":"closed"}` (cửa sổ đóng, lỗi capture…) |
| `0x16` | RequestKeyframe | V→H | `{}` |
| `0x20` | Ping | hai chiều | `u64` mốc thời gian ms của bên gửi (không phải JSON) |
| `0x21` | Pong | hai chiều | trả nguyên 8 byte của Ping |
| `0x30` | Error | H→V | `{"code":"capture_failed","message":"..."}` |

### 5.2 Video (H→V)

- type `0x01` Frame: `flags u8` (bit0 = keyframe), `timestampUs u64`, rồi dữ liệu **H.264 Annex B** (có start code; keyframe kèm SPS/PPS phía trước).
- Encoder: không B-frame, độ trễ thấp. Keyframe đầu tiên ngay khi bắt đầu stream, khi nhận `RequestKeyframe`, và định kỳ (mặc định 5s).
- Host được bỏ khung khi kênh nghẽn (vd hàng đợi gửi đã có > 2 khung chưa gửi xong) và gửi keyframe ở khung tiếp theo.

### 5.3 Input (V→H)

Toạ độ `x`, `y` là `u16` chuẩn hoá theo khung hình đang stream: `0` = mép trái/trên, `65535` = mép phải/dưới. Host quy đổi sang toạ độ màn hình của cửa sổ/màn hình đang xem.

| type | Tên | Payload |
|---|---|---|
| `0x01` | MouseMove | `x u16`, `y u16` |
| `0x02` | MouseButton | `button u8` (0 trái, 1 phải, 2 giữa, 3 X1, 4 X2), `down u8` (1/0), `x u16`, `y u16` |
| `0x03` | MouseWheel | `deltaX i16`, `deltaY i16` (đơn vị WHEEL_DELTA = 120 một nấc; dương = cuộn lên/sang phải), `x u16`, `y u16` |
| `0x04` | Key | `down u8`, `codeLength u8`, `code` ASCII (giá trị `KeyboardEvent.code` của trình duyệt, vd `KeyA`, `Enter`, `ControlLeft`) |
| `0x05` | Text | chuỗi UTF-8 (bàn phím ảo trên điện thoại) → Host gõ bằng `KEYEVENTF_UNICODE` |
| `0x06` | ReleaseAll | rỗng – nhả mọi phím/nút đang giữ (viewer mất focus) |

Khi xem một cửa sổ, Host đưa cửa sổ đó lên trước (`SetForegroundWindow`) trước khi gửi input nếu nó chưa ở trước.

### 5.4 Audio (H→V)

- type `0x01` Config: JSON `{"codec":"opus","sampleRate":48000,"channels":2}`.
- type `0x02` Packet: `timestampUs u64`, rồi một gói Opus thô.

## 6. Vector kiểm thử

`src/FocusRemote.Protocol.Tests/TestVectors.json` chứa vector tính khoá (PBKDF2 đủ 600000 vòng) và gói Sealed mẫu cho mỗi chiều (counter bắt đầu từ 0, liên tiếp). Bản C# và bản JS phải cùng ra đúng các giá trị đó. File được sinh bằng WebCrypto của Node và test C# khẳng định tái tạo đúng.

## 7. Quy ước khi đặc tả chưa nói rõ

- Gói Input/Video/Audio có byte thừa ở cuối thì bên nhận bỏ qua phần thừa (để mở rộng về sau); gói ngắn hơn mức tối thiểu thì bỏ gói đó.
- Bit không dùng trong `flags` của Video Frame bị bỏ qua.
- JSON của Control: trường lạ bị bỏ qua; thiếu trường bắt buộc hoặc JSON hỏng thì bỏ gói, không đóng phiên.
- Độ dài password tính theo số đơn vị UTF-16 (giống `string.length` của JS).

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
| `0x04` | Busy | host → viewer | rỗng; Host đang phục vụ viewer khác, gửi ngay khi viewer tới (trước hoặc sau bắt tay) rồi không trả lời gì thêm; viewer tự đóng (qua relay Host không đóng được socket viewer) |
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
| 4 | Terminal |
| 5–255 | dự trữ cho chức năng sau (bên nhận bỏ qua kênh không biết) |

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
| `0x17` | SetInputMode | V→H | `{"mode":"message"}`; `mode` = `sendinput` (mặc định) hoặc `message`. Chọn cách Host đưa input (§5.3) vào **nguồn cửa sổ**, giữ trong cả phiên viewer (đổi nguồn không mất). Giá trị lạ coi như `sendinput`; viewer cũ không gửi thì là `sendinput`. Viewer gửi ngay sau `AuthOk` và mỗi lần người dùng đổi |
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

Chế độ `sendinput` (mặc định, và luôn dùng cho nguồn màn hình): Host dùng `SendInput`; khi xem một cửa sổ, Host đưa cửa sổ đó lên trước (`SetForegroundWindow`) trước khi gửi input nếu nó chưa ở trước.

Chế độ `message` (Control `SetInputMode`, chỉ áp cho nguồn cửa sổ): Host **không** đưa cửa sổ lên trước và không đụng chuột/bàn phím thật, mà `PostMessage`:

- Chuột: `WM_MOUSEMOVE`, `WM_[L|R|M|X]BUTTONDOWN/UP` tới cửa sổ con sâu nhất dưới điểm (lặp `RealChildWindowFromPoint`, bỏ group box/vùng `HTTRANSPARENT`; con trả về bị ẩn/disabled thì dùng `ChildWindowFromPointEx` bỏ con ẩn/disabled/transparent), `lParam` = toạ độ client của cửa sổ con đó; khi đang giữ nút thì gửi tiếp cho cửa sổ (và vùng) đã nhận lúc nhấn (giống capture). `wParam` = cờ `MK_*` theo nút/Shift/Ctrl đang giữ (Host tự theo dõi). `WM_MOUSEWHEEL`/`WM_MOUSEHWHEEL`: `lParam` là toạ độ **màn hình**, word cao `wParam` là delta.
- Vùng non-client: điểm nằm ngoài vùng client của cửa sổ đích thì Host hỏi `WM_NCHITTEST` (`SendMessageTimeout` 100 ms, `SMTO_ABORTIFHUNG`; hết giờ thì coi như client). Kết quả khác `HTCLIENT`/`HTNOWHERE`/`HTTRANSPARENT`/`HTERROR` thì gửi `WM_NCMOUSEMOVE`/`WM_NC[L|R|M|X]BUTTONDOWN/UP` với `wParam` = mã hit (word cao = XBUTTON với nút X), `lParam` = toạ độ màn hình.
- Double-click: lần nhấn thứ hai cùng nút, cùng cửa sổ, trong `GetDoubleClickTime()` và trong ô `SM_CXDOUBLECLK`×`SM_CYDOUBLECLK` quanh lần nhấn trước thành `WM_xBUTTONDBLCLK` (client: chỉ khi class có `CS_DBLCLKS`) hoặc `WM_NCxBUTTONDBLCLK` (non-client). Lần nhấn thứ ba tính lại từ đầu.
- Phím: `WM_KEYDOWN`/`WM_KEYUP` (`WM_SYSKEYDOWN`/`UP` khi giữ Alt mà không giữ Ctrl) tới control đang focus của luồng GUI cửa sổ (`GetGUIThreadInfo().hwndFocus`, không thuộc cửa sổ thì gửi chính cửa sổ). `lParam`: repeat 1, scan code, bit 24 extended, bit 29 Alt (với SYS), bit 30 trạng thái trước, bit 31 nhả. Phím chữ/số khi đang giữ Ctrl hoặc Alt **không** được gửi `WM_KEYDOWN/UP` (app sẽ gõ ra chữ thay vì chạy phím tắt); Ctrl+chữ (không Alt) chỉ gửi `WM_CHAR` mã điều khiển `0x01`–`0x1A` (vd Ctrl+A → `0x01`), một số edit control chuẩn hiểu được.
- Text: mỗi đơn vị UTF-16 một `WM_CHAR`; `\n` thành `WM_KEYDOWN`+`WM_CHAR '\r'`+`WM_KEYUP` của Enter. Ở chế độ này viewer gửi phím in được (giá trị `KeyboardEvent.key` một code point, không giữ Ctrl/Alt/Meta; AltGr vẫn tính là gõ) dưới dạng `Text` thay vì `Key`; chỉ phím điều khiển và tổ hợp phím đi bằng `Key`.
- `ReleaseAll` (và khi đổi chế độ/đổi cửa sổ): gửi message nhả cho mọi nút/phím đang giữ.
- Chỉ chạy với app đọc input từ message; app dùng raw input/DirectInput/`GetAsyncKeyState`, game, hay tiến trình chạy quyền cao hơn Host (UIPI chặn `PostMessage`) sẽ không nhận. Trạng thái phím hệ thống (`GetKeyState`) không đổi, nên **phím tắt Ctrl/Alt (Ctrl+C, Alt+F4, Ctrl+S…) có thể không chạy**. Nhấn vào vùng non-client như thanh tiêu đề/viền có thể làm `DefWindowProc` vào vòng lặp kéo/đổi cỡ theo chuột thật.

### 5.4 Audio (H→V)

- type `0x01` Config: JSON `{"codec":"opus","sampleRate":48000,"channels":2}`.
- type `0x02` Packet: `timestampUs u64`, rồi một gói Opus thô.

### 5.5 Terminal (hai chiều)

Terminal từ xa: Host chạy shell trong Windows ConPTY (`CreatePseudoConsole`), viewer hiển thị bằng xterm.js. Mặc định **tắt**: chủ máy phải bật "Cho phép terminal" trong Host. `terminalId` (`u32`) do viewer chọn, duy nhất trong phiên. Mỗi phiên tối đa 8 terminal; viewer ngắt kết nối thì Host kill toàn bộ tiến trình của mọi terminal (job object). Viewer chỉ chọn shell theo `id` trong danh sách Host gửi, không gửi được dòng lệnh tuỳ ý.

| type | Tên | Chiều | Payload |
|---|---|---|---|
| `0x01` | ListShells | V→H | `{}` |
| `0x02` | ShellList | H→V | `{"enabled":true,"shells":[{"id":"pwsh","name":"PowerShell 7"}]}`; tắt thì `enabled:false`, `shells:[]`. Host gửi tự động ngay sau `SourceList`. `id` có thể là `cmd`, `powershell`, `pwsh`, `gitbash`, `wsl` (chỉ những shell có trên máy) |
| `0x10` | Open | V→H | `{"terminalId":1,"shellId":"pwsh","cols":120,"rows":30}` |
| `0x11` | Opened | H→V | `{"terminalId":1,"shellId":"pwsh"}` |
| `0x12` | Closed | H→V | `{"terminalId":1,"reason":"exited","exitCode":0,"message":null}`; `reason`: `exited` (shell tự thoát), `closed` (viewer đóng), `disabled`, `shell_not_found`, `start_failed`, `limit`, `duplicate_id` |
| `0x20` | Input | V→H | `terminalId u32`, rồi byte UTF-8 người dùng gõ (gồm chuỗi VT của phím đặc biệt, vd `\x1b[A`) |
| `0x21` | Output | H→V | `terminalId u32`, rồi byte đầu ra của ConPTY (UTF-8 kèm chuỗi VT; một ký tự UTF-8 có thể bị cắt giữa hai gói) |
| `0x22` | Resize | V→H | `terminalId u32`, `cols u16`, `rows u16` (bằng 0 thì bỏ gói) |
| `0x23` | Close | V→H | `terminalId u32`; Host đóng ConPTY, kill tiến trình rồi trả `Closed` với `reason:"closed"` |

Gói Input/Resize/Close cho `terminalId` không tồn tại thì bỏ qua.

## 6. Vector kiểm thử

`src/FocusRemote.Protocol.Tests/TestVectors.json` chứa vector tính khoá (PBKDF2 đủ 600000 vòng) và gói Sealed mẫu cho mỗi chiều (counter bắt đầu từ 0, liên tiếp). Bản C# và bản JS phải cùng ra đúng các giá trị đó. File được sinh bằng WebCrypto của Node và test C# khẳng định tái tạo đúng.

## 7. Quy ước khi đặc tả chưa nói rõ

- Gói Input/Video/Audio/Terminal (nhị phân) có byte thừa ở cuối thì bên nhận bỏ qua phần thừa (để mở rộng về sau); gói ngắn hơn mức tối thiểu thì bỏ gói đó.
- Bit không dùng trong `flags` của Video Frame bị bỏ qua.
- JSON của Control: trường lạ bị bỏ qua; thiếu trường bắt buộc hoặc JSON hỏng thì bỏ gói, không đóng phiên.
- Độ dài password tính theo số đơn vị UTF-16 (giống `string.length` của JS).

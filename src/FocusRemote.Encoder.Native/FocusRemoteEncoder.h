// FocusRemote native encoder: Windows.Graphics.Capture on the encoder D3D11 device -> GPU NV12 -> FFmpeg hardware encoder (Annex B).
// All functions use the cdecl calling convention. Strings are UTF-8, owned by the encoder.
#pragma once
#include <stdint.h>

#ifdef FOCUSREMOTE_ENCODER_EXPORTS
#define FR_API extern "C" __declspec(dllexport)
#else
#define FR_API extern "C" __declspec(dllimport)
#endif
#define FR_CALL __cdecl

typedef struct FrEncoder FrEncoder;
typedef struct FrCapture FrCapture;

enum FrCodec : int32_t
{
	FrCodec_H264 = 0,
	FrCodec_Hevc = 1,
};

#pragma pack(push, 8)
typedef struct FrEncoderOptions
{
	int32_t codec;          // FrCodec
	int32_t width;          // even, > 0
	int32_t height;         // even, > 0
	int32_t fps;            // > 0
	int32_t bitrateKbps;    // > 0
	int32_t gopSeconds;     // <= 0: very long GOP (keyframes only on request)
	const char* encoderName;// optional: force an encoder (e.g. "h264_mf"); null = auto by adapter vendor
} FrEncoderOptions;

typedef struct FrPacket
{
	const uint8_t* data;    // Annex B; owned by the encoder, valid until the next Encode/Open/Destroy
	int32_t size;
	int32_t isKeyframe;
	int64_t pts;            // same unit as timestampUs passed to Encode
} FrPacket;
#pragma pack(pop)

// Allocate an encoder context. Never returns null unless out of memory.
FR_API FrEncoder* FR_CALL FrEncoder_Create();
// Create the D3D11 device on DXGI adapter 0. Returns 0 on success.
FR_API int32_t FR_CALL FrEncoder_Init(FrEncoder* encoder);
// Open (or re-open) the encoder at options->width x options->height. Returns 0 on success.
FR_API int32_t FR_CALL FrEncoder_Open(FrEncoder* encoder, const FrEncoderOptions* options);
// Encode the latest frame of capture (scaled to the encoder size on the GPU). The capture must have been
// initialized with this encoder and FrCapture_HasFrame must be 1.
// Returns 1 when outPacket holds data, 0 when the encoder buffered the frame, < 0 on error.
FR_API int32_t FR_CALL FrEncoder_Encode(FrEncoder* encoder, FrCapture* capture, int32_t forceKeyframe, int64_t timestampUs, FrPacket* outPacket);
FR_API const char* FR_CALL FrEncoder_GetEncoderName(FrEncoder* encoder);
// 1 when frames reach the encoder as D3D11 textures (zero-copy), 0 when the NV12 frame is read back to system memory.
FR_API int32_t FR_CALL FrEncoder_IsZeroCopy(FrEncoder* encoder);
FR_API const char* FR_CALL FrEncoder_GetAdapterName(FrEncoder* encoder);
FR_API uint32_t FR_CALL FrEncoder_GetAdapterVendorId(FrEncoder* encoder);
FR_API const char* FR_CALL FrEncoder_GetLastError(FrEncoder* encoder);
FR_API void FR_CALL FrEncoder_Destroy(FrEncoder* encoder);

// ---------- Screen capture (Windows.Graphics.Capture on the encoder device) ----------
// Not thread-safe: call Poll and FrEncoder_Encode from the same thread. The capture keeps its own reference
// to the encoder's device, so destroy order does not matter.

enum FrCaptureKind : int32_t
{
	FrCaptureKind_Window = 0,  // handle = HWND
	FrCaptureKind_Monitor = 1, // handle = HMONITOR
};

FR_API FrCapture* FR_CALL FrCapture_Create();
// Start capturing on the device of an initialized encoder. Returns 0 on success.
FR_API int32_t FR_CALL FrCapture_Init(FrCapture* capture, FrEncoder* encoder, int32_t kind, void* handle, int32_t showCursor);
// Take the newest pending frame (older ones are dropped). Returns 1 when a new frame arrived, 0 when none, < 0 on error.
// The source only delivers frames when its content changes.
FR_API int32_t FR_CALL FrCapture_Poll(FrCapture* capture);
// Content size of the latest frame (before the first frame: the capture item size).
FR_API void FR_CALL FrCapture_GetSize(FrCapture* capture, int32_t* width, int32_t* height);
// 1 once at least one frame has been received.
FR_API int32_t FR_CALL FrCapture_HasFrame(FrCapture* capture);
// 1 when the captured window/monitor went away.
FR_API int32_t FR_CALL FrCapture_IsClosed(FrCapture* capture);
FR_API const char* FR_CALL FrCapture_GetLastError(FrCapture* capture);
FR_API void FR_CALL FrCapture_Destroy(FrCapture* capture);

// ---------- Opus audio encoder (FFmpeg libopus; fallback FFmpeg native "opus") ----------

typedef struct FrAudioEncoder FrAudioEncoder;

#pragma pack(push, 8)
typedef struct FrAudioEncoderOptions
{
	int32_t sampleRate;      // e.g. 48000
	int32_t channels;        // 1 or 2
	int32_t bitrateKbps;     // > 0
	int32_t frameDurationMs; // 20 recommended (2.5/5/10/20/40/60 for libopus)
} FrAudioEncoderOptions;
#pragma pack(pop)

FR_API FrAudioEncoder* FR_CALL FrAudioEncoder_Create();
// Open (or re-open) with low-delay settings. Returns 0 on success.
FR_API int32_t FR_CALL FrAudioEncoder_Open(FrAudioEncoder* encoder, const FrAudioEncoderOptions* options);
// Samples per channel each Send must provide (e.g. 960 for 20 ms at 48 kHz); 0 before Open.
FR_API int32_t FR_CALL FrAudioEncoder_GetFrameSamples(FrAudioEncoder* encoder);
// Queue one frame of interleaved 32-bit float PCM (exactly GetFrameSamples samples per channel). Returns 0 on success.
FR_API int32_t FR_CALL FrAudioEncoder_Send(FrAudioEncoder* encoder, const float* samples, int32_t samplesPerChannel, int64_t timestampUs);
// Fetch the next packet: 1 when outPacket holds one raw Opus packet (owned by the encoder, valid until the next
// Receive/Open/Destroy), 0 when nothing is ready, < 0 on error. Call until it returns 0 after each Send.
FR_API int32_t FR_CALL FrAudioEncoder_Receive(FrAudioEncoder* encoder, FrPacket* outPacket);
FR_API const char* FR_CALL FrAudioEncoder_GetEncoderName(FrAudioEncoder* encoder);
FR_API const char* FR_CALL FrAudioEncoder_GetLastError(FrAudioEncoder* encoder);
FR_API void FR_CALL FrAudioEncoder_Destroy(FrAudioEncoder* encoder);

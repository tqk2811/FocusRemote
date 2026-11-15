// FocusRemote native encoder: D3D11 shared BGRA texture -> GPU NV12 -> FFmpeg hardware encoder (Annex B).
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
// Create the D3D11 device on DXGI adapter 0 (same adapter as TqkLibrary.WindowCapture). Returns 0 on success.
FR_API int32_t FR_CALL FrEncoder_Init(FrEncoder* encoder);
// Open (or re-open) the encoder and (re)create the shared input surface of options->width x options->height. Returns 0 on success.
FR_API int32_t FR_CALL FrEncoder_Open(FrEncoder* encoder, const FrEncoderOptions* options);
// Shared BGRA input surface (IDXGISurface*), borrowed: NOT AddRef'd, owned by the encoder,
// valid until the next Open or Destroy. Pass it to WinrtGraphicCapture.Render.
FR_API void* FR_CALL FrEncoder_GetInputSurface(FrEncoder* encoder);
// Increments every time Open recreates the surface (use for Render's isNewSurface).
FR_API int32_t FR_CALL FrEncoder_GetSurfaceGeneration(FrEncoder* encoder);
// Encode the current surface content. Returns 1 when outPacket holds data, 0 when the encoder buffered the frame, < 0 on error.
FR_API int32_t FR_CALL FrEncoder_Encode(FrEncoder* encoder, int32_t forceKeyframe, int64_t timestampUs, FrPacket* outPacket);
FR_API const char* FR_CALL FrEncoder_GetEncoderName(FrEncoder* encoder);
// 1 when frames reach the encoder as D3D11 textures (zero-copy), 0 when the NV12 frame is read back to system memory.
FR_API int32_t FR_CALL FrEncoder_IsZeroCopy(FrEncoder* encoder);
FR_API const char* FR_CALL FrEncoder_GetAdapterName(FrEncoder* encoder);
FR_API uint32_t FR_CALL FrEncoder_GetAdapterVendorId(FrEncoder* encoder);
FR_API const char* FR_CALL FrEncoder_GetLastError(FrEncoder* encoder);
FR_API void FR_CALL FrEncoder_Destroy(FrEncoder* encoder);

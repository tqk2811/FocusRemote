// Internal glue between the encoder and the capture (not exported).
#pragma once
#include "FocusRemoteEncoder.h"

// D3D11 device of an initialized encoder (borrowed, not AddRef'd); null before FrEncoder_Init succeeds.
ID3D11Device* FrInternal_EncoderDevice(FrEncoder* encoder);
// Latest captured BGRA texture (exactly content size, DEFAULT usage, on the encoder device); null before the first frame.
ID3D11Texture2D* FrInternal_CaptureTexture(FrCapture* capture);

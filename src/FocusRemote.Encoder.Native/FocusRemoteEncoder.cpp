#include "pch.h"
#include "FrInternal.h"

using Microsoft::WRL::ComPtr;

namespace
{
	constexpr uint32_t VendorNvidia = 0x10DE;
	constexpr uint32_t VendorIntel = 0x8086;
	constexpr uint32_t VendorAmd = 0x1002;

	std::string WideToUtf8(const wchar_t* text)
	{
		int length = WideCharToMultiByte(CP_UTF8, 0, text, -1, nullptr, 0, nullptr, nullptr);
		if (length <= 1)
			return {};
		std::string result(length - 1, '\0');
		WideCharToMultiByte(CP_UTF8, 0, text, -1, result.data(), length, nullptr, nullptr);
		return result;
	}

	std::string AvError(int code)
	{
		char buffer[AV_ERROR_MAX_STRING_SIZE]{};
		av_strerror(code, buffer, sizeof(buffer));
		return buffer;
	}

	std::string HrError(HRESULT hr)
	{
		char buffer[32];
		snprintf(buffer, sizeof(buffer), "HRESULT 0x%08X", static_cast<unsigned>(hr));
		return buffer;
	}

	// Annex B NAL iteration: calls onNal(nalStart (incl. start code), nalEnd, nalType for H.264 / HEVC).
	template<typename TCallback>
	void ForEachNal(const uint8_t* data, size_t size, TCallback onNal)
	{
		auto findStart = [&](size_t from, size_t& codeLength) -> size_t {
			for (size_t i = from; i + 3 <= size; ++i)
			{
				if (data[i] == 0 && data[i + 1] == 0)
				{
					if (data[i + 2] == 1) { codeLength = 3; return i; }
					if (i + 4 <= size && data[i + 2] == 0 && data[i + 3] == 1) { codeLength = 4; return i; }
				}
			}
			return size;
		};
		size_t codeLength = 0;
		size_t start = findStart(0, codeLength);
		while (start < size)
		{
			size_t nextCodeLength = 0;
			size_t next = findStart(start + codeLength, nextCodeLength);
			if (start + codeLength < size)
				onNal(data + start, data + next, data[start + codeLength]);
			start = next;
			codeLength = nextCodeLength;
		}
	}
}

struct FrEncoder
{
public:
	~FrEncoder()
	{
		Close();
		av_buffer_unref(&_hwDevice);
	}

	int32_t Init()
	{
		if (_device)
			return 0;

		ComPtr<IDXGIFactory1> factory;
		HRESULT hr = CreateDXGIFactory1(IID_PPV_ARGS(factory.GetAddressOf()));
		if (FAILED(hr))
			return Fail("CreateDXGIFactory1 failed: " + HrError(hr));
		// Adapter 0 = the adapter D3D11CreateDevice(nullptr) picks. Capture runs on this same device
		// (FrCapture), Windows.Graphics.Capture handles a source shown on another adapter.
		ComPtr<IDXGIAdapter1> adapter;
		hr = factory->EnumAdapters1(0, adapter.GetAddressOf());
		if (FAILED(hr))
			return Fail("EnumAdapters1(0) failed: " + HrError(hr));
		DXGI_ADAPTER_DESC1 adapterDesc{};
		adapter->GetDesc1(&adapterDesc);
		_adapterName = WideToUtf8(adapterDesc.Description);
		_vendorId = adapterDesc.VendorId;

		const D3D_FEATURE_LEVEL featureLevels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
		hr = D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
			D3D11_CREATE_DEVICE_VIDEO_SUPPORT | D3D11_CREATE_DEVICE_BGRA_SUPPORT,
			featureLevels, ARRAYSIZE(featureLevels), D3D11_SDK_VERSION,
			_device.GetAddressOf(), nullptr, _context.GetAddressOf());
		if (FAILED(hr))
			return Fail("D3D11CreateDevice failed: " + HrError(hr));

		ComPtr<ID3D10Multithread> multithread;
		if (SUCCEEDED(_context.As(&multithread)))
			multithread->SetMultithreadProtected(TRUE);

		hr = _device.As(&_videoDevice);
		if (SUCCEEDED(hr))
			hr = _context.As(&_videoContext);
		if (FAILED(hr))
			return Fail("ID3D11VideoDevice not available: " + HrError(hr));

		_hwDevice = av_hwdevice_ctx_alloc(AV_HWDEVICE_TYPE_D3D11VA);
		if (!_hwDevice)
			return Fail("av_hwdevice_ctx_alloc failed");
		auto deviceContext = reinterpret_cast<AVHWDeviceContext*>(_hwDevice->data);
		auto d3d11Context = static_cast<AVD3D11VADeviceContext*>(deviceContext->hwctx);
		// FFmpeg takes ownership of one reference each.
		_device->AddRef();
		_context->AddRef();
		d3d11Context->device = _device.Get();
		d3d11Context->device_context = _context.Get();
		int ret = av_hwdevice_ctx_init(_hwDevice);
		if (ret < 0)
		{
			av_buffer_unref(&_hwDevice);
			return Fail("av_hwdevice_ctx_init failed: " + AvError(ret));
		}
		return 0;
	}

	int32_t Open(const FrEncoderOptions& options)
	{
		if (!_device)
			return Fail("FrEncoder_Init has not succeeded");
		if (options.width <= 0 || options.height <= 0 || (options.width & 1) || (options.height & 1))
			return Fail("width/height must be positive and even");
		if (options.fps <= 0 || options.bitrateKbps <= 0)
			return Fail("fps and bitrateKbps must be positive");

		Close();
		_options = options;

		int32_t result = CreateVideoProcessor();
		if (result != 0)
			return result;

		std::string errors;
		for (const std::string& name : CandidateEncoders())
		{
			if (OpenCodec(name))
				return 0;
			bool triedZeroCopy = _isZeroCopy;
			errors += name + ": " + _lastError + "; ";
			CloseCodec();
			// D3D11/QSV frame setup failed: the same encoder may still work with system-memory frames.
			if (triedZeroCopy)
			{
				if (OpenCodec(name, true))
					return 0;
				errors += name + " (system memory): " + _lastError + "; ";
				CloseCodec();
			}
		}
		return Fail("no encoder could be opened (" + errors + ")");
	}

	ID3D11Device* Device() const { return _device.Get(); }

	int32_t Encode(FrCapture* capture, bool forceKeyframe, int64_t timestampUs, FrPacket* outPacket)
	{
		*outPacket = {};
		if (!_codec)
			return Fail("encoder is not open");
		ID3D11Texture2D* texture = FrInternal_CaptureTexture(capture);
		if (!texture)
			return Fail("capture has no frame yet");
		if (SetInputTexture(texture) != 0)
			return -1;

		int32_t result = EncodeOnce(forceKeyframe, timestampUs, outPacket);
		if (result < 0 && _framesSent == 0 && _isZeroCopy)
		{
			// Some encoders (h264_mf on a software MFT: "Failed to set D3D manager") accept D3D11 frames
			// at open time but fail on the first frame. Re-open the same encoder with system-memory frames.
			std::string name = _encoderName;
			std::string zeroCopyError = _lastError;
			CloseCodec();
			if (!OpenCodec(name, true))
				return Fail(name + " failed with D3D11 frames (" + zeroCopyError + ") and with system-memory frames (" + _lastError + ")");
			result = EncodeOnce(forceKeyframe, timestampUs, outPacket);
		}
		return result;
	}

	const char* EncoderName() const { return _encoderName.c_str(); }
	bool IsZeroCopy() const { return _isZeroCopy; }
	const char* AdapterName() const { return _adapterName.c_str(); }
	uint32_t VendorId() const { return _vendorId; }
	const char* LastError() const { return _lastError.c_str(); }

private:
	int32_t EncodeOnce(bool forceKeyframe, int64_t timestampUs, FrPacket* outPacket)
	{
		AVFrame* frame = av_frame_alloc();
		if (!frame)
			return Fail("av_frame_alloc failed");
		int32_t result = !_isZeroCopy ? FillSoftwareFrame(frame) : _isQsv ? FillQsvFrame(frame) : FillHardwareFrame(frame);
		if (result != 0)
		{
			av_frame_free(&frame);
			return result;
		}

		frame->pts = timestampUs;
		if (forceKeyframe)
		{
			frame->pict_type = AV_PICTURE_TYPE_I;
			frame->flags |= AV_FRAME_FLAG_KEY;
		}
		int ret = avcodec_send_frame(_codec, frame);
		av_frame_free(&frame);
		if (ret < 0)
			return Fail("avcodec_send_frame failed: " + AvError(ret));
		++_framesSent;
		return Drain(outPacket);
	}

	int32_t Fail(std::string message)
	{
		_lastError = std::move(message);
		return -1;
	}

	void Lock()
	{
		auto hwctx = static_cast<AVD3D11VADeviceContext*>(reinterpret_cast<AVHWDeviceContext*>(_hwDevice->data)->hwctx);
		hwctx->lock(hwctx->lock_ctx);
	}
	void Unlock()
	{
		auto hwctx = static_cast<AVD3D11VADeviceContext*>(reinterpret_cast<AVHWDeviceContext*>(_hwDevice->data)->hwctx);
		hwctx->unlock(hwctx->lock_ctx);
	}

	std::vector<std::string> CandidateEncoders() const
	{
		std::vector<std::string> names;
		const char* prefix = _options.codec == FrCodec_Hevc ? "hevc" : "h264";
		if (_options.encoderName && *_options.encoderName)
		{
			names.emplace_back(_options.encoderName);
			return names;
		}
		switch (_vendorId)
		{
		case VendorNvidia: names.push_back(std::string(prefix) + "_nvenc"); break;
		case VendorIntel: names.push_back(std::string(prefix) + "_qsv"); break;
		case VendorAmd: names.push_back(std::string(prefix) + "_amf"); break;
		}
		names.push_back(std::string(prefix) + "_mf");
		return names;
	}

	// Input view for the capture texture; the capture creates a new texture object when the source size changes.
	int32_t SetInputTexture(ID3D11Texture2D* texture)
	{
		if (_inputTexture.Get() == texture && _inputView)
			return 0;
		_inputView.Reset();
		_inputTexture = texture;
		D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC inputDesc{};
		inputDesc.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
		HRESULT hr = _videoDevice->CreateVideoProcessorInputView(texture, _vpEnumerator.Get(), &inputDesc, _inputView.ReleaseAndGetAddressOf());
		if (FAILED(hr))
		{
			_inputTexture.Reset();
			return Fail("CreateVideoProcessorInputView failed: " + HrError(hr));
		}
		D3D11_TEXTURE2D_DESC desc{};
		texture->GetDesc(&desc);
		// Whole capture frame -> whole output (the host keeps the aspect ratio when choosing the encoder size).
		RECT source{ 0, 0, static_cast<LONG>(desc.Width), static_cast<LONG>(desc.Height) };
		RECT target{ 0, 0, _options.width, _options.height };
		_videoContext->VideoProcessorSetStreamSourceRect(_videoProcessor.Get(), 0, TRUE, &source);
		_videoContext->VideoProcessorSetStreamDestRect(_videoProcessor.Get(), 0, TRUE, &target);
		_videoContext->VideoProcessorSetOutputTargetRect(_videoProcessor.Get(), TRUE, &target);
		return 0;
	}

	int32_t CreateVideoProcessor()
	{
		// Input size is only a hint: the capture size may differ and the processor scales it.
		D3D11_VIDEO_PROCESSOR_CONTENT_DESC contentDesc{};
		contentDesc.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
		contentDesc.InputWidth = _options.width;
		contentDesc.InputHeight = _options.height;
		contentDesc.OutputWidth = _options.width;
		contentDesc.OutputHeight = _options.height;
		contentDesc.InputFrameRate = { static_cast<UINT>(_options.fps), 1 };
		contentDesc.OutputFrameRate = { static_cast<UINT>(_options.fps), 1 };
		contentDesc.Usage = D3D11_VIDEO_USAGE_OPTIMAL_SPEED;
		HRESULT hr = _videoDevice->CreateVideoProcessorEnumerator(&contentDesc, _vpEnumerator.ReleaseAndGetAddressOf());
		if (SUCCEEDED(hr))
			hr = _videoDevice->CreateVideoProcessor(_vpEnumerator.Get(), 0, _videoProcessor.ReleaseAndGetAddressOf());
		if (FAILED(hr))
			return Fail("CreateVideoProcessor failed: " + HrError(hr));

		// Full-range RGB in, BT.709 limited-range YCbCr out (matches the color tags set on the encoder).
		D3D11_VIDEO_PROCESSOR_COLOR_SPACE inputColor{};
		inputColor.RGB_Range = 0;
		D3D11_VIDEO_PROCESSOR_COLOR_SPACE outputColor{};
		outputColor.YCbCr_Matrix = 1;
		outputColor.Nominal_Range = D3D11_VIDEO_PROCESSOR_NOMINAL_RANGE_16_235;
		_videoContext->VideoProcessorSetStreamColorSpace(_videoProcessor.Get(), 0, &inputColor);
		_videoContext->VideoProcessorSetOutputColorSpace(_videoProcessor.Get(), &outputColor);
		_videoContext->VideoProcessorSetStreamFrameFormat(_videoProcessor.Get(), 0, D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE);
		_videoContext->VideoProcessorSetStreamAutoProcessingMode(_videoProcessor.Get(), 0, FALSE);
		return 0;
	}

	bool SupportsD3D11Frames(const AVCodec* codec) const
	{
		const void* configs = nullptr;
		int count = 0;
		if (avcodec_get_supported_config(nullptr, codec, AV_CODEC_CONFIG_PIX_FORMAT, 0, &configs, &count) < 0 || !configs)
			return false;
		auto formats = static_cast<const AVPixelFormat*>(configs);
		for (int i = 0; i < count; ++i)
			if (formats[i] == AV_PIX_FMT_D3D11)
				return true;
		return false;
	}

	void ApplyLowLatencyOptions(const std::string& name, AVDictionary** dict) const
	{
		// Called after _isZeroCopy is decided.
		if (name.ends_with("_nvenc"))
		{
			av_dict_set(dict, "preset", "p1", 0);
			av_dict_set(dict, "tune", "ull", 0);
			av_dict_set(dict, "zerolatency", "1", 0);
			// VBR capped by rc_max_rate: CBR pads static screens with filler data up to the full bitrate.
			av_dict_set(dict, "rc", "vbr", 0);
			// Constant quality inside the VBR cap (bit_rate is 0 for nvenc, see OpenCodec): static frames shrink to a few KB.
			av_dict_set(dict, "cq", "28", 0);
			av_dict_set(dict, "delay", "0", 0);
			av_dict_set(dict, "forced-idr", "1", 0);
		}
		else if (name.ends_with("_qsv"))
		{
			av_dict_set(dict, "preset", "veryfast", 0);
			av_dict_set(dict, "async_depth", "1", 0);
			av_dict_set(dict, "look_ahead", "0", 0);
			av_dict_set(dict, "forced_idr", "1", 0);
		}
		else if (name.ends_with("_amf"))
		{
			av_dict_set(dict, "usage", "ultralowlatency", 0);
			av_dict_set(dict, "quality", "speed", 0);
			av_dict_set(dict, "rc", "cbr", 0);
			av_dict_set(dict, "header_insertion_mode", "idr", 0);
		}
		else if (name.ends_with("_mf"))
		{
			av_dict_set(dict, "rate_control", "cbr", 0);
			av_dict_set(dict, "scenario", "display_remoting", 0);
			// D3D11 input needs a hardware MFT; the default software MFT rejects the D3D manager.
			if (_isZeroCopy)
				av_dict_set(dict, "hw_encoding", "1", 0);
		}
	}

	bool OpenCodec(const std::string& name, bool forceSystemMemoryFrames = false)
	{
		const AVCodec* codec = avcodec_find_encoder_by_name(name.c_str());
		if (!codec)
		{
			Fail("not found in this FFmpeg build");
			return false;
		}
		_codec = avcodec_alloc_context3(codec);
		if (!_codec)
		{
			Fail("avcodec_alloc_context3 failed");
			return false;
		}
		_codec->width = _options.width;
		_codec->height = _options.height;
		_codec->time_base = { 1, 1000000 };
		_codec->framerate = { _options.fps, 1 };
		_codec->bit_rate = static_cast<int64_t>(_options.bitrateKbps) * 1000;
		_codec->rc_max_rate = _codec->bit_rate;
		_codec->rc_buffer_size = static_cast<int>(_codec->bit_rate / _options.fps); // ~1 frame VBV
		// nvenc: no average target so "cq" drives quality; rc_max_rate still caps bursts.
		if (name.ends_with("_nvenc"))
			_codec->bit_rate = 0;
		_codec->gop_size = _options.gopSeconds > 0 ? _options.gopSeconds * _options.fps : _options.fps * 3600;
		_codec->max_b_frames = 0;
		_codec->color_range = AVCOL_RANGE_MPEG;
		_codec->colorspace = AVCOL_SPC_BT709;
		_codec->color_primaries = AVCOL_PRI_BT709;
		_codec->color_trc = AVCOL_TRC_BT709;
		_codec->flags |= AV_CODEC_FLAG_LOW_DELAY; // no GLOBAL_HEADER: SPS/PPS stay in-band (Annex B)

		// h264_qsv/hevc_qsv only take nv12 or qsv frames: zero-copy maps our D3D11 frames into a QSV
		// frames context derived from them. That needs the D3D11 device on an Intel adapter; on another
		// adapter QSV opens its own Intel device and we feed it system-memory frames.
		_isQsv = name.ends_with("_qsv");
		_isZeroCopy = !forceSystemMemoryFrames && (_isQsv ? _vendorId == VendorIntel : SupportsD3D11Frames(codec));
		if (_isZeroCopy)
		{
			// Pool size 0 = one texture per pool entry (no texture array): NV12 texture arrays with
			// BIND_RENDER_TARGET fail with E_INVALIDARG (seen on NVIDIA RTX 4060 and Intel UHD).
			// QSV derives from such a dynamic pool fine.
			if (!CreateD3D11Frames())
				return false;
			if (_isQsv)
			{
				if (!CreateQsvFrames())
					return false;
				_codec->pix_fmt = AV_PIX_FMT_QSV;
				_codec->hw_frames_ctx = av_buffer_ref(_qsvFrames);
			}
			else
			{
				_codec->pix_fmt = AV_PIX_FMT_D3D11;
				_codec->hw_frames_ctx = av_buffer_ref(_hwFrames);
			}
		}
		else
		{
			_codec->pix_fmt = AV_PIX_FMT_NV12;
			if (CreateSoftwarePath() != 0)
				return false;
		}

		AVDictionary* dict = nullptr;
		ApplyLowLatencyOptions(name, &dict);
		int ret = avcodec_open2(_codec, codec, &dict);
		av_dict_free(&dict);
		if (ret < 0)
		{
			Fail("avcodec_open2 failed: " + AvError(ret));
			return false;
		}
		_encoderName = name;
		return true;
	}

	bool CreateD3D11Frames()
	{
		_hwFrames = av_hwframe_ctx_alloc(_hwDevice);
		if (!_hwFrames)
		{
			Fail("av_hwframe_ctx_alloc failed");
			return false;
		}
		auto framesContext = reinterpret_cast<AVHWFramesContext*>(_hwFrames->data);
		framesContext->format = AV_PIX_FMT_D3D11;
		framesContext->sw_format = AV_PIX_FMT_NV12;
		framesContext->width = _options.width;
		framesContext->height = _options.height;
		framesContext->initial_pool_size = 0;
		static_cast<AVD3D11VAFramesContext*>(framesContext->hwctx)->BindFlags = D3D11_BIND_RENDER_TARGET;
		int ret = av_hwframe_ctx_init(_hwFrames);
		if (ret < 0)
		{
			Fail("av_hwframe_ctx_init failed: " + AvError(ret));
			return false;
		}
		return true;
	}

	bool CreateQsvFrames()
	{
		AVBufferRef* qsvDevice = nullptr;
		int ret = av_hwdevice_ctx_create_derived(&qsvDevice, AV_HWDEVICE_TYPE_QSV, _hwDevice, 0);
		if (ret < 0)
		{
			Fail("deriving QSV device from D3D11 failed: " + AvError(ret));
			return false;
		}
		ret = av_hwframe_ctx_create_derived(&_qsvFrames, AV_PIX_FMT_QSV, qsvDevice, _hwFrames, 0);
		av_buffer_unref(&qsvDevice); // the frames context keeps its own reference
		if (ret < 0)
		{
			Fail("deriving QSV frames from D3D11 failed: " + AvError(ret));
			return false;
		}
		return true;
	}

	int32_t CreateSoftwarePath()
	{
		D3D11_TEXTURE2D_DESC desc{};
		desc.Width = _options.width;
		desc.Height = _options.height;
		desc.MipLevels = 1;
		desc.ArraySize = 1;
		desc.Format = DXGI_FORMAT_NV12;
		desc.SampleDesc.Count = 1;
		desc.Usage = D3D11_USAGE_DEFAULT;
		desc.BindFlags = D3D11_BIND_RENDER_TARGET;
		HRESULT hr = _device->CreateTexture2D(&desc, nullptr, _nv12Texture.ReleaseAndGetAddressOf());
		if (FAILED(hr))
			return Fail("CreateTexture2D(NV12) failed: " + HrError(hr));
		desc.Usage = D3D11_USAGE_STAGING;
		desc.BindFlags = 0;
		desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
		hr = _device->CreateTexture2D(&desc, nullptr, _nv12Staging.ReleaseAndGetAddressOf());
		if (FAILED(hr))
			return Fail("CreateTexture2D(NV12 staging) failed: " + HrError(hr));
		return 0;
	}

	ID3D11VideoProcessorOutputView* OutputView(ID3D11Texture2D* texture, UINT arrayIndex)
	{
		auto key = std::make_pair(texture, arrayIndex);
		auto found = _outputViews.find(key);
		if (found != _outputViews.end())
			return found->second.Get();

		D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC desc{};
		D3D11_TEXTURE2D_DESC textureDesc{};
		texture->GetDesc(&textureDesc);
		if (textureDesc.ArraySize > 1)
		{
			desc.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2DARRAY;
			desc.Texture2DArray.FirstArraySlice = arrayIndex;
			desc.Texture2DArray.ArraySize = 1;
		}
		else
		{
			desc.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
		}
		ComPtr<ID3D11VideoProcessorOutputView> view;
		HRESULT hr = _videoDevice->CreateVideoProcessorOutputView(texture, _vpEnumerator.Get(), &desc, view.GetAddressOf());
		if (FAILED(hr))
		{
			Fail("CreateVideoProcessorOutputView failed: " + HrError(hr));
			return nullptr;
		}
		_outputViews.emplace(key, view);
		return view.Get();
	}

	int32_t Convert(ID3D11Texture2D* target, UINT arrayIndex)
	{
		ID3D11VideoProcessorOutputView* outputView = OutputView(target, arrayIndex);
		if (!outputView)
			return -1;
		D3D11_VIDEO_PROCESSOR_STREAM stream{};
		stream.Enable = TRUE;
		stream.pInputSurface = _inputView.Get();
		Lock();
		HRESULT hr = _videoContext->VideoProcessorBlt(_videoProcessor.Get(), outputView, 0, 1, &stream);
		Unlock();
		if (FAILED(hr))
			return Fail("VideoProcessorBlt failed: " + HrError(hr));
		return 0;
	}

	int32_t FillHardwareFrame(AVFrame* frame)
	{
		int ret = av_hwframe_get_buffer(_hwFrames, frame, 0);
		if (ret < 0)
			return Fail("av_hwframe_get_buffer failed: " + AvError(ret));
		auto texture = reinterpret_cast<ID3D11Texture2D*>(frame->data[0]);
		auto arrayIndex = static_cast<UINT>(reinterpret_cast<intptr_t>(frame->data[1]));
		return Convert(texture, arrayIndex);
	}

	// D3D11 frame -> QSV frame over the same texture (the mapped frame keeps the D3D11 frame alive).
	int32_t FillQsvFrame(AVFrame* frame)
	{
		AVFrame* d3d11Frame = av_frame_alloc();
		if (!d3d11Frame)
			return Fail("av_frame_alloc failed");
		int32_t result = FillHardwareFrame(d3d11Frame);
		if (result == 0)
		{
			frame->format = AV_PIX_FMT_QSV;
			frame->hw_frames_ctx = av_buffer_ref(_qsvFrames);
			int ret = frame->hw_frames_ctx ? av_hwframe_map(frame, d3d11Frame, AV_HWFRAME_MAP_READ) : AVERROR(ENOMEM);
			if (ret < 0)
				result = Fail("av_hwframe_map(D3D11 -> QSV) failed: " + AvError(ret));
		}
		av_frame_free(&d3d11Frame);
		return result;
	}

	int32_t FillSoftwareFrame(AVFrame* frame)
	{
		if (Convert(_nv12Texture.Get(), 0) != 0)
			return -1;
		frame->format = AV_PIX_FMT_NV12;
		frame->width = _options.width;
		frame->height = _options.height;
		int ret = av_frame_get_buffer(frame, 0);
		if (ret < 0)
			return Fail("av_frame_get_buffer failed: " + AvError(ret));

		Lock();
		_context->CopyResource(_nv12Staging.Get(), _nv12Texture.Get());
		D3D11_MAPPED_SUBRESOURCE mapped{};
		HRESULT hr = _context->Map(_nv12Staging.Get(), 0, D3D11_MAP_READ, 0, &mapped);
		if (SUCCEEDED(hr))
		{
			auto source = static_cast<const uint8_t*>(mapped.pData);
			av_image_copy_plane(frame->data[0], frame->linesize[0], source, mapped.RowPitch, _options.width, _options.height);
			av_image_copy_plane(frame->data[1], frame->linesize[1], source + static_cast<size_t>(mapped.RowPitch) * _options.height,
				mapped.RowPitch, _options.width, _options.height / 2);
			_context->Unmap(_nv12Staging.Get(), 0);
		}
		Unlock();
		if (FAILED(hr))
			return Fail("Map(NV12 staging) failed: " + HrError(hr));
		return 0;
	}

	int32_t Drain(FrPacket* outPacket)
	{
		_packetData.clear();
		bool isKeyframe = false;
		int64_t pts = 0;
		AVPacket* packet = av_packet_alloc();
		while (true)
		{
			int ret = avcodec_receive_packet(_codec, packet);
			if (ret == AVERROR(EAGAIN) || ret == AVERROR_EOF)
				break;
			if (ret < 0)
			{
				av_packet_free(&packet);
				return Fail("avcodec_receive_packet failed: " + AvError(ret));
			}
			bool packetKey = (packet->flags & AV_PKT_FLAG_KEY) != 0;
			if (packetKey)
				EnsureParameterSets(packet->data, packet->size);
			if (_packetData.empty())
				pts = packet->pts;
			_packetData.insert(_packetData.end(), packet->data, packet->data + packet->size);
			isKeyframe |= packetKey;
			av_packet_unref(packet);
		}
		av_packet_free(&packet);
		if (_packetData.empty())
			return 0;
		outPacket->data = _packetData.data();
		outPacket->size = static_cast<int32_t>(_packetData.size());
		outPacket->isKeyframe = isKeyframe ? 1 : 0;
		outPacket->pts = pts;
		return 1;
	}

	// Remembers SPS/PPS(/VPS) from keyframes; prepends them to a keyframe that lacks them,
	// so every keyframe is self-contained for a viewer joining mid-stream.
	void EnsureParameterSets(const uint8_t* data, int size)
	{
		bool isHevc = _options.codec == FrCodec_Hevc;
		std::vector<uint8_t> parameterSets;
		ForEachNal(data, static_cast<size_t>(size), [&](const uint8_t* begin, const uint8_t* end, uint8_t header) {
			int type = isHevc ? (header >> 1) & 0x3F : header & 0x1F;
			bool isParameterSet = isHevc ? (type >= 32 && type <= 34) : (type == 7 || type == 8);
			if (isParameterSet)
				parameterSets.insert(parameterSets.end(), begin, end);
		});
		if (!parameterSets.empty())
			_parameterSets = std::move(parameterSets);
		else if (!_parameterSets.empty())
			_packetData.insert(_packetData.end(), _parameterSets.begin(), _parameterSets.end());
	}

	void CloseCodec()
	{
		avcodec_free_context(&_codec);
		av_buffer_unref(&_qsvFrames);
		av_buffer_unref(&_hwFrames);
		_isQsv = false;
		_outputViews.clear();
		_nv12Texture.Reset();
		_nv12Staging.Reset();
		_encoderName.clear();
		_isZeroCopy = false;
		_framesSent = 0;
		_parameterSets.clear();
	}

	void Close()
	{
		CloseCodec();
		_inputView.Reset();
		_videoProcessor.Reset();
		_vpEnumerator.Reset();
		_inputTexture.Reset();
	}

	ComPtr<ID3D11Device> _device;
	ComPtr<ID3D11DeviceContext> _context;
	ComPtr<ID3D11VideoDevice> _videoDevice;
	ComPtr<ID3D11VideoContext> _videoContext;
	AVBufferRef* _hwDevice = nullptr;
	std::string _adapterName;
	uint32_t _vendorId = 0;

	FrEncoderOptions _options{};
	ComPtr<ID3D11Texture2D> _inputTexture; // capture texture the input view was created for
	ComPtr<ID3D11VideoProcessorEnumerator> _vpEnumerator;
	ComPtr<ID3D11VideoProcessor> _videoProcessor;
	ComPtr<ID3D11VideoProcessorInputView> _inputView;
	std::map<std::pair<ID3D11Texture2D*, UINT>, ComPtr<ID3D11VideoProcessorOutputView>> _outputViews;

	AVCodecContext* _codec = nullptr;
	AVBufferRef* _hwFrames = nullptr;  // D3D11 NV12 frames the video processor writes into
	AVBufferRef* _qsvFrames = nullptr; // QSV frames derived from _hwFrames (QSV zero-copy only)
	bool _isQsv = false;
	bool _isZeroCopy = false;
	int64_t _framesSent = 0;
	std::string _encoderName;
	ComPtr<ID3D11Texture2D> _nv12Texture;
	ComPtr<ID3D11Texture2D> _nv12Staging;

	std::vector<uint8_t> _packetData;
	std::vector<uint8_t> _parameterSets;
	std::string _lastError;
};

FR_API FrEncoder* FR_CALL FrEncoder_Create()
{
	return new (std::nothrow) FrEncoder();
}

FR_API int32_t FR_CALL FrEncoder_Init(FrEncoder* encoder)
{
	return encoder ? encoder->Init() : -1;
}

FR_API int32_t FR_CALL FrEncoder_Open(FrEncoder* encoder, const FrEncoderOptions* options)
{
	return encoder && options ? encoder->Open(*options) : -1;
}

FR_API int32_t FR_CALL FrEncoder_Encode(FrEncoder* encoder, FrCapture* capture, int32_t forceKeyframe, int64_t timestampUs, FrPacket* outPacket)
{
	return encoder && outPacket ? encoder->Encode(capture, forceKeyframe != 0, timestampUs, outPacket) : -1;
}

ID3D11Device* FrInternal_EncoderDevice(FrEncoder* encoder)
{
	return encoder ? encoder->Device() : nullptr;
}

FR_API const char* FR_CALL FrEncoder_GetEncoderName(FrEncoder* encoder)
{
	return encoder ? encoder->EncoderName() : "";
}

FR_API int32_t FR_CALL FrEncoder_IsZeroCopy(FrEncoder* encoder)
{
	return encoder && encoder->IsZeroCopy() ? 1 : 0;
}

FR_API const char* FR_CALL FrEncoder_GetAdapterName(FrEncoder* encoder)
{
	return encoder ? encoder->AdapterName() : "";
}

FR_API uint32_t FR_CALL FrEncoder_GetAdapterVendorId(FrEncoder* encoder)
{
	return encoder ? encoder->VendorId() : 0;
}

FR_API const char* FR_CALL FrEncoder_GetLastError(FrEncoder* encoder)
{
	return encoder ? encoder->LastError() : "encoder is null";
}

FR_API void FR_CALL FrEncoder_Destroy(FrEncoder* encoder)
{
	delete encoder;
}

#include "pch.h"
#include "FrInternal.h"

using Microsoft::WRL::ComPtr;
namespace wgc = winrt::Windows::Graphics::Capture;
namespace wgd = winrt::Windows::Graphics::DirectX;
namespace wgd3d = winrt::Windows::Graphics::DirectX::Direct3D11;

namespace
{
	std::string HrText(HRESULT hr)
	{
		char buffer[32];
		snprintf(buffer, sizeof(buffer), "HRESULT 0x%08X", static_cast<unsigned>(hr));
		return buffer;
	}

	bool IsPropertyPresent(const wchar_t* name)
	{
		try
		{
			return winrt::Windows::Foundation::Metadata::ApiInformation::IsPropertyPresent(L"Windows.Graphics.Capture.GraphicsCaptureSession", name);
		}
		catch (...)
		{
			return false;
		}
	}
}

// Windows.Graphics.Capture on the encoder's own D3D11 device: no cross-device sharing, no CPU round trip.
// Frames are pulled with TryGetNextFrame on the caller's thread (no FrameArrived callback), so every use of
// the immediate context stays on that one thread.
struct FrCapture
{
public:
	~FrCapture()
	{
		Close();
	}

	int32_t Init(FrEncoder* encoder, int32_t kind, void* handle, bool showCursor)
	{
		ID3D11Device* device = FrInternal_EncoderDevice(encoder);
		if (!device)
			return Fail("encoder is not initialized");
		if (!handle)
			return Fail("handle is null");
		Close();
		try
		{
			try
			{
				winrt::init_apartment(winrt::apartment_type::multi_threaded);
			}
			catch (const winrt::hresult_error&)
			{
				// Already initialized (possibly as STA): WinRT is usable either way.
			}
			if (!wgc::GraphicsCaptureSession::IsSupported())
				return Fail("Windows.Graphics.Capture is not supported on this system");

			_device = device;
			_device->GetImmediateContext(_context.ReleaseAndGetAddressOf());

			auto interop = winrt::get_activation_factory<wgc::GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
			wgc::GraphicsCaptureItem item{ nullptr };
			HRESULT hr = kind == FrCaptureKind_Window
				? interop->CreateForWindow(static_cast<HWND>(handle), winrt::guid_of<wgc::GraphicsCaptureItem>(), winrt::put_abi(item))
				: interop->CreateForMonitor(static_cast<HMONITOR>(handle), winrt::guid_of<wgc::GraphicsCaptureItem>(), winrt::put_abi(item));
			if (FAILED(hr))
				return Fail("GraphicsCaptureItem create failed: " + HrText(hr));
			_item = item;
			// The handler may still run on another thread after revoke: it owns the flag, not `this`.
			_closedToken = _item.Closed([closed = _closed](auto&&, auto&&) { *closed = true; });

			ComPtr<IDXGIDevice> dxgiDevice;
			hr = _device.As(&dxgiDevice);
			if (FAILED(hr))
				return Fail("IDXGIDevice not available: " + HrText(hr));
			winrt::com_ptr<::IInspectable> inspectable;
			hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.Get(), inspectable.put());
			if (FAILED(hr))
				return Fail("CreateDirect3D11DeviceFromDXGIDevice failed: " + HrText(hr));
			_winrtDevice = inspectable.as<wgd3d::IDirect3DDevice>();

			_poolSize = _item.Size();
			_size = _poolSize;
			_framePool = wgc::Direct3D11CaptureFramePool::CreateFreeThreaded(
				_winrtDevice, wgd::DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, _poolSize);
			_session = _framePool.CreateCaptureSession(_item);
			if (IsPropertyPresent(L"IsCursorCaptureEnabled"))
				_session.IsCursorCaptureEnabled(showCursor);
			if (IsPropertyPresent(L"IsBorderRequired"))
			{
				try
				{
					_session.IsBorderRequired(false);
				}
				catch (const winrt::hresult_error&)
				{
					// Borderless capture not granted: keep the yellow border.
				}
			}
			_session.StartCapture();
			return 0;
		}
		catch (const winrt::hresult_error& error)
		{
			return Fail("capture init failed: " + HrText(error.code()) + " " + winrt::to_string(error.message()));
		}
	}

	// Takes the newest pending frame (drops older ones). Returns 1 when a new frame was copied, 0 when none, < 0 on error.
	int32_t Poll()
	{
		if (!_framePool)
			return Fail("capture is not initialized");
		try
		{
			wgc::Direct3D11CaptureFrame latest{ nullptr };
			while (auto frame = _framePool.TryGetNextFrame())
			{
				if (latest)
					latest.Close();
				latest = frame;
			}
			if (!latest)
				return 0;

			winrt::Windows::Graphics::SizeInt32 contentSize = latest.ContentSize();
			int32_t result = 0;
			if (contentSize.Width > 0 && contentSize.Height > 0)
				result = CopyFrame(latest, contentSize);
			latest.Close();

			if (contentSize.Width > 0 && contentSize.Height > 0
				&& (contentSize.Width != _poolSize.Width || contentSize.Height != _poolSize.Height))
			{
				// Source resized: recreate the pool buffers at the new size (next frames are rendered at that size).
				_poolSize = contentSize;
				_framePool.Recreate(_winrtDevice, wgd::DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, _poolSize);
			}
			return result;
		}
		catch (const winrt::hresult_error& error)
		{
			return Fail("capture poll failed: " + HrText(error.code()) + " " + winrt::to_string(error.message()));
		}
	}

	void Size(int32_t* width, int32_t* height) const
	{
		*width = _size.Width;
		*height = _size.Height;
	}

	bool HasFrame() const { return _texture != nullptr; }
	bool IsClosed() const { return *_closed; }
	ID3D11Texture2D* Texture() const { return _texture.Get(); }
	const char* LastError() const { return _lastError.c_str(); }

private:
	int32_t CopyFrame(const wgc::Direct3D11CaptureFrame& frame, winrt::Windows::Graphics::SizeInt32 contentSize)
	{
		auto access = frame.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
		ComPtr<ID3D11Texture2D> source;
		HRESULT hr = access->GetInterface(IID_PPV_ARGS(source.GetAddressOf()));
		if (FAILED(hr))
			return Fail("capture surface has no ID3D11Texture2D: " + HrText(hr));

		D3D11_TEXTURE2D_DESC sourceDesc{};
		source->GetDesc(&sourceDesc);
		// Right after a resize the content can be larger than the pool buffers (clipped picture): drop that frame
		// instead of reporting a transient size, Poll recreates the pool and the next frame has the real size.
		if (static_cast<UINT>(contentSize.Width) > sourceDesc.Width || static_cast<UINT>(contentSize.Height) > sourceDesc.Height)
			return 0;
		UINT width = static_cast<UINT>(contentSize.Width);
		UINT height = static_cast<UINT>(contentSize.Height);

		if (!_texture || _textureWidth != width || _textureHeight != height)
		{
			D3D11_TEXTURE2D_DESC desc{};
			desc.Width = width;
			desc.Height = height;
			desc.MipLevels = 1;
			desc.ArraySize = 1;
			desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
			desc.SampleDesc.Count = 1;
			desc.Usage = D3D11_USAGE_DEFAULT;
			desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
			// New texture object (not resized in place): the encoder recreates its input view when the pointer changes.
			ComPtr<ID3D11Texture2D> texture;
			hr = _device->CreateTexture2D(&desc, nullptr, texture.GetAddressOf());
			if (FAILED(hr))
				return Fail("CreateTexture2D(capture BGRA) failed: " + HrText(hr));
			_texture = texture;
			_textureWidth = width;
			_textureHeight = height;
		}

		D3D11_BOX box{ 0, 0, 0, width, height, 1 };
		_context->CopySubresourceRegion(_texture.Get(), 0, 0, 0, 0, source.Get(), 0, &box);
		_size = { static_cast<int32_t>(width), static_cast<int32_t>(height) };
		return 1;
	}

	int32_t Fail(std::string message)
	{
		_lastError = std::move(message);
		return -1;
	}

	void Close()
	{
		try
		{
			if (_session)
				_session.Close();
			if (_framePool)
				_framePool.Close();
			if (_item && _closedToken.value)
				_item.Closed(_closedToken);
		}
		catch (...)
		{
		}
		_session = nullptr;
		_framePool = nullptr;
		_item = nullptr;
		_closedToken = {};
		_winrtDevice = nullptr;
		_texture.Reset();
		_context.Reset();
		_device.Reset();
		_closed = std::make_shared<std::atomic<bool>>(false);
	}

	ComPtr<ID3D11Device> _device;
	ComPtr<ID3D11DeviceContext> _context;
	wgd3d::IDirect3DDevice _winrtDevice{ nullptr };
	wgc::GraphicsCaptureItem _item{ nullptr };
	winrt::event_token _closedToken{};
	wgc::Direct3D11CaptureFramePool _framePool{ nullptr };
	wgc::GraphicsCaptureSession _session{ nullptr };
	winrt::Windows::Graphics::SizeInt32 _poolSize{};
	winrt::Windows::Graphics::SizeInt32 _size{};
	ComPtr<ID3D11Texture2D> _texture;
	UINT _textureWidth = 0;
	UINT _textureHeight = 0;
	std::shared_ptr<std::atomic<bool>> _closed = std::make_shared<std::atomic<bool>>(false);
	std::string _lastError;
};

ID3D11Texture2D* FrInternal_CaptureTexture(FrCapture* capture)
{
	return capture ? capture->Texture() : nullptr;
}

FR_API FrCapture* FR_CALL FrCapture_Create()
{
	return new (std::nothrow) FrCapture();
}

FR_API int32_t FR_CALL FrCapture_Init(FrCapture* capture, FrEncoder* encoder, int32_t kind, void* handle, int32_t showCursor)
{
	return capture ? capture->Init(encoder, kind, handle, showCursor != 0) : -1;
}

FR_API int32_t FR_CALL FrCapture_Poll(FrCapture* capture)
{
	return capture ? capture->Poll() : -1;
}

FR_API void FR_CALL FrCapture_GetSize(FrCapture* capture, int32_t* width, int32_t* height)
{
	if (!width || !height)
		return;
	if (!capture)
	{
		*width = 0;
		*height = 0;
		return;
	}
	capture->Size(width, height);
}

FR_API int32_t FR_CALL FrCapture_HasFrame(FrCapture* capture)
{
	return capture && capture->HasFrame() ? 1 : 0;
}

FR_API int32_t FR_CALL FrCapture_IsClosed(FrCapture* capture)
{
	return capture && capture->IsClosed() ? 1 : 0;
}

FR_API const char* FR_CALL FrCapture_GetLastError(FrCapture* capture)
{
	return capture ? capture->LastError() : "capture is null";
}

FR_API void FR_CALL FrCapture_Destroy(FrCapture* capture)
{
	delete capture;
}

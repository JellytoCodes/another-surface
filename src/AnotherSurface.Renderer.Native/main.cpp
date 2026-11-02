#include <d2d1_1.h>
#include <d2d1effects.h>
#include <d3d11.h>
#include <dcomp.h>
#include <dxgi1_3.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfmediaengine.h>
#include <wincodec.h>
#include <windows.h>
#include <wrl/client.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <cstdint>
#include <cstring>
#include <exception>
#include <filesystem>
#include <iomanip>
#include <iostream>
#include <memory>
#include <mutex>
#include <sstream>
#include <stdexcept>
#include <string>
#include <vector>

using Microsoft::WRL::ComPtr;

namespace
{
constexpr std::uint32_t ProtocolMagic = 0x454D4C56; // "VLME"
constexpr std::uint16_t ProtocolVersion = 1;
constexpr std::uint32_t MaximumPayloadBytes = 16 * 1024 * 1024;
constexpr wchar_t WindowClassName[] = L"AnotherSurface.Native.Compositor";

enum class Command : std::uint16_t
{
    Attach = 1,
    Initialize = 2,
    SetVisible = 3,
    Pause = 4,
    Resume = 5,
    UpdatePlayback = 6,
    Close = 7,
    Capture = 8
};

#pragma pack(push, 1)
struct MessageHeader
{
    std::uint32_t Magic;
    std::uint16_t Version;
    std::uint16_t Command;
    std::uint32_t PayloadLength;
};
#pragma pack(pop)

struct PlaybackSettings
{
    int Fit = 0;
    double Brightness = 1.0;
    double Contrast = 1.0;
    double Saturation = 1.0;
    double Volume = 0.0;
    bool Muted = true;
};

struct DecodedFrame
{
    std::uint32_t Width = 0;
    std::uint32_t Height = 0;
    std::vector<std::uint8_t> Pixels;
    std::int64_t Timestamp = 0;
};

[[noreturn]] void ThrowFailure(const char *operation, HRESULT result)
{
    std::ostringstream message;
    message << operation << " failed (HRESULT=0x" << std::hex << std::uppercase << static_cast<std::uint32_t>(result)
            << ")";
    throw std::runtime_error(message.str());
}

void Check(HRESULT result, const char *operation)
{
    if (FAILED(result))
    {
        ThrowFailure(operation, result);
    }
}

std::wstring Utf8ToWide(const std::string &text)
{
    if (text.empty())
    {
        return {};
    }

    const auto length =
        MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), nullptr, 0);
    if (length <= 0)
    {
        throw std::runtime_error("The renderer received invalid UTF-8 text.");
    }

    std::wstring result(static_cast<std::size_t>(length), L'\0');
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), result.data(),
                        length);
    return result;
}

bool IsEnvironmentFlagEnabled(const wchar_t *name)
{
    wchar_t value[8]{};
    const auto length = GetEnvironmentVariableW(name, value, ARRAYSIZE(value));
    return length == 1 && value[0] == L'1';
}

bool ReadExact(HANDLE stream, void *destination, std::uint32_t length)
{
    auto *output = static_cast<std::uint8_t *>(destination);
    std::uint32_t offset = 0;
    while (offset < length)
    {
        // This thread owns an HWND parented to Explorer. Blocking on the
        // synchronous stdin pipe also blocks display/DWM messages and can
        // stall the desktop's shared input queue during fullscreen changes.
        MSG message{};
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE))
        {
            if (message.message == WM_QUIT)
            {
                return false;
            }
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }

        DWORD available = 0;
        if (!PeekNamedPipe(stream, nullptr, 0, nullptr, &available, nullptr))
        {
            const auto error = GetLastError();
            if (error == ERROR_BROKEN_PIPE || error == ERROR_HANDLE_EOF)
            {
                return false;
            }
            throw std::runtime_error("Failed to inspect the renderer command stream.");
        }
        if (available == 0)
        {
            // Anonymous stdin pipes cannot use overlapped reads. Wait for
            // window messages and check the pipe at most 20 times per second;
            // never busy-spin or read beyond the bytes already available.
            if (MsgWaitForMultipleObjectsEx(0, nullptr, 50, QS_ALLINPUT, MWMO_INPUTAVAILABLE) == WAIT_FAILED)
            {
                throw std::runtime_error("Failed to wait for renderer window messages.");
            }
            continue;
        }
        DWORD read = 0;
        if (!ReadFile(stream, output + offset, std::min<DWORD>(length - offset, available), &read, nullptr))
        {
            const auto error = GetLastError();
            if (error == ERROR_BROKEN_PIPE || error == ERROR_HANDLE_EOF)
            {
                return false;
            }
            throw std::runtime_error("Failed to read the renderer command stream.");
        }
        if (read == 0)
        {
            return false;
        }
        offset += read;
    }
    return true;
}

void WriteExact(HANDLE stream, const void *source, std::uint32_t length)
{
    const auto *input = static_cast<const std::uint8_t *>(source);
    std::uint32_t offset = 0;
    while (offset < length)
    {
        DWORD written = 0;
        if (!WriteFile(stream, input + offset, length - offset, &written, nullptr) || written == 0)
        {
            throw std::runtime_error("Failed to write the renderer response stream.");
        }
        offset += written;
    }
}

class PayloadReader
{
  public:
    explicit PayloadReader(const std::vector<std::uint8_t> &payload) : payload_(payload)
    {
    }

    template <typename T> T Read()
    {
        if (offset_ + sizeof(T) > payload_.size())
        {
            throw std::runtime_error("The renderer command payload is truncated.");
        }

        T value{};
        std::memcpy(&value, payload_.data() + offset_, sizeof(T));
        offset_ += sizeof(T);
        return value;
    }

    std::string ReadString()
    {
        const auto length = Read<std::uint32_t>();
        if (length > MaximumPayloadBytes || offset_ + length > payload_.size())
        {
            throw std::runtime_error("The renderer command string is invalid.");
        }

        std::string value(reinterpret_cast<const char *>(payload_.data() + offset_), length);
        offset_ += length;
        return value;
    }

  private:
    const std::vector<std::uint8_t> &payload_;
    std::size_t offset_ = 0;
};

class PayloadWriter
{
  public:
    template <typename T> void Write(const T &value)
    {
        const auto previous = bytes_.size();
        bytes_.resize(previous + sizeof(T));
        std::memcpy(bytes_.data() + previous, &value, sizeof(T));
    }

    void WriteString(const std::string &value)
    {
        Write(static_cast<std::uint32_t>(value.size()));
        bytes_.insert(bytes_.end(), value.begin(), value.end());
    }

    const std::vector<std::uint8_t> &Bytes() const noexcept
    {
        return bytes_;
    }

  private:
    std::vector<std::uint8_t> bytes_;
};

LRESULT CALLBACK RendererWindowProc(HWND window, UINT message, WPARAM wordParameter, LPARAM longParameter)
{
    switch (message)
    {
    case WM_NCHITTEST:
        return HTTRANSPARENT;
    case WM_ERASEBKGND:
        return 1;
    default:
        return DefWindowProcW(window, message, wordParameter, longParameter);
    }
}

ATOM EnsureWindowClass()
{
    static const ATOM atom = [] {
        WNDCLASSEXW definition{};
        definition.cbSize = sizeof(definition);
        definition.lpfnWndProc = RendererWindowProc;
        definition.hInstance = GetModuleHandleW(nullptr);
        definition.lpszClassName = WindowClassName;
        definition.hCursor = LoadCursorW(nullptr, IDC_ARROW);
        const auto result = RegisterClassExW(&definition);
        if (result == 0 && GetLastError() != ERROR_CLASS_ALREADY_EXISTS)
        {
            throw std::runtime_error("Windows could not register the native renderer window.");
        }
        return result;
    }();
    return atom;
}

class CompositionRenderer
{
  public:
    ~CompositionRenderer()
    {
        Close();
    }

    void Attach(HWND parent, int displayX, int displayY, int width, int height, bool raisedDesktop, HWND shellView,
                bool visible)
    {
        std::scoped_lock lock(mutex_);

        if (!IsWindow(parent) || width <= 0 || height <= 0)
        {
            throw std::runtime_error("The native renderer received an invalid desktop host.");
        }

        RECT hostBounds{};
        if (!GetWindowRect(parent, &hostBounds))
        {
            throw std::runtime_error("Windows could not read the desktop host bounds.");
        }

        const auto relativeX = displayX - hostBounds.left;
        const auto relativeY = displayY - hostBounds.top;
        if (IsWindow(window_))
        {
            // Display changes only change geometry. Preserve the playback HWND
            // so Media Engine keeps its decoder, current position, and pause state.
            if (GetParent(window_) != parent)
            {
                SetParent(window_, parent);
                if (GetParent(window_) != parent)
                {
                    throw std::runtime_error("Windows could not reattach the native renderer HWND.");
                }
            }
            const auto insertionPoint = raisedDesktop && shellView && GetParent(shellView) == parent
                ? shellView : HWND_BOTTOM;
            if (!SetWindowPos(window_, insertionPoint, relativeX, relativeY, width, height, SWP_NOACTIVATE))
            {
                throw std::runtime_error("Windows could not resize the native renderer HWND.");
            }
            const bool sizeChanged = width_ != width || height_ != height;
            parent_ = parent;
            width_ = width;
            height_ = height;
            visibleRequested_ = visible;
            if (sizeChanged && swapChain_)
            {
                try
                {
                    ResizeGraphicsUnlocked();
                    if (frameBitmap_)
                    {
                        PresentUnlocked();
                    }
                }
                catch (...)
                {
                    // A failed buffer resize has already released the D2D target.
                    // Mark the HWND/content lost so the controller can recover it.
                    CloseUnlocked();
                    throw;
                }
            }
            if (firstFramePresented_)
            {
                ShowWindow(window_, visible ? SW_SHOWNOACTIVATE : SW_HIDE);
            }
            return;
        }

        CloseUnlocked();
        EnsureWindowClass();
        parent_ = parent;
        width_ = width;
        height_ = height;
        visibleRequested_ = visible;
        // Media Engine video uses legacy HWND playback. Keep the HWND on the
        // normal DWM redirection surface so a full-display child cannot
        // repeatedly switch between composed, MPO, and independent-flip paths
        // while another D3D application (notably Unreal Editor) is presenting.
        // The opt-out exists only for quick A/B rollback on affected systems.
        usesRedirectionSurface_ =
            !IsEnvironmentFlagEnabled(L"ANOTHER_SURFACE_NATIVE_LEGACY_NOREDIRECTION");
        DWORD extendedStyle = WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
        if (!usesRedirectionSurface_)
        {
            extendedStyle |= WS_EX_NOREDIRECTIONBITMAP;
        }
        window_ = CreateWindowExW(extendedStyle, WindowClassName, L"Another Surface Native Wallpaper Renderer",
                                  WS_CHILD | WS_CLIPCHILDREN | WS_CLIPSIBLINGS, relativeX, relativeY, width, height,
                                  parent, nullptr, GetModuleHandleW(nullptr), nullptr);
        if (!window_)
        {
            throw std::runtime_error("Windows could not create the native renderer HWND.");
        }

        if (raisedDesktop && shellView && GetParent(shellView) == parent)
        {
            if (!SetWindowPos(window_, shellView, relativeX, relativeY, width, height, SWP_NOACTIVATE))
            {
                throw std::runtime_error("Windows could not place the native renderer "
                                         "behind desktop icons.");
            }
        }
        else
        {
            SetWindowPos(window_, HWND_BOTTOM, relativeX, relativeY, width, height, SWP_NOACTIVATE);
        }

        CreateGraphicsUnlocked();
    }

    void SetVisible(bool visible)
    {
        std::scoped_lock lock(mutex_);
        visibleRequested_ = visible;
        if (window_ && firstFramePresented_)
        {
            ShowWindow(window_, visible ? SW_SHOWNOACTIVATE : SW_HIDE);
        }
    }

    void SetPlayback(const PlaybackSettings &settings, bool redraw = true)
    {
        std::scoped_lock lock(mutex_);
        const bool visualChanged = playback_.Fit != settings.Fit ||
            playback_.Brightness != settings.Brightness || playback_.Contrast != settings.Contrast ||
            playback_.Saturation != settings.Saturation;
        playback_ = settings;
        if (redraw && visualChanged && frameBitmap_)
        {
            PresentUnlocked();
        }
    }

    void Present(const std::uint8_t *pixels, std::uint32_t width, std::uint32_t height)
    {
        std::scoped_lock lock(mutex_);
        if (!pixels || width == 0 || height == 0)
        {
            throw std::runtime_error("The native renderer received an empty frame.");
        }

        EnsureFrameBitmapUnlocked(width, height);
        Check(frameBitmap_->CopyFromMemory(nullptr, pixels, width * 4), "ID2D1Bitmap1::CopyFromMemory");
        ++imageUploadCount_;
        sourceWidth_ = width;
        sourceHeight_ = height;
        PresentUnlocked();
    }

    std::uint32_t FrameWidth() const noexcept { return sourceWidth_; }
    std::uint32_t FrameHeight() const noexcept { return sourceHeight_; }
    std::uint64_t ImageUploadCount() const noexcept { return imageUploadCount_; }

    HWND Window() const noexcept
    {
        return window_;
    }

    HWND Parent() const noexcept
    {
        return parent_;
    }

    bool IsReady() const noexcept
    {
        return IsWindow(window_) && firstFramePresented_;
    }

    bool UsesRedirectionSurface() const noexcept
    {
        return usesRedirectionSurface_;
    }

    void PrepareExternalPlayback()
    {
        std::scoped_lock lock(mutex_);
        ReleaseGraphicsUnlocked();
        sourceWidth_ = 0;
        sourceHeight_ = 0;
        firstFramePresented_ = false;
    }

    void PrepareCompositionPlayback()
    {
        std::scoped_lock lock(mutex_);
        if (!swapChain_)
        {
            CreateGraphicsUnlocked();
        }
    }

    void MarkExternalFrameReady()
    {
        std::scoped_lock lock(mutex_);
        firstFramePresented_ = true;
        if (visibleRequested_ && window_)
        {
            ShowWindow(window_, SW_SHOWNOACTIVATE);
        }
    }

    void Close()
    {
        std::scoped_lock lock(mutex_);
        CloseUnlocked();
    }

  private:
    void ResizeGraphicsUnlocked()
    {
        // Release the D2D reference to the old back buffer before resizing it.
        d2dContext_->SetTarget(nullptr);
        targetBitmap_.Reset();
        Check(swapChain_->ResizeBuffers(0, static_cast<UINT>(width_), static_cast<UINT>(height_),
                                        DXGI_FORMAT_UNKNOWN, DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT),
              "IDXGISwapChain::ResizeBuffers");
        ComPtr<IDXGISurface> surface;
        Check(swapChain_->GetBuffer(0, IID_PPV_ARGS(&surface)), "IDXGISwapChain::GetBuffer(Resize)");
        const auto properties = D2D1::BitmapProperties1(
            D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
            D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED), 96.0f, 96.0f);
        Check(d2dContext_->CreateBitmapFromDxgiSurface(surface.Get(), &properties, &targetBitmap_),
              "ID2D1DeviceContext::CreateBitmapFromDxgiSurface(Resize)");
        d2dContext_->SetTarget(targetBitmap_.Get());
    }

    void CreateGraphicsUnlocked()
    {
        UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
        D3D_FEATURE_LEVEL featureLevel{};
        const D3D_FEATURE_LEVEL requested[] = {D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_1,
                                               D3D_FEATURE_LEVEL_10_0};
        auto result =
            D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags, requested, ARRAYSIZE(requested),
                              D3D11_SDK_VERSION, &d3dDevice_, &featureLevel, &d3dContext_);
        if (result == E_INVALIDARG)
        {
            result = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags, requested + 1,
                                       ARRAYSIZE(requested) - 1, D3D11_SDK_VERSION, &d3dDevice_, &featureLevel,
                                       &d3dContext_);
        }
        Check(result, "D3D11CreateDevice");

        ComPtr<IDXGIDevice> dxgiDevice;
        Check(d3dDevice_.As(&dxgiDevice), "ID3D11Device::QueryInterface(IDXGIDevice)");
        ComPtr<IDXGIAdapter> adapter;
        Check(dxgiDevice->GetAdapter(&adapter), "IDXGIDevice::GetAdapter");
        ComPtr<IDXGIFactory2> factory;
        Check(adapter->GetParent(IID_PPV_ARGS(&factory)), "IDXGIAdapter::GetParent");

        DXGI_SWAP_CHAIN_DESC1 swapDescription{};
        swapDescription.Width = static_cast<UINT>(width_);
        swapDescription.Height = static_cast<UINT>(height_);
        swapDescription.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        swapDescription.SampleDesc.Count = 1;
        swapDescription.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        swapDescription.BufferCount = 2;
        swapDescription.Scaling = DXGI_SCALING_STRETCH;
        swapDescription.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
        swapDescription.AlphaMode = DXGI_ALPHA_MODE_PREMULTIPLIED;
        swapDescription.Flags = DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;
        Check(factory->CreateSwapChainForComposition(d3dDevice_.Get(), &swapDescription, nullptr, &swapChain_),
              "IDXGIFactory2::CreateSwapChainForComposition");

        ComPtr<IDXGISwapChain2> swapChain2;
        if (SUCCEEDED(swapChain_.As(&swapChain2)))
        {
            swapChain2->SetMaximumFrameLatency(1);
            frameLatencyHandle_ = swapChain2->GetFrameLatencyWaitableObject();
        }

        ComPtr<IDXGISurface> surface;
        Check(swapChain_->GetBuffer(0, IID_PPV_ARGS(&surface)), "IDXGISwapChain::GetBuffer");
        const auto targetProperties = D2D1::BitmapProperties1(
            D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
            D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED), 96.0f, 96.0f);

        D2D1_FACTORY_OPTIONS factoryOptions{};
        Check(D2D1CreateFactory(D2D1_FACTORY_TYPE_MULTI_THREADED, __uuidof(ID2D1Factory1), &factoryOptions,
                                reinterpret_cast<void **>(d2dFactory_.GetAddressOf())),
              "D2D1CreateFactory");
        Check(d2dFactory_->CreateDevice(dxgiDevice.Get(), &d2dDevice_), "ID2D1Factory1::CreateDevice");
        Check(d2dDevice_->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE, &d2dContext_),
              "ID2D1Device::CreateDeviceContext");
        Check(d2dContext_->CreateBitmapFromDxgiSurface(surface.Get(), &targetProperties, &targetBitmap_),
              "ID2D1DeviceContext::CreateBitmapFromDxgiSurface");
        d2dContext_->SetTarget(targetBitmap_.Get());

        Check(d2dContext_->CreateEffect(CLSID_D2D1ColorMatrix, &colorEffect_),
              "ID2D1DeviceContext::CreateEffect(ColorMatrix)");

        Check(DCompositionCreateDevice(dxgiDevice.Get(), __uuidof(IDCompositionDevice),
                                       reinterpret_cast<void **>(compositionDevice_.GetAddressOf())),
              "DCompositionCreateDevice");
        Check(compositionDevice_->CreateTargetForHwnd(window_, TRUE, &compositionTarget_),
              "IDCompositionDevice::CreateTargetForHwnd");
        Check(compositionDevice_->CreateVisual(&compositionVisual_), "IDCompositionDevice::CreateVisual");
        Check(compositionVisual_->SetContent(swapChain_.Get()), "IDCompositionVisual::SetContent");
        Check(compositionTarget_->SetRoot(compositionVisual_.Get()), "IDCompositionTarget::SetRoot");
        Check(compositionDevice_->Commit(), "IDCompositionDevice::Commit");
    }

    D2D1_MATRIX_5X4_F CreateColorMatrix() const
    {
        const auto saturation = static_cast<float>(std::clamp(playback_.Saturation, 0.0, 1.5));
        const auto contrast = static_cast<float>(std::clamp(playback_.Contrast, 0.5, 1.5));
        const auto brightness = static_cast<float>(std::clamp(playback_.Brightness, 0.25, 1.25));
        constexpr float red = 0.2126f;
        constexpr float green = 0.7152f;
        constexpr float blue = 0.0722f;
        const auto inverse = 1.0f - saturation;
        const auto bias = 0.5f * (1.0f - contrast) + (brightness - 1.0f);

        return D2D1::Matrix5x4F(
            contrast * (inverse * red + saturation), contrast * (inverse * red), contrast * (inverse * red), 0.0f,
            contrast * (inverse * green), contrast * (inverse * green + saturation), contrast * (inverse * green), 0.0f,
            contrast * (inverse * blue), contrast * (inverse * blue), contrast * (inverse * blue + saturation), 0.0f,
            0.0f, 0.0f, 0.0f, 1.0f, bias, bias, bias, 0.0f);
    }

    void EnsureFrameBitmapUnlocked(std::uint32_t width, std::uint32_t height)
    {
        if (frameBitmap_ && frameBitmap_->GetPixelSize().width == width &&
            frameBitmap_->GetPixelSize().height == height)
        {
            return;
        }

        frameBitmap_.Reset();
        const auto properties = D2D1::BitmapProperties1(
            D2D1_BITMAP_OPTIONS_NONE, D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED),
            96.0f, 96.0f);
        Check(d2dContext_->CreateBitmap(D2D1::SizeU(width, height), nullptr, 0, &properties, &frameBitmap_),
              "ID2D1DeviceContext::CreateBitmap");
    }

    void PresentUnlocked()
    {
        if (!window_ || !d2dContext_ || !swapChain_ || !frameBitmap_)
        {
            throw std::runtime_error("The native composition surface is not attached.");
        }

        const auto width = sourceWidth_;
        const auto height = sourceHeight_;

        if (frameLatencyHandle_)
        {
            WaitForSingleObjectEx(frameLatencyHandle_, 1000, TRUE);
        }

        d2dContext_->BeginDraw();
        d2dContext_->SetTransform(D2D1::Matrix3x2F::Identity());
        d2dContext_->Clear(D2D1::ColorF(D2D1::ColorF::Black, 1.0f));
        colorEffect_->SetInput(0, frameBitmap_.Get());
        const auto matrix = CreateColorMatrix();
        Check(colorEffect_->SetValue(D2D1_COLORMATRIX_PROP_COLOR_MATRIX, matrix), "ID2D1Effect::SetValue(ColorMatrix)");

        float scaleX = static_cast<float>(width_) / width;
        float scaleY = static_cast<float>(height_) / height;
        if (playback_.Fit == 0)
        {
            scaleX = scaleY = std::max(scaleX, scaleY);
        }
        else if (playback_.Fit == 1)
        {
            scaleX = scaleY = std::min(scaleX, scaleY);
        }
        const auto renderedWidth = width * scaleX;
        const auto renderedHeight = height * scaleY;
        const auto offsetX = (static_cast<float>(width_) - renderedWidth) * 0.5f;
        const auto offsetY = (static_cast<float>(height_) - renderedHeight) * 0.5f;
        d2dContext_->SetTransform(D2D1::Matrix3x2F::Scale(scaleX, scaleY) *
                                  D2D1::Matrix3x2F::Translation(offsetX, offsetY));
        d2dContext_->DrawImage(colorEffect_.Get(), D2D1_INTERPOLATION_MODE_HIGH_QUALITY_CUBIC,
                               D2D1_COMPOSITE_MODE_SOURCE_OVER);
        d2dContext_->SetTransform(D2D1::Matrix3x2F::Identity());
        const auto drawResult = d2dContext_->EndDraw();
        Check(drawResult, "ID2D1DeviceContext::EndDraw");
        const auto presentResult = swapChain_->Present(1, 0);
        if (FAILED(presentResult) && presentResult != DXGI_STATUS_OCCLUDED)
        {
            ThrowFailure("IDXGISwapChain::Present", presentResult);
        }

        firstFramePresented_ = true;
        if (visibleRequested_)
        {
            ShowWindow(window_, SW_SHOWNOACTIVATE);
        }
    }

    void ReleaseGraphicsUnlocked()
    {
        frameBitmap_.Reset();
        colorEffect_.Reset();
        targetBitmap_.Reset();
        d2dContext_.Reset();
        d2dDevice_.Reset();
        d2dFactory_.Reset();
        compositionVisual_.Reset();
        compositionTarget_.Reset();
        if (compositionDevice_)
        {
            compositionDevice_->Commit();
        }
        compositionDevice_.Reset();
        swapChain_.Reset();
        d3dContext_.Reset();
        d3dDevice_.Reset();
        if (frameLatencyHandle_)
        {
            CloseHandle(frameLatencyHandle_);
            frameLatencyHandle_ = nullptr;
        }
    }

    void CloseUnlocked()
    {
        ReleaseGraphicsUnlocked();
        if (window_)
        {
            DestroyWindow(window_);
            window_ = nullptr;
        }
        parent_ = nullptr;
        width_ = 0;
        height_ = 0;
        sourceWidth_ = 0;
        sourceHeight_ = 0;
        imageUploadCount_ = 0;
        firstFramePresented_ = false;
        usesRedirectionSurface_ = true;
    }

    mutable std::mutex mutex_;
    HWND window_ = nullptr;
    HWND parent_ = nullptr;
    int width_ = 0;
    int height_ = 0;
    std::uint32_t sourceWidth_ = 0;
    std::uint32_t sourceHeight_ = 0;
    bool visibleRequested_ = true;
    bool firstFramePresented_ = false;
    bool usesRedirectionSurface_ = true;
    PlaybackSettings playback_{};
    std::uint64_t imageUploadCount_ = 0;
    HANDLE frameLatencyHandle_ = nullptr;
    ComPtr<ID3D11Device> d3dDevice_;
    ComPtr<ID3D11DeviceContext> d3dContext_;
    ComPtr<IDXGISwapChain1> swapChain_;
    ComPtr<ID2D1Factory1> d2dFactory_;
    ComPtr<ID2D1Device> d2dDevice_;
    ComPtr<ID2D1DeviceContext> d2dContext_;
    ComPtr<ID2D1Bitmap1> targetBitmap_;
    ComPtr<ID2D1Bitmap1> frameBitmap_;
    ComPtr<ID2D1Effect> colorEffect_;
    ComPtr<IDCompositionDevice> compositionDevice_;
    ComPtr<IDCompositionTarget> compositionTarget_;
    ComPtr<IDCompositionVisual> compositionVisual_;
};

D2D1_SIZE_U GetImageDecodeSize(std::uint32_t sourceWidth, std::uint32_t sourceHeight,
                              int displayWidth, int displayHeight, int fit)
{
    if (sourceWidth == 0 || sourceHeight == 0 || displayWidth <= 0 || displayHeight <= 0)
    {
        throw std::runtime_error("The image or display has no drawable pixels.");
    }
    auto scaleX = std::min(1.0, static_cast<double>(displayWidth) / sourceWidth);
    auto scaleY = std::min(1.0, static_cast<double>(displayHeight) / sourceHeight);
    if (fit == 0)
    {
        // Fill may extend beyond one screen axis. Keep enough source pixels
        // for the visible crop instead of stretching a Fit-sized thumbnail.
        scaleX = scaleY = std::max(scaleX, scaleY);
    }
    else if (fit == 1)
    {
        scaleX = scaleY = std::min(scaleX, scaleY);
    }
    return D2D1::SizeU(
        std::max(1U, static_cast<std::uint32_t>(std::ceil(sourceWidth * scaleX))),
        std::max(1U, static_cast<std::uint32_t>(std::ceil(sourceHeight * scaleY))));
}

DecodedFrame DecodeImage(const std::wstring &path, int displayWidth, int displayHeight, int fit)
{
    ComPtr<IWICImagingFactory> factory;
    Check(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory)),
          "CoCreateInstance(WICImagingFactory)");
    ComPtr<IWICBitmapDecoder> decoder;
    Check(
        factory->CreateDecoderFromFilename(path.c_str(), nullptr, GENERIC_READ, WICDecodeMetadataCacheOnLoad, &decoder),
        "IWICImagingFactory::CreateDecoderFromFilename");
    ComPtr<IWICBitmapFrameDecode> frame;
    Check(decoder->GetFrame(0, &frame), "IWICBitmapDecoder::GetFrame");
    UINT originalWidth = 0;
    UINT originalHeight = 0;
    Check(frame->GetSize(&originalWidth, &originalHeight), "IWICBitmapFrameDecode::GetSize");
    const auto decodeSize = GetImageDecodeSize(originalWidth, originalHeight, displayWidth, displayHeight, fit);
    ComPtr<IWICBitmapSource> imageSource;
    Check(frame.As(&imageSource), "IWICBitmapFrameDecode::QueryInterface(IWICBitmapSource)");
    if (decodeSize.width != originalWidth || decodeSize.height != originalHeight)
    {
        ComPtr<IWICBitmapScaler> scaler;
        Check(factory->CreateBitmapScaler(&scaler), "IWICImagingFactory::CreateBitmapScaler");
        Check(scaler->Initialize(frame.Get(), decodeSize.width, decodeSize.height, WICBitmapInterpolationModeFant),
              "IWICBitmapScaler::Initialize");
        Check(scaler.As(&imageSource), "IWICBitmapScaler::QueryInterface(IWICBitmapSource)");
    }
    ComPtr<IWICFormatConverter> converter;
    Check(factory->CreateFormatConverter(&converter), "IWICImagingFactory::CreateFormatConverter");
    Check(converter->Initialize(imageSource.Get(), GUID_WICPixelFormat32bppPBGRA, WICBitmapDitherTypeNone, nullptr, 0.0,
                                WICBitmapPaletteTypeCustom),
          "IWICFormatConverter::Initialize");

    DecodedFrame output;
    Check(converter->GetSize(&output.Width, &output.Height), "IWICBitmapSource::GetSize");
    if (output.Width == 0 || output.Height == 0)
    {
        throw std::runtime_error("The image has no drawable pixels.");
    }
    const auto byteCount = static_cast<std::uint64_t>(output.Width) * output.Height * 4;
    if (byteCount > MaximumPayloadBytes * 16ULL)
    {
        throw std::runtime_error("The image is too large for the native renderer.");
    }
    const auto stride = output.Width * 4;
    output.Pixels.resize(static_cast<std::size_t>(byteCount));
    Check(converter->CopyPixels(nullptr, stride, static_cast<UINT>(output.Pixels.size()), output.Pixels.data()),
          "IWICBitmapSource::CopyPixels");
    return output;
}

class MediaEngineNotification final : public IMFMediaEngineNotify
{
  public:
    STDMETHODIMP QueryInterface(REFIID interfaceId, void **value) override
    {
        if (!value)
        {
            return E_POINTER;
        }
        *value = nullptr;
        if (interfaceId == IID_IUnknown || interfaceId == __uuidof(IMFMediaEngineNotify))
        {
            *value = static_cast<IMFMediaEngineNotify *>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    STDMETHODIMP_(ULONG) AddRef() override
    {
        return ++referenceCount_;
    }

    STDMETHODIMP_(ULONG) Release() override
    {
        const auto references = --referenceCount_;
        if (references == 0)
        {
            delete this;
        }
        return references;
    }

    STDMETHODIMP EventNotify(DWORD eventId, DWORD_PTR parameterOne, DWORD parameterTwo) override
    {
        std::scoped_lock lock(mutex_);
        switch (eventId)
        {
        case MF_MEDIA_ENGINE_EVENT_LOADEDDATA:
        case MF_MEDIA_ENGINE_EVENT_CANPLAY:
            metadataReady_ = true;
            break;
        case MF_MEDIA_ENGINE_EVENT_FIRSTFRAMEREADY:
            firstFrameReady_ = true;
            break;
        case MF_MEDIA_ENGINE_EVENT_ERROR:
        case MF_MEDIA_ENGINE_EVENT_STREAMRENDERINGERROR: {
            std::ostringstream message;
            message << "Media Engine video playback failed (event=" << eventId << ", p1=0x" << std::hex
                    << static_cast<std::uint64_t>(parameterOne) << ", p2=0x" << parameterTwo << ").";
            error_ = message.str();
            break;
        }
        default:
            break;
        }
        changed_.notify_all();
        return S_OK;
    }

    void WaitForMetadata()
    {
        WaitFor(
            std::chrono::seconds(8), [this] { return metadataReady_; },
            "Media Engine did not load video metadata within 8 seconds.");
    }

    void WaitForFirstFrame()
    {
        WaitFor(
            std::chrono::seconds(20), [this] { return firstFrameReady_; },
            "Media Engine did not present a video frame within 20 seconds.");
    }

  private:
    template <typename Predicate>
    void WaitFor(std::chrono::seconds timeout, Predicate predicate, const char *timeoutMessage)
    {
        std::unique_lock lock(mutex_);
        if (!changed_.wait_for(lock, timeout, [this, &predicate] { return predicate() || !error_.empty(); }))
        {
            throw std::runtime_error(timeoutMessage);
        }
        if (!error_.empty())
        {
            throw std::runtime_error(error_);
        }
    }

    std::atomic_ulong referenceCount_{1};
    std::mutex mutex_;
    std::condition_variable changed_;
    bool metadataReady_ = false;
    bool firstFrameReady_ = false;
    std::string error_;
};

class MediaEngineVideoSession
{
  public:
    ~MediaEngineVideoSession()
    {
        Stop();
    }

    void Start(HWND playbackWindow, const std::wstring &path, const PlaybackSettings &settings)
    {
        Stop();
        if (!IsWindow(playbackWindow))
        {
            throw std::runtime_error("Media Engine received an invalid playback HWND.");
        }

        auto *notification = new MediaEngineNotification();
        notify_.Attach(notification);
        ComPtr<IMFAttributes> attributes;
        Check(MFCreateAttributes(&attributes, 2), "MFCreateAttributes(MediaEngine)");
        Check(attributes->SetUnknown(MF_MEDIA_ENGINE_CALLBACK, notify_.Get()),
              "IMFAttributes::SetUnknown(MediaEngineCallback)");
        Check(attributes->SetUINT64(MF_MEDIA_ENGINE_PLAYBACK_HWND,
                                    static_cast<UINT64>(reinterpret_cast<std::uintptr_t>(playbackWindow))),
              "IMFAttributes::SetUINT64(PlaybackHwnd)");

        ComPtr<IMFMediaEngineClassFactory> factory;
        Check(CoCreateInstance(CLSID_MFMediaEngineClassFactory, nullptr, CLSCTX_INPROC_SERVER,
                               IID_PPV_ARGS(&factory)),
              "CoCreateInstance(MFMediaEngineClassFactory)");
        Check(factory->CreateInstance(0, attributes.Get(), &engine_),
              "IMFMediaEngineClassFactory::CreateInstance");
        Check(engine_->SetLoop(TRUE), "IMFMediaEngine::SetLoop");
        Check(engine_->SetAutoPlay(FALSE), "IMFMediaEngine::SetAutoPlay");
        Check(engine_->SetMuted(settings.Muted), "IMFMediaEngine::SetMuted");
        Check(engine_->SetVolume(std::clamp(settings.Volume, 0.0, 1.0)), "IMFMediaEngine::SetVolume");

        BSTR source = SysAllocString(path.c_str());
        if (!source)
        {
            throw std::bad_alloc();
        }
        const auto sourceResult = engine_->SetSource(source);
        SysFreeString(source);
        Check(sourceResult, "IMFMediaEngine::SetSource");
        Check(engine_->Load(), "IMFMediaEngine::Load");
        notification->WaitForMetadata();
        Check(engine_->GetNativeVideoSize(&nativeWidth_, &nativeHeight_), "IMFMediaEngine::GetNativeVideoSize");
        if (nativeWidth_ == 0 || nativeHeight_ == 0)
        {
            throw std::runtime_error("Media Engine returned an empty video size.");
        }
        settings_ = settings;
        UpdateLayout(playbackWindow);
        Check(engine_->Play(), "IMFMediaEngine::Play");
        notification->WaitForFirstFrame();
        playbackWindow_ = playbackWindow;
        ready_ = true;
    }

    void Pause()
    {
        if (engine_)
        {
            Check(engine_->Pause(), "IMFMediaEngine::Pause");
        }
    }

    void Resume()
    {
        if (engine_)
        {
            Check(engine_->Play(), "IMFMediaEngine::Play");
        }
    }

    void UpdatePlayback(const PlaybackSettings &settings)
    {
        settings_ = settings;
        if (!engine_)
        {
            return;
        }
        Check(engine_->SetMuted(settings.Muted), "IMFMediaEngine::SetMuted");
        Check(engine_->SetVolume(std::clamp(settings.Volume, 0.0, 1.0)), "IMFMediaEngine::SetVolume");
        UpdateLayout(playbackWindow_);
    }

    void UpdateLayout()
    {
        UpdateLayout(playbackWindow_);
    }

    void Stop() noexcept
    {
        ready_ = false;
        if (engine_)
        {
            engine_->Pause();
            engine_->Shutdown();
        }
        engine_.Reset();
        notify_.Reset();
        playbackWindow_ = nullptr;
        nativeWidth_ = 0;
        nativeHeight_ = 0;
    }

    bool IsReady() const noexcept
    {
        return ready_;
    }

  private:
    void UpdateLayout(HWND playbackWindow)
    {
        if (!engine_ || !IsWindow(playbackWindow) || nativeWidth_ == 0 || nativeHeight_ == 0)
        {
            return;
        }

        RECT client{};
        if (!GetClientRect(playbackWindow, &client))
        {
            throw std::runtime_error("Media Engine could not read the playback bounds.");
        }
        const auto destinationWidth = client.right - client.left;
        const auto destinationHeight = client.bottom - client.top;
        if (destinationWidth <= 0 || destinationHeight <= 0)
        {
            return;
        }

        MFVideoNormalizedRect source{0.0f, 0.0f, 1.0f, 1.0f};
        RECT destination = client;
        const auto sourceAspect = static_cast<double>(nativeWidth_) / nativeHeight_;
        const auto destinationAspect = static_cast<double>(destinationWidth) / destinationHeight;
        if (settings_.Fit == 0)
        {
            if (sourceAspect > destinationAspect)
            {
                const auto visible = static_cast<float>(destinationAspect / sourceAspect);
                source.left = (1.0f - visible) * 0.5f;
                source.right = source.left + visible;
            }
            else
            {
                const auto visible = static_cast<float>(sourceAspect / destinationAspect);
                source.top = (1.0f - visible) * 0.5f;
                source.bottom = source.top + visible;
            }
        }
        else if (settings_.Fit == 1)
        {
            if (sourceAspect > destinationAspect)
            {
                const auto height = static_cast<LONG>(destinationWidth / sourceAspect);
                destination.top = (destinationHeight - height) / 2;
                destination.bottom = destination.top + height;
            }
            else
            {
                const auto width = static_cast<LONG>(destinationHeight * sourceAspect);
                destination.left = (destinationWidth - width) / 2;
                destination.right = destination.left + width;
            }
        }

        MFARGB border{};
        border.rgbAlpha = 0xFF;
        ComPtr<IMFMediaEngineEx> extended;
        Check(engine_.As(&extended), "IMFMediaEngine::QueryInterface(IMFMediaEngineEx)");
        Check(extended->UpdateVideoStream(&source, &destination, &border),
              "IMFMediaEngineEx::UpdateVideoStream");
    }

    HWND playbackWindow_ = nullptr;
    DWORD nativeWidth_ = 0;
    DWORD nativeHeight_ = 0;
    bool ready_ = false;
    PlaybackSettings settings_{};
    ComPtr<MediaEngineNotification> notify_;
    ComPtr<IMFMediaEngine> engine_;
};

class RendererHost
{
  public:
    RendererHost() = default;

    ~RendererHost()
    {
        Close();
    }

    void Attach(PayloadReader &payload)
    {
        const bool reusingWindow = IsWindow(renderer_.Window());
        if (!reusingWindow)
        {
            video_.Stop();
        }
        const auto parent = reinterpret_cast<HWND>(static_cast<std::uintptr_t>(payload.Read<std::int64_t>()));
        const auto x = payload.Read<std::int32_t>();
        const auto y = payload.Read<std::int32_t>();
        const auto width = payload.Read<std::int32_t>();
        const auto height = payload.Read<std::int32_t>();
        const auto raised = payload.Read<std::uint8_t>() != 0;
        const auto visible = payload.Read<std::uint8_t>() != 0;
        payload.Read<std::uint16_t>();
        const auto shellView = reinterpret_cast<HWND>(static_cast<std::uintptr_t>(payload.Read<std::int64_t>()));
        payload.Read<std::int64_t>(); // Reserved background WorkerW handle.
        const bool imageNeedsResize = reusingWindow && !imagePath_.empty() &&
            (displayWidth_ != width || displayHeight_ != height);
        renderer_.Attach(parent, x, y, width, height, raised, shellView, visible);
        displayWidth_ = width;
        displayHeight_ = height;
        visible_ = visible;
        if (reusingWindow)
        {
            video_.UpdateLayout();
            if (imageNeedsResize)
            {
                ReloadImage();
            }
        }
        else
        {
            backend_ = "Direct3D 11 compositor";
        }
    }

    void Initialize(PayloadReader &payload)
    {
        video_.Stop();
        imagePath_.clear();
        const auto kind = payload.Read<std::int32_t>();
        const auto settings = ReadPlayback(payload);
        const auto path = Utf8ToWide(payload.ReadString());
        if (!std::filesystem::is_regular_file(path))
        {
            throw std::runtime_error("The native wallpaper source file does not exist.");
        }
        settings_ = settings;
        renderer_.SetPlayback(settings, false);
        if (kind == 0)
        {
            renderer_.PrepareCompositionPlayback();
            imagePath_ = path;
            ReloadImage();
            backend_ = "Direct3D 11 + WIC";
        }
        else if (kind == 2)
        {
            renderer_.PrepareExternalPlayback();
            video_.Start(renderer_.Window(), path, settings);
            renderer_.MarkExternalFrameReady();
            backend_ = "C++ Media Foundation Media Engine";
        }
        else
        {
            throw std::runtime_error("This wallpaper kind is not supported by the native renderer.");
        }
        renderer_.SetVisible(visible_);
    }

    void SetVisible(PayloadReader &payload)
    {
        visible_ = payload.Read<std::uint8_t>() != 0;
        renderer_.SetVisible(visible_);
    }

    void Pause()
    {
        video_.Pause();
    }

    void Resume()
    {
        video_.Resume();
    }

    void UpdatePlayback(PayloadReader &payload)
    {
        const auto settings = ReadPlayback(payload);
        const bool imageNeedsRefit = !imagePath_.empty() && settings.Fit != settings_.Fit;
        settings_ = settings;
        renderer_.SetPlayback(settings, !imageNeedsRefit);
        if (imageNeedsRefit)
        {
            ReloadImage();
        }
        video_.UpdatePlayback(settings);
    }

    void Close()
    {
        video_.Stop();
        renderer_.Close();
        imagePath_.clear();
    }

    HWND Window() const noexcept
    {
        return renderer_.Window();
    }

    HWND Parent() const noexcept
    {
        return renderer_.Parent();
    }

    bool IsReady() const noexcept
    {
        return renderer_.IsReady();
    }

    const std::string &Backend() const noexcept
    {
        return backend_;
    }

    std::string Diagnostics() const
    {
        RECT bounds{};
        GetWindowRect(Window(), &bounds);
        std::ostringstream result;
        result << "backend=native-cpp-d3d11-composition, hwnd=0x" << std::hex << std::uppercase
               << reinterpret_cast<std::uintptr_t>(Window()) << ", parent=0x"
               << reinterpret_cast<std::uintptr_t>(Parent()) << ", layered=false, redirectionBitmap="
               << std::boolalpha << renderer_.UsesRedirectionSurface()
               << ", ready=" << std::boolalpha << IsReady()
               << std::dec << ", rect=" << bounds.left << ',' << bounds.top << '-'
               << bounds.right << ',' << bounds.bottom
               << ", dpi=" << GetDpiForWindow(Window());
        if (!imagePath_.empty())
        {
            result << ", image=" << renderer_.FrameWidth() << 'x' << renderer_.FrameHeight()
                   << ", imageUploads=" << renderer_.ImageUploadCount();
        }
        return result.str();
    }

  private:
    void ReloadImage()
    {
        // Always resize from the original file, never from a previously reduced
        // frame. The transient CPU pixels are released immediately after upload.
        const auto image = DecodeImage(imagePath_, displayWidth_, displayHeight_, settings_.Fit);
        renderer_.Present(image.Pixels.data(), image.Width, image.Height);
    }

    static PlaybackSettings ReadPlayback(PayloadReader &payload)
    {
        PlaybackSettings settings;
        settings.Fit = payload.Read<std::int32_t>();
        settings.Brightness = payload.Read<double>();
        settings.Contrast = payload.Read<double>();
        settings.Saturation = payload.Read<double>();
        settings.Volume = payload.Read<double>();
        settings.Muted = payload.Read<std::uint8_t>() != 0;
        return settings;
    }

    CompositionRenderer renderer_;
    MediaEngineVideoSession video_;
    std::wstring imagePath_;
    int displayWidth_ = 0;
    int displayHeight_ = 0;
    PlaybackSettings settings_{};
    bool visible_ = true;
    std::string backend_ = "Direct3D 11 compositor";
};

void WriteResponse(HANDLE output, Command command, const RendererHost &host, std::int32_t result,
                   const std::string &error)
{
    PayloadWriter payload;
    payload.Write(result);
    payload.Write(static_cast<std::uint64_t>(reinterpret_cast<std::uintptr_t>(host.Window())));
    payload.Write(static_cast<std::uint64_t>(reinterpret_cast<std::uintptr_t>(host.Parent())));
    payload.Write(static_cast<std::uint8_t>(host.IsReady() ? 1 : 0));
    payload.Write(static_cast<std::uint8_t>(0));
    payload.Write(static_cast<std::uint16_t>(0));
    payload.WriteString(host.Backend());
    payload.WriteString(host.Diagnostics());
    payload.WriteString(error);

    const MessageHeader header{ProtocolMagic, ProtocolVersion, static_cast<std::uint16_t>(command),
                               static_cast<std::uint32_t>(payload.Bytes().size())};
    WriteExact(output, &header, sizeof(header));
    if (!payload.Bytes().empty())
    {
        WriteExact(output, payload.Bytes().data(), static_cast<std::uint32_t>(payload.Bytes().size()));
    }
    FlushFileBuffers(output);
}

int RunProtocol()
{
    const auto input = GetStdHandle(STD_INPUT_HANDLE);
    const auto output = GetStdHandle(STD_OUTPUT_HANDLE);
    if (!input || input == INVALID_HANDLE_VALUE || !output || output == INVALID_HANDLE_VALUE)
    {
        return 2;
    }

    RendererHost host;
    bool closing = false;
    while (!closing)
    {
        MessageHeader header{};
        if (!ReadExact(input, &header, sizeof(header)))
        {
            break;
        }
        if (header.Magic != ProtocolMagic || header.Version != ProtocolVersion ||
            header.PayloadLength > MaximumPayloadBytes)
        {
            return 3;
        }

        std::vector<std::uint8_t> payload(header.PayloadLength);
        if (header.PayloadLength > 0 && !ReadExact(input, payload.data(), header.PayloadLength))
        {
            break;
        }

        const auto command = static_cast<Command>(header.Command);
        std::int32_t result = 0;
        std::string error;
        try
        {
            PayloadReader reader(payload);
            switch (command)
            {
            case Command::Attach:
                host.Attach(reader);
                break;
            case Command::Initialize:
                host.Initialize(reader);
                break;
            case Command::SetVisible:
                host.SetVisible(reader);
                break;
            case Command::Pause:
                host.Pause();
                break;
            case Command::Resume:
                host.Resume();
                break;
            case Command::UpdatePlayback:
                host.UpdatePlayback(reader);
                break;
            case Command::Capture:
                throw std::runtime_error("Native preview capture is not implemented yet.");
            case Command::Close:
                host.Close();
                closing = true;
                break;
            default:
                throw std::runtime_error("The native renderer received an unknown command.");
            }
        }
        catch (const std::exception &exception)
        {
            result = -1;
            error = exception.what();
        }

        WriteResponse(output, command, host, result, error);
        MSG message{};
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE))
        {
            if (message.message == WM_QUIT)
            {
                closing = true;
                break;
            }
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
    }
    host.Close();
    return 0;
}

int RunSelfTest()
{
    const auto expectDecodeSize = [](UINT width, UINT height, int displayWidth, int displayHeight, int fit,
                                     UINT expectedWidth, UINT expectedHeight) {
        const auto size = GetImageDecodeSize(width, height, displayWidth, displayHeight, fit);
        if (size.width != expectedWidth || size.height != expectedHeight)
        {
            throw std::runtime_error("Self-test image decode dimensions did not preserve the display fit contract.");
        }
    };
    expectDecodeSize(7680, 4320, 1920, 1080, 0, 1920, 1080);
    expectDecodeSize(1920, 1080, 210, 90, 0, 210, 119);
    expectDecodeSize(1920, 1080, 210, 90, 1, 160, 90);
    expectDecodeSize(1920, 1080, 90, 160, 0, 285, 160);
    expectDecodeSize(1920, 1080, 90, 160, 1, 90, 51);
    expectDecodeSize(1920, 1080, 90, 160, 2, 90, 160);
    expectDecodeSize(64, 36, 1920, 1080, 0, 64, 36);

    // Direct2D applies row-vector transforms left to right. Translation is in
    // destination pixels after Scale; dividing these offsets by scale is wrong.
    const auto fitTransform = D2D1::Matrix3x2F::Scale(0.5f, 0.5f) *
        D2D1::Matrix3x2F::Translation(0.0f, 30.0f);
    const auto origin = fitTransform.TransformPoint(D2D1::Point2F(0.0f, 0.0f));
    const auto corner = fitTransform.TransformPoint(D2D1::Point2F(400.0f, 200.0f));
    if (origin.x != 0.0f || origin.y != 30.0f || corner.x != 200.0f || corner.y != 130.0f)
    {
        throw std::runtime_error("Self-test image centering transform failed.");
    }
    HWND hostWindow = CreateWindowExW(WS_EX_TOOLWINDOW, L"STATIC", L"Another Surface Native Renderer Self Test", WS_OVERLAPPED,
                                      -32000, -32000, 128, 128, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
    if (!hostWindow)
    {
        throw std::runtime_error("Self-test could not create its host window.");
    }

    CompositionRenderer renderer;
    renderer.Attach(hostWindow, -32000, -32000, 128, 128, false, nullptr, false);
    std::vector<std::uint8_t> pixels(128 * 128 * 4);
    for (int y = 0; y < 128; ++y)
    {
        for (int x = 0; x < 128; ++x)
        {
            const auto offset = static_cast<std::size_t>(y * 128 + x) * 4;
            const bool mint = ((x / 16) + (y / 16)) % 2 == 0;
            pixels[offset + 0] = mint ? 203 : 127;
            pixels[offset + 1] = mint ? 206 : 122;
            pixels[offset + 2] = mint ? 134 : 19;
            pixels[offset + 3] = 255;
        }
    }
    renderer.Present(pixels.data(), 128, 128);
    PlaybackSettings adjusted;
    adjusted.Brightness = 0.8;
    renderer.SetPlayback(adjusted);
    renderer.Attach(hostWindow, -32000, -32000, 192, 192, false, nullptr, false);
    if (renderer.ImageUploadCount() != 1)
    {
        throw std::runtime_error("Self-test uploaded unchanged image pixels during redraw or buffer resize.");
    }
    const auto ready = renderer.IsReady();
    renderer.Close();
    DestroyWindow(hostWindow);
    if (!ready)
    {
        throw std::runtime_error("Self-test did not present a Direct3D frame.");
    }
    std::cout << "Another Surface native renderer self-test passed.\n";
    return 0;
}
} // namespace

int wmain(int argumentCount, wchar_t **arguments)
{
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);
    // Match the app's physical-pixel display contract before creating any HWND.
    if (!SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) &&
        !AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(),
                                     DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
    {
        std::cerr << "Per-monitor DPI initialization failed.\n";
        return 9;
    }
    const auto comResult = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(comResult))
    {
        std::cerr << "COM initialization failed.\n";
        return 10;
    }
    const auto mediaResult = MFStartup(MF_VERSION, MFSTARTUP_FULL);
    if (FAILED(mediaResult))
    {
        CoUninitialize();
        std::cerr << "Media Foundation initialization failed.\n";
        return 11;
    }

    int result = 0;
    try
    {
        const bool selfTest = argumentCount > 1 && _wcsicmp(arguments[1], L"--self-test") == 0;
        result = selfTest ? RunSelfTest() : RunProtocol();
    }
    catch (const std::exception &exception)
    {
        std::cerr << "Another Surface native renderer: " << exception.what() << '\n';
        result = 12;
    }

    MFShutdown();
    CoUninitialize();
    return result;
}

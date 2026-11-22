#pragma once
#include <d3d11.h>
#include <dcomp.h>
#include <wrl/client.h>
#include <cstring>
#include <new>
#include <vector>

// Owned by the composition worker. This visual belongs to the game's HWND.
// Windows can display it between two presents of the underlying game scene.
class RadarComposition {
    template<class T> using Ptr = Microsoft::WRL::ComPtr<T>;
    Ptr<ID3D11Device> gpu;
    Ptr<ID3D11DeviceContext> context;
    Ptr<IDCompositionDevice> device;
    Ptr<IDCompositionTarget> target;
    Ptr<IDCompositionVisual> visual;
    Ptr<IDCompositionSurface> surface;
    UINT surfaceWidth{}, surfaceHeight{};
    std::vector<unsigned char> upload;
public:
    ~RadarComposition() {
        // Retire the visual before the game starts blending the same pixels.
        if (target && SUCCEEDED(target->SetRoot(nullptr)) && SUCCEEDED(device->Commit()))
            device->WaitForCommitCompletion();
    }

    bool open(HWND window) {
        Ptr<IDXGIDevice> dxgi;
        return SUCCEEDED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
                   D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION,
                   &gpu, nullptr, &context))
            && SUCCEEDED(gpu.As(&dxgi))
            && SUCCEEDED(DCompositionCreateDevice(dxgi.Get(), IID_PPV_ARGS(&device)))
            && SUCCEEDED(device->CreateTargetForHwnd(window, TRUE, &target))
            && SUCCEEDED(device->CreateVisual(&visual))
            && SUCCEEDED(target->SetRoot(visual.Get()));
    }
    bool draw(const unsigned char* pixels, UINT w, UINT h, float x, float y, float width, float height,
              bool resized) {
        // Update a complete premultiplied surface, including transparent padding.
        // Rounding the allocation keeps small changes to the crop from recreating it each frame.
        const UINT paddedWidth = (w + 63u) & ~63u;
        const UINT paddedHeight = (h + 63u) & ~63u;
        if (resized || paddedWidth != surfaceWidth || paddedHeight != surfaceHeight) {
            Ptr<IDCompositionSurface> next;
            if (FAILED(device->CreateSurface(paddedWidth, paddedHeight, DXGI_FORMAT_B8G8R8A8_UNORM,
                       DXGI_ALPHA_MODE_PREMULTIPLIED, &next)) || FAILED(visual->SetContent(next.Get()))) return false;
            surface = next;
            surfaceWidth = paddedWidth;
            surfaceHeight = paddedHeight;
        }
        const UINT pitch = paddedWidth * 4;
        const UINT contentPitch = w * 4;
        if (paddedWidth != w || paddedHeight != h) {
            try {
                upload.resize(static_cast<size_t>(pitch) * paddedHeight);
            } catch (const std::bad_alloc&) { return false; }
            for (UINT row = 0; row < h; ++row) {
                auto* dest = upload.data() + row * pitch;
                std::memcpy(dest, pixels + row * contentPitch, contentPitch);
                std::memset(dest + contentPitch, 0, pitch - contentPitch);
            }
            std::memset(upload.data() + h * pitch, 0, (paddedHeight - h) * pitch);
            pixels = upload.data();
        }
        POINT offset{};
        Ptr<ID3D11Texture2D> texture;
        if (FAILED(surface->BeginDraw(nullptr, IID_PPV_ARGS(&texture), &offset))) return false;
        D3D11_BOX box{UINT(offset.x), UINT(offset.y), 0, UINT(offset.x)+paddedWidth, UINT(offset.y)+paddedHeight, 1};
        context->UpdateSubresource(texture.Get(), 0, &box, pixels, pitch, 0);
        texture.Reset();
        if (FAILED(surface->EndDraw())) return false;
        D2D_RECT_F clip{0, 0, float(w), float(h)};
        D2D_MATRIX_3X2_F transform{width/w, 0, 0, height/h, x, y};
        return SUCCEEDED(visual->SetClip(clip)) && SUCCEEDED(visual->SetTransform(transform))
            && SUCCEEDED(device->Commit());
    }
    bool healthy() const { BOOL valid = FALSE; return device && SUCCEEDED(device->CheckDeviceState(&valid)) && valid; }
};

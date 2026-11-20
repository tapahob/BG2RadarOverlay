#pragma once
#include <d3d11.h>
#include <dcomp.h>
#include <wrl/client.h>

// Owned by the composition worker. This visual belongs to the game's HWND.
// Windows can display it between two presents of the underlying game scene.
class RadarComposition {
    template<class T> using Ptr = Microsoft::WRL::ComPtr<T>;
    Ptr<ID3D11Device> gpu;
    Ptr<ID3D11DeviceContext> context;
    Ptr<IDCompositionDevice> device;
    Ptr<IDCompositionTarget> target;
    Ptr<IDCompositionVisual> visual;
    Ptr<IDCompositionVirtualSurface> surface;
public:
    bool open(HWND window, UINT maxSide) {
        Ptr<IDXGIDevice> dxgi;
        return SUCCEEDED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
                   D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION,
                   &gpu, nullptr, &context))
            && SUCCEEDED(gpu.As(&dxgi))
            && SUCCEEDED(DCompositionCreateDevice(dxgi.Get(), IID_PPV_ARGS(&device)))
            && SUCCEEDED(device->CreateTargetForHwnd(window, TRUE, &target))
            && SUCCEEDED(device->CreateVisual(&visual))
            && SUCCEEDED(device->CreateVirtualSurface(maxSide, maxSide, DXGI_FORMAT_B8G8R8A8_UNORM,
                   DXGI_ALPHA_MODE_PREMULTIPLIED, &surface))
            && SUCCEEDED(visual->SetContent(surface.Get()))
            && SUCCEEDED(target->SetRoot(visual.Get()));
    }
    bool draw(const unsigned char* pixels, UINT w, UINT h, float x, float y, float width, float height) {
        RECT area{0, 0, LONG(w), LONG(h)};
        POINT offset{};
        Ptr<IDXGISurface> dest;
        if (FAILED(surface->BeginDraw(&area, IID_PPV_ARGS(&dest), &offset))) return false;
        Ptr<ID3D11Texture2D> texture;
        HRESULT hr = dest.As(&texture);
        if (SUCCEEDED(hr)) {
            D3D11_BOX box{UINT(offset.x), UINT(offset.y), 0, UINT(offset.x)+w, UINT(offset.y)+h, 1};
            context->UpdateSubresource(texture.Get(), 0, &box, pixels, w*4, 0);
        }
        if (FAILED(surface->EndDraw()) || FAILED(hr)) return false;
        D2D_RECT_F clip{0, 0, float(w), float(h)};
        D2D_MATRIX_3X2_F transform{width/w, 0, 0, height/h, x, y};
        return SUCCEEDED(visual->SetClip(clip)) && SUCCEEDED(visual->SetTransform(transform))
            && SUCCEEDED(device->Commit());
    }
    bool healthy() const { BOOL valid = FALSE; return device && SUCCEEDED(device->CheckDeviceState(&valid)) && valid; }
};

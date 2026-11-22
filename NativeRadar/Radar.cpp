#include <windows.h>
#include <GL/gl.h>
#include <d3d9.h>
#include <wrl/client.h>
#include <MinHook.h>
#include <cstdint>
#include <vector>
#include <mutex>
#include <cwchar>
#include <cstring>
#include <string>
#include <filesystem>
#include <new>
#include <atomic>
#include <memory>
#include "Composition.h"

using Microsoft::WRL::ComPtr;
void startCombatEvents(const std::wstring& gameDirectory);

namespace {
// Layout shared with GameFramePublisher.cs. The renderer owns the mapping;
// every participant uses try-lock, so a stalled companion cannot stop a frame.
constexpr int maxSide = 4096;
struct FrameHeader {
    uint64_t lastPresent, lastUpdate;
    uint32_t revision, width, height;
    float left, top, right, bottom;
    uint32_t drawnRevision;
    uint64_t window;
};
static_assert(sizeof(FrameHeader) == 56);
constexpr size_t capacity = sizeof(FrameHeader) + maxSide * maxSide * 4;
HANDLE frameMutex;
FrameHeader* shared;
HANDLE frameUpdated;
HANDLE windowLayerAllowed;
std::atomic<HWND> compositionWindow{}, composedWindow{};
// SDL can set a new hardware cursor between presents. Remember what the game
// requested so leaving the radar restores the action under the pointer.
HCURSOR (WINAPI *setGameCursor)(HCURSOR);
struct PointerState {
    HWND window{};
    HCURSOR source{}, hand{}, previous{};
    uint64_t lease{};
};
thread_local PointerState pointer;
HCURSOR WINAPI setRadarCursor(HCURSOR requested) {
    if (pointer.hand) {
        if (requested != pointer.hand) pointer.previous = requested;
        if (GetForegroundWindow() == pointer.window && pointer.lease + 250 > GetTickCount64())
            return setGameCursor(pointer.hand);
    }
    return setGameCursor(requested);
}

struct PointerChannel {
    struct Message { uint64_t handle, updated; };
    HANDLE mapping{};
    const volatile Message* message{};
    uint64_t nextProbe{};

    const volatile Message* read(uint64_t now) {
        if (message || now < nextProbe) return message;
        nextProbe = now + 500;
        wchar_t name[96];
        swprintf_s(name, L"Local\\BG2Radar.Pointer.1.%lu", GetCurrentProcessId());
        mapping = OpenFileMappingW(FILE_MAP_READ, FALSE, name);
        if (mapping) {
            message = static_cast<const Message*>(MapViewOfFile(mapping, FILE_MAP_READ, 0, 0, sizeof(Message)));
            // The view keeps the memory alive. The handle keeps its *name* alive
            // so a restarted companion opens the same section the game reads.
            if (!message) { CloseHandle(mapping); mapping = nullptr; }
        }
        return message;
    }

    ~PointerChannel() {
        if (message) UnmapViewOfFile(const_cast<const Message*>(message));
        if (mapping) CloseHandle(mapping);
    }
};

void updatePointer(HWND window) {
    if (!setGameCursor || GetWindowThreadProcessId(window, nullptr) != GetCurrentThreadId()) return;
    static PointerChannel channel;
    auto now = GetTickCount64();
    auto message = channel.read(now);
    if (!message) return;
    auto lease = message->updated;
    HCURSOR wanted = nullptr;
    if (lease && lease + 250 > now && GetForegroundWindow() == window) {
        CURSORINFO info{sizeof(CURSORINFO)};
        if (GetCursorInfo(&info) && (info.flags & CURSOR_SHOWING)) {
            wanted = reinterpret_cast<HCURSOR>(message->handle);
            if (!wanted) wanted = LoadCursor(nullptr, IDC_HAND);
        }
    }
    pointer.window = window;
    pointer.lease = lease;
    if (pointer.source == wanted) {
        if (wanted && GetCursor() != pointer.hand) pointer.previous = setGameCursor(pointer.hand);
        return;
    }
    HCURSOR next = wanted ? CopyCursor(wanted) : nullptr;
    if (next) {
        HCURSOR old = setGameCursor(next);
        if (!pointer.hand || old != pointer.hand) pointer.previous = old;
    }
    else if (pointer.hand && GetCursor() == pointer.hand) {
        setGameCursor(pointer.previous);
    }
    if (pointer.hand) DestroyCursor(pointer.hand);
    pointer.hand = next;
    pointer.source = next ? wanted : nullptr;
}

struct Snapshot {
    FrameHeader frame{};
    std::vector<unsigned char> pixels;
    bool read(HWND presenting = nullptr) {
        auto now = GetTickCount64();
        DWORD lock = WaitForSingleObject(frameMutex, 0);
        if (lock == WAIT_OBJECT_0 || lock == WAIT_ABANDONED) {
            if (presenting) {
                shared->window = reinterpret_cast<uintptr_t>(presenting);
                shared->lastPresent = now;
                if (presenting == composedWindow.load()) { ReleaseMutex(frameMutex); return false; }
            }
            const auto h = *shared;
            if (h.revision != frame.revision || h.width != frame.width || h.height != frame.height) {
                frame = h;
                const bool valid = h.width && h.height && h.width <= maxSide && h.height <= maxSide
                    && h.left >= 0 && h.top >= 0 && h.right <= 1 && h.bottom <= 1
                    && h.left < h.right && h.top < h.bottom;
                if (valid) {
                    try {
                        const auto* src = reinterpret_cast<const unsigned char*>(shared + 1);
                        pixels.assign(src, src + static_cast<size_t>(h.width) * h.height * 4);
                    } catch (const std::bad_alloc&) {
                        pixels.clear();
                        frame.revision = 0;
                    }
                } else pixels.clear();
            }
            frame.lastUpdate = h.lastUpdate;
            frame.window = h.window;
            ReleaseMutex(frameMutex);
        }
        return !pixels.empty() && frame.lastUpdate && frame.lastUpdate + 1500 > now
            && (!presenting || composedWindow.load() != presenting);
    }
    bool update(HWND window, bool compositable = true) {
        updatePointer(window);
        HWND desired = compositable ? window : nullptr;
        if (compositionWindow.exchange(desired) != desired) {
            if (windowLayerAllowed) {
                if (desired) SetEvent(windowLayerAllowed);
                else ResetEvent(windowLayerAllowed);
            }
            if (frameUpdated) SetEvent(frameUpdated);
        }
        return read(window);
    }
};

DWORD WINAPI composeRadar(void*) {
    Snapshot snapshot;
    std::unique_ptr<RadarComposition> layer;
    HWND owner{};
    uint32_t drawn = 0;
    RECT previous{};
    uint64_t retry = 0;
    for (;;) {
        WaitForSingleObject(frameUpdated, 200);
        HWND window = compositionWindow.load();
        RECT client{};
        bool visible = window && !IsIconic(window) && GetClientRect(window, &client)
            && client.right > 0 && client.bottom > 0 && snapshot.read()
            && snapshot.frame.window == reinterpret_cast<uintptr_t>(window);
        if (!visible || owner != window || (layer && !layer->healthy())) {
            layer.reset();
            composedWindow.store(nullptr);
            drawn = 0;
            owner = window;
        }
        if (!visible) continue;
        if (!layer) {
            if (GetTickCount64() < retry) continue;
            layer = std::make_unique<RadarComposition>();
            if (!layer->open(window)) {
                layer.reset();
                retry = GetTickCount64() + 2000;
                continue;
            }
        }
        const auto& f = snapshot.frame;
        const bool resized = !EqualRect(&previous, &client);
        if (drawn == f.revision && !resized) continue;
        if (compositionWindow.load() != window) continue;
        composedWindow.store(window);
        if (layer->draw(snapshot.pixels.data(), f.width, f.height, client.right*f.left, client.bottom*f.top,
                        client.right*(f.right-f.left), client.bottom*(f.bottom-f.top), resized)) {
            previous = client;
            shared->drawnRevision = drawn = f.revision;
        } else {
            layer.reset();
            composedWindow.store(nullptr);
            drawn = 0;
            retry = GetTickCount64() + 2000;
        }
    }
}
bool isGameWindow(HWND w) {
    DWORD pid = 0;
    GetWindowThreadProcessId(w, &pid);
    return w && pid == GetCurrentProcessId() && IsWindowVisible(w) && GetAncestor(w, GA_ROOT) == w;
}

// A separate legacy compatibility context on the same double-buffered drawable
// keeps the game's shader, framebuffer, texture units and vertex state intact.
BOOL (WINAPI *swapGameBuffers)(HDC);
BOOL WINAPI swapRadarBuffers(HDC dc) {
    static HGLRC overlayContext, gameContext;
    static HDC drawable;
    static GLuint texture;
    static uint32_t revision;
    static Snapshot snapshot;
    static std::mutex renderLock;
    std::unique_lock lock(renderLock, std::try_to_lock);
    HGLRC current = wglGetCurrentContext();
    HWND window = WindowFromDC(dc);
    if (lock && current && wglGetCurrentDC() == dc && isGameWindow(window) && snapshot.update(window)) {
        if (gameContext != current || drawable != dc || !overlayContext) {
            if (overlayContext) wglDeleteContext(overlayContext);
            overlayContext = wglCreateContext(dc);
            gameContext = current;
            drawable = dc;
            texture = 0;
        }
        if (overlayContext && wglMakeCurrent(dc, overlayContext)) {
            const auto& f = snapshot.frame;
            if (!texture || revision != f.revision) {
                if (!texture) glGenTextures(1, &texture);
                glBindTexture(GL_TEXTURE_2D, texture);
                glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
                glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
                glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, 0x812F); // GL_CLAMP_TO_EDGE
                glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, 0x812F);
                glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA, f.width, f.height, 0,
                             0x80E1 /* GL_BGRA */, GL_UNSIGNED_BYTE, snapshot.pixels.data());
                revision = f.revision;
            }
            RECT r{};
            GetClientRect(window, &r);
            glViewport(0, 0, r.right, r.bottom);
            glDrawBuffer(GL_BACK);
            glEnable(GL_BLEND);
            glBlendFunc(GL_ONE, GL_ONE_MINUS_SRC_ALPHA);
            glEnable(GL_TEXTURE_2D);
            float l = 2*f.left-1, t = 1-2*f.top, b = 1-2*f.bottom, right = 2*f.right-1;
            glBegin(GL_TRIANGLE_STRIP);
            glTexCoord2f(0, 0); glVertex2f(l, t);
            glTexCoord2f(1, 0); glVertex2f(right, t);
            glTexCoord2f(0, 1); glVertex2f(l, b);
            glTexCoord2f(1, 1); glVertex2f(right, b);
            glEnd();
            glFlush();
            shared->drawnRevision = f.revision;
            wglMakeCurrent(dc, current);
        }
    }
    return swapGameBuffers(dc);
}

using DevicePresent = HRESULT (WINAPI *)(IDirect3DDevice9*, const RECT*, const RECT*, HWND, const RGNDATA*);
DevicePresent presentGame;
void drawD3D(IDirect3DDevice9* device, IDirect3DSurface9* back, const RECT* source, Snapshot& snapshot) {
    // Capture all pipeline state before drawing. Render target/depth surface
    // are not covered by D3DSBT_ALL and must be restored separately.
    ComPtr<IDirect3DStateBlock9> state;
    ComPtr<IDirect3DSurface9> previous, depth;
    if (FAILED(device->CreateStateBlock(D3DSBT_ALL, &state)) ||
        FAILED(device->GetRenderTarget(0, &previous))) return;
    device->GetDepthStencilSurface(&depth);
    D3DSURFACE_DESC desc{};
    if (FAILED(back->GetDesc(&desc))) return;
    const RECT rect = source ? *source : RECT{0, 0, LONG(desc.Width), LONG(desc.Height)};
    const auto& f = snapshot.frame;
    // Managed textures survive a D3D9 Reset. Drop the cache when switching devices.
    static ComPtr<IDirect3DTexture9> texture;
    static IDirect3DDevice9* owner;
    static FrameHeader cached{};
    if (owner != device || cached.width != f.width || cached.height != f.height) texture.Reset();
    if (!texture || cached.revision != f.revision) {
        if (!texture && FAILED(device->CreateTexture(f.width, f.height, 1, 0, D3DFMT_A8R8G8B8,
                                                     D3DPOOL_MANAGED, &texture, nullptr))) return;
        D3DLOCKED_RECT dest{};
        if (FAILED(texture->LockRect(0, &dest, nullptr, 0))) return;
        for (UINT y = 0; y < f.height; ++y)
            memcpy(static_cast<char*>(dest.pBits) + y * dest.Pitch,
                   snapshot.pixels.data() + y * f.width * 4, f.width * 4);
        texture->UnlockRect(0);
        cached = f;
        owner = device;
    }
    if (FAILED(device->BeginScene())) return;
    device->SetDepthStencilSurface(nullptr);
    if (SUCCEEDED(device->SetRenderTarget(0, back))) {
        D3DVIEWPORT9 viewport{0, 0, desc.Width, desc.Height, 0, 1};
        device->SetViewport(&viewport);
        device->SetVertexShader(nullptr);
        device->SetPixelShader(nullptr);
        device->SetFVF(D3DFVF_XYZRHW | D3DFVF_TEX1);
        device->SetRenderState(D3DRS_ZENABLE, FALSE);
        device->SetRenderState(D3DRS_STENCILENABLE, FALSE);
        device->SetRenderState(D3DRS_LIGHTING, FALSE);
        device->SetRenderState(D3DRS_FOGENABLE, FALSE);
        device->SetRenderState(D3DRS_SCISSORTESTENABLE, FALSE);
        device->SetRenderState(D3DRS_ALPHATESTENABLE, FALSE);
        device->SetRenderState(D3DRS_CULLMODE, D3DCULL_NONE);
        device->SetRenderState(D3DRS_COLORWRITEENABLE, 15);
        device->SetRenderState(D3DRS_ALPHABLENDENABLE, TRUE);
        device->SetRenderState(D3DRS_BLENDOP, D3DBLENDOP_ADD);
        device->SetRenderState(D3DRS_SEPARATEALPHABLENDENABLE, FALSE);
        device->SetRenderState(D3DRS_SRCBLEND, D3DBLEND_ONE);
        device->SetRenderState(D3DRS_DESTBLEND, D3DBLEND_INVSRCALPHA);
        device->SetTexture(0, texture.Get());
        device->SetTextureStageState(0, D3DTSS_COLOROP, D3DTOP_SELECTARG1);
        device->SetTextureStageState(0, D3DTSS_COLORARG1, D3DTA_TEXTURE);
        device->SetTextureStageState(0, D3DTSS_ALPHAOP, D3DTOP_SELECTARG1);
        device->SetTextureStageState(0, D3DTSS_ALPHAARG1, D3DTA_TEXTURE);
        device->SetTextureStageState(0, D3DTSS_TEXCOORDINDEX, 0);
        device->SetTextureStageState(0, D3DTSS_TEXTURETRANSFORMFLAGS, D3DTTFF_DISABLE);
        device->SetTextureStageState(1, D3DTSS_COLOROP, D3DTOP_DISABLE);
        device->SetSamplerState(0, D3DSAMP_MINFILTER, D3DTEXF_LINEAR);
        device->SetSamplerState(0, D3DSAMP_MAGFILTER, D3DTEXF_LINEAR);
        device->SetSamplerState(0, D3DSAMP_ADDRESSU, D3DTADDRESS_CLAMP);
        device->SetSamplerState(0, D3DSAMP_ADDRESSV, D3DTADDRESS_CLAMP);
        const float w = float(rect.right - rect.left), h = float(rect.bottom - rect.top);
        float l = rect.left+w*f.left-.5f, r = rect.left+w*f.right-.5f;
        float t = rect.top+h*f.top-.5f, b = rect.top+h*f.bottom-.5f;
        struct Vertex { float x, y, z, rhw, u, v; };
        Vertex quad[] = {{l,t,0,1,0,0}, {r,t,0,1,1,0}, {l,b,0,1,0,1}, {r,b,0,1,1,1}};
        if (SUCCEEDED(device->DrawPrimitiveUP(D3DPT_TRIANGLESTRIP, 2, quad, sizeof(Vertex))))
            shared->drawnRevision = f.revision;
    }
    device->SetRenderTarget(0, previous.Get());
    device->SetDepthStencilSurface(depth.Get());
    state->Apply();
    device->EndScene();
}
HRESULT WINAPI presentRadar(IDirect3DDevice9* device, const RECT* src, const RECT* dst,
                            HWND window, const RGNDATA* dirty) {
    static Snapshot snapshot;
    static std::mutex renderLock;
    {
        std::unique_lock lock(renderLock, std::try_to_lock);
        D3DDEVICE_CREATION_PARAMETERS creation{};
        ComPtr<IDirect3DSurface9> back;
        if (lock && SUCCEEDED(device->GetCreationParameters(&creation)) &&
            isGameWindow(window ? window : creation.hFocusWindow) &&
            SUCCEEDED(device->GetBackBuffer(0, 0, D3DBACKBUFFER_TYPE_MONO, &back))) {
            ComPtr<IDirect3DSwapChain9> chain;
            D3DPRESENT_PARAMETERS params{};
            bool compositable = SUCCEEDED(device->GetSwapChain(0, &chain))
                && SUCCEEDED(chain->GetPresentParameters(&params)) && params.Windowed;
            if (snapshot.update(window ? window : creation.hFocusWindow, compositable))
                drawD3D(device, back.Get(), src, snapshot);
        }
    }
    return presentGame(device, src, dst, window, dirty);
}

// Observe only graphics devices created by Baldur.exe's ordinary D3D9 startup.
using CreateDevice = HRESULT (WINAPI *)(IDirect3D9*, UINT, D3DDEVTYPE, HWND, DWORD,
                                        D3DPRESENT_PARAMETERS*, IDirect3DDevice9**);
CreateDevice createGameDevice;
using Create9 = IDirect3D9* (WINAPI *)(UINT);
Create9 createGame9;

template<typename Fn>
bool attach(void* address, Fn replacement, Fn& original) {
    if (MH_CreateHook(address, reinterpret_cast<void*>(replacement),
                      reinterpret_cast<void**>(&original)) != MH_OK) return false;
    return MH_EnableHook(address) == MH_OK;
}
HRESULT WINAPI createRadarDevice(IDirect3D9* d3d, UINT index, D3DDEVTYPE type, HWND w, DWORD flags,
                                 D3DPRESENT_PARAMETERS* params, IDirect3DDevice9** device) {
    HRESULT hr = createGameDevice(d3d, index, type, w, flags, params, device);
    if (SUCCEEDED(hr) && device && *device && !presentGame)
        attach((*reinterpret_cast<void***>(*device))[17], presentRadar, presentGame);
    return hr;
}
IDirect3D9* WINAPI createRadar9(UINT sdk) {
    IDirect3D9* result = createGame9(sdk);
    if (result && !createGameDevice)
        attach((*reinterpret_cast<void***>(result))[16], createRadarDevice, createGameDevice);
    return result;
}
void startCompanion(const std::wstring& gameDirectory) {
    // The installer records the absolute apphost path even on volumes without symlinks.
    std::wstring directory = gameDirectory + L"\\BGRadar";
    std::wstring exe = directory + L"\\BG Radar Overlay.exe";
    const auto config = directory + L"\\companion.ini";
    if (GetFileAttributesW(config.c_str()) != INVALID_FILE_ATTRIBUTES) {
        std::wstring value(32768, L'\0');
        DWORD count = GetPrivateProfileStringW(L"Radar", L"Executable", L"", value.data(),
                                               static_cast<DWORD>(value.size()), config.c_str());
        if (!count || count >= value.size() - 1) return;
        value.resize(count);
        const std::filesystem::path path(value);
        if (!path.is_absolute() || _wcsicmp(path.extension().c_str(), L".exe") != 0) return;
        exe = value;
    } else if (GetFileAttributesW(exe.c_str()) == INVALID_FILE_ATTRIBUTES) {
        directory = gameDirectory + L"\\BG2Radar"; // Existing manual installs.
        exe = directory + L"\\BG Radar Overlay.exe";
    }
    DWORD attributes = GetFileAttributesW(exe.c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_DIRECTORY)) return;
    HANDLE existing = OpenMutexW(SYNCHRONIZE, FALSE, L"Local\\BG2Radar.Companion");
    if (existing) {
        CloseHandle(existing);
        return;
    }
    // Older installs place a symlink here. Start its target beside its own runtime.
    if (attributes & FILE_ATTRIBUTE_REPARSE_POINT) {
        HANDLE file = CreateFileW(exe.c_str(), FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                                  nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) return;
        DWORD length = GetFinalPathNameByHandleW(file, nullptr, 0, FILE_NAME_NORMALIZED);
        std::wstring resolved(length, L'\0');
        DWORD copied = length ? GetFinalPathNameByHandleW(file, resolved.data(), length, FILE_NAME_NORMALIZED) : 0;
        CloseHandle(file);
        if (!copied || copied >= length) return;
        resolved.resize(copied);
        if (resolved.rfind(L"\\\\?\\UNC\\", 0) == 0) exe = L"\\\\" + resolved.substr(8);
        else if (resolved.rfind(L"\\\\?\\", 0) == 0) exe = resolved.substr(4);
        else exe = resolved;
    }
    directory = exe.substr(0, exe.find_last_of(L'\\'));
    std::wstring command = L"\"" + exe + L"\" --game-pid " + std::to_wstring(GetCurrentProcessId());
    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    startup.dwFlags = STARTF_USESHOWWINDOW;
    startup.wShowWindow = SW_SHOWNOACTIVATE;
    PROCESS_INFORMATION child{};
    if (CreateProcessW(exe.c_str(), command.data(), nullptr, nullptr, FALSE, 0, nullptr,
                       directory.c_str(), &startup, &child)) {
        CloseHandle(child.hThread);
        CloseHandle(child.hProcess);
    }
}

}

extern "C" __declspec(dllexport) void InitializeASI() {
    wchar_t path[MAX_PATH]{};
    if (!GetModuleFileNameW(nullptr, path, MAX_PATH)) return;
    const wchar_t* filename = wcsrchr(path, L'\\');
    if (!filename || (_wcsicmp(filename + 1, L"Baldur.exe") && _wcsicmp(filename + 1, L"icewind.exe"))) return;
    static std::once_flag once;
    std::call_once(once, [&] {
        wchar_t name[96];
        swprintf_s(name, L"Local\\BG2Radar.Frame.1.%lu.Lock", GetCurrentProcessId());
        frameMutex = CreateMutexW(nullptr, FALSE, name);
        // Advertise child-window support before the companion can open the frame mapping.
        swprintf_s(name, L"Local\\BG2Radar.Frame.1.%lu.WindowLayer", GetCurrentProcessId());
        windowLayerAllowed = CreateEventW(nullptr, TRUE, FALSE, name);
        swprintf_s(name, L"Local\\BG2Radar.Frame.1.%lu", GetCurrentProcessId());
        HANDLE mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, capacity, name);
        if (!mapping || !frameMutex) return;
        shared = static_cast<FrameHeader*>(MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, capacity));
        if (!shared || MH_Initialize() != MH_OK) return;
        const std::wstring gameDirectory(path, static_cast<size_t>(filename - path));
        startCombatEvents(gameDirectory);
        swprintf_s(name, L"Local\\BG2Radar.Frame.1.%lu.Updated", GetCurrentProcessId());
        frameUpdated = CreateEventW(nullptr, FALSE, FALSE, name);
        if (frameUpdated) {
            HANDLE worker = CreateThread(nullptr, 0, composeRadar, nullptr, 0, nullptr);
            if (worker) CloseHandle(worker);
        }
        attach(reinterpret_cast<void*>(&SetCursor), setRadarCursor, setGameCursor);
        attach(reinterpret_cast<void*>(&SwapBuffers), swapRadarBuffers, swapGameBuffers);
        HMODULE d3d = LoadLibraryExW(L"d3d9.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (d3d) attach(reinterpret_cast<void*>(GetProcAddress(d3d, "Direct3DCreate9")), createRadar9, createGame9);
        startCompanion(gameDirectory);
    });
}

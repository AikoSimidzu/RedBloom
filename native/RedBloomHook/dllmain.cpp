// RedBloomHook - grabs the animated wallpaper straight out of Wallpaper Engine.
//
// Why a hook at all: the desktop icons (SHELLDLL_DefView) and the wallpaper (WorkerW, with
// Wallpaper Engine's DX11 window inside it) are sibling child windows of Progman. Every
// capture API Windows offers works at top-level-window granularity or coarser, so all of
// them hand back the two composed together:
//
//   PrintWindow(Progman | WorkerW, PW_RENDERFULLCONTENT) -> wallpaper *and* icons
//   PrintWindow(WPEDesktopDX11Window)                    -> black, the content is D3D
//   Graphics Capture CreateForWindow(WorkerW)            -> E_INVALIDARG, child window
//
// The one place the wallpaper exists on its own is inside Wallpaper Engine's swap chain,
// before the desktop is composed. So we sit in that process and copy the back buffer as it
// is presented. This is the same mechanism OBS uses for game capture.

#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>

// IDXGISwapChain1 and DXGI_PRESENT_PARAMETERS, needed for the Present1 slot.
#include <dxgi1_2.h>
#include <cstdio>
#include <cstdarg>

#include "Shared.h"

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "user32.lib")

namespace
{
    // ---- diagnostics ----

    // There is no debugger on the far side of an injection, and a fault takes the host down
    // with it, so every step says so in a file before it is attempted.
    void Log(const char* format, ...)
    {
        wchar_t path[MAX_PATH] = {};
        if (GetTempPathW(MAX_PATH, path) == 0)
        {
            return;
        }

        wcscat_s(path, L"RedBloomHook.log");

        FILE* file = nullptr;
        if (_wfopen_s(&file, path, L"a") != 0 || file == nullptr)
        {
            return;
        }

        SYSTEMTIME time = {};
        GetLocalTime(&time);
        fprintf(file, "[%02d:%02d:%02d.%03d] ", time.wHour, time.wMinute, time.wSecond, time.wMilliseconds);

        va_list arguments;
        va_start(arguments, format);
        vfprintf(file, format, arguments);
        va_end(arguments);

        fprintf(file, "\n");
        fclose(file);
    }

    // ---- shared memory ----

    HANDLE g_mapping = nullptr;
    uint8_t* g_view = nullptr;
    RedBloomFrameHeader* g_header = nullptr;

    bool OpenSharedMemory()
    {
        if (g_view != nullptr)
        {
            return true;
        }

        g_mapping = CreateFileMappingW(
            INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, REDBLOOM_TOTAL_BYTES, REDBLOOM_MAP_NAME);
        if (g_mapping == nullptr)
        {
            return false;
        }

        g_view = static_cast<uint8_t*>(MapViewOfFile(g_mapping, FILE_MAP_ALL_ACCESS, 0, 0, REDBLOOM_TOTAL_BYTES));
        if (g_view == nullptr)
        {
            CloseHandle(g_mapping);
            g_mapping = nullptr;
            return false;
        }

        g_header = reinterpret_cast<RedBloomFrameHeader*>(g_view);

        // Only stamp the magic once the block is usable, so a reader that arrives mid-setup
        // sees nothing rather than a half-built header.
        g_header->Width = 0;
        g_header->Height = 0;
        g_header->FrameIndex = 0;
        g_header->Latest = 0;
        if (g_header->IntervalMs == 0)
        {
            g_header->IntervalMs = 33;
        }

        MemoryBarrier();
        g_header->Magic = REDBLOOM_MAGIC;
        return true;
    }

    // ---- capture ----

    ID3D11Texture2D* g_mipTexture = nullptr;
    ID3D11ShaderResourceView* g_mipView = nullptr;
    ID3D11Texture2D* g_staging = nullptr;
    D3D11_TEXTURE2D_DESC g_sourceDesc = {};
    UINT g_mipLevel = 0;
    ULONGLONG g_lastCopyMs = 0;

    void ReleaseTextures()
    {
        if (g_mipView) { g_mipView->Release(); g_mipView = nullptr; }
        if (g_mipTexture) { g_mipTexture->Release(); g_mipTexture = nullptr; }
        if (g_staging) { g_staging->Release(); g_staging = nullptr; }
        g_sourceDesc = {};
    }

    /// <summary>Strips the sRGB variant, which cannot be used for a shader resource view here.</summary>
    DXGI_FORMAT ViewFormat(DXGI_FORMAT format)
    {
        switch (format)
        {
        case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB: return DXGI_FORMAT_B8G8R8A8_UNORM;
        case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB: return DXGI_FORMAT_R8G8B8A8_UNORM;
        default: return format;
        }
    }

    bool IsSupported(DXGI_FORMAT format)
    {
        switch (ViewFormat(format))
        {
        case DXGI_FORMAT_B8G8R8A8_UNORM:
        case DXGI_FORMAT_R8G8B8A8_UNORM:
        case DXGI_FORMAT_R10G10B10A2_UNORM:
            return true;
        default:
            return false;
        }
    }

    bool EnsureTextures(ID3D11Device* device, const D3D11_TEXTURE2D_DESC& source)
    {
        if (g_staging != nullptr
            && source.Width == g_sourceDesc.Width
            && source.Height == g_sourceDesc.Height
            && source.Format == g_sourceDesc.Format)
        {
            return true;
        }

        ReleaseTextures();
        g_sourceDesc = source;

        // Downscale by picking a mip rather than running a shader: no pipeline state to save
        // and restore, which matters when the render loop belongs to somebody else.
        g_mipLevel = 0;
        while ((source.Width >> g_mipLevel) > REDBLOOM_MAX_WIDTH
               || (source.Height >> g_mipLevel) > REDBLOOM_MAX_HEIGHT)
        {
            g_mipLevel++;
        }

        const UINT width = max(1u, source.Width >> g_mipLevel);
        const UINT height = max(1u, source.Height >> g_mipLevel);

        D3D11_TEXTURE2D_DESC mip = {};
        mip.Width = source.Width;
        mip.Height = source.Height;
        mip.MipLevels = g_mipLevel + 1;
        mip.ArraySize = 1;
        mip.Format = ViewFormat(source.Format);
        mip.SampleDesc.Count = 1;
        mip.Usage = D3D11_USAGE_DEFAULT;
        mip.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
        mip.MiscFlags = D3D11_RESOURCE_MISC_GENERATE_MIPS;

        if (FAILED(device->CreateTexture2D(&mip, nullptr, &g_mipTexture)))
        {
            ReleaseTextures();
            return false;
        }

        D3D11_SHADER_RESOURCE_VIEW_DESC view = {};
        view.Format = mip.Format;
        view.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
        view.Texture2D.MipLevels = mip.MipLevels;

        if (FAILED(device->CreateShaderResourceView(g_mipTexture, &view, &g_mipView)))
        {
            ReleaseTextures();
            return false;
        }

        D3D11_TEXTURE2D_DESC staging = {};
        staging.Width = width;
        staging.Height = height;
        staging.MipLevels = 1;
        staging.ArraySize = 1;
        staging.Format = mip.Format;
        staging.SampleDesc.Count = 1;
        staging.Usage = D3D11_USAGE_STAGING;
        staging.CPUAccessFlags = D3D11_CPU_ACCESS_READ;

        if (FAILED(device->CreateTexture2D(&staging, nullptr, &g_staging)))
        {
            ReleaseTextures();
            return false;
        }

        return true;
    }

    void Publish(const D3D11_MAPPED_SUBRESOURCE& mapped, UINT width, UINT height, DXGI_FORMAT format)
    {
        const UINT stride = width * 4;
        if (stride * height > REDBLOOM_BUFFER_BYTES)
        {
            return;
        }

        const uint32_t next = (g_header->Latest + 1) % REDBLOOM_BUFFER_COUNT;
        uint8_t* destination = g_view + REDBLOOM_HEADER_BYTES + next * REDBLOOM_BUFFER_BYTES;
        const uint8_t* source = static_cast<const uint8_t*>(mapped.pData);

        // Row by row: the staging pitch is whatever the driver chose and is rarely width * 4.
        for (UINT y = 0; y < height; y++)
        {
            memcpy(destination + y * stride, source + y * mapped.RowPitch, stride);
        }

        g_header->Width = width;
        g_header->Height = height;
        g_header->Stride = stride;
        g_header->Channels = (ViewFormat(format) == DXGI_FORMAT_R8G8B8A8_UNORM) ? 1u : 0u;

        // The buffer is complete before it is advertised, so a reader never sees a half-copy.
        MemoryBarrier();
        g_header->Latest = next;
        g_header->FrameIndex++;
    }

    bool IsWallpaperWindow(HWND window)
    {
        if (window == nullptr)
        {
            return false;
        }

        wchar_t className[64] = {};
        GetClassNameW(window, className, ARRAYSIZE(className));

        // Wallpaper Engine also presents its own preview and settings windows through the same
        // swap chain vtable; only the desktop one is wanted.
        return wcscmp(className, L"WPEDesktopDX11Window") == 0;
    }

    void Capture(IDXGISwapChain* swapChain)
    {
        if (!OpenSharedMemory())
        {
            return;
        }

        const ULONGLONG now = GetTickCount64();

        // Nobody reading means nothing to do. Wallpaper Engine should not pay for a capture
        // that RedBloom stopped collecting.
        if (now - g_header->ReaderTickMs > 2000)
        {
            return;
        }

        const uint32_t interval = g_header->IntervalMs ? g_header->IntervalMs : 33;
        if (now - g_lastCopyMs < interval)
        {
            return;
        }

        DXGI_SWAP_CHAIN_DESC description = {};
        if (FAILED(swapChain->GetDesc(&description)) || !IsWallpaperWindow(description.OutputWindow))
        {
            return;
        }

        ID3D11Texture2D* backBuffer = nullptr;
        if (FAILED(swapChain->GetBuffer(0, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&backBuffer))))
        {
            return;
        }

        D3D11_TEXTURE2D_DESC desc = {};
        backBuffer->GetDesc(&desc);

        ID3D11Device* device = nullptr;
        backBuffer->GetDevice(&device);

        ID3D11DeviceContext* context = nullptr;
        if (device != nullptr)
        {
            device->GetImmediateContext(&context);
        }

        if (device != nullptr && context != nullptr && desc.SampleDesc.Count == 1 && IsSupported(desc.Format)
            && EnsureTextures(device, desc))
        {
            context->CopySubresourceRegion(g_mipTexture, 0, 0, 0, 0, backBuffer, 0, nullptr);

            if (g_mipLevel > 0)
            {
                context->GenerateMips(g_mipView);
            }

            context->CopySubresourceRegion(g_staging, 0, 0, 0, 0, g_mipTexture, g_mipLevel, nullptr);

            D3D11_MAPPED_SUBRESOURCE mapped = {};
            if (SUCCEEDED(context->Map(g_staging, 0, D3D11_MAP_READ, 0, &mapped)))
            {
                D3D11_TEXTURE2D_DESC stagingDesc = {};
                g_staging->GetDesc(&stagingDesc);
                Publish(mapped, stagingDesc.Width, stagingDesc.Height, desc.Format);
                context->Unmap(g_staging, 0);
                g_lastCopyMs = now;
            }
        }

        if (context) context->Release();
        if (device) device->Release();
        backBuffer->Release();
    }

    // ---- vtable hook ----

    // Defined further down, next to the other structured-exception wrappers.
    void SafeCapture(IDXGISwapChain* swapChain);

    typedef HRESULT(STDMETHODCALLTYPE* PresentFn)(IDXGISwapChain*, UINT, UINT);
    typedef HRESULT(STDMETHODCALLTYPE* Present1Fn)(IDXGISwapChain1*, UINT, UINT, const DXGI_PRESENT_PARAMETERS*);

    PresentFn g_originalPresent = nullptr;
    Present1Fn g_originalPresent1 = nullptr;
    void** g_vtable = nullptr;

    // Reentrancy guard: Capture calls into D3D, and a driver that presents internally would
    // otherwise recurse straight back into us.
    thread_local bool g_inCapture = false;

    HRESULT STDMETHODCALLTYPE HookedPresent(IDXGISwapChain* swapChain, UINT interval, UINT flags)
    {
        if (!g_inCapture)
        {
            g_inCapture = true;
            SafeCapture(swapChain);
            g_inCapture = false;
        }

        return g_originalPresent(swapChain, interval, flags);
    }

    HRESULT STDMETHODCALLTYPE HookedPresent1(
        IDXGISwapChain1* swapChain, UINT interval, UINT flags, const DXGI_PRESENT_PARAMETERS* parameters)
    {
        if (!g_inCapture)
        {
            g_inCapture = true;
            SafeCapture(swapChain);
            g_inCapture = false;
        }

        return g_originalPresent1(swapChain, interval, flags, parameters);
    }

    bool PatchSlot(void** vtable, int index, void* replacement, void** original)
    {
        DWORD previous = 0;
        if (!VirtualProtect(&vtable[index], sizeof(void*), PAGE_EXECUTE_READWRITE, &previous))
        {
            return false;
        }

        *original = vtable[index];
        vtable[index] = replacement;
        VirtualProtect(&vtable[index], sizeof(void*), previous, &previous);
        return true;
    }

    /// <summary>
    /// Builds a throwaway swap chain purely to read its vtable. Every swap chain DXGI hands
    /// out shares that table, so patching it once covers the one Wallpaper Engine is using -
    /// and unlike an inline hook it needs no disassembler and leaves the code untouched.
    /// </summary>
    bool InstallHook()
    {
        WNDCLASSEXW windowClass = {};
        windowClass.cbSize = sizeof(windowClass);
        windowClass.lpfnWndProc = DefWindowProcW;
        windowClass.hInstance = GetModuleHandleW(nullptr);
        windowClass.lpszClassName = L"RedBloomHookProbe";
        RegisterClassExW(&windowClass);

        HWND window = CreateWindowExW(
            0, windowClass.lpszClassName, L"", WS_OVERLAPPEDWINDOW,
            0, 0, 8, 8, nullptr, nullptr, windowClass.hInstance, nullptr);
        if (window == nullptr)
        {
            return false;
        }

        DXGI_SWAP_CHAIN_DESC desc = {};
        desc.BufferCount = 1;
        desc.BufferDesc.Width = 8;
        desc.BufferDesc.Height = 8;
        desc.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        desc.OutputWindow = window;
        desc.SampleDesc.Count = 1;
        desc.Windowed = TRUE;
        desc.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;

        IDXGISwapChain* swapChain = nullptr;
        ID3D11Device* device = nullptr;
        ID3D11DeviceContext* context = nullptr;

        Log("creating probe device");

        const HRESULT hr = D3D11CreateDeviceAndSwapChain(
            nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0,
            D3D11_SDK_VERSION, &desc, &swapChain, &device, nullptr, &context);

        Log("D3D11CreateDeviceAndSwapChain hr=0x%08X swapChain=%p", hr, swapChain);

        bool patched = false;
        if (SUCCEEDED(hr) && swapChain != nullptr)
        {
            g_vtable = *reinterpret_cast<void***>(swapChain);
            Log("vtable=%p", g_vtable);

            // IDXGISwapChain::Present is slot 8, always present.
            patched = PatchSlot(g_vtable, 8, &HookedPresent, reinterpret_cast<void**>(&g_originalPresent));
            Log("patched Present (slot 8): %d, original=%p", patched ? 1 : 0, g_originalPresent);

            // Present1 is slot 22 of the *same* table, but only when the object really is an
            // IDXGISwapChain1. Writing slot 22 of a plain IDXGISwapChain table would scribble
            // past its end and take the host process down - which is exactly what happened the
            // first time this ran.
            IDXGISwapChain1* swapChain1 = nullptr;
            if (SUCCEEDED(swapChain->QueryInterface(__uuidof(IDXGISwapChain1), reinterpret_cast<void**>(&swapChain1)))
                && swapChain1 != nullptr)
            {
                void** vtable1 = *reinterpret_cast<void***>(swapChain1);
                if (vtable1 == g_vtable)
                {
                    PatchSlot(g_vtable, 22, &HookedPresent1, reinterpret_cast<void**>(&g_originalPresent1));
                    Log("patched Present1 (slot 22), original=%p", g_originalPresent1);
                }
                else
                {
                    Log("IDXGISwapChain1 has a separate vtable (%p); leaving Present1 alone", vtable1);
                }

                swapChain1->Release();
            }
            else
            {
                Log("no IDXGISwapChain1; leaving Present1 alone");
            }
        }

        if (swapChain) swapChain->Release();
        if (context) context->Release();
        if (device) device->Release();
        DestroyWindow(window);

        Log("InstallHook done, patched=%d", patched ? 1 : 0);
        return patched;
    }

    // A fault in here would otherwise kill Wallpaper Engine, which is somebody else's process
    // and not ours to crash. Structured exception handling keeps our mistakes to ourselves.
    int LogException(unsigned int code)
    {
        Log("EXCEPTION 0x%08X - swallowed", code);
        return EXCEPTION_EXECUTE_HANDLER;
    }

    void SafeCapture(IDXGISwapChain* swapChain)
    {
        __try
        {
            Capture(swapChain);
        }
        __except (LogException(GetExceptionCode()))
        {
        }
    }

    bool SafeInstallHook()
    {
        __try
        {
            return InstallHook();
        }
        __except (LogException(GetExceptionCode()))
        {
            return false;
        }
    }

    DWORD WINAPI Startup(LPVOID)
    {
        Log("startup thread running");

        const bool shared = OpenSharedMemory();
        Log("shared memory: %d", shared ? 1 : 0);

        SafeInstallHook();
        return 0;
    }
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    switch (reason)
    {
    case DLL_PROCESS_ATTACH:
        DisableThreadLibraryCalls(module);
        Log("attached to pid %lu", GetCurrentProcessId());

        // Nothing heavy under the loader lock: creating a D3D device from DllMain deadlocks.
        CloseHandle(CreateThread(nullptr, 0, Startup, nullptr, 0, nullptr));
        break;

    case DLL_PROCESS_DETACH:
        if (g_vtable != nullptr && g_originalPresent != nullptr)
        {
            void* discard = nullptr;
            PatchSlot(g_vtable, 8, g_originalPresent, &discard);

            if (g_originalPresent1 != nullptr)
            {
                PatchSlot(g_vtable, 22, g_originalPresent1, &discard);
            }
        }
        break;
    }

    return TRUE;
}

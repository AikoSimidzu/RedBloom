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
//
// Keeping Wallpaper Engine responsive: the earlier version did the downscale and the CPU
// read-back on Wallpaper Engine's own render thread, inside Present. That loaded its render
// thread and its GPU queue, and under load stalled it - which showed up as explorer.exe
// hanging cross-process on wallpaper64.exe. This version does almost nothing on that thread:
// it copies the back buffer into one shared texture, guarded by a keyed mutex, and returns.
// A second D3D device we own, driven by our own thread, opens that shared texture and does
// all the mip downscale, the staging copy and the (now freely blocking) Map. Wallpaper
// Engine's thread pays for one GPU copy and two keyed-mutex calls per captured frame and
// nothing else.

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

    void Publish(const D3D11_MAPPED_SUBRESOURCE& mapped, UINT width, UINT height, DXGI_FORMAT format);

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

    // ---- our own device (does the heavy lifting, off Wallpaper Engine's thread) ----

    ID3D11Device* g_ownDevice = nullptr;
    ID3D11DeviceContext* g_ownContext = nullptr;

    // The one texture Wallpaper Engine's thread writes; the same surface, opened on our device,
    // is what our thread reads. A keyed mutex hands it back and forth so the two GPUs' work
    // never overlaps on it.
    ID3D11Texture2D* g_sharedWrite = nullptr;   // on Wallpaper Engine's device
    IDXGIKeyedMutex* g_writeMutex = nullptr;
    ID3D11Texture2D* g_sharedRead = nullptr;    // same surface, on our device
    IDXGIKeyedMutex* g_readMutex = nullptr;

    // Our downscale chain, entirely on our own device.
    ID3D11Texture2D* g_mipTexture = nullptr;
    ID3D11ShaderResourceView* g_mipView = nullptr;
    ID3D11Texture2D* g_staging = nullptr;
    UINT g_mipLevel = 0;

    // What the shared surface is currently built for, so it is only rebuilt on a real change.
    ID3D11Device* g_builtForDevice = nullptr;
    UINT g_builtWidth = 0;
    UINT g_builtHeight = 0;
    DXGI_FORMAT g_builtFormat = DXGI_FORMAT_UNKNOWN;

    ULONGLONG g_lastCopyMs = 0;

    // Serialises the rare rebuild against the reader thread. The present thread only ever
    // *tries* to take it, so Wallpaper Engine never waits on us.
    CRITICAL_SECTION g_lock;
    bool g_lockReady = false;

    void ReleaseSharedTextures()
    {
        if (g_mipView) { g_mipView->Release(); g_mipView = nullptr; }
        if (g_mipTexture) { g_mipTexture->Release(); g_mipTexture = nullptr; }
        if (g_staging) { g_staging->Release(); g_staging = nullptr; }
        if (g_readMutex) { g_readMutex->Release(); g_readMutex = nullptr; }
        if (g_sharedRead) { g_sharedRead->Release(); g_sharedRead = nullptr; }
        if (g_writeMutex) { g_writeMutex->Release(); g_writeMutex = nullptr; }
        if (g_sharedWrite) { g_sharedWrite->Release(); g_sharedWrite = nullptr; }

        g_builtForDevice = nullptr;
        g_builtWidth = 0;
        g_builtHeight = 0;
        g_builtFormat = DXGI_FORMAT_UNKNOWN;
    }

    bool CreateOwnDevice()
    {
        if (g_ownDevice != nullptr)
        {
            return true;
        }

        // BGRA support so a B8G8R8A8 swap chain (the common one) can be handled; the adapter is
        // left to DXGI, which picks the same default one Wallpaper Engine renders on, so the
        // shared surface opens.
        const D3D_FEATURE_LEVEL wanted[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
        const HRESULT hr = D3D11CreateDevice(
            nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            wanted, ARRAYSIZE(wanted), D3D11_SDK_VERSION, &g_ownDevice, nullptr, &g_ownContext);

        Log("own device hr=0x%08X device=%p", hr, g_ownDevice);
        return SUCCEEDED(hr) && g_ownDevice != nullptr;
    }

    /// <summary>
    /// Builds (or rebuilds) the shared surface and our downscale chain for one back-buffer
    /// shape. Runs on Wallpaper Engine's thread but only on a real change, which is rare, and
    /// under the lock so the reader never sees a half-swapped set of textures.
    /// </summary>
    bool EnsureSharedTextures(ID3D11Device* weDevice, const D3D11_TEXTURE2D_DESC& back)
    {
        const DXGI_FORMAT viewFormat = ViewFormat(back.Format);

        if (g_sharedWrite != nullptr
            && weDevice == g_builtForDevice
            && back.Width == g_builtWidth
            && back.Height == g_builtHeight
            && viewFormat == g_builtFormat)
        {
            return true;
        }

        ReleaseSharedTextures();

        if (!CreateOwnDevice())
        {
            return false;
        }

        // The shared surface, created on Wallpaper Engine's device with a keyed mutex so our
        // device can open and synchronise with it.
        D3D11_TEXTURE2D_DESC shared = {};
        shared.Width = back.Width;
        shared.Height = back.Height;
        shared.MipLevels = 1;
        shared.ArraySize = 1;
        shared.Format = viewFormat;
        shared.SampleDesc.Count = 1;
        shared.Usage = D3D11_USAGE_DEFAULT;
        shared.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        shared.MiscFlags = D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;

        if (FAILED(weDevice->CreateTexture2D(&shared, nullptr, &g_sharedWrite)))
        {
            Log("CreateTexture2D(shared) failed");
            ReleaseSharedTextures();
            return false;
        }

        // Hand the surface to our device by its shared handle.
        IDXGIResource* resource = nullptr;
        HANDLE handle = nullptr;
        if (SUCCEEDED(g_sharedWrite->QueryInterface(__uuidof(IDXGIResource), reinterpret_cast<void**>(&resource))))
        {
            resource->GetSharedHandle(&handle);
            resource->Release();
        }

        if (handle == nullptr
            || FAILED(g_ownDevice->OpenSharedResource(handle, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&g_sharedRead))))
        {
            Log("OpenSharedResource failed (handle=%p)", handle);
            ReleaseSharedTextures();
            return false;
        }

        if (FAILED(g_sharedWrite->QueryInterface(__uuidof(IDXGIKeyedMutex), reinterpret_cast<void**>(&g_writeMutex)))
            || FAILED(g_sharedRead->QueryInterface(__uuidof(IDXGIKeyedMutex), reinterpret_cast<void**>(&g_readMutex))))
        {
            Log("keyed mutex query failed");
            ReleaseSharedTextures();
            return false;
        }

        // Our downscale chain: a mip texture we generate down, then a staging copy to read.
        g_mipLevel = 0;
        while ((back.Width >> g_mipLevel) > REDBLOOM_MAX_WIDTH
               || (back.Height >> g_mipLevel) > REDBLOOM_MAX_HEIGHT)
        {
            g_mipLevel++;
        }

        const UINT width = max(1u, back.Width >> g_mipLevel);
        const UINT height = max(1u, back.Height >> g_mipLevel);

        D3D11_TEXTURE2D_DESC mip = {};
        mip.Width = back.Width;
        mip.Height = back.Height;
        mip.MipLevels = g_mipLevel + 1;
        mip.ArraySize = 1;
        mip.Format = viewFormat;
        mip.SampleDesc.Count = 1;
        mip.Usage = D3D11_USAGE_DEFAULT;
        mip.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
        mip.MiscFlags = D3D11_RESOURCE_MISC_GENERATE_MIPS;

        if (FAILED(g_ownDevice->CreateTexture2D(&mip, nullptr, &g_mipTexture)))
        {
            ReleaseSharedTextures();
            return false;
        }

        D3D11_SHADER_RESOURCE_VIEW_DESC view = {};
        view.Format = viewFormat;
        view.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
        view.Texture2D.MipLevels = mip.MipLevels;

        if (FAILED(g_ownDevice->CreateShaderResourceView(g_mipTexture, &view, &g_mipView)))
        {
            ReleaseSharedTextures();
            return false;
        }

        D3D11_TEXTURE2D_DESC staging = {};
        staging.Width = width;
        staging.Height = height;
        staging.MipLevels = 1;
        staging.ArraySize = 1;
        staging.Format = viewFormat;
        staging.SampleDesc.Count = 1;
        staging.Usage = D3D11_USAGE_STAGING;
        staging.CPUAccessFlags = D3D11_CPU_ACCESS_READ;

        if (FAILED(g_ownDevice->CreateTexture2D(&staging, nullptr, &g_staging)))
        {
            ReleaseSharedTextures();
            return false;
        }

        g_builtForDevice = weDevice;
        g_builtWidth = back.Width;
        g_builtHeight = back.Height;
        g_builtFormat = viewFormat;
        Log("shared surface built %ux%u mip=%u -> %ux%u", back.Width, back.Height, g_mipLevel, width, height);
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

    // ---- the tiny part that runs on Wallpaper Engine's thread ----

    void Capture(IDXGISwapChain* swapChain)
    {
        if (!g_lockReady || !OpenSharedMemory())
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

        // Never wait on the reader. If it is mid-frame, skip this present - Wallpaper Engine's
        // render thread must not block on us, which is the whole point of this rewrite.
        if (!TryEnterCriticalSection(&g_lock))
        {
            return;
        }

        __try
        {
            ID3D11Device* device = nullptr;
            ID3D11DeviceContext* context = nullptr;
            swapChain->GetDevice(__uuidof(ID3D11Device), reinterpret_cast<void**>(&device));
            if (device != nullptr)
            {
                device->GetImmediateContext(&context);
            }

            if (device == nullptr || context == nullptr)
            {
                if (context) context->Release();
                if (device) device->Release();
                __leave;
            }

            ID3D11Texture2D* backBuffer = nullptr;
            if (SUCCEEDED(swapChain->GetBuffer(0, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&backBuffer))))
            {
                D3D11_TEXTURE2D_DESC desc = {};
                backBuffer->GetDesc(&desc);

                if (desc.SampleDesc.Count == 1 && IsSupported(desc.Format) && EnsureSharedTextures(device, desc))
                {
                    // Take the surface with key 0. If the reader still holds it (key 1), skip -
                    // we will get the next present.
                    if (g_writeMutex->AcquireSync(0, 0) == WAIT_OBJECT_0)
                    {
                        context->CopyResource(g_sharedWrite, backBuffer);
                        g_writeMutex->ReleaseSync(1); // hand to the reader
                        g_lastCopyMs = now;
                    }
                }

                backBuffer->Release();
            }

            context->Release();
            device->Release();
        }
        __finally
        {
            LeaveCriticalSection(&g_lock);
        }
    }

    // ---- the reader thread: everything heavy, on our own device ----

    volatile bool g_readerRunning = false;

    void ReadOneFrame()
    {
        if (!TryEnterCriticalSection(&g_lock))
        {
            return;
        }

        __try
        {
            if (g_readMutex == nullptr || g_sharedRead == nullptr || g_ownContext == nullptr)
            {
                __leave;
            }

            // Take the surface only if the writer has handed it over (key 1). Non-blocking, so a
            // stretch with no new frame just falls through and the loop sleeps.
            if (g_readMutex->AcquireSync(1, 0) != WAIT_OBJECT_0)
            {
                __leave;
            }

            // Copy the shared surface into our own mip texture, then let the writer have the
            // surface straight back - everything past here is on textures we own alone.
            g_ownContext->CopySubresourceRegion(g_mipTexture, 0, 0, 0, 0, g_sharedRead, 0, nullptr);
            g_readMutex->ReleaseSync(0);

            if (g_mipLevel > 0)
            {
                g_ownContext->GenerateMips(g_mipView);
            }

            g_ownContext->CopySubresourceRegion(g_staging, 0, 0, 0, 0, g_mipTexture, g_mipLevel, nullptr);

            // Blocking Map is fine now: it is our device and our thread, so no one else waits.
            D3D11_MAPPED_SUBRESOURCE mapped = {};
            if (SUCCEEDED(g_ownContext->Map(g_staging, 0, D3D11_MAP_READ, 0, &mapped)))
            {
                D3D11_TEXTURE2D_DESC stagingDesc = {};
                g_staging->GetDesc(&stagingDesc);
                Publish(mapped, stagingDesc.Width, stagingDesc.Height, stagingDesc.Format);
                g_ownContext->Unmap(g_staging, 0);
            }
        }
        __finally
        {
            LeaveCriticalSection(&g_lock);
        }
    }

    DWORD WINAPI ReaderThread(LPVOID)
    {
        Log("reader thread running");
        while (g_readerRunning)
        {
            __try
            {
                ReadOneFrame();
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                Log("reader EXCEPTION 0x%08X - swallowed", GetExceptionCode());
            }

            // Idle politely; the writer's interval throttles the real rate anyway.
            const uint32_t interval = (g_header && g_header->IntervalMs) ? g_header->IntervalMs : 33;
            Sleep(max(5u, interval / 2));
        }
        return 0;
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

        InitializeCriticalSection(&g_lock);
        g_lockReady = true;

        const bool shared = OpenSharedMemory();
        Log("shared memory: %d", shared ? 1 : 0);

        // Our own device and the reader thread come up before the hook, so the first captured
        // present already has somewhere to hand its copy.
        CreateOwnDevice();
        g_readerRunning = true;
        CloseHandle(CreateThread(nullptr, 0, ReaderThread, nullptr, 0, nullptr));

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
        // Stop the reader before anything it touches goes away.
        g_readerRunning = false;

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

# RedBloom

RedBloom is a Windows terminal emulator with a built-in AI agent.
It hosts local shells and SSH sessions in tabs and split panes, renders them with xterm.js
inside WebView2, and lets a language model run commands in the same session.

Built with C# and WPF on .NET 10 (net10.0-windows). WinForms is referenced only for the
tray NotifyIcon; its implicit usings are disabled so they do not shadow WPF types.

## Features

- Tabs and split panes with draggable splitters and animated sizing
- Local shells through ConPTY: cmd, Windows PowerShell, pwsh, WSL or any custom profile
- SSH sessions through SSH.NET, with known_hosts, SHA-256 fingerprint prompt and port forwarding
- AI agent chat backed by the Anthropic API or any OpenAI-compatible endpoint
- The agent can run shell commands, request elevation and receive image attachments
- The model list is fetched from the endpoint, so proxies and new models work without a rebuild
- Theming: colors, per-surface opacity, fonts, Mica or Acrylic backdrop, custom window chrome
- Live wallpaper as terminal background, including Wallpaper Engine capture
- Tray icon, Explorer context menu integration, English and Russian localization
- Session secrets protected with DPAPI in CurrentUser scope


## Layout

```
RedBloom/
  MainWindow.xaml(.cs)      shell of the app: tabs, panes, sidebar, settings host
  TerminalTab.cs            one tab: backend + view + state
  Controls/                 hand-written controls, no third-party UI libraries
    SplitContainer.cs       recursive split layout
    TabStripPanel.cs        tab strip with drag and reorder
    TerminalView.cs         WebView2 host for xterm.js
    AgentChatView.cs        WebView2 host for the agent chat
    ColorPickerPopup.cs, GridLengthAnimation.cs, BackdropHost.cs
  Terminal/
    ITerminalBackend.cs     common contract for anything that reads and writes a PTY
    ConPtyBackend.cs        local shells; ConPtyNative.cs holds the P/Invoke layer
    SshBackend.cs           SSH.NET based backend; SshConnection.cs, SshHostKey.cs
    ShellProfile.cs         discovered and user-defined shell profiles
  Services/
    Ai/                     transports, model catalog, command runner, markdown, chats
    ThemeService.cs         settings.json, brush resources, debounced save
    SessionStore.cs         sessions.json
    KnownHostsStore.cs      known_hosts.json
    ChatStore.cs            one JSON file per chat
    Secrets.cs              DPAPI protect/unprotect
    Elevation.cs, ElevatedHost.cs   elevated command execution
    WallpaperCapture.cs, WallpaperEngineCapture.cs, LiveWallpaper*.cs
    LocalizationService.cs, TrayManager.cs, ShellIntegration.cs
  Views/                    settings pages and dialogs
  Models/                   settings, sessions, agents, chat sessions, enums
  Interop/                  Dwm.cs (backdrop, custom chrome), MaximizeBounds.cs
  Assets/                   terminal.html, chat.html, xterm bundle
native/RedBloomHook/
  dllmain.cpp               in-process D3D11 Present hook for Wallpaper Engine capture
  Shared.h, build.ps1
```

## Building

Requirements: .NET 10 SDK, Visual Studio 2022 with the C++ toolchain (for the native hook),
and the WebView2 runtime, which is present on current Windows installs.

```
dotnet build RedBloom.sln -c Release
```

The capture hook is not part of the solution and is built separately:

```
powershell -ExecutionPolicy Bypass -File native\RedBloomHook\build.ps1
```

It locates the toolchain with vswhere, calls vcvars64.bat and produces
native\RedBloomHook\bin\RedBloomHook.dll. The build is x64 only: Wallpaper Engine is a
64-bit process and would refuse to load a 32-bit DLL.


## Stored data

Configuration lives in `%APPDATA%\RedBloom`:

| File | Contents |
|---|---|
| `settings.json` | theme, colors, opacity, fonts, backdrop, language |
| `sessions.json` | saved SSH sessions and port forwards |
| `known_hosts.json` | accepted host keys with SHA-256 fingerprints |
| `*.json` (chats) | one file per chat, written atomically through a .tmp file |

Chats prefer a `Chats` folder next to the executable when that folder is writable, which makes
portable installs work; otherwise they fall back to `%APPDATA%` and existing files are
migrated.

Passwords and API keys are never written in plain text. `Secrets.cs` wraps DPAPI
(`ProtectedData`, `CurrentUser` scope) with a fixed entropy tag, so the encrypted values are
only readable by the same Windows user on the same machine.

## Notes on wallpaper capture

Capturing an animated wallpaper is harder than it looks. The wallpaper surface and the desktop
icons are sibling child windows of `Progman`, while every capture API in Windows works at the
level of a top-level window. `PrintWindow` on `WorkerW` therefore returns the wallpaper with
the icons painted on top, `PrintWindow` on the DX11 child returns a black frame, and Graphics
Capture rejects a child window with `E_INVALIDARG`.

The working approach is the one OBS uses for game capture: inject into the wallpaper process
and copy the back buffer at `Present`. `dllmain.cpp` hooks the swap chain and does exactly one
GPU copy into a shared texture guarded by a keyed mutex. Everything after that (downscale,
read-back, format conversion) happens on RedBloom's own D3D device and thread.

This split matters. An earlier version did the downscale and CPU read-back inside `Present`,
on the render thread of Wallpaper Engine. Under load that stalled `explorer.exe` in a
cross-process wait on `wallpaper64.exe` and froze the desktop. Keep foreign threads doing as
little as possible.

## Security

The agent executes commands on the local machine with the privileges of the user running
RedBloom, and can request elevation. Commands are surfaced for approval before they run.
Treat an agent endpoint the same way you would treat anyone with shell access: only point it
at services you trust.

## Status

Personal project, developed on the `master` branch. There is no test suite and no release
pipeline yet.


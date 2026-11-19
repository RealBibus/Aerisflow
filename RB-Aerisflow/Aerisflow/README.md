# Aerisflow

Snow on your desktop — it's snowing on your screen.

A small recreation of the [Aerisflow](https://github.com/mbezruchko/Aerisflow) idea: a tiny app that
lives in the system tray and lets soft snow drift across your screen, settle along the tops of your
windows and slip off when you drag them.

## Features

- **Falls** — every flake keeps its own pace and sways in a slow, gusting breeze. Presets (far, mid,
  near, powder, lump) drive the size, fall speed, brightness and sway, and particles are plain structs,
  so the simulation runs without allocating as snow spawns.
- **Settles** — each flake starts a little heap where it lands, and the heaps build into procedural
  drifts at the bottom of the screen, on the taskbar and on top of open windows, slumping to a natural
  slope as they grow and spilling over an edge once a column can't hold its angle.
- **Sticks** — snow is glued to the surface it lies on. Drag a window and the bank rides it rigidly with
  no lag and no accidental shedding, exactly as if it were frozen to the glass.
- **Shakes** — a real shake or a hard yank breaks the grip: whole columns slide off the ends, crumble
  into lumps and fall. Jerk a window upward and its snow lifts, flies up with it and comes back down;
  jerk hard enough and it tears off entirely.
- **Drops** — close or minimize a window and its bank lets go at once, falling in clumps that burst into
  powder where they land, so a closed window's snow lands on the next surface below.
- **Parts** — sweep the cursor through a drift and it digs a hollow and throws up powder; falling flakes
  scatter out of the way gently instead of flying.
- **Hides** — a bank is only drawn where its window's top edge is actually visible, never across a window
  standing in front of it.
- **Storm** — the blizzard preset adds a dense veil of tiny flakes and soft white powder drifting sideways,
  like a windy, half-whiteout evening.
- **Frost** — a procedural rim of fern-like crystals slowly grows along the screen edges. It can be turned
  off separately from the snow.
- **Fullscreen Off** — when the fullscreen option is off, only falling flakes are drawn: no window ledges
  and no taskbar bank.

## Rendering

The primary renderer is a native, click-through overlay: a borderless window with
`WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOREDIRECTIONBITMAP`, filled by a DirectComposition visual
drawn with Direct2D on a D3D11 device. No WPF visual tree sits under the snow, so flakes and drifts
composite straight through the GPU over every window, the taskbar included, and the overlay is re-raised
periodically so later topmost windows can't cover it. The overlay passes hit-testing through to the
desktop, so apps and the taskbar stay clickable.

If Direct3D 11 or DirectComposition can't start, or the device is lost later, the app falls back to the
WPF renderer in `Snow/SnowCanvas.cs` using the same engine and window tracker.

## Snow on the taskbar

Snow settles on the top edge of the taskbar — the actual floor of the working area (DWM work area) —
and not on the raw bottom of the display. The ground is re-checked every second, so a moved, re-docked or
auto-hiding taskbar (top, bottom, left or right) moves the snow floor accordingly.

## Tray menu

Clicking the tray icon opens a Windows 11 style flyout: a borderless window (`None` style, no resize,
hidden from the taskbar) whose single root card uses a dark translucent background (`#B01C1C1C`) with a
hairline border (`#25FFFFFF`). Corners are rounded through the DWM corner preference and the shell
backdrop uses the transient (acrylic) window type; the card fades and slides in over 160 ms. It drops to
the bottom right of the working area, just above the taskbar.

The card is minimal: intensity slider with pink accent, opacity sub-row, mode, weather sync, melt switch
and a melt-after delay — tap a row to expand a sub-control, chevrons cycle values, all explanations live in
system tooltips. Clear and quit actions sit below a hairline separator.

The flyout is created on demand and released as soon as it closes, so an idle app keeps none of its
visual tree in memory. While it is open the snow overlay stays below it.

## Building

Requires .NET 10 SDK and Windows.

```
dotnet build
dotnet run
```

## Installer

`AerisflowSetup.exe` is a custom WPF installer with the same dark acrylic card as the tray flyout. It
installs for the current user into `%LocalAppData%\Aerisflow` (no administrator rights), preserves
`settings.json` on reinstall, creates Start menu and desktop shortcuts, registers the app in Windows
Settings > Apps, and can enable Start with Windows.

Build it with:

```
powershell -ExecutionPolicy Bypass -File build-installer.ps1
```

The result is `build\dist\AerisflowSetup.exe`. Add `-FrameworkDependent` for a much smaller installer
that requires the .NET 10 desktop runtime.

Start with Windows is written to the per-user `Run` key and resolves the real executable path (the
stable `Launcher.exe` when an update root is present, otherwise the current app exe). Stale
`StartupApproved` records are cleared too, so an entry disabled by Task Manager starts working again
when the switch is turned on.

## Automatic updates

The app reads releases from `RealBibus/Aerisflow` on GitHub. Every installed build checks on launch
(after 20 seconds) and then every two hours:

1. `manifest.json` and `manifest.sig` are downloaded from the newest non-prerelease GitHub release.
2. The RSA-SHA256 signature is verified with the public key baked into the app; unsigned or modified
   manifests are ignored.
3. Every listed file is verified against its SHA-256. Files that did not change are copied from the
   current version, changed files are downloaded.
4. The complete version is staged into `app-<version>` without touching the running app.
5. On sign-out, shutdown or a normal exit the staged version is written to `current.json`. The next
   launch uses it through `Launcher.exe`. A staged version is also adopted at startup if the previous
   shutdown was abrupt.

There is no modal prompt: the download happens quietly in the background and the tray flyout only
shows a "installs on shutdown" status. `Restart now` is still available for impatient users.

### Publishing a release

`build-installer.ps1` also creates a signed update bundle in `build\release\<version>`:

```
AerisflowSetup.exe   installer for new users
manifest.json        update manifest (version, hashes, notes)
manifest.sig         RSA signature of the manifest
Aerisflow.exe        changed app file (asset)
Aerisflow.ico        unchanged asset
UPLOAD.txt           exact asset list
```

Create a GitHub release tagged `v<version>` in `RealBibus/Aerisflow`, mark it as a normal (not
pre-release) release, and upload every file from that folder. The update signing key stays private in
`Aerisflow\Keys\private.pem`; only `public.txt` (already compiled into the app) may be published.

> The setup and the app are not Authenticode-signed yet (no code-signing certificate is installed on
> this machine). Windows SmartScreen may show an "unknown publisher" warning on first run. The update
> manifest itself is always cryptographically signed, so update payloads cannot be swapped silently.

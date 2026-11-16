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

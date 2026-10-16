<p align="center">
  <img src="assets/icon.png" width="112" alt="HeadsetAutoSwitch icon">
</p>

<h1 align="center">HeadsetAutoSwitch</h1>

<p align="center">
  Turn your wireless headset off → sound moves to your speakers.<br>
  Turn it back on → sound comes back. Automatically.
</p>

<p align="center">
  <a href="https://github.com/bahaa-b7a/HeadsetAutoSwitch/releases/latest/download/HeadsetAutoSwitch.exe"><b>Download</b></a> ·
  <a href="https://bahaa-b7a.github.io/HeadsetAutoSwitch/">Website</a> ·
  <a href="#how-it-works">How it works</a>
</p>

---

## Why

Headsets like the **SteelSeries Arctis Nova 5** use a 2.4 GHz USB dongle. When you switch the headset off,
the dongle stays plugged in, so Windows still thinks the headset is there and keeps sending sound into the void.
HeadsetAutoSwitch asks the dongle whether the headset is actually on and switches the default output for you.

## Features

- **Tiny** – one ~50 KB exe, ~2 MB RAM, effectively 0% CPU
- **Nothing to install** – no runtime, no driver, no admin rights, no registry changes
- **No window, no tray clutter** – it just runs quietly in the background
- **Fast** – switches within ~4 seconds
- **Respects you** – only acts when the headset turns on/off; if you pick another output yourself, it leaves it alone
- **Offline** – no network access, no telemetry

## Install

1. Download **[HeadsetAutoSwitch.exe](https://github.com/bahaa-b7a/HeadsetAutoSwitch/releases/latest/download/HeadsetAutoSwitch.exe)**
2. Double-click it. Done.

It copies itself to `%LOCALAPPDATA%\HeadsetAutoSwitch`, adds a shortcut to your Startup folder and starts running.

> Windows SmartScreen may warn about an unknown app because the exe isn't code-signed.
> Click **More info → Run anyway**, or build it yourself from source (below) — it takes two seconds.

### Uninstall

```
%LOCALAPPDATA%\HeadsetAutoSwitch\HeadsetAutoSwitch.exe --uninstall
```

Paste that into **Win + R**. It stops the app and removes the shortcut and files.

## Configure

By default it switches between an output whose name contains **`Arctis Nova 5`** and one containing **`Realtek`**
(most laptop speakers). If your names differ, pass them when installing:

```
HeadsetAutoSwitch.exe "Arctis Nova 5" "Speakers"
```

Any part of the name shown in *Settings → System → Sound* works.

## Supported headsets

| Headset | USB ID | Status |
|---|---|---|
| SteelSeries Arctis Nova 5 | `1038:2232` | ✅ tested |
| SteelSeries Arctis Nova 5X | `1038:2253` | should work (same protocol) |

Bluetooth and wired headsets don't need this — Windows already switches when they disconnect.
Other dongle headsets can be added if their status protocol is known (PRs welcome).

## How it works

```
every 2 s ──► dongle HID interface 3 (usage page 0xFFC0)
              send  [00 B0]                 status request
              read  [00 B0 SS ...]          SS = 02 → headset off
                                            SS = 03 → headset on
state changed (2 reads in a row) ──► IPolicyConfig.SetDefaultEndpoint(...)
```

- **Detection** – the same status request SteelSeries GG uses for the battery readout.
  Protocol documented by the excellent [HeadsetControl](https://github.com/Sapd/HeadsetControl) project.
- **Switching** – Windows' `IPolicyConfig` COM interface (the one the Sound control panel uses),
  for the console, multimedia and communications roles.
- **Lightweight** – plain Win32 P/Invoke on .NET Framework 4 (built into Windows 10/11), working set trimmed after each switch.

## Build from source

No SDK needed — Windows ships a C# compiler.

```
git clone https://github.com/bahaa-b7a/HeadsetAutoSwitch
cd HeadsetAutoSwitch
build.bat
```

Output goes to `bin\`. `HeadsetAutoSwitch-debug.exe --test` runs in a console and prints what the dongle reports,
handy for troubleshooting or adding a new headset.

The icon is generated from code: `tools\MakeIcon.cs` renders `assets\icon.ico` / `icon.png` (same design as `assets\icon.svg`).

## License

[MIT](LICENSE)

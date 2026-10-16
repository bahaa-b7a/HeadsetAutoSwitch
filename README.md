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
  <a href="#supported-headsets">Supported headsets</a> ·
  <a href="#any-other-headset-teach-it">Teach it yours</a>
</p>

---

## Why

Wireless headsets with a 2.4 GHz USB dongle (SteelSeries, Logitech, Corsair, HyperX, …) have a problem on Windows:
when you switch the headset off, **the dongle stays plugged in**, so Windows still thinks the headset is there and
keeps sending sound into the void. HeadsetAutoSwitch asks the dongle whether the headset is actually on and switches
the default output for you.

## Features

- **Works with many dongle headsets out of the box**, and you can teach it any other one in a minute
- **Zero setup**: finds your headset's audio device and your built-in speakers by itself
- **Puts things back the way they were**: restores whatever you were using before (Game/Chat outputs, NVIDIA Broadcast, Sonar…)
- **Tiny**: one ~90 KB exe, ~4 MB RAM, effectively 0% CPU
- **Nothing to install**: no runtime, driver, admin rights or registry changes
- **Out of your way**: no window and no tray icon. It only acts when the headset turns on or off.
- **Offline**: no network access, no telemetry

## Install

1. Download **[HeadsetAutoSwitch.exe](https://github.com/bahaa-b7a/HeadsetAutoSwitch/releases/latest/download/HeadsetAutoSwitch.exe)**
2. Double-click it. Done.

It copies itself to `%LOCALAPPDATA%\HeadsetAutoSwitch`, adds itself to your Startup folder and starts running.
If your headset isn't recognised, it offers to learn it right away.

> Windows SmartScreen may warn about an unknown app because the exe isn't code-signed.
> Click **More info → Run anyway**, or build it yourself from source (below). It takes two seconds.

**Uninstall:** press <kbd>Win</kbd>+<kbd>R</kbd> and run `%LOCALAPPDATA%\HeadsetAutoSwitch\HeadsetAutoSwitch.exe --uninstall`

## Supported headsets

These work out of the box. The protocols come from the excellent [HeadsetControl](https://github.com/Sapd/HeadsetControl) project.

| Brand | Headsets | Status |
|---|---|---|
| SteelSeries | Arctis Nova 5 / 5X | ✅ tested |
| SteelSeries | Arctis Nova 7 / 7X / 7P (all versions), Nova 3P / 3X Wireless, Arctis 7+, Nova Pro Wireless, Arctis 1 / 7X / 7P Wireless, Arctis Pro Wireless | built in |
| Logitech | G533, G535, G633, G635, G733, G933, G935, G PRO, G PRO X | built in |
| Corsair | Void Wireless family (Void / Pro / RGB Elite / Elite), Virtuoso XT / SE | built in |
| HyperX | Cloud Alpha Wireless | built in |
| **Any Bluetooth headset** | or any dongle whose Windows audio device disappears when the headset is off | automatic (endpoint mode) |
| **Anything else** | | [teach it](#any-other-headset-teach-it) |

"Built in" means the rule is implemented from documented protocols but hasn't been confirmed on real hardware yet.
If yours works (or doesn't), please [open an issue](https://github.com/bahaa-b7a/HeadsetAutoSwitch/issues) and add it to the table.

## Any other headset? Teach it

```
%LOCALAPPDATA%\HeadsetAutoSwitch\HeadsetAutoSwitch.exe --learn
```

The wizard asks you to turn the headset **off** and back **on** once. Meanwhile it records:

1. **Windows audio devices.** Does the headset's output appear and disappear?
2. **Status requests.** It sends the read-only status queries known for your dongle's brand and compares the replies.
3. **Messages from the dongle.** Many dongles announce "headset connected" or "disconnected" on their own.

It then picks the signal that cleanly separates *on* from *off*, saves it to `config.ini` and starts using it.
If nothing is found it saves `learn-log.txt`. Attach that to an issue and support can be added for everyone.

## How it works

```
every 2 s ──► ask the dongle (vendor HID interface)        e.g. SteelSeries:  send 00 B0
              read the status byte                               reply  00 B0 02 … → off
                                                                         00 B0 03 … → on
state changed ──► remember what you were listening to, set the new default output
                  (console, multimedia and communications roles)
```

- **Finding the right audio device.** The dongle's HID interface and its audio output share a Windows
  *ContainerId*, so the app always knows which output belongs to the headset. No names to configure.
- **Choosing the fallback.** It uses the output you used last time the headset was off. The first time, it
  picks your built-in speakers and skips virtual devices like NVIDIA Broadcast or VB-Cable.
- **Switching.** It uses Windows' `IPolicyConfig` COM interface, the one the Sound control panel uses.
- **Safety.** An "off" reading must be seen twice in a row, and unknown or garbled replies never trigger a switch.

## Configure (optional)

`%LOCALAPPDATA%\HeadsetAutoSwitch\config.ini`:

```ini
headset=Arctis        ; part of the headset output's name, if it isn't found automatically
speakers=Realtek      ; part of the fallback output's name
rule=name="My headset" vid=1038 pid=2232 page=FFC0 req=00B0 match=00B0 at=2 off=02
```

A `rule` line says how to ask a dongle: `req` = bytes to send (report ID first), `match` = how the reply starts,
`at` = which byte holds the status, `off` / `on` = the values for each state. `mode=listen` waits for
messages from the dongle instead of asking, `timeout=off` treats "no reply" as off, and `mode=endpoint id=…` watches a
Windows audio device. Restart the app after editing (just run the installer again).

## Troubleshooting

| | |
|---|---|
| `HeadsetAutoSwitch-debug.exe --list` | shows detected dongles, audio outputs and the rule in use |
| `HeadsetAutoSwitch-debug.exe --test` | runs in a console and prints every reading and switch |
| `%LOCALAPPDATA%\HeadsetAutoSwitch\log.txt` | what the background app did |

The debug build is attached to every [release](https://github.com/bahaa-b7a/HeadsetAutoSwitch/releases).

## Build from source

No SDK needed. Windows ships a C# compiler.

```
git clone https://github.com/bahaa-b7a/HeadsetAutoSwitch
cd HeadsetAutoSwitch
build.bat
```

The output goes to `bin\`. The icon is generated from code: `tools\MakeIcon.cs` renders `assets\icon.ico` and
`assets\icon.png` from the same design as `assets\icon.svg`.

## Adding a headset to the built-in list

Add one line to `Rule.BuiltIn` in [`src/HeadsetAutoSwitch.cs`](src/HeadsetAutoSwitch.cs), in the same format as
`config.ini`. If you ran `--learn`, the line it wrote to your `config.ini` is exactly what's needed. PRs welcome!

## License

[MIT](LICENSE) · vibe coded with ❤️ by [bahaa-b7a](https://github.com/bahaa-b7a)

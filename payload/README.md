# SwitchDeck payload

Bare-metal Nintendo Switch payload (launched from hekate) that turns the console into a USB touch screen
for [SysDeck](../README.md). / Payload bare-metal (lancé depuis hekate) qui transforme la Switch en écran
tactile USB pour [SysDeck](../README.fr.md).

| | |
|---|---|
| `src/main.c` | Entry point, waiting / session loop, VOL± menu, and power/boot actions |
| `src/deck_link.c`, `src/deck_proto.h` | USB session and protocol (mirrored in `SysDeck/Deck/DeckProtocol.cs`), including Switch power/RCM and hekate actions |
| `src/screen.c` | 1280×720 landscape framebuffer (VIC rotation); battery and external power status |
| `src/night.c` | Night mode: picture + clock from the RTC while the PC is off |
| `src/usb_descriptors.c`, `src/btn.c`, `src/start.S` | Modified copies of hekate BDK files (take priority via `VPATH`) |

Build: `build.sh` (devkitARM, downloads hekate v6.5.4 into `../third_party/hekate` if missing).
Output: `output/switchdeck.bin` → `sd:/bootloader/payloads/`.

License: GPL-2.0 (see [LICENSE](LICENSE)), as a derivative of the hekate BDK.

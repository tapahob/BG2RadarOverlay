# In-game radar for BG2EE on Windows

Close the game before installing. Copy BG2Radar.asi into the same directory as Baldur.exe. The module uses the x64 Ultimate ASI Loader. If you already use that loader, keep it. For a clean installation, also copy the supplied winmm.dll from GameFiles alongside Baldur.exe. If another mod already provides winmm.dll, leave that file in place and check its loader compatibility first.

For automatic startup, create a BG2Radar subfolder beside Baldur.exe and place the updated BG Radar Overlay.exe inside it. Launch the game normally through Steam, GOG or a shortcut. The module starts the companion once and the companion exits when that game closes. Settings and radar.log for automatic launches live in %LOCALAPPDATA%\BG2RadarOverlay. To reuse an existing configuration, copy your config.cfg there. Translations are built into the executable. Optional Locales files beside the executable override those translations.

Manual startup from a different folder also works. The module draws the list, radar button and open creature cards into the OpenGL or Direct3D 9 frame. Fullscreen and the graphics backend stay under the game's control. The ASI module works independently of EEex and Twitch.

Click a list row to inspect a creature. Right-click a card to dismiss it. Cards follow the pointer while dragging. The radar button and enemy list slide horizontally and remember their position on release. The pointing and gripping gloves use the game's cursor artwork. Right-click the radar button to open the settings panel inside the game window. The panel receives mouse and keyboard input while the game stays fullscreen.

On a composed display the module attaches a DirectComposition visual to the game window. The radar can update between game frames, at the display's refresh rate. A true exclusive Direct3D 9 device or an unavailable composition device uses the game's own presents. When the companion closes, its last image expires within 1.5 seconds.

To return to the desktop overlay, close BG2EE and remove BG2Radar.asi from the game folder. Remove winmm.dll only if you installed it for this module and no other mod uses it.

## Building

Use an x64 Visual Studio C++ toolchain with a Windows SDK, CMake and Git. From the repository root, run the commands below in a developer shell. CMake fetches the pinned MinHook revision.

```powershell
cmake -S NativeRadar -B obj/NativeRadar -A x64
cmake --build obj/NativeRadar --config Release
cmake --install obj/NativeRadar --config Release --prefix bin/NativeRadar
```

For a bundle with the loader, place the official x64 Ultimate ASI Loader in bin/NativeRadar as winmm.dll and include its MIT license there as Loader-LICENSE.txt. The tested release is [Ultimate ASI Loader v10.0.4](https://github.com/ThirteenAG/Ultimate-ASI-Loader/releases/tag/v10.0.4). The release's Ultimate-ASI-Loader_x64.zip contains dinput8.dll. Its SHA-256 after extraction is 0cdd02c8e02e8f79a036dc0ec36adb040c5011a1030a0cf956188f9111d9199d.

Then run build.bat as usual. Publishing picks up the staged module and notices in GameFiles. A standalone module build needs an existing x64 ASI loader in the game.

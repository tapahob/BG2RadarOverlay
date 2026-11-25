# In-game radar for BG2EE on Windows

For manual installation, close the game first. Copy BG2Radar.asi into the same directory as Baldur.exe. The module uses the x64 Ultimate ASI Loader. If you already use that loader, keep it. For a clean installation, also copy the supplied winmm.dll from GameFiles alongside Baldur.exe. If another mod already provides winmm.dll, leave that file in place and check its loader compatibility first.

Radar installs the native module the first time it connects to a new game installation. The setup window shows the game and overlay paths and asks you to restart the game. You can also select **Inject radar natively** in the Games tab. New installations record the full path of the Radar executable in `BGRadar/companion.ini`. Radar refreshes this path for registered games when the executable moves. Existing executable links in BGRadar and manual installations in BG2Radar also work. Launch the game normally through Steam, GOG or a shortcut. The module starts the companion once and the companion exits when that game closes. Settings and radar.log for automatic launches live in %LOCALAPPDATA%\BG2RadarOverlay. To reuse an existing configuration, copy your config.cfg there. Translations are built into the executable. Optional Locales files beside the executable override those translations.

Manual startup from a different folder also works. The module draws the list, radar button and open creature cards into the OpenGL or Direct3D 9 frame. Fullscreen and the graphics backend stay under the game's control. The ASI module works independently of EEex and Twitch.

Click a list row to inspect a creature. Right-click a card to dismiss it. Cards follow the pointer while dragging. The radar button and enemy list slide horizontally and remember their position on release. The pointing and gripping gloves use the game's cursor artwork. Right-click the radar button to open the settings panel inside the game window. The panel receives mouse and keyboard input while the game stays fullscreen.

In a regular window or the radar's borderless mode, the companion displays its live WPF panels in a transparent, click-through child window. In OBS, select the game in Window Capture and use the Windows Graphics Capture (WGC) method to include the cards. Dragging and animations can update between game frames. For SDL's monitor-sized fullscreen popup the companion uses a separate owned window. Exclusive Direct3D 9 still draws the radar into the game frame.

For OpenGL fullscreen in OBS, keep the game as the bottom source. While the game is fullscreen, add a Window Capture above it for **BG Radar - Fullscreen Overlay**. Use Modern (WGC), match by exact window title, and turn off cursor capture for this source. Give it the same placement and scale as the game. The transparent layer contains the live radar and can update even between game frames. A Window Capture of the game underneath also includes the child radar when you switch back to windowed mode.

To return to the desktop overlay, close BG2EE and remove BG2Radar.asi from the game folder. Remove winmm.dll only if you installed it for this module and no other mod uses it.

Combat Text displays weapon hits, misses, critical hits, backstabs and elemental damage over their targets. A critical backstab shows one combined label with the resolved damage. When the game reports an ineffective weapon, a skeleton banner appears over the target without a damage number. The module reports each resolved event separately on supported 2.6.6 and 2.7.3 x64 builds. An older module or a standalone desktop overlay shows the health lost between creature scans as a plain damage number. To upgrade an existing module, close the game and replace its BG2Radar.asi with the copy from the companion's GameFiles folder.

With EEex installed on a supported build, floating damage uses the engine's individual damage events, including the amount after resistance and the damage type. Radar installs M_RDRCRT.lua in the game's override folder alongside the native installation and checks for it again at startup, including when EEex was added later. Restart the game through InfinityLoader to load the script. For a manual installation, copy M_RDRCRT.lua from GameFiles into override. The script uses EEex's base weapon damage listener and works independently of the Twitch bridge. If the script or listener is absent, critical hits show the usual damage-type style. Misses are available when the game's hit-roll entry point is intact.

## Building

Use an x64 Visual Studio C++ toolchain with a Windows SDK, CMake and Git. From the repository root, run the commands below in a developer shell. CMake fetches the pinned MinHook revision.

```powershell
cmake -S NativeRadar -B obj/NativeRadar -A x64
cmake --build obj/NativeRadar --config Release
cmake --install obj/NativeRadar --config Release --prefix bin/NativeRadar
```

For a bundle with the loader, place the official x64 Ultimate ASI Loader in bin/NativeRadar as winmm.dll and include its MIT license there as Loader-LICENSE.txt. The tested release is [Ultimate ASI Loader v10.0.4](https://github.com/ThirteenAG/Ultimate-ASI-Loader/releases/tag/v10.0.4). The release's Ultimate-ASI-Loader_x64.zip contains dinput8.dll. Its SHA-256 after extraction is 0cdd02c8e02e8f79a036dc0ec36adb040c5011a1030a0cf956188f9111d9199d.

Then run build.bat as usual. Publishing picks up the staged module and notices in GameFiles. A standalone module build needs an existing x64 ASI loader in the game.

# BG2RadarOverlay
An overlay program for Baldurs Gate 1-2 EE and Icewind Dale:EE showing nearest enemies, their resistances, buffs and its durations in realtime.

## Demos

[![IMAGE_ALT_TEXT](http://img.youtube.com/vi/dqm3qja_ARE/0.jpg)](https://youtu.be/dqm3qja_ARE "Radar Overlay 2.5 Major Update")

[![IMAGE ALT TEXT](http://img.youtube.com/vi/ceSuvPXkQXA/0.jpg)](https://www.youtube.com/watch?v=ceSuvPXkQXA "Kangaxx fight using Radar by @coredumped")
[![IMAGE ALT TEXT](http://img.youtube.com/vi/APyk8AeeoO4/0.jpg)](https://www.youtube.com/watch?v=APyk8AeeoO4 "Demogorgon fight using Radar by @coredumped")

## Screenshots
<img src=https://user-images.githubusercontent.com/1484801/172044039-3dde5348-54f1-48e2-9754-4920b6180d7f.jpg width=250px /><img src=https://i.imgur.com/FpUZ9s0.webp/ width=250px><img src=https://user-images.githubusercontent.com/1484801/175143688-d036ed2d-6c66-4d01-a436-88a32c8807e6.jpg width=250px/><img src=https://user-images.githubusercontent.com/1484801/177209642-a025a038-3387-4282-95bc-30cfd312b0c5.png width=250px/>

## Requirements
- BG1EE/BG2EE 2.6 or 2.7
- ModMerge / DLCMerge to work properly with SoD
- IWDEE 2.7 now also supported

## Installation

Just extract the archive and start the "BG Radar Overlay.exe".
The folder does not matter anymore, the game would be detected automatically once its launched.

## Configuration
if you are to right-click the Radar at the top left corner it would show a proper **Options window**
There is also a config file "config.cfg". The gamefolder should be detected automatically, if it does not - specify the path manualy.
It is also possible to set the language. By default it is set to en_US, but it can be set to any of the languages you have in your BG2\lang folder.

## Building from source
```dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -o publish_output -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true```

## Star History

<a href="https://www.star-history.com/?repos=tapahob%2FBG2RadarOverlay&type=date&legend=top-left">
 <picture>
   <source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/chart?repos=tapahob/BG2RadarOverlay&type=date&theme=dark&legend=top-left" />
   <source media="(prefers-color-scheme: light)" srcset="https://api.star-history.com/chart?repos=tapahob/BG2RadarOverlay&type=date&legend=top-left" />
   <img alt="Star History Chart" src="https://api.star-history.com/chart?repos=tapahob/BG2RadarOverlay&type=date&legend=top-left" />
 </picture>
</a>

## Possible Windows Defender false positives

Windows Defender might find the archive to be infected with a virus - which is a false positive.
You can check that with [VirusTotal](https://www.virustotal.com/gui/url/aed7400b278f71e1b5b3879740113ec3ec463e224556af12ffc0f4b80a12a8c4?nocache=1)
![image](https://user-images.githubusercontent.com/1484801/177207022-37b40db9-6dba-4c8f-bab8-be6efc3c294b.png)

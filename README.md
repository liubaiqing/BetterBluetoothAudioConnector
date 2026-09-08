# Bluetooth Audio Receiver (Reveicer)

A UWP app that turns a Windows PC into a Bluetooth audio receiver. Connect your phone to the PC over Bluetooth and hear the phone's audio through the PC's default output device (headphones/speakers) alongside PC audio.

## Features

- Enumerates nearby Bluetooth devices that support `Windows.Media.Audio.AudioPlaybackConnection`.
- Opens / closes an audio playback connection from the selected device.
- Shows connection state: `Idle`, `Connecting...`, `Connected`, `Disconnected`.

## Project layout

```
BluetoothAudioReveicer.sln
NuGet.Config                       # offline fallback to local UWP SDK packages
BluetoothAudioReveicer/
  BluetoothAudioReveicer.csproj    # classic UWP C# project (.NET Native 2.2)
  Package.appxmanifest             # Bluetooth capability, Windows 10 19041
  App.xaml / App.xaml.cs
  MainPage.xaml / MainPage.xaml.cs # device watcher + AudioPlaybackConnection logic
  Properties/
    AssemblyInfo.cs
    Default.rd.xml
  Assets/                          # app logos/splash
```

## Requirements

- Windows 10 SDK 10.0.19041.0 or newer.
- Visual Studio 2019/2022 with the **Universal Windows Platform development** workload, or MSBuild with the same components.
- NuGet restore works offline through the local fallback folder configured in `NuGet.Config`.

## Build

From the repo root:

```bat
"E:\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" BluetoothAudioReveicer.sln /t:Restore,Build /p:Configuration=Debug /p:Platform=x64
```

Or open `BluetoothAudioReveicer.sln` in Visual Studio and build.

## Notes

- The original installed package used the misspelled name `Bluetooth Audio Reveicer`; this source tree intentionally keeps that spelling for compatibility.
- The `.pfx` in the project is a locally generated development signing certificate (`BluetoothAudioReveicer_TemporaryKey.pfx`, password `BluetoothAudioReveicer123!`). Replace it before publishing.

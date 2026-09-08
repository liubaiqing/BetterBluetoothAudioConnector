# Better Bluetooth Audio Connector

[简体中文](README.md) | English

<p align="center">
  <img src="BetterBluetoothAudioConnector/Assets/BetterBluetoothAudioConnector-Source.png" width="128" alt="Better Bluetooth Audio Connector icon">
</p>

An unpackaged, self-contained WinUI 3 desktop app that turns a Windows PC into a Bluetooth audio receiver. Connect your phone to the PC over Bluetooth and hear the phone's audio through the PC's default output device alongside PC audio.

## Features

- Keeps paired audio-capable devices in the list and marks them as checking, nearby, offline, connected, or unknown.
- Opens and closes an audio playback connection with cancellable 5-second Start and 10-second Open stages.
- Retries transient failures once with a fresh connection object.
- Cancels the underlying WinRT operation and isolates late completion results.
- Releases stale connections when devices go offline and restarts device watchers after Bluetooth interruptions.
- Keeps the receiver active while its WinUI 3 desktop window is minimized.
- Supports system media controls and an in-app reconnect action for stalled audio streams.
- Opens with a compact, Per-Monitor V2 DPI-aware interface.
- Writes privacy-preserving diagnostic logs to `%LocalAppData%\BetterBluetoothAudioConnector\Logs` and retains them for seven days.

## Requirements

- Windows 10 version 2004 (build 19041) or newer, x64.
- The source device must first be paired in Windows Bluetooth settings.
- Target PCs do not need MSIX, .NET, or a separately installed Windows App SDK.
- .NET 8 SDK and Visual Studio 2022 with Windows application development tools are required only for development.

## Build and test

```powershell
dotnet restore .\BetterBluetoothAudioConnector.sln
dotnet build .\BetterBluetoothAudioConnector.sln --configuration Debug --no-restore -p:Platform=x64
dotnet test .\BetterBluetoothAudioConnector.Tests\BetterBluetoothAudioConnector.Tests.csproj --configuration Debug --no-restore -p:Platform=x64
```

## Publish the portable build

```powershell
dotnet publish .\BetterBluetoothAudioConnector\BetterBluetoothAudioConnector.csproj --configuration Release --no-restore -p:Platform=x64
```

The output folder is:

```text
BetterBluetoothAudioConnector\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish
```

Copy or archive the entire folder and run `Better Bluetooth Audio Connector.exe`. The runtime DLL and PRI files beside the executable must remain in place.

## Notes

- The current build is unpackaged and self-contained; it does not install or register MSIX.
- `Package.appxmanifest` and the development certificate remain only as migration history.

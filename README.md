# Better Bluetooth Audio Connector

An unpackaged, self-contained WinUI 3 desktop app that turns a Windows PC into a Bluetooth audio receiver. Connect your phone to the PC over Bluetooth and hear the phone's audio through the PC's default output device (headphones/speakers) alongside PC audio.

## Features

- Keeps paired audio-capable devices in the list and marks them as checking,
  nearby, offline, connected, or unknown.
- Opens / closes an audio playback connection from the selected device, with
  cancellable 5-second Start and 10-second Open stages.
- Retries transient connection failures once with a fresh connection object.
- Cancels the underlying WinRT operation and isolates late completion results,
  so a timed-out request cannot revive an obsolete connection.
- Releases stale connections when devices go offline or the desktop window closes,
  and automatically restarts device watchers after Bluetooth radio interruptions.
- Keeps the receiver alive while the window is minimized; WinUI 3 desktop apps
  are not automatically suspended by the UWP process-lifecycle manager.
- Exposes play/pause state through desktop-compatible system media controls.
- Provides an in-app reconnect action for recovering a stalled audio stream
  without resetting the Windows Bluetooth radio.
- Opens with a compact 360 x 320 DIP client area and scales correctly for the
  active monitor DPI; the minimum window size is 340 x 280 DIP.
- Shows connection and watcher state, changes Disconnect to Cancel while connecting,
  and provides a shortcut to the diagnostic log folder.
- Writes privacy-preserving diagnostic logs to
  `%LocalAppData%\BetterBluetoothAudioConnector\Logs`, retaining seven days.

## Project layout

```
BetterBluetoothAudioConnector.sln
NuGet.Config                       # nuget.org source for Windows App SDK restore
BetterBluetoothAudioConnector/
  BetterBluetoothAudioConnector.csproj # SDK-style .NET 8 + WinUI 3 project
  app.manifest                     # Win32 compatibility declaration
  App.xaml / App.xaml.cs
  MainPage.xaml / MainPage.xaml.cs # WinUI window and event forwarding only
  Models/                          # immutable device/connection snapshots
  ViewModels/                      # UI state and commands
  Services/                        # watchers, connection state machine, cancellation
  Diagnostics/                     # asynchronous rotating diagnostic logs
  Properties/
    AssemblyInfo.cs
  Assets/                          # app logos/splash
```

## Requirements

- Windows 10 version 2004 (build 19041) or newer, x64.
- .NET 8 SDK and Visual Studio 2022 with Windows application development tools
  are required only for development.
- Internet access for the first Windows App SDK package restore.
- Target PCs do not need MSIX, .NET, or a separately installed Windows App SDK.

## Build

From the repo root:

```powershell
dotnet restore .\BetterBluetoothAudioConnector.sln
dotnet build .\BetterBluetoothAudioConnector.sln --configuration Debug --no-restore
dotnet test .\BetterBluetoothAudioConnector.Tests\BetterBluetoothAudioConnector.Tests.csproj --configuration Debug --no-restore -p:Platform=x64
```

To publish the directly runnable folder:

```powershell
dotnet publish .\BetterBluetoothAudioConnector\BetterBluetoothAudioConnector.csproj --configuration Release --no-restore -p:Platform=x64
```

The output folder is:

```text
BetterBluetoothAudioConnector\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish
```

Copy the entire folder to the target PC and run `Better Bluetooth Audio Connector.exe`.
The DLL and runtime files beside the executable are required and must remain in
the same folder. No installation or package registration is performed.

## Notes

- `Package.appxmanifest` and the development certificate remain only as migration
  history; the current build is unpackaged and does not sign or install MSIX.

# Better Bluetooth Audio Connector

简体中文 | [English](README_EN.md)

<p align="center">
  <img src="BetterBluetoothAudioConnector/Assets/BetterBluetoothAudioConnector-Source.png" width="128" alt="Better Bluetooth Audio Connector 图标">
</p>

Better Bluetooth Audio Connector 是一款免安装、自包含的 WinUI 3 桌面应用。它可以让 Windows 电脑作为蓝牙音频接收端，将手机等已配对设备的声音传输到电脑，并通过电脑当前的耳机或扬声器播放。

<p align="center">
  <a href="https://github.com/liubaiqing/BetterBluetoothAudioConnector/releases/latest"><strong>下载最新安装包</strong></a>
</p>

## 功能特性

- 保留所有已配对且支持音频传输的设备，并显示“检查中、附近、离线、已连接或状态未知”。
- 维持单设备连接，支持连接、断开、取消连接和重新连接。
- Start 和 Open 阶段分别具有 5 秒和 10 秒超时，并支持真正取消底层 WinRT 操作。
- 瞬时连接失败时自动重试一次，而且每次尝试都会创建全新的连接对象。
- 隔离超时或取消后的晚到结果，避免已经失效的请求重新恢复为已连接状态。
- 设备持续离线两秒后释放陈旧连接；设备重新上线时只更新状态，不会自动连接。
- 蓝牙 watcher 异常停止后自动退避重启，关闭再开启电脑蓝牙时无需重启程序。
- 使用 WinUI 3 桌面生命周期，窗口最小化后不会受到 UWP 自动挂起限制。
- 支持系统媒体播放/暂停控制，以及应用内手动恢复音频流。
- 提供紧凑界面、Per-Monitor V2 DPI 缩放和清晰的高分辨率字体。
- 提供隐私保护的诊断日志，日志保留 7 天，可通过界面的 `Open Logs` 按钮打开。

日志目录：

```text
%LocalAppData%\BetterBluetoothAudioConnector\Logs
```

## 下载与安装

1. 打开 [Releases](https://github.com/liubaiqing/BetterBluetoothAudioConnector/releases/latest)。
2. 下载 `BetterBluetoothAudioConnector-Setup-1.0.1-x64.exe`。
3. 运行安装器，选择语言和安装位置，然后点击 `Install`。
4. 安装完成后可从开始菜单或可选的桌面快捷方式启动程序。

安装器按当前用户安装，默认路径为 `%LocalAppData%\Programs\Better Bluetooth Audio Connector`，无需管理员权限。程序可以从 Windows“已安装的应用”或开始菜单中的卸载入口移除。

当前发布包尚未进行代码签名，Windows 可能显示“未知发布者”或 SmartScreen 提示。请只从本项目的 GitHub Releases 页面下载，并可使用 Release 附带的 SHA-256 文件核对完整性。

## 使用要求

- Windows 10 版本 2004（内部版本 19041）或更高版本。
- 64 位 x64 系统。
- 手机等音频源设备需要先在 Windows 蓝牙设置中完成配对。
- 发布版本已经包含 .NET 8 和 Windows App SDK 运行库，目标电脑不需要安装 MSIX 或额外运行环境。

## 使用方法

1. 在 Windows 蓝牙设置中配对手机或其他音频设备。
2. 启动 `Better Bluetooth Audio Connector.exe`。
3. 在设备列表中选择状态为 `Nearby` 或 `Unknown` 的设备。
4. 点击 `Connect`，然后从手机播放音频。
5. 如音频流长时间使用后停止，可点击 `Reconnect` 重新建立连接。

如果使用免安装版本，必须保留整个 `publish` 文件夹，不能只复制 EXE 文件。

## 当前限制

- 仅支持同时连接一个音频源设备，不提供多设备混音。
- 不提供应用内配对，请先在 Windows 蓝牙设置中完成设备配对。
- 设备重新进入范围时只更新状态，不会自动连接。
- 当前仅发布 Windows x64 版本。

## 项目结构

```text
BetterBluetoothAudioConnector.sln
NuGet.Config
BetterBluetoothAudioConnector/
  BetterBluetoothAudioConnector.csproj
  App.xaml / App.xaml.cs           # 应用创建和生命周期
  MainPage.xaml / MainPage.xaml.cs # WinUI 窗口及事件转发
  Models/                          # 设备和连接状态快照
  ViewModels/                      # 界面状态与命令
  Services/                        # watcher、连接状态机和取消逻辑
  Diagnostics/                     # 异步轮转诊断日志
  Assets/                          # 应用图标和视觉资源
BetterBluetoothAudioConnector.Tests/
```

## 构建与测试

需要 .NET 8 SDK，以及包含 Windows 应用开发工具的 Visual Studio 2022。

```powershell
dotnet restore .\BetterBluetoothAudioConnector.sln
dotnet build .\BetterBluetoothAudioConnector.sln --configuration Debug --no-restore -p:Platform=x64
dotnet test .\BetterBluetoothAudioConnector.Tests\BetterBluetoothAudioConnector.Tests.csproj --configuration Debug --no-restore -p:Platform=x64
```

## 发布免安装版本

```powershell
dotnet publish .\BetterBluetoothAudioConnector\BetterBluetoothAudioConnector.csproj --configuration Release --no-restore -p:Platform=x64
```

输出目录：

```text
BetterBluetoothAudioConnector\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish
```

将整个目录打包给用户，解压后运行 `Better Bluetooth Audio Connector.exe` 即可。

## 构建安装器

安装 [Inno Setup 6](https://jrsoftware.org/isdl.php) 后运行：

```powershell
.\installer\Build-Installer.ps1
```

脚本会先生成 Release x64 自包含版本，再将整个 `publish` 目录打包为单一安装程序：

```text
artifacts\installer\BetterBluetoothAudioConnector-Setup-1.0.1-x64.exe
```

安装器支持中英文界面、安装位置选择、开始菜单快捷方式、可选桌面快捷方式和标准卸载。默认按当前用户安装到 `%LocalAppData%\Programs\Better Bluetooth Audio Connector`，不需要管理员权限。

发布其他版本号时可以指定：

```powershell
.\installer\Build-Installer.ps1 -Version 1.1.0
```

## 说明

- 当前版本采用 unpackaged、自包含发布方式，不安装或注册 MSIX。
- `Package.appxmanifest` 和开发证书仅作为迁移历史保留，不参与当前发布流程。

## 开源许可

本项目采用 [MIT License](LICENSE) 开源。你可以使用、复制、修改和分发本项目，也可以用于商业用途，但必须保留原始版权和许可声明。软件按“原样”提供，不附带任何明示或暗示的担保。

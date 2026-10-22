# 轻录屏

一个简单的 Windows 桌面录屏工具，用悬浮球进行录屏和截屏，支持录制电脑声音和麦克风。

## 功能

- 拖动桌面悬浮球，点击打开操作面板。
- 录制整块显示器，保存为 MP4。
- 截屏并保存为 PNG。
- 声音可选电脑声音、麦克风、两者一起或静音。
- `Ctrl+Alt+R` 开始或停止录屏，`Ctrl+Alt+S` 截屏。
- 视频和截图保存在程序旁的 `录屏与截图` 文件夹。

## 编译与运行

适用于 Windows 10/11，需要 .NET Framework 4.5 或更新版本。使用 C#、Windows Forms、WASAPI 和 FFmpeg。

1. 下载并解压本项目源码。
2. 双击 `编译.bat`，生成 `轻录屏.exe`。
3. 从 [FFmpeg 官网](https://ffmpeg.org/download.html) 指向的 Windows 构建下载 FFmpeg，将 `ffmpeg.exe` 放到程序同目录。需包含 `gdigrab` 和 `libx264`。
4. 双击 `轻录屏.exe`，点击桌面悬浮球开始操作。

也可在 PowerShell 中执行 `./build.ps1` 编译。

## 使用提示

默认录制电脑播放的声音。录麦克风时，请允许 Windows 桌面应用访问麦克风。声音设备不可用时会提示，请检查设备或切换声音模式。

录制范围是开始时悬浮球所在的显示器。停止后会合并声音和视频，请等待保存完成。录制期间请勿切换默认声音设备。

目前不支持区域选择、暂停和开机启动。临时音轨使用 WAV，长时间录制请分段。

## 文件说明

- `FloatRecorder.cs`：悬浮球、截图、录屏和音视频合并。
- `AudioCapture.cs`：Windows WASAPI 音频捕获。
- `build.ps1` / `编译.bat`：编译脚本。
- `使用说明.txt`：详细使用说明。
- `THIRD_PARTY.txt` / `ffmpeg-license.txt`：第三方组件说明。

## 上传 GitHub

将源码压缩包解压后，把本目录里的文件上传到 GitHub 仓库。源码包不包含录屏、截图、测试文件、可执行程序或 FFmpeg 二进制文件。

FFmpeg 为独立的第三方组件，需要自行下载，其许可取决于具体构建；附带的 `ffmpeg-license.txt` 记录了开发时本机所用构建的许可信息。

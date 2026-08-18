# MultiScrcpyPanel（C# 重制版）

多设备 Android 投屏控制面板。复用 **官方 `scrcpy-server.jar v4.0`**，与 Python 版**逐字节协议一致**。

- 规格来源：[`docs/ARCHITECTURE_CSHARP.md`](../docs/ARCHITECTURE_CSHARP.md)（v1.0 定稿）、[`docs/PRD.md`](../docs/PRD.md)
- 目标框架：`net8.0-windows`（x64 only），WinForms
- 解码：FFmpeg 6.x（`FFmpeg.AutoGen 6.0.0.2`）

---

## 1. 环境要求

| 依赖 | 版本 | 说明 |
|---|---|---|
| .NET SDK | 8.0+ | `dotnet --version` 验证 |
| Windows | 10 1809+ / 11 | x64 |
| Android Platform-Tools | 任意近期版本 | 提供 `adb.exe`，需在 `PATH` 中或在配置里显式指定 |
| FFmpeg | **6.x shared** win64 | 由 `tools\fetch_ffmpeg.ps1` 拉取 |
| scrcpy-server | **v4.0** | 由 `tools\fetch_scrcpy_server.ps1` 拉取 |

> ⚠️ **版本配对铁律**：`FFmpeg.AutoGen 6.0.0.2` ↔ FFmpeg **6.x**
> （`avutil-58` / `avcodec-60` / `swscale-7` / `swresample-4`）。
> 换成 7.x 会导致运行时静默崩溃或函数签名错位，**不要擅自升级 NuGet 版本**。

---

## 2. 首次构建

```powershell
cd csharp

# 1) 拉取 FFmpeg 6.x 原生库 → native\ffmpeg\x64\
pwsh tools\fetch_ffmpeg.ps1

# 2) 拉取官方 scrcpy-server v4.0 → assets\scrcpy-server-v4.0.jar
pwsh tools\fetch_scrcpy_server.ps1

# 3) 还原 + 构建
dotnet restore MultiScrcpyPanel.sln
dotnet build   MultiScrcpyPanel.sln -c Debug

# 4) 单元测试（无头，必须全绿）
dotnet test MultiScrcpy.Tests\MultiScrcpy.Tests.csproj -c Debug

# 5) 运行
dotnet run --project MultiScrcpyPanel\MultiScrcpyPanel.csproj -c Debug
```

### 发布（自包含单目录）

```powershell
dotnet publish MultiScrcpyPanel\MultiScrcpyPanel.csproj `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false
```

产物目录中必须同时存在：

```
MultiScrcpyPanel.exe
ffmpeg\x64\avutil-58.dll, avcodec-60.dll, swscale-7.dll, swresample-4.dll
assets\scrcpy-server-v4.0.jar
config\settings.json          （首次运行自动生成）
```

---

## 3. 目录结构

```
csharp/
├── MultiScrcpyPanel.sln
├── Directory.Build.props              统一编译约定（C# 12 / Nullable / x64）
├── MultiScrcpyPanel/
│   ├── MultiScrcpyPanel.csproj
│   ├── Program.cs                     入口：DPI → 日志 → 配置 → FFmpeg → Application.Run
│   ├── AppConfig.cs                   config/settings.json 读写与归一化
│   ├── Protocol/                      【纯 BCL，零 WinForms / 零 FFmpeg / 零 Socket】
│   │   ├── ScrcpyConstants.cs         协议常量 + 定点数 + scid
│   │   ├── ControlMessages.cs         PC→设备 控制消息（14 / 32 / 21 / 5+N 字节）
│   │   ├── StreamPackets.cs           设备→PC 12 字节包头解析
│   │   └── CoordinateMapper.cs        letterbox 坐标换算
│   ├── Core/                          【禁止 WinForms】
│   │   ├── Errors.cs / Log.cs / Models.cs
│   │   ├── VideoStreamReader.cs       精确收流 + 握手
│   │   ├── FrameBuffer.cs             三缓冲位图交换（latest-frame-wins）
│   │   ├── DeviceController.cs        控制通道写线程 + 有界队列
│   │   ├── DeviceSession.cs           单设备会话编排（后台线程）
│   │   ├── DeviceManager.cs           设备发现 / 挂载 / 卸载 / 全局收尾
│   │   ├── Adb/                       AdbClient / PortAllocator / ScrcpyServerLauncher
│   │   └── Decoder/                   FFmpegBinariesHelper / IVideoDecoder / H264Decoder / FrameConverter
│   └── UI/                            【禁止 Socket / Process / FFmpeg】
│       ├── UiTheme.cs / ScreenView.cs / DeviceCard.cs / ToastForm.cs / MainForm.cs
├── MultiScrcpy.Tests/                 xUnit（无头，不加载 FFmpeg 原生库）
│   ├── ControlMessageTests.cs         ← T01 字节级门禁
│   ├── StreamPacketTests.cs           ← T01 字节级门禁
│   ├── CoordinateMapperTests.cs       ← T01 字节级门禁
│   ├── AdbParsingTests.cs
│   └── ModelTests.cs
├── tools/
│   ├── fetch_ffmpeg.ps1
│   └── fetch_scrcpy_server.ps1
├── native/ffmpeg/x64/                 （脚本产物，已 gitignore）
└── assets/                            （脚本产物，已 gitignore）
```

### 分层铁律

| 层 | 允许依赖 | 明确禁止 |
|---|---|---|
| `MultiScrcpy.Protocol` | `System`、`System.Buffers.Binary`、`System.Text`、`System.Drawing.Primitives` | WinForms、FFmpeg、Socket、本项目其他命名空间 |
| `MultiScrcpy.Core` | BCL、`System.Drawing`、FFmpeg.AutoGen、`Protocol` | **WinForms** |
| `MultiScrcpy.UI` | WinForms、`System.Drawing`、`Core`、`Protocol` | **Socket / Process / FFmpeg**（一律经 `DeviceManager` / `DeviceController`） |

---

## 4. 配置：`config/settings.json`

首次运行自动生成，字段说明见 `AppConfig.cs`。常用项：

| 字段 | 默认 | 说明 |
|---|---|---|
| `adbPath` | `""` | 空则从 `PATH` 查找 `adb.exe` |
| `serverJarPath` | `""` | 空则用 `assets\scrcpy-server-v{serverVersion}.jar` |
| `serverVersion` | `"4.0"` | **必须与 jar 版本严格一致**（作为 `app_process` 首个位置参数） |
| `portBase` | `27183` | 本地 forward 端口起始值，按设备递增 |
| `videoCodec` | `"h264"` | `h264` / `h265` |
| `maxSize` | `1024` | server 侧最大边长 |
| `videoBitRate` | `4000000` | 码率（bps） |
| `maxFps` | `30` | 最大帧率 |
| `maxDevices` | `8` | 同时投屏上限（PRD Q1）；超出时 Toast + 状态栏双通道提示 |
| `scanIntervalMs` | `2000` | 设备扫描间隔 |
| `statusIntervalMs` | `30000` | 电量轮询间隔 |
| `screenshotDir` | `%USERPROFILE%\Pictures\MultiScrcpy` | 截图目录，PNG，**设备原始分辨率** |
| `ffmpegPath` | `""` | 空则用输出目录下 `ffmpeg\x64` |
| `swsFlags` | `2` | `2 = SWS_BILINEAR`，`1 = SWS_FAST_BILINEAR`（更快、更糊） |

---

## 5. 使用

1. 手机开启 **开发者选项 → USB 调试**，USB 连接后在手机上勾选「始终允许」并点「允许」。
2. 启动程序，设备卡片自动出现并开始投屏。
3. 卡片操作：
   - 画面区：**鼠标左键**单指触控（按下 / 拖拽 / 抬起），**滚轮**滚动。
   - 按钮区：主页 / 返回 / 多任务 / 电源 / 音量− / 音量+ / 截图 / 重试。
4. 未授权设备显示**橙色标题 + 引导文案**，按键区禁用，仅保留「重试」（3 秒防连点）。
5. 工具栏可切换卡片缩放（50% / 75% / 100% / 150% / 200%）与截图目录。
6. 关闭窗口会自动停止所有会话、杀掉 `app_process`、清空 `adb forward`。

---

## 6. 常见问题

**Q：启动即弹「FFmpeg 初始化失败」**
A：跑 `pwsh tools\fetch_ffmpeg.ps1`，确认 `native\ffmpeg\x64\` 下有 4 个必备 DLL，然后重新 build
（DLL 会复制到输出目录 `ffmpeg\x64\`）。注意必须是 **shared** 构建，不是 static/essentials。

**Q：`未找到 scrcpy-server jar`**
A：跑 `pwsh tools\fetch_scrcpy_server.ps1`，或手动把 `scrcpy-server-v4.0.jar` 放到 `assets\`。

**Q：`未找到 adb`**
A：安装 Android Platform-Tools 并加入 `PATH`，或在 `config/settings.json` 的 `adbPath` 里写全路径。

**Q：设备一直「待授权」**
A：拔插数据线，在手机弹窗勾选「始终允许」→「允许」，再点卡片上的「重试」。
仍不行执行 `adb kill-server && adb start-server`。

**Q：画面颜色偏蓝/偏红**
A：说明像素格式接反了。本项目 `FrameConverter` 固定输出 `AV_PIX_FMT_BGR24`
对应 GDI+ 的 `PixelFormat.Format24bppRgb`（GDI 的 "RGB" 内存序其实是 BGR），不要改成 `AV_PIX_FMT_RGB24`。

**Q：多设备时 CPU 占用高**
A：调低 `maxFps`（如 15）、`maxSize`（如 720）、`videoBitRate`，或把 `swsFlags` 设为 `1`。

---

## 7. 开发约定

- 所有整数编解码一律 **大端序**，统一用 `System.Buffers.Binary.BinaryPrimitives`；**禁止 `BitConverter`**。
- 跨线程更新 UI 一律 `Control.BeginInvoke`（项目内封装为 `UiTheme.SafePost`）；**禁止 `Invoke`**（会死锁）。
- 全项目**禁止 `MessageBox`**，唯一例外：`Program.Main` 中 FFmpeg 原生库加载失败的启动期致命错误。
- 修改 `Protocol/` 下任何文件后，**必须先让 `dotnet test` 全绿再提交**——
  字节格式错一位，后面全部白干。

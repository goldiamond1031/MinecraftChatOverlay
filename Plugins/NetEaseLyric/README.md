# 网易云歌词插件

把正在播放的网易云歌词显示成一个**自己开的置顶小窗**（不走宿主的悬浮窗）。外观照 `C:\netease_lyric_shadow\CSharpWpf` 那套做的：主文字后面叠一层同款"影子文字"，靠影子和描边把字从游戏画面里抠出来，而不是加个背景框。

## 数据从哪来

```
网易云（设置里要打开「系统媒体控制 / SMTC」）
  └─ SMTC：曲名 / 歌手 / 播放位置 / 播放状态        SmtcReader.cs
       └─ 用「曲名 歌手」搜歌 → 歌曲 id             NeteaseLyricClient.cs
            └─ 用 id 取 LRC（含官方翻译）           NeteaseLyricClient.cs
                 └─ 按播放位置定位当前句            LrcParser.cs / LyricEngine.cs
                      └─ 画到歌词窗                  LyricWindow.xaml(.cs)
```

**前置条件**：网易云音乐 → 设置 → 里把「系统媒体控制」打开。这是 Windows 官方的"当前在放什么"通道，不开的话插件读不到任何东西（页面上的【测试读取】会直接告诉你）。

## 两个工程上的坑（重要）

### 1. 这个插件必须带附属 dll，而且 `plugin.json` 必须写 `assembly`

读 SMTC 用的是 WinRT 投影（`Windows.Media.Control`）。C# 要用它就得把 TFM 提到 `net8.0-windows10.0.19041.0`，然后运行时需要两份 dll 跟着走：

- `Microsoft.Windows.SDK.NET.dll`（24.8 MB，整个 Windows SDK 的投影，没法裁剪）
- `WinRT.Runtime.dll`（528 KB）

所以：

- `NetEaseLyric.csproj` 里开了 `CopyLocalLockFileAssemblies`，否则库工程默认不复制这两份；
- `plugin.json` 里**必须**写 `"assembly": "NetEaseLyricPlugin.dll"` —— 宿主挑主 dll 时，没有这个字段就按文件名排序取第一个，会误选到 `Microsoft.Windows.SDK.NET.dll`；
- `tools\pack-market-package.ps1` 会把输出目录里除主 dll、契约 dll 之外的 dll 一并装进包。打包出来约 6.35 MB。

宿主的 `PluginLoadContext` 会优先从插件自己的目录解析依赖，所以这几份 dll 放在插件目录里就能load 起来。

### 2. 为什么不走 PowerShell 读 SMTC

备选方案是内嵌一个 PS 脚本、起常驻子进程去读 SMTC（插件包只有几十 KB）。没选它是因为要常驻一个额外进程（约 40~80 MB 内存、每秒轮询），对"就看个歌词"这件事太重。当前方案一个进程都不用多，代价只是插件包大 6 MB。

## 文件

| 文件 | 干什么 |
|---|---|
| `NetEaseLyricPlugin.cs` | 插件入口：插件页（设置项/按钮）、加卸载 |
| `NetEaseLyricSettings.cs` | 全部可调项的默认值，存插件目录 settings.json |
| `SmtcReader.cs` | 读系统媒体会话（曲名/歌手/位置/状态），位置按时间外推 |
| `NeteaseLyricClient.cs` | 搜歌 + 取歌词 + 磁盘缓存（cache\ 下，7 天过期） |
| `LrcParser.cs` | 解析 LRC、合并官方翻译、过滤"作词 : xxx"这类制作信息行 |
| `LyricEngine.cs` | 快节拍(120ms)算当前句 + 慢节拍(1s)查换歌、取歌词 |
| `LyricWindow.xaml(.cs)` | 歌词窗：影子文字、淡入动效、拖动、锁定后鼠标穿透 |

## 调节项（插件页里）

显示行数（单行/双行带下一句）、官方翻译、歌名歌手、字号、粗体、文字色、影子色/不透明度/模糊/偏移、底色不透明度、锁定穿透、暂停时隐藏、歌词提前量、跟随哪个播放器。
按钮：重新载入歌词、重置窗口位置、清空歌词缓存、测试读取。

## 改完怎么调

```bat
tools\publish-plugin.bat NetEaseLyric   rem 编译 + 打包 + 重建市场清单
```

想直接在本机试（不等市场）：把 `market\packages\goldiamond.neteaselyric-<版本>.zip` 解到
`%AppData%\MinecraftChatOverlay\plugins\goldiamond.neteaselyric\`，然后重启宿主。

## 已知限制

- 歌词只在网易云有 LRC 的情况下才有；纯音乐/还没收录的歌会显示"这首歌没有歌词"。
- 桌面歌词窗是普通置顶窗：游戏用**无边框全屏**（配合「窗口全屏」插件）能盖住；独占全屏盖不住，这是 Windows 的限制。
- SMTC 给的播放位置取决于网易云上报的频率，切歌/拖动进度时最准；长时间不刷新时靠"上次刷新时间 + 播放速率"外推，个别歌可能有一点点漂移，用插件页的「歌词提前量」微调。

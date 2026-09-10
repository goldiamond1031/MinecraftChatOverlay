# Minecraft 聊天栏悬浮窗（C# WPF）

读取 Minecraft 客户端日志，过滤出真正的聊天栏消息，并在透明置顶悬浮窗中实时显示。

## 功能

- 自动监听 `logs/latest.log` 的新增内容，只提取 `[CHAT]` 开头的聊天栏消息
- 忽略 `Ignoring chat session`、网络包日志等非聊天栏输出
- 透明、置顶、无边框悬浮窗，可拖动，可开启鼠标点击穿透
- WPF 配置界面：
  - 日志文件路径
  - 日志编码（Auto / UTF-8 / GBK，兼容部分网易/国服客户端）
  - 多少字符换行
  - 悬浮窗最大宽度、不透明度
  - 字体、字重、字号
  - 文字颜色、背景颜色、文字阴影
  - 最多保留消息数、是否显示时间
  - 用户修改后自动保存到 `%AppData%\MinecraftChatOverlay\settings.json`

## 运行环境

- Windows 10/11
- .NET 8 SDK 或 .NET Desktop Runtime 8

## 编译与运行

```bash
dotnet build MinecraftChatOverlay.csproj
dotnet run --project MinecraftChatOverlay.csproj
```

也可以在 Visual Studio 2022 中打开 `MinecraftChatOverlay.csproj` 后按 F5。

## 使用方法

1. 打开程序后，选择 Minecraft 日志文件。常见路径：
   - 原版 / 官方启动器：`%AppData%\.minecraft\logs\latest.log`
   - 网易我的世界 / MCL：根据启动器实际安装目录，一般为 `...\.minecraft\logs\latest.log`
2. 点击“开始监听”，程序会自动打开悬浮窗。
3. 如果游戏里聊天栏出现新消息，悬浮窗会同步显示。
4. 若中文乱码，可以在监听过程中直接切换“日志编码”，对后续新日志立即生效。
5. 主界面下方“后端输出日志”会显示解码后的 `[CHAT]` 原始行和对应 HEX；需要排查时点“复制日志”发回即可。

## 过滤示例

日志原始行：

```
[069月2026 09:32:12.646] [Render thread/INFO] [net.minecraft.client.gui.components.ChatComponent/]: [CHAT] 床被摧毁 > [关注妄想天使喵]JustReach 摧毁了 青队 的圣床，引发灾难。
```

悬浮窗显示：

```
床被摧毁 > [关注妄想天使喵]JustReach 摧毁了 青队 的圣床，引发灾难。
```

不是聊天栏消息的行（例如 `Ignoring chat session ...`）会被自动忽略。

## 目录结构

```
MinecraftChatOverlay.csproj
App.xaml / App.xaml.cs
MainWindow.Modern.xaml / MainWindow.xaml.cs # 新版配置界面（实际编译）
ModernControls.xaml                        # 新版界面样式与控件模板
MainWindow.xaml                            # 旧版界面备份（不参与编译）
OverlayWindow.xaml / OverlayWindow.xaml.cs # 置顶透明悬浮窗
Models/
  AppSettings.cs
Services/
  ChatLineParser.cs
  LogTextDecoder.cs
  MinecraftLogWatcher.cs
  SettingsService.cs
ViewModels/
  ChatMessageViewModel.cs
app.manifest
```

## 隐私

项目不包含任何服务器密钥或个人配置；设置只保存在当前 Windows 用户的 `%AppData%\MinecraftChatOverlay\settings.json`。

# DEV-NOTES —— 开发交接笔记

> 给下一个会话（和未来的我）。**每完成一段就更新这个文件，别让它过时。**
> 这里只放「现在做到哪、接下来做什么、怎么编译、什么不能做」；功能文档见第 7 节。
> **历史来龙去脉、已经被推翻的结论都在 `DEV-NOTES-ARCHIVE.md`**（那份顶部写着「不保证仍有效」，别照抄）。

---

## 0. 项目与红线

`C:\MinecraftChatOverlayDSUI3` —— C# WPF / .NET 8（主程序 `MinecraftChatOverlay`）+ MinGW C++（`native/`）。
原本是「读 Minecraft 日志 → 透明悬浮窗显示聊天」，现在是一个壳 + 多条独立数据管线
（日志 / AI 识图 / B站直播 / 游戏帧混合 / 消息提示音 / 击杀反馈），文档见两个 README。

**红线（绝对不做，这是 gold_ 定的，一直有效）**：

- 不做 ESP / 自瞄 / 透视 / 读他人实体坐标。
  击杀反馈的信息**全部来自聊天栏**（本来就看得见），性质是"注意力辅助"——这条线要守住。
  如果他提「受伤方向指示」这种**游戏没主动给的信息**，那是外挂，拒绝。
- 不绕过会员付费、不破解服务端校验、不改包分发
- 不做隐藏痕迹 / 反检测对抗（改名、清痕迹、绕检测）

---

## 1. 当前状态与下一步

### 在跑的东西（都已验收）

- **击杀反馈**：`KillFeedbackEffect` 枚举 = `None` + **8 个真效果**（EdgePulse / VignettePulse / Chromatic /
  ZoomPunch / Shake / Flash / Shockwave / Glitch）。设置页可逐个开关 + 调百分比；
  事件走共享内存（`MotionBlurControlBlock` → 游戏里的钩子 DLL），**和帧混合解耦**（帧混合关着也能放）。
  击杀图标是**用户导入的图片文件**（`KillFeedback.BannerIconFiles`），内置图标 v8 起已全删。
- **动态模糊注入 = 手动映射**：`GameProcessInjector.Inject()` 只做 查重 → 查 DLL → `ManualMapper.MapRemote`，
  失败直接抛（没有 LoadLibrary 兜底可退）。老的 `CreateRemoteThread + LoadLibraryW` 那条路**已经整个删掉**，
  实现细节在 `Services\GameMotionBlur\ManualMapper.cs` / `ManualMapper.Remote.cs`。
- **插件平台 + 6 个插件**（见第 5 节）、**插件市场**（无服务器，见第 6 节）、**开屏公告**（判新旧比 `updatedAt`，见第 7 节）。

### 待办 / 注意

- **区域放大**已从内置功能整体迁成插件（`Plugins\RegionMagnifier\`），但**真机还没验收**（当时游戏没开）：
  ① 窗口化 / 无边框全屏下能不能抓到画面（独占全屏必然抓不到）；② 框"物品栏最后一格"时放大镜够不够用。
- `GameProcessInjector` 里有两处**死代码残留**，读代码时别被误导：`PreferManualMapping` 属性
  （全工程零引用，注释还写着"失败会回退到 LoadLibraryW"——**那是假的**）、`GetRemoteLoadLibraryAddress()`（旧路径残留）。
- 击杀日志真实样本：`[CHAT] 纉襫 击败了 掉打你们除了g!`（游戏 ID = `纉襫`）。

---

## 2. 编译与验收

### .NET 侧

`dotnet restore` 在**我这边的自动化 shell 里**是坏的：
`NuGet.targets: error : Value cannot be null. (Parameter 'path1')`（根因 = 进程里 `APPDATA` 缺失）。
**不能拿它当结论** —— gold_ 双击 `build-plugins.bat` 是能正常 restore 的。

```bash
cd C:/MinecraftChatOverlayDSUI3
APPDATA='C:\Users\gold_\AppData\Roaming' dotnet build MinecraftChatOverlay.csproj --no-restore -v m
```

- **改过 XAML 必须带 APPDATA**：WPF 标记编译会生成 `*_wpftmp.csproj`，它自己要走一遍 restore，
  不带就报 `NETSDK1060 加载锁定文件出错` —— 那条信息很坑人，`project.assets.json` 其实是好的，**别去删 obj**。
- 既有 2 个 `CS8600` 警告在 `MotionBlurControlBlock.cs`，**不是新引入的**，别误判。
- 想引 NuGet 包（比如给插件加 `PackageReference`）**必须让 gold_ 试**，别拿我这边的失败当结论；
  实在不想引包，备选是 `HintPath` 直接引用 dll（不触发 restore）。

### 原生侧（MinGW 直接编，比 build.ps1 可控）

```bash
cd C:/MinecraftChatOverlayDSUI3/native
/c/mingw64/bin/g++.exe -shared -O2 -std=c++17 -fno-exceptions -fno-rtti \
  -static-libgcc -static-libstdc++ -Wall -Wno-unknown-pragmas -Wno-cast-function-type \
  -DUNICODE -D_UNICODE -DWIN32_LEAN_AND_MEAN -D_WIN32_WINNT=0x0A00 \
  -DNTDDI_VERSION=0x0A000000 -DWINVER=0x0A00 \
  GameMotionBlur/dllmain.cpp GameMotionBlur/hook.cpp \
  GameMotionBlur/gl_hook.cpp GameMotionBlur/gl_blur.cpp GameMotionBlur/bmp.cpp \
  GameMotionBlur/control.cpp GameMotionBlur/log.cpp \
  -o dist/GameMotionBlurHook.dll \
  -lopengl32 -luser32 -lgdi32 -lole32 -lpsapi -ladvapi32
```

- 2026-09-28 起原生侧**不再需要 JDK**：JNI 探针 `jmc_probe.cpp`（附着到游戏进程里的 JVM 反射读聊天类布局）
  连同调用点、`build.ps1` 里的 `jni.h` 探测段**已彻底移除**（属于废弃的「真实颜色提取」路线）。
- ⚠ `native\build.ps1` 是 **UTF-8 无 BOM** → `powershell -File` 会按 GBK 读它，中文注释变乱码、直接语法报错。
  要么用 `pwsh` 跑，要么把它另存成**带 BOM**。
- ⚠ **原生改完要编"两份"**：`dist\GameMotionBlurHook.dll` 和 **`dist\GameMotionBlurHook_v6.dll`**。
  后者才是界面 / 发布实际用的那份 —— csproj 会把 `_v6` 复制成输出目录里的 `GameMotionBlurHook.dll`
  （`_v6` 不存在才退回用前者）。只编一份的话，`bin\` 里那份还是老 DLL，很容易误判成"改动没生效"。
  `build.ps1` 的写法：`-HookOnly -OutName GameMotionBlurHook_v6.dll`。

### 验收套路（每次改完都走）

1. 编译看 **0 error**。
2. **改过 XAML 必须做启动自检**（XAML 的 `StaticResource` / 事件绑定是运行时解析的）：
   `cd bin/Debug/net8.0-windows && timeout 7 ./MinecraftChatOverlay.exe`；
   **exit=124 = 跑满 7 秒没崩 = 窗口加载正常**（会闪一下配置窗口 + 悬浮窗）。
3. **标签开闭平衡**（大段改 XAML 后必查）：用 python 数 `<Tag` / `<Tag/>` / `</Tag>` 三种出现次数，差额应为 0。
4. **大段编辑后必须读回来确认** —— 有一次把整个方法体删了都没发现。
5. 想跑独立验证（新建工程跑不了）：在 `App.xaml.cs` 的 `OnStartup` 开头加一个**环境变量触发的自检入口**，
   结果写 `%TEMP%` 文件，**跑完必须删干净**（`KillFeedbackSelfTest.cs` 当年就是这么做的，已删，可以照着重写）。
6. 产物被运行中的实例锁住（`MSB3027`）：gold_ **授权可以直接关他的实例**（先说一声），用 PowerShell `Stop-Process`；
   或者 `mv` 挪开 + `cp` 新的进来绕过（被挪走那份删不掉，会留个 `.old-v2` 残留）。
7. 原生验收**不用碰游戏**：`native/dist/TestGL.exe`（测试画面，窗口标题带 PID）+ `gmblur`
   （`list / inject / on / off / status / watch / dump / kill / killall / reset / unhook / reinstall`）。
   `gmblur kill 1.0 1200` 自带结论输出 —— 关键是看 DLL 那边的**累计次数涨没涨**，光发请求不算数。
   ⚠ TestGL 是**当前 shell 的子进程**，用后台 bash 起、或跟着 shell 一起退出，都没法再从另一个调用里注入进去；
   得让它真正独立存活。
   ⚠ `gmblur inject <pid> [dll路径]` 的路径是**注入方（gmblur 自己）**去读的（`ManualMapper.MapRemote` →
   `File.ReadAllBytes`），按 gmblur 自己的 CWD 解析；不传就自动用 `AppContext.BaseDirectory` / 往上找 `native\dist` 的**绝对路径**。
   旧笔记里"目标进程按它自己的 CWD 解析、`LoadLibrary` 返回 0"那段是**手动映射之前**的说法，**已经过期**。

---

## 3. 已知的坑（都踩过，别再踩）

> 完整版（带现象 / 定位过程 / 实测数据）在 `DEV-NOTES-ARCHIVE.md` 的「4. 这个项目已知的坑」。编号沿用那边。

1. **改共享内存字段必须两边一起改**：`native/GameMotionBlur/common.h` + `Services/GameMotionBlur/MotionBlurControlBlock.cs`，并把 `kVersion` / `Version` 各加一。自检能抓 sizeof 和几个偏移，但**抓不到"两边字段顺序不一样、大小恰好相同"**，别只靠自检。
2. **击杀效果要放在 `GlBlurOnSwapLocked` 的早退判断之后**，否则帧混合关着时效果永远不生效；另外只有效果（没开混合）时 `ctl` 可能是 `nullptr`。
3. **着色器加 uniform 要给默认值**（`uUvScale` 默认 `(1,1)`），否则帧混合那条路会被影响。
5. 「游戏动态模糊」页的进程列表 = **所有带窗口的进程**，很容易选错（钩子进过 Windows「设置」）。
6. **`SettingsService.Save` 是全量同步写盘** → 滑块类必须 debounce（照抄击杀反馈页的 `QueueKillFeedbackSave`，400ms）。
8. `MainWindow.xaml` / `GUI.html` / `design/` / `promo/` 不参与编译，是历史资产。
9. **加新导航页要改 4 处**：NavPanel 的 RadioButton、新 ScrollViewer、`MoveIndicatorToSelected()`、`AnimateActivePanel()`（漏后两个 = 点了导航没反应）。
10. `StartListening()` 第一行就是 `SaveSettingsFromUi()` → **启动就会写一次 settings.json**，是原有行为。
11. 数字输入框排版规矩：标签在上 + 输入框占满 + `Padding="12,0,NN,0"` 右侧留白 + 单位文字 `IsHitTestVisible="False"`；**别写 `Width="70"` 的小框**。
12. 卡片形态抄「启用自动 GG」那张：`ToggleSwitchStyle` 的 CheckBox + 15px ExtraBold 标题 + 12px 说明。
13. 新界面逻辑一律另开 `MainWindow.*.xaml.cs` partial，别往主文件堆。
14. `MainWindow.Modern.xaml` 里 8 个面板是**同级的 ScrollViewer**，插卡片前先量边界（我在这上面放错过 3 次位置）。
15. **`Border` 只能有一个子级** —— 往卡片底部补第二行会直接报 `MC3089`。要么把两个 `TextBlock` 塞进一个 `StackPanel`，要么拆成两个 Border。
16. **`WarningBrush` 不存在**，别顺手写；警示色用 `AmberBrush`（#FFB547）。写之前先 grep `ModernControls.xaml` 的 brush key 列表。
17. **加 / 删一个击杀效果要改 6 处**（漏一处就编译不过或运行期错位）：`KillFeedbackEffects.cs`、`MotionBlurControlBlock.cs`、`KillFeedbackSettings.cs`、`MainWindow.KillFeed.xaml.cs`、`MainWindow.Modern.xaml`、原生 `control.h`/`control.cpp`/`gl_blur.cpp`，外加 `tools/GmBlurCli/Program.cs` 的 `killall` 参数表。**现在有 8 个真效果**（枚举里的 `None` 只是空档）。
18. **隐式样式会覆盖你手写的 `Width`/`Padding`/`MinHeight`**（隐式 Button 带 `MinWidth=72`、隐式 TextBox 带 `MinHeight=40`）。紧凑列表里放小控件**必须写独立 `Style`**（不 BasedOn 隐式样式）——已有 `SwatchButtonStyle`（26×26 色块按钮）/ `EffectPercentTextBoxStyle`（30 高数字框）。
19. **窗口 `Show()` 之前 `ActualWidth/Height` 恒为 0** → 位置直接用 `SystemParameters.WorkArea` 算，别读窗口自身尺寸。**自检工具的坐标系必须和被测代码一致**（写死 1280×720 vs 真实 1707×1019 让我多绕两圈）。
20. **`Viewbox` / `VisualBrush` 会自动缩放内容** → 想把内容"原样"贴到另一块画布，必须 `Stretch="None"` + `AlignmentX/Y="Left/Top"`。
21. 击杀图标现在是**用户导入的图片文件**（`KillFeedback.BannerIconFiles`），`KillBannerWindow` 用 `BitmapImage` 读绝对路径。早年那套"物理文件 `KillIcons.xaml` + `XamlReader.Parse` + csproj `CopyToOutputDirectory`"的做法**已经不存在了**，别再照着找。
22. **CS2 官方素材不能打包分发**（Valve 版权，仓库 license 是 null）→ **v8 起内置图标全删，全部由用户导入**。解析这类 SVG 要跳过 `<symbol>` 里的 path，也要跳过 `fill="none"` 的纯描边路径。
23. **禁用态的 `Foreground` 必须自己接管**（WPF 对 `IsEnabled=False` 会套一个跟主题无关的系统浅灰）。要**两条一起改**：(a) 隐式 `ListBoxItem` 样式加 `IsEnabled=False` 触发器 → `TextTertiaryBrush`；(b) 删掉 template 里写死的 `Foreground="{DynamicResource TextPrimaryBrush}"`（写死的会盖过继承）。⚠ **别和第 31 条（背景发白）搞混**，那是另一回事。
24. **纯 `ResourceDictionary` 里不能挂事件处理器**（`ModernControls.xaml` 没有 `x:Class` → `MC6024`）→ **从数据侧接线**（模型实现 `INotifyPropertyChanged`，重建列表时订阅）。退订账本**每张卡各一本**（合成一本会出现"在 A 里改勾选 B 不响应"，很隐蔽）。
25. **`MediaPlayer` 没有 `Tag`**（`player.Tag = path` → `CS1061`）；要记"这实例挂着哪个文件"只能在外面开一本 `Dictionary<MediaPlayer, string>`。
26. **样式 `TargetType` 不匹配会被静默忽略**（`SmallPrimaryButtonStyle` 套到 `ToggleButton` 上既不报错也不生效）→ 镜像样式是 `SmallPrimaryToggleButtonStyle`。套样式前先确认 `TargetType`。
27. **XAML 属性值里不能嵌 ASCII 直引号**（会让解析提前结束、报一堆看不懂的行号错）→ 中文文案的引号统一用「」。
28. **新增可序列化集合字段必须防 null**（`SettingsService.Load` 里对新 List 全部 `??= new()`），否则老配置反序列化出 null，一 `.Add()` 就空引用。迁移照抄 `MigrateLegacySoundFiles`（只搬"绝对路径且文件还在"的）。
29. **`INotifyPropertyChanged` 不要加在 `MainWindow` 的多个 partial 上**（报"重复实现接口"）→ 统一放 `MainWindow.LockedCard.xaml.cs`，其他 partial 只调它的方法。
30. **卡片级"锁定态"用 `Tag` 传布尔**，不要 `DataContext`；也**不要整卡 `IsEnabled=False`**（总开关本身在卡片里，禁用整卡会连开关一起点不动，用户永远打不开）。
31. **【暗色下"列表背景发白"的真正根因】必须给 `ListBox` 写显式 `ControlTemplate`** —— Aero2 默认模板里有一个名叫 `Bd` 的内部 `Border` 负责背涂，取的是 `SystemColors.WindowBrush`，**跟应用的 `Background` 和主题都无关**（`Background="Transparent"` 设了也没用）。正解：隐式 `ListBox` 样式里写一条"纯 `ScrollViewer` + `ItemsPresenter`、不画任何背景"的模板。⚠ **这个白和 `IsEnabled` 完全没关系**（开关开着也是白的），只是关掉后内容没了衬托才显得扎眼——很容易误判成禁用态 bug。
32. **普通复选方框 `CheckBox` 在暗色下也是一块白**（Aero2 模板里画死的白底 + 系统色描边）→ 给 `TargetType="CheckBox"` 写隐式样式 + 自带 `ControlTemplate`（圆角方框 + `Path` 画的白色对勾）。带 `x:Key` 的开关样式（`ToggleSwitchStyle` / `InlineToggleStyle`）**不受影响**。禁用压暗要包一层外层 `Border` 改它的 `Opacity`，**别单独去改 `box`/`check` 的 `Opacity`**（会跟勾选触发器抢优先级 → 未勾选也显示淡淡的对勾）。
33. **`RenderTargetBitmap.Render(离屏元素)` 会得到全透明 / 全白** —— 没被选中的导航页根本没参与布局（`ActualWidth/Height` 是 0）→ 探针必须**先切页**（`nav.IsChecked = true`）再泵几轮 dispatcher。**不要靠肉眼看 PNG 定色**，要 `Template.FindName(...)` 读部件的真实属性值。
34. **`bool` 绑不到 `Visibility`**，要转换器（`Converters.cs` 的 `BoolToVisibilityConverter`，能放在独立 ResourceDictionary 里）。但这类"XAML 引用 C# 类型"的东西**编译过不代表运行期能解析**，必须开真窗口 `FindResource(...)` 试一次。
35. 改「关掉随机用哪个」时别忘了三条侧路：`ApplyXxxToNotifier` 里的兜底值、**预热**、**状态行文案**；而且关随机时**不要**清空勾选组合（重新打开随机要原样恢复）。
36. **「预览」和「真触发」必须是同一条抽签路径** —— 预览曾直接读旧字段 `SoundFile`（只在开关变动 / 列表重建时刷新）→ 在列表里改选中项它根本不跟着变，gold_ 报"预览老是 test.wav"。任何"试听 / 预览"入口都要复用真实播放的选文件逻辑。`SoundFile` 现在只剩"播放器内部兜底"一个用途。
37. **自检别自证** —— 探针要读"**被测代码实际写进去的**观测字段"（如 `ChatSoundNotifier.LastRequestedFile`），别在探针里把表达式再算一遍（那验的是"我以为它会怎么算"，等于没验）。写探针前问一句：**我读到的这个值，是被测代码写进去的，还是我算出来的？**
38. **删掉一行里的某个控件后，要检查那行的容器宽度** —— 去掉一个开关后 `Grid` 还留着当初按三个元素定的 `Width="392"`，结果右边空出 218px 空洞。**编译过看不出来，得量**（`ActualWidth` / `TransformToAncestor`）；容器别写死宽，用 `HorizontalAlignment="Left"` 让内容自己撑。
39. **`Environment.GetEnvironmentVariable` 返回 `string?`** → 写 `string? x = ...`（否则 `CS8600`）。
40. **`ShowActivated="False"` 不是"永不抢焦点"的保证** —— 只在**窗口第一次显示**时可靠，复用型窗口（`Hide()` 后再 `Show()`）不保证。硬保证是 Win32 的 `WS_EX_NOACTIVATE (0x08000000)`，在 `SourceInitialized` 里 `SetWindowLong(hwnd, GWL_EXSTYLE, ...)` 打上；要交互时临时摘掉、结束了打回。**验证要读实际位 + `GetForegroundWindow()` 前后对比**，别靠"看起来没抢"。
41. **`IsHitTestVisible="False"` ≠ 鼠标穿透** —— 它只管 WPF 层的命中测试，窗口在**系统眼里依然可点击**（全屏游戏里光标照样现形）。真穿透是 Win32 的 `WS_EX_TRANSPARENT (0x00000020)`（同一处 `GWL_EXSTYLE`）。`OverlayWindow.UpdateClickThrough` 早就这么做了，`KillBannerWindow` 曾漏。
42. **反复 `Hide()`/`Show()` 一个全屏 layered window 本身就是游戏扰动源**（每次显示都要重排 z-order、重建 layered surface）→ **更好的做法：窗口常驻、只切内容**（`Collapsed` / `Opacity=0`），这样 Win32 状态从头到尾不变。代价：退出路径要显式 `Close()`，否则进程退不干净。
43. **刚"变可见"就调 `SetWindowPos`，会把窗口尺寸冻死在"还没布局"的 2px 宽**（现象：悬浮窗启动后是一条只长高不长宽的 2px 细线，发消息也不变宽）。根因：`IsVisibleChanged` 里直接调 `EnsureTopmost()`（即 `SetWindowPos(..., SWP_NOSIZE)`），那一刻 WPF 还没跑第一次布局。**修法**：丢到 `Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ...)`，并给 `Width` 配一个同值的 `MinWidth` 兜底。
44. **抓屏抓游戏画面：必须从"屏幕 DC"抓，且必须带 `CAPTUREBLT`** —— 用窗口 DC（`GetDC(hwnd)`）抓硬件加速窗口常常拿到的全是黑；要从 `GetDC(IntPtr.Zero)` + `BitBlt(SRCCOPY | CAPTUREBLT)` 抓"屏幕上真实显示的那块"。区域放大抓的是**屏幕画面** → 目标必须真的显示在屏幕上（**独占全屏 F11 抓不到**，要窗口化 / 无边框全屏）。**判"是不是抓到黑屏"不要用颜色种数**（`颜色种数 > 4` 会把纯色区域判成黑屏），按**非黑像素比例（>2%）**就够了。
45. **给窗口设了 `WDA_EXCLUDEFROMCAPTURE` 之后，连自己的验证截图也看不到它**（为了不让放大窗"自己拍自己"，显示窗会 `SetWindowDisplayAffinity`；副作用是 `BitBlt` / `CopyFromScreen` 拍到的是它后面的桌面，极易误判成"显示窗是黑的"）→ 验证改用 `PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT=2)`。另外：**未声明 DPI 感知的进程**（我这层 PowerShell 就是）拿到的 `GetWindowRect` / `CopyFromScreen` 是**虚拟化坐标**，拿它当物理像素用会采到完全不相干的一块屏。
46. **按 100% 缩放写死的默认窗口位置，在 150%/200% 下会整块落到屏幕外** —— `SystemParameters.WorkArea` 是 **DIP**（屏幕 2560px / 150% 时工作区只有 ~1707 DIP 宽，默认 `DisplayLeft = 1900` 直接超界）。**修法**：显示前统一走 `ClampIntoScreen()`（整块不在工作区内就挪到右上角）。
47. **插件 dll 被运行时锁住 → 删不掉 / 覆盖不了**（`Directory.Delete` / `File.Copy` 抛 "Access to the path ... denied"；托管程序集一旦被装载，句柄在 ALC `Unload()` 之后也不一定马上放）。**修法**：新版本先整包拷到 `<id>.new\`，卸载残留写进 `pending-delete.txt`，下次启动 `ProcessPendingChanges()` 处理。**别跟锁硬碰。**
48. **插件配置目录必须和插件安装目录分开**（换版是"整目录替换"，配置放安装目录里会被换版吃掉用户设置）→ `IPluginHost.PluginDirectory` 指向 `%AppData%\MinecraftChatOverlay\plugin-data\<id>\`，安装目录只放 dll + plugin.json。
49. **主工程必须整目录排除 `Plugins\**`**，否则 wpftmp 临时工程报 `CS0579` 特性重复（主工程 `**\*.cs` 会把插件源码和插件 `obj\` 里的 `AssemblyInfo.cs` 一起编译，而 `DefaultItemExcludes` 的 `obj\**` 管不到 `Plugins\X\obj\`）→ csproj 里 `Compile/Page/ApplicationDefinition/EmbeddedResource/None` 全部 `Remove="Plugins\**"`。
50. **`UseWPF=true` 的工程隐式 using 里没有 `System.IO`**（`File` / `Path` / `Directory` 报 `CS0103`）→ 涉及 IO 的文件要显式 `using System.IO;`。
51. **命令中途抛异常会"半执行"，而且这台机器上 PowerShell 的 stderr 不回显**（工具只回一个 `[exit code: 1]`、没有任何输出，看起来像"命令太长被截断"，实际是脚本在中间抛了异常）。**教训**：一次只发小块命令（≲80 行）、关键步骤包 `try/catch` 并显式 `Write-Host` 错误、写完立刻读回来确认。（当年 `RegionCaptureService.cs` 就是这么丢的，靠调用方在用的成员签名重写回来的。）
52. **插件自绘页面里，样式 / 画刷一律用 `DynamicResource`，不能用 `StaticResource`** —— 插件控件是"先 new 出来、再挂进宿主窗口"的（`ContentFactory()` 在加入可视树之前就被调用了），`StaticResource` 在解析那一刻沿自身→父级→Application 找，而宿主的主题字典挂在 **Window** 上 → 直接抛 `XamlParseException`（整个插件页面白掉）。`DynamicResource` 是延迟解析的，挂上树再找 ✓。
53. **插件页面"填控件"时不能顺手存盘** —— `LoadFromSettings()` 里第一个 `IsChecked = true` 就触发 `Checked → SaveToSettings()`，此时后面几个 TextBox 还是空的 → 把 `""`/`false` 存了回去，用户原本的正则 / 按键 / 发送文字全被清掉。**修法**：加 `_loading` 闸门。**凡是"控件值变化即保存"的页面都要这个闸门。**
54. **`IsMouseOver` 是只读依赖属性，`RaiseEvent` 装不出来**（伪造 `MouseEnter` 没用，它由真实鼠标位置驱动）→ 必须真移动光标 `SetCursorPos`。**注意单位**：`PointToScreen` 在这个进程里返回 **DIP**，而 `SetCursorPos` 要**物理像素**（150% 缩放下要乘 `PresentationSource.FromVisual(win).CompositionTarget.TransformToDevice` 的比例）。稳妥写法：先按原值试，读 `IsMouseOver` 还是 false 就乘缩放再试。验完**把光标移回原位**。
55. **`TextBlock.Text` 在内容用 `Inlines.Add(...)` 填的时候读不到**（返回空串；症状是"探针说页面里开关数 0"、卡片永远匹配不上）→ 用 `new TextRange(block.ContentStart, block.ContentEnd).Text`。要在代码里按文字找控件，一律用这个，别信 `.Text`。
56. **`Compress-Archive` 在非交互的 PowerShell 调用里会静默失败**（退出码 0、没有任何输出、zip 根本没生成）。gold_ 双击 bat 是好的，所以这只是**我这边的调用方式**不行 → 我这边打 zip 直接用 Python 的 `zipfile`。
57. **新建的插件工程在我这边编不了**（`NETSDK1004 找不到资产文件 project.assets.json`，因为还原是坏的）→ 从任意能编的插件工程（如 `Plugins\RegionMagnifier\obj\`）复制 5 个 nuget 文件（`project.assets.json` / `project.nuget.cache` / `<项目名>.csproj.nuget.dgspec.json` / `.g.props` / `.g.targets`）到新工程的 `obj\`，把里面的项目名 / 程序集名全局替换掉，再用 `--no-restore` 编。
58. **插件页的初始化必须挂 `_loading` 闸门**（坑 53 的变种，触发源更隐蔽：`InitializeXxxUi()` 里设 `SelectedIndex = 0` → 触发 `SelectionChanged` → "值变了就存盘" → 而此时 `LoadXxxFromSettings()` 还没跑，空值被写回配置，症状是"用户存好的 API KEY 一打开插件页就没了"）。**顺序**：`InitializeComponent` → 闸门内初始化 → 再订阅事件。
59. **删掉宿主的 `AppSettings` 字段之后，旧配置值会在下次保存时被抹掉**（`SettingsService.Save` 是全量写盘，属性没了 JSON 里那个键就没了 → 用户的 API KEY 从 `settings.json` 里消失，靠 `settings.json.bak_*` 备份救回来）。**规矩**：任何"从宿主搬设置到插件"的迁移，**必须在删宿主字段之前把值读出来写进插件目录**；动手前先把 `settings.json` 复制一份。
60. **探针要拿"插件页本身"**，别拿 `PluginDetailContent.Children[0]`（那是宿主包页面的 `StackPanel`）→ 按类型名找：`FindAll<FrameworkElement>(detail).FirstOrDefault(x => x.GetType().Name == "PlayerQueryPage")`。
61. **`GameWindowFullscreen.FindCandidates()` 会把本程序自己的窗口排除掉**（按 PID 比对，防误选）→ 验「窗口全屏」要拿**别的进程**当靶子（`Process.Start("notepad.exe")`，测完 Kill 掉自己起的那一个，别去动用户开着的）。
62. **Win11 的记事本是打包应用，`Process.Start("notepad.exe")` 拿到的进程没有窗口句柄**（`MainWindowHandle` = 0、`MainWindowTitle` 空，看着像"根本没起来"）→ 按进程名重找 `Process.GetProcessesByName("Notepad")`（注意大写 N）。拿不到句柄就没法独立量窗口尺寸，"自检"会退化成只看插件自己报的状态文字 = 自证（坑 37）。
63. **插件跑在同一个进程里，所以它能改主程序的配色**（实测：`Application.Current.MainWindow.Resources["PrimaryBrush"] = new SolidColorBrush(...)` 就能整片换色，因为主题资源放在 **Window 层**、且全项目对 `PrimaryBrush` 的引用**全是 `DynamicResource`、0 处静态**）→ 想支持"主题 / 美化插件"**只要约定一套稳定的键名**就够，不需要开放更多内部；但**放任插件自己摸 `Resources` 是危险的**（静默失效、互相覆盖、能把界面改到不可用），要做就正式化（白名单入口 + 宿主校验 + 一键恢复）。
65. **悬浮窗上"点击"会被 `DragMove()` 吃掉** —— 窗口为了能拖住在 `Window_MouseLeftButtonDown` 里调 `DragMove()`，它进入模态移动循环并捕获鼠标，子控件的"抬起"永远收不到（表现：悬停提示都有，点下去毫无反应）→ 窗口层先判断"点的是不是可点击消息"，是就直接 return、不 `DragMove()`。
70. **`.ps1` 必须 UTF-8 带 BOM，`.bat` 要 GBK** —— 用 UTF-8 **无 BOM** 写 `.ps1`，PowerShell 5.1 会按 ANSI 读 → 中文注释变乱码 → 直接语法报错（"The Try statement is missing its Catch or Finally block."，而且报的还是看起来完全正常的那一行）。`.ps1` 要用 `New-Object System.Text.UTF8Encoding($true)` 写；`.bat` 要 GBK + `chcp 936`（和 `push.bat` / `publish.bat` 一致）。**注意这跟 `.cs` / `.xaml` 的「UTF-8 无 BOM」约定相反。** 附带两条：`ExecutionPolicy` 是 `Restricted` 的机器上不能直接跑 `.ps1` → 加个 `.bat` 包装（`powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0xxx.ps1" %*`）；`dotnet run --project X --nologo` 里的 `--nologo` 会被当成**传给程序的参数**，要给程序传参得用 `--` 分隔。
71. **robocopy 的 `/XD` 裸名会匹配任意层级的同名目录** —— `push.bat` 里原来写着 `/XD ... packages ...`（本意是排除打包产物），结果把 `market\packages` 也一起排除了，市场包根本推不上去、**而且不报错、静默不同步**。修法：写成全路径 `/XD "%DEV%\packages"`。**教训：给 robocopy 加排除项之后一定要干跑验证（`/L`，而且别加 `/NFL` —— 加了它只统计不列文件名，看起来像"没匹配到"）。**
72. **`tools\GmBlurCli` 在改成手动映射之后一直是编不出来的** —— 它的 csproj 只 `<Compile Include>` 了 `MotionBlurControlBlock.cs` + `GameProcessInjector.cs`，而 `Inject()` 现在要调 `ManualMapper.MapRemote` → `dotnet build tools\GmBlurCli\GmBlurCli.csproj` 直接 `CS0103: 找不到 ManualMapper`。已把 `ManualMapper.cs` / `ManualMapper.Remote.cs` 也加进 csproj（实测 0 warning / 0 error）。**教训**：工具工程用 `Compile Include` 链主工程源码时，主工程加文件必须同步给工具工程。
73. **`GameProcessInjector.EnsureSameBitness()` 是死代码**（`Inject()` 没调它；接它得先 `OpenProcess`，现在的 Inject 不自己开句柄）→ 位宽不对（32 位目标 / 32 位本程序）时的表现是"手动映射注入失败：…"，**看不到**那条人话提示。已在方法上标了 XML 注释"目前没有被调用"；要么接回去、要么删掉。
74. **钩子日志写哪儿是"注入前宿主告诉 DLL 的"，不是 DLL 自己算出来的** —— 手动映射进去的模块不在 loader 的模块表里：`GetModuleHandleExW(FROM_ADDRESS)` 反查不到（实测 false / 句柄 0），`self` 还是 NULL，于是 `GetModuleFileNameW` 给出的是**进程 exe 的路径**（实测非 0，不是失败）→ 老行为就是写到 `<游戏目录>\gmb_hook_<pid>.log`。现在改成：宿主（`GameMotionBlurService.Inject` / `gmblur inject`）在**注入之前**把目录写进控制块那段「输出目录」字符串区（就是 `gmblur dump` 用的 `dumpDirChars/dumpDirOffset`，260 字符；**协议没动、kVersion 没升**），DLL 起来后 `LogSetDirectory(ControlOutputDir())` 读过来 → 默认 `程序目录\logs\`。宿主没写 / 目录建不出来 → 退回游戏 exe 旁边；连 exe 路径都拿不到 → 才退 `%TEMP%`。界面按 程序目录\logs → 游戏目录 → DLL 目录 依次找，老 DLL 的日志照样能显示。另外：DLL 一进去的头几行（`已载入 pid=...`）在"还不知道写哪儿"的时候本来会被丢掉，现在先攒在静态缓冲里、开文件时补写（实测：多级目录自动建、BOM 正常、不可用盘退回 exe 旁边）。**改完要重新注入才会写新位置。**

---

## 4. 协作习惯（gold_）

- 先结论后推理；要"能不能做、代价是什么"；**不过度承诺**，宁可听坏消息
- **先分析、后动手**：方案摆出来等确认再改；但他明确让你做就直接做
- 他会自己实测，**每处改动都要能独立验证**，大功能拆小步
- 他会中途「插一嘴」改需求 / 改流程 —— **和功能要求一样重要**，照做
- 他授权过：编译被他的实例挡住时，**可以先关掉再编**（先说一声）
- 中文、结构化（表格 / 对比）、用「你」不用「您」、用「…」不用「...」
- 我犯过的错（引以为戒）：位置放错 3 次、方案理解错 1 次、测试预期写错 2 次、
  编辑把方法体删了 1 次 —— **每次改完都要读回来**

---

## 5. 插件平台速记

- **契约**：`Plugins\Abstractions\`（`PluginApi.Version = 1`，故意不引 WPF）。插件**只引它，永不引主程序 exe**。
- **权限模型**：插件跑在**主程序同一个进程**里，权限和主程序一模一样（联网、开窗、调 Win32、挂全局钩子、模拟键鼠都拦不住）。
  只有契约里有的才保证稳定；摸契约以外的东西（`MainWindow`、`Resources`、视觉树）属于**未定义行为**，宿主一重构就**静默失效**。
  所以"知情同意"很重要：导入前弹确认框列 `capabilities`。
- **页面**两种写法：表单式（`PluginPage` / `PluginField`[Toggle|Slider|Select|Text|ReadOnly] / `PluginAction` / `StatusText`，
  宿主用现有主题画，所以插件页和内置页长得一样）与自绘（用 **DynamicResource**、初始化挂 `_loading` 闸门 —— 见坑 52 / 53 / 58）。
- **插件包** = 一个 zip（`plugin.json` + dll + 可选资源）。两个插件根：exe 旁 `plugins\`（内置 / 便携）与
  `%APPDATA%\MinecraftChatOverlay\plugins\`（导入安装的）。
- **宿主侧代码全在 `Services\Plugins\`**：`PluginManifest`（宽容解析 plugin.json）、`PluginLoadContext`
  （每个插件一个可卸载 ALC；**契约程序集必须回落到宿主那一份**，否则 `IPlugin` 类型身份不一致）、`PluginEntry`、
  `HostWindowEnumerator`（给插件列游戏窗口）、`PluginHostContext`（每次调用都包 try/catch）、
  `PluginManager`（扫描 / 装载 / 隔离 / 消息总线 / 导入 zip / 启停 / 卸载 / 排队变更）。
- **禁用 vs 卸载（语义别搞混，改代码前先看这段）** —— `PluginManager.SetEnabled()` / `Uninstall()`：
  - **禁用** = `Unload()` + `State = Disabled` + 记进 `DisabledPlugins`（会持久化）→ **条目留着、卡片还在、开关变关**，
    下次启动不再装载，用户随时能再打开。（曾经这里误写成 `_entries.Remove`：卡片当场从列表消失、状态还存不下来。）
  - **卸载** = `Unload()`(+GC) → `TryWipeDirectory()`（**先 `Directory.Move` 改名搬走、再递归删**：直接 `Directory.Delete(recursive)`
    是逐个文件删的，撞上被锁的 dll 就抛异常、已删的不回滚 → 留一个"plugin.json 没了、dll 还在"的半死目录）
    → 条目**立刻从列表消失**；改名都失败（一个字都没删）就 `AddPendingDelete()` 排队，下次启动 `ProcessPendingChanges()` 清。
- **一键脚本**：`build-plugins.bat` —— 编契约 + 编所有插件 + 装到用户插件目录 + 打 zip 到
  `%APPDATA%\MinecraftChatOverlay\plugin-packages\`（可直接拖进「插件」页试装）。
- **现有插件**（`Plugins\`）：`AutoGg`（goldiamond.autogg 自动 GG）、`NeteaseLyrics`（goldiamond.neteaselyrics）、
  `PlayerQuery`、`RegionMagnifier`、`WindowFullscreen`、`SamplePlugin`（最小示例）。
  - **网易云歌词不是"探针可行性验证"了**：源码在 `Plugins\NeteaseLyrics\`、market 里 `1.1.5`；
    中继插件 MCOBridge 装到 `C:\betterncm\plugins_dev\MCOBridge\`。数据来源仍是"窗口标题 + 中继读进度"，
    但旧笔记里"探针已删、只是验证可行性"那句**已经不对**。

---

## 6. 插件市场 / 发布插件

**市场没有服务器** —— 清单就是仓库里的 `market/index.json`，插件包就是仓库里的 zip。

| 部分 | 在哪 |
|---|---|
| 清单模型 | `Services\Plugins\Market\PluginMarketModels.cs`（`MarketIndex` / `MarketPlugin`，宽容解析） |
| 联网 | `Services\Plugins\Market\PluginMarketClient.cs`（主源→备用源；下载 + 大小 / SHA256 校验） |
| 缓存 | `Services\Plugins\Market\PluginMarketCache.cs`（`%AppData%\MinecraftChatOverlay\market\`，断网显示上次列表） |
| 界面 | `MainWindow.Plugins.Market.xaml.cs` + `MainWindow.Modern.xaml` 的 `PluginMarketView` |
| 设置 | `AppSettings.PluginMarket`（`IndexUrl` 默认 jsDelivr 镜像、`FallbackUrl` 默认 raw） |
| 数据 | `market\index.json` + `market\packages\*.zip` + `market\README.md` |
| 生成清单 | `tools\rebuild-market-index.ps1` + `.bat` |

- **安装复用现有那条路**：下载完的 zip 直接交给 `PluginManager.ImportZip()` → 安装确认框（列 capabilities）、
  zip-slip 校验、apiVersion 校验、dll 被占用时排队到下次启动，**全都白送**，市场里没有第二套安装逻辑。
- 清单里的 `downloadUrl` 写**相对路径**，客户端按清单自身地址解析 → 换镜像 / 分支 / 仓库都不用改清单。
- 卡片状态按 **id + 版本**比较：`未安装` / `已安装 vX（已是最新）` / `可更新到 vY` / `需要的契约版本不符`。
- 启动时若缓存超过 6 小时，会**后台预热**一次（打开市场时列表已经在了）。

**发布插件**：`tools\publish-plugin.bat <插件目录名>`（例：`publish-plugin.bat AutoGg`）一条命令四步 ——
① 编 `Plugins\<插件名>` → ② 打包 `market\packages\<插件id>-<版本>.zip` → ③ 删掉这个插件的旧版本包（一个 id 只留最新）→
④ 重建 `market\index.json`。底层干活的是 `tools\pack-market-package.ps1`。
**故意不把插件装进本机** —— 否则市场卡片直接显示"已装最新"，就没法验"更新"这条路了。

**push.bat**（只在仓库副本里，开发目录没有）：4/4 段加了自动重试 —— `git push` 被拒（远程有本地没有的提交）
→ 自动 `git fetch` + `git pull --rebase origin main` → 重推，最多 3 轮；rebase 真出冲突就停下并打印手动处理步骤。

---

## 7. 文档在哪、哪份权威

| 文档 | 给谁看 / 权威性 |
|---|---|
| `README.md`（根） | **用户 / 功能说明**：页面表、各功能怎么用。 |
| `native\README.md` | 原生钩子（`native/`）怎么编、怎么验收；含 `build.ps1` 的 BOM 坑 + 注入现状。 |
| `about\README.md` | 开屏公告怎么发（`tools\announcement.bat`、`tools\publish-announcement.ps1`）、判新旧比 `updatedAt`、`LastSeenAnnouncement*` 字段。 |
| `market\README.md` | 插件市场怎么加包 / 重建清单。 |
| **`Plugins\PLUGIN-DEV-GUIDE.html`** | **插件开发文档 = 权威**：仓库里维护、跟着代码走，自己注明"文档与代码不一致时以 `PluginApi.cs` 为准"；**给插件作者 + AI**（第 11 节就是「附录：AI 速查规范」）。 |
| `Plugins\MCO插件开发指南_SDK文档版.html` | **参考**：另一份独立成篇的 SDK 文档，内容与上面重叠、章节更偏"规范"（能力与红线 / 调试 / 自己开窗口…）。原先只在 Downloads，已复制进项目。**遇冲突以 `PLUGIN-DEV-GUIDE.html`（进而 `PluginApi.cs`）为准。** |
| `DEV-NOTES.md`（本文） / `DEV-NOTES-ARCHIVE.md` | 开发交接笔记（现状 / 编译 / 坑） / **历史归档，不保证仍有效**。 |

**已核过（2026-10-02）**：`MCO插件开发指南_SDK文档版.html` 第 0 节原来指向不存在的 `Plugins\README.md` —— 已改指 `PLUGIN-DEV-GUIDE.html`，并在文件顶部加了"这份不是权威文档"的说明框；它 3.1 / 3.3 里"卸载先禁用 + 排 pending-delete"的旧说法也一并改成了现在的语义（先改名搬走 + 卡片立刻消失）。

另一条对比：把用户给的「Minecraft Chat Overlay 插件开发文档（apiVersion 1）.html」快照和仓库里的 `PLUGIN-DEV-GUIDE.html` 逐行比 —— 标题 / 章节完全相同，但快照**少 4 处**（6.4 的弹窗 Grid 三行写法、排错表 2 行、AI 速查第 15 / 16 条）。说明那份快照是**较旧的版本**，仓库里这份才是最新的（对应 `CHANGELOG.md` 里"新增 6.4 弹窗 … 排速查补到 16 条"那次）。**所以别拿快照覆盖仓库那份。**

---

## 8. 历史归档

`DEV-NOTES-ARCHIVE.md` = 拆分前的 `DEV-NOTES.md` 全文，包含：当时的「2026-10-02 勘误与追加」节、击杀反馈 v8~v14 逐版流水账、
区域放大 / 快捷命令的完整来龙去脉、插件平台的四段实测评估、以及**坑 1~71 的完整版**（本文第 3 节是它的精简）。
**只作考古用**，里面不少结论已经被代码推翻（例如"动态模糊走 `CreateRemoteThread + LoadLibraryW`"、
"`gmblur inject` 的相对路径由目标进程解析"、"公告比 id"这三条都已过期）。

---

## 维护约定

- 本文只写**今天依然成立**的；过时的内容**移去 ARCHIVE**，别留在正文里当"历史注记"。
- 每完成一段就更新这个文件，别让它过时。
- 每条数字 / 结论都要对着代码或实测核过再写；不确定的标「**待核**」。

- **改文件用整份重写，或按行号 `InsertRange`；禁止"按内容做批量整行替换"** —— 修"卸载时摘条目"那次按 `_disabled.Add(...)` 匹配，同时命中了 `SetEnabled` 的禁用分支，把"禁用"改成了"从列表消失且不记状态"。下手前先打印**所有**命中行号。
- **找方法结尾别用 `while (line -ne '}')`** —— 会抓到内层 `if` 的 `}`，把成员插进方法体里。按缩进（`^    }$`）或数大括号来定位。
- **同步 / 提交**：开发目录（`C:\MinecraftChatOverlayDSUI3`，**不是 git 仓库**）--robocopy /E--> 仓库副本 `C:\Github\MinecraftChatOverlay`（排除 bin obj .vs .git design publish* 等）；**`/E` 不会删仓库里多出来的文件**，发现多余的要手动删。推送要在仓库副本里做，且**直接跑 `git` 会让 pwsh 会话崩** → 用 `Start-Process git -ArgumentList (...) -NoNewWindow -Wait -RedirectStandardOutput <文件>` 再读文件（判断成败看退出码，别被 stderr 骗，见坑 51）。
- **行尾**：`native\**` 的 `.h` / `.cpp` **一律 LF**；`.cs` / `.xaml` 是**混的**（大约一半 LF、一半 CRLF）。
  改哪份就跟着那份原有的行尾走（按行号打补丁天然会跟住），**别顺手把整份文件归成 CRLF** —— 那是整文件 diff。
- ⚠ **命令行里别出现全角引号（U+201C / U+201D）**：会让整条命令失败（`exit 1`、一个字的输出都没有，看起来像"什么都没发生"）。
  写引号用半角 `"`，中文文案统一用「」；往文件里写中文用 `[IO.File]::WriteAllText(路径, 文本, UTF8Encoding($false))`。

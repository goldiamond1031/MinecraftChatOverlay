# DEV-NOTES —— 开发交接笔记

> 给下一个会话（和未来的我）。**每完成一段就更新这个文件，别让它过时。**
> 功能文档在 `README.md` 和 `native/README.md`，这里只放"现在做到哪、接下来做什么、怎么编译、什么不能做"。

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

## 1. 正在做：击杀反馈

需求：检测到**自己**的击杀时，在游戏画面上做一次视觉反馈。
当前范围：只做 Minecraft Java 版（OpenGL）；基岩版 / D3D11 路径已移除。
他已选定效果 = **8 种效果可多选**（缩放脉冲 / 边缘脉冲 / 暗角脉冲 / 色差分离 / 抖动 / 全屏闪光 / 中心冲击环 / 故障撕裂），每种可独立输入大小（0~500%），边缘脉冲可选颜色；另有 WPF 击杀横幅。确认**走注入**（动态模糊一直在用）。
（原第 9 种「径向模糊」已按 gold_ 要求移除。）
另有「击杀图标」（屏幕中部弹确认图标，CS / 瓦那种）和「击杀提示音」，
v8（2026-09-28 下午）改成：
- **图标和音效都不再随程序发布，全部由用户导入**（内置图标 `KillIcons.xaml` 与
  `res/notify_*.wav` 已删，版权上干净）
- 列表项带**勾选框** + **随机抽取**（图标、音效、消息提示音三处各一套独立开关）
  - v10 起勾选框**只在开随机时才显示**：关掉随机时它没有意义（用选中的那个），
    藏起来免得用户以为"勾了才用"
- 图标支持**屏幕上真拖动定位**，存百分比坐标
- 显示时长拆成**淡入 + 停留 + 淡出**三段
- 连杀：**图标直接换新并刷新时长**；**音效不掐断、叠着响**（`ChatSoundNotifier._voices` 实例池，上限 6 路）

v9（2026-09-28 傍晚）修暗色模式的两处"日间白"：
- **列表背景白** → `ListBox` 缺显式 `ControlTemplate`，Aero2 模板里的 `Border#Bd` 画的（见坑 31）
- **复选方框白** → `CheckBox` 同样问题，已加隐式样式（见坑 32，顺带修好全工程 9 个裸勾选框）
- 这两处和 `IsEnabled` **无关**（开关开着也白），之前一直误判成禁用态 bug

v10（2026-09-28 晚）改随机语义：
- **关掉随机 = 用列表里选中的那一项**（v8/v9 是"固定用第一项"）
- **勾选框只在开随机时才显示**，关掉随机时 `Collapsed`
- 三张卡（击杀图标 / 击杀提示音 / 消息提示音）统一
- 好处：关掉随机时勾选组合**原样留着**，重新打开随机立刻恢复，不用重勾

v11（2026-09-28 晚）修「预览播错音」：
- 现象：击杀提示音列表里换选另一个音，点【触发一次】还是放老的（他配置里是 `test.wav`）
- 根因：`PreviewKillSoundForEffect()` 读的是旧字段 `SoundFile`，该字段不跟列表选中项走（见坑 36）
- 修法：预览改走 `RandomPicker.Pick(..., selected)`，和真实击杀同一套
- 顺带：`ChatSoundNotifier` 加只读观测字段 `LastRequestedFile`，供自检读"实际放了什么"（见坑 37）
- 自检 7/7 通过：关随机选第 2 项 → 放第 2 项；改选第 1 项 → 跟着换；开随机只勾 1 项 → 恒放它

v12（2026-09-28 晚）去掉预览的「循环播放」开关 + 消 CS8600：
- 循环开关整个移除（`KillFeedbackLoopCheckBox` 及其事件、`KillPreviewLoopGapMs` 常量）
  —— 预览现在**只演一遍**，放完清状态行。少一个开关，少一个状态。
- **拆开关后别忘行容器宽度**：那行 `<Grid Width="392">` 是按「三个元素」定的宽，
  去掉循环开关后只剩两个按钮（约 174px 内容），右侧留了 218px 空洞。
  改成不写死宽 + `HorizontalAlignment="Left"`，容器自动收窄（见坑 38）。
- CS8600 修法：`Environment.GetEnvironmentVariable` 返回 `string?`，
  接它的局部变量要**显式写成 `string?`**，否则编译器报「null 塞进非空 string」（见坑 39）。

v13（2026-09-28 晚）修「击杀图标弹出把游戏顶掉焦点 → 游戏自己弹 ESC」：
- 现象（gold_ 报）：游戏里触发击杀图标，像"被切了窗口"，游戏变成 ESC 菜单
- 根因：`KillBannerWindow` 只靠 XAML 的 `ShowActivated="False"`，
  **没有 Win32 层的 `WS_EX_NOACTIVATE`**。`ShowActivated` 只在窗口**首次显示**时可靠，
  而这个窗口是「`Hide()` 了下次击杀再 `Show()`」的复用窗口 —— 重新 Show 时不保证不激活，
  于是把游戏顶到后台，Minecraft 失焦就自己弹 ESC。
  （对照：`OverlayWindow` 是显式 `SetWindowPos(..., SWP_NOACTIVATE)` 的，所以聊天悬浮窗没这毛病。）
- 修法：`SourceInitialized` 里打上 `WS_EX_NOACTIVATE`；`ShowIcon` 里再确认一次；
  **拖动模式临时摘掉、`EndDragMode` 打回**（拖动要正常收鼠标）。
- 自检 6/6：初始有 → `Hide`→`Show` 后仍在 → 前台窗口不变 → 拖动中摘掉 → 退出后打回

v14（2026-09-28 晚）击杀图标窗口改「常驻 + 鼠标真穿透」：
- gold_ 报：击杀图标出现时**鼠标光标也会出现**，还是在影响游戏
- 两个独立根因：
  1. **鼠标光标** —— XAML 的 `IsHitTestVisible="False"` 只管 WPF 内部命中测试，
     **窗口在系统眼里仍是"可点击的"**，鼠标移上去光标就现形。
     真正的穿透是 Win32 的 `WS_EX_TRANSPARENT (0x20)`（见坑 41）。
  2. **反复 `Hide()`/`Show()` 本身就是扰动源** —— 每次显示都要重排 z-order、
     重建 layered surface，全屏游戏会被干扰。
- 修法：窗口**启动即创建并常驻**（`EnsureKillBannerWindow` → `EnsureVisible`），
  之后**再也不 Hide/Show**。「不显示」改由图标自身 `Collapsed`/`Opacity=0` 表达。
  击杀时只有一个"让图标亮一下"的动作，没有任何窗口级操作。
- ExStyle 从 `0x00080008` → `0x08080028`（多了 NOACTIVATE `0x08000000` 和 TRANSPARENT `0x20`）
- 拖动模式临时摘掉两个位、`EndDragMode` 打回（拖动要收鼠标）
- 自检 12/12：句柄连续 3 次击杀不变、窗口始终 `IsVisible`、图标自己收起而窗口不动、
  拖动摘位/退打回 全过
- 退出路径：`MainWindow.Window_Closing` 里 `_killBanner?.Close()`（常驻窗口不显式关会退不干净）

真实日志样本（格式就长这样，别再猜）：

```
[CHAT] 纉襫 击败了 掉打你们除了g!
```

- 他的游戏 ID：**纉襫**
- 击杀消息里**可能带** `[vip1]`、`[25阶1003⚝]`、`【称号】` 装饰
- VIP 图标在日志里本来是**私用区字符 U+E0F8..U+E0FF**；日志走 GBK 时被
  `LogTextDecoder` 在字节层换成 `[vipN]`，走 UTF-8 时**原样保留**
- ⚠ `LogTextDecoder.MapVipGlyphs()` 是**死代码**（只有定义没有调用）

### 已完成并验收

| 部分 | 文件 | 状态 |
|---|---|---|
| 匹配（多行规则、不锚定行首、剥离装饰、从示例生成） | `Services/KillFeedback/KillFeedbackMatcher.cs` | ✅ 自检 18/19 |
| 效果渲染器（纯像素、**不依赖 WPF**） | `Services/KillFeedback/KillFeedbackEffects.cs` | ✅ |
| 效果预览器（用最近一张游戏截图实时演算） | `MainWindow.KillFeed.xaml.cs` | ✅ |
| 事件通道 + 多效果配置（共享内存 v2→v4） | `native/.../common.h` + `Services/GameMotionBlur/MotionBlurControlBlock.cs` | ✅ 两边同步、probe 自检通过 |
| 效果状态机（QPC 计时 + 快进缓出包络 + 多效果并播） | `native/.../control.cpp` `ControlTickKillEffects()` | ✅ |
| **OpenGL 8 效果** | `native/.../gl_blur.cpp` | ✅ 新 shader 已编译并导出帧验证（径向模糊已删） |
| 界面 | `MainWindow.KillFeed.xaml.cs` + `KillFeedPanel` | ✅ |
| 接入真实聊天流 | `MainWindow.xaml.cs` `Watcher_ChatLineReceived` → `NotifyKillFeedback` | ✅ |
| `gmblur kill` / `gmblur killall` 命令 | `tools/GmBlurCli/Program.cs` | ✅ |

### 设计要点（别改丢）

1. **规则 = 一段固定文本，一行一条，命中任意一条就算，不锚定行首**。
   因为他的 ID 一会儿在前（`纉襫 击败了 X`）一会儿在后（`X 被 纉襫 击败了`）。
   默认**纯文本**（自动 `Regex.Escape`），可勾「按正则解释」。
   空行和 `#` 开头的行跳过。
2. **规则不含敌方名字、不含收尾**。收尾是 `!` 还是 `，达成连杀！` 都不影响。
3. **剥离三类装饰**：半角 `[...]`、全角 `【...】`、私用区字符。剥空了退回原文。
4. **生成规则**：用户填「自己的ID + 示例消息 + 示例里的敌方名字」，
   敌方名字**只当切点**（在前取它之后、在后取它之前），不写进规则。
5. **效果状态机放在 `control.cpp`**（`ControlTickKillEffects`），
   只在 OpenGL 出帧钩子里使用；多效果都在这一帧状态里算好。
6. **包络常数必须和 C# 预览器一致**：RisePortion `0.16`、DecayRate `3.6`；缩放峰值 `0.10`、抖动 UV `0.012`、色差 UV `0.0045`、边缘/暗角强度峰值 `1.0`；闪光 `0.55`、冲击环/故障撕裂 `1.0`。
   改一处必须改另一处，否则"预览和游戏里不一样"。
7. **计时用 QPC**，别用 `GetTickCount64`（15.6ms 粒度，400ms 动画只有 25 步）。
8. **事件用序号不用标志位**（标志位连杀时会被合并掉）。
9. 预览器**不播放时改参数直接渲染峰值帧**（`PeakElapsedMs`）——
   画 elapsed=0 永远是一张原图，看不出强度变化。

### 自检实测数据（真实 C# / 真实 DLL）

匹配：**18/19**。唯一 FAIL 是**测试预期写错**——
`X 被 纉襫 击败了` 同时命中第 1 条规则 `纉襫 击败了`，而匹配是"第一条命中即返回"。
→ 写"命中哪条"的断言前，**先确认更靠前的规则不会也命中**（这个坑踩了两次）。

原生：注入 `TestGL.exe`，用隔离映射 `GMBLUR_MAP` 避免干扰真实游戏进程。
`gmblur killall 500 300 200 150 250 900` 一次触发全部 5 种效果：
DLL 累计放过 1 次、峰值缩放 **1.485x**（500% × 0.10 = 1.5，包络采样到 1.485）、零 GL 错误。
像素层面：逐个效果导出帧确认——边缘脉冲边框亮度 +197、暗角边框 -2、
色差 R-B 通道差约 -4.9、抖动/缩放的前后帧差异明显；没有出现帧全黑。

---

## 2. 当前状态与下一步

**2026-09-28（下午）已完成：**
* **移除径向模糊**（gold_ 要求）。原生枚举 `kKillEffectRadialBlur = 9` 已删，`kKillEffectGlitch` 从 10 **前移补位到 9**，
  共享内存协议 **kVersion / Version 升到 6**（`common.h` + `MotionBlurControlBlock.cs`）。
  C# 侧同步删了 `KillFeedbackEffect.RadialBlur`、`RenderRadialBlur()`、`KillFeedbackSettings.RadialBlur*`、
  XAML 里那一行 Grid；CLI `killall` 从 9 参数变 8 参数（旧 5 参数兼容格式照旧）。
  DLL 重编：`native\dist\GameMotionBlurHook.dll` = **107273 字节**，
  SHA256 `BDEF86C25A927EF134DAD8579F79B093D491976B48ADA15D8A212DFD883DD6C7`，
  已同步到 `native\dist\GameMotionBlurHook_v6.dll`（settings.json 指的就是这个名字）+ `bin\Debug\net8.0-windows\`。
* **修布局**（第二轮，第一轮改像素没解决根本问题）：
  根因是**隐式样式覆盖**——原来只用 `Width="22"` 想缩小色块按钮，但隐式 Button 样式带 `MinWidth=72`，
  按钮被撑成 72px 大胶囊压在数字框上。现在：
  - `ModernControls.xaml` 新增 `SwatchButtonStyle`（26×26 真色块）和 `EffectPercentTextBoxStyle`（30 高数字框）；
  - 效果列表重排成统一栅格：复选框 `*` + 数字框 `64` + `%` + 色块/占位 `38`，
    色块挪到**最右**，8 行数字框对齐成一列；
  - 左列 240→272，预览区固定 392×221（16:9），按钮行同宽，`KillFeedbackPreviewStatusText` 从按钮行里拆出来单独一行。
* **预览播击杀音效**（新需求）：点【触发一次】时调 `PreviewKillSoundForEffect()`，
  **跟随「击杀提示音」开关**（关着不响），和游戏里真实击杀表现一致。
* **闪白问题的提示**（gold_ 要求先不改代码，只在卡片底部提示）：
  实测（TestGL 800×600，dump 帧测中心/边缘亮度）——
  无效果 中心5/边缘5、只开暗角 中心5/边缘2.2、只开闪白 中心255/边缘255、**闪白+暗角 中心255/边缘226.3**。
  即：**暗角会把闪白在边缘吃掉 ~12%，中心不受影响**；画面整体越亮越明显（8 位缓冲到顶就加不动了）。
  已在预览卡片底部加一行 `AmberBrush` 提示说明这一点。

**必须重新注入：** v5 → v6 语义已变（故障撕裂 10→9），**游戏必须完全退出后重新注入**。
旧 DLL 一旦 LoadLibrary 进进程，`DllMain` 不会再跑，光点【注入并接管】换不掉。

**2026-09-28（下午·第二件）已完成：击杀横幅 → 击杀图标**

gold_ 想要 CS / 瓦 那种"击杀后屏幕中部偏下弹一个图标"。原来的「击杀横幅」只有一行文字，
**整体换掉**（不留文字横幅，敌人 ID 也不显示）。

* 新文件 `KillIcons.xaml`：8 个自绘矢量图标，归一化到 0..100 坐标框、EvenOdd 挖空。
  主力是「骷髅 + 交叉骨」。**路径数据全是原创的，不含任何 Valve 素材，可随程序分发。**
* `KillBannerWindow` 从「居中文字条」改成**铺满全屏的透明画布**，图标按 X/Y 百分比摆放。
  位置直接用 `SystemParameters.WorkArea` 算（**别读窗口 `ActualWidth`**，见坑 19）。
* 设置页新字段：`BannerIcon`（内置 key / `__custom__`）、`BannerCustomImagePath`、
  `BannerPosX` / `BannerPosY`（0~1 百分比）、`BannerIconSize`、`BannerIconColor`。
  `BannerText` 字段保留，只为让老配置能读进来。
* 设置页 UI：图标列表（带小预览）+ 落点预览框 + 颜色色块 + 4 个滑块（X / Y / 大小 / 停留）
  + 【浏览…】导入自己的图片 + 【在屏幕上试一下】。
* 验收：位置自检 5 个坐标点全部精确落位；8 个图标渲染正常；**0 error**；启动自检 `exit=124`。
* 临时探针（`IconRenderProbe.cs` / `KillIconSelfCheck.cs` / `--selfcheck-killicon` 入口）**已全部清理**。

**2026-09-28（上午）已完成：**
* 9 种效果全部进 DLL：缩放脉冲、边缘脉冲（可自定义颜色）、暗角脉冲、色差分离、抖动、全屏闪光、中心冲击环、径向模糊/速度线、故障撕裂。
  （当天下午已按 gold_ 要求**移除径向模糊**，剩 8 种，协议升 v6。）
* 多效果同时播放；每种效果独立启用、独立输入大小，界面允许 0~500%（内部 0.0~5.0）。
* 新增 WPF 击杀横幅：可配置文字与停留时间，在画面上方淡入淡出。
* 共享内存升级到 **v5**：`kVersion = 5`，`KillEffectConfig.reserved` 低 24 位作为边缘脉冲的 RRGGBB 颜色。
* 生产产物：`native\dist\GameMotionBlurHook_v6.dll`，**108349 字节**，
  SHA256 `BDE252346A62575A59D897541ACE5F5E846AA0E25FEC93D4DFEEDB6C2CCD440E`；
  已同步到 `bin\Debug\net8.0-windows\GameMotionBlurHook.dll`。
  `%APPDATA%\MinecraftChatOverlay\settings.json` 的 `MotionBlur.HookDllPath` 指向 v6 DLL。
  （2026-09-28：移除 JVM 探针后重新编译，体积从 121579 降到 108349。
  控制块协议没动，仍是 v5/v6，所以 `settings.json` 和 `kVersion` 都不用改。）
* 验证：TestGL（隔离映射 `GMBLUR_MAP`）导出帧确认 shader 编译成功、新效果产生像素变化；
  `gmblur killall` 支持旧 5 参数和新 9 参数两种格式，事件通道 `EventPlayCount 0→1`。

**必须重新注入：** v4 → v5 协议语义已变，旧 DLL 里没有颜色/新效果，游戏必须完全退出后注入 v6。
（2026-09-28 补充：旧的 121579 字节那版 DLL 里还编着 JVM 探针，已连同源码一起移除。
如果你的游戏进程里已经加载过旧 DLL，**要完全退出游戏再重新注入**才会换成新的干净版 ——
DLL 一旦 LoadLibrary 进进程，`DllMain` 就不会再跑，光点【注入并接管】换不掉。）

**下一步（按顺序）：**
1. **等 gold_ 在真实游戏里实测**：确认 8 效果叠加的视觉强度、500% 上限是否合适；
   以及「全屏闪光 + 暗角脉冲」在他实际的画面亮度下到底能不能看出来。
2. 连杀相关需求已明确不做（分级、连杀光环、变调等）。
3. 可选：把闪白改成「向白色混合 + 最终 clamp」（现在只在界面加了提示，没改代码）——
   真要改必须 C# 预览器和 `gl_blur.cpp` 着色器一起改，否则预览和游戏不一致。
4. 可选：「游戏动态模糊」页的进程列表把明显不是游戏的项过滤/标出来。

---

## 3. 编译与验收（**必读：这台机器的 NuGet 是坏的**）

### .NET 侧

`dotnet restore` 在这台机器**彻底坏**：
`NuGet.targets: error : Value cannot be null. (Parameter 'path1')`。
根因 = 进程里 **`APPDATA` 环境变量缺失**，NuGet 在静态构造里拿它拼路径。
**没法新建需要还原的工程**，别尝试。

能用的命令：

```bash
cd C:/MinecraftChatOverlayDSUI3
APPDATA='C:\Users\gold_\AppData\Roaming' dotnet build MinecraftChatOverlay.csproj --no-restore -v m
```

- **改过 XAML 必须带 APPDATA**（WPF 标记编译生成 `*_wpftmp.csproj`，它自己要走一遍
  `_GetRestoreSettings`，不带就报 `NETSDK1060 加载锁定文件出错`——那条信息很坑人，
  `project.assets.json` 其实是好的，别去删 obj）
- 既有 2 个 `CS8600` 警告在 `MotionBlurControlBlock.cs`，**不是新引入的**，别误判

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

（2026-09-28 起原生侧**不再需要 JDK**。原先那个 `jmc_probe.cpp` —— JNI 探针，
会附着到游戏进程里的 JVM 再反射读聊天类布局 —— 连同 `dllmain.cpp` 的调用和
`build.ps1` 里的 `jni.h` 探测段一起**已彻底移除**：它属于已废弃的「真实颜色提取」
路线，且「注入 + 从游戏内部取聊天信息」本身就是项目红线禁止的方向。）

### 验收套路（每次改完都走）

1. 编译看 **0 error**
2. **改过 XAML 必须做启动自检**（XAML 的 `StaticResource` / 事件绑定是运行时解析的）：

   ```bash
   cd bin/Debug/net8.0-windows && timeout 7 ./MinecraftChatOverlay.exe; code=$?
   # exit=124 = 跑满 7 秒没崩 = 窗口加载正常（会闪一下配置窗口+悬浮窗）
   ```

3. **标签开闭平衡**（大段改 XAML 后必查）：

   ```python
   import re
   s = open('MainWindow.Modern.xaml', encoding='utf-8').read()
   for tag in ['Border','StackPanel','Grid','ScrollViewer','TextBlock','CheckBox','ListBox','Button','UniformGrid','Slider','Image','RadioButton','TextBox']:
       o = len(re.findall('<'+tag+r'[ />]', s))
       sc = len(re.findall('<'+tag+r'[^>]*/>', s))
       c = len(re.findall('</'+tag+r'>', s))
       if o - sc - c: print(tag, '差', o-sc-c)
   ```

4. **大段编辑后必须读回来确认**——有一次把整个方法体删了都没发现
5. **想跑独立验证**（新建工程跑不了）：在 `App.xaml.cs` 的 `OnStartup` 开头加一个
   **环境变量触发的自检入口**，结果写 `%TEMP%` 文件，**跑完必须删干净**
   （`KillFeedbackSelfTest.cs` 就是这么做的，已删，可以照着重写）
6. 产物被运行中的实例锁住（`MSB3027`）：
   gold_ **授权直接关他的实例**（先说一声），用 PowerShell `Stop-Process`；
   或者 `mv` 挪开 + `cp` 新的进来绕过（被挪走那份删不掉，会留个 `.old-v2` 残留）
7. 原生验收不用碰游戏：

   ```bash
   native/dist/TestGL.exe &                        # 测试画面，窗口标题带 PID
   gmblur list && gmblur inject <PID> <DLL 绝对路径>
   gmblur kill 1.0 1200                            # 自带结论输出
   gmblur dump <目录> 5                            # 导帧对比
   ```

   ⚠ **`inject` 显式传 DLL 路径时必须是绝对路径**：注入器把路径字符串原样写进目标进程，
   目标进程按**它自己的**工作目录去解析。写相对路径（如 `native/dist/xxx.dll`）时
   TestGL 的 CWD 是 `dist`，那个相对路径不存在 → `LoadLibrary` 返回 0，
   报「目标进程拒绝了 LoadLibrary」，看着像 DLL 坏了，其实只是路径错。
   （不传第三个参数时 `DefaultDllPath()` 给的是绝对路径，所以不写反而没事。）
   ⚠ TestGL 是**当前 shell 的子进程** —— 用后台 bash 起、或让它跟着 shell 一起退出，
   都没法从另一个调用里注入。要么开一个常驻的后台任务把 TestGL 挂着，要么用别的方式让它真正独立存活。

---

## 4. 这个项目已知的坑（都踩过，别再踩）

1. **改共享内存字段必须两边一起改**：`native/.../common.h` + `Services/GameMotionBlur/MotionBlurControlBlock.cs`，
   并把 `kVersion` / `Version` 各加一。probe 自检会抓 sizeof 和几个偏移，
   但**抓不到"两边字段顺序不一样但大小恰好相同"**——所以别只靠自检。
2. **击杀效果要放在 `GlBlurOnSwapLocked` 的早退判断之后**，
   否则帧混合关着时击杀效果永远不生效。同理注意只有效果时 `ctl` 可能是 `nullptr`。
3. **着色器加 uniform 要给默认值**（`uUvScale` 默认 `(1,1)`），否则帧混合那条路会被影响。
4. `GmBlurCli.DefaultDllPath()` 的相对路径已修（原来少一级 `../`）。
5. 「游戏动态模糊」页的进程列表 = **所有带窗口的进程**，很容易选错
   （这次钩子进了 Windows「设置」的进程）。
6. `SettingsService.Save` 是全量同步写盘 —— 滑块类要 debounce
   （击杀反馈页的 `QueueKillFeedbackSave` 是 400ms debounce，照这个来）。
7. `LogTextDecoder.MapVipGlyphs()` 是死代码（只有定义没调用）。
8. `MainWindow.xaml` / `GUI.html` / `design/` / `promo/` 不参与编译，是历史资产。
9. 加新导航页要改 **4 处**：NavPanel 的 RadioButton、新 ScrollViewer、
   `MoveIndicatorToSelected()`、`AnimateActivePanel()`（漏后两个 = 点了导航没反应）。
10. `StartListening()` 第一行就是 `SaveSettingsFromUi()` → **启动就会写一次 settings.json**，是原有行为。
11. 数字输入框的排版规矩：标签在上 + 输入框占满 + `Padding="12,0,NN,0"` 右侧留白 +
    单位文字 `IsHitTestVisible="False"` 浮在右边。**别写 `Width="70"` 的小框**。
12. 卡片形态抄「启用自动 GG」那张：`ToggleSwitchStyle` 的 CheckBox + 15px ExtraBold 标题 + 12px 说明。
13. 新界面逻辑一律另开 `MainWindow.*.xaml.cs` partial，别往主文件堆。
14. `MainWindow.Modern.xaml` 里 8 个面板是**同级的 ScrollViewer**，
    插卡片前先用 `grep -nE '<ScrollViewer x:Name=' MainWindow.Modern.xaml` 量边界
    （我在这上面放错过 3 次位置）。
15. **`Border` 只能有一个子级** —— 往卡片底部 `<Border>` 里补第二行提示时会直接报
    `MC3089 对象"Border"已经具有子级`。要么把两个 `TextBlock` 塞进一个 `StackPanel`，要么拆成两个 Border。
16. **`WarningBrush` 不存在**，别顺手写。要警示色用 `AmberBrush`（#FFB547）；
    参考 `ModernControls.xaml` 里的 brush key 列表，写之前先 grep 一遍。
17. **移除一个效果要改 6 处**（漏一处就编译不过或运行期错位）：
    `KillFeedbackEffects.cs`（枚举 + case + 方法）、`MotionBlurControlBlock.cs`（枚举值）、
    `KillFeedbackSettings.cs`（两个字段）、`MainWindow.KillFeed.xaml.cs`（4 个引用点）、
    `MainWindow.Modern.xaml`（那一行 Grid）、`control.h`/`control.cpp`/`gl_blur.cpp`（原生枚举 + case + uniform），
    外加 `tools/GmBlurCli/Program.cs` 的 `killall` 参数表。改完重编 DLL 并同步三处副本。
18. **隐式样式会覆盖你手写的 `Width`/`Padding`/`MinHeight`** —— 这是我在色块按钮上翻车的真正原因：
    隐式 `TargetType="Button"` 带 `MinWidth=72 / Height=40 / Padding=16,0`，
    隐式 `TargetType="TextBox"` 带 `MinHeight=40 / Padding=12,0`。
    `Width="22"` 干不过 `MinWidth="72"`，所以色块按钮被撑成一个大胶囊压在数字框上。
    **在紧凑列表里放小控件，一定要写独立的 `Style`（不 BasedOn 隐式样式），别只改 `Width`。**
    已加 `SwatchButtonStyle`（26×26 色块按钮）和 `EffectPercentTextBoxStyle`（30 高数字框）在 `ModernControls.xaml`。
19. **窗口 `Show()` 之前 `ActualWidth/Height` 恒为 0** —— 击杀图标窗口就是栽在这：
    先设 `Left/Top/Width/Height` 没生效，`ActualWidth` 还是 0，退回读 XAML 里 `Width="560"` 的默认值算坐标，
    图标全跑到右下角。**正解：位置直接用 `SystemParameters.WorkArea` 算，不读窗口自身尺寸。**
    这个 bug 我自己的自检工具还掩盖过一次 —— 自检画布写死 1280×720，真实屏幕 1707×1019，
    两套坐标系对不上，看起来"还是偏"，害我多绕两圈。**自检工具的坐标系必须和被测代码一致。**
20. **`Viewbox` / `VisualBrush` 会自动缩放内容** —— 想把窗口内容"原样"贴到另一块画布上，
    必须 `Stretch="None"` + `AlignmentX/Y="Left/Top"`，否则整块被拉伸，位置全乱。
21. **`KillBannerWindow` 读的是物理文件 `KillIcons.xaml`**（`XamlReader.Parse(File.ReadAllText(...))`），
    没走 WPF 资源编译 —— 所以 **csproj 里必须配 `<None Update="KillIcons.xaml" CopyToOutputDirectory="PreserveNewest"/>`**，
    否则发布后 `bin` 里没这个文件，图标会全部退化成 `Geometry.Empty`（什么都不显示）。
22. **CS2 官方素材不能直接打包分发** —— `huntiezz/cs2-assets` 仓库 `license` 是 `null`，
    素材版权属 Valve。个人本地用风险低，但**跟着 exe 发到 GitHub Releases 是侵权**。
    ~~现在的做法是照造型自己重画（`KillIcons.xaml`）~~ → **v8 起内置图标整体删掉了，全部由用户导入**。
    解析这类 SVG 时注意：**要跳过 `<symbol>` 里的 path**（那是模板定义，直接抓会把
    `dude-transit` 小人叠到每个图标上），也要跳过 `fill="none"` 的纯描边路径（那是设计稿辅助线）。
23. **禁用态的 `Foreground` 必须自己接管** —— 暗色主题下"总开关关掉后列表**文字**变成日间浅灰"，根因是
    WPF 对 `IsEnabled=False` 会套一个**系统默认禁用前景色**（跟主题无关的浅灰）。
    修法要**两条一起改**才完整：
    (a) 在 `ModernControls.xaml` 的隐式 `ListBoxItem` 样式里加
        `Trigger Property="IsEnabled" Value="False"` → `Foreground = TextTertiaryBrush`；
    (b) **删掉** `SoundChoiceItemTemplate` 里 TextBlock 上写死的 `Foreground="{DynamicResource TextPrimaryBrush}"` ——
        写死的前景色会盖过继承来的禁用色，不删的话 (a) 不生效。
    同类问题以后还会出现在任何"父级 `IsEnabled` 绑定"的地方，看到就顺手检查。
    ⚠️ **注意别和第 31 条搞混**：这条管的是**文字**，"列表背景发白"是另一回事（看 31）。
    我当初把背景白也归到这来，绕了一大圈才找对人。
24. **纯 `ResourceDictionary` 里不能挂事件处理器** —— `ModernControls.xaml` 没有 `x:Class`，
    在里面的 `DataTemplate` 写 `Checked="Xxx_Changed"` 会直接报 `MC6024`（"根元素需要 x:Class 特性"）。
    **正解：从数据侧接线** —— 让列表项模型实现 `INotifyPropertyChanged`，
    重建列表时订阅 `PropertyChanged`，用户点勾选框 → TwoWay 绑定写回 → 通知 → 同步进配置。
    两个注意点：闭包要用局部变量接住循环变量；退订的账本**每张卡各一本**
    （合成一本的话，重建 A 卡会把 B 卡的钩子也清掉，症状是"在 A 里改勾选 B 不响应"，很隐蔽）。
25. **`MediaPlayer` 没有 `Tag`** —— 习惯性写 `player.Tag = path` 会编译报 `CS1061`。
    要记"这个实例挂着哪个文件"，只能在外面开一本 `Dictionary<MediaPlayer, string>`。
26. **样式 `TargetType` 不匹配会被规则静默忽略** —— `SmallPrimaryButtonStyle` 的 `TargetType="Button"`，
    套到 `ToggleButton` 上既不报错也不生效（退回默认外观，看着完全不对）。
    镜像样式见 `SmallPrimaryToggleButtonStyle`。
    **给控件套样式前先确认 `TargetType`**，不然会有"样式写了但没效果"的鬼故事。
27. **XAML 属性值里不能嵌 ASCII 直引号** —— `Text="...是"停住让你看清"的时间"` 会让解析提前结束，
    报一堆看不懂的行号错。中文文案里的引号统一用「」。
28. **新增的可序列化集合字段必须防 null** —— `SettingsService.Load` 里对新 List 全部 `??= new()`。
    老配置里没这些字段，反序列化出来是 null，之后一 `.Add()` 就是空引用。
    加字段时顺手在 `Load` 里补一行，并考虑加一个迁移方法把老字段并过来
    （见 `MigrateLegacySoundFiles`：只搬"绝对路径且文件还在"的，相对路径的老内置音不要搬）。
29. **`INotifyPropertyChanged` 不要加在 `MainWindow` 的多个 partial 上** —— 会报"重复实现接口"。
    统一放在一个文件（现在是 `MainWindow.LockedCard.xaml.cs`），其他的 partial 只调它的方法。
30. **卡片级"锁定态"用 `Tag` 传布尔，不要用 `DataContext`** —— 多张卡共用同一条 `Style` 时，
    各自 `Tag="{Binding IsXxxCardLocked}"` 就行，不用复制三份 `DataTemplate`。
    也**不要整卡 `IsEnabled=False`**：总开关本身就在卡片里，禁用整卡会连开关一起点不动，用户永远打不开。
31. **【暗色下"列表背景发白"的真正根因】必须给 `ListBox` 写显式 `ControlTemplate`** ——
    WPF 默认（Aero2）的 `ListBox` 模板里，有一个**名叫 `Bd` 的内部 `Border`** 负责背涂，
    它取的是 `SystemColors.WindowBrush`，**跟应用的 `Background` 属性和主题都无关**。
    所以 `Background="Transparent"` 设了也没用 —— 那个 `Bd` 是模板内部的，属性够不着。
    系统是浅色时那块就是纯白，压在暗色卡片上就是一块"日间白"。
    **关键认知：这个白和 `IsEnabled` 完全没关系，开关开着也是白的**，
    只是关掉之后列表内容没了衬托，才显得特别扎眼——所以很容易误判成禁用态 bug。
    正解：在 `ModernControls.xaml` 的隐式 `ListBox` 样式里写一条独立的 `ControlTemplate`，
    内容就是"一个纯 `ScrollViewer` + `ItemsPresenter`，不画任何背景"，让卡片底色透上来。
    怎么确认根因（这套手段以后复用）：探针走**真实 `MainWindow`** 的视觉树，
    从目标往上/往下逐层打印每个 `Panel`/`Control`/`Border` 的 `Background`，
    看到 `Border#Bd bg=#FFFFFFFF` 就抓到现行了。
32. **普通复选方框（`CheckBox` 没有 `x:Key` 样式）在暗色下也是一块白** —— 同一个病：
    Aero2 默认 `CheckBox` 模板里那个方框是画死的白底 + 系统色描边。
    修法同理：给 `TargetType="CheckBox"` 写一条**隐式**样式（`ModernControls.xaml` 里那一条），
    自带 `ControlTemplate`（圆角方框 + `Path` 画的白色对勾）。
    注意两点：
    (a) 开关类的 `CheckBox` 用的是带 `x:Key` 的 `ToggleSwitchStyle` / `InlineToggleStyle`，
        **带 key 的不受影响**，所以改隐式样式不会波及开关；
    (b) 禁用态压暗要包一层外层 `Border` 改它的 `Opacity`，
        **别单独去改 `box` / `check` 的 `Opacity`** —— 那会和"勾选/未勾选"的触发器抢优先级
        （谁写在后面谁赢），症状是"未勾选时也显示一个淡淡的对勾"。
    顺手收益：全工程 9 个裸 `CheckBox`（击杀反馈那 8 个效果开关 + 替换规则页 1 个）一起修好了。
33. **`RenderTargetBitmap.Render(离屏元素)` 会得到全透明/全白，别当证据用** ——
    没被选中导航页里的面板根本没参与布局，`ActualWidth/Height` 读出来是 `0` 或者渲染空白。
    探针必须**先切页**（`nav.IsChecked = true`）再 `Drain()` 泵几轮 dispatcher，然后才抓。
    另外 `VisualBrush` 放大渲染小控件时，会叠一层透明合成，颜色看着和实际不一样 ——
    **不要靠肉眼看 PNG 定色，要读 `Template.FindName(...)` 拿到部件的真实属性值**（底色/描边/不透明度）。
    我这次就是先看 PNG 误判了一轮，改成读属性才对上。
34. **`bool` 绑不到 `Visibility`，要写转换器，而且转换器能放在独立 ResourceDictionary 里** ——
    先找了一圈以为工程里没有 `IValueConverter`（只搜到 `HexToBrushConverter`），
    自己写了个 `BoolToVisibilityConverter`（`Converters.cs`）。
    关键点：`ModernControls.xaml` 顶部本来就有 `xmlns:local="clr-namespace:MinecraftChatOverlay"`，
    所以直接 `<local:BoolToVisibilityConverter x:Key="..."/>` 就能用，**不需要 x:Class**。
    但这类"XAML 引用 C# 类型"的坑**编译过不代表运行期能解析**，
    必须开真窗口 `FindResource(...)` 试一次（我这次验过才敢发）。
35. **改「关掉随机用哪个」时，别忘了三条侧路** —— 主路径（放的时候抽签）好改，
    容易漏的是：(a) `ApplyXxxToNotifier` 里的 `SoundFile` 兜底值、
    (b) **预热**（关随机时只需要热选中那一个，不用热一整批）、
    (c) **状态行文案** —— 现在会带上选中项的名字，不然用户不知道到底在用哪个。
    还有：关随机时**不要**去清空勾选组合 —— 用户重新打开随机时得原样恢复，清了就得重勾一遍。
36. **「预览」和「真触发」必须是同一条抽签路径** —— 这次 gold_ 报「击杀提示音预览老是 test.wav」，
    根因：`PreviewKillSoundForEffect()` 直接读了**旧字段** `_settings.KillFeedback.SoundFile`。
    那个字段只在「开关变动 / 列表重建」时才刷新，**光在列表里改选中项根本不碰它**，
    所以关掉随机、换选另一个音之后，预览还在放老的那个。
    修法：预览改成走和 `NotifyKillSound` 完全同一套的 `RandomPicker.Pick(..., selected)`。
    教训：**任何"试听/预览"入口都要复用真实播放的选文件逻辑**，不能另抄一份。
    另外 `SoundFile` 现在只剩「播放器内部兜底」一个用途，任何地方不该再拿它决定播什么。
37. **自检别自证** —— 第一版探针是「把 `RandomPicker.Pick` 的表达式在探针里再算一遍」，
    这验的是"我以为它会怎么算"，不是"它实际放了什么"，等于没验。
    改成给 `ChatSoundNotifier` 加一个只读观测字段 `LastRequestedFile`（在 `Play` /
    `PlayOverlapping` 第一行写），探针**反射调真实私有方法** `PreviewKillSoundForEffect`
    再读这个字段 —— 验的是**播放器实际收到的路径**。
    写这类探针前先问自己一句：**我读到的这个值，是被测代码写进去的，还是我算出来的？**
38. **删掉一行里的某个控件后，要检查那行的容器宽度** —— 这次去掉「循环播放」开关，
    按钮行 `Grid` 还留着 `Width="392"`（当初按三个元素定的），
    结果只剩两个按钮却在右边留了 **218px 空洞**。
    **光编译过看不出来，得量**（探针读 `ActualWidth` / `TransformToAncestor` 算坐标）。
    修法：容器别写死宽，改 `HorizontalAlignment="Left"` 让内容自己撑。
39. **`Environment.GetEnvironmentVariable` 返回 `string?`** —— 直接 `string x = ...` 会报
    `CS8600 将 null 文本或可能的 null 值转换为不可为 null 类型`。
    写 `string? x = ...` 即可（后面的 `IsNullOrWhiteSpace` 判空逻辑本来就没问题）。
40. **`ShowActivated="False"` 不是"永不抢焦点"的保证** —— 它只在**窗口第一次显示**时可靠。
    **复用型窗口**（`Hide()` 之后下次再 `Show()`）重新显示时不保证不激活。
    真正的硬保证是 Win32 层的 **`WS_EX_NOACTIVATE (0x08000000)`**，
    在 `SourceInitialized` 里 `SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_NOACTIVATE)` 打上。
    要交互（比如拖动）时临时摘掉，结束了记得打回。
    **验证方式**：读 `GetWindowLong(hwnd, GWL_EXSTYLE)` 的实际位 + `GetForegroundWindow()` 前后对比，
    不要靠"看起来没抢"。
41. **`IsHitTestVisible="False"` ≠ 鼠标穿透** —— 它只管 **WPF 层**的元素命中测试，
    窗口在**系统眼里依然是"可点击的"**，鼠标移上去光标照样现形（全屏游戏里特别明显）。
    真正的穿透是 Win32 的 **`WS_EX_TRANSPARENT (0x00000020)`**，
    和 `WS_EX_NOACTIVATE` 一样在 `GWL_EXSTYLE` 里、用 `SetWindowLong` 设。
    对照：`OverlayWindow` 早就这么做了（`UpdateClickThrough`），`KillBannerWindow` 一直漏着。
42. **反复 `Hide()`/`Show()` 一个全屏 layered window，本身就是游戏扰动源** ——
    每次显示都要重排 z-order、重建 layered surface。
    **更好的做法：窗口常驻，只切内容**（`Collapsed` / `Opacity=0`）。
    这样窗口的 Win32 状态从头到尾不变，物理上不可能再扰动游戏。
    代价：得在退出路径显式 `Close()`，否则常驻窗口让进程退不干净。
    **验证方式**：连续多次触发，断言 HWND 不变 + `IsVisible` 始终 true。

  43. **刚「变可见」就调 `SetWindowPos`，会把窗口尺寸冻死在"还没布局"的 2px 宽** ——
      **现象**：悬浮窗启动后是一条**只长高、不长宽**的 2px 细线，发消息也不会变宽；
      只要触发一次重新布局（例如开关「启用后端日志」→ `SaveSettingsFromUi()` → `ApplySettings()`）就恢复正常。
      **原因**：`IsVisibleChanged` 里直接调 `EnsureTopmost()`（即 `SetWindowPos(HWND_TOPMOST, …, SWP_NOSIZE)`）。
      那一刻 WPF 还没跑第一次布局、尺寸还没算，窗口被按"空内容"提交成 2px；
      之后 `SizeToContent="Height"` 只更新高度，宽度再也回不来。
      （`OnSourceInitialized` 里那次同样的调用无害：那时 `IsVisible=false`，`EnsureTopmost` 直接 return。）
      **修法**：把那句丢到 `Dispatcher.BeginInvoke(DispatcherPriority.Loaded, …)`（布局/渲染之后）再执行；
      并在 `ApplySettings()` 里给 `Width` 配一个同值的 `MinWidth` 做硬兜底。
      **验证**：重启后**空**悬浮窗 = 540×27（坏时 2×27）；发消息后 540×67 且有文字；
      ex-style 仍有 `WS_EX_NOACTIVATE` + `WS_EX_TOPMOST`，窗口中心仍命中自己（压得住铺满屏的游戏）。
      **定位过程**：两版 `OverlayWindow.xaml.cs` 只差 215 行新增、0 行删改；整体换回旧版 → 恢复正常，
      再单独停用这一条订阅 → 也恢复正常。


---

## 5. 协作习惯（gold_）

- 先结论后推理；要"能不能做、代价是什么"；**不过度承诺**，宁可听坏消息
- **先分析、后动手**：方案摆出来等确认再改；但他明确让你做就直接做
- 他会自己实测，**每处改动都要能独立验证**，大功能拆小步
- 他会中途「插一嘴」改需求 / 改流程 —— **和功能要求一样重要**，照做
- 他授权过：编译被他的实例挡住时，**可以先关掉再编**（先说一声）
- 中文、结构化（表格 / 对比）、用「你」不用「您」、用「…」不用「...」
- 我犯过的错（引以为戒）：位置放错 3 次、方案理解错 1 次、测试预期写错 2 次、
  编辑把方法体删了 1 次 —— **每次改完都要读回来**

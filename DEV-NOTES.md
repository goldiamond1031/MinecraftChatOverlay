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

**⚠ 补记（2026-09-29）：上面的"NuGet 坏"只在我这边的自动化 shell 里成立，别拿它当结论。**

- 我这边：`dotnet build`（带隐式 restore）**必报**
  `NuGet.targets(782,5): error : Value cannot be null. (Parameter 'path1')`，
  补 `APPDATA` / `NUGET_PACKAGES` / `PROGRAMDATA` 都不管用；但**加 `--no-restore` 一切正常**（主工程、插件都能编）。
- gold_ 那边：双击 `build-plugins.bat` 是**能正常 restore 的** —— 证据是 14:01 那次三个插件成功装进
  `%APPDATA%\MinecraftChatOverlay\plugins\`，而 bat 里的 `dotnet build` 并没带 `--no-restore`。
- **推论**：判断"某个工程能不能引 NuGet 包"（例如给插件加 `PackageReference`），
  **不能拿我这边的失败当结论**，得让他双击 bat 或在自己的终端里试。
  QRCoder 1.6.0 在 `~/.nuget/packages/qrcoder/1.6.0/lib/net6.0-windows7.0/` 有缓存，
  实在不想引包的备选是 `HintPath` 直接引用 dll（不触发 restore）。

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


    44. **抓屏抓游戏画面：必须从"屏幕 DC"抓，且必须带 `CAPTUREBLT`** ——
        用窗口 DC（`GetDC(hwnd)`）抓 Minecraft 这类硬件加速（OpenGL/D3D）窗口，拿到的常常是**全黑**；
        要从 `GetDC(IntPtr.Zero)` + `BitBlt(SRCCOPY | CAPTUREBLT)` 抓"屏幕上真实显示的那块"。
        所以「区域放大」抓的是**屏幕画面**而不是窗口内容：目标必须真的显示在屏幕上
        （**独占全屏 F11 抓不到**，要窗口化 / 无边框全屏）。
        **顺带一个判据坑**：判断"是不是抓到黑屏"**不要用"颜色种数"** —— `颜色种数 > 4` 这种判据会把
        **纯色区域**（整块红底、纯色 UI、纯色格子）判成黑屏；表现是：抓屏其实成功了，窗口却盖着
        "抓到的是黑屏"的提示，用户看到一片黑。只按**非黑像素比例**（`> 2%`）判断就够了。
        （这就是「区域放大」第一版的 bug。）

    45. **给窗口设了 `WDA_EXCLUDEFROMCAPTURE` 之后，连自己的验证截图也看不到它** ——
        为了不让放大窗"自己拍自己"，显示窗会 `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`。
        副作用：**任何基于 `BitBlt` / `Graphics.CopyFromScreen` 的截图（包括我自己的验证脚本）都拍不到它**，
        看到的是它后面的桌面（一片黑），极易误判成"显示窗口是黑的"。
        **验证要改用 `PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT=2)`** —— 实测同一时刻
        `CopyFromScreen` 全黑、`PrintWindow` 中心像素是纯绿 `(0,254,0)`。
        另外：**未声明 DPI 感知的进程**（我这层 PowerShell 就是）去 `GetWindowRect` / `CopyFromScreen`，
        拿到的是**虚拟化坐标**；拿它当物理像素用会采到完全不相干的一块屏（这一坑连坑了我两次）。

    46. **按 100% 缩放写死的默认窗口位置，在 150%/200% 缩放下会整块落到屏幕外** ——
        `SystemParameters.WorkArea` 是 **DIP**：屏幕 2560px / 150% 时工作区只有 ~1707 DIP 宽，
        默认 `DisplayLeft = 1900` 直接超出右边界 —— 窗口"开出来了但看不见"，用户只会以为功能坏了。
        **修法**：显示前统一走 `ClampIntoScreen()` —— 整块不在工作区内就挪到右上角
        （`WorkArea.Right - 宽 - 16`）。**验证**：配置故意写 `(1900,40)`，启起来后实际落在
        `(1241,16)` 300×300，整块可见、内容仍正确。（`MoveToTopRight()` 用的是同一套 WorkArea 口径。）

    ---

    ### 区域放大（新功能，已实现并自测通过；**真机待验收**）

    需求：框选游戏画面的一小块（例：物品栏最后一格，约 20px），放大后钉在屏幕别处（例：右上角）。

    - 新增文件：
      - `Models/RegionMagnifierSettings.cs` —— 选区（相对客户区的**物理像素**）+ 显示窗（**DIP**）两套坐标
      - `Services/RegionMagnifier/RegionCaptureService.cs` —— `RegionWindowFinder`（按进程/标题找窗、
        客户区屏幕坐标）与 `RegionFrameGrabber`（屏幕 DC + CAPTUREBLT 抓帧 → `WriteableBitmap`）
      - `RegionMagnifierWindow.xaml(.cs)` —— 置顶无边框显示窗：不抢焦点（`WS_EX_NOACTIVATE`）、可拖、
        可点穿、**隐藏即停抓**（`IsVisibleChanged` → `PauseTimers`）、`ClampIntoScreen`、可选自排除抓屏
      - `RegionSelectorWindow.xaml(.cs)` —— 全屏框选层：`EvenOdd` 挖洞让选区保持原亮度、
        **光标旁 48px 放大镜（最近邻）**（专为对准小格子）、方向键微调（Shift=10px）、回车/双击确认、Esc 取消
      - `MainWindow.RegionMagnifier.xaml.cs` + `MainWindow.Modern.xaml` 的「区域放大」页（`NavRegionMagnifier`）
    - 已实测：纯绿方块 150×150 → 存成相对客户区 (100,100)/150×150、放大 3× → 显示窗 300×300 **内容纯绿**；
      框选层拖 (300,300)→(500,450) + 回车 → 存成 (100,100)/200×150 且页面状态同步刷新；
      显示/隐藏切换正常；`dotnet build -c Release` 0 警告 0 错误。
    - **真机待办**：还没在真 Minecraft 上跑过（当时游戏没开）。重点两点：
      ① 窗口化 / 无边框全屏下能否抓到画面（独占全屏必然抓不到）；
      ② 框"物品栏最后一格"时放大镜够不够用（不够就调 `RegionSelectorWindow.xaml.cs` 里的
      `LoupeSourceSize` / 放大镜边框尺寸）。
    - 显示设置改版：删掉了「窗宽/窗高」两个输入框 —— 它们从未参与计算（显示窗大小恒等于
      **选区大小 × 放大倍数 ÷ DPI 缩放**，见 `RegionMagnifierWindow.ApplySettings()`）；
      「放大倍数」「每秒帧数」改成**滑块**（1–8 倍、步进 0.5；5–60 fps、步进 5），拖动即生效、
      立刻存档（`RegionMagnifierSlider_Changed` → `RegionMagnifierOption_Changed`），数值实时显示在标签右边。
    47. **插件 dll 被运行时锁住 → 删不掉/覆盖不了，必须"排队到下次启动"** ——
        更新一个正在运行的插件时 `Directory.Delete` / `File.Copy` 会抛 `Access to the path ... denied`：
        托管程序集一旦被装载，句柄在 ALC `Unload()` 之后也不一定马上放（卸载是异步的，GC 也不保证）。
        **修法**：新版本先整包拷到 `<id>.new\`，下次启动 `ProcessPendingChanges()` 再换上去；
        卸载同理写进 `pending-delete.txt`，启动时清掉。**别跟锁硬碰**。
        **验证**：测试台 setup（装 → 覆盖更新 → 卸载，请求都进队列）→ restart 新进程扫目录 → 5 项全过。

    48. **插件配置目录必须和插件安装目录分开** ——
        换版是"整目录替换"（删旧 + 落新），配置若放在安装目录里，**用户设置会被换版吃掉**。
        所以 `IPluginHost.PluginDirectory` 指向 `%AppData%\MinecraftChatOverlay\plugin-data\<id>\`，
        安装目录只放 dll + plugin.json。**验证**：测试台的「换版后插件配置还在」一项。

    49. **主工程必须整目录排除 `Plugins\**`，否则 wpftmp 临时工程报 CS0579 特性重复** ——
        主工程 `**\*.cs` 通配会把插件源码、以及插件 `obj\` 里生成的 `AssemblyInfo.cs` 一起编译；
        `DefaultItemExcludes` 里的 `obj\**` 只管主工程根目录那一层，管不到 `Plugins\X\obj\`。
        **修法**：csproj 里 `Compile/Page/ApplicationDefinition/EmbeddedResource/None` 全部 `Remove="Plugins\**"`。

    50. **`UseWPF=true` 的工程，隐式 using 里没有 `System.IO`** ——
        `File` / `Path` / `Directory` 都不存在，编译报 CS0103。涉及 IO 的文件要显式 `using System.IO;`
        （本次给插件装载器 4 个文件都补了）。

    51. **命令中途抛异常会"半执行"，而且这台机器上 PowerShell 的 stderr 不回显** ——
        表现：工具只回一个 `[exit code: 1]`、没有任何输出，看起来像"命令太长被截断"，实际是脚本在中间抛了异常。
        本次 `Services\RegionMagnifier\RegionCaptureService.cs`（647 行）就是这么丢的：一次批量迁移命令中途报错，
        源文件已经没了、目标文件也没生成，全盘无备份。**教训**：一次只发小块命令（≲80 行）、
        关键步骤包 `try/catch` 并显式 `Write-Host` 错误、写完立刻读回来确认。
        **抢救办法**：调用方还在 → 契约就被钉死了：按调用方用到的成员签名
        （`RegionNative` / `RegionWindowFinder` / `RegionFrameGrabber`）重写了一份（733 行，注释更全），
        编译 + 测试台 + 真机三项都过了。

    52. **插件自绘页面里，样式/画刷一律用 `DynamicResource`，不能用 `StaticResource`** ——
        插件控件是"先 new 出来、再挂进宿主窗口"的（`ContentFactory()` 在加入可视树之前就被调用了），
        `StaticResource` 在解析那一刻沿自身→父级→Application 找资源，而宿主的主题字典挂在 **Window** 上，
        于是直接抛 `XamlParseException`（整个插件页面白掉）。`DynamicResource` 是延迟解析的，挂上树再找 ✓。
        区域放大插件没踩到，是因为它的窗口自己带样式；自动 GG 用的 `CardStyle`/`ToggleSwitchStyle` 全在宿主 Window 里。

    53. **插件页面"填控件"时不能顺手存盘，会把空值写回设置** ——
        自动 GG 迁移时实测踩到：`LoadFromSettings()` 里第一个 `IsChecked = true` 触发 `Checked → SaveToSettings()`，
        此时后面几个 TextBox 还是空的，于是把 `""`/`false` 存了回去 —— 用户原本的正则、按键、发送文字全被清掉。
        **修法**：加 `_loading` 闸门，填值期间 `SaveToSettings()` 直接 return（已写进 `AutoGgPage.xaml.cs`）。
        **凡是"控件值变化即保存"的页面都要这个闸门。**

    54. **`IsMouseOver` 是只读依赖属性，`RaiseEvent` 装不出来** ——
        想验「鼠标移上去会怎样」，伪造 `MouseEnter` 事件是没用的（`IsMouseOver` 由真实鼠标位置驱动）。
        必须真的移动光标：`SetCursorPos`。**注意坐标单位** ——
        `Visual.PointToScreen` 在这个进程里返回的是 **DIP（虚拟化）坐标**，
        而 `SetCursorPos` 要**物理像素**；150% 缩放下要再乘
        `PresentationSource.FromVisual(win).CompositionTarget.TransformToDevice` 的比例。
        稳妥写法：先按原值试一次，读 `IsMouseOver` 还是 false 就乘缩放再试一次（探针就是这么自适应的）。
        验完**把光标移回原位**，别把人家的鼠标留在窗口中间。

    55. **`TextBlock.Text` 在内容是用 `Inlines.Add(...)` 填的时候读不到（返回空字符串）** ——
        探针想"按卡片标题找卡片"时栽在这：插件卡片的标题是 `title.Inlines.Add(new Run(...))` 拼的，
        `TextBlock.Text` 读出来是空，于是永远匹配不上、卡片点不开，症状是"探针说页面里开关数 0"。
        **正解**：`new TextRange(block.ContentStart, block.ContentEnd).Text`（`System.Windows.Documents`）。
        要在代码里按文字找控件，一律用这个，别信 `.Text`。


    56. **`Compress-Archive` 在非交互的 PowerShell 调用里会静默失败** ——
        退出码 0、没有任何输出、zip 根本没生成。
        gold_ 双击 `build-plugins.bat` 是好的（14:01 那三个 zip 就是它打的），所以这只是**我这边的调用方式**不行。
        要在我这边打 zip，直接用 Python 的 `zipfile`（几行就完事）。

    57. **新建的插件工程在我这边编不了**（`NETSDK1004 找不到资产文件 project.assets.json`）——
        因为这台机器的 `dotnet restore` 是坏的（见第 3 节），新工程没有 assets 文件、还原又跑不起来。
        **绕法**：找任何一个已经能编的插件工程（比如 `Plugins\RegionMagnifier\obj\`），
        把 `project.assets.json` / `project.nuget.cache` / `<项目名>.csproj.nuget.dgspec.json` /
        `<项目名>.csproj.nuget.g.props` / `<项目名>.csproj.nuget.g.targets` 五个文件复制到新工程的 `obj\`，
        再把里面的**项目名和程序集名**全局替换掉即可（插件都只引契约程序集，结构完全一样）。
        用 `--no-restore` 编译就不会再去看那个坏掉的还原。

    58. **插件页的初始化必须挂 `_loading` 闸门，否则会把还没回填的设置抹掉** ——
        这是坑 53 的变种，但触发源更隐蔽：`InitializePlayerQueryUi()` 里给下拉框设
        `SelectedIndex = 0` → 触发 XAML 上绑的 `SelectionChanged` → 里面"值变了就存盘" →
        而此时 `LoadPlayerQueryUiFromSettings()` **还没跑**，API KEY / 玩家 ID 都还是空 → **空值被写回配置**。
        症状：用户存好的 API KEY 一打开插件页就没了。
        **正解**：构造函数里用 `_loading = true; try { 各种 Initialize; } finally { _loading = false; }` 把整个初始化包起来，
        **事件订阅放在这之后**。顺序是：`InitializeComponent` → 闸门内初始化 → 订阅事件。

    59. **删掉宿主的 `AppSettings` 字段之后，旧配置值会在下次保存时被抹掉** ——
        `SettingsService.Save` 是全量写盘，属性没了 → JSON 里那个键就没了。
        这次迁玩家查询时，宿主已经不带 `PlayerQueryApiKey` 了，程序一启动一保存，**用户的 API KEY 就从
        `settings.json` 里消失了**（靠 `settings.json.bak_*` 备份救回来）。
        **规矩**：任何"从宿主搬设置到插件"的迁移，**必须在删宿主字段之前把值读出来写进插件目录**；
        保险起见先把 `settings.json` 复制一份再动手。

    60. **探针要拿"插件页本身"，别拿 `PluginDetailContent.Children[0]`** ——
        那是宿主用来包页面的 `StackPanel`（`host.Content`），插件页在它**里面**。
        按类型名找才准：`FindAll<FrameworkElement>(detail).FirstOrDefault(x => x.GetType().Name == "PlayerQueryPage")`。
        拿错了的表现是：`GetField("PlayerQueryStatusText")` 全返回 null、打印出来一堆 -1，看着像功能坏了。



    61. **`GameWindowFullscreen.FindCandidates()` 会把本程序自己的窗口排除掉**（按 PID 比对，防误选）——
        所以验收「窗口全屏」时**不能拿自己 new 出来的测试窗口**：它属于宿主进程，压根不会出现在下拉列表里，
        症状是"怎么刷新都找不到刚开的那个窗口"。
        正解：拿**别的进程**当靶子 —— `Process.Start("notepad.exe")` 起个记事本，测完 Kill 掉自己起的那一个
        （别去动用户已经打开的那些）。

    62. **Win11 的记事本是打包应用，`Process.Start("notepad.exe")` 拿到的进程没有窗口句柄** ——
        `MainWindowHandle` 是 0、`MainWindowTitle` 是空，看着像"记事本根本没起来"。
        正解：按进程名重新找 —— `Process.GetProcessesByName("Notepad")`（注意是大写 N）。
        拿不到句柄就没法用 `GetWindowRect` 独立量窗口尺寸，"自检"会退化成"只看插件自己报的状态文字"，
        那就等于自证了（坑 37）。

    63. **插件跑在同一个进程里，所以它能改主程序的配色**（实测过，不用碰契约）——
        插件拿 `Application.Current.MainWindow.Resources["PrimaryBrush"] = new SolidColorBrush(...)`
        就能把整个界面换色：因为宿主把主题资源放在 **Window 层**，而 `PrimaryBrush` 全项目
        **41 处引用都是 `DynamicResource`、0 处静态**（当初为了支持浅色/暗色切换统一改的），
        所以改动会整片传导到已渲染的控件上（导航指示条、按钮、开关全跟着变）。
        **实测**：`#7C6CF0` → `#FF6B35` 后，插件卡片开关的实际填充色也变成 `#FF6B35`；调 `ApplyTheme(false)` 即还原。
        推论：想支持"主题/美化插件"，**只要约定一套稳定的键名**就够了，不需要开放更多内部；
        但**放任插件自己摸 `Resources` 是危险的**——静默失效、互相覆盖、能把界面改到不可用。
        要做就正式化：契约加一条白名单式的"主题覆盖"入口（宿主校验 + 可一键恢复）。

64. **悬浮窗里选中文字按 Ctrl+C 没反应** —— 悬浮窗带 `WS_EX_NOACTIVATE`（永不成为前台窗口，见坑 40），
    因此**永远拿不到键盘焦点**，Ctrl+C 压根送不到它身上（不是快捷键没绑）。修法：控件侧记 `LastWithSelection`，
    悬浮窗挂 `WH_KEYBOARD_LL` 钩子自己看 Ctrl+C（只在可见时挂、不吞键），另给"双击整条复制"这条不依赖键盘的路。
    细节见文末「坑 64」小节（含真机验证数据）。

65. **悬浮窗上"点击"会被 `DragMove()` 吃掉** —— 悬浮窗为了能按住拖动，在 `Window_MouseLeftButtonDown` 里调
    `DragMove()`；它会进入模态移动循环并把鼠标捕获走，于是子控件的"抬起"永远收不到 ——
    表现是"消息看着能点（悬停提示都有），点下去毫无反应"。修法见「快捷命令」小节：
    窗口层先判断"点的是不是可点击消息"，是就直接 return、不 DragMove。

## 5. 协作习惯（gold_）

- 先结论后推理；要"能不能做、代价是什么"；**不过度承诺**，宁可听坏消息
- **先分析、后动手**：方案摆出来等确认再改；但他明确让你做就直接做
- 他会自己实测，**每处改动都要能独立验证**，大功能拆小步
- 他会中途「插一嘴」改需求 / 改流程 —— **和功能要求一样重要**，照做
- 他授权过：编译被他的实例挡住时，**可以先关掉再编**（先说一声）
- 中文、结构化（表格 / 对比）、用「你」不用「您」、用「…」不用「...」
- 我犯过的错（引以为戒）：位置放错 3 次、方案理解错 1 次、测试预期写错 2 次、
  编辑把方法体删了 1 次 —— **每次改完都要读回来**

## 6. 插件平台（新：区域放大已从内置功能迁成插件）

需求来源：开源相对闭源的最大优势应该是"大众能自己做插件"—— 作者做好一个功能，
能打包发给别人，别人装上就能用，并且像内置功能一样出现在左侧导航栏。

- **契约**：`Plugins\Abstractions\`（`PluginApi.Version = 1`，故意不引 WPF）。
  插件**只引它，永不引主程序 exe** —— 这样主程序内部怎么改都不会把插件弄碎。
  页面用"宿主渲染"：插件交 `PluginPage` / `PluginField`[Toggle|Slider|Select|Text|ReadOnly] /
  `PluginAction` / `StatusText`，宿主用现有主题画，所以插件页和内置页长得一样、也不会因主程序改样式而白屏。
- **插件包** = 一个 zip（`plugin.json` + dll + 可选资源）。两个插件根：
  exe 旁 `plugins\`（内置/便携）与 `%APPDATA%\MinecraftChatOverlay\plugins\`（导入安装的）。
- **宿主侧代码**：`Services\Plugins\` —
  `PluginManifest`（宽容解析 plugin.json）、
  `PluginLoadContext`（每个插件一个可卸载 ALC；契约程序集**必须**回落到宿主那一份，否则 `IPlugin` 类型身份不一致）、
  `PluginEntry`、`HostWindowEnumerator`（给插件列游戏窗口）、
  `PluginHostContext`（`IPluginHost` 实现，每次调用都包 try/catch）、
  `PluginManager`（扫描/装载/隔离/消息总线/导入 zip/启用禁用/卸载/排队变更）。
- **接线（只在 MainWindow.xaml.cs 加了几行）**：`InitializePluginsUi()`；
  `MoveIndicatorToSelected` / `AnimateActivePanel` 各一行插件回退（`FindCheckedPluginNav` / `FindActivePluginPanel`）；
  `Watcher_ChatLineReceived` 里 `NotifyPluginsChatLine`；`Window_Closing` 里 `ShutdownPlugins()`。
  插件页动态插到「调试后台」前面，面板加在内置面板同一个容器里。
- **管理页**：`MainWindow.Plugins.xaml.cs` + `MainWindow.Modern.xaml` 的「插件」页（`NavPlugins`）：
  拖 zip 导入 / 选文件导入 / 打开插件目录 / 重新扫描 / 启用 / 禁用 / 卸载 / 打开单个插件目录；
  导入前弹确认框列出 `capabilities`（知情同意）；zip slip 路径校验、apiVersion 校验。
- **一键脚本**：`build-plugins.bat` —— 编契约 + 编所有插件 + 装到用户插件目录 + 打 zip 到
  `%APPDATA%\MinecraftChatOverlay\plugin-packages\`（可直接拖进「插件」页试装）。
  作者文档：`Plugins\README.md`；最小示例：`Plugins\SamplePlugin\`。
- **已迁移**：「区域放大」从内置功能整体搬进 `Plugins\RegionMagnifier\`
  （宿主里的页面 XAML、`MainWindow.RegionMagnifier.xaml.cs`、两个窗口、`Models\RegionMagnifierSettings.cs`、
  `Services\RegionMagnifier\`、`AppSettings.RegionMagnifier` 节点**全部删掉**）。
  老设置不丢：把 `settings.json` 里的 `RegionMagnifier` 节点搬进了
  `plugin-data\goldiamond.regionmagnifier\settings.json`（选区 66×64、倍数 7 都带过去了）。
- **验证**：
  ① 测试台（`%TEMP%\PluginHarness`，两阶段：装/覆盖更新/卸载排队 → 新进程处理排队）**19 项全过** ——
     含导入 zip、装载、页面注册、聊天消息投递、插件存配置、往悬浮窗发消息、契约版本拒装、zip-slip 拦截、
     换版后配置还在、待删名单清空；
  ② 真机：导航栏出现插件注册的「区域放大」「示例插件」两页（共 12 项）；
     页面控件齐（ComboBox 1 / Slider 2 / CheckBox 5 / Button 21 / Text 55）；
  ③ 点「显示 / 隐藏放大窗」→ 出现 `Minecraft 区域放大` 窗口 `462×448 @ 1843,24`
     （正好 = 迁移过来的选区 66×64 × 7 倍，且在右上角）；
  ④ 点「框选要放大的区域」→ 全屏选择层开出来，Esc 能取消；
  ⑤ 插件自己存盘写在 `plugin-data\goldiamond.regionmagnifier\settings.json`（`Enabled=true` + 位置已记）。
- **还没做（按需再加）**：插件图标/作者信息展示得更漂亮、包签名、插件市场/更新检查、
  给插件开放热键与叠加层等更多能力、插件页允许高级的自绘 UI（现在只支持表单式）。

### 自动 GG（已从内置功能迁成插件，2026 追加）

- 插件：`Plugins\AutoGg\`（`goldiamond.autogg`）—— `AutoGgPlugin.cs`（触发判断 + 设置）、
  `AutoGgPage.xaml(.cs)`（原来的 `AutoGgPanel` 整块搬过来，作为**自绘页面**）、
  `AutoGgKeySender.cs`（模拟按键 + 剪贴板粘贴 + 低级键盘钩子阻断真人按键）、`AutoGgSettings.cs`。
- 宿主里删掉了：`NavAutoGG` 导航项 + `AutoGgPanel`（89 行 XAML）、`TryAutoGg`/`SendAutoGgAsync`/键盘阻断 hook（约 190 行）、
  `AutoGgResetButton_Click`/`EnableAutoGgCheckBox_Changed`/`UpdateAutoGgVisibility`、以及 `AppSettings` 里的 5 个 `AutoGg*` 属性。
- 老设置已搬到 `plugin-data\goldiamond.autogg\settings.json`（Enable=True、正则 `恭喜! .+? 获得胜利!`、按键 t、文字 gg、剪贴板 true）。
- **验证**：导航项 9 个（自动 GG 不再占导航）；「插件」页 3 张卡片、点「进入」进自动 GG 自绘页，
  控件与取值全对（正则/按键/文字/两个开关 = On）；页面加载后设置**没有被写坏**（修了坑 53）；
  往 `latest.log` 追加 `[14:05:00] [Render thread/INFO]: [CHAT] 恭喜! Steve 获得胜利!` → 5 秒内触发，
  `LastTriggerAt` 写入 `2026-09-29T14:03:12`，页面显示「上次触发：14:03:12」、宿主状态行「启用中 · 上次触发 14:03:12」✓。
- 新增契约能力：`PluginPage.ContentFactory`（插件交一个 WPF 控件，宿主插进页面；契约仍不引 WPF，用 `object` 交接 + 类型检查 + try/catch）。
- 剩下三个（玩家查询 ~1300 行、击杀反馈 ~3500 行、B站弹幕 ~4700 行）待迁，顺序和注意点见第 6 节。

### 玩家查询（已从内置功能迁成插件，2026-09-29）

- 新插件 `Plugins\PlayerQuery\`（`goldiamond.playerquery`）：
  `PlayerQueryPlugin.cs`（入口）、`PlayerQuerySettings.cs`（6 个设置字段）、
  `PlayerQueryTexts.cs`（从宿主 `Copy.cs` 搬来的 26 条文案）、
  `PlayerQueryPage.xaml(.cs)`（**自绘页面**，1673 行 —— 拖拽排序、字段下拉、按需生成的结果卡片，表单式表达不了），
  加上原样搬来的 `PlayerQueryField.cs` / `PlayerQueryFieldCatalog.cs` / `BuJiDaoQueryService.cs`（只改了命名空间）。
- 宿主里删掉的：`MainWindow.xaml.cs` **1660 行**（字段声明 31-55、`PlayerStatLabels` 83-105、主体 313-1912、
  6 处零散调用与导航分支）、`MainWindow.Modern.xaml` **228 行**（`NavPlayerQuery` + `PlayerQueryPanel`）、
  `AppSettings` 的 6 个 `PlayerQuery*` 字段，以及 `Models\PlayerQueryField.cs`、
  `Services\PlayerQueryFieldCatalog.cs`、`Services\BuJiDaoQueryService.cs` 三个文件。
- **搬运后必改的 5 类**（下次迁别的模块照这个清单走，能省一大圈）：
  1. `Copy.Xxx` → 插件内的文案常量类；
  2. `_settings.PlayerQueryXxx` → `_settings.Xxx`（插件设置去掉了前缀）；
  3. 宿主能力替换：`ShowToast(` → `_plugin.Host.ShowToast(`、`LogStatus(` → `_plugin.Host.Log(`、
     `SettingsService.Save(_settings)` → `_plugin.SaveSettings()`；
  4. XAML 三处：`{StaticResource}` → `{DynamicResource}`（坑 52）、`AncestorType=Window` → `AncestorType=UserControl`、
     **去掉自带的 `ScrollViewer` 外壳和页头那块**（标题/说明由宿主统一画）；
  5. 宿主专有的小工具（`Motion.Soft()` 缓动、`PlayerStatLabels` 字典）内联或搬过去。
- **怎么快速确认"这块能不能整块搬"**：先列一遍区间内的方法签名
  （`awk 'NR>=A && NR<=B && /^    (private|public|internal|protected|static)/'`）——
  玩家查询正好是**连续一整块**（313→1912 无其它功能夹杂），所以能整个切走；
  再 grep 一遍这块引用的宿主成员（`_settings` / `ShowToast` / `Copy.` / 别的下划线字段），
  依赖清单就出来了，改起来心里有数。
- 验收（探针 `MCO_PQPROBE`，已删）：插件卡片出现、点开正常；控件 ComboBox 14 / TextBox 27 / PasswordBox 1 / Button 17；
  字段 12 行；回填 API KEY（336 字符）· 玩家 ID `JustReach` · 游戏类型「起床战争」；
  **真跑了一次查询**，12 项数据全部渲染（总胜场 2273、总胜率 38.8%、当前模式胜率 59.6%、总最终击杀 11248…）。
- 老配置迁移：`settings.json` 的 `PlayerQueryApiKey` / `PlayerQueryPlayerId` / `PlayerQueryGameType` /
  `PlayerQueryMode` / `PlayerQueryFields` → `plugin-data\goldiamond.playerquery\settings.json`（字段名去掉前缀）。
  ⚠ **这一步必须在删宿主字段之前做完** —— 否则宿主下次保存配置时那些键就没了（见坑 59，这次真丢了 API KEY，
  靠 `settings.json.bak_*` 备份救回来的）。
- 宿主迁移前的原文件备份放在 `C:\Users\gold_\_pq_migration_backup\`（`MainWindow.xaml.cs` / `MainWindow.Modern.xaml` /
  `AppSettings.cs`），确认没问题后可以删。

### 窗口全屏（已从内置功能迁成插件，2026-09-29）

- 新插件 `Plugins\WindowFullscreen\`（`goldiamond.windowfullscreen`）：
  `WindowFullscreenPlugin.cs`、`WindowFullscreenSettings.cs`、`WindowFullscreenPage.xaml(.cs)`（234 行 + 85 行 XAML）、
  `GameWindowFullscreen.cs`（669 行，原样搬来只改命名空间 —— 它本来就零宿主依赖）。
- 宿主删掉的：`MainWindow.WindowFullscreen.xaml.cs`、`Services\BorderlessWindow\`（整个目录）、
  `Models\WindowFullscreenSettings.cs`，`MainWindow.xaml.cs` 5 行（初始化调用 + 2 处导航 + 退出还原）、
  `MainWindow.Modern.xaml` 94 行（`NavWindowFullscreen` + `WindowFullscreenPanel`）、`AppSettings` 的 `WindowFullscreen` 节点。
- **退出还原挪进了插件的 `Shutdown()`**（原来挂在宿主 `Window_Closing`）——
  这样"插件被禁用 / 被卸载 / 程序退出"三种情况都能兜住，不会留下"游戏窗口还是改过的样子、而插件已经不管了"的状态。
- **契约加了一个方法**（这是唯一一处契约改动）：`IPluginHost.ReassertOverlayTopmost()` ——
  原来页面改完窗口会调 `_overlay?.ReassertTopmost()` 把悬浮窗再抬一次，插件拿不到那个窗口。
  加方法**不影响已发布的插件**（老插件不调用它，`PluginApi.Version` 不用升）。
  实现链路：`PluginHostContext` → `PluginManager.PluginOverlayTopmostRequested` 事件 → `MainWindow` 订阅后 `_overlay?.ReassertTopmost()`。
- 验收（探针 `MCO_WFPROBE`，已删）：卡片出现、页面打开、两个开关回填成 True、刷新列表 13 项 ✓
  **真拿记事本试了一遍**，用 Win32 `GetWindowRect` **独立量**（不靠插件自报）：
  `1130×1029 @ (57,57)` → 点【变无边框全屏】→ **`2560×1600 @ (0,0)`**（正好屏幕尺寸）→ 点【还原窗口】→ `1130×1029 @ (57,57)` ✓
- 老配置迁移：宿主 `settings.json` 的 `WindowFullscreen` 节点（4 个字段，字段名没变）→
  `plugin-data\goldiamond.windowfullscreen\settings.json`。
- 迁移前原文件备份在 `C:\Users\gold_\_wf_migration_backup\`。

### 评估：插件能不能"全方位美化"界面（2026-09-29 实测 + 结论）

gold_ 问「插件能不能对软件做全方位美化，比如换背景图、加动效」。**结论：外观层能换，结构层不能换。**

**界面实际是两层构成的：**
- **外观层**（颜色 / 画刷 / 字体 / 圆角 / 阴影 / 动效）—— 主程序**已经全部是 `{DynamicResource XxxBrush}` / `{DynamicResource XxxStyle}`**，
  插件改 `MainWindow.Resources` 里的键就能整片生效。**实测过两次**：
  ① 改 `PrimaryBrush` → 导航指示条 / 按钮 / 开关全变橙；
  ② 把 `WindowBgBrush` / `ContentBgBrush` / `SidebarBgBrush` / `CardBgBrush` / `SurfaceBrush` 换成 **`ImageBrush`** →
  整个内容区变成一张深蓝渐变星点图、侧边栏变半透明、卡片半透明透出底图（`ContentBgBrush` 从固体色 `#FFFFFF` 变成 ImageBrush，还原后回白）。
  **连动效都能换** —— 卡片的悬停/入场动画都写在 `CardStyle`、按钮/开关写在各自 Style 里，**换 Style 资源 = 换动效**。
- **视觉树层**（往界面里塞控件、给宿主控件加动效、改布局属性）—— 我一开始判断"插件拿不到视觉树"，**判断错了**。
  插件和宿主同进程，`Application.Current.MainWindow` 拿得到、`VisualTreeHelper` 走得动、`FindName` 按名字就能拿宿主控件。
  **第三个探针实测**（临时插件 `goldiamond.beautifyprobe`，验收完已删）：
  ① 走整棵视觉树：**585 个元素、34 种类型**全可达（Border ×120、TextBlock ×102、Grid ×87…）；
  ② `FindName` 直接拿到 `MainContentGrid` / `SidebarBorder` / `NavIndicator` / `NavPlugins` / `PluginsListPanel`；
  ③ 给 `MainContentGrid` 起 `DoubleAnimation`（Opacity 1.0→0.35 / 6 秒）→ 截图里**整个内容区真的淡到 35%、侧边栏和标题栏不动**；
  ④ `MainContentGrid.Children.Add` 塞进去一个自绘圆形装饰层（渐变 + 360° 无限旋转）→ 截图里看得到，而且和内容一起被淡化了。
  **全程只用了契约里的 `Log`，一行新契约都没加。** 三行代码就能把界面玩半透明 —— 这既是能力也是风险。

**所以真正的分界不是"能做多少"，而是"有没有约定"：**
- 走**资源层**（DynamicResource 覆盖）→ 键名可以当契约、可以白名单、可以做"恢复默认"，是支持美化插件的正确姿势
- 走**视觉树层** → 什么都能干（上面实测），但属于**裸摸内部**：宿主一重构控件名就静默失效、
  多个美化插件互相盖、没有一键还原、动画失控没人管。能做 ≠ 该让插件这么做。
- 整个外壳重画（ControlTemplate 级）→ 技术上也是换资源 / 换模板就能到，但牵动所有内置页面，风险最大

**建议**：真要支持美化插件，宿主做两件小事（都和加 `ReassertOverlayTopmost()` 一个量级，老插件无感）：
① **资源键白名单**公开成文档（哪些键允许覆盖），契约加 `ApplyThemeOverrides(IReadOnlyDictionary<string,string>)`，
   宿主校验颜色后应用、提供一键还原；
② 在 XAML 里留几个固定名字的**空装饰层**（背景层 + 前景层），契约加 `object? GetHostLayer(string)`，
   插件往层里塞粒子 / 光效 / 扫描线 / 水印，不用摸视觉树。

---

### 实测：插件能不能做「菜单式功能悬浮窗」（2026-09-29，探针已删）

gold_ 问「假如有人要用插件做一个菜单式的功能性悬浮窗，能做到吗」。**能做到，宿主一行代码都不用改。**
临时探针插件 `goldiamond.menuoverlay` 全流程跑通后已删（源码、工程、装机目录都清了）。

实测数据（都是 Win32 从窗口上读回来的真实值，不是插件自报）：
- 窗口本体：444×285 @ (210,210)，有属主（WPF 见 `ShowInTaskbar=false` 自动认的，属主活着菜单就必然压在它上面）
- 样式：`WS_EX_TOPMOST` ✓ / `WS_EX_NOACTIVATE` ✓ / `WS_EX_TOOLWINDOW` ✓（不进任务栏和 Alt-Tab）/ `WS_EX_TRANSPARENT` = False
- 焦点：开窗前后台 = 宿主主窗口 → 开完窗**没变** → 真点一下按钮**还是没变**，`IsActive` 全程 False
- 点击：`SetCursorPos` + `mouse_event` 真点了一下「传送回城」按钮 → `ClickCount = 1`，状态行变成"已响应 1 次"

**两个必须记的坑（都会让"菜单悬浮窗"变成"点一下游戏就暂停"）：**

1. **`WS_EX_NOACTIVATE` 补得太晚没用**：WPF `Show()` 那一下就已经激活窗口了，`Loaded` 里才补样式拦不住。
   要 `ShowActivated = false`，并且在 `SourceInitialized`（HWND 建好、还没显示）就摆样式。
2. **光有 `WS_EX_NOACTIVATE` 也挡不住"点击激活"**：实测点了按钮前台还是切到菜单窗。
   必须 `HwndSource.AddHook` 拦 `WM_MOUSEACTIVATE`（0x0021）返回 `MA_NOACTIVATE`(3)——
   拦完之后点击照样送达按钮，但前台窗口不动。**三件套缺一不可**：
   `ShowActivated=false` + `WS_EX_NOACTIVATE` + 拦 `WM_MOUSEACTIVATE`。

其它：点击穿透用 `WS_EX_TRANSPARENT` 开关（菜单开着必须关、收起后打开就能把点击还给游戏）；
唤起/收起菜单可以用低级键盘钩子（`AutoGgKeySender` 那套 `WH_KEYBOARD_LL` 已经跑通）。
已有的「区域放大」插件其实已经把大半套用上了，只是没人把"能点 + 不抢焦点"这条链完整验过。

---

### 实测：插件能不能做「网易云/QQ音乐歌词悬浮窗」（2026-09-29，探针已删）

gold_ 问歌词悬浮窗。难点不在悬浮窗（前面已验过）而在**数据来源**。探针 `goldiamond.lyricprobe` 跑通后已删。

**先摸清的事实：**
- 网易云装在 `D:\CloudMusic`（不在 Program Files），测试时没开；QQ音乐装没装没确认。
- 本机**没有 WinRT 投影包**（`dotnet packs` 里没有 `Microsoft.Windows.SDK.NET.Ref`），NuGet 又是坏的
  → **SMTC（Windows 全局媒体会话，最正统的路线）暂时用不了**。
- `UIAutomationClient` / `UIAutomationTypes` 是 WindowsDesktop 框架自带的，UseWPF 工程**不用写 `<Reference>`**
  （写了反而 MSB3243/MSB3245 和框架引用打架）。

**实测（UI Automation 跨进程读）：**
- 用记事本打开 `lyric-target.txt` 当靶子（本机没开播放器），插件从**另一个进程**找到窗口（42 个控件）并读到全文；
- 用 `RangeValuePattern` 读到了宿主 `OpacitySlider` 的当前值和范围 —— 换成播放器进度条就是「播到第几秒」；
- 坑：**新版记事本的 UIA 文本不保换行**（TextPattern.GetText / ValuePattern 读出来两行挤成一行）；
  真做歌词时按「每行歌词一个控件、逐个取 Name」读，或者干脆走歌词接口拿 .lrc 自己按时间轴滚；
- 坑：`System.Windows.Automation.Condition` 和 `System.Windows.Condition` 同名，要写全名。

**歌词覆盖层本身（比菜单窗还简单）：** 置顶 + `NOACTIVATE` + `TOOLWINDOW` + **`TRANSPARENT`（点击穿透）**，
弹出后前台窗口没变 —— 纯显示的哑巴窗，鼠标全还给游戏。

**结论：能做，零契约。** 三条数据路线：

| 路线 | 能拿到什么 | 状态 |
|---|---|---|
| 窗口标题（GetWindowText） | 歌名 - 歌手（网易云/QQ音乐的标题就是这个格式） | 稳，但**没有播放进度** |
| UIAutomation | 控件树里的歌名 / 进度条值 / 甚至客户端自己的歌词文本 | 技术上通，但**这俩是 DirectUI 自绘系，暴露多少必须装了实测** |
| SMTC + 歌词接口(.lrc) | 歌名 / 歌手 / 进度 + 变化事件（逐行同步的正规军） | 本机缺 WinRT 投影包，要先解决依赖 |

**教训（自己引入的事故）：** 收尾时按进程 Kill 记事本，而**新版记事本所有窗口共用一个进程** ——
桌面上用户自己开着的「无标题 - Notepad」也被一起关了。以后关「我拉起的进程」要么对窗口句柄发 `WM_CLOSE`，
要么记下 PID 并先确认那个进程里只有自己的窗口。

---

### 实测：拿真网易云验「歌词悬浮窗」的数据来源（2026-09-29，工具已删）

gold_ 开了网易云放歌，用临时工具 `Plugins/UiaProbe`（跑完已删）把真客户端摸了一遍。
**结论：歌名歌手稳、悬浮窗显示稳、进度的正路缺一块依赖、界面上抠不到。**

**① 窗口标题 = 歌名 - 歌手（✅ 稳，两次采样都成）**
主窗口 `OrpheusBrowserHost` 的标题就是「歌名 - 歌手」，换歌会更新：
`"Miracle - A Day to Remember"` → `"When the World Ends - VALORANT/Raiden/jeonghyeon"`。
它还额外开了一个 `class=icon` 的隐藏窗，标题同步 —— 冗余来源，两条都读得到。

**② 网易云的窗口构成（实测枚举）**

| class | 说明 |
|---|---|
| `OrpheusBrowserHost` | 主窗口（CEF 系，内含 `Chrome Legacy Window`） |
| `icon` | 隐藏小窗，标题跟着歌曲走 |
| `DesktopLyrics` | **客户端自己的桌面歌词窗**（默认隐藏，Win32 标题恒为"桌面歌词"） |
| `MiniPlayer` | 迷你播放器（默认隐藏） |
| `MediaPlayer SMTC window - {GUID}` | **说明它确实在往 SMTC 发布播放信息** |

**③ UI Automation 读不到网易云内部（❌）**
主窗口后代只有 6 个元素，有名字的只有 `Pane "网易云音乐"` 和 `Document "Chrome Legacy Window"`；
**等 2 秒让 CEF 打开辅助功能树后再挖，Document 后代仍是 0**（CEF 需要 `--force-renderer-accessibility`，我们改不了）。
整棵树没有任何支持 `RangeValue` 的控件 → **读不到进度条**。`DesktopLyrics` / `MiniPlayer` 的 UIA 后代也都是 0。

**④ 播放进度（⚠️ 正路是 SMTC，本机缺依赖）**
`dotnet/packs` 里**没有 `Microsoft.Windows.SDK.NET.Ref`**，NuGet 又是坏的 → .NET 8 工程调不到 WinRT。
修好 restore 后把插件 TFM 改成 `net8.0-windows10.0.19041.0`，即可用
`GlobalSystemMediaTransportControlsSessionManager` 拿 歌名/歌手/**Position/EndTime**/播放状态，还有变化事件。
（试探记录：PowerShell **能解析** WinRT 类型，但 5.1 取不出 `IAsyncOperation` 的结果——要
`System.Runtime.WindowsRuntime` 的 `AsTask`，而本会话沙箱把 `Add-Type` 和 `csc.exe` 全禁了。
**这些限制只影响我的探针，不影响正式插件和 gold_ 自己的编译。**）

**⑤ 歌词文本（⚠️ 要联网）** 本机没有 `.lrc` 缓存（`D:\CloudMusic` 只是安装目录 + 几首 mp3），
整首歌词得走歌词接口，或者读用户下载目录里同名的 lrc。

**待验的一条**：让 gold_ 打开网易云的「桌面歌词」，再看 `DesktopLyrics` 窗能不能读到当前歌词行 ——
能读的话连进度都不需要，直接镜像客户端自己的同步结果，那是最省事也最准的做法。

---

## 7. 插件页卡片改版（2026-09-29 下午）

需求（gold_）：插件卡片改成「名字 / 版本号 / 作者 / 简介 + 右侧开关」的紧凑卡片、**两列网格**；
「添加插件」「打开插件目录」挪到**页头右侧**；**点卡片直接进插件页面**；卡片上不要图标、不要底部按钮排。

### 改了什么

**`MainWindow.Modern.xaml`（`PluginsPanel`）**
- 页头改成和其它页同构的 `Grid`（左：`PanelTitleStyle` + `PanelDescStyle`；右：两个按钮）—— 抄「悬浮窗显示」页那一套。
- 删掉原「1. 安装插件」卡片（按钮搬去页头），也删掉「2. 已装的插件」外层大卡片 —— 列表直接就是卡片网格。
- `PluginsListPanel`：`StackPanel` → **`UniformGrid Columns="2"`**（`RefreshPluginsManagerList` 里的 `.Children.Clear/Add` 不用改）。
- 「3. 想自己写一个？」保留，标题去掉编号；「重新扫描」降级成列表下方的 `SmallGhostButtonStyle` 小按钮。

**`MainWindow.Plugins.xaml.cs`**（`BuildPluginRow` 由 147 行重写为 190 行）
- 卡片 = `Grid` 两列两行：标题行（名字 `Bold 14.5` + `"  v1.0.0 · 作者"` 次要色）
  + 简介（12 号、次要色、`LineHeight=18` + `MaxHeight=36` = 最多两行）；右上角一个 `CheckBox`。
- 开关用 `InlineToggleStyle`，**不给 `Content`** 就只剩一个 34×20 的小开关，正好塞进标题行右侧。
- 开关 = 启用/禁用（`TogglePluginEnabled` → `PluginManager.SetEnabled`）；
  装载失败时在简介下面加一行 `AmberBrush` 的失败原因（否则用户不知道开关为什么没用）。
- **新增 `BuildPluginCardMenu` / `UninstallPlugin`：卸载和「打开这个插件的目录」收进右键菜单** ——
  卡片上放不下了，但功能不能丢（这是我和 gold_ 约定的折中，他没要求，但删功能不行）。
- **新增 `IsInsideToggle`**：点开关不该顺手把插件页面也打开（`MouseLeftButtonUp` 会从开关冒泡到卡片）。

### 补（同日）：插件卡片加悬停反馈

gold_ 反馈「插件小卡片没有交互反馈，要和卡片页面一样」。做法是**新加一条样式而不是在卡片上写死**：

- `ModernControls.xaml` 新增 `PluginCardStyle`（`TargetType="Border"`），**悬停语言和 `CardStyle` 逐字一致**：
  `Effect` 从 `CardShadow`(Blur 14/Depth 3) → `CardHoverShadow`(24/6)、
  `BorderBrush` → `BorderStrongBrush`、`RenderTransform.TranslateTransform.Y` 0 → -2
  （进入 200ms / 退出 240ms，都是 `QuinticEase EaseOut`）。
  只把圆角(12)、内边距(16,14)、外边距(0,0,14,14)调小一号，好并排摆两列。
- `MainWindow.Plugins.xaml.cs` 的 `BuildPluginRow` 里那些散写的外观属性全部删掉，改成
  `border.SetResourceReference(FrameworkElement.StyleProperty, "PluginCardStyle")` ——
  以后调悬停手感只动一个地方，大卡片和插件卡片不会各自漂移。

实测（把鼠标真的移上去，读 `Effect` / `BorderBrush` / `TranslateTransform.Y` 的真实值）：

```
【悬停前】IsMouseOver=False · 阴影Blur=14/Depth=3 · 描边=#FFE7E7EC · 上浮Y=0
【悬停后】IsMouseOver=True  · 阴影Blur=24/Depth=6 · 描边=#FFD4D4DB · 上浮Y=-2
【移开后】IsMouseOver=False · 阴影Blur=14/Depth=3 · 描边=#FFE7E7EC · 上浮Y=0
```

**踩到的坑（第 4 节新加的第 54 条）**：`IsMouseOver` 是**只读依赖属性，`RaiseEvent` 装不出来** ——
想验悬停必须真的把鼠标移过去。而 `Visual.PointToScreen` 在这个进程里返回的是 **DIP（虚拟化）坐标**，
`SetCursorPos` 要的是**物理像素**：150% 缩放下必须再乘
`PresentationSource.FromVisual(win).CompositionTarget.TransformToDevice` 的比例才对得上。
（探针里就是这么自适应试两次的：先按原值试，`IsMouseOver` 还是 false 就乘缩放再试。）

### 补（同日·第二件事）：自动 GG 页删状态行 + 新增「/again」开关

gold_ 的两条要求：① 页头下面那行「启用中 · 上次触发 …」太突兀，删掉；
② 在「使用剪贴板粘贴方式发送」下面加一个开关 —— 发完 gg 后自动再发 `/again`。

- **顺带**：`PluginsRootText` 那行目录提示改成**只报用户插件目录**（原来还报"内置插件目录：exe 旁 plugins\（随程序发布）"，
  但那个目录从来没存在过、纯属噪音）。
  ⚠ **内置插件根在代码里保留着**（`PluginManager` 的 `portableRoot` 参数 + `IPluginHost.PluginsRootDirectory`），
  只是不展示了 —— 那个成员是**契约的一部分**，删它就得升 `PluginApi.Version`、把已装的插件全拒装，不划算。
  真要内置插件（比如以后把某个插件做成"装上就有"），还得在 csproj 里加把插件产物拷到输出目录 `plugins\` 的规则。
- **宿主侧**（`MainWindow.Plugins.xaml.cs`）：`PluginPage.StatusText` 为 null 时**整行不建**
  （以前无论如何都会建一个空 `TextBlock` 占位，留一段空白）。`PluginApi.cs` 的注释同步说明 ——
  **只是注释，接口签名没动，`PluginApi.Version` 不用升**。
- **插件侧**（`Plugins/AutoGg/`）：
  - `AutoGgPlugin`：去掉 `StatusText = BuildStatus` 和 `BuildStatus()`；
    页面底部那行「上次触发：…」照旧（走 `AutoGgLastTriggerText`，没丢信息）。
  - `AutoGgSettings`：新增 `SendAgainAfterGg`（**默认 false**）。
  - `AutoGgKeySender`：把「开聊天栏 → 输入 → 回车」抽成 `TypeAndSendAsync`；
    主文字发完隔 **350ms** 再发一次 `/again`。**两次都在同一次键盘阻断里**（中途不放开，
    否则玩家按着的键会趁机混进聊天栏）；剪贴板写不进去时逐字敲兜底。
  - `AutoGgPage`：新开关（`ToggleSwitchStyle`，和上面那张卡片同一款）+ 加载/保存/恢复默认三处接线。

验证（探针 `MCO_AUTOGG_SHOT`，**已删干净**）：
- 页头文字序列 = 「自动 GG」→ 说明 → 「启用自动 GG」…，**状态行确实没了**；
- 页面里 3 个开关，新开关位置就在「使用剪贴板粘贴方式发送」下面；
- 存盘链路：初始 `false` → 文件里还没这字段（老配置照常读）；切成 true → `"SendAgainAfterGg": true`；
  切回 false → `false`（**验完已还原，没动用户配置**）；
- 主工程 + 插件均 **0 警告 0 错误**；启动自检 `exit=124`；插件 dll + plugin.json 已装进
  `%APPDATA%\...\plugins\goldiamond.autogg\`，zip 也重打过（用 Python，见坑 56）。

### 验证（真机截图 + 交互探针，全部通过）

`App.xaml.cs` 里临时加过 `MCO_PLUGINSHOT=<目录>` 触发的探针（**已删干净，`grep MCO_PLUGINSHOT` 零命中**）：

```
窗口 1200x820 · 根元素 1200x820
列表容器 UniformGrid · 卡片数 3
第一张卡片 Border · 高度 93
卡片里的开关 已找到 · IsChecked=True
IsInsideToggle(开关) = True（期望 True）
IsInsideToggle(卡片) = False（期望 False）
点击后：详情视图=Visible（期望 Visible） · 列表视图=Collapsed（期望 Collapsed）
```

编译 **0 警告 0 错误**；清理探针后无环境变量启动自检 `exit=124`。

### 顺手记下两条验收技巧（下次改 UI 直接抄）

1. **对「已经显示在屏幕上」的窗口元素做 `RenderTargetBitmap` 是有效的** —— 和第 33 条不冲突：
   那条失效的场景是「没被选中导航页、从未参与布局的离屏元素」。切页 → `Dispatcher.Yield(ContextIdle)`
   泵 8~10 轮 → 截根元素，拿到的就是真实排版（这次就是这么截出插件页的）。
2. **验「点这里会怎样」不用真鼠标**：`element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,
   Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent })`
   就能触发挂在它上面的处理器，`OriginalSource` 就是那个元素 —— 正好用来验 `IsInsideToggle`
   这种「该不该响应」的分支，而且没有副作用（不会真的把插件禁用掉）。
3. **要截「整个屏幕」（连桌面、别的窗口一起）时**：`RenderTargetBitmap` 只能渲自己的视觉树，
   截不到桌面。**本机 PowerShell 的 `Add-Type` 被安全策略拦了**（`System.Drawing` 加载不了），
   所以走的是「探针导航 + Python 截屏」这条路：
   - 探针只做导航，用**一个通用环境变量 `MCO_OPEN_PAGE`** 指定去哪：
     - `MCO_OPEN_PAGE=NavMotionBlur` → 点某个内置导航页（写 `x:Name` 字段名，`NavPlugins` / `NavKillFeed` / … 都行）
     - `MCO_OPEN_PAGE=plugin:自动 GG` → 进插件板块，按卡片上的标题文字找到那个插件卡片再点开
     之后 `win.Activate()` 把窗口置前，**然后故意不退出**（程序保持运行，供外部截图）。
   - 截屏脚本留在 `%TEMP%\mco_grab_screen.py`（纯标准库，可直接复用；被清了就照下面重写）。
   - Python 用纯标准库 + `ctypes`：`SetProcessDpiAwareness(2)` → `GetDC(0)` →
     `CreateCompatibleDC` / `CreateCompatibleBitmap` → `BitBlt(SRCCOPY | CAPTUREBLT)` →
     `GetDIBits` 拿 BGRA → `del data[3::4]` 去 alpha、`data[0::3], data[2::3] = data[2::3], data[0::3]` 换 R/B
     （都是 C 级切片，2560×1600 一两秒就完）→ 手写 PNG 写盘。
   - 收尾：`Stop-Process` 关程序 → **恢复 `App.xaml.cs` 并重编**（探针不能留在代码里）。


### 坑 64：悬浮窗里选了文字按 Ctrl+C 没反应（永远拿不到键盘焦点）

**现象**：悬浮窗里能用鼠标选中文字，但按 Ctrl+C 复制不了（右键菜单也不想用 —— 菜单会抢焦点，
而游戏一旦失焦就自己弹 ESC 菜单）。

**根因**：悬浮窗带 `WS_EX_NOACTIVATE`（这是"点了不抢游戏焦点"的代价，见坑 40），
它**永远不是前台窗口**，因此永远拿不到键盘焦点 —— WPF 那套输入路由
（`InputBindings`、`PreviewKeyDown`、`ContextMenu` 的快捷键、`ApplicationCommands.Copy`）整条都走不到，
不是"快捷键没绑"的问题，是键盘事件压根没送到这个窗口。

**修法**（`OverlayWindow.xaml.cs` + `Controls/SelectableChatTextBox.cs`）：
1. 控件侧记着"最近谁有选中文字"：`SelectionChanged` 时把实例存进静态的
   `SelectableChatTextBox.LastWithSelection`（同时记时间戳），选区清空则清掉。
   并提供 `CopySelection()`（复制选区）和 `CopyAll()`（复制整条）。
2. 悬浮窗侧挂一个**低级键盘钩子**（`WH_KEYBOARD_LL`，只在窗口可见时挂、隐藏/关闭时摘掉）：
   看到 `Ctrl+C` 且鼠标还在悬浮窗上（或 20 秒内刚选过）就拿 `LastWithSelection.CopySelection()` 复制。
   两条注意：**不吞键**（游戏该收到的 Ctrl+C 照收）；判断"是不是冲着悬浮窗来的"用鼠标位置/时间兜底，
   免得在游戏里按 Ctrl+C 被截胡。
3. 另给一条**完全不依赖键盘**的路：**双击消息 = 复制整条**（`OnMouseDoubleClick` → `CopyAll()`），
   即使钩子在某些环境不生效，用户也有得用。
4. 复制成功后在悬浮窗右上角闪一下"已复制"（`CopyHint`，`DoubleAnimation` 淡出）。

**验证**（真机，Debug 版）：往悬浮窗发一条 `[示例] 你好，我是插件。`，
在它上面拖动选中 → 按 Ctrl+C → 剪贴板拿到 `[示例] 你好，`；
双击 → 剪贴板拿到整条 `[示例] 你好，我是插件。`；拖选时窗口位置不动（说明按在文字上、没拖窗）；
未发生"没按 Ctrl+C 就被自动覆盖剪贴板"。

**顺带改的文案**：设置页「允许选中文本」的说明从"需要关闭鼠标穿透才能用"改成
"需先关闭鼠标穿透；选中后按 Ctrl+C 复制"（不然用户不知道能复制）。


---

## 快捷命令（可点击聊天 → 一键发命令）2026-09-30

**需求**：服务器聊天里有些消息点了能快捷发命令（例：`骑单车的鹅_1请求加入您的队伍！您可以进入组队界面进行处理！`
→ 点它 → 自动发 `/zd accept 骑单车的鹅_1`）。悬浮窗要做到一样的效果。

### 实现（全在宿主里，不依赖插件）

| 文件 | 干什么 |
| --- | --- |
| `Models/QuickCommandSettings.cs` | 设置：`Enabled` / `DoubleClickToSend` / `TargetProcessName` / `ChatKey` / `UseClipboard` / `StepDelayMs` / `ShowToast` / `Rules`；`QuickCommandRule` = 名字 + 正则 + 命令模板；`CreateDefaultRules()` 自带"组队请求 → `/zd accept {1}`" |
| `Services/QuickCommand/QuickCommandMatcher.cs` | 按顺序试规则；正则编译+匹配都带 120ms 超时（用户手写正则，不能让它卡死程序）；`Expand()` 支持 `{0}` `{1}` `{名字}`；`ValidateRule()` 给页面实时挑错 |
| `Services/QuickCommand/GameChatSender.cs` | 真正发送：按进程名找游戏窗口 → **必须已经在前台（切不过去就放弃）** → 按聊天键 → 剪贴板粘贴（`UseClipboard`，发完还原剪贴板）→ 回车；`IsTargetForeground()` 给页面做状态显示 |
| `ViewModels/ChatMessageViewModel.cs` | 新增 `ClickAction` / `ClickTooltip` / `RequiresDoubleClick` / `IsClickable` |
| `Controls/SelectableChatTextBox.cs` | 可点击消息：手型光标 + 悬停高亮；**按下记位置、抬起若没怎么移动就触发**（位移 >4px 算拖拽，不触发）；双击模式下 `ClickCount=2` 才发；两次动作至少隔 800ms（防双击发两次） |
| `OverlayWindow.xaml(.cs)` | 绑定 `IsClickable` / `ToolTip`；窗口层加 `IsOverClickableMessage()`：点在可点击消息上就**不 DragMove**（见坑 65） |
| `MainWindow.QuickCommand.xaml.cs` | 页面：总开关 / 单击还是双击 / 目标进程名 / 聊天按键 / 每步延迟 / 用剪贴板 / 发送后弹提示 / 规则增删改 / **"试一下"实时试算** / 游戏窗口检测；聊天进悬浮窗前调 `AttachQuickCommandToMessage()` 挂动作；发送丢到后台线程 + 弹 toast + 写调试日志 |
| `MainWindow.Modern.xaml(.xaml.cs)` | 新增导航「快捷命令」+ 面板（4 张卡片）+ 三处接线 |
| `MainWindow.Plugins.xaml.cs` | `OnPluginOverlayMessage()` 里也给插件注入的消息挂一遍匹配（插件发的测试消息一样能点） |

### 设计取舍

- **默认关闭**（`Enabled=false`）：往游戏里发命令是有副作用的动作，不该默认替用户打开。
- 发送前**硬校验目标窗口在前台**：切不过去就放弃并提示 —— 宁可发不出去，也不能把命令打进别的程序。
- 单条消息只在**命中规则**时才可点（悬停提示会写清"点击发送：xxx"），其余消息保持原样（可选文字、可拖窗）。
- 页面里那几处提示写明了代价：可点击消息上按住不能拖窗口、命令是用你自己的账号发出去的。

### 真机验证（2026-09-30）

- 试算：粘 `骑单车的鹅_1请求加入您的队伍！您可以进入组队界面进行处理！` → 页面显示"命中规则「组队请求」/ 抠出来的目标：骑单车的鹅_1 / 将发送：/zd accept 骑单车的鹅_1" ✓
- 可点击性：消息元素的 UIA `HelpText` = `点击发送：/zd accept 骑单车的鹅_1` ✓
- 端到端：起了一个 WinForms 接收窗当"游戏"（进程名 powershell、标题带 minecraft），让它在最前 → 点悬浮窗里那条消息 → 接收窗里出现 `t/zd accept 骑单车的鹅_1` ✓✓（`t` 是聊天键、命令整条粘进去、后面跟回车）
- 调试日志记录：`[快捷命令] 命中规则「测试规则」→ /zd accept 骑单车的鹅_1（源消息：…）`、`[快捷命令] 点击触发，开始发送：…`、`[快捷命令] 已发送：…（窗口：…）`
- Release / Debug 都是 0 警告 0 错误

### 已知限制

- 只在**窗口化/无边框全屏**下可靠（独占全屏时按键注入要看驱动/系统版本；截图类功能同理）。
- 发送依赖"游戏窗口能被切到前台"。悬浮窗是 `WS_EX_NOACTIVATE`（坑 40），点它不会把游戏顶到后台，所以正常点击时游戏本来就在前台。
- 模拟点击（`mouse_event`）进不了这个 `WS_EX_NOACTIVATE` + 分层窗口，**验收时要真鼠标点**（或者像上面那样用 PostMessage 投递 `WM_LBUTTONDOWN/UP` 做自动化）。

---

## 坑 66：悬浮窗里的可点/高亮范围，下标必须以「显示文本」为基准（不是服务器原样文本）

`OverlayWindow.BuildSegments()`（OverlayWindow.xaml.cs:718）会在消息最前面插一段 `[HH:mm:ss] ` 时间前缀，
后面还可能被颜色分段（§ 码）切碎。所以 `ChatMessageViewModel.Segments` 拼起来才是**屏幕上真正渲染的文字**。

「快捷命令」的规则正则必须跑在服务器原样文本上（`^` 锚点在原样文本上才成立），
但给消息挂"可点部分"时的字符下标**必须换算到显示文本**：

```csharp
var displayText = message.Segments is { Count: > 0 }
    ? string.Concat(message.Segments.Select(s => s.Text))
    : message.Text ?? rawText;
var start = displayText.IndexOf(part.Text, StringComparison.Ordinal);
```

反例（踩过的坑）：直接拿 `rawText` 里的下标去标记显示文本，整条消息会整体错位 11 个字符
（`[HH:mm:ss] ` 的长度），现象是"点 `[踢出]` 却发出 `[升职]` 的命令"。

配套：`SelectableChatTextBox.HitAt()` 用 `GetPositionFromPoint(point, true)` 拿 `TextPointer`，
再 `Document.ContentStart.GetOffsetToPosition(position) - 1` 换成字符下标（ContentStart 的 0 是虚位置）。

## 坑 67：分层 + WS_EX_NOACTIVATE 的悬浮窗，收不到真实鼠标「按下」

实测：鼠标悬停/提示都正常（能收到 move），但真实点击**根本不产生 WM_LBUTTONDOWN**
（诊断日志里连一行都没有；`PostMessage` 直投却能触发）——所以 WPF 里写多少点击处理都没用。

做法：在悬浮窗可见时挂**低级鼠标钩子**（`WH_MOUSE_LL = 14`，仿已有的 Ctrl+C 键盘钩子），
在输入层自己判断"点在哪"，命中可点部分就触发动作并 `return (IntPtr)1` 吞掉这次点击
（不吞的话点击会漏给下面的游戏）。判"点击"而不是"拖选"：按下→抬起位移 ≤6px 且 <1200ms。

同一个机制也顺手解决了"一条消息多个可点部分"：钩子拿到屏幕坐标 → `PointFromScreen` →
`GetPositionFromPoint` → 字符下标 → 找落在哪一块 → 发那一块自己的命令。

## 快捷命令：一条消息里多个可点部分（`Buttons` 字段）

`QuickCommandRule.Buttons`：每行一个 `文字=>命令`（也接受 `->`、`→`，行间可用 `|`），留空 = 整条消息可点（老行为）。
占位符和 `Command` 一样（`{1}` = 正则第 1 个捕获组）。

例（消息 `骑单车的鹅_1(在线) [踢出][升职]`）：

```
正则： ([^\s\[\]【】()（）]+)[（(](?:在线|离线)[)）]\s*\[踢出\]\s*\[升职\]
可点：[踢出]=>/zd kick {1}
      [升职]=>/zd promote {1}
```

名字的字符类要排除 `[]【】()（）`，这样 `[vip1][25阶1003⚝]骑单车的鹅_1(在线) …` 也能正确抠出 `骑单车的鹅_1`。
可点部分的文字必须能在显示文本里原样找到（`IndexOf`），找不到就退回正则匹配位置。

「点击后的绿色提示」已按用户要求去掉，只有**失败**才在悬浮窗顶部闪红字。

## 坑 68：ViewModel 的属性是「渲染之后」才挂上来的 → 控件必须自己盯着它重建

宿主挂快捷命令的顺序是：

```csharp
var v = _overlay?.AddMessage(chatMessage);    // 设 Segments → 控件重建文档（此时 Hits 还是空的）
AttachQuickCommandToMessage(v, chatMessage);  // 之后才 SetHits
```

`Hits` 是普通属性，改它**不会**让 `SelectableChatTextBox` 重建文档。如果控件只在 `Segments`
变化时重建，那它内部做点击判定用的 `_hitRuns` 字典就永远是空的：下划线画不出来、
`IsPointOnClickablePart` 永远 false、**点哪都没反应**。
（旧写法是点击那一刻才去读 `vm.Hits`，所以还能点、只是位置会偏 —— 两种写法各踩一个坑。）

修法：控件在 `DataContextChanged` 里订阅 `INotifyPropertyChanged`，`Hits`/`Segments` 变了就重建，
`IsClickable`/`TextSelectionEnabled` 变了就重算 `IsHitTestVisible`。ViewModel 的属性可能是日志
监听线程改的，所以要先 `Dispatcher.CheckAccess()` 再决定直接跑还是 `BeginInvoke`。

诊断特征：`%TEMP%\mcc-click-trace.log` 里**一条 `渲染自检` 都没有**（自检只在 `_hitRuns` 非空时才跑）。

## 坑 69：低级钩子 + UI 线程被阻塞 = 整台机器的鼠标键盘一起卡

悬浮窗挂了 `WH_MOUSE_LL`（抓点击）和 `WH_KEYBOARD_LL`（Ctrl+C 复制）。低级钩子的回调是在
**安装它的那个线程**上被调用的，也就是悬浮窗的 **UI 线程**；而 Windows 必须等钩子回调返回才继续
处理输入 —— 所以 **UI 线程一卡，全系统输入一起顿**。

触发点：`Clipboard.SetText()`。剪贴板被别的程序占着时 WPF/OLE 会在内部重试，能把调用线程卡住
几百毫秒到几秒，表现就是「偶尔选中文字按一下 Ctrl+C，鼠标卡一阵」。

修法：复制丢到**后台 STA 线程**去写（OLE 剪贴板要求 STA，线程里要 `OleInitialize` / `OleUninitialize`），
UI 线程完全不碰剪贴板。已验证：后台 STA 线程写完，主线程能立刻读回。

顺带记一句：钩子回调里**只能做常数时间的判断**（读结构体、比坐标、`BeginInvoke`），
任何可能阻塞或耗时的活（剪贴板、文件、`Dispatcher.Invoke`）都必须挪走。

## 【回退】「快捷命令（可点消息）」已整体删除；悬浮窗复制改回原生

用户决定不做「点聊天消息 → 自动往游戏发命令」这个功能了，已整体删除。同时把复制改回旧版的原生方式。

### 为什么旧版复制是好的

09-28 的旧快照里，悬浮窗**没有 `WS_EX_NOACTIVATE`**（对比坑 40）：它是可激活的普通置顶窗，
点它就能拿到键盘焦点，WPF 的 `RichTextBox` **自带**选中与 Ctrl+C 就能用 —— 一个钩子都不需要。

后来为了让悬浮窗不抢游戏焦点，加了 `WS_EX_NOACTIVATE`（坑 40）→ 窗口再也拿不到键盘焦点
→ 点击收不到（坑 67）、Ctrl+C 也送不进来（坑 64）→ 只好补 `WH_MOUSE_LL` / `WH_KEYBOARD_LL` 两个全局钩子
→ 钩子回调跑在 UI 线程上，剪贴板一卡就**全系统输入跟着卡**（坑 69）。
这次按用户要求把这条链子整体剪掉，回到旧版做法。

### 现在的状态

- `OverlayWindow.xaml`、`Controls\SelectableChatTextBox.cs`：与 09-28 旧版**逐字节一致**（直接用旧文件覆盖的）。
- `OverlayWindow.xaml.cs`：删掉 `ApplyNoActivate` / `WH_MOUSE_LL` / `WH_KEYBOARD_LL` / `ShowCopyHint` /
  `ShowActionHint` 以及「可点命中」的全部分支。**保留**了后来加的功能：KeepAlive 巡检、`EnsureTopmost`、
  `WM_DISPLAYCHANGE`、坑 43 的延迟重申、`_isMouseOverOverlay` 自动滚动。
- 删除文件：`MainWindow.QuickCommand.xaml.cs`、`Models\QuickCommandSettings.cs`、`Services\QuickCommand\*`。
- `MainWindow.Modern.xaml` 去掉 `NavQuickCommand` 与 `QuickCommandPanel`（导航回到 7 项）。
- `MainWindow.xaml.cs` 去掉 `InitializeQuickCommandUi` / 两条导航映射 / `Watcher_ChatLineReceived` 里的挂载；
  `MainWindow.Plugins.xaml.cs` 去掉插件消息的挂载；`ChatMessageViewModel` 去掉可点成员；`AppSettings` 去掉 `QuickCommand` 节。

### 实测（只读窗口属性，不注入任何输入）

悬浮窗 ex-style = **`0x00080008`**（`LAYERED | TOPMOST`，**没有 `0x08000000`**）⇒ 可被激活 ⇒ 原生 Ctrl+C 回来；
空悬窗 360x18、有一条消息 360x45（本进程非 DPI 感知，虚拟化坐标；×1.5 = 物理 540x27 / 540x67），与坑 43 修复后的预期一致。
Release / Debug 均 0 警告 0 错误。

### 代价（想清楚再动）

窗口可激活 = **点它 / 拖它会让游戏失去焦点**，Minecraft 会自己弹 ESC 菜单 —— 这正是当初加坑 40 的原因。
如果这个变难受了，折中是：让 `WM_MOUSEACTIVATE` 返回 `MA_NOACTIVATE`（点它不抢焦点、但**照样收到鼠标消息**，
所以可点按钮/自绘拖拽都能做），再把复制做成悬浮窗里的「复制全部」按钮 —— 代价是纯 Ctrl+C 那条路没了
（不激活就没有键盘焦点，这是硬约束）。

## 【新功能】插件市场（已实现并自测通过）

一句话：**市场没有服务器** —— 清单是仓库里的 `market/index.json`，插件包是仓库里的 zip。

| 部分 | 在哪 |
|---|---|
| 清单模型 | `Services\Plugins\Market\PluginMarketModels.cs`（`MarketIndex` / `MarketPlugin`，宽容解析） |
| 联网 | `Services\Plugins\Market\PluginMarketClient.cs`（拉清单 主源→备用源；下载 + 大小/SHA256 校验） |
| 缓存 | `Services\Plugins\Market\PluginMarketCache.cs`（`%AppData%\MinecraftChatOverlay\market\`，断网显示上次列表） |
| 界面 | `MainWindow.Plugins.Market.xaml.cs` + `MainWindow.Modern.xaml` 的 `PluginMarketView` |
| 设置 | `AppSettings.PluginMarket`（`IndexUrl` 默认 jsDelivr 镜像、`FallbackUrl` 默认 raw） |
| 数据 | `market\index.json` + `market\packages\*.zip` + `market\README.md` |
| 生成清单 | `tools\rebuild-market-index.ps1` + `tools\rebuild-market-index.bat` |

设计上刻意做的事：

- **安装复用现有的一条路**：下载完的 zip 直接交给 `ImportPluginZip()` → `PluginManager.ImportZip()`，
  所以安装确认框（列作者声明的能力）、zip 校验、apiVersion 检查、dll 被占用时排队到下次启动，全都白送，
  市场里没有第二套安装逻辑。
- 清单里的 `downloadUrl` 写**相对路径**，客户端按清单自身地址解析 → 换镜像/换分支/换仓库不用改清单。
- 卡片状态由 id + 版本比较得出：`未安装` / `已安装 vX（已是最新）` / `可更新到 vY` / `需要的契约版本不符`。
- 启动时若缓存超过 6 小时会**后台预热**一次（打开市场时列表已经在了）。

自测（本机起 HTTP 服务当仓库、程序/探针真去拉，不注入任何输入）：

```
[1] 拉清单 ok=True 插件数=1
[2] goldiamond.autogg v1.0.0 «自动 GG» size=10730 api=1
    相对地址解析 → http://…/packages/AutoGG-1.1.0.zip
[3] 正常下载 ok=True 10730B
[4] 给错 sha256 ok=False「SHA256 和清单对不上，已丢弃」→ 文件确实被删掉了
[5] 404 分支 ok=False「网络不通（404）」
[6] 主源挂了自动切备用源 ok=True
```

另：真机跑过一遍完整链路（把 settings 的 `PluginMarket.IndexUrl` 临时指到本机服务）——
服务端收到 `index.json → 200`，程序写下缓存并解析出 `goldiamond.autogg v1.0.0` ✓。
真实 GitHub 地址实测：`market/packages/AutoGG-1.1.0.zip` 在 raw 和 jsDelivr 上都是 **HTTP 200**。
（`index.json` 还没进仓库，所以真实清单地址要等 push 之后才活。）

## 坑 70：PowerShell 脚本要 UTF-8 **带 BOM**，.bat 要 GBK

用 UTF-8 **无 BOM** 写 `.ps1`，PowerShell 5.1 会按 ANSI 读 → 中文注释变乱码 → 直接语法报错
（"The Try statement is missing its Catch or Finally block."，而且报的还是看起来正常的那一行）。
`.ps1` 必须带 BOM（`New-Object System.Text.UTF8Encoding($true)`）；
`.bat` 要 GBK + `chcp 936`，和 `push.bat` / `publish.bat` 一致。
（注意这跟 `.cs` / `.xaml` 的「UTF-8 无 BOM」约定**相反**。）

附带两条小坑：

- `ExecutionPolicy` 是 `Restricted` 的机器上不能直接跑 `.ps1` → 加个 `.bat` 包装
  （`powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0xxx.ps1" %*`，和 `build-plugins.bat` 一样）。
- `dotnet run --project X -c Release --nologo` 里的 `--nologo` 会被当成**传给程序的参数**；
  要给程序传参得用 `--` 分隔。（探针里踩到过：程序把 "--nologo" 当 URL，报 "invalid request URI"。）

## 【新增】发布插件：两条命令搞定

### 1) tools\publish-plugin.bat <插件目录名>

一条命令把某个插件发布到市场（例：publish-plugin.bat AutoGg）：

1. 编译 Plugins\<插件名>
2. 打包 market\packages\<插件id>-<版本>.zip（条目是 <id>/<dll> + <id>/plugin.json，正斜杠标准 zip）
3. 删掉这个插件的旧版本包（一个 id 只留最新那个）
4. 重建 market\index.json

故意不把插件装进本机 —— 否则市场卡片会直接显示「已装最新」，就没法验证"更新"这条路了。
底层干活的是 tools\pack-market-package.ps1（认主 dll 的顺序：plugin.json 的 assembly → <插件名>Plugin.dll → 输出目录里唯一的非契约 dll）。

### 2) push.bat

4/4 段加了自动重试：git push 被拒（远程有本地没有的提交，最常见的就是"在网页上直接改过仓库"）
→ 自动 git fetch + git pull --rebase origin main → 重推，最多 3 轮；
rebase 真出冲突就停下，并打印手动处理步骤。推完还会打印 origin/main 的最新提交和本地/远程状态。

## 坑 71：robocopy 的 /XD 裸名会匹配任意层级的同名目录

push.bat 里原来写着 /XD ... packages ...，本意是排除打包产物，结果把 market\packages 也一起排除了 ——
以后 publish-plugin.bat 产出的市场包根本推不上去，而且不报错、静默不同步。

实测：源目录里同时放 packages\b.txt 和 market\packages\a.zip，/XD packages 之后两个都没同步。
修法：写成全路径 /XD "%DEV%\packages"。

教训：给 robocopy 加排除项之后，一定要干跑验证（/L，而且不要加 /NFL ——
加了它就只统计不列文件名，看起来像"没匹配到"，其实只是没打印）。

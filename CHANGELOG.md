# 更新内容清单

## v1.2.5

### 「关于」页（新）

- 左侧导航新增「关于」：开源声明、打赏二维码（微信扫码，编译进程序集）、B站主页 / QQ 闲聊群入口。
- 打赏鸣谢与插件开发鸣谢两份名单放在仓库 `about/about.json`，软件直接拉（jsDelivr 主源 + GitHub raw 备用，取 `updatedAt` 更新的那份）。
- 名单里 `name` 必填，`amount` / `message` / `time`（打赏）与 `plugin` / `link` / `note`（开发者）都可选，不写就不显示那一截。
- 点右上角【刷新名单】手动更新，成功/失败都有提示；连不上时保留上次拉到的列表。

### 开屏公告（新）

- 启动时静默拉 `about/announcement.json`，拉到新公告就弹窗（外观和扫码登录那个窗一套）。
- 同一条公告只弹一次：软件把弹过的 `id` 记在本机配置里，所以改内容时要换新 `id`。
- 拉不到、没公告、格式不对都当无事发生，不打扰用户；公告可以带一个可选链接按钮。

### 版本

- `Copy.cs` 的 `AppVersion`：`1.2.1` → `1.2.5`（`publish.bat` 据此产出 `release_v1.2.5.zip`）。
- 界面版本号显示同步跟上（`v1.2.5`）。

## v1.2.1

### 插件市场（新）

- 插件页右上角新增【插件市场】：清单是仓库里的 `market/index.json`，插件包就是仓库里的 zip —— **没有服务器**。
- 客户端两个源都查（jsDelivr 镜像 + GitHub raw），取 `updatedAt` 更新的那一份
  （jsDelivr 对分支 URL 缓存很久，只认它就会一直看到旧清单）。
- 缓存到 `%AppData%\MinecraftChatOverlay\market\`，断网时显示上次的列表；启动时缓存超过 6 小时会后台预热。
- 清单里的下载地址写相对路径，换镜像 / 换分支 / 换仓库只改软件里那一个地址。
- 下载后校验 SHA256 和大小，对不上直接丢弃；安装复用现有 zip 导入流程（确认框、校验、文件占用排队全都一致）。
- 卡片状态由版本号比较得出：未安装 / 已安装 vX（已是最新）/ 可更新到 vY / 依赖的契约版本不符。

### 插件发布工具（新）

- `tools\publish-plugin.bat <插件名>`：编译 → 打包 `market\packages\<id>-<版本>.zip` → 删掉旧版本包 → 重建清单，一条命令。
- `tools\pack-market-package.ps1`：底层打包（认主 dll：`assembly` 字段 → `<插件名>Plugin.dll` → 唯一的非契约 dll）。
- `tools\rebuild-market-index.bat`：扫 zip 重建 `market\index.json`（兼容反斜杠的 zip、同一个 id 只留版本最高的）。
- `push.bat`：推送被拒时自动 `fetch` + `pull --rebase` 重试（最多 3 轮），真冲突才停下并打印手动步骤。

### 修复

- **robocopy 的 `/XD packages` 裸名会匹配任意层级的同名目录**，把 `market\packages` 也排除了 ——
  市场的插件包根本推不上去且不报错。已改成全路径 `/XD "%DEV%\packages"`。
- 插件页「想自己写一个插件？」卡片换成按钮，点击打开在线开发文档 <https://goldiamond1031.github.io/MCO>。

### 版本

- `Copy.cs` 的 `AppVersion`：`1.2.0` → `1.2.1`（`publish.bat` 据此产出 `release_v1.2.1.zip`）。

---

## v1.1.0（相对 v1.0.5）

这是从 MinecraftChatOverlayDSUI3 同步过来的一个大版本：新增「游戏动态模糊」和「击杀反馈」两个完整模块，
另外把提示音系统、文本渲染规则、README 都重做了一遍。

---

### 一、新增功能

#### 1. 游戏动态模糊（帧混合）

给游戏画面本身加拖影，靠注入一个原生 DLL 拦截 OpenGL 的换帧调用来做，不是录屏叠加。

- 新增 `Services/GameMotionBlur/` 三个文件（服务、进程注入、控制块，共 1195 行）
- 新增 `native/` 整个 C++ 原生工程：`GameMotionBlur`（钩子 DLL 源码）、`TestApp`（独立测试程序）、
  `build.ps1`（编译脚本）、`dist/`（编好的 DLL）
- 新增 `tools/GmBlurCli/`：命令行小工具，用来在不开主程序的情况下单独试注入
- 新增对应设置页：选目标进程、注入/卸载、模糊强度、只对画面还是含 UI

> 注意：`native/dist/GameMotionBlurHook_v6.dll` 会被 csproj 复制成输出目录里的
> `GameMotionBlurHook.dll`。没跑过 `native/build.ps1` 也不影响主程序启动。

#### 2. 击杀反馈

在游戏画面上做击杀时的视觉 + 听觉反馈。

- 新增 `Services/KillFeedback/`：`KillFeedbackMatcher.cs`（匹配日志里的击杀信息）、
  `KillFeedbackEffects.cs`（各种视觉效果，536 行）
- 新增 `KillBannerWindow.xaml` + `.xaml.cs`：击杀横幅悬浮窗（590 行）
- 新增 `Models/KillFeedbackSettings.cs`、`KillIconChoice.cs`
- 新设置页 `MainWindow.KillFeed.xaml.cs`（1624 行）—— 本项目最大的单个界面文件
- 效果可多选、各自调大小；击杀图标 / 自定义图片；击杀提示音；连杀合并；匹配规则可写多条

#### 3. 提示音系统重做

提示音从「程序内置四个 wav」改成「用户在界面上自己导入」。

- 新增 `Services/ChatSoundNotifier.cs`（461 行）：统一的播放服务，支持重叠播放
- 新增 `Services/RandomPicker.cs`：多音效随机 / 顺序挑选
- 新增 `Models/SoundChoice.cs`：一个音效条目（路径 + 显示名）
- 新增 `MainWindow.SoundNotify.xaml.cs`（429 行）、`MainWindow.KillSound.xaml.cs`（386 行）
- 删掉了内置的 `notify_ding / notify_dingdong / notify_soft / notify_blip` 四个 wav

#### 4. AI 识图（截图 → 自动分配颜色）

- 新增 `Services/AiVisionClient.cs`（379 行）：调用视觉模型识别截图里的队伍信息
- 新增 `Services/ScreenshotWatcher.cs`：盯截图目录，有新图就处理
- 新增 `Services/AiResponseLog.cs`：把 AI 的返回记下来方便排查
- 新设置页 `MainWindow.MotionBlur.*` 之外还拆出了截图目录、识别开关等

#### 5. 其他新增

- `Models/TeamColorTable.cs`：队伍 → 颜色对照表
- `Services/ChatIdCollector.cs`（366 行）：从聊天里收集玩家 ID
- `Converters.cs`：几个 WPF 值转换器
- `MainWindow.LockedCard.xaml.cs`（186 行）：卡片锁定 / 拖动
- `design/`：三份 UI 风格试验稿（ui-depth / ui-dialects / ui-neumorphism）
- `promo/`：宣传页素材
- `GUI.html`：界面说明页（2589 行，不进发布包）
- `DEV-NOTES.md`：开发笔记（499 行，含 42 条踩坑记录）

---

### 二、修改的功能

| 文件 | 改动 |
|---|---|
| `Copy.cs` | 版本号 `1.0.5` → `1.1.0`；新增一批界面文案常量 |
| `MainWindow.Modern.xaml` | 主界面大改：新增三个设置页、重排布局 |
| `MainWindow.xaml` | 旧版备份视图同步跟进 |
| `MainWindow.xaml.cs` | 新增 AI 卡片折叠、屏蔽关键词「仅玩家」、击杀窗口生命周期等逻辑 |
| `MainWindow.xaml.cs` | 关闭时显式关闭常驻的击杀横幅窗 |
| `Models/AppSettings.cs` | 新增动态模糊、击杀反馈、提示音的配置节点 |
| `Models/BlockKeywordItem.cs` | 新增 `OnlyPlayerContent` 字段 |
| `Models/TextColorRule.cs` | 跟进新的规则结构 |
| `Services/ChatTextProcessor.cs` | 新增按「仅玩家内容」判定屏蔽的重载 |
| `Services/ChatLineParser.cs` | 跟随日志格式调整 |
| `Services/SettingsService.cs` | 配置目录仍是 `%AppData%\MinecraftChatOverlay\settings.json`，新增字段迁移逻辑 |
| `Controls/ColoredTextBlock.cs` | 染色渲染调整 |
| `Controls/SelectableChatTextBox.cs` | 可选中文本调整 |
| `OverlayWindow.xaml.cs` | 配合新的置顶 / 不抢焦点处理 |
| `ViewModels/ChatMessageViewModel.cs`<br>`ViewModels/ChatSegmentViewModel.cs` | 消息模型跟进 |
| `ModernControls.xaml` | 补了一批控件样式 |
| `MinecraftChatOverlay.csproj` | 见下面「三、编译相关」 |
| `README.md` | 从 83 行扩写到 413 行，补全 8 个模块的说明 |

---

### 三、编译相关（csproj 关键改动，必须一起同步）

1. **排除 `obj\`**，避免 WPF 标记编译生成的临时工程重复包含 `AssemblyInfo.cs`：
   ```xml
   <DefaultItemExcludes>$(DefaultItemExcludes);obj\**</DefaultItemExcludes>
   ```

2. **把 `tools\` 和 `native\` 排除在主工程通配之外**，否则 `tools\` 里生成的
   `AssemblyInfo.cs` 会被再编译一次，报 CS0579「程序集特性重复定义」：
   ```xml
   <Compile Remove="tools\**" />
   <Page Remove="tools\**" />
   <ApplicationDefinition Remove="tools\**" />
   <EmbeddedResource Remove="tools\**" />
   <None Remove="tools\**" />
   <None Remove="native\**" />
   ```

3. **原生钩子 DLL 的复制规则**（`_v6` 优先，没有才用旧的）：
   ```xml
   <None Include="native\dist\GameMotionBlurHook_v6.dll" Link="GameMotionBlurHook.dll"
         Condition="Exists('native\dist\GameMotionBlurHook_v6.dll')"
         CopyToOutputDirectory="PreserveNewest" />
   ```

4. 新增 NuGet 依赖：`QRCoder 1.6.0`（B 站扫码登录生成二维码）。

---

### 四、v1.1.0 这一轮（相对本清单前面几项）的收尾改动

- 击杀反馈设置页顶部新增提示条：**要先开启动态模糊并注入游戏，击杀反馈才会生效**
  （顶部实时显示 ✓ / !，会跟着注入状态变）
- 文本渲染规则 - AI 自动识别卡片：**没开启时整张卡片收起来，只留开关**
- 文本渲染规则 - 屏蔽关键词：新增 **「仅玩家发送消息生效」**，勾上后系统消息
  （进服 / 退服 / 成就等）提到该关键词不会被屏蔽
- 击杀反馈里「预览循环」按钮移除（预览只播一次）
- 击杀横幅窗口改为**常驻悬浮窗**：程序启动时就创建并显示，只在识别到击杀时露图标，
  其余时间完全透明。这样避免了反复 `Hide()`/`Show()` 全屏分层窗口本身造成的干扰；
  同时通过 `WS_EX_NOACTIVATE + WS_EX_TRANSPARENT` 保证窗口既不抢游戏焦点、也不吃鼠标


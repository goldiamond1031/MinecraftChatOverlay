# 更新内容清单

## v1.2.8（未发布）

### 击杀反馈：击杀图标 / 击杀提示音支持「顺序播放」

- 原来只有【随机显示】/【随机播放】（每次从勾选的里面抽一个）。现在多一个【顺序播放】：
  按列表顺序轮着放，**列表里紫色高亮的那一项就是"下一个要用的"**，放完自动把高亮移到下一项；
  点列表里任意一项，就能把"轮到谁"改成它。适合"想按固定顺序换着来"的用法。
- 顺序播放和随机播放**互斥**（勾一个会自动取消另一个）；两个都不勾时还是原来的语义：
  固定用列表里选中的那一个。
- 顺序播放同样只在你勾中的那些里轮（一个都没勾就退化成全部）—— 和随机播放的池子规则一致。
- 顺手改了击杀反馈开关下面那句说明：现在是「打开此开关不等于打开了视觉特效，视觉特效只有在注入动态模糊时才会自动开启」。

### 击杀反馈：匹配规则「新手引导」（16 步，手把手）

- 匹配规则那块原来只有【查看填写教程】弹一段文字，很多用户看完还是不知道**该写什么**。现在多了一个
  **【▶ 新手引导（手把手）】**：页面底部浮出引导卡，一步一步带着走 —— 高亮"你自己的 ID"、替你填一条
  "把敌人 ID 也写进去"的错规则、带你发现错在哪、再找出**变的部分和不变的部分**、最后处理
  "第 #1 个最终击杀"这种带数字的消息，收尾留一条能用的规则在框里。
- **进去之前先弹窗说明代价**：引导会清空已经填好的匹配规则（并顺手打开击杀反馈开关），
  给【进入教程】/【再等等】两个选项，不点就什么都不动。
- 顺便把入口做醒目了：原来那个弱色的【查看填写教程】改成主色按钮【▶ 新手引导（手把手）】，
  原来的文字教程保留成旁边的【文字版】。
- 引导只改规则框和开关，其它设置一律不动；中途点【退出教程】，规则框里是什么就留什么。

### 游戏动态模糊：钩子日志能直接在界面里看了

- **新位置**：日志落在**程序目录的 `logs\gmb_hook_<游戏PID>.log`**；动态模糊页最下面多了一张
  【3. 钩子日志】卡片 —— 每秒自动刷新、显示末尾 200 行，还能【打开文件夹】/【复制路径】，
  找不到文件时卡片里会写出"本来应该在哪儿"。
- 以前落点靠 DLL 自己反查"我在哪个目录"，手动映射之后拿不到，只能退回**游戏 exe 旁边**
  （常常是启动器的 runtime 目录，根本找不到）。现在改成**注入之前由宿主告诉 DLL 写哪儿**：
  复用控制块里那段「输出目录」字符串区（原本是 `gmblur dump <目录>` 导出帧用的），
  `GameMotionBlurService.Inject()` 和 `gmblur inject` 都会先写进去。
- **协议没动**（没改结构体布局、没升 `kVersion`）：老 DLL 注入的进程仍写游戏 exe 旁边，
  界面按 程序目录\logs → 游戏目录 → DLL 目录 依次找，两种都能显示出来。
- v1.2.7 里那句「改成回退 `%TEMP%`」不准确：实测 `GetModuleFileNameW` 总是给出**游戏 exe 的路径**（不是失败），
  `%TEMP%` 只有连它都拿不到时才会用到。
- DLL 一进去的头几行（`已载入 pid=...`、控制块初始化结果）以前会因为"还不知道写哪儿"而丢掉，
  现在先攒在内存里、开文件时整段补写，**一行不丢**。
- 想让正在跑的游戏改到新位置，**得重新注入一次**（重开游戏，或再点一次【注入并接管】/`gmblur inject`）。

### 插件市场

- 上架「抖音直播弹幕」`nn.douyindanmaku` v1.0.7：把抖音直播间的弹幕 / 点赞 / 礼物 / 进场 /
  在线人数 / 粉丝团六类消息转发到聊天悬浮窗，每类消息的显示格式都能自己改，支持关键词黑/白名单过滤。
  **不依赖任何外部程序**，填个直播间号就能连。连接页放在插件页最上面（自绘页面）。

### 版本号

- `Copy.cs` 的 `AppVersion`：`1.2.7` → `1.2.8`（`publish.bat` 据此产出 `release_v1.2.8.zip`）。
  界面版本号显示同步跟上（`v1.2.8`）—— 界面上那处 `Text="v1.0.4"` 只是设计期占位，
  运行时由 `MainWindow.xaml.cs` 读 `Copy.AppVersion` 覆盖，不用手动改。

## v1.2.7

### 游戏动态模糊：注入方式换成「手动映射」

- 不再用 `CreateRemoteThread(LoadLibraryW)`，改成自己当加载器：解析 PE → 铺镜像 → 修基址重定位 →
  填导入表（含 `api-ms-win-crt-*` 这类转发导出，按宿主模块换算远程地址）→ **在目标进程里**分配 TLS 索引 →
  劫持一个线程（改 RIP 跑一段 Stub 调 DllMain，再把寄存器/标志位/栈全部还原）→ 跳回原处。
- 这样 DLL 不出现在目标进程的模块表里、磁盘上没有对应的加载痕迹。
- **旧的 LoadLibrary 注入路径已整个删除**：留着等于还有一条会留痕迹的路，也让人分不清走的哪条。
  手动映射失败会直接把原因抛给界面，不再静默换路。
- 修了原生钩子的日志路径：手动映射时 `GetModuleFileNameW` 拿不到自身路径，改成回退 `%TEMP%`。
- 实测：Minecraft（java.exe x64）里重定位 43 处、导入 12 个 DLL/86 个函数、TLS 在目标里分配、
  DllMain 返回 TRUE，DLL 日志显示 OpenGL 已接管、帧混合目标建立，blendCount 持续增长。

### 插件页

- **禁用/启用修好**：禁用后卡片不再从列表里消失、开关会正确变成"关"、禁用状态会写进配置（重启后仍然禁用）。
- **插件目录不会再被删成一半**：以前更新/卸载用的是逐个文件删，撞上被锁的 dll 会抛异常但已删的文件不回滚，
  结果 `plugin.json` 没了、dll 还在，插件再也装不回来。现在改成**先改名搬走、再删**（原子操作：
  锁着就一个字都不删，走"下次启动再清"）。

### 开屏公告

- 判断标准从「比 id」改成「**比更新时间**」：id 只能说明内容换过，不能说明新旧 ——
  GitHub 源拉不到、退回还没同步的镜像时，按 id 会把已经看过的旧公告又弹一遍。
- 右上角原来的【保存配置】按钮（配置本来就是改了即存，它没用）改成【查看公告】：
  点了重新拉一次并强制弹窗。

### 版本

- `Copy.cs` 的 `AppVersion`：`1.2.6` → `1.2.7`（`publish.bat` 据此产出 `release_v1.2.7.zip`）。
- 界面版本号显示同步跟上（`v1.2.7`）。

## v1.2.6

### 插件（本体的插件系统）

- 插件卡片右侧新增【卸载】按钮（原来只藏在卡片右键菜单里）；插件详情页页头也加了一个【卸载这个插件】。
  两处走同一个确认框，删插件目录 + `plugin-data\<id>\`，删完自动退回插件列表。
- 「插件」页补了一行说明，告诉用户想删插件该点哪儿。

### 击杀反馈

- 新增封禁风险提示条：**只有靠注入的画面效果有封禁风险**；击杀图标和击杀提示音在本机自己弹、自己播，
  不碰游戏进程，没有这个风险。
- 前置条件那条提示改了口径：不再说"必须先注入才能用"，改成
  「打开击杀反馈开关 + 不注入动态模糊 + 填对匹配规则 → 图标反馈和音效反馈可用」；
  已注入时明确写出"画面效果会生效，也就带上了上面那条封禁风险"。
- 【查看填写教程】改成独立弹窗（讲清匹配规则为什么必须填、怎么写、怎么自检）。

### 游戏动态模糊

- 顶部加封禁风险提示条：注入 DLL 改画面在服务端 / 反作弊眼里属于灰色地带，
  本身有封禁记录的机器、以及被人举报都会进一步加大风险。

### 关于页

- 打赏鸣谢改成一行一条：名字 + 金额在左，留言跟在右边（不再另起一行），时间靠最右；长留言自己折行。

### 界面（本体）

- 扫码登录窗改成「标题 / 内容 / 按钮」三行布局，内容区可滚：登录状态文字变长时按钮不再被顶出窗口。

### 插件市场

- 上架「网易云歌词」`goldiamond.neteaselyrics` v1.1.1：置顶歌词窗、逐字染色、可选中继插件读播放进度。
- 下架废弃的 `goldiamond.neteaselyric`（旧的 SMTC 方案），仓库里的历史残留一并删掉。

### 文档

- `README.md`：界面总览改成真实的 8 个导航页 + 5 个独立窗口；新增第 9 节「插件」、第 10 节「关于」；
  击杀反馈补「必须至少填一条匹配规则」；动态模糊 / 击杀反馈都写明封禁风险。
- `Plugins\PLUGIN-DEV-GUIDE.html`：新增 6.4 弹窗、6.5 选色、6.6 控件对照表；排错表 +6 行；AI 速查补到 16 条硬规则。

### 版本

- `Copy.cs` 的 `AppVersion`：`1.2.5` → `1.2.6`（`publish.bat` 据此产出 `release_v1.2.6.zip`）。
- 界面版本号显示同步跟上（`v1.2.6`）。

### 工具

- `publish.bat` 修掉"压缩包名字变成 `release_vv.zip`"的老毛病。
  原因：`findstr` 是包含匹配，`public const string AppVersion` 会连下面那行
  `AppVersionPrefix = "v";` 一起命中，循环跑两遍、后一条把版本号覆盖成 `v`，
  于是 `release_v` + `v` = `release_vv.zip`。现在匹配串带上等于号，
  并加了一道"读出来的必须像版本号"的自检，不对就直接报错停下。

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


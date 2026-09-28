# GameMotionBlur —— 给游戏加"帧混合动态模糊"的原生层

这个目录里是**注入到游戏进程里干活的那一半**。界面（WPF）那一半在
`Services/GameMotionBlur/`，命令行小工具在 `tools/GmBlurCli/`。

当前只支持一条渲染路径：

| 游戏 | 图形 API | 出帧函数 | 钩法 |
|---|---|---|---|
| Minecraft Java 版 / 任何 OpenGL 程序 | OpenGL | `SwapBuffers` / `wglSwapBuffers` | 扫各模块 IAT + 定期补扫 |

---

## 1. 它是怎么拿到游戏帧的（和 OBS 游戏源一样）

OBS 的「游戏源」本质上是三步：

1. 把 `graphics-hook.dll` **注入**到游戏进程里；
2. 在游戏进程内部 **Hook 图形 API 的呈现调用**；
3. 于是每一帧的缓冲区都会从它手里过一遍，它顺便把这一帧拷走送去编码。

我们做的是**一模一样的三步**，区别只在于拿到这一帧之后干什么：

| | 拿到帧之后 |
|---|---|
| OBS 游戏源 | 拷成纹理 → 送去编码 → 直播/录制 |
| 本项目 | **在游戏自己的后台缓冲上原地做帧混合**，然后再出帧 |

因为没有"拷出去再送回来"这一趟，所以：

* 不需要额外窗口覆盖在游戏上（全屏独占也能用）；
* 游戏里看到的就是模糊后的画面本身，不是"模糊的直播画面"；
* 每一帧只多一次全屏三角形绘制 + 一两次全屏拷贝。

### 为什么 OpenGL 用 IAT

GL 没有统一的虚表，出帧就是一个普通导出函数，每个调用方在 IAT 里各存一份地址。
所以：**扫所有已加载模块的导入表**，把指向 `gdi32!SwapBuffers` / `opengl32!wglSwapBuffers`
的槽位换成我们的函数；`HookPumpGL` 每 2 秒再扫一次，覆盖注入之后才加载的 `lwjgl_glfw.dll` 之类。

Minecraft Java 的实际路径是 GLFW 的 `win32_window.c` → `SwapBuffers(dc)`（gdi32），
正好命中上面这条。

> ⚠ **刻意不去改 opengl32/gdi32 的导出表（EAT）**：那是 32 位 RVA，有些还是 jmp 转发，
> 实测直接按"指针槽"去写会**把函数代码本身的前 8 字节覆盖掉**（我们踩过这个坑）。
> IAT 覆盖真实调用方就够了，风险小得多。

---

## 2. 帧混合是怎么算的

开启之后，每帧（出帧之前）做两件事：

```
1) 往后台缓冲上画一个全屏三角形，采样上一帧的混合结果 history：
      out = history * strength + 当前帧 * (1 - strength)
2) 把结果拷回 history，供下一帧使用 → 指数累积，也就是拖影。
```

实现方式：

* **OpenGL core（3.2+，Minecraft 1.17+）**：VAO + `gl_VertexID` 全屏三角形 +
  `glBlendFuncSeparate(GL_CONSTANT_ALPHA, GL_ONE_MINUS_CONSTANT_ALPHA, GL_ZERO, GL_ONE)`，
  strength 由 `glBlendColor` 给。
* **OpenGL 兼容模式（2.x，Minecraft 1.16-）**：固定管线 `glBegin/glEnd` 画贴图四边形，
  `glTexEnv(GL_MODULATE)` + `glColor4f(1,1,1,strength)` + `glBlendFunc(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA)`。
  这一条不依赖 `glBlendColor`，老驱动也能用。

`strength` 就是界面上的"模糊强度"：`0` = 完全关闭（连绘制都跳过）、
`0.55` = 默认、`0.95` = 几乎冻住。

**关于游戏状态的保护**：我们借用的是游戏自己的 context，动手之前会把状态存一份、画完原样还原：

* OpenGL：program、VAO、FBO、活动纹理单元与绑定、blend 全套、colorMask、depth/cull/scissor/stencil、viewport、
  draw/read buffer、pack/unpack 对齐（老路线上还有 TEXTURE_2D 与 TEXENV）。

这是"注入式后处理"能不能不把游戏画坏的关键。

---

## 3. 目录结构

```
native/
  build.ps1                     用 MinGW-w64 编译（不需要装 Visual Studio）
  GameMotionBlur/
    common.h                    ★ C++/C# 共享内存布局（改字段要两边一起改！）
    dllmain.cpp                 注入入口：只起线程，不干重活
    hook.cpp                    OpenGL 钩子 + 状态回写 + 总调度
    gl_hook.cpp                 OpenGL 钩子（IAT 扫描 / 卸载 / 定期补扫）
    gl_blur.cpp                 OpenGL 帧混合（core 与固定管线两条路线）
    bmp.cpp                     导出帧用的 BMP 写出
    control.cpp                 跨进程共享内存控制块
    log.cpp                     日志（写在 DLL 旁边 gmb_hook_<pid>.log）
  TestApp/
    TestGL.cpp                  OpenGL 验收画面（3.2 core profile，和 Java 版同款）
  dist/                         产物
```

---

## 4. 编译

```powershell
# 需要 MinGW-w64（默认找 C:\mingw64\bin\g++.exe，可以用 -Gxx 指定别的）
powershell -File native\build.ps1
```

产物：

* `native\dist\GameMotionBlurHook.dll` —— 钩子本体（只依赖 kernel32/user32/UCRT/opengl32）
* `native\dist\TestGL.exe` —— 测试画面

DLL 会被 `MinecraftChatOverlay.csproj` 自动复制到主程序输出目录。

---

## 5. 不用真游戏也能验收

```powershell
# 1) 启动测试画面
native\dist\TestGL.exe        # OpenGL：深色背景 + 左右扫描的白方块（3.2 core profile）

# 2) 注入（PID 在窗口标题上）
tools\GmBlurCli\bin\Debug\net8.0-windows\gmblur.exe list
tools\GmBlurCli\bin\Debug\net8.0-windows\gmblur.exe inject <PID>

# 3) 开效果、导出帧（顺便会把 history 纹理也导一份，方便对账）
gmblur.exe on 0.75
gmblur.exe dump C:\temp\blur 3

# 4) 对照：关掉再导一次
gmblur.exe off
gmblur.exe dump C:\temp\sharp 3
```

导出的 BMP 直接双击就能看。本机实测（800x600、方块 90x90、强度 0.75）：

| 路径 | 状态 | 纯白占比 | 中间灰（拖影） | 单行亮区宽度 |
|---|---|---|---|---|
| OpenGL | 关闭 | 1.69% | 0.00% | 90 px |
| OpenGL | 开启 | 0.00% | 4.16% | 198 px |

关闭时画面和原图**逐像素一致**（纯白占比、边界宽度都没变），说明状态保存/还原没把游戏画坏；
开启时亮度沿运动方向按 `strength` 几何衰减（OpenGL 那组实测剖面 68→115→150→176→196→211→222，
正是每帧 ×1/0.75 的累积结果），和公式吻合。连续混合上万帧、卸载钩子后进程仍然 `Responding=True`。

### 踩过的两个坑（留个记录）

1. **OpenGL 的全屏三角形 UV 不能乘 0.5**。用 `p=(0,0),(2,0),(0,2)` + `gl_Position=p*2-1`
   时，屏幕范围内的 `p` 本来就是 0..1，`vUv=p` 才对；乘 0.5 会让整屏都采样到贴图边缘
   （背景色），表现为"混合像没生效"。
2. **别去改 opengl32 的导出表**，见上面第一节的警告。

---

## 6. 击杀反馈：事件通道 + 多效果配置（控制块 v2 → v5）

控制块里既有「事件序号」通道，也有 v4/v5 的「效果配置数组」：

```
界面（C#）                                               游戏进程里的 DLL
  WriteKillEffects([(kind,strength,enabled,reserved), ...])
  RequestKillFeedbackEffects(durationMs)
        │  effectConfigCount / EffectConfigs + eventSeq++ / eventDurationMs
        ▼
   ── 共享内存 ControlHeader + KillEffectConfig[kMaxKillEffects] ──
        ▲  eventHandledSeq / eventActiveMs / eventPlayCount / eventZoomMilli
        │
  GetStatus()  ←  ControlTickKillEffects() 每帧推进计时并算出每个效果的实时强度
```

### 设计要点

1. **事件用序号不用标志位**。连杀时发 `eventSeq++`，钩子端只比较序号是否变化；
   按 uint32 回绕也仍然正确。
2. **计时和包络放在 `control.cpp`**。`ControlTickKillEffects()` 每帧推进，
   强度包络和 C# 预览器 `KillFeedbackEffects.Envelope` 保持一致
   （前 16% 冲到峰值，之后 `exp(-3.6u)` 回落）。
3. **v5 配置数组**。`KillEffectConfig{kind, strength, enabled, reserved}` 写在头部之后；
   强度 0.0~5.0 对应界面 0~500%。多个效果可以同时启用，互不覆盖；
   `reserved` 的低 24 位是边缘脉冲的 RRGGBB 颜色。
4. **旧单效果事件兼容**。老调用 `RequestKillFeedback` 仍能触发缩放脉冲，方便回退。

### 目前实现状态

| 效果 | OpenGL core | OpenGL 兼容（固定管线） |
|---|---|---|
| 缩放脉冲 | ✅ | ✅ |
| 抖动 | ✅ | ✅ |
| 边缘脉冲（可自定义颜色） | ✅ | ❌（需要 core 着色器） |
| 暗角脉冲 | ✅ | ❌（需要 core 着色器） |
| 色差分离 | ✅ | ❌（需要 core 着色器） |
| 全屏闪光 | ✅ | ❌（需要 core 着色器） |
| 中心冲击环 | ✅ | ❌（需要 core 着色器） |
| 故障撕裂 | ✅ | ❌（需要 core 着色器） |

* **OpenGL core**：把后台缓冲拷进暂存纹理，再用一个全屏 fragment shader 同时计算
  `uUvScale` / `uShake` / `uChromatic` / `uEdge` / `uVignette` / `uFlash` / `uShockwave` / `uGlitch` 等。多效果就是多个 uniform 同时非零。
* **OpenGL 兼容**：只支持缩放脉冲和抖动（改纹理坐标 / 顶点偏移），其余效果会在日志里提示需要 core。
* **平时零开销**：没有事件、也没开帧混合时，不会多画这一遍。

### 不用真游戏也能验收

```powershell
native\dist\TestGL.exe                        # 启动测试画面（窗口标题上有 PID）
gmblur.exe list                               # 找到它
gmblur.exe inject <PID> native\dist\GameMotionBlurHook_v6.dll
gmblur.exe killall 500 300 200 150 250 400 400 400 400 900    # 9 个效果各自大小，900ms
```

`killall` 会自己看一秒并把结论打出来；缩放 500% 时峰值约为
`1 + 500% × 0.10 = 1.5`，包络采样后一般看到 `1.485x` 左右。
想从像素层面确认，可以边发事件边导出帧；测试画面的白方块左右移动但面积恒定，
边缘/暗角/色差/抖动都有各自的特征差异，不会互相冒充。

### ⚠ 升级到 v5 之后必须重新注入

v5 在保留结构体布局的同时，给 `KillEffectConfig.reserved` 赋予了边缘颜色语义，
并新增了 4 种 core shader 效果。如果游戏里那份还是旧 DLL，它会忽略颜色和新效果。
当前生产 DLL 为 `native\dist\GameMotionBlurHook_v6.dll`，
测试/注入时请使用这个文件；旧游戏进程需要完全退出后再注入。

---

## 7. 已知限制

* **只覆盖 OpenGL**。当前只做 Minecraft Java 版；其他图形 API（Direct3D / Vulkan 等）不支持。
  界面上会明确显示当前识别到的是哪条路径。
* **OpenGL 这条路的假设**：游戏通过 IAT 调用 `SwapBuffers/wglSwapBuffers`
  （GLFW、LWJGL2/3、SDL 都是这样）。如果某个游戏是运行时 `GetProcAddress` 拿到地址
  再存进自己的结构体里调用，就抓不到。
* **反作弊**：任何注入 + Hook 都可能被反作弊拦。带反作弊的联机游戏请自行判断风险，
  这类工具一般只适合单机 / 无 AC 的场景。
* **需要权限对等**：游戏以管理员身份运行的话，本程序也要用管理员身份启动，
  否则 `OpenProcess` 会被拒绝（界面会给出这条提示）。
* 每帧会多一次全屏绘制和一到两次全屏拷贝，开销很小但不是零。

## 8. 接下来可以加的

1. 多帧不同权重（三角/高斯核），现在是单帧指数累积；
2. 基于亮度的自适应强度（运动快的时候才加大拖影）；
3. 只在"画面确实变了"的时候混合（要用 compute 或降采样做差异检测）。
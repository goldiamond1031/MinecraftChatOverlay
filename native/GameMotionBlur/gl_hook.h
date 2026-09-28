#pragma once

namespace gmblur {

// OpenGL（Minecraft Java 那一类）的钩子。
// GL 的"出帧"函数 SwapBuffers/wglSwapBuffers 是**普通导出函数**，
// 每个调用方在 IAT 里各存一份地址，所以这里用的是：
//   1) 把每个模块 IAT 里指向 SwapBuffers / wglSwapBuffers 的槽位换成我们的函数；
//   2) 顺便把 gdi32/opengl32 导出表里的地址也换掉（管 GetProcAddress 的调用方）。
// 游戏之后才加载的 DLL（lwjgl、glfw 之类）由 HookPumpGL 定期重扫补上。
bool HookInstallGL();
void HookUninstallGL();
void HookPumpGL();

bool HookGlInstalled();
unsigned HookGlSwapCount();
unsigned HookGlBlendCount();

} // namespace gmblur
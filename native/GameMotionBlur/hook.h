#pragma once

namespace gmblur {

// OpenGL（Minecraft Java 版 / LWJGL / GLFW）出帧钩子的安装/卸载。
// 具体做法见 gl_hook.cpp：扫各模块 IAT，把 SwapBuffers / wglSwapBuffers
// 换成我们自己的函数，然后在出帧前做帧混合和击杀反馈。
bool HookInstall();

// 还原 IAT 槽位（游戏不用重启就能干净地退出这个功能）。
void HookUninstall();

// 由后台线程定期调用：处理控制端请求 + 回写状态。
void HookPump();

bool HookInstalled();

} // namespace gmblur

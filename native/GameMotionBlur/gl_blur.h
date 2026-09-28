#pragma once

#include <windows.h>

namespace gmblur {

// 每帧 SwapBuffers 时调用：在游戏自己的后台缓冲上做帧混合。
// 返回 true 表示这一帧确实做了混合。
bool GlBlurOnSwap(HDC hdc);

// 上下文/资源失效时清缓存。
void GlBlurReset(const wchar_t* reason);

void GlBlurShutdown();

// 给状态栏用（GL 路径自己记一份尺寸）。
unsigned GlBlurTargetWidth();
unsigned GlBlurTargetHeight();
unsigned GlBlurTargetFrames();
bool GlBlurSupported();

} // namespace gmblur
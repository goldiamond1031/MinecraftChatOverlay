// ============================================================================
//  hook.cpp —— OpenGL 出帧钩子的总调度
//
//  当前只支持 Minecraft Java 版 / OpenGL：
//    扫描各模块 IAT，把 SwapBuffers / wglSwapBuffers 换成我们自己的函数，
//    然后在每一帧出帧前做帧混合和击杀反馈。
// ============================================================================
#include "hook.h"
#include "gl_hook.h"
#include "gl_blur.h"
#include "common.h"
#include "control.h"
#include "log.h"

#include <windows.h>
#include <wchar.h>

namespace gmblur {

static volatile LONG s_installed = 0;

bool HookInstall()
{
    if (s_installed)
    {
        return true;
    }

    if (!HookInstallGL())
    {
        Log(L"OpenGL 钩子没装成功");
        return false;
    }

    InterlockedExchange(&s_installed, 1);
    Log(L"钩子安装完成：OpenGL=是");
    return true;
}

void HookUninstall()
{
    if (!s_installed)
    {
        return;
    }

    HookUninstallGL();
    InterlockedExchange(&s_installed, 0);
    Log(L"钩子已卸载，游戏回到原生的 SwapBuffers");
}

bool HookInstalled() { return s_installed != 0; }

// ---------------------------------------------------------------------------
void HookPump()
{
    ControlHeader* ctl = Control();
    if (!ctl)
    {
        return;
    }

    // 给"注入之后才加载"的模块补 IAT 补丁（lwjgl/glfw 之流）
    HookPumpGL();

    if ((ctl->flags & kFlagUnhook) && s_installed)
    {
        HookUninstall();
        ctl->flags &= ~(UINT)kFlagUnhook;
    }

    // 重新装钩子。
    // DLL 一旦 LoadLibrary 进进程，DllMain 就不会再跑；所以"卸载之后再注入一次"是没用的
    // （LoadLibrary 直接返回已有句柄）。这里给界面一个入口，让 DLL 自己把钩子装回去。
    if ((ctl->flags & kFlagReinstall) && !s_installed)
    {
        HookInstall();
        ctl->flags &= ~(UINT)kFlagReinstall;
    }

    static LARGE_INTEGER s_freq = {};
    static LARGE_INTEGER s_lastQpc = {};
    static LONG          s_lastSwaps = 0;

    LARGE_INTEGER now;
    if (!s_freq.QuadPart)
    {
        QueryPerformanceFrequency(&s_freq);
    }
    QueryPerformanceCounter(&now);

    const LONG swaps  = (LONG)HookGlSwapCount();
    const LONG blends = (LONG)HookGlBlendCount();

    float fps = 0.0f;
    if (s_lastQpc.QuadPart > 0 && s_freq.QuadPart > 0)
    {
        const double dt = (double)(now.QuadPart - s_lastQpc.QuadPart) / (double)s_freq.QuadPart;
        if (dt > 0.0001)
        {
            fps = (float)((double)(swaps - s_lastSwaps) / dt);
        }
    }

    s_lastQpc = now;
    s_lastSwaps = swaps;

    const uint32_t kind = (swaps > 0) ? kDeviceOpenGL : kDeviceUnknown;
    const uint32_t width = (swaps > 0) ? GlBlurTargetWidth() : 0;
    const uint32_t height = (swaps > 0) ? GlBlurTargetHeight() : 0;

    wchar_t status[160];
    if (!s_installed)
    {
        _snwprintf(status, 159, L"钩子未安装");
    }
    else if (swaps == 0)
    {
        _snwprintf(status, 159, L"钩子已挂上，等待游戏出帧（SwapBuffers 还没被调到）");
    }
    else
    {
        _snwprintf(status, 159, L"OpenGL 已接管：SwapBuffers %ld 次 · 混合 %ld 次 · 平均 %u 帧",
                   swaps, blends, (unsigned)GlBlurTargetFrames());
    }
    status[159] = 0;

    ControlPublishStats(kind, width, height, 0,
                        (uint32_t)swaps, (uint32_t)blends,
                        s_installed ? (blends > 0 ? kStateActive : kStateIdle) : kStateIdle,
                        0, status, fps);

    ctl->hookInstalled  = s_installed ? 1u : 0u;
    ctl->blendFrames    = GlBlurTargetFrames();
    ctl->qpcFrequency   = (uint64_t)s_freq.QuadPart;
    ctl->lastPresentQpc = (uint64_t)now.QuadPart;
}

} // namespace gmblur

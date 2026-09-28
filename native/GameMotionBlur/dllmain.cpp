// ============================================================================
//  dllmain.cpp —— 被注入进游戏进程后的入口
//
//  DllMain 里绝对不干活（loader lock），只起一个线程，剩下全在线程里做。
// ============================================================================
#include <windows.h>

#include "hook.h"
#include "control.h"
#include "log.h"

namespace {

DWORD WINAPI WorkerThread(LPVOID)
{
    // 让注入它的那只线程先跑完 LoadLibrary，避免几条线程一起抢 loader lock。
    Sleep(120);

    gmblur::LogInit();
    gmblur::Log(L"==================================================");
    gmblur::Log(L"GameMotionBlur hook 已载入 pid=%u", GetCurrentProcessId());

    if (!gmblur::ControlInit())
    {
        gmblur::Log(L"控制块创建失败，钩子仍然会装，但收不到参数");
    }

    gmblur::HookInstall();

    for (;;)
    {
        Sleep(250);
        gmblur::HookPump();
    }
}

} // namespace

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(module);

        HANDLE thread = CreateThread(nullptr, 0, WorkerThread, nullptr, 0, nullptr);
        if (thread)
        {
            CloseHandle(thread);
        }
    }
    else if (reason == DLL_PROCESS_DETACH)
    {
        // 进程要没了，不做任何可能死锁的事。
    }

    return TRUE;
}

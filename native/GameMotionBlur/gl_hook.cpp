// ============================================================================
//  gl_hook.cpp —— OpenGL 的 SwapBuffers / wglSwapBuffers 钩子
//
//  这里不用虚表钩子：GL 没有统一的 COM 虚表，
//  出帧就是一个普通导出函数，调用方在 IAT 里各记一份地址。所以：
//    * 扫描所有已加载模块的导入表，把指向 SwapBuffers( gdi32 ) 或
//      wglSwapBuffers( opengl32 ) 的槽位换成我们的函数；
//    * （刻意不动 opengl32/gdi32 的导出表：那是 32 位 RVA + 有些是 jmp 转发，
//      乱改很容易把函数代码本身覆盖掉，风险远大于收益）；
//    * 游戏启动后新加载的 DLL（lwjgl_glfw 等）由定期重扫补上。
//
//  Minecraft Java 的出帧路径：GLFW 的 win32_window.c 里调 SwapBuffers(dc)，
//  也就是 gdi32!SwapBuffers，所以上面这两条都命中了。
// ============================================================================
#include "gl_hook.h"
#include "gl_blur.h"
#include "log.h"

#include <windows.h>
#include <tlhelp32.h>
#include <string.h>
#include <wchar.h>

namespace gmblur {

typedef BOOL (WINAPI *SwapBuffersFn)(HDC);

static SwapBuffersFn s_origGdiSwap = nullptr;
static SwapBuffersFn s_origWglSwap = nullptr;

static volatile LONG s_glSwapCount = 0;
static volatile LONG s_glBlendCount = 0;
static volatile LONG s_glInstalled = 0;

struct PatchEntry
{
    void** slot;
    void*  original;
};

static PatchEntry g_patches[512];
static volatile LONG g_patchCount = 0;

// ---------------------------------------------------------------------------
static bool WritePointer(void** slot, void* value)
{
    if (!slot)
    {
        return false;
    }

    DWORD oldProtect = 0;
    if (!VirtualProtect(slot, sizeof(void*), PAGE_EXECUTE_READWRITE, &oldProtect))
    {
        return false;
    }

    *slot = value;

    DWORD ignored = 0;
    VirtualProtect(slot, sizeof(void*), oldProtect, &ignored);
    FlushInstructionCache(GetCurrentProcess(), slot, sizeof(void*));
    return true;
}

static bool PatchSlot(void** slot, void* replacement, void** original)
{
    if (!slot)
    {
        return false;
    }

    void* current = *slot;
    if (current == replacement)
    {
        return false;   // 已经是我们自己了
    }

    if (!WritePointer(slot, replacement))
    {
        return false;
    }

    if (original)
    {
        *original = current;
    }

    const LONG index = InterlockedIncrement(&g_patchCount) - 1;
    if (index >= 0 && index < (LONG)(sizeof(g_patches) / sizeof(g_patches[0])))
    {
        g_patches[index].slot = slot;
        g_patches[index].original = current;
    }
    else
    {
        InterlockedDecrement(&g_patchCount);
    }
    return true;
}

// ---------------------------------------------------------------------------
static void OnGlFrame(HDC hdc)
{
    InterlockedIncrement(&s_glSwapCount);

    if (GlBlurOnSwap(hdc))
    {
        InterlockedIncrement(&s_glBlendCount);
    }
}

static BOOL WINAPI SwapBuffersThunk(HDC hdc)
{
    OnGlFrame(hdc);

    if (!s_origGdiSwap)
    {
        s_origGdiSwap = (SwapBuffersFn)GetProcAddress(GetModuleHandleW(L"gdi32.dll"), "SwapBuffers");
    }
    return s_origGdiSwap ? s_origGdiSwap(hdc) : FALSE;
}

static BOOL WINAPI WglSwapBuffersThunk(HDC hdc)
{
    OnGlFrame(hdc);

    if (!s_origWglSwap)
    {
        s_origWglSwap = (SwapBuffersFn)GetProcAddress(GetModuleHandleW(L"opengl32.dll"), "wglSwapBuffers");
    }
    return s_origWglSwap ? s_origWglSwap(hdc) : FALSE;
}

// ---------------------------------------------------------------------------
static int PatchModuleIat(HMODULE module)
{
    if (!module)
    {
        return 0;
    }

    PIMAGE_DOS_HEADER dos = (PIMAGE_DOS_HEADER)module;
    if (dos->e_magic != IMAGE_DOS_SIGNATURE)
    {
        return 0;
    }

    PIMAGE_NT_HEADERS nt = (PIMAGE_NT_HEADERS)((BYTE*)module + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE)
    {
        return 0;
    }

    const IMAGE_DATA_DIRECTORY& importDir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (!importDir.VirtualAddress)
    {
        return 0;
    }

    int patched = 0;
    PIMAGE_IMPORT_DESCRIPTOR descriptor = (PIMAGE_IMPORT_DESCRIPTOR)((BYTE*)module + importDir.VirtualAddress);

    for (; descriptor->Name; ++descriptor)
    {
        const char* dllName = (const char*)((BYTE*)module + descriptor->Name);
        const bool isGdi = _stricmp(dllName, "gdi32.dll") == 0;
        const bool isGl = _stricmp(dllName, "opengl32.dll") == 0;
        if (!isGdi && !isGl)
        {
            continue;
        }

        PIMAGE_THUNK_DATA thunk = (PIMAGE_THUNK_DATA)((BYTE*)module + descriptor->FirstThunk);
        PIMAGE_THUNK_DATA lookup = descriptor->OriginalFirstThunk
            ? (PIMAGE_THUNK_DATA)((BYTE*)module + descriptor->OriginalFirstThunk)
            : thunk;

        for (; lookup->u1.AddressOfData; ++lookup, ++thunk)
        {
            if (lookup->u1.Ordinal & IMAGE_ORDINAL_FLAG)
            {
                continue;   // 按序号导入的，名字拿不到，跳过
            }

            const char* importName = (const char*)((BYTE*)module + lookup->u1.AddressOfData + sizeof(WORD));

            if (isGdi && strcmp(importName, "SwapBuffers") == 0)
            {
                if (PatchSlot((void**)&thunk->u1.Function, (void*)&SwapBuffersThunk, (void**)&s_origGdiSwap))
                {
                    ++patched;
                }
            }
            else if (isGl && strcmp(importName, "wglSwapBuffers") == 0)
            {
                if (PatchSlot((void**)&thunk->u1.Function, (void*)&WglSwapBuffersThunk, (void**)&s_origWglSwap))
                {
                    ++patched;
                }
            }
        }
    }

    return patched;
}

// ---------------------------------------------------------------------------
static int ScanAllModulesForGlCalls()
{
    int patched = 0;

    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, GetCurrentProcessId());
    if (snapshot == INVALID_HANDLE_VALUE)
    {
        Log(L"CreateToolhelp32Snapshot 失败 err=%u", GetLastError());
        return 0;
    }

    MODULEENTRY32W entry;
    memset(&entry, 0, sizeof(entry));
    entry.dwSize = sizeof(entry);

    if (Module32FirstW(snapshot, &entry))
    {
        do
        {
            patched += PatchModuleIat((HMODULE)entry.modBaseAddr);
        }
        while (Module32NextW(snapshot, &entry));
    }

    CloseHandle(snapshot);
    return patched;
}

// ---------------------------------------------------------------------------
bool HookInstallGL()
{
    if (s_glInstalled)
    {
        return true;
    }

    // 逐个模块扫导入表：谁 import 了 SwapBuffers / wglSwapBuffers，就把谁 IAT 里那一格换掉。
    // 原始地址直接从被替换掉的那一格读出来，最可靠。
    const int patched = ScanAllModulesForGlCalls();

    if (!s_origGdiSwap && !s_origWglSwap)
    {
        Log(L"没找到任何 SwapBuffers/wglSwapBuffers 导入点（这个进程大概不是 OpenGL 程序）");
        return false;
    }

    InterlockedExchange(&s_glInstalled, 1);
    Log(L"OpenGL 钩子已装上：本次补丁 %d 处（gdi=%p wgl=%p）",
        patched, (void*)s_origGdiSwap, (void*)s_origWglSwap);
    return true;
}

void HookUninstallGL()
{
    if (!s_glInstalled)
    {
        return;
    }

    const LONG count = InterlockedCompareExchange(&g_patchCount, 0, 0);
    for (LONG i = count - 1; i >= 0; --i)
    {
        if (g_patches[i].slot)
        {
            WritePointer(g_patches[i].slot, g_patches[i].original);
            g_patches[i].slot = nullptr;
        }
    }

    InterlockedExchange(&g_patchCount, 0);
    InterlockedExchange(&s_glInstalled, 0);
    Log(L"OpenGL 钩子已卸载，SwapBuffers 恢复原样");
}

void HookPumpGL()
{
    if (!s_glInstalled)
    {
        return;
    }

    // 游戏可能在注入之后才加载 lwjgl/glfw 那些 DLL，定期补扫一遍。
    static DWORD lastScan = 0;
    const DWORD now = GetTickCount();
    if (now - lastScan < 2000)
    {
        return;
    }
    lastScan = now;

    const int added = ScanAllModulesForGlCalls();
    if (added > 0)
    {
        Log(L"OpenGL：给 %d 个新出现的调用点补上了补丁", added);
    }
}

bool HookGlInstalled() { return s_glInstalled != 0; }
unsigned HookGlSwapCount() { return (unsigned)InterlockedCompareExchange(&s_glSwapCount, 0, 0); }
unsigned HookGlBlendCount() { return (unsigned)InterlockedCompareExchange(&s_glBlendCount, 0, 0); }

} // namespace gmblur
// ============================================================================
//  log.cpp —— 极简日志（注入到游戏里，一切从简：不开线程、不分配堆）
// ============================================================================
#include "log.h"

#include <windows.h>
#include <stdio.h>
#include <stdarg.h>
#include <wchar.h>

namespace gmblur {

static wchar_t  g_path[MAX_PATH] = {};
static FILE*    g_file = nullptr;
static CRITICAL_SECTION g_lock;
static bool     g_lockReady = false;
static bool     g_failed = false;
static wchar_t  g_line[2048];

// 时间戳精确到毫秒；不用 CRT 的 time 函数，避免额外依赖。
static void Stamp(wchar_t* buf, size_t count)
{
    SYSTEMTIME st;
    GetLocalTime(&st);
    _snwprintf(buf, count, L"[%02u:%02u:%02u.%03u] ",
               st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);
}

void LogInit()
{
    if (g_file || g_failed)
    {
        return;
    }

    InitializeCriticalSection(&g_lock);
    g_lockReady = true;

    // 用"这个函数自己的地址"反查 DLL 模块句柄，比拿进程 exe 路径可靠。
    HMODULE self = nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                       (LPCWSTR)(const void*)&LogInit, &self);

    wchar_t path[MAX_PATH] = {};

    // 手动映射（不走 LoadLibrary）时，这个模块不在 loader 的模块表里，
    // GetModuleFileNameW 会返回 0 —— 那就退到临时目录，别让日志整个哑掉。
    if (GetModuleFileNameW(self, path, MAX_PATH) == 0)
    {
        if (GetTempPathW(MAX_PATH, path) == 0)
        {
            wcscpy_s(path, MAX_PATH, L"C:\\");
        }
    }

    // 取 DLL 所在目录
    for (int i = (int)wcslen(path) - 1; i >= 0; --i)
    {
        if (path[i] == L'\\' || path[i] == L'/')
        {
            path[i] = 0;
            break;
        }
    }

    _snwprintf(g_path, MAX_PATH, L"%s\\gmb_hook_%u.log", path, GetCurrentProcessId());

    g_file = _wfopen(g_path, L"wb");
    if (!g_file)
    {
        g_failed = true;
        return;
    }

    // 带 BOM，记事本能正确识别 UTF-8
    fwrite("\xEF\xBB\xBF", 1, 3, g_file);
    fflush(g_file);
}

const wchar_t* LogPath() { return g_path; }
bool LogHasError() { return g_failed; }

static void Write(const wchar_t* text)
{
    if (!g_file)
    {
        return;
    }

    // 转成 UTF-8 再写，避免不同代码页下日志乱码。
    char utf8[4096];
    int n = WideCharToMultiByte(CP_UTF8, 0, text, -1, utf8, (int)sizeof(utf8) - 1, nullptr, nullptr);
    if (n <= 0)
    {
        return;
    }

    if (g_lockReady)
    {
        EnterCriticalSection(&g_lock);
    }

    if (g_file)
    {
        fwrite(utf8, 1, (size_t)(n - 1), g_file);
        fwrite("\r\n", 1, 2, g_file);
        fflush(g_file);
    }

    if (g_lockReady)
    {
        LeaveCriticalSection(&g_lock);
    }
}

void Log(const wchar_t* fmt, ...)
{
    if (!g_file)
    {
        return;
    }

    wchar_t body[1800];
    va_list args;
    va_start(args, fmt);
    _vsnwprintf(body, 1799, fmt, args);
    va_end(args);
    body[1799] = 0;

    wchar_t head[64];
    Stamp(head, 63);

    _snwprintf(g_line, 2047, L"%s%s", head, body);
    Write(g_line);
}

void LogLine(const wchar_t* text)
{
    if (!g_file)
    {
        return;
    }

    wchar_t head[64];
    Stamp(head, 63);
    _snwprintf(g_line, 2047, L"%s%s", head, text);
    Write(g_line);
}

} // namespace gmblur

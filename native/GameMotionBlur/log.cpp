// ============================================================================
//  log.cpp - 极简日志（注入到游戏里，一切从简：不开线程、不分配堆）
//
//  落点：宿主（MCO 界面 / gmblur CLI）在注入之前，把目录写进控制块里那段输出目录
//  字符串，DLL 起来、ControlInit() 之后调 LogSetDirectory() 读过来；
//  没给（老宿主 / 控制块没建起来 / 目录写不进去）就退回老位置：进程 exe 所在目录。
//
//  为什么要先攒后写：DLL 一进去的那几行（已载入 pid=... 、控制块初始化结果）
//  比后面的日志更值钱，可那时候还不知道往哪写，所以先攒在内存里，
//  开文件的时候整段补写进去，一行都不丢。
// ============================================================================
#include "log.h"

#include <windows.h>
#include <stdio.h>
#include <stdarg.h>
#include <wchar.h>
#include <string.h>

namespace gmblur {

static wchar_t  g_path[MAX_PATH] = {};
static FILE*    g_file = nullptr;
static CRITICAL_SECTION g_lock;
static bool     g_lockReady = false;
static bool     g_failed = false;
static wchar_t  g_line[2048];

/// 日志文件还没开之前，先攒在这里（静态缓冲，不动堆）。
static char     g_pending[8192];
static size_t   g_pendingLen = 0;
static unsigned g_pendingDropped = 0;

// 时间戳精确到毫秒；不用 CRT 的 time 函数，避免额外依赖。
static void Stamp(wchar_t* buf, size_t count)
{
    SYSTEMTIME st;
    GetLocalTime(&st);
    _snwprintf(buf, count, L"[%02u:%02u:%02u.%03u] ",
               st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);
}

/// 逐级建目录：CreateDirectoryW 不会自动建中间那几层。
static bool EnsureDirectory(const wchar_t* directory)
{
    if (!directory || !directory[0])
    {
        return false;
    }

    if (GetFileAttributesW(directory) != INVALID_FILE_ATTRIBUTES)
    {
        return true;
    }

    const size_t length = wcslen(directory);
    if (length == 0 || length >= MAX_PATH)
    {
        return false;
    }

    wchar_t temp[MAX_PATH] = {};
    wmemcpy(temp, directory, length);
    temp[length] = 0;

    for (size_t i = 0; i < length; ++i)
    {
        if (temp[i] != L'\\' && temp[i] != L'/')
        {
            continue;
        }

        const wchar_t saved = temp[i];
        temp[i] = 0;

        // "C:" 这种只是盘符，不能单独建
        const size_t partial = wcslen(temp);
        if (partial > 2 || (partial == 2 && temp[1] != L':'))
        {
            CreateDirectoryW(temp, nullptr);
        }

        temp[i] = saved;
    }

    CreateDirectoryW(temp, nullptr);
    return GetFileAttributesW(directory) != INVALID_FILE_ATTRIBUTES;
}

/// 默认落点：进程 exe 所在目录（真拿不到就退 %TEMP%）。
/// 手动映射的模块不在 loader 的模块表里，反查它自己的路径是拿不到的。
static void DefaultDirectory(wchar_t* folder, size_t count)
{
    wchar_t path[MAX_PATH] = {};

    HMODULE self = nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                       (LPCWSTR)(const void*)&LogInit, &self);

    if (GetModuleFileNameW(self, path, MAX_PATH) == 0)
    {
        if (GetTempPathW(MAX_PATH, path) == 0)
        {
            wcscpy_s(path, MAX_PATH, L"C:\\");
        }
    }

    for (int i = (int)wcslen(path) - 1; i >= 0; --i)
    {
        if (path[i] == L'\\' || path[i] == L'/')
        {
            path[i] = 0;
            break;
        }
    }

    wcsncpy(folder, path, count - 1);
    folder[count - 1] = 0;
}

/// 把攒着的几行补写进刚开的文件（调用方已经拿着锁）。
static void FlushPending()
{
    if (g_pendingDropped)
    {
        char note[160];
        const int n = _snprintf(note, sizeof(note) - 1,
                                "[%u 行在日志文件打开之前被丢弃（缓冲区满）]\r\n",
                                g_pendingDropped);
        if (n > 0)
        {
            fwrite(note, 1, (size_t)n, g_file);
        }
        g_pendingDropped = 0;
    }

    if (g_pendingLen)
    {
        fwrite(g_pending, 1, g_pendingLen, g_file);
        g_pendingLen = 0;
    }

    fflush(g_file);
}

/// 真正把文件开出来（调用方已经拿着锁）。失败返回 false。
static bool OpenFile(const wchar_t* folder)
{
    if (!folder || !folder[0])
    {
        return false;
    }

    _snwprintf(g_path, MAX_PATH, L"%s\\gmb_hook_%u.log", folder, GetCurrentProcessId());
    g_file = _wfopen(g_path, L"wb");
    if (!g_file)
    {
        g_path[0] = 0;
        return false;
    }

    // 带 BOM，记事本能正确识别 UTF-8
    fwrite("\xEF\xBB\xBF", 1, 3, g_file);

    // 开文件之前记的那几行补上
    FlushPending();
    return true;
}

void LogInit()
{
    if (g_lockReady)
    {
        return;
    }

    InitializeCriticalSection(&g_lock);
    g_lockReady = true;
}

void LogSetDirectory(const wchar_t* directory)
{
    if (g_file || g_failed)
    {
        return;
    }

    if (!g_lockReady)
    {
        LogInit();
    }

    wchar_t folder[MAX_PATH] = {};
    const bool fromHost = (directory != nullptr && directory[0] != 0);

    if (fromHost)
    {
        wcsncpy(folder, directory, MAX_PATH - 1);
        folder[MAX_PATH - 1] = 0;

        // 去掉末尾的分隔符，免得拼出 ...\logs\\gmb_hook_1.log
        size_t length = wcslen(folder);
        while (length > 3 && (folder[length - 1] == L'\\' || folder[length - 1] == L'/'))
        {
            folder[--length] = 0;
        }
    }
    else
    {
        DefaultDirectory(folder, MAX_PATH);
    }

    if (g_lockReady)
    {
        EnterCriticalSection(&g_lock);
    }

    bool ok = EnsureDirectory(folder) && OpenFile(folder);

    if (!ok && fromHost)
    {
        // 宿主给的目录用不了（建不出来 / 只读）：退回游戏目录，至少日志还在
        DefaultDirectory(folder, MAX_PATH);
        ok = EnsureDirectory(folder) && OpenFile(folder);
    }

    if (!ok)
    {
        g_failed = true;
    }

    if (g_lockReady)
    {
        LeaveCriticalSection(&g_lock);
    }
}

static void Write(const wchar_t* text)
{
    // 转成 UTF-8 再写，避免不同代码页下日志乱码。
    char utf8[4096];
    const int n = WideCharToMultiByte(CP_UTF8, 0, text, -1, utf8, (int)sizeof(utf8) - 1, nullptr, nullptr);
    if (n <= 0)
    {
        return;
    }

    const size_t length = (size_t)(n - 1);   // 末尾那个 0 不算

    if (g_lockReady)
    {
        EnterCriticalSection(&g_lock);
    }

    if (g_file)
    {
        fwrite(utf8, 1, length, g_file);
        fwrite("\r\n", 1, 2, g_file);
        fflush(g_file);
    }
    else if (!g_failed)
    {
        // 日志文件还没开（在等宿主给目录）：先攒着，开文件的时候整段补写
        if (g_pendingLen + length + 2 <= sizeof(g_pending))
        {
            memcpy(g_pending + g_pendingLen, utf8, length);
            g_pendingLen += length;
            g_pending[g_pendingLen++] = '\r';
            g_pending[g_pendingLen++] = '\n';
        }
        else
        {
            ++g_pendingDropped;
        }
    }

    if (g_lockReady)
    {
        LeaveCriticalSection(&g_lock);
    }
}

void Log(const wchar_t* fmt, ...)
{
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
    wchar_t head[64];
    Stamp(head, 63);
    _snwprintf(g_line, 2047, L"%s%s", head, text);
    Write(g_line);
}

const wchar_t* LogPath() { return g_path; }
bool LogHasError() { return g_failed; }

} // namespace gmblur
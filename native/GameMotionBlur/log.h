#pragma once

namespace gmblur {

/// 把日志追加写到 <目标进程 exe 所在目录>\gmb_hook_<pid>.log。
/// 之所以落在那个目录而不是 %TEMP%：界面上就写着"看游戏目录里的 gmb_hook_*.log"，用户能直接点开看。
/// ⚠ 手动映射的模块不在 loader 的模块表里，"DLL 自己的路径"反查不出来 —— 实测拿到的是进程 exe 的
///   路径（不是 0），所以落点就是游戏目录；真拿不到时才退到 %TEMP%。
void LogInit();
void Log(const wchar_t* fmt, ...);
void LogLine(const wchar_t* text);
const wchar_t* LogPath();
bool LogHasError();

} // namespace gmblur
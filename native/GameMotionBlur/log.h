#pragma once

namespace gmblur {

/// 把日志追加写到 <DLL 所在目录>\gmb_hook_<pid>.log。
/// 之所以放在 DLL 旁边而不是 %TEMP%：用户从界面上就能直接点开看。
void LogInit();
void Log(const wchar_t* fmt, ...);
void LogLine(const wchar_t* text);
const wchar_t* LogPath();
bool LogHasError();

} // namespace gmblur
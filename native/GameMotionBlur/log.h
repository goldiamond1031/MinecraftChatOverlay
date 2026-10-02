#pragma once

namespace gmblur {

/// 极简日志的落点规则：
///   1. 宿主（MCO 界面 / gmblur CLI）在注入之前，把目录写进控制块里那段 "输出目录" 字符串
///      （和导出帧共用，见 common.h 的 dumpDirOffset）；
///   2. DLL 起来、ControlInit() 之后调 LogSetDirectory() 把目录读过来，
///      目录不存在就新建，文件名 gmb_hook_<pid>.log；
///   3. 没给（老宿主 / 控制块没建起来 / 目录写不进去）就退回默认位置：进程 exe 所在目录。
///      注意：手动映射的模块不在 loader 的模块表里，反查不到 "DLL 自己的路径"，
///      拿到的总是进程 exe 的路径；真拿不到时才退到 %TEMP%。
///
/// 调用顺序：LogInit() -> 随便 Log(...)（文件还没开，先攒在内存里，一行不丢）
///          -> ControlInit() -> LogSetDirectory(宿主给的目录)  <- 这时才真正建文件。
void LogInit();

/// 指定日志目录（空 = 用默认位置）。目录不存在会新建；只生效一次。
void LogSetDirectory(const wchar_t* directory);

void Log(const wchar_t* fmt, ...);
void LogLine(const wchar_t* text);
const wchar_t* LogPath();
bool LogHasError();

} // namespace gmblur
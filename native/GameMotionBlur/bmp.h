#pragma once

#include <windows.h>

namespace gmblur {

// 把一段 32bpp 像素写成一个 BMP 文件（调试/导出帧用）。
//   data            像素数据
//   pitch           每行字节数
//   width/height    尺寸
//   swapRb          true = 输入是 RGBA 顺序，写入前换成 BGRA（BMP 要 BGRA）
//   sourceIsTopDown true = 输入第一行是画面顶部，false = 最后一行是顶部（OpenGL）
void WriteBmpFile(const wchar_t* path, const BYTE* data, UINT pitch, UINT width, UINT height,
                  bool swapRb, bool sourceIsTopDown);

} // namespace gmblur
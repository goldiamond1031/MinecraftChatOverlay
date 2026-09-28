// ============================================================================
//  bmp.cpp —— 极简 BMP 写出（OpenGL 导出路径用）
//
//  BMP 的行序是"从下往上"；OpenGL 的 glReadPixels 也是"从下往上"，
//  这里保留一个参数以便兼容其他来源。
// ============================================================================
#include "bmp.h"

#include <stdio.h>
#include <string.h>

namespace gmblur {

void WriteBmpFile(const wchar_t* path, const BYTE* data, UINT pitch, UINT width, UINT height,
                  bool swapRb, bool sourceIsTopDown)
{
    if (!path || !data || width == 0 || height == 0 || width > 8192)
    {
        return;
    }

    FILE* file = _wfopen(path, L"wb");
    if (!file)
    {
        return;
    }

    const UINT rowBytes = width * 4;
    const UINT imageBytes = rowBytes * height;
    const UINT fileSize = 54 + imageBytes;

    BYTE header[54];
    memset(header, 0, sizeof(header));
    header[0] = 'B';
    header[1] = 'M';
    memcpy(header + 2, &fileSize, 4);
    UINT dataOffset = 54;
    memcpy(header + 10, &dataOffset, 4);
    UINT infoSize = 40;
    memcpy(header + 14, &infoSize, 4);
    INT32 signedWidth = (INT32)width;
    INT32 signedHeight = (INT32)height;
    memcpy(header + 18, &signedWidth, 4);
    memcpy(header + 22, &signedHeight, 4);
    WORD planes = 1;
    memcpy(header + 26, &planes, 2);
    WORD bitsPerPixel = 32;
    memcpy(header + 28, &bitsPerPixel, 2);
    UINT imageSize = imageBytes;
    memcpy(header + 34, &imageSize, 4);

    fwrite(header, 1, 54, file);

    static BYTE rowBuffer[8192 * 4];

    for (UINT outRow = 0; outRow < height; ++outRow)
    {
        // BMP 第 0 行是画面最下面
        const UINT srcRow = sourceIsTopDown ? (height - 1 - outRow) : outRow;
        const BYTE* src = data + (size_t)srcRow * pitch;

        if (swapRb)
        {
            for (UINT x = 0; x < width; ++x)
            {
                rowBuffer[x * 4 + 0] = src[x * 4 + 2];
                rowBuffer[x * 4 + 1] = src[x * 4 + 1];
                rowBuffer[x * 4 + 2] = src[x * 4 + 0];
                rowBuffer[x * 4 + 3] = src[x * 4 + 3];
            }
            fwrite(rowBuffer, 1, rowBytes, file);
        }
        else
        {
            fwrite(src, 1, rowBytes, file);
        }
    }

    fclose(file);
}

} // namespace gmblur
using System;
using System.Collections.Generic;
using System.Text;

namespace MinecraftChatOverlay.Services;

/// <summary>
/// 极简 protobuf 读取器。
/// B站部分弹幕流命令（如 INTERACT_WORD_V2）把数据放在 base64 的 protobuf 里，
/// 这里只实现读取需要的字段，不引入 protobuf 依赖。
/// </summary>
internal static class ProtoReader
{
    /// <summary>读取一个 varint 字段。失败返回 false。</summary>
    public static bool TryReadVarint(ReadOnlySpan<byte> data, ref int offset, out long value)
    {
        value = 0;
        var shift = 0;
        while (offset < data.Length)
        {
            var b = data[offset++];
            value |= (long)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return true;
            }

            shift += 7;
            if (shift > 63)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// 按字段号取值。返回字典：字段号 -> 值（varint 为 long，字符串为 string）。
    /// 遇到无法识别的 wire type 就停止（容错，不抛异常）。
    /// </summary>
    public static Dictionary<int, object> ReadFields(ReadOnlySpan<byte> data)
    {
        var result = new Dictionary<int, object>();
        var offset = 0;

        while (offset < data.Length)
        {
            if (!TryReadVarint(data, ref offset, out var key))
            {
                break;
            }

            var fieldNumber = (int)(key >> 3);
            var wireType = (int)(key & 0x07);
            if (fieldNumber <= 0)
            {
                break;
            }

            switch (wireType)
            {
                case 0: // varint
                    if (!TryReadVarint(data, ref offset, out var varintValue))
                    {
                        return result;
                    }

                    result[fieldNumber] = varintValue;
                    break;

                case 1: // 64-bit
                    if (offset + 8 > data.Length)
                    {
                        return result;
                    }

                    offset += 8;
                    break;

                case 2: // length-delimited
                    if (!TryReadVarint(data, ref offset, out var length) || length < 0 || offset + length > data.Length)
                    {
                        return result;
                    }

                    var slice = data.Slice(offset, (int)length);
                    result[fieldNumber] = DecodeString(slice);
                    offset += (int)length;
                    break;

                case 5: // 32-bit
                    if (offset + 4 > data.Length)
                    {
                        return result;
                    }

                    offset += 4;
                    break;

                default:
                    return result;
            }
        }

        return result;
    }

    /// <summary>取出某个 length-delimited 字段的原始字节（用于读取嵌套消息）。找不到返回 null。</summary>
    public static byte[]? GetFieldBytes(ReadOnlySpan<byte> data, int targetFieldNumber)
    {
        var offset = 0;
        while (offset < data.Length)
        {
            if (!TryReadVarint(data, ref offset, out var key))
            {
                return null;
            }

            var fieldNumber = (int)(key >> 3);
            var wireType = (int)(key & 0x07);
            if (fieldNumber <= 0)
            {
                return null;
            }

            switch (wireType)
            {
                case 0:
                    if (!TryReadVarint(data, ref offset, out _))
                    {
                        return null;
                    }

                    break;

                case 1:
                    if (offset + 8 > data.Length)
                    {
                        return null;
                    }

                    offset += 8;
                    break;

                case 2:
                    if (!TryReadVarint(data, ref offset, out var length) || length < 0 || offset + length > data.Length)
                    {
                        return null;
                    }

                    if (fieldNumber == targetFieldNumber)
                    {
                        return data.Slice(offset, (int)length).ToArray();
                    }

                    offset += (int)length;
                    break;

                case 5:
                    if (offset + 4 > data.Length)
                    {
                        return null;
                    }

                    offset += 4;
                    break;

                default:
                    return null;
            }
        }

        return null;
    }

    private static string DecodeString(ReadOnlySpan<byte> slice)
    {
        try
        {
            var text = Encoding.UTF8.GetString(slice);
            // 只接受看起来像正常文本的内容，否则认为是嵌套结构，不做解码。
            foreach (var ch in text)
            {
                if (ch < 0x20 && ch != '\t')
                {
                    return "";
                }
            }

            return text;
        }
        catch
        {
            return "";
        }
    }
}

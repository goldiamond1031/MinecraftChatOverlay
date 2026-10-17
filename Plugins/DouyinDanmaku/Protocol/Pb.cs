using System.Buffers.Binary;

namespace DouyinDanmaku.Protocol;

/// <summary>
/// protobuf wire format 读取器。抖音推流是标准 proto3，只需 varint / length-delimited / fixed 四种。
/// 手写而非引库：插件必须零第三方 NuGet 依赖（宿主安装目录只读，且不引入额外 dll）。
/// </summary>
internal sealed class Pb
{
    private readonly byte[] _buf;
    private int _pos;
    private readonly int _end;

    public Pb(byte[] buf, int pos = 0, int end = -1)
    {
        _buf = buf;
        _pos = pos;
        _end = end < 0 ? buf.Length : end;
    }

    public bool AtEnd => _pos >= _end;

    /// <summary>读下一个字段头。返回 false 表示已到末尾。</summary>
    public bool Next(out int field, out int wire)
    {
        field = 0;
        wire = 0;
        if (AtEnd)
        {
            return false;
        }

        ulong key = Varint();
        field = (int)(key >> 3);
        wire = (int)(key & 0x07);
        return true;
    }

    public ulong Varint()
    {
        ulong result = 0;
        int shift = 0;
        while (_pos < _end)
        {
            byte b = _buf[_pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                break;
            }

            shift += 7;
            if (shift > 63)
            {
                break;
            }
        }

        return result;
    }

    public uint Fixed32()
    {
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(_buf.AsSpan(_pos, 4));
        _pos += 4;
        return v;
    }

    public ulong Fixed64()
    {
        ulong v = BinaryPrimitives.ReadUInt64LittleEndian(_buf.AsSpan(_pos, 8));
        _pos += 8;
        return v;
    }

    public byte[] Bytes()
    {
        int len = (int)Varint();
        if (len < 0 || _pos + len > _end)
        {
            _pos = _end;
            return Array.Empty<byte>();
        }

        var r = new byte[len];
        Array.Copy(_buf, _pos, r, 0, len);
        _pos += len;
        return r;
    }

    public string Str() => System.Text.Encoding.UTF8.GetString(Bytes());

    public Pb Sub()
    {
        int len = (int)Varint();
        if (len < 0 || _pos + len > _end)
        {
            _pos = _end;
            return new Pb(Array.Empty<byte>());
        }

        var r = new Pb(_buf, _pos, _pos + len);
        _pos += len;
        return r;
    }

    public void Skip(int wire)
    {
        switch (wire)
        {
            case 0: Varint(); break;
            case 1: Fixed64(); break;
            case 2: Bytes(); break;
            case 5: Fixed32(); break;
        }
    }
}

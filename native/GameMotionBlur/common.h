// ============================================================================
//  common.h —— 注入 DLL 与 C# 控制端共享的内存布局 / 常量
//
//  两边（C++ 和 C#）各自声明一份一模一样的结构体，靠 magic + version +
//  structSize + probe 数组互相确认"我们说的确实是同一个东西"。
//  任何一边改字段，都必须同时改另一份，并把 kVersion 加一。
//
//  为什么不用"结构体里带 wchar 数组"这种写法：
//  C# 那边带 string 字段的结构体不是 blittable 的，Marshal.SizeOf /
//  MemoryMappedViewAccessor 都会直接拒绝。所以这里把字符串区单独放在
//  头部之后，用固定偏移访问 —— 头部是纯数值，两边都能直接内存映射读写。
// ============================================================================
#pragma once

#include <stdint.h>
#include <stddef.h>

namespace gmblur {

// 共享内存名（Local\ 前缀只在本会话可见，不同用户互不干扰）。
// 想同时跑两份（或者做隔离测试）时，把环境变量 GMBLUR_MAP 设成别的名字即可。
const wchar_t* MapName();

constexpr uint32_t kMagic   = 0x424C4D47u;  // 'GMLB'
constexpr uint32_t kVersion = 6;            // v6：移除径向模糊（故障撕裂 10→9）

// 整块共享内存的大小（头部 + 两段定长字符串区）。
constexpr uint32_t kMappingSize = 4096;

#pragma pack(push, 8)

// 一个击杀反馈效果配置（多效果共存用）。
// kind 对应 KillEffectKind；strength 允许到 5.0（界面上的 500%）。
struct KillEffectConfig
{
    uint32_t kind;
    float    strength;
    uint32_t enabled;
    // 边缘脉冲颜色：低 24 位为 RRGGBB；其余效果忽略。
    uint32_t reserved;
};

constexpr uint32_t kMaxKillEffects = 8;

// 控制块头部：纯数值，C# 端有逐字段对应的结构体。
struct ControlHeader
{
    // ---------- 头部 ----------
    uint32_t magic;         // 必须等于 kMagic
    uint32_t version;       // 必须等于 kVersion
    uint32_t structSize;    // 必须等于 sizeof(ControlHeader)
    uint32_t seq;           // 控制端每次改参数加一

    // ---------- 控制端（C#）写入 ----------
    uint32_t enable;        // 0 = 关（只保留钩子，不做混合），1 = 开
    float    strength;      // 0.0 ~ 0.95：历史帧权重，越大拖影越长
    uint32_t flags;         // gmblur::Flags 位组合
    uint32_t dumpFrames;    // flags 带 kFlagDumpFrames 时，要导出多少帧

    // ---------- 钩子端（DLL）写入，控制端只读做状态显示 ----------
    uint32_t hookInstalled; // 1 = OpenGL 出帧钩子已装上
    uint32_t deviceKind;    // gmblur::DeviceKind
    uint32_t presentCount;  // 被 hook 到的出帧次数（OpenGL: SwapBuffers）
    uint32_t blendCount;    // 真正做了帧混合的次数
    uint32_t width;         // 当前画面尺寸
    uint32_t height;
    uint32_t format;        // 图形格式（OpenGL 路径暂为 0）
    uint32_t lastHresult;   // 最近一次失败
    uint32_t state;         // gmblur::State
    uint32_t probe[8];      // 布局自检（见 control.cpp）
    uint64_t lastPresentQpc;   // 最后一次出帧的 QPC（字段名保留兼容）
    uint64_t qpcFrequency;
    uint32_t fpsMilli;      // 实测帧率 x 1000
    uint32_t blendFrames;   // 当前实际平均了几帧（2~8）；0 = 没在混合

    // ---------- 字符串区的位置（让 C# 端知道去哪读写） ----------
    uint32_t statusChars;   // 状态字符串的字符数
    uint32_t dumpDirChars;  // 导出目录的字符数
    uint32_t statusOffset;  // 相对映射起点的字节偏移
    uint32_t dumpDirOffset;

    // ==========================================================================
    //  击杀反馈事件通道（v3 新增）
    //
    //  为什么用"序号"而不是"标志位"：
    //  标志位只能表达"有一个待处理事件"，连杀时会被合并掉；序号天然能表达
    //  "又来了一个"。钩子端记住处理过的最大序号，发现变大了就说明有新事件。
    //  按 uint32 回绕（约 49 天满一圈）也仍然正确，因为只比较"相等与否"。
    // ==========================================================================

    // ---------- 控制端（C#）写入 ----------
    uint32_t eventSeq;        // 每请求一次 +1
    uint32_t eventKind;       // 1 = 缩放脉冲；0 = 无效（钩子端忽略）
    uint32_t eventDurationMs; // 这次效果持续多少毫秒
    float    eventStrength;   // 0 ~ 1

    // ---------- 钩子端（DLL）写入，控制端只读做状态显示 ----------
    uint32_t eventHandledSeq; // 已处理到哪个序号
    uint32_t eventActiveMs;   // 当前效果还剩多少毫秒（0 = 没在放）
    uint32_t eventPlayCount;  // 累计放过几次
    uint32_t eventZoomMilli;  // 当前实际缩放 x1000（诊断用，1000 = 原样）

    // ==========================================================================
    //  多效果配置（v4 新增）
    //  配置区紧跟在 ControlHeader 之后，C# 端按固定偏移逐个写入。
    // ==========================================================================
    uint32_t effectConfigCount;     // 有效配置条数（<= kMaxKillEffects）
    uint32_t effectConfigVersion;   // 写入配置时的协议版本，诊断用
};

#pragma pack(pop)

constexpr uint32_t kStatusChars   = 160;
constexpr uint32_t kDumpDirChars  = 260;
constexpr uint32_t kEffectConfigOffset = (uint32_t)sizeof(ControlHeader);
constexpr uint32_t kEffectConfigBytes  = kMaxKillEffects * (uint32_t)sizeof(KillEffectConfig);
constexpr uint32_t kStatusOffset  = kEffectConfigOffset + kEffectConfigBytes;
constexpr uint32_t kDumpDirOffset = kStatusOffset + kStatusChars * 2;
constexpr uint32_t kStringsEnd    = kDumpDirOffset + kDumpDirChars * 2;

static_assert(kStringsEnd <= kMappingSize, "字符串区放不进共享内存里");

inline wchar_t* HeaderStatus(ControlHeader* h)
{
    return (wchar_t*)((unsigned char*)h + h->statusOffset);
}

inline wchar_t* HeaderDumpDir(ControlHeader* h)
{
    return (wchar_t*)((unsigned char*)h + h->dumpDirOffset);
}

inline KillEffectConfig* HeaderEffectConfigs(ControlHeader* h)
{
    return (KillEffectConfig*)((unsigned char*)h + kEffectConfigOffset);
}

enum Flags : uint32_t
{
    kFlagDumpFrames   = 1u << 0,  // 导出若干帧到 dumpDir（调试/验证用）
    kFlagUnhook       = 1u << 1,  // 要求卸载钩子（把 IAT 槽位还原）
    kFlagResetHistory = 1u << 2,  // 清空历史帧（例如刚开启、或切场景后）
    kFlagReinstall    = 1u << 3, // 要求重新装钩子（DLL 已在本进程里、但钩子被卸载过时用）
};

enum DeviceKind : uint32_t
{
    kDeviceUnknown = 0,
    kDeviceOpenGL  = 3,  // Minecraft Java / LWJGL：走 SwapBuffers 钩子
};

enum State : uint32_t
{
    kStateIdle   = 0,
    kStateActive = 1,
    kStateError  = 2,
};

} // namespace gmblur
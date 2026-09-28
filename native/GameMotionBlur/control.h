#pragma once

#include "common.h"

namespace gmblur {

/// 映射/创建共享内存控制块。失败返回 false（此时退化成"只注入不混合"）。
bool ControlInit();
ControlHeader* Control();

/// 钩子端把自己的字段写进控制块。
void ControlPublishStats(uint32_t deviceKind, uint32_t width, uint32_t height, uint32_t format,
                         uint32_t presentCount, uint32_t blendCount, uint32_t state,
                         uint32_t lastHresult, const wchar_t* status, float fps);

/// 把 probe 数组填好，让 C# 端能自检结构体布局。
void ControlWriteProbes();

/// 状态字符串（可直接 wcsncpy 到自己的缓冲区）。
const wchar_t* ControlDumpDir();

// ============================================================================
//  击杀反馈
// ============================================================================

/// 效果种类（对应 ControlHeader::eventKind / KillEffectConfig::kind）。
enum KillEffectKind : uint32_t
{
    kKillEffectNone          = 0,

    /// 缩放脉冲：整幅画面放大一点点再弹回。
    kKillEffectZoomPunch     = 1,

    /// 边缘脉冲：四周亮起一圈光带。
    kKillEffectEdgePulse     = 2,

    /// 暗角脉冲：四周短暂压暗。
    kKillEffectVignettePulse = 3,

    /// 色差分离：红蓝沿径向错开。
    kKillEffectChromatic     = 4,

    /// 抖动：整幅画面小幅高频错位。
    kKillEffectShake         = 5,

    /// 按 ControlHeader 里的效果配置数组播放（多效果共存用）。
    kKillEffectConfigured    = 6,

    /// 全屏闪光：整个画面短暂变亮。
    kKillEffectFlash         = 7,

    /// 中心冲击环：一圈光从画面中心向外扩散。
    kKillEffectShockwave     = 8,

    /// 故障撕裂：横向块位移 + 红蓝分离。
    /// （原 9 号「径向模糊」已移除，10 号前移补位 —— 协议语义变了，kVersion 已升到 6。）
    kKillEffectGlitch        = 9,
};

/// 一帧里所有击杀效果叠加后的参数。
/// zoom = 1、其余为 0 表示这一帧不用画效果。
struct KillEffectFrameState
{
    float    zoom;       // 1 = 原样；> 1 = 放大
    float    shake;      // UV 偏移幅度（0.01 = 屏幕高度的 1%）
    float    chromatic;  // UV 径向偏移幅度
    float    edge;       // 边缘脉冲强度
    float    vignette;   // 暗角强度
    float    flash;      // 全屏闪光强度
    float    shockwave;  // 中心冲击环强度
    float    glitch;     // 故障撕裂强度
    float    edgeR;      // 边缘脉冲颜色
    float    edgeG;
    float    edgeB;
    float    progress;   // 0..1，当前事件的时间进度
    float    timeSec;    // 当前事件已经过去多少秒
    uint32_t activeMs;   // 当前效果还剩多少毫秒（0 = 没在放）
};

/// 消费一次事件、推进计时，输出这一帧所有效果的叠加参数。
///
/// 放在 control.cpp 里而不是渲染路径里，是为了让 OpenGL 出帧钩子复用同一份状态。
/// 强度包络和 C# 预览器里的 KillFeedbackEffects.Envelope 保持一致（快进缓出）。
void ControlTickKillEffects(KillEffectFrameState* out);

} // namespace gmblur
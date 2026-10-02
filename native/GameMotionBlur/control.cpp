// ============================================================================
//  control.cpp —— 跨进程共享内存控制块
// ============================================================================
#include "control.h"
#include "log.h"

#include <windows.h>
#include <math.h>
#include <string.h>
#include <wchar.h>

namespace gmblur {

// 默认名字；环境变量 GMBLUR_MAP 可以覆盖（隔离测试 / 同时跑多份）
static wchar_t g_mapName[160] = {};

const wchar_t* MapName()
{
    if (g_mapName[0])
    {
        return g_mapName;
    }

    const DWORD length = GetEnvironmentVariableW(L"GMBLUR_MAP", g_mapName, 159);
    if (length == 0 || length >= 159)
    {
        wcsncpy(g_mapName, L"Local\\GameMotionBlurCtrl_v1", 159);
        g_mapName[159] = 0;
    }
    else
    {
        g_mapName[length] = 0;
    }
    return g_mapName;
}

static HANDLE        g_map = nullptr;
static ControlHeader* g_head = nullptr;

bool ControlInit()
{
    if (g_head)
    {
        return true;
    }

    // 控制端（C# 界面/CLI）一般会先创建；如果它还没起来，这里创建同名的即可，
    // CreateFileMapping 对同一个名字返回的本来就是同一个内核对象。
    g_map = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0,
                               (DWORD)kMappingSize, MapName());
    if (!g_map)
    {
        Log(L"ControlInit: CreateFileMapping 失败 err=%u", GetLastError());
        return false;
    }

    g_head = (ControlHeader*)MapViewOfFile(g_map, FILE_MAP_ALL_ACCESS, 0, 0, kMappingSize);
    if (!g_head)
    {
        Log(L"ControlInit: MapViewOfFile 失败 err=%u", GetLastError());
        return false;
    }

    // 这里刻意不 memset：控制端可能已经写好参数了。
    if (g_head->magic != kMagic || g_head->structSize != sizeof(ControlHeader))
    {
        g_head->magic = kMagic;
        g_head->version = kVersion;
        if (!(g_head->strength > 0.0f) || g_head->strength > 0.95f)
        {
            g_head->strength = 0.55f;
        }
    }

    // 字符串区的位置每次都由钩子端写清楚，C# 端只按这个偏移访问。
    g_head->structSize    = (uint32_t)sizeof(ControlHeader);
    g_head->statusChars   = kStatusChars;
    g_head->dumpDirChars  = kDumpDirChars;
    g_head->statusOffset  = kStatusOffset;
    g_head->dumpDirOffset = kDumpDirOffset;
    if (g_head->effectConfigCount > kMaxKillEffects)
    {
        g_head->effectConfigCount = 0;
    }
    g_head->effectConfigVersion = kVersion;
    HeaderStatus(g_head)[0] = 0;

    ControlWriteProbes();

    Log(L"ControlInit: 共享内存已就绪 头部=%u 字节 字符串区=%u..%u strength=%.2f enable=%u",
        (unsigned)sizeof(ControlHeader), kStatusOffset, kStringsEnd, g_head->strength, g_head->enable);
    return true;
}

ControlHeader* Control() { return g_head; }

void ControlWriteProbes()
{
    if (!g_head)
    {
        return;
    }

    // C# 端会拿自己的 Marshal.OffsetOf 逐个比对，对不上说明两边结构体不一致。
    g_head->probe[0] = (uint32_t)sizeof(ControlHeader);
    g_head->probe[1] = (uint32_t)offsetof(ControlHeader, probe);
    g_head->probe[2] = kStatusOffset;
    g_head->probe[3] = (uint32_t)offsetof(ControlHeader, lastPresentQpc);
    g_head->probe[4] = kDumpDirOffset;
    g_head->probe[5] = 0xDEADBEEFu;
    g_head->probe[6] = (uint32_t)offsetof(ControlHeader, enable);
    g_head->probe[7] = (uint32_t)offsetof(ControlHeader, presentCount);
}

void ControlPublishStats(uint32_t deviceKind, uint32_t width, uint32_t height, uint32_t format,
                         uint32_t presentCount, uint32_t blendCount, uint32_t state,
                         uint32_t lastHresult, const wchar_t* status, float fps)
{
    if (!g_head)
    {
        return;
    }

    g_head->deviceKind   = deviceKind;
    g_head->width        = width;
    g_head->height       = height;
    g_head->format       = format;
    g_head->presentCount = presentCount;
    g_head->blendCount   = blendCount;
    g_head->state        = state;
    g_head->lastHresult  = lastHresult;
    g_head->fpsMilli     = (uint32_t)(fps * 1000.0f + 0.5f);

    if (status)
    {
        wchar_t* dest = HeaderStatus(g_head);
        wcsncpy(dest, status, kStatusChars - 1);
        dest[kStatusChars - 1] = 0;
    }
}

/// 宿主指定的输出目录：界面 / CLI 在注入之前把它写进这段字符串区（和导出帧共用），
/// DLL 起来之后用它决定日志往哪写（见 log.cpp 的 LogSetDirectory）。
const wchar_t* ControlOutputDir()
{
    if (!g_head)
    {
        return L"";
    }
    return HeaderDumpDir(g_head);
}

// ============================================================================
//  击杀反馈：事件消费 + 计时（v4：多效果共存）
// ============================================================================

namespace {

/// 和 C# 预览器 KillFeedbackEffects 里那两个常数保持一致 —— 改一处必须改另一处。
constexpr double kRisePortion = 0.16;   // 前 16% 冲到峰值
constexpr double kDecayRate   = 3.6;    // 之后的指数回落速度

/// 各效果在 100% 强度时的基准幅度。界面允许输入到 500%，所以最终 amount 可能是 5 倍。
constexpr double kZoomPeak      = 0.10;    // 缩放：100% = 放大 10%
constexpr float  kShakePeak     = 0.012f;  // 抖动：100% = UV 偏移 1.2%
constexpr float  kChromaticPeak = 0.0045f; // 色差：100% = UV 径向偏移 0.45%
constexpr float  kFlashPeak     = 0.55f;   // 闪光：100% = 叠加 0.55 的亮度
constexpr float  kShockwavePeak = 1.00f;   // 冲击环：100% = 基础环强度
constexpr float  kGlitchPeak    = 1.00f;   // 故障撕裂：100% = 基础位移

/// 默认持续时间（控制端没给时用）
constexpr uint32_t kDefaultDurationMs = 380;

bool     s_effectActive    = false;
double   s_effectStartMs   = 0.0;
double   s_effectDurMs     = (double)kDefaultDurationMs;
uint32_t s_effectKind      = 0;
float    s_legacyStrength  = 0.6f;
uint32_t s_lastSeq         = 0;
uint32_t s_playCount       = 0;
bool     s_firstTick       = true;   // 第一拍只记下当前序号，不把历史事件当成新的

/// 毫秒级单调时钟。刻意用 QPC 而不是 GetTickCount64 ——
/// 后者的默认粒度约 15.6ms，400ms 的动画只够走 25 步，看起来会一格一格的。
double NowMs()
{
    static LARGE_INTEGER freq = {};
    if (freq.QuadPart == 0)
    {
        QueryPerformanceFrequency(&freq);
    }

    LARGE_INTEGER counter;
    QueryPerformanceCounter(&counter);
    return freq.QuadPart ? (double)counter.QuadPart * 1000.0 / (double)freq.QuadPart : 0.0;
}

/// 强度包络：快进缓出。progress 在 [0,1) 之外返回 0。
double KillEnvelope(double progress)
{
    if (progress <= 0.0 || progress >= 1.0)
    {
        return 0.0;
    }

    if (progress < kRisePortion)
    {
        return progress / kRisePortion;
    }

    const double decayed = (progress - kRisePortion) / (1.0 - kRisePortion);
    return exp(-kDecayRate * decayed);
}

float ClampFloat(float v, float lo, float hi)
{
    return v < lo ? lo : (v > hi ? hi : v);
}

} // namespace

void ControlTickKillEffects(KillEffectFrameState* out)
{
    if (!out)
    {
        return;
    }

    out->zoom       = 1.0f;
    out->shake      = 0.0f;
    out->chromatic  = 0.0f;
    out->edge       = 0.0f;
    out->vignette   = 0.0f;
    out->flash      = 0.0f;
    out->shockwave  = 0.0f;
    out->glitch     = 0.0f;
    out->edgeR      = 1.0f;
    out->edgeG      = 0.96f;
    out->edgeB      = 0.88f;
    out->progress   = 0.0f;
    out->timeSec    = 0.0f;
    out->activeMs   = 0;

    if (!g_head)
    {
        return;
    }

    // ---- 1) 有新事件？----
    if (s_firstTick)
    {
        // 第一拍不下手：控制端可能早就写过序号了，那些是"上辈子的事"
        s_firstTick = false;
        s_lastSeq = g_head->eventSeq;
    }
    else if (g_head->eventSeq != s_lastSeq)
    {
        s_lastSeq       = g_head->eventSeq;
        s_effectKind    = g_head->eventKind;
        s_legacyStrength = ClampFloat(g_head->eventStrength, 0.0f, 5.0f);
        s_effectDurMs   = (g_head->eventDurationMs >= 60 && g_head->eventDurationMs <= 5000)
                              ? (double)g_head->eventDurationMs : (double)kDefaultDurationMs;
        s_effectStartMs = NowMs();
        s_effectActive  = (s_effectKind != kKillEffectNone);

        uint32_t enabledCount = 0;
        if (s_effectActive && s_effectKind == kKillEffectConfigured)
        {
            uint32_t count = g_head->effectConfigCount;
            if (count > kMaxKillEffects)
            {
                count = kMaxKillEffects;
            }
            KillEffectConfig* configs = HeaderEffectConfigs(g_head);
            for (uint32_t i = 0; i < count; ++i)
            {
                if (configs[i].enabled != 0 && configs[i].kind != kKillEffectNone &&
                    ClampFloat(configs[i].strength, 0.0f, 5.0f) > 0.0f)
                {
                    ++enabledCount;
                }
            }

            if (enabledCount == 0)
            {
                s_effectActive = false;
            }
        }

        if (s_effectActive)
        {
            ++s_playCount;
            Log(L"收到击杀反馈事件 #%u：kind=%u 强度=%.2f 时长=%.0fms 启用效果=%u（累计第 %u 次）",
                s_lastSeq, s_effectKind, s_legacyStrength, s_effectDurMs,
                s_effectKind == kKillEffectConfigured ? enabledCount : 1u, s_playCount);
        }
        else
        {
            Log(L"收到击杀反馈事件 #%u：kind=%u，没有可播放的效果", s_lastSeq, s_effectKind);
        }
    }

    // 把事件通道的状态写回共享内存，C# 端的 CLI / 界面靠它确认"真的接住了"。
    g_head->eventHandledSeq = s_lastSeq;
    g_head->eventPlayCount = s_playCount;

    if (!s_effectActive)
    {
        g_head->eventActiveMs = 0;
        g_head->eventZoomMilli = 1000;
        return;
    }

    // ---- 2) 推进计时 ----
    const double elapsed = NowMs() - s_effectStartMs;
    if (elapsed >= s_effectDurMs)
    {
        s_effectActive = false;
        g_head->eventActiveMs = 0;
        g_head->eventZoomMilli = 1000;
        return;
    }

    const double envelope = KillEnvelope(elapsed / s_effectDurMs);
    const float  progress = (float)(elapsed / s_effectDurMs);
    const uint32_t remaining = (uint32_t)(s_effectDurMs - elapsed + 0.5);
    out->progress = progress;
    out->timeSec  = (float)(elapsed / 1000.0);

    if (s_effectKind == kKillEffectConfigured)
    {
        uint32_t count = g_head->effectConfigCount;
        if (count > kMaxKillEffects)
        {
            count = kMaxKillEffects;
        }

        KillEffectConfig* configs = HeaderEffectConfigs(g_head);
        for (uint32_t i = 0; i < count; ++i)
        {
            if (configs[i].enabled == 0 || configs[i].kind == kKillEffectNone)
            {
                continue;
            }

            const float strength = ClampFloat(configs[i].strength, 0.0f, 5.0f);
            if (strength <= 0.0f)
            {
                continue;
            }

            const float amount = (float)(envelope * (double)strength);
            switch (configs[i].kind)
            {
                case kKillEffectZoomPunch:
                {
                    // 多个缩放同时开时取最强的一个，避免叠乘后直接飞出屏幕
                    const float z = 1.0f + amount * (float)kZoomPeak;
                    if (z > out->zoom)
                    {
                        out->zoom = z;
                    }
                    break;
                }
                case kKillEffectShake:
                    out->shake = amount * kShakePeak;
                    break;
                case kKillEffectChromatic:
                    out->chromatic = amount * kChromaticPeak;
                    break;
                case kKillEffectEdgePulse:
                    out->edge = amount;
                    if ((configs[i].reserved & 0xFFFFFFu) != 0)
                    {
                        out->edgeR = ((configs[i].reserved >> 16) & 0xFFu) / 255.0f;
                        out->edgeG = ((configs[i].reserved >> 8) & 0xFFu) / 255.0f;
                        out->edgeB = (configs[i].reserved & 0xFFu) / 255.0f;
                    }
                    break;
                case kKillEffectVignettePulse:
                    out->vignette = amount;
                    break;
                case kKillEffectFlash:
                    out->flash = amount * kFlashPeak;
                    break;
                case kKillEffectShockwave:
                    out->shockwave = amount * kShockwavePeak;
                    break;
                case kKillEffectGlitch:
                    out->glitch = amount * kGlitchPeak;
                    break;
                default:
                    break;
            }
        }
    }
    else
    {
        // 兼容旧的单效果事件（CLI 直接调 gmblur kill 时走这里）
        const float amount = (float)(envelope * (double)s_legacyStrength);
        switch (s_effectKind)
        {
            case kKillEffectZoomPunch:
                out->zoom = 1.0f + amount * (float)kZoomPeak;
                break;
            case kKillEffectShake:
                out->shake = amount * kShakePeak;
                break;
            case kKillEffectChromatic:
                out->chromatic = amount * kChromaticPeak;
                break;
            case kKillEffectEdgePulse:
                out->edge = amount;
                break;
            case kKillEffectVignettePulse:
                out->vignette = amount;
                break;
            case kKillEffectFlash:
                out->flash = amount * kFlashPeak;
                break;
            case kKillEffectShockwave:
                out->shockwave = amount * kShockwavePeak;
                break;
            case kKillEffectGlitch:
                out->glitch = amount * kGlitchPeak;
                break;
            default:
                break;
        }
    }

    out->activeMs = remaining;
    g_head->eventActiveMs = remaining;
    g_head->eventZoomMilli = (uint32_t)(out->zoom * 1000.0f + 0.5f);
}

} // namespace gmblur
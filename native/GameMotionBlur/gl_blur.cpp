// ============================================================================
//  gl_blur.cpp —— OpenGL（Minecraft Java / LWJGL / GLFW）的帧混合
//
//  这条路径在 OpenGL 上做的事：
//    1) 在游戏调用 SwapBuffers 时，后台缓冲里躺着这一帧；
//    2) 用一个全屏三角形采样"上一帧的混合结果"，用常量 alpha 混合写回后台缓冲：
//           out = history * strength + 当前帧 * (1 - strength)
//       （glBlendColor 给 strength，glBlendFuncSeparate 用 GL_CONSTANT_ALPHA）
//    3) glCopyTexSubImage2D 把结果存回纹理，供下一帧用；
//    4) 最后才真正 SwapBuffers —— 屏幕上就是已经模糊过的画面。
//
//  两条绘制路线，按上下文版本自动选：
//    * GL 3.2 core（Minecraft 1.17+）：VAO + gl_VertexID 生成全屏三角形；
//    * GL 2.x 兼容模式（Minecraft 1.16-）：固定管线 glBegin/glEnd 画一个贴图四边形。
//      （core profile 里固定管线是被删掉的，所以老版本必须走这一条）
//
//  借用的是游戏自己的上下文，所以动手前后要把 GL 状态存下来再还原。
// ============================================================================
#include "gl_blur.h"
#include "bmp.h"
#include "common.h"
#include "control.h"
#include "log.h"

#include <windows.h>
#include <GL/gl.h>
#include <GL/glext.h>
#include <stdio.h>
#include <stdlib.h>
#include <wchar.h>
#include <string.h>

#ifndef GL_SAMPLER_BINDING
#define GL_SAMPLER_BINDING 0x8919
#endif
#ifndef GL_FUNC_ADD
#define GL_FUNC_ADD 0x8006
#endif

namespace gmblur {

// ---------------------------------------------------------------------------
// GL 函数表（GL 1.1 从 opengl32 导出取，1.2+ 用 wglGetProcAddress 取）
// ---------------------------------------------------------------------------
#define GL_FUNC(ret, name, args) typedef ret (APIENTRY *PF_##name) args; static PF_##name p_##name = nullptr;

GL_FUNC(const GLubyte*, glGetString, (GLenum name))
GL_FUNC(void, glGetIntegerv, (GLenum pname, GLint* data))
GL_FUNC(void, glGetBooleanv, (GLenum pname, GLboolean* data))
GL_FUNC(void, glGetFloatv, (GLenum pname, GLfloat* data))
GL_FUNC(void, glDeleteTextures, (GLsizei n, const GLuint* textures))
GL_FUNC(void, glDeleteProgram, (GLuint program))
GL_FUNC(void, glGetTexImage, (GLenum target, GLint level, GLenum format, GLenum type, void* pixels))
GL_FUNC(GLenum, glGetError, (void))
GL_FUNC(void, glEnable, (GLenum cap))
GL_FUNC(void, glDisable, (GLenum cap))
GL_FUNC(GLboolean, glIsEnabled, (GLenum cap))
GL_FUNC(void, glViewport, (GLint x, GLint y, GLsizei w, GLsizei h))
GL_FUNC(void, glColorMask, (GLboolean r, GLboolean g, GLboolean b, GLboolean a))
GL_FUNC(void, glColor4f, (GLfloat r, GLfloat g, GLfloat b, GLfloat a))
GL_FUNC(void, glGenFramebuffers, (GLsizei n, GLuint* framebuffers))
GL_FUNC(void, glFramebufferTexture2D, (GLenum target, GLenum attachment, GLenum textarget, GLuint texture, GLint level))
GL_FUNC(void, glDeleteFramebuffers, (GLsizei n, const GLuint* framebuffers))
GL_FUNC(GLenum, glCheckFramebufferStatus, (GLenum target))
GL_FUNC(void, glBlendFunc, (GLenum sfactor, GLenum dfactor))
GL_FUNC(void, glGenTextures, (GLsizei n, GLuint* textures))
GL_FUNC(void, glBindTexture, (GLenum target, GLuint texture))
GL_FUNC(void, glTexParameteri, (GLenum target, GLenum pname, GLint param))
GL_FUNC(void, glTexImage2D, (GLenum target, GLint level, GLint internalFormat, GLsizei w, GLsizei h, GLint border, GLenum format, GLenum type, const void* pixels))
GL_FUNC(void, glCopyTexSubImage2D, (GLenum target, GLint level, GLint xoffset, GLint yoffset, GLint x, GLint y, GLsizei w, GLsizei h))
GL_FUNC(void, glPixelStorei, (GLenum pname, GLint param))
GL_FUNC(void, glReadPixels, (GLint x, GLint y, GLsizei w, GLsizei h, GLenum format, GLenum type, void* pixels))
GL_FUNC(void, glDrawArrays, (GLenum mode, GLint first, GLsizei count))
GL_FUNC(void, glDrawBuffer, (GLenum buf))
GL_FUNC(void, glReadBuffer, (GLenum src))
GL_FUNC(void, glActiveTexture, (GLenum texture))
GL_FUNC(void, glBindFramebuffer, (GLenum target, GLuint framebuffer))
GL_FUNC(void, glBlendFuncSeparate, (GLenum srcRGB, GLenum dstRGB, GLenum srcAlpha, GLenum dstAlpha))
GL_FUNC(void, glBlendColor, (GLfloat r, GLfloat g, GLfloat b, GLfloat a))
GL_FUNC(GLuint, glCreateShader, (GLenum type))
GL_FUNC(void, glShaderSource, (GLuint shader, GLsizei count, const GLchar* const* string, const GLint* length))
GL_FUNC(void, glCompileShader, (GLuint shader))
GL_FUNC(void, glGetShaderiv, (GLuint shader, GLenum pname, GLint* params))
GL_FUNC(void, glGetShaderInfoLog, (GLuint shader, GLsizei bufSize, GLsizei* length, GLchar* infoLog))
GL_FUNC(GLuint, glCreateProgram, (void))
GL_FUNC(void, glAttachShader, (GLuint program, GLuint shader))
GL_FUNC(void, glLinkProgram, (GLuint program))
GL_FUNC(void, glGetProgramiv, (GLuint program, GLenum pname, GLint* params))
GL_FUNC(void, glGetProgramInfoLog, (GLuint program, GLsizei bufSize, GLsizei* length, GLchar* infoLog))
GL_FUNC(void, glUseProgram, (GLuint program))
GL_FUNC(GLint, glGetUniformLocation, (GLuint program, const GLchar* name))
GL_FUNC(void, glUniform1i, (GLint location, GLint v0))
GL_FUNC(void, glUniform1f, (GLint location, GLfloat v0))
GL_FUNC(void, glUniform2f, (GLint location, GLfloat v0, GLfloat v1))
GL_FUNC(void, glUniform3f, (GLint location, GLfloat v0, GLfloat v1, GLfloat v2))
GL_FUNC(void, glGenVertexArrays, (GLsizei n, GLuint* arrays))
GL_FUNC(void, glBindVertexArray, (GLuint array))
GL_FUNC(void, glDeleteShader, (GLuint shader))
GL_FUNC(void, glMatrixMode, (GLenum mode))
GL_FUNC(void, glLoadIdentity, (void))
GL_FUNC(void, glPushMatrix, (void))
GL_FUNC(void, glPopMatrix, (void))
GL_FUNC(void, glOrtho, (GLdouble l, GLdouble r, GLdouble b, GLdouble t, GLdouble n, GLdouble f))
GL_FUNC(void, glBegin, (GLenum mode))
GL_FUNC(void, glEnd, (void))
GL_FUNC(void, glTexCoord2f, (GLfloat s, GLfloat t))
GL_FUNC(void, glVertex2f, (GLfloat x, GLfloat y))
GL_FUNC(void, glTexEnvI, (GLenum target, GLenum pname, GLint param))
GL_FUNC(void, glGetTexEnviv, (GLenum target, GLenum pname, GLint* params))
GL_FUNC(void, glBindSampler, (GLuint unit, GLuint sampler))
GL_FUNC(void, glBlendEquation, (GLenum mode))
GL_FUNC(void, glBlendEquationSeparate, (GLenum modeRGB, GLenum modeAlpha))

static void* GlProc(const char* name)
{
    static HMODULE module = nullptr;
    if (!module)
    {
        module = GetModuleHandleW(L"opengl32.dll");
        if (!module)
        {
            module = LoadLibraryW(L"opengl32.dll");
        }
    }

    void* proc = (void*)wglGetProcAddress(name);
    if (!proc || proc == (void*)1 || proc == (void*)2 || proc == (void*)3 || proc == (void*)-1)
    {
        proc = module ? (void*)GetProcAddress(module, name) : nullptr;
    }
    return proc;
}

#define LOAD_GL(name, required)                                 \
    p_##name = (PF_##name)GlProc(#name);                        \
    if (!p_##name && required) {                                \
        Log(L"OpenGL 缺少函数 %S，这条路径用不了", #name);       \
        return false;                                           \
    }

static HGLRC g_lastContext = nullptr;
static bool  g_loaded = false;
static bool  g_useCore = false;      // true = VAO + 着色器；false = 固定管线
static bool  g_loadFailed = false;

// ---------------------------------------------------------------------------
// 资源
// ---------------------------------------------------------------------------
static const UINT kMaxBlendFrames = 8;          // 最多平均几帧
static const UINT kMaxRingSlots   = kMaxBlendFrames;

static GLuint g_ring[kMaxRingSlots] = {};       // 环形缓冲（每帧拷贝一张）
static GLuint g_accum = 0;                      // 累加缓冲（优先 RGBA16F）
static GLuint g_accumFbo = 0;                   // 把 accum 当渲染目标用
static UINT   g_slotCount = 0;
static UINT   g_ringIndex = 0;
static UINT   g_blendFrames = 0;
static bool   g_seeded = false;

static GLuint g_program = 0;
static GLuint g_vao = 0;
static GLint  g_texUniform = -1;
static GLint  g_uvScaleUniform = -1;
static GLint  g_shakeUniform = -1;
static GLint  g_chromaticUniform = -1;
static GLint  g_edgeUniform = -1;
static GLint  g_vignetteUniform = -1;
static GLint  g_edgeColorUniform = -1;
static GLint  g_flashUniform = -1;
static GLint  g_shockwaveUniform = -1;
static GLint  g_glitchUniform = -1;
static GLint  g_progressUniform = -1;
static GLint  g_timeUniform = -1;

/// 击杀反馈用的暂存纹理：把后台缓冲拷进来，再按缩放后的 UV 画回去。
/// 不能直接"从后台缓冲采样画到后台缓冲"，所以要这一张中转。
static GLuint g_effectTex = 0;

static int  g_width = 0;
static int  g_height = 0;
static bool g_pipelineFailed = false;

/// 强度(0~1) → 平均帧数（2~8）
static UINT FramesFromStrength(float strength)
{
    if (!(strength > 0.0f)) { strength = 0.0f; }
    if (strength > 1.0f)    { strength = 1.0f; }

    UINT frames = (UINT)(2.0f + strength * (float)(kMaxBlendFrames - 2) + 0.5f);
    if (frames < 2) { frames = 2; }
    if (frames > kMaxBlendFrames) { frames = kMaxBlendFrames; }
    if (g_slotCount && frames > g_slotCount) { frames = g_slotCount; }
    return frames;
}

static unsigned g_dumpRemaining = 0;
static unsigned g_dumpIndex = 0;
static wchar_t  g_dumpDir[MAX_PATH] = {};
static BYTE*    g_readBuffer = nullptr;
static size_t   g_readBufferSize = 0;

static CRITICAL_SECTION g_lock;
static INIT_ONCE g_once = INIT_ONCE_STATIC_INIT;
static volatile LONG g_inBlur = 0;

static BOOL CALLBACK LockInitOnce(PINIT_ONCE, PVOID, PVOID*)
{
    InitializeCriticalSection(&g_lock);
    return TRUE;
}

static void EnsureLock()
{
    InitOnceExecuteOnce(&g_once, LockInitOnce, nullptr, nullptr);
}

// ---------------------------------------------------------------------------
static bool LoadGlFunctions(bool wantCore)
{
    LOAD_GL(glGetString, true)
    LOAD_GL(glGetIntegerv, true)
    LOAD_GL(glGetBooleanv, true)
    LOAD_GL(glGetFloatv, true)
    LOAD_GL(glDeleteTextures, true)
    LOAD_GL(glGetError, true)
    LOAD_GL(glEnable, true)
    LOAD_GL(glDisable, true)
    LOAD_GL(glIsEnabled, true)
    LOAD_GL(glViewport, true)
    LOAD_GL(glColorMask, true)
    LOAD_GL(glGenTextures, true)
    LOAD_GL(glBindTexture, true)
    LOAD_GL(glTexParameteri, true)
    LOAD_GL(glTexImage2D, true)
    LOAD_GL(glCopyTexSubImage2D, true)
    LOAD_GL(glPixelStorei, true)
    LOAD_GL(glReadPixels, true)
    LOAD_GL(glDrawBuffer, true)
    LOAD_GL(glReadBuffer, true)

    // 常量 alpha 混合是 GL 1.4 的东西，老到没有它就只能放弃
    LOAD_GL(glBlendFunc, true)
    LOAD_GL(glBlendColor, true)

    // 这几个是可选增强，缺了也能跑（blend 退化成 glBlendFunc，FBO 缺了就直接累加到后台缓冲）
    p_glBlendFuncSeparate = (PF_glBlendFuncSeparate)GlProc("glBlendFuncSeparate");
    p_glActiveTexture = (PF_glActiveTexture)GlProc("glActiveTexture");
    p_glColor4f = (PF_glColor4f)GlProc("glColor4f");

    // 可选：有些整合包/光影会在 unit0 绑 sampler object；采样时它会覆盖我们纹理自身的过滤参数，
    // 导致 mipmap 不完整时整屏黑。这里先拿到函数，主路径里临时解绑再恢复。
    p_glBindSampler = (PF_glBindSampler)GlProc("glBindSampler");
    if (!p_glBindSampler)
    {
        p_glBindSampler = (PF_glBindSampler)GlProc("glBindSamplerARB");
    }

    // 可选：帧混合依赖加法混合，先保存游戏 blend equation，主路径里强制 GL_FUNC_ADD，最后还原。
    p_glBlendEquation = (PF_glBlendEquation)GlProc("glBlendEquation");
    p_glBlendEquationSeparate = (PF_glBlendEquationSeparate)GlProc("glBlendEquationSeparate");
    if (!p_glBlendEquationSeparate)
    {
        p_glBlendEquationSeparate = (PF_glBlendEquationSeparate)GlProc("glBlendEquationSeparateEXT");
    }

    p_glBindFramebuffer = (PF_glBindFramebuffer)GlProc("glBindFramebuffer");
    p_glGenFramebuffers = (PF_glGenFramebuffers)GlProc("glGenFramebuffers");
    p_glFramebufferTexture2D = (PF_glFramebufferTexture2D)GlProc("glFramebufferTexture2D");
    p_glDeleteFramebuffers = (PF_glDeleteFramebuffers)GlProc("glDeleteFramebuffers");
    p_glCheckFramebufferStatus = (PF_glCheckFramebufferStatus)GlProc("glCheckFramebufferStatus");
    if (!p_glBindFramebuffer)
    {
        p_glBindFramebuffer = (PF_glBindFramebuffer)GlProc("glBindFramebufferEXT");
        p_glGenFramebuffers = (PF_glGenFramebuffers)GlProc("glGenFramebuffersEXT");
        p_glFramebufferTexture2D = (PF_glFramebufferTexture2D)GlProc("glFramebufferTexture2DEXT");
        p_glDeleteFramebuffers = (PF_glDeleteFramebuffers)GlProc("glDeleteFramebuffersEXT");
        p_glCheckFramebufferStatus = (PF_glCheckFramebufferStatus)GlProc("glCheckFramebufferStatusEXT");
    }

    if (wantCore)
    {
        LOAD_GL(glDrawArrays, true)
        LOAD_GL(glCreateShader, true)
        LOAD_GL(glShaderSource, true)
        LOAD_GL(glCompileShader, true)
        LOAD_GL(glGetShaderiv, true)
        LOAD_GL(glGetShaderInfoLog, true)
        LOAD_GL(glCreateProgram, true)
        LOAD_GL(glAttachShader, true)
        LOAD_GL(glLinkProgram, true)
        LOAD_GL(glGetProgramiv, true)
        LOAD_GL(glGetProgramInfoLog, true)
        LOAD_GL(glUseProgram, true)
        LOAD_GL(glGetUniformLocation, true)
        LOAD_GL(glUniform1i, true)
        // 击杀反馈的效果 uniform：core 路线才有。缺了只是"效果不生效"，不该让整条路挂掉
        LOAD_GL(glUniform1f, false)
        LOAD_GL(glUniform2f, false)
        LOAD_GL(glUniform3f, false)
        LOAD_GL(glGenVertexArrays, true)
        LOAD_GL(glBindVertexArray, true)
        LOAD_GL(glDeleteShader, true)
        LOAD_GL(glDeleteProgram, true)
        p_glGetTexImage = (PF_glGetTexImage)GlProc("glGetTexImage");
    }
    else
    {
        LOAD_GL(glMatrixMode, true)
        LOAD_GL(glLoadIdentity, true)
        LOAD_GL(glPushMatrix, true)
        LOAD_GL(glPopMatrix, true)
        LOAD_GL(glOrtho, true)
        LOAD_GL(glBegin, true)
        LOAD_GL(glEnd, true)
        LOAD_GL(glTexCoord2f, true)
        LOAD_GL(glVertex2f, true)
        p_glTexEnvI = (PF_glTexEnvI)GlProc("glTexEnvi");
        if (!p_glTexEnvI)
        {
            Log(L"OpenGL 缺少 glTexEnvi，固定管线路径用不了");
            return false;
        }
        p_glGetTexEnviv = (PF_glGetTexEnviv)GlProc("glGetTexEnviv");
        if (!p_glGetTexEnviv)
        {
            Log(L"OpenGL 缺少 glGetTexEnviv，固定管线路径用不了");
            return false;
        }
    }

    return true;
}
// ---------------------------------------------------------------------------
// 着色器（3.2 core 用；全屏三角形靠 gl_VertexID 现算，不需要顶点缓冲）
//
// 注意 vUv 就是 p 本身、不用再乘 0.5：p 在屏幕范围内正好是 0..1（p = (NDC+1)/2），
// 多乘一次就会全部采样到贴图边缘（表现为"混合没生效"）。
// 也不用翻转 v：glCopyTexSubImage2D 是从帧缓冲左下角拷到纹理左下角，两边原点一致。
// 和 NDC/纹理坐标都是"左下角为原点"的约定，方向天然一致。
// ---------------------------------------------------------------------------
static const char* kVertexShader =
    "#version 150 core\n"
    "out vec2 vUv;\n"
    "void main()\n"
    "{\n"
    "    vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));\n"
    "    vUv = p;\n"
    "    gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);\n"
    "}\n";

static const char* kFragmentShader =
    "#version 150 core\n"
    "in vec2 vUv;\n"
    "uniform sampler2D uTex;\n"
    "uniform vec2 uUvScale;\n"     // (1,1) = 原样；小于 1 = 以画面中心为原点放大
    "uniform vec2 uShake;\n"
    "uniform float uChromatic;\n"
    "uniform float uEdge;\n"
    "uniform vec3 uEdgeColor;\n"
    "uniform float uVignette;\n"
    "uniform float uFlash;\n"
    "uniform float uShockwave;\n"
    "uniform float uGlitch;\n"
    "uniform float uProgress;\n"
    "uniform float uTime;\n"
    "out vec4 fragColor;\n"
    "void main()\n"
    "{\n"
    "    vec2 baseUv = (vUv - 0.5) * uUvScale + 0.5 + uShake;\n"
    "    vec2 radial = vUv - 0.5;\n"
    "    float d = length(radial);\n"
    "    vec2 dir = d > 0.0001 ? radial / d : vec2(0.0);\n"
    "    float r = texture(uTex, baseUv + dir * uChromatic).r;\n"
    "    vec4 g = texture(uTex, baseUv);\n"
    "    float b = texture(uTex, baseUv - dir * uChromatic).b;\n"
    "    vec3 color = vec3(r, g.g, b);\n"
    "\n"
    "    // 全屏闪光\n"
    "    color += vec3(1.0) * uFlash;\n"
    "\n"
    "    // 中心冲击环：随进度从中心向外扩散\n"
    "    if (uShockwave > 0.0001) {\n"
    "        float radius = uProgress * 0.95;\n"
    "        float ring = 1.0 - smoothstep(0.0, 0.075, abs(d - radius));\n"
    "        color += vec3(1.0, 0.94, 0.78) * ring * uShockwave;\n"
    "    }\n"
    "\n"
    "    // 故障撕裂：按行做横向块位移 + 红蓝分离\n"
    "    if (uGlitch > 0.0001) {\n"
    "        float row = floor(vUv.y * 36.0);\n"
    "        float n = fract(sin(row * 12.9898 + uTime * 17.0) * 43758.5453);\n"
    "        float shift = (n - 0.5) * 2.0 * uGlitch * 0.05;\n"
    "        float gr = texture(uTex, baseUv + vec2(shift, 0.0)).r;\n"
    "        float gg = texture(uTex, baseUv).g;\n"
    "        float gb = texture(uTex, baseUv - vec2(shift, 0.0)).b;\n"
    "        color = vec3(gr, gg, gb);\n"
    "    }\n"
    "\n"
    "    float edgeMask = smoothstep(0.50, 1.0, d * 2.0);\n"
    "    color += uEdgeColor * (edgeMask * uEdge * 0.85);\n"
    "    float vig = smoothstep(0.30, 1.0, d * 2.0) * uVignette;\n"
    "    color *= (1.0 - vig * 0.80);\n"
    "    fragColor = vec4(color, 1.0);\n"
    "}\n";

static GLuint CompileShaderStage(GLenum type, const char* source)
{
    GLuint shader = p_glCreateShader(type);
    if (!shader)
    {
        return 0;
    }

    p_glShaderSource(shader, 1, &source, nullptr);
    p_glCompileShader(shader);

    GLint ok = 0;
    p_glGetShaderiv(shader, GL_COMPILE_STATUS, &ok);
    if (!ok)
    {
        char log[512] = {};
        p_glGetShaderInfoLog(shader, 511, nullptr, log);
        Log(L"OpenGL 着色器编译失败：%S", log);
        p_glDeleteShader(shader);
        return 0;
    }
    return shader;
}

static bool BuildCorePipeline()
{
    GLuint vs = CompileShaderStage(GL_VERTEX_SHADER, kVertexShader);
    GLuint fs = CompileShaderStage(GL_FRAGMENT_SHADER, kFragmentShader);
    if (!vs || !fs)
    {
        return false;
    }

    g_program = p_glCreateProgram();
    p_glAttachShader(g_program, vs);
    p_glAttachShader(g_program, fs);
    p_glLinkProgram(g_program);
    p_glDeleteShader(vs);
    p_glDeleteShader(fs);

    GLint ok = 0;
    p_glGetProgramiv(g_program, GL_LINK_STATUS, &ok);
    if (!ok)
    {
        char log[512] = {};
        p_glGetProgramInfoLog(g_program, 511, nullptr, log);
        Log(L"OpenGL 程序链接失败：%S", log);
        return false;
    }

    GLint previousProgram = 0;
    if (p_glGetIntegerv)
    {
        p_glGetIntegerv(GL_CURRENT_PROGRAM, &previousProgram);
    }

    p_glGenVertexArrays(1, &g_vao);
    p_glUseProgram(g_program);
    g_texUniform = p_glGetUniformLocation(g_program, "uTex");
    p_glUniform1i(g_texUniform, 0);

    g_uvScaleUniform = p_glGetUniformLocation(g_program, "uUvScale");
    g_shakeUniform = p_glGetUniformLocation(g_program, "uShake");
    g_chromaticUniform = p_glGetUniformLocation(g_program, "uChromatic");
    g_edgeUniform = p_glGetUniformLocation(g_program, "uEdge");
    g_vignetteUniform = p_glGetUniformLocation(g_program, "uVignette");
    g_edgeColorUniform = p_glGetUniformLocation(g_program, "uEdgeColor");
    g_flashUniform = p_glGetUniformLocation(g_program, "uFlash");
    g_shockwaveUniform = p_glGetUniformLocation(g_program, "uShockwave");
    g_glitchUniform = p_glGetUniformLocation(g_program, "uGlitch");
    g_progressUniform = p_glGetUniformLocation(g_program, "uProgress");
    g_timeUniform = p_glGetUniformLocation(g_program, "uTime");
    if (g_uvScaleUniform >= 0 && p_glUniform2f)
    {
        p_glUniform2f(g_uvScaleUniform, 1.0f, 1.0f);
    }
    if (g_shakeUniform >= 0 && p_glUniform2f)
    {
        p_glUniform2f(g_shakeUniform, 0.0f, 0.0f);
    }
    if (g_chromaticUniform >= 0 && p_glUniform1f)
    {
        p_glUniform1f(g_chromaticUniform, 0.0f);
    }
    if (g_edgeUniform >= 0 && p_glUniform1f)
    {
        p_glUniform1f(g_edgeUniform, 0.0f);
    }
    if (g_vignetteUniform >= 0 && p_glUniform1f)
    {
        p_glUniform1f(g_vignetteUniform, 0.0f);
    }
    if (g_edgeColorUniform >= 0 && p_glUniform3f)
    {
        p_glUniform3f(g_edgeColorUniform, 1.0f, 0.96f, 0.88f);
    }
    if (g_flashUniform >= 0 && p_glUniform1f)
    {
        p_glUniform1f(g_flashUniform, 0.0f);
    }
    if (g_shockwaveUniform >= 0 && p_glUniform1f)
    {
        p_glUniform1f(g_shockwaveUniform, 0.0f);
    }
    if (g_glitchUniform >= 0 && p_glUniform1f)
    {
        p_glUniform1f(g_glitchUniform, 0.0f);
    }
    if (g_progressUniform >= 0 && p_glUniform1f)
    {
        p_glUniform1f(g_progressUniform, 0.0f);
    }
    if (g_timeUniform >= 0 && p_glUniform1f)
    {
        p_glUniform1f(g_timeUniform, 0.0f);
    }

    p_glUseProgram((GLuint)previousProgram);

    if (!g_vao)
    {
        Log(L"OpenGL glGenVertexArrays 失败，core 路线不可用");
        return false;
    }

    Log(L"OpenGL core 路线就绪（VAO + 着色器）");
    return true;
}

// ---------------------------------------------------------------------------
// GL 状态快照：只存我们要碰的那些，-1 表示"这个上下文没有这个查询，别还原"
// ---------------------------------------------------------------------------
struct GlSnapshot
{
    GLint program;
    GLint vao;
    GLint fbo;
    GLint activeTexture;
    GLint texture0;
    GLint sampler0;
    GLint texture2D;
    GLint texEnvMode;
    GLint blend;
    GLint blendSrcRgb, blendDstRgb, blendSrcAlpha, blendDstAlpha;
    GLint blendEqRgb, blendEqAlpha;
    GLfloat blendColor[4];
    GLboolean colorMask[4];
    GLint depthTest, cullFace, scissorTest, stencilTest;
    GLint viewport[4];
    GLint drawBuffer, readBuffer;
    GLint packAlign, unpackAlign;
};

static void SaveState(GlSnapshot* s)
{
    memset(s, 0, sizeof(*s));

    s->program = -1;
    s->vao = -1;
    s->fbo = -1;
    s->activeTexture = -1;
    s->drawBuffer = -1;
    s->readBuffer = -1;
    s->texEnvMode = -1;
    s->blendEqRgb = -1;
    s->blendEqAlpha = -1;
    s->blendSrcRgb = -1;
    s->blendDstRgb = -1;
    s->blendSrcAlpha = -1;
    s->blendDstAlpha = -1;
    s->packAlign = -1;
    s->unpackAlign = -1;
    s->texture0 = 0;
    s->sampler0 = -1;

    if (p_glGetString)
    {
        if (p_glGetIntegerv)
        {
            if (p_glActiveTexture)
            {
                p_glGetIntegerv(GL_ACTIVE_TEXTURE, &s->activeTexture);
                p_glActiveTexture(GL_TEXTURE0);
            }
            p_glGetIntegerv(GL_TEXTURE_BINDING_2D, &s->texture0);
            if (p_glBindSampler)
            {
                p_glGetIntegerv(GL_SAMPLER_BINDING, &s->sampler0);
            }
        }

        p_glGetIntegerv(GL_VIEWPORT, s->viewport);
        p_glGetIntegerv(GL_BLEND_SRC_RGB, &s->blendSrcRgb);
        p_glGetIntegerv(GL_BLEND_DST_RGB, &s->blendDstRgb);
        p_glGetIntegerv(GL_BLEND_SRC_ALPHA, &s->blendSrcAlpha);
        p_glGetIntegerv(GL_BLEND_DST_ALPHA, &s->blendDstAlpha);
        p_glGetIntegerv(GL_PACK_ALIGNMENT, &s->packAlign);
        p_glGetIntegerv(GL_UNPACK_ALIGNMENT, &s->unpackAlign);
        p_glGetBooleanv(GL_COLOR_WRITEMASK, s->colorMask);
        s->blendColor[0] = s->blendColor[1] = s->blendColor[2] = s->blendColor[3] = 0.0f;
        p_glGetFloatv(GL_BLEND_COLOR, s->blendColor);
        p_glGetIntegerv(GL_BLEND_EQUATION_RGB, &s->blendEqRgb);
        p_glGetIntegerv(GL_BLEND_EQUATION_ALPHA, &s->blendEqAlpha);
    }

    s->depthTest = (GLint)p_glIsEnabled(GL_DEPTH_TEST);
    s->cullFace = (GLint)p_glIsEnabled(GL_CULL_FACE);
    s->scissorTest = (GLint)p_glIsEnabled(GL_SCISSOR_TEST);
    s->stencilTest = (GLint)p_glIsEnabled(GL_STENCIL_TEST);
    s->blend = (GLint)p_glIsEnabled(GL_BLEND);
    s->texture2D = -1;

    if (g_useCore)
    {
        p_glGetIntegerv(GL_CURRENT_PROGRAM, &s->program);
        p_glGetIntegerv(GL_VERTEX_ARRAY_BINDING, &s->vao);
        p_glGetIntegerv(GL_DRAW_BUFFER, &s->drawBuffer);
        p_glGetIntegerv(GL_READ_BUFFER, &s->readBuffer);
        if (p_glBindFramebuffer)
        {
            p_glGetIntegerv(GL_FRAMEBUFFER_BINDING, &s->fbo);
        }
    }
    else
    {
        s->texture2D = (GLint)p_glIsEnabled(GL_TEXTURE_2D);
        if (p_glTexEnvI && p_glGetTexEnviv)
        {
            p_glGetTexEnviv(GL_TEXTURE_ENV, GL_TEXTURE_ENV_MODE, &s->texEnvMode);
        }
    }
}

static void RestoreState(const GlSnapshot* s)
{
    if (s->program >= 0 && p_glUseProgram)
    {
        p_glUseProgram((GLuint)s->program);
    }
    if (s->vao >= 0 && p_glBindVertexArray)
    {
        p_glBindVertexArray((GLuint)s->vao);
    }
    if (s->fbo >= 0 && p_glBindFramebuffer)
    {
        p_glBindFramebuffer(GL_FRAMEBUFFER, (GLuint)s->fbo);
    }

    if (p_glActiveTexture)
    {
        p_glActiveTexture(GL_TEXTURE0);
    }
    p_glBindTexture(GL_TEXTURE_2D, (GLuint)s->texture0);

    if (s->texture2D >= 0)
    {
        if (s->texture2D) { p_glEnable(GL_TEXTURE_2D); } else { p_glDisable(GL_TEXTURE_2D); }
    }
    if (s->texEnvMode >= 0 && p_glTexEnvI)
    {
        p_glTexEnvI(GL_TEXTURE_ENV, GL_TEXTURE_ENV_MODE, s->texEnvMode);
    }

    if (s->sampler0 >= 0 && p_glBindSampler)
    {
        p_glBindSampler(0, (GLuint)s->sampler0);
    }

    if (s->activeTexture >= 0 && p_glActiveTexture)
    {
        p_glActiveTexture((GLenum)s->activeTexture);
    }

    if (p_glBlendFuncSeparate)
    {
        p_glBlendFuncSeparate((GLenum)s->blendSrcRgb, (GLenum)s->blendDstRgb,
                              (GLenum)s->blendSrcAlpha, (GLenum)s->blendDstAlpha);
    }
    else
    {
        p_glBlendFunc((GLenum)s->blendSrcRgb, (GLenum)s->blendDstRgb);
    }
    p_glBlendColor(s->blendColor[0], s->blendColor[1], s->blendColor[2], s->blendColor[3]);
    if (s->blendEqRgb >= 0 && s->blendEqAlpha >= 0)
    {
        if (p_glBlendEquationSeparate)
        {
            p_glBlendEquationSeparate((GLenum)s->blendEqRgb, (GLenum)s->blendEqAlpha);
        }
        else if (p_glBlendEquation)
        {
            p_glBlendEquation((GLenum)s->blendEqRgb);
        }
    }

    p_glViewport(s->viewport[0], s->viewport[1], s->viewport[2], s->viewport[3]);
    p_glColorMask(s->colorMask[0], s->colorMask[1], s->colorMask[2], s->colorMask[3]);

    if (s->blend) { p_glEnable(GL_BLEND); } else { p_glDisable(GL_BLEND); }
    if (s->depthTest) { p_glEnable(GL_DEPTH_TEST); } else { p_glDisable(GL_DEPTH_TEST); }
    if (s->cullFace) { p_glEnable(GL_CULL_FACE); } else { p_glDisable(GL_CULL_FACE); }
    if (s->scissorTest) { p_glEnable(GL_SCISSOR_TEST); } else { p_glDisable(GL_SCISSOR_TEST); }
    if (s->stencilTest) { p_glEnable(GL_STENCIL_TEST); } else { p_glDisable(GL_STENCIL_TEST); }

    if (s->drawBuffer >= 0) { p_glDrawBuffer((GLenum)s->drawBuffer); }
    if (s->readBuffer >= 0) { p_glReadBuffer((GLenum)s->readBuffer); }
    if (s->packAlign >= 0) { p_glPixelStorei(GL_PACK_ALIGNMENT, s->packAlign); }
    if (s->unpackAlign >= 0) { p_glPixelStorei(GL_UNPACK_ALIGNMENT, s->unpackAlign); }
}

// ---------------------------------------------------------------------------
static void ReleaseTarget()
{
    if (p_glDeleteTextures)
    {
        for (UINT i = 0; i < kMaxRingSlots; ++i)
        {
            if (g_ring[i]) { p_glDeleteTextures(1, &g_ring[i]); g_ring[i] = 0; }
        }
        if (g_accum) { p_glDeleteTextures(1, &g_accum); g_accum = 0; }
        if (g_effectTex) { p_glDeleteTextures(1, &g_effectTex); g_effectTex = 0; }
    }
    if (g_accumFbo && p_glDeleteFramebuffers)
    {
        p_glDeleteFramebuffers(1, &g_accumFbo);
        g_accumFbo = 0;
    }

    g_slotCount = 0;
    g_ringIndex = 0;
    g_blendFrames = 0;
    g_seeded = false;
    g_width = 0;
    g_height = 0;
}

static bool CreateRingTexture(GLuint* out, GLsizei width, GLsizei height, GLint internalFormat)
{
    p_glGenTextures(1, out);
    if (!*out)
    {
        return false;
    }

    p_glBindTexture(GL_TEXTURE_2D, *out);
    p_glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
    p_glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
    p_glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
    p_glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
    p_glPixelStorei(GL_UNPACK_ALIGNMENT, 4);
    p_glTexImage2D(GL_TEXTURE_2D, 0, internalFormat, width, height, 0, GL_RGBA, GL_UNSIGNED_BYTE, nullptr);
    return p_glGetError() == GL_NO_ERROR;
}

static bool RebuildTarget(GLsizei width, GLsizei height)
{
    GLint savedActiveTexture = 0;
    GLint savedTexture0 = 0;
    GLint savedFbo = 0;
    GLint savedUnpack = 0;
    const bool canSave = (p_glGetIntegerv != nullptr);
    if (canSave)
    {
        if (p_glActiveTexture)
        {
            p_glGetIntegerv(GL_ACTIVE_TEXTURE, &savedActiveTexture);
            p_glActiveTexture(GL_TEXTURE0);
        }
        p_glGetIntegerv(GL_TEXTURE_BINDING_2D, &savedTexture0);
        if (p_glBindFramebuffer)
        {
            p_glGetIntegerv(GL_FRAMEBUFFER_BINDING, &savedFbo);
        }
        p_glGetIntegerv(GL_UNPACK_ALIGNMENT, &savedUnpack);

        if (savedFbo != 0 && savedFbo == (GLint)g_accumFbo)
        {
            savedFbo = 0;
        }
        if (savedTexture0 != 0)
        {
            bool ours = false;
            for (UINT i = 0; i < kMaxRingSlots; ++i)
            {
                if ((GLuint)savedTexture0 == g_ring[i]) { ours = true; break; }
            }
            if (!ours && ((GLuint)savedTexture0 == g_accum || (GLuint)savedTexture0 == g_effectTex))
            {
                ours = true;
            }
            if (ours)
            {
                savedTexture0 = 0;
            }
        }
    }

    auto restoreTargetState = [&]()
    {
        if (!canSave)
        {
            return;
        }
        if (p_glActiveTexture)
        {
            p_glActiveTexture(GL_TEXTURE0);
        }
        p_glBindTexture(GL_TEXTURE_2D, (GLuint)savedTexture0);
        if (p_glBindFramebuffer)
        {
            p_glBindFramebuffer(GL_FRAMEBUFFER, (GLuint)savedFbo);
        }
        p_glPixelStorei(GL_UNPACK_ALIGNMENT, savedUnpack);
        if (p_glActiveTexture)
        {
            p_glActiveTexture((GLenum)savedActiveTexture);
        }
    };

    ReleaseTarget();

    for (UINT i = 0; i < kMaxRingSlots; ++i)
    {
        if (!CreateRingTexture(&g_ring[i], width, height, GL_RGBA8))
        {
            Log(L"OpenGL glGenTextures/glTexImage2D 失败（第 %u 张环形缓冲）", i);
            ReleaseTarget();
            restoreTargetState();
            return false;
        }
    }
    g_slotCount = kMaxRingSlots;

    // Minecraft 1.21.x + 采样器/光影/驱动组合下，RGBA16F 累加 FBO 会出现整屏黑；
    // 直接累加到后台缓冲在最多 8 帧内的精度足够，先保证画面正确。
    const bool hasFbo = false;

    if (hasFbo && CreateRingTexture(&g_accum, width, height, GL_RGBA16F))
    {
        p_glGenFramebuffers(1, &g_accumFbo);
        p_glBindFramebuffer(GL_FRAMEBUFFER, g_accumFbo);
        p_glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, g_accum, 0);

        const GLenum status = p_glCheckFramebufferStatus(GL_FRAMEBUFFER);
        p_glBindFramebuffer(GL_FRAMEBUFFER, (GLuint)savedFbo);

        if (status != GL_FRAMEBUFFER_COMPLETE)
        {
            Log(L"OpenGL 累加 FBO 不完整（0x%04X），退化成直接累加到后台缓冲", (unsigned)status);
            p_glDeleteFramebuffers(1, &g_accumFbo);
            g_accumFbo = 0;
            p_glDeleteTextures(1, &g_accum);
            g_accum = 0;
        }
    }
    else
    {
        if (g_accum) { p_glDeleteTextures(1, &g_accum); g_accum = 0; }
        p_glGetError();
        Log(L"OpenGL 使用后台缓冲直接累加（不用 RGBA16F FBO；静态画面可能有 ±1 级误差）");
    }

    g_width = width;
    g_height = height;
    g_seeded = false;

    {
        GLuint effectTex = 0;
        if (CreateRingTexture(&effectTex, width, height, GL_RGBA8))
        {
            g_effectTex = effectTex;
        }
        else
        {
            if (effectTex)
            {
                p_glDeleteTextures(1, &effectTex);
            }
            Log(L"OpenGL 建不了击杀反馈暂存纹理，缩放脉冲这次不生效");
        }
    }

    restoreTargetState();

    Log(L"OpenGL 帧混合目标已建立：%dx%d 槽位=%u 累加缓冲=%s",
        (int)width, (int)height, g_slotCount, g_accumFbo ? L"RGBA16F(FBO)" : L"后台缓冲");
    return true;
}

// ---------------------------------------------------------------------------
// GL 调用出错就记一条日志（同一个阶段只记一次，免得刷屏）
// ---------------------------------------------------------------------------
static void CheckGlError(const wchar_t* stage)
{
    static const wchar_t* reported[16] = {};
    static int reportedCount = 0;

    GLenum error = p_glGetError ? p_glGetError() : 0;
    while (error != GL_NO_ERROR)
    {
        bool already = false;
        for (int i = 0; i < reportedCount; ++i)
        {
            if (reported[i] == stage) { already = true; break; }
        }
        if (!already && reportedCount < 16)
        {
            reported[reportedCount++] = stage;
            Log(L"OpenGL 报错 0x%04X（阶段：%s）", (unsigned)error, stage);
        }
        error = p_glGetError ? p_glGetError() : 0;
    }
}
// ---------------------------------------------------------------------------
// 导出帧（调试用；glReadPixels 会卡一下，只在导出时用）
// ---------------------------------------------------------------------------
static void DumpFrameIfNeeded(GLsizei width, GLsizei height)
{
    if (g_dumpRemaining == 0 || !g_dumpDir[0])
    {
        return;
    }

    const size_t needed = (size_t)width * (size_t)height * 4;
    if (g_readBufferSize < needed)
    {
        if (g_readBuffer)
        {
            free(g_readBuffer);
            g_readBuffer = nullptr;
        }
        g_readBuffer = (BYTE*)malloc(needed);
        if (!g_readBuffer)
        {
            Log(L"导出失败：内存不足");
            g_dumpRemaining = 0;
            return;
        }
        g_readBufferSize = needed;
    }

    p_glPixelStorei(GL_PACK_ALIGNMENT, 4);
    p_glReadPixels(0, 0, width, height, GL_RGBA, GL_UNSIGNED_BYTE, g_readBuffer);
    CheckGlError(L"glReadPixels");

    CreateDirectoryW(g_dumpDir, nullptr);

    wchar_t path[MAX_PATH];
    _snwprintf(path, MAX_PATH, L"%s\\frame_%05u_%dx%d.bmp", g_dumpDir, g_dumpIndex, (int)width, (int)height);

    // GL_RGBA -> BMP 要 BGRA，且 glReadPixels 本来就是"从下往上"，正好是 BMP 的行序
    WriteBmpFile(path, g_readBuffer, (UINT)width * 4, (UINT)width, (UINT)height, true, false);

    // 诊断用：把环形缓冲里最新那一帧也存一份，方便对账（和导出的画面应当一致）
    if (p_glGetTexImage && g_slotCount)
    {
        const UINT newestSlot = (g_ringIndex + g_slotCount - 1) % g_slotCount;
        memset(g_readBuffer, 0, needed);
        p_glBindTexture(GL_TEXTURE_2D, g_ring[newestSlot]);
        p_glGetTexImage(GL_TEXTURE_2D, 0, GL_RGBA, GL_UNSIGNED_BYTE, g_readBuffer);
        CheckGlError(L"glGetTexImage");
        wchar_t historyPath[MAX_PATH];
        _snwprintf(historyPath, MAX_PATH, L"%s\\ring_%05u_%dx%d.bmp", g_dumpDir, g_dumpIndex, (int)width, (int)height);
        WriteBmpFile(historyPath, g_readBuffer, (UINT)width * 4, (UINT)width, (UINT)height, true, false);
    }

    ++g_dumpIndex;
    --g_dumpRemaining;
    if (g_dumpRemaining == 0)
    {
        Log(L"OpenGL 导出完成：%u 帧 -> %s", g_dumpIndex, g_dumpDir);
    }
}

// ---------------------------------------------------------------------------
static bool LoadForContext(HGLRC context)
{
    ReleaseTarget();
    g_loaded = false;
    g_pipelineFailed = false;
    g_lastContext = nullptr;

    HMODULE module = GetModuleHandleW(L"opengl32.dll");
    if (!module)
    {
        module = LoadLibraryW(L"opengl32.dll");
    }
    if (!module)
    {
        Log(L"加载 opengl32.dll 失败");
        g_loadFailed = true;
        return false;
    }

    // 先用 1.1 导出表里的 glGetString 看版本，再决定走哪条路线
    typedef const GLubyte* (APIENTRY *PFN_GetString)(GLenum);
    PFN_GetString getString = (PFN_GetString)GetProcAddress(module, "glGetString");
    if (!getString)
    {
        Log(L"取不到 glGetString，放弃 OpenGL 路径");
        g_loadFailed = true;
        return false;
    }

    const char* version = (const char*)getString(GL_VERSION);
    int major = 0;
    int minor = 0;
    if (version)
    {
        major = atoi(version);
        const char* dot = strchr(version, '.');
        if (dot)
        {
            minor = atoi(dot + 1);
        }
    }

    const bool wantCore = (major > 3) || (major == 3 && minor >= 2);

    if (!LoadGlFunctions(wantCore))
    {
        g_loadFailed = true;
        return false;
    }

    g_useCore = false;
    if (wantCore)
    {
        if (BuildCorePipeline())
        {
            g_useCore = true;
        }
        else
        {
            Log(L"core 路线建不起来，退回固定管线");
            if (!LoadGlFunctions(false))
            {
                g_loadFailed = true;
                return false;
            }
        }
    }

    g_loaded = true;
    g_lastContext = context;
    Log(L"OpenGL 已接管：版本=%S 路线=%s", version ? version : "?",
        g_useCore ? L"VAO+着色器" : L"固定管线");
    return true;
}

static bool EnsurePipeline()
{
    if (g_pipelineFailed)
    {
        return false;
    }

    HGLRC context = wglGetCurrentContext();
    if (!context)
    {
        return false;
    }

    if (g_loaded && g_lastContext == context)
    {
        return true;
    }

    if (g_loadFailed)
    {
        return false;
    }

    if (!LoadForContext(context))
    {
        g_pipelineFailed = true;
        return false;
    }

    return true;
}

// ---------------------------------------------------------------------------
// ---------------------------------------------------------------------------
// 画一个覆盖整个后台缓冲的全屏四边形，采样指定纹理。
//   replaceColor = true  → 固定管线用 GL_REPLACE（合成时用）
//   replaceColor = false → 固定管线用 GL_MODULATE（累加时用，顶点色就是权重）
//   uvScale      = 1.0   → 原样；小于 1 → 采样范围缩小、画面被放大（击杀反馈的缩放脉冲用）
// ---------------------------------------------------------------------------
static void DrawFullscreen(GLuint texture, bool replaceColor, const KillEffectFrameState* effect = nullptr)
{
    const float zoom = (effect && effect->zoom > 0.0001f) ? effect->zoom : 1.0f;
    const float uvScale = 1.0f / zoom;
    const float shakeX = effect ? effect->shake : 0.0f;
    const float shakeY = effect ? effect->shake * 0.75f : 0.0f;
    const float chromatic = effect ? effect->chromatic : 0.0f;
    const float edge = effect ? effect->edge : 0.0f;
    const float vignette = effect ? effect->vignette : 0.0f;
    const float flash = effect ? effect->flash : 0.0f;
    const float shockwave = effect ? effect->shockwave : 0.0f;
    const float glitch = effect ? effect->glitch : 0.0f;
    const float progress = effect ? effect->progress : 0.0f;
    const float timeSec = effect ? effect->timeSec : 0.0f;
    const float edgeR = effect ? effect->edgeR : 1.0f;
    const float edgeG = effect ? effect->edgeG : 0.96f;
    const float edgeB = effect ? effect->edgeB : 0.88f;

    if (g_useCore)
    {
        if (p_glActiveTexture)
        {
            p_glActiveTexture(GL_TEXTURE0);
        }
        p_glUseProgram(g_program);

        if (p_glUniform2f && g_uvScaleUniform >= 0)
        {
            p_glUniform2f(g_uvScaleUniform, uvScale, uvScale);
        }
        if (p_glUniform2f && g_shakeUniform >= 0)
        {
            p_glUniform2f(g_shakeUniform, shakeX, shakeY);
        }
        if (p_glUniform1f && g_chromaticUniform >= 0)
        {
            p_glUniform1f(g_chromaticUniform, chromatic);
        }
        if (p_glUniform1f && g_edgeUniform >= 0)
        {
            p_glUniform1f(g_edgeUniform, edge);
        }
        if (p_glUniform1f && g_vignetteUniform >= 0)
        {
            p_glUniform1f(g_vignetteUniform, vignette);
        }
        if (p_glUniform3f && g_edgeColorUniform >= 0)
        {
            p_glUniform3f(g_edgeColorUniform, edgeR, edgeG, edgeB);
        }
        if (p_glUniform1f && g_flashUniform >= 0)
        {
            p_glUniform1f(g_flashUniform, flash);
        }
        if (p_glUniform1f && g_shockwaveUniform >= 0)
        {
            p_glUniform1f(g_shockwaveUniform, shockwave);
        }
        if (p_glUniform1f && g_glitchUniform >= 0)
        {
            p_glUniform1f(g_glitchUniform, glitch);
        }
        if (p_glUniform1f && g_progressUniform >= 0)
        {
            p_glUniform1f(g_progressUniform, progress);
        }
        if (p_glUniform1f && g_timeUniform >= 0)
        {
            p_glUniform1f(g_timeUniform, timeSec);
        }

        p_glBindVertexArray(g_vao);
        p_glBindTexture(GL_TEXTURE_2D, texture);
        p_glDrawArrays(GL_TRIANGLES, 0, 3);
        p_glBindVertexArray(0);

        // 复位，免得影响后面的帧混合绘制
        if (p_glUniform2f && g_uvScaleUniform >= 0)
        {
            p_glUniform2f(g_uvScaleUniform, 1.0f, 1.0f);
        }
        if (p_glUniform2f && g_shakeUniform >= 0)
        {
            p_glUniform2f(g_shakeUniform, 0.0f, 0.0f);
        }
        if (p_glUniform1f && g_chromaticUniform >= 0)
        {
            p_glUniform1f(g_chromaticUniform, 0.0f);
        }
        if (p_glUniform1f && g_edgeUniform >= 0)
        {
            p_glUniform1f(g_edgeUniform, 0.0f);
        }
        if (p_glUniform1f && g_vignetteUniform >= 0)
        {
            p_glUniform1f(g_vignetteUniform, 0.0f);
        }
        if (p_glUniform3f && g_edgeColorUniform >= 0)
        {
            p_glUniform3f(g_edgeColorUniform, 1.0f, 0.96f, 0.88f);
        }
        if (p_glUniform1f && g_flashUniform >= 0)
        {
            p_glUniform1f(g_flashUniform, 0.0f);
        }
        if (p_glUniform1f && g_shockwaveUniform >= 0)
        {
            p_glUniform1f(g_shockwaveUniform, 0.0f);
        }
        if (p_glUniform1f && g_glitchUniform >= 0)
        {
            p_glUniform1f(g_glitchUniform, 0.0f);
        }
        if (p_glUniform1f && g_progressUniform >= 0)
        {
            p_glUniform1f(g_progressUniform, 0.0f);
        }
        if (p_glUniform1f && g_timeUniform >= 0)
        {
            p_glUniform1f(g_timeUniform, 0.0f);
        }

        p_glUseProgram(0);
        return;
    }

    // 固定管线：只能做缩放 + 抖动。边缘脉冲 / 暗角 / 色差需要着色器；
    // 没上 core 的旧版本（1.16-）里其余效果会被忽略，但不影响游戏。
    if (chromatic != 0.0f || edge != 0.0f || vignette != 0.0f ||
        flash != 0.0f || shockwave != 0.0f || glitch != 0.0f)
    {
        static bool logged = false;
        if (!logged)
        {
            logged = true;
            Log(L"当前是固定管线，仅支持缩放/抖动；边缘/暗角/色差/闪光/冲击环/故障撕裂需要 OpenGL 3.2 core");
        }
    }

    if (p_glActiveTexture)
    {
        p_glActiveTexture(GL_TEXTURE0);
    }
    p_glEnable(GL_TEXTURE_2D);
    p_glBindTexture(GL_TEXTURE_2D, texture);
    if (p_glTexEnvI)
    {
        p_glTexEnvI(GL_TEXTURE_ENV, GL_TEXTURE_ENV_MODE, replaceColor ? GL_REPLACE : GL_MODULATE);
    }

    p_glMatrixMode(GL_PROJECTION);
    p_glPushMatrix();
    p_glLoadIdentity();
    p_glOrtho(0.0, 1.0, 0.0, 1.0, -1.0, 1.0);
    p_glMatrixMode(GL_MODELVIEW);
    p_glPushMatrix();
    p_glLoadIdentity();

    // 固定管线这边不用着色器：缩放就是把纹理坐标的范围收窄，抖动用偏移实现。
    const GLfloat loX = 0.5f - 0.5f * uvScale + shakeX;
    const GLfloat hiX = 0.5f + 0.5f * uvScale + shakeX;
    const GLfloat loY = 0.5f - 0.5f * uvScale + shakeY;
    const GLfloat hiY = 0.5f + 0.5f * uvScale + shakeY;

    p_glBegin(GL_TRIANGLE_STRIP);
    p_glTexCoord2f(loX, loY); p_glVertex2f(0.0f, 0.0f);
    p_glTexCoord2f(hiX, loY); p_glVertex2f(1.0f, 0.0f);
    p_glTexCoord2f(loX, hiY); p_glVertex2f(0.0f, 1.0f);
    p_glTexCoord2f(hiX, hiY); p_glVertex2f(1.0f, 1.0f);
    p_glEnd();

    p_glMatrixMode(GL_PROJECTION);
    p_glPopMatrix();
    p_glMatrixMode(GL_MODELVIEW);
    p_glPopMatrix();
}

static bool GlBlurOnSwapLocked(HDC hdc)
{
    if (!EnsurePipeline())
    {
        return false;
    }

    HWND hwnd = WindowFromDC(hdc);
    RECT client;
    if (!hwnd || !GetClientRect(hwnd, &client))
    {
        return false;
    }

    const GLsizei width = (GLsizei)(client.right - client.left);
    const GLsizei height = (GLsizei)(client.bottom - client.top);
    if (width <= 0 || height <= 0)
    {
        return false;
    }

    // 诊断：只在尺寸变化时打一条（以前每秒一条，日志会被刷爆）
    {
        static GLsizei lastWidth = -1;
        static GLsizei lastHeight = -1;
        if (width != lastWidth || height != lastHeight)
        {
            lastWidth = width;
            lastHeight = height;

            GLint viewport[4] = { 0, 0, 0, 0 };
            p_glGetIntegerv(GL_VIEWPORT, viewport);
            Log(L"GL 尺寸：客户区=%dx%d 视口=%d,%d %dx%d",
                (int)width, (int)height, viewport[0], viewport[1], viewport[2], viewport[3]);
        }
    }
    if (width != g_width || height != g_height || g_slotCount == 0)
    {
        if (!RebuildTarget(width, height))
        {
            return false;
        }
    }

    ControlHeader* ctl = Control();
    const UINT flags = ctl ? ctl->flags : 0u;

    if (ctl && (flags & kFlagDumpFrames) && ctl->dumpFrames > 0)
    {
        g_dumpRemaining = ctl->dumpFrames;
        g_dumpIndex = 0;
        wcsncpy(g_dumpDir, HeaderDumpDir(ctl), MAX_PATH - 1);
        g_dumpDir[MAX_PATH - 1] = 0;
        ctl->dumpFrames = 0;
        ctl->flags = flags & ~(UINT)kFlagDumpFrames;
        Log(L"OpenGL 收到导出请求：%u 帧 -> %s", g_dumpRemaining, g_dumpDir);
    }

    if (ctl && (flags & kFlagResetHistory))
    {
        g_seeded = false;
        ctl->flags = flags & ~(UINT)kFlagResetHistory;
    }

    const bool wantBlend = ctl && ctl->enable != 0;

    // 击杀反馈：消费事件、推进计时，拿到这一帧所有效果的叠加参数。
    // 放在帧混合之前 —— 事件必须在同一帧里就被看见，不能因为帧混合关着就漏掉。
    KillEffectFrameState effectState;
    ControlTickKillEffects(&effectState);
    const bool wantEffect =
        (effectState.zoom != 1.0f ||
         effectState.shake != 0.0f ||
         effectState.chromatic != 0.0f ||
         effectState.edge != 0.0f ||
         effectState.vignette != 0.0f ||
         effectState.flash != 0.0f ||
         effectState.shockwave != 0.0f ||
         effectState.glitch != 0.0f) &&
        (g_effectTex != 0);

    if (!wantBlend && !wantEffect)
    {
        // 关掉之后环形缓冲作废，下次开启重新以当前帧为起点
        g_seeded = false;
        g_blendFrames = 0;
        if (g_dumpRemaining > 0)
        {
            DumpFrameIfNeeded(width, height);
        }
        return false;
    }

    // 注意：只放击杀效果时 ctl 可能是 nullptr，所以帧混合那几项必须放在 if 里算
    UINT frames = 0;
    if (wantBlend)
    {
        frames = FramesFromStrength(ctl->strength);
        g_blendFrames = frames;
    }
    else
    {
        // 只放击杀效果时，帧混合那条路要彻底"歇着"：环形缓冲作废，
        // 免得下次真开启时掺进很久以前的画面
        g_seeded = false;
        g_blendFrames = 0;
    }

    GlSnapshot snapshot;
    SaveState(&snapshot);

    // 所有纹理操作都固定在 unit0 上，这样只需要还原 unit0 的绑定。
    if (p_glActiveTexture)
    {
        p_glActiveTexture(GL_TEXTURE0);
    }

    // 诊断：第一次真正打开帧混合时记录关键状态，方便黑屏时定位。
    {
        static bool logged = false;
        if (!logged)
        {
            logged = true;
            Log(L"首次帧混合状态：sampler0=%d blendEq=%04X/%04X draw=%04X read=%04X FBO=%d",
                (int)snapshot.sampler0,
                (unsigned)snapshot.blendEqRgb, (unsigned)snapshot.blendEqAlpha,
                (unsigned)snapshot.drawBuffer, (unsigned)snapshot.readBuffer,
                (int)snapshot.fbo);
        }
    }

    // 有些整合包/光影会在 unit0 绑 sampler object；它会覆盖我们纹理自身的过滤参数，
    // mipmap 不完整时会整屏黑。这里临时解绑，RestoreState 会还原。
    if (p_glBindSampler)
    {
        p_glBindSampler(0, 0);
    }

    // 帧混合必须是加法混合；游戏的 blend equation 可能不是默认的 GL_FUNC_ADD。
    // 先强制成加法，RestoreState 会还原。
    if (p_glBlendEquationSeparate)
    {
        p_glBlendEquationSeparate(GL_FUNC_ADD, GL_FUNC_ADD);
    }
    else if (p_glBlendEquation)
    {
        p_glBlendEquation(GL_FUNC_ADD);
    }

    // 统一到"默认帧缓冲 + 后台缓冲"
    if (p_glBindFramebuffer)
    {
        p_glBindFramebuffer(GL_FRAMEBUFFER, 0);
    }
    p_glDrawBuffer(GL_BACK);
    p_glReadBuffer(GL_BACK);
    p_glViewport(0, 0, width, height);
    p_glDisable(GL_DEPTH_TEST);
    p_glDisable(GL_CULL_FACE);
    p_glDisable(GL_SCISSOR_TEST);
    p_glDisable(GL_STENCIL_TEST);
    p_glColorMask(GL_TRUE, GL_TRUE, GL_TRUE, GL_TRUE);

    bool touched = false;

    if (wantBlend)
    do
    {
        // ---- 1) 当前帧进环形缓冲 ----
        if (!g_seeded)
        {
            // 刚开启/刚清空：所有槽都填当前画面
            for (UINT i = 0; i < g_slotCount; ++i)
            {
                p_glBindTexture(GL_TEXTURE_2D, g_ring[i]);
                p_glCopyTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, 0, 0, width, height);
            }
            CheckGlError(L"种子拷贝");
            g_seeded = true;
            g_ringIndex = 1 % g_slotCount;
        }
        else
        {
            p_glBindTexture(GL_TEXTURE_2D, g_ring[g_ringIndex]);
            p_glCopyTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, 0, 0, width, height);
            CheckGlError(L"帧拷贝");
            g_ringIndex = (g_ringIndex + 1) % g_slotCount;
        }

        const UINT newest = (g_ringIndex + g_slotCount - 1) % g_slotCount;

        // 线性（三角）核：w_i ∝ (N - i)，归一化后和恒为 1 → 静止画面逐像素不变
        const float weightSum = (float)(frames * (frames + 1) / 2);

        p_glEnable(GL_BLEND);

        for (UINT i = 0; i < frames; ++i)
        {
            const UINT slot = (newest + g_slotCount - i) % g_slotCount;
            const float weight = (float)(frames - i) / weightSum;

            if (i == 0)
            {
                if (g_accumFbo)
                {
                    if (p_glBindFramebuffer) { p_glBindFramebuffer(GL_FRAMEBUFFER, g_accumFbo); }
                    p_glDrawBuffer(GL_COLOR_ATTACHMENT0);
                }
                else
                {
                    if (p_glBindFramebuffer) { p_glBindFramebuffer(GL_FRAMEBUFFER, 0); }
                    p_glDrawBuffer(GL_BACK);
                }
            }

            if (g_useCore)
            {
                if (p_glBlendFuncSeparate)
                {
                    p_glBlendFuncSeparate(GL_CONSTANT_ALPHA, (i == 0) ? GL_ZERO : GL_ONE, GL_ZERO, GL_ONE);
                }
                else
                {
                    p_glBlendFunc(GL_CONSTANT_ALPHA, (i == 0) ? GL_ZERO : GL_ONE);
                }
                p_glBlendColor(0.0f, 0.0f, 0.0f, weight);
            }
            else
            {
                p_glBlendFunc(GL_ONE, (i == 0) ? GL_ZERO : GL_ONE);
                if (p_glColor4f) { p_glColor4f(weight, weight, weight, weight); }
            }

            DrawFullscreen(g_ring[slot], false);
        }
        CheckGlError(L"累加绘制");

        // ---- 3) 一次性写回后台缓冲 ----
        if (g_accumFbo)
        {
            if (p_glBindFramebuffer) { p_glBindFramebuffer(GL_FRAMEBUFFER, 0); }
            p_glDrawBuffer(GL_BACK);
            p_glReadBuffer(GL_BACK);
            p_glDisable(GL_BLEND);
            DrawFullscreen(g_accum, true);
            CheckGlError(L"合成绘制");
        }
        else
        {
            p_glDisable(GL_BLEND);
        }

        touched = true;
    } while (false);

    // ---- 击杀反馈：缩放脉冲 ----
    // 放在帧混合之后：缩放作用在"最终要显示的那一帧"上，两件事的叠加顺序也就是确定的。
    if (wantEffect)
    {
        p_glDisable(GL_BLEND);

        // 不能"从后台缓冲采样再画回后台缓冲"，所以先拷进暂存纹理
        p_glBindTexture(GL_TEXTURE_2D, g_effectTex);
        p_glCopyTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, 0, 0, width, height);
        CheckGlError(L"击杀反馈暂存拷贝");

        // DrawFullscreen 内部会按 effectState 组合缩放 / 抖动 / 色差 / 边缘 / 暗角
        DrawFullscreen(g_effectTex, true, &effectState);
        CheckGlError(L"击杀反馈绘制");

        touched = true;
    }

    p_glDisable(GL_BLEND);

    if (touched)
    {
        DumpFrameIfNeeded(width, height);
    }

    RestoreState(&snapshot);
    return touched;
}

bool GlBlurOnSwap(HDC hdc)
{
    if (!hdc)
    {
        return false;
    }

    EnsureLock();
    EnterCriticalSection(&g_lock);
    bool blended = GlBlurOnSwapLocked(hdc);
    LeaveCriticalSection(&g_lock);
    return blended;
}

void GlBlurReset(const wchar_t* reason)
{
    EnsureLock();
    EnterCriticalSection(&g_lock);
    Log(L"OpenGL 状态重置：%s", reason ? reason : L"");
    ReleaseTarget();
    g_loaded = false;
    g_lastContext = nullptr;
    g_loadFailed = false;
    g_pipelineFailed = false;
    LeaveCriticalSection(&g_lock);
}

void GlBlurShutdown()
{
    EnsureLock();
    EnterCriticalSection(&g_lock);
    ReleaseTarget();
    if (g_program && p_glDeleteProgram)
    {
        p_glDeleteProgram(g_program);
    }
    g_program = 0;
    g_vao = 0;
    if (g_readBuffer)
    {
        free(g_readBuffer);
        g_readBuffer = nullptr;
        g_readBufferSize = 0;
    }
    LeaveCriticalSection(&g_lock);
}

unsigned GlBlurTargetWidth() { return (unsigned)g_width; }
unsigned GlBlurTargetHeight() { return (unsigned)g_height; }
unsigned GlBlurTargetFrames() { return g_blendFrames; }
bool GlBlurSupported() { return g_loaded && !g_pipelineFailed; }

} // namespace gmblur
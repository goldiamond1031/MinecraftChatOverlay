// ============================================================================
//  TestGL.cpp —— 用来验证「OpenGL 帧混合」的测试画面
//
//  刻意做成和 Minecraft Java 版一样的样子：
//    * 用 wglCreateContextAttribsARB 建一个 **3.2 core profile** 的上下文
//      （Minecraft 1.17+ 就是 3.2 core；老版本是 2.1/兼容模式）；
//    * core profile 里没有固定管线和 glBegin，所以用 VAO + 着色器画；
//    * 出帧走 gdi32!SwapBuffers（GLFW 也是这么调的），正好验证 IAT 打补丁那条路。
//
//  画面：深色背景 + 一个按帧数匀速左右扫描的白色方块（每帧固定移 12px，
//  这样不管帧率多少，拖影长度都是可复现的）。
//
//  启动后第 30 帧会把自己的一帧写成 BMP 放在 %TEMP%\gmblur_testgl.bmp，
//  用来确认"测试程序自己画得对不对、行序对不对"。
// ============================================================================
#include <windows.h>
#include <GL/gl.h>
#include <GL/glext.h>
#include <stdio.h>
#include <math.h>

#include "..\GameMotionBlur\bmp.h"

// ---- GL 函数指针（core profile 的函数都得自己取） ----
#define DEF_GLFN(ret, name, args) typedef ret (APIENTRY *PF_##name) args; static PF_##name p_##name = nullptr;

DEF_GLFN(void, glViewport, (GLint x, GLint y, GLsizei w, GLsizei h))
DEF_GLFN(void, glClearColor, (GLfloat r, GLfloat g, GLfloat b, GLfloat a))
DEF_GLFN(void, glClear, (GLbitfield mask))
DEF_GLFN(const GLubyte*, glGetString, (GLenum name))
DEF_GLFN(void, glGenVertexArrays, (GLsizei n, GLuint* arrays))
DEF_GLFN(void, glBindVertexArray, (GLuint array))
DEF_GLFN(void, glUseProgram, (GLuint program))
DEF_GLFN(GLuint, glCreateShader, (GLenum type))
DEF_GLFN(void, glShaderSource, (GLuint shader, GLsizei count, const GLchar* const* string, const GLint* length))
DEF_GLFN(void, glCompileShader, (GLuint shader))
DEF_GLFN(void, glGetShaderiv, (GLuint shader, GLenum pname, GLint* params))
DEF_GLFN(void, glGetShaderInfoLog, (GLuint shader, GLsizei bufSize, GLsizei* length, GLchar* infoLog))
DEF_GLFN(GLuint, glCreateProgram, (void))
DEF_GLFN(void, glAttachShader, (GLuint program, GLuint shader))
DEF_GLFN(void, glLinkProgram, (GLuint program))
DEF_GLFN(void, glGetProgramiv, (GLuint program, GLenum pname, GLint* params))
DEF_GLFN(void, glGetProgramInfoLog, (GLuint program, GLsizei bufSize, GLsizei* length, GLchar* infoLog))
DEF_GLFN(GLint, glGetUniformLocation, (GLuint program, const GLchar* name))
DEF_GLFN(void, glUniform4f, (GLint location, GLfloat v0, GLfloat v1, GLfloat v2, GLfloat v3))
DEF_GLFN(void, glUniform2f, (GLint location, GLfloat v0, GLfloat v1))
DEF_GLFN(void, glDrawArrays, (GLenum mode, GLint first, GLsizei count))
DEF_GLFN(void, glDeleteShader, (GLuint shader))
DEF_GLFN(void, glPixelStorei, (GLenum pname, GLint param))
DEF_GLFN(void, glReadPixels, (GLint x, GLint y, GLsizei w, GLsizei h, GLenum format, GLenum type, void* pixels))
DEF_GLFN(void, glEnable, (GLenum cap))
DEF_GLFN(void, glDisable, (GLenum cap))
DEF_GLFN(GLenum, glGetError, (void))

#define GET_GLFN(name)                                                        \
    p_##name = (PF_##name)GetGlProc(#name);                                   \
    if (!p_##name) { printf("缺少 GL 函数: %s\n", #name); return false; }

static bool g_core = false;
static HWND g_hwnd = nullptr;
static HDC  g_dc = nullptr;
static HGLRC g_rc = nullptr;
static GLuint g_vao = 0;
static GLuint g_program = 0;
static GLint  g_rectLoc = -1;
static GLint  g_resLoc = -1;
static int    g_width = 800;
static int    g_height = 600;
static unsigned g_frame = 0;
static bool   g_quit = false;

// ---------------------------------------------------------------------------
static void* GetGlProc(const char* name)
{
    void* p = (void*)wglGetProcAddress(name);
    if (p == nullptr || p == (void*)1 || p == (void*)2 || p == (void*)3 || p == (void*)-1)
    {
        static HMODULE gl = LoadLibraryW(L"opengl32.dll");
        p = gl ? (void*)GetProcAddress(gl, name) : nullptr;
    }
    return p;
}

static LRESULT CALLBACK WndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    if (msg == WM_KEYDOWN && wp == VK_ESCAPE) { g_quit = true; return 0; }
    if (msg == WM_CLOSE || msg == WM_DESTROY) { g_quit = true; return 0; }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

static bool LoadGlFunctions()
{
    GET_GLFN(glViewport)
    GET_GLFN(glClearColor)
    GET_GLFN(glClear)
    GET_GLFN(glGetString)
    GET_GLFN(glEnable)
    GET_GLFN(glDisable)
    GET_GLFN(glGetError)
    GET_GLFN(glPixelStorei)
    GET_GLFN(glReadPixels)

    if (g_core)
    {
        GET_GLFN(glGenVertexArrays)
        GET_GLFN(glBindVertexArray)
        GET_GLFN(glUseProgram)
        GET_GLFN(glCreateShader)
        GET_GLFN(glShaderSource)
        GET_GLFN(glCompileShader)
        GET_GLFN(glGetShaderiv)
        GET_GLFN(glGetShaderInfoLog)
        GET_GLFN(glCreateProgram)
        GET_GLFN(glAttachShader)
        GET_GLFN(glLinkProgram)
        GET_GLFN(glGetProgramiv)
        GET_GLFN(glGetProgramInfoLog)
        GET_GLFN(glGetUniformLocation)
        GET_GLFN(glUniform4f)
        GET_GLFN(glUniform2f)
        GET_GLFN(glDrawArrays)
        GET_GLFN(glDeleteShader)
    }
    return true;
}

// ---------------------------------------------------------------------------
static GLuint CompileShader(GLenum type, const char* source)
{
    GLuint shader = p_glCreateShader(type);
    p_glShaderSource(shader, 1, &source, nullptr);
    p_glCompileShader(shader);

    GLint ok = 0;
    p_glGetShaderiv(shader, GL_COMPILE_STATUS, &ok);
    if (!ok)
    {
        char log[1024] = {};
        p_glGetShaderInfoLog(shader, 1023, nullptr, log);
        printf("着色器编译失败:\n%s\n", log);
        return 0;
    }
    return shader;
}

static bool BuildCorePipeline()
{
    // 全屏三角形 + 在片元里判断是否落在方块内，连顶点缓冲都不需要
    const char* vs =
        "#version 150 core\n"
        "void main() {\n"
        "    vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));\n"
        "    gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);\n"
        "}\n";

    const char* fs =
        "#version 150 core\n"
        "uniform vec4 uRect;\n"
        "uniform vec2 uRes;\n"
        "out vec4 fragColor;\n"
        "void main() {\n"
        "    vec2 pos = gl_FragCoord.xy;\n"
        "    bool inRect = pos.x >= uRect.x && pos.x <= uRect.x + uRect.z &&\n"
        "                  pos.y >= uRect.y && pos.y <= uRect.y + uRect.w;\n"
        "    fragColor = inRect ? vec4(1.0) : vec4(0.02, 0.02, 0.02, 1.0);\n"
        "}\n";

    GLuint vsh = CompileShader(GL_VERTEX_SHADER, vs);
    GLuint fsh = CompileShader(GL_FRAGMENT_SHADER, fs);
    if (!vsh || !fsh)
    {
        return false;
    }

    g_program = p_glCreateProgram();
    p_glAttachShader(g_program, vsh);
    p_glAttachShader(g_program, fsh);
    p_glLinkProgram(g_program);

    GLint ok = 0;
    p_glGetProgramiv(g_program, GL_LINK_STATUS, &ok);
    if (!ok)
    {
        char log[1024] = {};
        p_glGetProgramInfoLog(g_program, 1023, nullptr, log);
        printf("程序链接失败:\n%s\n", log);
        return false;
    }

    p_glDeleteShader(vsh);
    p_glDeleteShader(fsh);

    p_glGenVertexArrays(1, &g_vao);
    p_glBindVertexArray(g_vao);

    g_rectLoc = p_glGetUniformLocation(g_program, "uRect");
    g_resLoc = p_glGetUniformLocation(g_program, "uRes");
    return true;
}

// ---------------------------------------------------------------------------
static bool StartFrameCapture()
{
    // 第 30 帧把自己的画面存下来，确认画对了、行序也对
    static bool done = false;
    if (done || g_frame < 30)
    {
        return false;
    }
    done = true;
    return true;
}

static void SaveSelfFrame()
{
    const int rowBytes = g_width * 4;
    unsigned char* pixels = (unsigned char*)malloc((size_t)rowBytes * g_height);
    if (!pixels)
    {
        return;
    }

    p_glPixelStorei(GL_PACK_ALIGNMENT, 4);
    p_glReadPixels(0, 0, g_width, g_height, GL_BGRA, GL_UNSIGNED_BYTE, pixels);

    p_glEnable(GL_BLEND);
    p_glDisable(GL_BLEND);

    unsigned long white = 0;
    unsigned long mid = 0;
    for (int i = 0; i < g_width * g_height; ++i)
    {
        unsigned char v = pixels[i * 4];
        if (v > 250) { ++white; }
        else if (v > 12 && v < 250) { ++mid; }
    }
    printf("自检：纯白像素=%lu 中间灰=%lu（共 %d 像素）\n", white, mid, g_width * g_height);

    wchar_t path[MAX_PATH];
    GetTempPathW(MAX_PATH, path);
    wcscat(path, L"gmblur_testgl.bmp");
    gmblur::WriteBmpFile(path, pixels, (UINT)rowBytes, (UINT)g_width, (UINT)g_height, false, false);
    wprintf(L"已保存自检帧：%s\n", path);

    free(pixels);
}

static void Render()
{
    const int size = 90;
    // 每帧固定移 12px 的三角波：0 -> 1 -> 0
    const int span = g_width - size;
    const int period = (span / 12) * 2;
    int phase = period > 0 ? (int)(g_frame % (unsigned)period) : 0;
    int step = phase < period / 2 ? phase : period - phase;
    int x = step * 12;
    int y = (g_height - size) / 2;

    if (g_core)
    {
        p_glViewport(0, 0, g_width, g_height);
        p_glUseProgram(g_program);
        p_glBindVertexArray(g_vao);
        p_glUniform4f(g_rectLoc, (GLfloat)x, (GLfloat)y, (GLfloat)size, (GLfloat)size);
        p_glUniform2f(g_resLoc, (GLfloat)g_width, (GLfloat)g_height);
        p_glDrawArrays(GL_TRIANGLES, 0, 3);
    }
    else
    {
        p_glViewport(0, 0, g_width, g_height);
        p_glClearColor(0.02f, 0.02f, 0.02f, 1.0f);
        p_glClear(GL_COLOR_BUFFER_BIT);
        glMatrixMode(GL_PROJECTION);
        glLoadIdentity();
        glOrtho(0, g_width, 0, g_height, -1, 1);
        glMatrixMode(GL_MODELVIEW);
        glLoadIdentity();
        glColor3f(1.0f, 1.0f, 1.0f);
        glBegin(GL_QUADS);
        glVertex2i(x, y);
        glVertex2i(x + size, y);
        glVertex2i(x + size, y + size);
        glVertex2i(x, y + size);
        glEnd();
    }

    if (StartFrameCapture())
    {
        SaveSelfFrame();
    }

    SwapBuffers(g_dc);
    ++g_frame;
}

// ---------------------------------------------------------------------------
static bool CreateCoreContext(HWND hwnd, HDC dc)
{
    // 先在一个临时窗口上建普通上下文，只为拿到 wglCreateContextAttribsARB
    HDC dummyDc = GetDC(hwnd);
    PIXELFORMATDESCRIPTOR pfd = {};
    pfd.nSize = sizeof(pfd);
    pfd.nVersion = 1;
    pfd.dwFlags = PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL | PFD_DOUBLEBUFFER;
    pfd.iPixelType = PFD_TYPE_RGBA;
    pfd.cColorBits = 32;
    pfd.cDepthBits = 24;

    int format = ChoosePixelFormat(dummyDc, &pfd);
    if (!format || !SetPixelFormat(dummyDc, format, &pfd))
    {
        ReleaseDC(hwnd, dummyDc);
        return false;
    }

    HGLRC dummyRc = wglCreateContext(dummyDc);
    if (!dummyRc || !wglMakeCurrent(dummyDc, dummyRc))
    {
        ReleaseDC(hwnd, dummyDc);
        return false;
    }

    typedef HGLRC (WINAPI *PFN_wglCreateContextAttribsARB)(HDC, HGLRC, const int*);
    PFN_wglCreateContextAttribsARB createAttribs =
        (PFN_wglCreateContextAttribsARB)GetGlProc("wglCreateContextAttribsARB");

    // 定义在 wglext.h 里，这里直接写值，免得依赖头文件版本
    const int WGL_CONTEXT_MAJOR_VERSION_ARB_ = 0x2091;
    const int WGL_CONTEXT_MINOR_VERSION_ARB_ = 0x2092;
    const int WGL_CONTEXT_PROFILE_MASK_ARB_  = 0x9126;
    const int WGL_CONTEXT_CORE_PROFILE_BIT_ARB_ = 0x00000001;

    HGLRC coreRc = nullptr;
    if (createAttribs)
    {
        const int attribs[] = {
            WGL_CONTEXT_MAJOR_VERSION_ARB_, 3,
            WGL_CONTEXT_MINOR_VERSION_ARB_, 2,
            WGL_CONTEXT_PROFILE_MASK_ARB_, WGL_CONTEXT_CORE_PROFILE_BIT_ARB_,
            0
        };
        coreRc = createAttribs(dc, nullptr, attribs);
    }

    wglMakeCurrent(nullptr, nullptr);
    wglDeleteContext(dummyRc);
    ReleaseDC(hwnd, dummyDc);

    if (!coreRc)
    {
        return false;
    }

    g_rc = coreRc;
    g_core = true;
    return wglMakeCurrent(dc, g_rc) == TRUE;
}

static bool CreateLegacyContext(HDC dc)
{
    PIXELFORMATDESCRIPTOR pfd = {};
    pfd.nSize = sizeof(pfd);
    pfd.nVersion = 1;
    pfd.dwFlags = PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL | PFD_DOUBLEBUFFER;
    pfd.iPixelType = PFD_TYPE_RGBA;
    pfd.cColorBits = 32;
    pfd.cDepthBits = 24;

    int format = ChoosePixelFormat(dc, &pfd);
    if (!format || !SetPixelFormat(dc, format, &pfd))
    {
        return false;
    }

    g_rc = wglCreateContext(dc);
    if (!g_rc || !wglMakeCurrent(dc, g_rc))
    {
        return false;
    }

    g_core = false;
    return true;
}

int main()
{
    WNDCLASSEXW wc = {};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = WndProc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    wc.lpszClassName = L"GmBlurTestGlWindow";
    RegisterClassExW(&wc);

    wchar_t title[160];
    _snwprintf(title, 159, L"GameMotionBlur OpenGL 测试画面  pid=%u  (ESC 退出)", (unsigned)GetCurrentProcessId());

    RECT rc = { 0, 0, (LONG)g_width, (LONG)g_height };
    AdjustWindowRect(&rc, WS_OVERLAPPEDWINDOW, FALSE);

    HWND hwnd = CreateWindowExW(0, L"GmBlurTestGlWindow", title, WS_OVERLAPPEDWINDOW,
                                CW_USEDEFAULT, CW_USEDEFAULT,
                                rc.right - rc.left, rc.bottom - rc.top,
                                nullptr, nullptr, wc.hInstance, nullptr);
    if (!hwnd)
    {
        printf("CreateWindow 失败\n");
        return 1;
    }
    g_hwnd = hwnd;
    g_dc = GetDC(hwnd);

    if (!CreateCoreContext(hwnd, g_dc))
    {
        printf("拿不到 3.2 core profile，退回兼容模式（Minecraft 老版本就是这种）\n");
        if (!CreateLegacyContext(g_dc))
        {
            printf("创建 OpenGL 上下文失败\n");
            return 2;
        }
    }

    if (!LoadGlFunctions())
    {
        return 3;
    }

    const char* version = (const char*)p_glGetString(GL_VERSION);
    const char* renderer = (const char*)p_glGetString(GL_RENDERER);
    printf("OpenGL 上下文就绪：%s | 渲染器 %s | core profile=%s\n",
           version ? version : "?", renderer ? renderer : "?", g_core ? "是" : "否");
    fflush(stdout);

    if (g_core && !BuildCorePipeline())
    {
        printf("core profile 管线创建失败\n");
        return 4;
    }

    ShowWindow(hwnd, SW_SHOW);

    MSG msg;
    while (!g_quit)
    {
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE))
        {
            if (msg.message == WM_QUIT)
            {
                g_quit = true;
            }
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
        if (g_quit)
        {
            break;
        }

        Render();
    }

    if (g_rc)
    {
        wglMakeCurrent(nullptr, nullptr);
        wglDeleteContext(g_rc);
    }
    if (g_dc)
    {
        ReleaseDC(hwnd, g_dc);
    }
    printf("TestGL 退出（共 %u 帧）\n", g_frame);
    return 0;
}
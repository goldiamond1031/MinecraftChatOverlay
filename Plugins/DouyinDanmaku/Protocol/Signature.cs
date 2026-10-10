using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jint;

namespace DouyinDanmaku.Protocol;

/// <summary>
/// 抖音推流签名器（X-Bogus）。
///
/// 算法与 skmcj/dycast、tiangalon/DyDanmaku 两个开源实现完全一致：
///   1. base_string = "live_id=1,aid=6383,...,room_id={roomId},...,user_unique_id={uid},...,identity=audience"
///   2. X-MS-STUB = md5(base_string) 小写十六进制
///   3. X-Bogus  = 抖音 webmssdk 的签名函数({ 'X-MS-STUB': stub })['X-Bogus']
///
/// 第 3 步是混淆 JS，没法用 C# 重写，所以拿**纯托管 ES 引擎 Jint** 执行官方 JS：
/// 不依赖 WebView2 / Node / 浏览器子进程（那几条路在本机沙箱里都会被拦），
/// 只需要 Jint.dll + Acornima.dll 两个托管 dll 随插件分发。
///
/// 引擎构建约几百毫秒（要解析 50 万字符的 webmssdk），进程内懒建并复用；
/// 换房间只需要重算 stub，不用重建引擎。
/// </summary>
public sealed class DouyinSigner
{
    /// <summary>进程级单例（引擎复用）。签名本身要按 roomId+uid 重算。</summary>
    public static DouyinSigner Instance { get; } = new();

    // 嵌资源别名（csproj 里 LogicalName 指定；文件名带 ~ 不适合当资源名）
    private const string RuntimeLogicalName = "douyindanmaku.signature.runtime.js";
    private const string SdkLogicalName = "douyindanmaku.signature.webmssdk.js";

    private readonly object _gate = new();
    private Engine? _engine;
    private bool _initFailed;

    /// <summary>最近一次失败原因（null = 正常）。状态行与插件日志用。</summary>
    public string? LastError { get; private set; }

    private DouyinSigner() { }

    /// <summary>
    /// 计算 X-Bogus 签名。失败返回 null（调用方退回无签名尝试并把 LastError 显示出去）。
    /// </summary>
    public string? Compute(string roomId, string userUniqueId)
    {
        if (!EnsureEngine())
        {
            return null;
        }

        var baseString =
            "live_id=1,aid=6383,version_code=180800,webcast_sdk_version=1.0.14-beta.0," +
            $"room_id={roomId},sub_room_id=,sub_channel_id=,did_rule=3,user_unique_id={userUniqueId}," +
            "device_platform=web,device_type=,ac=,identity=audience";
        var stub = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(baseString))).ToLowerInvariant();

        try
        {
            lock (_gate)
            {
                // stub 只有十六进制字符，拼进脚本没有注入风险
                var script =
                    $"var __in = {{'X-MS-STUB': '{stub}'}};" +
                    "var __out = (typeof window._0x5c2014 === 'function') ? window._0x5c2014(__in)" +
                    " : (typeof byted_acrawler !== 'undefined' ? byted_acrawler.frontierSign(__in) : null);" +
                    "JSON.stringify(__out);";
                var json = _engine!.Evaluate(script).ToObject() as string;
                if (string.IsNullOrEmpty(json) || json == "null")
                {
                    LastError = "签名函数没有返回结果";
                    return null;
                }

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("X-Bogus", out var bogus))
                {
                    var value = bogus.GetString();
                    if (!string.IsNullOrEmpty(value))
                    {
                        LastError = null;
                        return value;
                    }
                }

                LastError = "签名结果里没有 X-Bogus";
                return null;
            }
        }
        catch (Exception ex)
        {
            LastError = $"签名计算失败：{ex.Message}";
            return null;
        }
    }

    /// <summary>懒建引擎。成功 true；失败置 _initFailed 并记原因（不再反复重试）。</summary>
    private bool EnsureEngine()
    {
        lock (_gate)
        {
            if (_engine != null)
            {
                return true;
            }

            if (_initFailed)
            {
                return false;
            }

            try
            {
                var asm = typeof(DouyinSigner).Assembly;
                var runtimeJs = ReadResource(asm, RuntimeLogicalName);
                var sdkJs = ReadResource(asm, SdkLogicalName);

                var engine = new Engine();
                engine.Execute(BrowserStubsJs);
                engine.Execute(runtimeJs);
                engine.Execute(sdkJs);

                // 确认签名入口真的存在（两个版本的 SDK 入口名不同，都认）
                var probe = engine.Evaluate(
                    "typeof window._0x5c2014 === 'function' || " +
                    "(typeof byted_acrawler !== 'undefined' && typeof byted_acrawler.frontierSign === 'function')").ToObject();
                if (probe is not bool ok || !ok)
                {
                    LastError = "签名 JS 里没有 window._0x5c2014 / byted_acrawler.frontierSign";
                    _initFailed = true;
                    return false;
                }

                _engine = engine;
                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                LastError = $"签名引擎初始化失败：{ex.GetType().Name}: {ex.Message}";
                _initFailed = true;
                return false;
            }
        }
    }

    private static string ReadResource(Assembly asm, string name)
    {
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"嵌资源缺失：{name}（可用：{string.Join(", ", asm.GetManifestResourceNames())}）");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// 浏览器环境桩（DyDanmaku 的 Nashorn 方案同款）：webmssdk 启动时探测这些全局对象，
    /// 缺一个就抛。setTimeout 立即执行是刻意的 —— SDK 用定时器做初始化，不等它签名函数不会挂上。
    /// </summary>
    private const string BrowserStubsJs = """
        var window = { toString: function() { return '[object Window]'; } };
        var self = window;
        var document = {
          cookie: '', body: null, head: null, documentElement: null,
          getElementsByTagName: function() { return []; },
          createElement: function() { return { style: {}, innerHTML: '', offsetWidth: 0, offsetHeight: 0,
            getContext: function() { return null; }, getAttribute: function() { return ''; },
            setAttribute: function() {}, appendChild: function() {}, removeChild: function() {} }; },
          createEvent: function() { return { initEvent: function(){} }; },
          addEventListener: function() {}, removeEventListener: function() {}, dispatchEvent: function() {}
        };
        window.document = document;
        var navigator = {
          userAgent: 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_4) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36',
          appName: 'Netscape', appVersion: '5.0', platform: 'MacIntel', language: 'zh-CN',
          languages: ['zh-CN', 'zh'], hardwareConcurrency: 8, maxTouchPoints: 0,
          cookieEnabled: true, onLine: true, vendor: 'Google Inc.', vendorSub: '', productSub: '20030107',
          getBattery: function() { return null; }, mediaCapabilities: null, bluetooth: null
        };
        window.navigator = navigator;
        function setTimeout(fn, delay) { try { fn(); } catch(e) {} return 0; }
        function clearTimeout(id) {}
        function setInterval(fn, delay) { return 0; }
        function clearInterval(id) {}
        function XMLHttpRequest() {
          this.readyState = 0; this.status = 0; this.responseText = ''; this.onreadystatechange = null;
          this.open = function() {}; this.send = function() { this.readyState = 4; this.status = 200; };
          this.setRequestHeader = function() {}; this.getAllResponseHeaders = function() { return ''; };
        }
        var ActiveXObject = null;
        var console = { log: function() {}, warn: function() {}, error: function() {} };
        var location = { href: 'https://live.douyin.com/', protocol: 'https:', host: 'live.douyin.com' };
        var screen = { width: 1920, height: 1080, colorDepth: 24, pixelDepth: 24 };
        var performance = { now: function() { return Date.now(); }, timing: {} };
        var localStorage = { getItem: function() { return null; }, setItem: function() {}, removeItem: function() {} };
        var sessionStorage = { getItem: function() { return null; }, setItem: function() {}, removeItem: function() {} };
        var indexedDB = null;
        var RTCPeerConnection = null;
        var mozRTCPeerConnection = null;
        var webkitRTCPeerConnection = null;
        """;
}

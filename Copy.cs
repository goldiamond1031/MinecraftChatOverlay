namespace MinecraftChatOverlay;

/// <summary>
/// 所有面向用户的文案集中在这里。
///
/// 语气规范 —— 定位是"有礼貌的助手"，不是"卖萌"：
///   1. 平视，称"你"，不用"您"，也不用"用户"
///   2. 同一个场景用同一种句式，读起来才像一个人写的：
///        - 缺输入 → 「还没 X」
///        - 缺选择 → 「先…吧」
///   3. 短提示不加句号，完整句子加句号
///   4. 延续性状态用省略号「…」，不用三个句点「...」
///   5. 不堆感叹号，不用「～」，不叠语气词
///   6. 状态栏保持信息准确：只把生硬的措辞换软，不牺牲"发生了什么"的清晰度
///   7. 出错时说清楚"没成功"而不是"失败"，少一点责备感
///
/// 想调整整体语气，改这一个文件就够了。
/// </summary>
public static class Copy
{
    // ==================== 缺输入 ====================

    public const string NeedLogPath = "还没选日志文件呢，先挑一个吧。";
    public const string NeedColorText = "还没填要染色的文字。";

    // ---------- ID 对应颜色（队伍色）----------
    public const string TeamAssignEmpty = "还没粘玩家列表。";
    public const string TeamAssignNoMatch = "一条都没认出来 —— 每行要像「灰队 | 2B12」这样。";
    public const string TeamColorsNoneToClear = "没有队伍色规则要清。";

    /// <summary>粘贴内容后实时显示的识别结果。</summary>
    public static string TeamAssignPreview(int count, string breakdown) =>
        $"认到 {count} 个 ID：{breakdown}";

    public static string TeamAssigned(int count) => $"分配好了：{count} 个 ID 染上了队伍色";

    public static string TeamColorsCleared(int count) => $"清掉了 {count} 条队伍色规则";

    // ---------- AI 识图 ----------
    public const string AiNeedBaseUrl = "先填接口地址（BaseUrl）。";
    public const string AiNeedModel = "先填模型 ID。";
    public const string AiNeedDir = "截图目录不存在，先设置一下。";
    public const string AiNoScreenshot = "截图目录里还没有 png 图片。";
    public const string AiNothingRecognized = "AI 没认出队伍行，这次不分配（保持现有颜色）";
    public const string NeedFindText = "还没填要查找的文字。";
    public const string NeedKeyword = "还没填关键词。";
    public const string NeedApiKey = "还没填 API KEY";
    public const string NeedPlayerId = "还没填玩家 ID 或 UUID";
    public const string NeedQueryField = "至少留一个显示字段吧";

    // ==================== 缺选择 ====================

    public const string PickColorRule = "先在列表里选中一条规则吧。";
    public const string PickReplaceRule = "先在列表里选中一条替换规则吧。";
    public const string PickKeyword = "先在列表里选中一个关键词吧。";

    // ==================== 没成功 ====================

    public const string ExportFailed = "导出没成功：";
    public const string ImportFailed = "导入没成功：";
    public const string ImportEmpty = "这个文件是空的，或者格式不太对。";
    public const string QueryFailed = "没查到：";
    public const string QueryError = "查询出错了：";
    public const string CopyFailed = "复制没成功";

    // 查询接口的失败原因（会拼在 QueryFailed / QueryError 后面一起显示）
    public const string QueryTimeout = "网络没反应，等下再试试？";
    public const string QueryEmptyResponse = "接口什么也没返回";
    public const string QueryBadCode = "接口返回异常";
    public const string QueryRejected = "接口拒绝了这个请求";
    public const string NoContent = "没有返回内容";

    // ==================== 做完了 ====================

    public const string QueryDone = "查好了";
    public const string SentToOverlay = "已经发到悬浮窗了";
    public const string SwitchedToDark = "换成夜间模式了";
    public const string SwitchedToLight = "换成日间模式了";
    public const string RulesExported = "规则导出好了：";
    public const string RulesImported = "规则导入好了：";
    public const string DebugCopied = "日志复制好了，可以直接粘贴";
    public const string DebugCleared = "后端日志清空了";
    public const string OverlayCleared = "悬浮窗清空了";
    public const string OverlayPositionReset = "悬浮窗回到右下角了";
    public const string ResetOverlay = "外观设置恢复默认了";
    public const string ResetAutoGg = "自动 GG 设置恢复默认了";
    public const string ConfigSaved = "配置保存好了";
    public const string ConfigLoaded = "配置读取好了：";
    public const string QuerySettingsSaved = "玩家查询设置保存好了";

    // ==================== 消息提示音 ====================

    public const string SoundNotifyOff = "关着，不会响";
    public const string PickSound = "先在列表里选一个提示音吧。";

    /// <summary>没有任何提示音时，切换开关给出的提醒。</summary>
    public const string SoundNoFiles = "还没有提示音 —— 点【添加提示音…】导入一个。";

    public const string SoundFileFilterTitle = "选一个提示音文件";

    /// <summary>
    /// 提示音的自检计数。三个数分开记，是为了让"没响"这件事一眼能定位：
    /// 已看一直是 0 → 日志/消息根本没进来；
    /// 已看涨、判为发言不涨 → 判据没认出来（要调 ChatTextProcessor.LooksLikePlayerSpeech）；
    /// 判为发言涨、响不涨 → 是防刷屏间隔或音频通路的问题。
    /// </summary>
    public static string SoundNotifyStats(int seen, int speech, int played) =>
        $"已看 {seen} 条 · 判为发言 {speech} 条 · 响 {played} 次";

    // ==================== 击杀提示音 ====================

    public const string KillSoundOff = "关着，击杀不会响";

    public static string KillSoundStats(int played) => $"击杀时已响 {played} 次";

    // ==================== 击杀反馈 ====================

    public const string KillFeedbackNoScreenshot =
        "截图目录里还没有图。先去游戏里按 F2 截一张，或者点【换一张截图】自己选一张。";
    public const string KillFeedbackPickImageTitle = "选一张游戏截图当预览底图";
    public const string KillFeedbackImageFilter = "图片 (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|所有文件 (*.*)|*.*";
    public const string KillFeedbackPlaying = "正在播放…";
    public const string KillFeedbackImageTooSmall = "这张图太小了，换一张大点的。";
    public const string KillFeedbackOff = "关着，不会匹配";
    public const string KillFeedbackNoPattern = "还没有匹配规则 —— 上面粘一条示例消息，点【生成并加一行】";

    /// <summary>
    /// 击杀反馈的"前置条件"横幅：还没注入时显示的标题。
    /// 注意这里不能再说"必须先注入才能用" —— 图标反馈 / 音效反馈本来就不需要注入，
    /// 只有「画面效果」和「效果预览」才依赖那条注入通路。写错了会把只想用图标和声音的人劝退。
    /// </summary>
    public const string KillFeedbackPrereqMissing =
        "如果要无风险使用反馈，需要打开击杀反馈开关并关闭动态模糊的注入，填写正确下面的匹配规则后，才可以正常使用图标反馈和音效反馈";

    /// <summary>同上，还没注入时的说明（点明哪半边不需要注入）。</summary>
    public const string KillFeedbackPrereqMissingDetail =
        "图标反馈和音效反馈不碰游戏进程，不需要注入；「画面效果」和「效果预览」要靠注入才能跑，"
        + "用它们也就带上了上面那条封禁风险。";

    /// <summary>注入已就绪时的标题。</summary>
    public const string KillFeedbackPrereqReady = "动态模糊已注入 —— 图标 / 音效 / 画面效果都会生效";

    /// <summary>同上，说明（说清代价 + 怎么退回去）。</summary>
    public const string KillFeedbackPrereqReadyDetail =
        "画面效果会通过已注入的通路送到游戏，这也就是上面说的封禁风险。"
        + "想只留图标和音效，到「游戏动态模糊」页点【卸载钩子】就行。";

    /// <summary>选了原生侧还没实现的效果时给的提示。</summary>
    public const string KillFeedbackEffectNotInGame =
        "这个效果在当前图形路径下没实现（OpenGL 兼容上下文只支持缩放脉冲和抖动），所以匹配到了画面上也不会动";

    /// <summary>
    /// 击杀匹配的自检计数。和提示音那行一个思路：把故障点分开。
    /// 已看一直是 0 → 日志/聊天没进来；
    /// 已看涨、匹配不涨 → 规则不对（多半是称号 / VIP 前缀，或者该开剥离）；这是最要紧的一栏。
    /// 匹配涨了但画面没动 → 那是 DLL 那一半的事。
    /// 最后那句会把**命中的是哪条规则**打出来 —— 写了好几条时能看出是哪条在生效。
    /// </summary>
    public static string KillFeedbackStats(int ruleCount, int seen, int matched, DateTime? lastAt, string lastRule)
    {
        var head = $"规则 {ruleCount} 条 · 已看 {seen} 条 · 匹配 {matched} 条";
        if (lastAt is null || string.IsNullOrWhiteSpace(lastRule))
        {
            return head;
        }

        return head + $" · 最后一次 {lastAt.Value:HH:mm:ss} 命中「{lastRule}」";
    }

    // ==================== 监听状态（状态栏） ====================

    public const string ListeningStarted = "开始监听聊天栏";
    public const string ListeningStopped = "已经停止监听";
    public const string ListeningTo = "正在监听：";
    public const string NoLogPathSet = "还没设置日志文件路径";
    public const string WaitingForLog = "等日志文件出现：";
    public const string CannotOpenLog = "打不开日志文件：";
    public const string LogChanged = "日志文件变了，重新打开…";
    public const string LogGone = "日志文件没了，等它重新出现…";
    public const string LogRotated = "日志被轮转了，重新打开…";
    public const string WatchError = "监听出错了：";
    public const string WatchStarted = "开始监听：";

    // ==================== 悬浮窗 ====================

    public const string OverlayHidden = "悬浮窗收起来了";
    public const string OverlayShown = "悬浮窗出来了";

    // ==================== 版本与更新 ====================

    /// <summary>当前版本号。只改这一处，界面标题和更新检查都用它。</summary>
    public const string AppVersion = "1.2.7";

    /// <summary>界面上版本号的前缀（应用名单独一行显示，所以这里只剩 v）。</summary>
    public const string AppVersionPrefix = "v";
    public const string CheckUpdate = "检查更新";
    public const string GetUpdate = "获取更新";
    public const string CheckingUpdate = "正在检查…";
    public const string AlreadyLatest = "已经是最新版本了";
    public const string UpdateAvailable = "有新版本：";
    public const string CheckUpdateFailed = "检查更新失败了：";
    public const string ReleasesOpened = "已经在浏览器里打开 Releases 页面";
    public const string BrowserFailed = "打不开浏览器：";

    /// <summary>失败提示弹窗的标题。</summary>
    public const string CheckUpdateTitle = "检查更新";

    // ==================== B站弹幕模块 ====================

    public const string BiliConfigLoaded = "B站弹幕配置读取好了：";
    public const string BiliLogCopied = "B站连接日志复制好了，可以直接粘贴";
    public const string BiliOverlayHidden = "B站弹幕窗收起来了";
    public const string BiliOverlayShown = "B站弹幕窗出来了";
    public const string BiliGiftNameEmpty = "先填一个礼物名称或关键词";
    public const string BiliGiftNotSelected = "先在列表里选一行";
    public const string OverlaySentTo = "已经发到悬浮窗了";

    // ==================== 玩家查询 ====================

    public const string Querying = "正在请求布吉岛 API…";
    public const string QueryingButton = "查询中…";
    public const string QueryStartButton = "开始查询";
    public const string QueryFailedShort = "查询失败";
    public const string NoDisplayData = "没有可展示的数据";
    public const string NoMatchingFieldData = "接口请求成功，但当前字段路径没匹配到数据，检查一下字段路径。";
    public const string QueryCleared = "查询结果清空了";
    public const string QueryIdleHint = "输入 API KEY 和玩家 ID 后开始查询";
    public const string NeedQueryFirst = "先查询一次，才能从数据里选字段";
    public const string NoPickableFields = "这次查询结果里没有可选的字段";
    public const string AllFieldsAdded = "库里能加的字段都已经在列表里了";
    public const string NeedPickFields = "还没勾选字段";
    public const string OrderUpdated = "顺序更新好了";
    public const string FieldPickerTitle = "从数据里选字段";

    public static string QueryDoneWithCount(int count) => $"查好了 · {count} 项数据";

    public static string QueryTarget(string playerId, string gameType, string mode) =>
        $"查询对象：{playerId} · {gameType} · {mode}";

    public static string FieldsAdded(int count) => $"加了 {count} 个字段";

    // ==================== 异常兜底 ====================

    public const string UnhandledTitle = "出了点问题";

    /// <summary>未处理异常。仍然要让用户看得懂"发生了什么"，所以把异常原文一并给出。</summary>
    public static string Unhandled(object? exception) => $"程序遇到了点意外：\n{exception}";
}

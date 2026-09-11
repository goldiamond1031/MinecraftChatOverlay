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
    public const string ResetOverlay = "外观设置恢复默认了";
    public const string ResetAutoGg = "自动 GG 设置恢复默认了";
    public const string ConfigSaved = "配置保存好了";
    public const string ConfigLoaded = "配置读取好了：";
    public const string QuerySettingsSaved = "玩家查询设置保存好了";

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

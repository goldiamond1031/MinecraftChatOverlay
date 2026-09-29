namespace MinecraftChatOverlay.Plugins.PlayerQuery;

/// <summary>
/// 玩家查询用到的文案（原来散在宿主的 Copy.cs 里）。
/// 插件不能引主程序，所以搬插件时把这些常量一起带过来了。
/// </summary>
internal static class PlayerQueryTexts
{
    // ---------- 输入校验 ----------

    public const string NeedApiKey = "还没填 API KEY";
    public const string NeedPlayerId = "还没填玩家 ID 或 UUID";
    public const string NeedQueryField = "至少留一个显示字段吧";

    // ---------- 查询过程与结果 ----------

    public const string QueryFailed = "没查到：";
    public const string QueryError = "查询出错了：";

    /// <summary>查询接口的失败原因（会拼在 QueryFailed / QueryError 后面一起显示）。</summary>
    public const string QueryTimeout = "网络没反应，等下再试试？";
    public const string QueryEmptyResponse = "接口什么也没返回";
    public const string QueryBadCode = "接口返回异常";
    public const string QueryRejected = "接口拒绝了这个请求";
    public const string NoContent = "没有返回内容";

    public const string QueryDone = "查好了";
    public const string Querying = "正在请求布吉岛 API…";
    public const string QueryingButton = "查询中…";
    public const string QueryStartButton = "开始查询";
    public const string QueryFailedShort = "查询失败";
    public const string NoDisplayData = "没有可展示的数据";
    public const string NoMatchingFieldData = "接口请求成功，但当前字段路径没匹配到数据，检查一下字段路径。";
    public const string QueryCleared = "查询结果清空了";
    public const string QueryIdleHint = "输入 API KEY 和玩家 ID 后开始查询";

    // ---------- 杂项 ----------

    public const string OrderUpdated = "顺序更新好了";
    public const string QuerySettingsSaved = "玩家查询设置保存好了";

    public static string QueryDoneWithCount(int count) => $"查好了 · {count} 项数据";

    public static string QueryTarget(string playerId, string gameType, string mode) =>
        $"查询对象：{playerId} · {gameType} · {mode}";
}

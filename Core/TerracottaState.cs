using System.Text.Json;

namespace LargeHardLiteLauncher.Core;

/// <summary>
/// Terracotta（陶瓦联机）的 /state 返回内容。
/// state 字段取值：waiting / host-scanning / host-starting / host-ok /
/// guest-connecting / guest-starting / guest-ok / exception
/// </summary>
public sealed class TerracottaSnapshot
{
    /// <summary>状态版本号。只有它变大才说明状态真的变了（防止乱序响应盖回去）</summary>
    public int Index { get; init; }

    public string State { get; init; } = "";

    /// <summary>host-ok 时才有：房间码 U/XXXX-XXXX-XXXX-XXXX</summary>
    public string? Room { get; init; }

    /// <summary>guest-ok 时才有：该连的服务器地址</summary>
    public string? Url { get; init; }

    /// <summary>玩家列表变化的序号</summary>
    public int ProfileIndex { get; init; }

    /// <summary>房间里的玩家名</summary>
    public List<string> Profiles { get; init; } = new();

    /// <summary>exception 状态下的错误类型编号（0~5）</summary>
    public int ExceptionType { get; init; } = -1;

    public bool IsHostOk => State == "host-ok";
    public bool IsGuestOk => State == "guest-ok";
    public bool IsException => State == "exception";

    /// <summary>正在忙（扫描/连接中），按钮该禁用</summary>
    public bool IsBusy => State is "host-scanning" or "host-starting"
        or "guest-connecting" or "guest-starting";

    /// <summary>能发"离开/回到待机"吗</summary>
    public bool CanLeave => State != "waiting";

    public string ExceptionText => ExceptionType switch
    {
        0 => "Ping 房主失败(对方可能已经关了游戏)",
        1 => "Ping 房主被重置(防火墙或运营商拦截)",
        2 => "访客侧 EasyTier 崩了",
        3 => "房主侧 EasyTier 崩了",
        4 => "Ping 中继服务器被重置",
        5 => "联机协议响应非法(版本不兼容)",
        _ => "未知错误",
    };

    public string StateText => State switch
    {
        "waiting" => "待机",
        "host-scanning" => "正在查找本机游戏...",
        "host-starting" => "正在建立隧道...",
        "host-ok" => "房间已创建,等朋友加入",
        "guest-connecting" => "正在连接房主...",
        "guest-starting" => "正在握手...",
        "guest-ok" => "已连上房主",
        "exception" => "出错了:" + ExceptionText,
        _ => State,
    };

    /// <summary>从 /state 的 JSON 解析。解析不出来返回 null。</summary>
    public static TerracottaSnapshot? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var profiles = new List<string>();
            if (root.TryGetProperty("profiles", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in arr.EnumerateArray())
                {
                    // TerracottaProfile 的字段名可能变，宽松地挨个试
                    var name = p.ValueKind == JsonValueKind.String
                        ? p.GetString()
                        : Str(p, "name") ?? Str(p, "player_name") ?? Str(p, "display_name")
                          ?? Str(p, "profile") ?? Str(p, "username");
                    if (!string.IsNullOrWhiteSpace(name))
                        profiles.Add(name!);
                }
            }

            return new TerracottaSnapshot
            {
                Index = Int(root, "index"),
                State = Str(root, "state") ?? "",
                Room = Str(root, "room"),
                Url = Str(root, "url"),
                ProfileIndex = Int(root, "profile_index"),
                Profiles = profiles,
                ExceptionType = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.Number
                    ? t.GetInt32()
                    : -1,
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? Str(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v)
        && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v)
        && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;
}

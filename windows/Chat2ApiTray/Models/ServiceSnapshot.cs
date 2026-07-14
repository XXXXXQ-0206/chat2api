namespace Chat2ApiTray.Models;

public sealed record ServiceSnapshot(
    bool ProcessRunning,
    bool HealthOk,
    string Provider,
    bool? LoggedIn,
    bool? NeedsLogin,
    string? ExpiresAt,
    string Detail)
{
    public static ServiceSnapshot Starting(string detail) => new(
        ProcessRunning: true,
        HealthOk: false,
        Provider: "unknown",
        LoggedIn: null,
        NeedsLogin: null,
        ExpiresAt: null,
        Detail: detail);

    public static ServiceSnapshot Stopped(string detail = "服务未启动。") => new(
        ProcessRunning: false,
        HealthOk: false,
        Provider: "unknown",
        LoggedIn: null,
        NeedsLogin: null,
        ExpiresAt: null,
        Detail: detail);

    public string Headline
    {
        get
        {
            if (!ProcessRunning)
            {
                return "chat2api 已停止";
            }

            if (!HealthOk)
            {
                return "chat2api 启动中";
            }

            if (LoggedIn == true)
            {
                return "chat2api 已登录";
            }

            if (NeedsLogin == true || LoggedIn == false)
            {
                return "chat2api 需要登录";
            }

            return "chat2api 运行中";
        }
    }

    public string Tooltip => string.IsNullOrWhiteSpace(Detail)
        ? Headline
        : $"{Headline} | {Detail}";
}

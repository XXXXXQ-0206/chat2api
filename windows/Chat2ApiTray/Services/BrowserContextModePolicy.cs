namespace Chat2ApiTray.Services;

public static class BrowserContextModePolicy
{
    public static bool ShouldSwitchRequestContextToHeadless(bool contextIsHeadless) => !contextIsHeadless;
}

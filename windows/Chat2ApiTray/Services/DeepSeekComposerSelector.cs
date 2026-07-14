namespace Chat2ApiTray.Services;

public static class DeepSeekComposerSelector
{
    public const string EnabledSendButton = "div[role='button'].ds-button--primary.ds-button--filled:not(.ds-button--disabled)";

    public static bool ShouldReloadAfterNoResponse(bool hasAttachments) => !hasAttachments;
}

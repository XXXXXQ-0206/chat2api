namespace Chat2ApiTray.Services;

public static class DeepSeekModeSelector
{
    public static string For(string mode) => mode.ToLowerInvariant() switch
    {
        "expert" => "[data-model-type='expert'][role='radio']",
        "vision" => "[data-model-type='vision'][role='radio']",
        _ => "[data-model-type='default'][role='radio']"
    };
}

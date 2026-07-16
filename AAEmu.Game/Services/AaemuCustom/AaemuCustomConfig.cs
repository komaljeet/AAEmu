namespace AAEmu.Game.Services.AaemuCustom;

/// <summary>
/// Configuration for the aaemu-custom Rust sidecar integration.
/// Bound from the "AaemuCustom" section of Config.json / Config.Local.json.
/// </summary>
public class AaemuCustomConfig
{
    /// <summary>
    /// Master switch. When false, all sidecar calls are no-ops.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Base URL of the Rust sidecar HTTP API (default listens on 127.0.0.1:1281).
    /// </summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:1281";
}
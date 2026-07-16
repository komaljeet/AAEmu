using AAEmu.Game.Services.AaemuCustom;

namespace AAEmu.Game.Models;

public partial class AppConfiguration
{
    /// <summary>
    /// Optional aaemu-custom Rust sidecar integration settings.
    /// </summary>
    public AaemuCustomConfig AaemuCustom { get; set; } = new();
}
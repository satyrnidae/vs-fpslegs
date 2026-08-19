using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace FirstPersonLegs;

public class FirstPersonLegsConfig
{
    /// <summary>Whether the first-person leg renderer is enabled.</summary>
    /// <remarks>
    /// Attributes get read reflectively by the optional ConfigLib integration (see
    /// <see cref="FirstPersonLegsConfigLib"/>) to build the in-game settings GUI; it's inert when
    /// ConfigLib isn't installed. ConfigLib's managed-config reflection does not use <c>[Category]</c>
    /// for visual grouping. It only recognizes the literal tags <c>"logarithmic"</c>, a log-scale slider, and
    /// <c>"clientside"</c>, which skips the controlserver-privilege check that would otherwise grey the
    /// setting out for non-op players in multiplayer. Every setting here is tagged
    /// <c>[Category("ClientSide")]</c> since this whole mod is client-only with no server sync.
    /// </remarks>
    [Category("ClientSide")]
    [Description("description-FpLegsEnabled")]
    public bool FpLegsEnabled { get; set; } = true;

    /// <summary>
    /// Fixed vertical field of view in degrees the legs render through, independent of the player's world
    /// FOV - the same trick vanilla uses for first-person arms via a fixed <c>fpHandsFoV</c> projection.
    /// Keeps the legs' on-screen size/position constant as world FOV changes, with world FOV the leg tops
    /// climb into view at high FOV. Look-down and size tuning is calibrated against this value, so 70
    /// matches the mod's default tuning, but it should remain acceptable within the range of this setting.
    /// </summary>
    [Category("ClientSide")]
    [Range(40.0, 120.0)]
    [Description("description-LegRenderFov")]
    public float LegRenderFov { get; set; } = 70.0f;

    /// <summary>
    /// When true, logs diagnostics on every leg-mesh rebuild: texture source, full-body and filtered
    /// vertex counts, resolved leg joints. Useful for debugging compatibility with player-model mods,
    /// e.g. PlayerModelLib custom races. Off by default to avoid log spam.
    /// </summary>
    [Category("ClientSide")]
    [Description("description-VerboseLogging")]
    public bool VerboseLogging { get; set; } = false;
}

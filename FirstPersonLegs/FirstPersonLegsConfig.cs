using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace FirstPersonLegs;

public class FirstPersonLegsConfig
{
    /// <summary>Whether the first-person leg renderer is enabled.</summary>
    /// <remarks>
    /// Attributes are read reflectively by the optional ConfigLib integration (see
    /// <see cref="FirstPersonLegsConfigLib"/>) to build the in-game settings GUI; they are inert when
    /// ConfigLib is not installed. <c>[Category("ClientSide")]</c> marks the setting client-side so it is
    /// never overridden by a server, matching this mod's client-only nature.
    /// </remarks>
    [Category("ClientSide")]
    [Description("Render the player's own legs in first person.")]
    public bool FpLegsEnabled { get; set; } = true;

    /// <summary>
    /// Maximum backward lean (radians) applied to the leg assembly when looking straight down. Negative
    /// folds the pelvis backward out of the downward view; positive folds it forward into view. This
    /// approximates Immersive First Person's torso fold, which this mod can't drive via bone poses.
    /// </summary>
    [Category("ClientSide")]
    [Range(-1.5, 0)]
    [Description("Backward lean (radians) of the legs at full look-down")]
    public float LookDownLeanRad { get; set; } = -0.24f;

    /// <summary>
    /// Maximum distance (metres) the leg assembly is pushed backward in world space when looking straight
    /// down, so the pelvis clears the bottom of the view. Pairs with <see cref="LookDownLeanRad"/>.
    /// </summary>
    [Category("ClientSide")]
    [Range(-1.0, 1.0)]
    [Description("Backward shift (metres) of the legs at full look-down, by foot position. 0.0 centers feet in frame.")]
    public float LookDownBackShift { get; set; } = 0.12f;

    /// <summary>
    /// Multiplier applied to the player's entity size when drawing the first-person legs, on top of the
    /// vanilla <c>Properties.Client.Size</c>. 1.0 leaves the legs at their natural size; raising it scales
    /// the whole leg assembly up (useful if the legs read as too small/distant in first person).
    /// </summary>
    [Category("ClientSide")]
    [Range(1.0, 2.0)]
    [Description("Scale multiplier for the first-person legs, on top of the player's normal size.")]
    public float SizeMultiplier { get; set; } = 1.0f;

    /// <summary>
    /// When true, the renderer logs diagnostics on every leg-mesh rebuild (texture source, full-body
    /// and filtered vertex counts, resolved leg joints). Useful for debugging compatibility with
    /// player-model mods (e.g. PlayerModelLib custom races). Off by default to avoid log spam.
    /// </summary>
    [Category("ClientSide")]
    [Description("Log diagnostics on every leg-mesh rebuild. For debugging player-model compatibility.")]
    public bool VerboseLogging { get; set; } = false;
}

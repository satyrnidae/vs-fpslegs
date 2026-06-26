namespace FirstPersonLegs;

public class FirstPersonLegsConfig
{
    /// <summary>Whether the first-person leg renderer is enabled.</summary>
    public bool FpLegsEnabled { get; set; } = true;

    /// <summary>
    /// When true, the renderer logs diagnostics on every leg-mesh rebuild (texture source, full-body
    /// and filtered vertex counts, resolved leg joints). Useful for debugging compatibility with
    /// player-model mods (e.g. PlayerModelLib custom races). Off by default to avoid log spam.
    /// </summary>
    public bool VerboseLogging { get; set; } = false;
}

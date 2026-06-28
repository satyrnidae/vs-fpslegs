using System;
using ConfigLib;
using Vintagestory.API.Common;

namespace FirstPersonLegs;

/// <summary>
/// Optional integration with the <c>configlib</c> mod, kept in its own class so that the only code
/// that touches ConfigLib types lives here. The CLR resolves a method's type references when that
/// method is first JIT-compiled, so as long as <see cref="TryRegister"/> is never called when
/// ConfigLib is absent (see the <c>IsModEnabled</c> guard in <see cref="FirstPersonLegsCore"/>), the
/// missing <c>configlib.dll</c> can never trip a type-load failure — making the dependency genuinely
/// optional with no entry in modinfo.
/// </summary>
internal static class FirstPersonLegsConfigLib
{
    public const string ConfigLibModId = "configlib";

    /// <summary>
    /// Registers the mod's config object with ConfigLib as a managed config. ConfigLib then owns the
    /// config file (same <c>firstpersonlegs.json</c> the mod already uses), renders the in-game settings
    /// GUI, and assigns edited values straight back into <paramref name="config"/>. Whenever a value
    /// changes, <paramref name="onChanged"/> runs so the renderer can react (e.g. toggle the enable flag).
    /// </summary>
    public static void TryRegister(ICoreAPI api, FirstPersonLegsConfig config, Action onChanged)
    {
        ConfigLibModSystem configLib = api.ModLoader.GetModSystem<ConfigLibModSystem>();
        if (configLib == null) return;

        string domain = FirstPersonLegsCore.ModId;

        // Point ConfigLib at the same file the mod loads on its own, so there is a single source of
        // truth whether or not ConfigLib is installed. ConfigLib reflects over the config object's
        // public properties (and their [Description]/[Range]/[Category] attributes) to build settings.
        configLib.RegisterCustomManagedConfig(
            domain,
            config,
            path: "firstpersonlegs.json",
            onSyncedFromServer: onChanged,
            onSettingChanged: _ => onChanged());

        // ConfigLib does not push the file's parsed values into the object on registration, only on
        // later edits — so pull the authoritative values in once now, then let the renderer apply them.
        configLib.GetConfig(domain)?.AssignSettingsValues(config);
        onChanged();

        FirstPersonLegsCore.Logger?.Notification("[firstpersonlegs] ConfigLib integration enabled.");
    }
}

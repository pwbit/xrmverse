namespace Xrmd.Solution.SourceLayout;

/// <summary>Folder rules shared by unpack and pack, so the two cannot drift apart.</summary>
internal static class LayoutFolders
{
    /// <summary>
    /// The folders whose files are zip entries of their own, kept byte for byte at the same path in the zip and in the
    /// source folder, as the reference folders show (code component bundles and resources, agents, custom APIs,
    /// environment variables, app actions, assets). A zip entry or folder file elsewhere that no writer places is refused.
    /// </summary>
    public static readonly string[] PassThrough =
        ["Controls/", "bots/", "botcomponents/", "skills/", "customapis/", "environmentvariabledefinitions/", "appactions/", "Assets/"];

    /// <summary>Whether <paramref name="path"/> lies in one of the <see cref="PassThrough"/> folders.</summary>
    public static bool IsPassThrough(string path) => PassThrough.Any(f => path.StartsWith(f, StringComparison.Ordinal));

    /// <summary>
    /// The top folders the layout places components in (every writer and <see cref="PassThrough"/>). A sync from the
    /// organization writes and removes files in these folders only; anything else a person keeps next to them stays.
    /// </summary>
    public static readonly string[] Components =
    [
        "Other/", "Entities/", "WebResources/", "Workflows/", "PluginAssemblies/", "SdkMessageProcessingSteps/", "Dashboards/", "Roles/",
        "AppModules/", "AppModuleSiteMaps/", .. PassThrough,
    ];

    /// <summary>Whether <paramref name="path"/> lies in one of the <see cref="Components"/> folders.</summary>
    public static bool IsComponent(string path) => Components.Any(f => path.StartsWith(f, StringComparison.Ordinal));
}

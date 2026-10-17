using Melvune.Core;

namespace Melvune.App.Services;

internal static class AppDataDirectory
{
    public static string Resolve()
    {
        const string prefix = "--data-directory=";
        var argument = Environment.GetCommandLineArgs().FirstOrDefault(value => value.StartsWith(prefix, StringComparison.Ordinal));
        if (argument is not null)
        {
            return ProductIdentity.ResolveDataDirectory(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), argument[prefix.Length..]);
        }
#if DEBUG
        if (Environment.GetCommandLineArgs().Contains("--preview-synthetic"))
            return Path.Combine(AppContext.BaseDirectory, "synthetic-settings");
#endif
        return ProductIdentity.ResolveDataDirectory(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    }
}

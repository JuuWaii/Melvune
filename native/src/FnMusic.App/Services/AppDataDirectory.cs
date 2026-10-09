namespace FnMusic.App.Services;

internal static class AppDataDirectory
{
    public static string Resolve()
    {
        const string prefix = "--data-directory=";
        var argument = Environment.GetCommandLineArgs().FirstOrDefault(value => value.StartsWith(prefix, StringComparison.Ordinal));
        if (argument is not null)
        {
            string path = argument[prefix.Length..];
            if (Path.IsPathFullyQualified(path)) return Path.GetFullPath(path);
            throw new ArgumentException("The data directory must be an absolute path.");
        }
#if DEBUG
        if (Environment.GetCommandLineArgs().Contains("--preview-synthetic"))
            return Path.Combine(AppContext.BaseDirectory, "synthetic-settings");
#endif
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FnMusic.Native");
    }
}

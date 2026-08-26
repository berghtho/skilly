using System.IO;

namespace Skilly.Infrastructure;

public static class SkillyPaths
{
    public const int StateSchemaVersion = 3;

    public static string LocalAppDataRoot
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured;
            }

            return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
    }

    public static string ApplicationRoot => Path.Combine(LocalAppDataRoot, "Skilly");

    public static string StateFilePath => Path.Combine(ApplicationRoot, "state.json");

    public static string LogsDirectory => Path.Combine(ApplicationRoot, "logs");

    public static void EnsureApplicationDirectories()
    {
        Directory.CreateDirectory(ApplicationRoot);
        Directory.CreateDirectory(LogsDirectory);
    }
}

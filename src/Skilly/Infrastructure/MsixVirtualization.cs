using System.IO;

namespace Skilly.Infrastructure;

public static class MsixVirtualization
{
    // Package identity (GetCurrentPackageFullName) is absent in processes that merely
    // inherit a container's filesystem silo, so detection writes a probe file into the
    // application root and looks for it in a package's LocalCache mirror instead.
    public static string? DetectRedirectedApplicationRoot()
    {
        var probeName = "virtualization-probe-" + Guid.NewGuid().ToString("N") + ".tmp";
        var probePath = Path.Combine(SkillyPaths.ApplicationRoot, probeName);
        try
        {
            Directory.CreateDirectory(SkillyPaths.ApplicationRoot);
            File.WriteAllText(probePath, string.Empty);
            var packagesRoot = Path.Combine(SkillyPaths.LocalAppDataRoot, "Packages");
            if (!Directory.Exists(packagesRoot))
            {
                return null;
            }

            foreach (var packageDirectory in Directory.EnumerateDirectories(packagesRoot))
            {
                if (File.Exists(Path.Combine(packageDirectory, "LocalCache", "Local", "Skilly", probeName)))
                {
                    return Path.GetFileName(packageDirectory);
                }
            }

            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            try
            {
                File.Delete(probePath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public static string DescribeRefusal(string hostPackageName)
        => $"Skilly is running inside the MSIX container of '{hostPackageName}'. "
           + "Windows redirects writes under %LOCALAPPDATA% into that package's private LocalCache, "
           + "so the authority state would be virtualized and invisible to normally launched instances. "
           + "Launch Skilly directly from Explorer or a regular terminal instead. Nothing changed.";
}

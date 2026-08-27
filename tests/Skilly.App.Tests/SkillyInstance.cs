using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Skilly.App.Tests;

public sealed class IsolatedProfile : IDisposable
{
    public IsolatedProfile()
    {
        Root = Path.Combine(Path.GetTempPath(), "skilly-profile-" + Guid.NewGuid().ToString("N"));
        Home = Path.Combine(Root, "home");
        LocalAppData = Path.Combine(Home, "AppData", "Local");
        RoamingAppData = Path.Combine(Home, "AppData", "Roaming");
        Directory.CreateDirectory(LocalAppData);
        Directory.CreateDirectory(RoamingAppData);
    }

    public string Root { get; }

    public string Home { get; }

    public string LocalAppData { get; }

    public string RoamingAppData { get; }

    public string SkillyRoot => Path.Combine(LocalAppData, "Skilly");

    public string StateFilePath => Path.Combine(SkillyRoot, "state.json");

    public string LogsDirectory => Path.Combine(SkillyRoot, "logs");

    public bool StateFileExists() => File.Exists(StateFilePath);

    public void Dispose() => PackagedAppFixture.TryDeleteDirectory(Root);
}

public sealed class SkillyInstance : IDisposable
{
    private const uint WmClose = 0x0010;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    private SkillyInstance(Process process, IsolatedProfile profile)
    {
        Process = process;
        Profile = profile;
    }

    public Process Process { get; }

    public IsolatedProfile Profile { get; }

    public static SkillyInstance Start(
        string exePath,
        IsolatedProfile profile,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
        };
        psi.Environment["LOCALAPPDATA"] = profile.LocalAppData;
        psi.Environment["APPDATA"] = profile.RoamingAppData;
        psi.Environment["USERPROFILE"] = profile.Home;
        var missingRuntime = Path.Combine(profile.Root, "no-dotnet-runtime");
        psi.Environment["DOTNET_ROOT"] = missingRuntime;
        psi.Environment["DOTNET_ROOT_X64"] = missingRuntime;
        psi.Environment["DOTNET_ROOT(x86)"] = missingRuntime;
        psi.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
        psi.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = Path.Combine(profile.Root, "bundled-runtime");
        psi.Environment["PATH"] = Environment.SystemDirectory;
        if (environment is not null)
        {
            foreach (var variable in environment)
            {
                psi.Environment[variable.Key] = variable.Value;
            }
        }

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to launch Skilly.exe.");
        return new SkillyInstance(process, profile);
    }

    public IntPtr WaitForMainWindow(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            Process.Refresh();
            if (Process.HasExited)
            {
                throw new InvalidOperationException($"Skilly exited prematurely with code {Process.ExitCode}.");
            }

            if (Process.MainWindowHandle != IntPtr.Zero)
            {
                return Process.MainWindowHandle;
            }

            Thread.Sleep(200);
        }

        throw new TimeoutException("Skilly did not create a main window in time.");
    }

    public void WaitForReadyStateFile(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Profile.StateFileExists())
            {
                return;
            }

            Thread.Sleep(100);
        }

        throw new TimeoutException("Skilly did not create its state file under the isolated LocalAppData root.");
    }

    public bool WaitForExit(TimeSpan timeout)
    {
        return Process.WaitForExit((int)timeout.TotalMilliseconds);
    }

    public void CloseMainWindowAndWait(TimeSpan? timeout = null)
    {
        Process.Refresh();
        Assert.True(Process.MainWindowHandle != IntPtr.Zero, "Cannot close: Skilly has no main window.");
        PostMessage(Process.MainWindowHandle, WmClose, IntPtr.Zero, IntPtr.Zero);
        Assert.True(WaitForExit(timeout ?? TimeSpan.FromSeconds(20)), $"Skilly did not exit after WM_CLOSE within {(timeout ?? TimeSpan.FromSeconds(20)).TotalSeconds}s.");
    }

    public void Dispose()
    {
        try
        {
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
                Process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
        }

        Process.Dispose();
        Profile.Dispose();
    }
}

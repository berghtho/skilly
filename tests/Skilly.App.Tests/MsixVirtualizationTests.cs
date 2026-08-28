using Skilly.Infrastructure;

namespace Skilly.App.Tests;

public sealed class MsixVirtualizationTests
{
    [Fact]
    public void Detection_probe_leaves_no_file_behind_and_returns_null_or_a_package_name()
    {
        var hostPackage = MsixVirtualization.DetectRedirectedApplicationRoot();

        if (hostPackage is not null)
        {
            Assert.NotEqual(string.Empty, hostPackage);
        }
        Assert.Empty(System.IO.Directory.EnumerateFiles(SkillyPaths.ApplicationRoot, "virtualization-probe-*.tmp"));
    }

    [Fact]
    public void Refusal_names_the_host_package_and_explains_the_virtualized_authority_state()
    {
        var refusal = MsixVirtualization.DescribeRefusal("Claude_pzs8sxrjxfjjc");

        Assert.Contains("Claude_pzs8sxrjxfjjc", refusal);
        Assert.Contains("LocalCache", refusal);
        Assert.Contains("authority state", refusal);
        Assert.Contains("Nothing changed.", refusal);
    }
}

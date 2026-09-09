using ActionsRing.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Security.Principal;

namespace ActionsRing.App.Tests;

[TestClass]
public sealed class UpdateInstallationPrivilegeTests
{
    [TestMethod]
    public void IsProcessElevated_CanCheckTheCurrentProcessToken()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var expected = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);

        Assert.AreEqual(expected, UpdateInstallationLauncher.IsProcessElevated());
    }

    [TestMethod]
    public async Task IsProcessElevated_CanCheckTheCurrentProcessTokenOnWorkerThread()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var expected = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);

        var actual = await Task.Run(UpdateInstallationLauncher.IsProcessElevated);

        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void Launch_PrivilegeCheckReachesValidationWithoutStartingAnInstaller()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        var launcher = new UpdateInstallationLauncher();

        if (elevated)
        {
            Assert.ThrowsException<InvalidOperationException>(() =>
                launcher.Launch(null!, Environment.ProcessId));
            return;
        }

        var exception = Assert.ThrowsException<ArgumentNullException>(() =>
            launcher.Launch(null!, Environment.ProcessId));
        Assert.AreEqual("package", exception.ParamName);
    }
}

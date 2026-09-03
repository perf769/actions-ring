using ActionsRing.App;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ActionsRing.App.Tests;

[TestClass]
public sealed class UpdateCheckScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 10, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void Due_WhenNeverChecked() =>
        Assert.IsTrue(MainWindow.IsAutomaticUpdateCheckDue(null, Now));

    [TestMethod]
    public void NotDue_BeforeTwentyFourHours() =>
        Assert.IsFalse(MainWindow.IsAutomaticUpdateCheckDue(Now.AddHours(-23), Now));

    [TestMethod]
    public void Due_AfterTwentyFourHours() =>
        Assert.IsTrue(MainWindow.IsAutomaticUpdateCheckDue(Now.AddHours(-24), Now));

    [TestMethod]
    public void Due_WhenSavedClockIsFarInFuture() =>
        Assert.IsTrue(MainWindow.IsAutomaticUpdateCheckDue(Now.AddHours(1), Now));
}

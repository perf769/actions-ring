using ActionsRing.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ActionsRing.App.Tests;

[TestClass]
public sealed class SemanticVersionTests
{
    [TestMethod]
    public void CompareTo_FollowsSemVerPrereleasePrecedence()
    {
        var ordered = new[]
        {
            "1.0.0-alpha",
            "1.0.0-alpha.1",
            "1.0.0-alpha.beta",
            "1.0.0-beta",
            "1.0.0-beta.2",
            "1.0.0-beta.11",
            "1.0.0-rc.1",
            "1.0.0",
            "2.0.0",
        }.Select(SemanticVersion.Parse).ToArray();

        for (var index = 1; index < ordered.Length; index++)
        {
            Assert.IsTrue(ordered[index - 1] < ordered[index], $"{ordered[index - 1]} should precede {ordered[index]}.");
        }
    }

    [TestMethod]
    public void CompareTo_HandlesUnboundedNumericIdentifiersAndIgnoresBuildMetadata()
    {
        var lower = SemanticVersion.Parse("999999999999999999999999.1.0+first");
        var higher = SemanticVersion.Parse("1000000000000000000000000.0.0+second");
        var samePrecedence = SemanticVersion.Parse("999999999999999999999999.1.0+other");

        Assert.IsTrue(lower < higher);
        Assert.AreEqual(0, lower.CompareTo(samePrecedence));
        Assert.AreNotEqual(lower, samePrecedence);
    }

    [TestMethod]
    public void TryParseTag_AcceptsVPrefixButStrictParserDoesNot()
    {
        Assert.IsTrue(SemanticVersion.TryParseTag("v2.1.0-beta.3+win64", out var parsed));
        Assert.AreEqual("2.1.0-beta.3+win64", parsed.ToString());
        Assert.IsFalse(SemanticVersion.TryParse("v2.1.0", out _));
        Assert.IsFalse(SemanticVersion.TryParse("2.01.0", out _));
        Assert.IsFalse(SemanticVersion.TryParse("2.1.0-beta.01", out _));
        Assert.IsFalse(SemanticVersion.TryParse("2.1", out _));
    }

    [TestMethod]
    public void FromVersion_UsesThreePartApplicationVersion()
    {
        Assert.AreEqual("2.1.0", SemanticVersion.FromVersion(new Version(2, 1)).ToString());
        Assert.AreEqual("2.1.7", SemanticVersion.FromVersion(new Version(2, 1, 7, 42)).ToString());
    }
}

using System.Collections.Immutable;
using Prescriva.Agent.Domain.Selectors;

namespace Prescriva.Agent.Domain.Tests.Selectors;

public sealed class SelectorMatcherTests
{
    private readonly SelectorMatcher matcher = new();

    [Fact]
    public void ExactAutomationIdAndControlType_ReturnsFound()
    {
        var selector = new ElementFingerprint(" ERP.EXE ", "Main Window", AutomationId: " Add_Item ", ControlType: "Button");
        var candidates = new[]
        {
            new ElementCandidate("wrong", new ElementFingerprint("erp.exe", "Main Window", Name: "Add Item", RelativeBounds: new(0.1, 0.2, 0.2, 0.1))),
            new ElementCandidate("add", new ElementFingerprint("erp.exe", " main window ", AutomationId: "add_item", ControlType: "button"))
        };

        var result = matcher.Match(selector, candidates, new SelectorWeights());

        Assert.Equal(SelectorMatchStatus.Found, result.Status);
        Assert.Equal("add", result.CandidateId);
        Assert.InRange(result.Score, 60, 100);
        Assert.Contains("automationId", result.Evidence.Keys);
        Assert.Contains("controlType", result.Evidence.Keys);
    }

    [Fact]
    public void PositionOnly_ReturnsNotFound()
    {
        var selector = new ElementFingerprint("erp.exe", "Main", RelativeBounds: new(0.2, 0.3, 0.1, 0.1));
        var candidates = new[]
        {
            new ElementCandidate("position", new ElementFingerprint("erp.exe", "Main", RelativeBounds: new(0.2, 0.3, 0.1, 0.1)))
        };

        var result = matcher.Match(selector, candidates, new SelectorWeights());

        Assert.Equal(SelectorMatchStatus.NotFound, result.Status);
        Assert.Null(result.CandidateId);
        Assert.InRange(result.Score, 1, 10);
        Assert.Contains("relativeBounds", result.Evidence.Keys);
    }

    [Fact]
    public void CloseTopScores_ReturnsAmbiguous()
    {
        var selector = new ElementFingerprint("erp.exe", "Main", AutomationId: "add", ControlType: "Button", Name: "Add", ClassName: "ActionButton");
        var candidates = new[]
        {
            new ElementCandidate("name-match", new ElementFingerprint("erp.exe", "Main", AutomationId: "add", ControlType: "Button", Name: "Add")),
            new ElementCandidate("class-match", new ElementFingerprint("erp.exe", "Main", AutomationId: "add", ControlType: "Button", ClassName: "ActionButton"))
        };

        var result = matcher.Match(selector, candidates, new SelectorWeights());

        Assert.Equal(SelectorMatchStatus.Ambiguous, result.Status);
        Assert.Null(result.CandidateId);
        Assert.InRange(result.Score, 70, 100);
        Assert.Contains("name", result.Evidence.Keys);
    }

    [Fact]
    public void DifferentProcess_IsExcluded()
    {
        var selector = new ElementFingerprint("erp.exe", "Main", AutomationId: "add", ControlType: "Button");
        var candidates = new[]
        {
            new ElementCandidate("other-process", new ElementFingerprint("other.exe", "Main", AutomationId: "add", ControlType: "Button"))
        };

        var result = matcher.Match(selector, candidates, new SelectorWeights());

        Assert.Equal(SelectorMatchStatus.NotFound, result.Status);
        Assert.Null(result.CandidateId);
        Assert.Equal(0, result.Score);
        Assert.Empty(result.Evidence);
    }

    [Fact]
    public void DifferentWindow_IsExcluded()
    {
        var selector = new ElementFingerprint("erp.exe", "Orders", AutomationId: "add", ControlType: "Button");
        var candidates = new[]
        {
            new ElementCandidate("other-window", new ElementFingerprint("erp.exe", "Customers", AutomationId: "add", ControlType: "Button"))
        };

        var result = matcher.Match(selector, candidates, new SelectorWeights());

        Assert.Equal(SelectorMatchStatus.NotFound, result.Status);
        Assert.Null(result.CandidateId);
        Assert.Equal(0, result.Score);
        Assert.Empty(result.Evidence);
    }

    [Fact]
    public void MissingOptionalSignals_DoesNotThrow()
    {
        var selector = new ElementFingerprint("erp.exe", "Main", AutomationId: "add", ControlType: "Button");
        var candidates = new[]
        {
            new ElementCandidate("add", new ElementFingerprint("erp.exe", "Main", AutomationId: "add", ControlType: "Button"))
        };

        var result = matcher.Match(selector, candidates, new SelectorWeights());

        Assert.Equal(SelectorMatchStatus.Found, result.Status);
        Assert.Equal("add", result.CandidateId);
        Assert.InRange(result.Score, 60, 100);
        Assert.Equal(new[] { "automationId", "controlType" }, result.Evidence.Keys.OrderBy(key => key));
    }

    [Fact]
    public void SemanticSignals_OutweighAllContextSignals()
    {
        var selector = new ElementFingerprint(
            "erp.exe", "Main", AutomationId: "price", ControlType: "Edit",
            Ancestors: ImmutableArray.Create(new AncestorFingerprint(AutomationId: "form")),
            NearbyLabels: ImmutableArray.Create("Price"),
            RelativeBounds: new(0.2, 0.3, 0.2, 0.1));
        var candidates = new[]
        {
            new ElementCandidate("context", new ElementFingerprint(
                "erp.exe", "Main", Ancestors: ImmutableArray.Create(new AncestorFingerprint(AutomationId: "form")),
                NearbyLabels: ImmutableArray.Create("Price"), RelativeBounds: new(0.2, 0.3, 0.2, 0.1))),
            new ElementCandidate("semantic", new ElementFingerprint("erp.exe", "Main", AutomationId: "price", ControlType: "Edit"))
        };

        var result = matcher.Match(selector, candidates, new SelectorWeights());

        Assert.Equal(SelectorMatchStatus.Found, result.Status);
        Assert.Equal("semantic", result.CandidateId);
        Assert.InRange(result.Score, 60, 100);
        Assert.DoesNotContain("ancestors", result.Evidence.Keys);
    }

    [Fact]
    public void ContextSignals_ContributeEvidenceWithoutResolvingAlone()
    {
        var selector = new ElementFingerprint(
            "erp.exe", "Main", Ancestors: ImmutableArray.Create(new AncestorFingerprint(AutomationId: "form")),
            NearbyLabels: ImmutableArray.Create("Price"), RelativeBounds: new(0.2, 0.3, 0.2, 0.1));
        var candidates = new[]
        {
            new ElementCandidate("context", new ElementFingerprint(
                "erp.exe", "Main", Ancestors: ImmutableArray.Create(new AncestorFingerprint(AutomationId: " FORM ")),
                NearbyLabels: ImmutableArray.Create(" price "), RelativeBounds: new(0.21, 0.31, 0.2, 0.1)))
        };

        var result = matcher.Match(selector, candidates, new SelectorWeights());

        Assert.Equal(SelectorMatchStatus.NotFound, result.Status);
        Assert.Null(result.CandidateId);
        Assert.InRange(result.Score, 1, 10);
        Assert.Contains("ancestors", result.Evidence.Keys);
        Assert.Contains("nearbyLabels", result.Evidence.Keys);
        Assert.Contains("relativeBounds", result.Evidence.Keys);
    }

    [Fact]
    public void AncestorWithConflictingControlType_DoesNotContributeEvidence()
    {
        var selector = new ElementFingerprint(
            "erp.exe", "Main", AutomationId: "price", ControlType: "Edit",
            Ancestors: ImmutableArray.Create(new AncestorFingerprint(AutomationId: "form", ControlType: "Pane")));
        var candidates = new[]
        {
            new ElementCandidate("price", new ElementFingerprint(
                "erp.exe", "Main", AutomationId: "price", ControlType: "Edit",
                Ancestors: ImmutableArray.Create(new AncestorFingerprint(AutomationId: "form", ControlType: "Window"))))
        };

        var result = matcher.Match(selector, candidates, new SelectorWeights());

        Assert.Equal(SelectorMatchStatus.Found, result.Status);
        Assert.Equal("price", result.CandidateId);
        Assert.DoesNotContain("ancestors", result.Evidence.Keys);
    }

    [Fact]
    public void UnicodeEquivalentName_ContributesEvidence()
    {
        var selector = new ElementFingerprint("erp.exe", "Main", AutomationId: "customer", ControlType: "Edit", Name: "Café");
        var candidates = new[]
        {
            new ElementCandidate("customer", new ElementFingerprint("erp.exe", "Main", AutomationId: "customer", ControlType: "Edit", Name: "Cafe\u0301"))
        };

        var result = matcher.Match(selector, candidates, new SelectorWeights());

        Assert.Equal(SelectorMatchStatus.Found, result.Status);
        Assert.Equal("customer", result.CandidateId);
        Assert.Contains("name", result.Evidence.Keys);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonpositiveMinimumLead_IsRejectedBeforeTiedCandidatesCanBeSelected(int minimumLead)
    {
        var selector = new ElementFingerprint("erp.exe", "Main", AutomationId: "add", ControlType: "Button");
        var candidates = new[]
        {
            new ElementCandidate("first", new ElementFingerprint("erp.exe", "Main", AutomationId: "add", ControlType: "Button")),
            new ElementCandidate("second", new ElementFingerprint("erp.exe", "Main", AutomationId: "add", ControlType: "Button"))
        };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            matcher.Match(selector, candidates, new SelectorWeights { MinimumLead = minimumLead }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public void MinimumScoreOutsideValidRange_IsRejected(int minimumScore)
    {
        var selector = new ElementFingerprint("erp.exe", "Main", AutomationId: "add", ControlType: "Button");
        var candidates = new[]
        {
            new ElementCandidate("no-evidence", new ElementFingerprint("erp.exe", "Main", AutomationId: "other", ControlType: "Edit"))
        };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            matcher.Match(selector, candidates, new SelectorWeights { MinimumScore = minimumScore }));
    }

    [Fact]
    public void NegativeSignalWeight_IsRejected()
    {
        var selector = new ElementFingerprint("erp.exe", "Main", AutomationId: "add");
        var candidates = new[]
        {
            new ElementCandidate("add", new ElementFingerprint("erp.exe", "Main", AutomationId: "add"))
        };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            matcher.Match(selector, candidates, new SelectorWeights { AutomationId = -1 }));
    }

    [Fact]
    public void SignalWeightTotalAbove100_IsRejected()
    {
        var selector = new ElementFingerprint("erp.exe", "Main", AutomationId: "add", ControlType: "Button");
        var candidates = new[]
        {
            new ElementCandidate("add", new ElementFingerprint("erp.exe", "Main", AutomationId: "add", ControlType: "Button"))
        };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            matcher.Match(selector, candidates, new SelectorWeights { AutomationId = 101 }));
    }
}

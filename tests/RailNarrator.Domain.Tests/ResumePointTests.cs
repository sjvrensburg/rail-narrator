using RailNarrator.Domain;
using RailNarrator.TestFixtures;
using Xunit;

namespace RailNarrator.Domain.Tests;

/// <summary>
/// A saved reading position found again in a plan it was not saved against: by its place on the
/// page, since a passage id is only a number in one plan.
/// </summary>
public sealed class ResumePointTests
{
    // Six sentences, three to a page, each at its own offset in its page's text.
    private static readonly (string Text, int Page, int Start)[] Sentences =
    [
        ("The first sentence.", 0, 0), ("The second sentence.", 0, 20), ("The third sentence.", 0, 41),
        ("The fourth sentence.", 1, 0), ("The fifth sentence.", 1, 21), ("The sixth sentence.", 1, 41),
    ];

    private static ReadingUnit Unit(int i)
    {
        var (text, page, start) = Sentences[i];
        var anchor = SyntheticNarration.Anchor($"a{i}", page, new PageRect(72, 100 + i * 16, 300, 112 + i * 16),
            new TextRange(start, text.Length));
        return SyntheticNarration.ProseUnit($"u{i}", text, anchor);
    }

    private static NarrationPlan PlanOf(string planId, params int[] sentences)
    {
        var plan = SyntheticNarration.Plan(sentences.Select(Unit).ToList());
        return plan with { Header = plan.Header with { PlanId = planId } };
    }

    // The first plan reads all six; the second skips the second sentence, so every id after it
    // names the sentence after the one it named before.
    private static readonly NarrationPlan Before = PlanOf("plan-before", 0, 1, 2, 3, 4, 5);
    private static readonly NarrationPlan After = PlanOf("plan-after", 0, 2, 3, 4, 5);

    private static ReaderPosition SavedAt(NarrationPlan plan, string segmentId)
    {
        var (page, offset) = ResumePoint.Locate(plan, segmentId)!.Value;
        return new ReaderPosition("doc", segmentId, page, 0, null, MathMode.Interpret, 1, FollowMode.Follow, 1,
            ResumePoint.PlanKey(plan), offset);
    }

    private static string TextOf(NarrationPlan plan, string? id) => plan.Segment(id!)!.SpokenText;

    [Fact]
    public void APassage_IsLocatedByItsPageAndWhereItsTextStarts()
        => Assert.Equal((1, 21), ResumePoint.Locate(Before, "s00004"));

    [Fact]
    public void InThePlanItWasSavedAgainst_TheSavedPassageIsKept()
        => Assert.Equal("s00003", ResumePoint.Find(Before, SavedAt(Before, "s00003")));

    [Fact]
    public void InANewPlan_ThePassageAtTheSamePlaceIsFound_NotTheOneWithTheSameNumber()
    {
        // "s00003" was the fourth sentence; in the new plan that id is the fifth.
        var position = SavedAt(Before, "s00003");
        string? found = ResumePoint.Find(After, position);
        Assert.Equal("The fourth sentence.", TextOf(After, found));
        Assert.Equal("s00002", found);
    }

    [Fact]
    public void APassageTheNewPlanNoLongerReads_ResumesAtTheOneAfterIt()
        => Assert.Equal("The third sentence.", TextOf(After, ResumePoint.Find(After, SavedAt(Before, "s00001"))));

    [Fact]
    public void ThePassageAfterASkippedOne_MayBeOnTheNextPage()
    {
        var withoutPageEnd = PlanOf("plan-short", 0, 1, 3, 4, 5);
        Assert.Equal("The fourth sentence.", TextOf(withoutPageEnd, ResumePoint.Find(withoutPageEnd, SavedAt(Before, "s00002"))));
    }

    [Fact]
    public void WithNothingReadAfterIt_ThePassageBeforeIsResumed_NotTheStart()
    {
        // The last sentence is no longer read (say, it is in the references, now skipped).
        var withoutTheEnd = PlanOf("plan-no-end", 0, 1, 2, 3, 4);
        Assert.Equal("The fifth sentence.", TextOf(withoutTheEnd, ResumePoint.Find(withoutTheEnd, SavedAt(Before, "s00005"))));
    }

    [Fact]
    public void APositionSavedBeforeItsPlanWasKept_KeepsItsPassageId()
    {
        var old = SavedAt(Before, "s00003") with { PlanId = null, SourceCharOffset = null, PageIndex = 0 };
        Assert.Equal("s00003", ResumePoint.Find(After, old));
        Assert.Null(ResumePoint.Find(After, old with { SegmentId = "s00099" }));
    }

    [Fact]
    public void APassageWithNoTextBehindIt_IsFoundByItsPage()
    {
        // An equation from pixels: an anchor on page 1 with no characters.
        var visual = SyntheticNarration.MathsUnit("m", "x^2", SyntheticNarration.Anchor("am", 1, new PageRect(72, 400, 300, 420)));
        var withEquation = SyntheticNarration.Plan([Unit(0), Unit(1), visual, Unit(3)]);
        withEquation = withEquation with { Header = withEquation.Header with { PlanId = "plan-eq" } };
        var position = SavedAt(withEquation, "s00002");
        Assert.Equal((1, (int?)null), ResumePoint.Locate(withEquation, "s00002"));

        Assert.Equal("The fourth sentence.", TextOf(After, ResumePoint.Find(After, position)));
    }

    [Fact]
    public void APlanWithTheSameIdButOtherInputs_IsANewPlan()
    {
        // Only the filters changed (say, the references are now read): the plan id is the same,
        // the passages are not.
        var refiltered = After with { Header = After.Header with { PlanId = "plan-before", InputFingerprint = "references-read" } };
        Assert.Equal("The fourth sentence.", TextOf(refiltered, ResumePoint.Find(refiltered, SavedAt(Before, "s00003"))));
    }

    [Fact]
    public void NothingSaved_StartsAtTheBeginning()
        => Assert.Null(ResumePoint.Find(After, SavedAt(Before, "s00000") with { SegmentId = null }));
}

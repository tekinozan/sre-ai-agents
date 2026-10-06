using SreAiAgent.Domain.Findings;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Domain.Tests.Findings;

public sealed class ReportFeedbackTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ARevisionChangesTheVerdictButKeepsTheFirstSubmissionTime()
    {
        ReportFeedback first = ReportFeedback.Create(
            "acme", IncidentId.New(), 1, InvestigationOutcome.Complete, ReportVerdict.Confirmed, null, "  looked right  ", "alice", Now);

        ReportFeedback revised = first.Revise(ReportVerdict.Incorrect, "DNS, not the pool", null, Now.AddHours(1));

        first.Comment.ShouldBe("looked right");
        revised.Id.ShouldBe(first.Id);
        revised.Verdict.ShouldBe(ReportVerdict.Incorrect);
        revised.ActualRootCause.ShouldBe("DNS, not the pool");
        revised.Comment.ShouldBeNull();
        revised.SubmittedAt.ShouldBe(Now);
        revised.UpdatedAt.ShouldBe(Now.AddHours(1));
    }

    [Fact]
    public void TextLongerThanTheLimitIsRefused()
    {
        Should.Throw<DomainException>(() => ReportFeedback.Create(
            "acme", IncidentId.New(), 1, InvestigationOutcome.Complete, ReportVerdict.Incorrect,
            new string('x', ReportFeedback.MaximumTextLength + 1), null, "alice", Now))
            .Code.ShouldBe(DomainErrorCodes.ReportFeedbackInvalid);
    }

    [Theory]
    [InlineData(0, ReportVerdict.Confirmed)]
    [InlineData(1, (ReportVerdict)7)]
    public void AnImpossibleVersionOrVerdictIsRefused(int version, ReportVerdict verdict)
        => Should.Throw<DomainException>(() => ReportFeedback.Create(
            "acme", IncidentId.New(), version, InvestigationOutcome.Complete, verdict, null, null, "alice", Now))
            .Code.ShouldBe(DomainErrorCodes.ReportFeedbackInvalid);
}

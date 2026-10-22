using EventTicketing.Tooling.DeviceSimulator;

namespace EventTicketing.Tooling.ScalingLab.Tests;

/// <summary>T8's verdict (ADR-0015, D64): 99% of the syncing devices' answers within 500 ms, a failure counting as a miss; a revocation at every device within 30 s.</summary>
public sealed class DeviceReportTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 22, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void T8_holds_when_99_of_100_answers_come_within_500_ms()
    {
        var samples = Answers(99, TimeSpan.FromMilliseconds(40)).Append(Answer(TimeSpan.FromSeconds(2))).ToList();

        Report(samples).Should().Match<Report>(report => report.AnswersHold && report.Holds);
    }

    [Fact]
    public void A_failed_request_is_an_answer_that_missed()
    {
        var samples = Answers(98, TimeSpan.FromMilliseconds(40)).Append(Answer(TimeSpan.FromMilliseconds(40), ok: false)).Append(Answer(TimeSpan.FromSeconds(2))).ToList();

        Report(samples).AnswersHold.Should().BeFalse("only 98 of 100 were answered in time");
    }

    [Fact]
    public void The_first_downloads_and_what_they_slow_down_are_reported_apart()
    {
        var samples = Answers(100, TimeSpan.FromMilliseconds(40))
            .Append(new Sample(Kind.Download, TimeSpan.FromSeconds(3), true, At: Start.AddSeconds(1)))
            .Append(new Sample(Kind.Sync, TimeSpan.FromSeconds(3), true, At: Start.AddSeconds(2)))
            .ToList();

        var report = Report(samples);

        report.Steady.Should().HaveCount(100);
        report.Start.Should().ContainSingle();
        report.AnswersHold.Should().BeTrue();
    }

    [Fact]
    public void The_revocation_must_reach_every_device_within_30_s()
    {
        var probe = new RevocationProbe(Guid.NewGuid(), 0);
        var samples = Answers(100, TimeSpan.FromMilliseconds(40));

        Report([.. samples], probe, [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(6)], devices: 2).RevocationHolds.Should().BeTrue();
        Report([.. samples], probe, [TimeSpan.FromSeconds(5)], devices: 2).RevocationHolds.Should().BeFalse("one device never had it");
        Report([.. samples], probe, [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(31)], devices: 2).RevocationHolds.Should().BeFalse();
    }

    private static Report Report(IReadOnlyList<Sample> samples, RevocationProbe? probe = null, IReadOnlyList<TimeSpan>? seen = null, int devices = 200) =>
        new(devices, samples, probe, seen ?? [], TimeSpan.FromSeconds(90), Start, Start.AddSeconds(10));

    private static IEnumerable<Sample> Answers(int count, TimeSpan took) => Enumerable.Range(0, count).Select(_ => Answer(took));

    private static Sample Answer(TimeSpan took, bool ok = true) => new(Kind.Sync, took, ok, ok ? null : "HTTP 503", Start.AddSeconds(30));
}

using OBDim.Monitoring.Models;
using OBDim.Monitoring.Services;
using Xunit;

namespace OBDim.Tests.Monitoring;

public class FreshnessEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    private static ProviderSnapshot Snapshot(DateTimeOffset? succeeded, DateTimeOffset? resetAt) => new()
    {
        Provider = ProviderId.Codex,
        IdentityKey = "k",
        IdentityVerified = true,
        AttemptedAtUtc = succeeded ?? Now,
        SucceededAtUtc = succeeded,
        Buckets =
        [
            new QuotaBucket
            {
                SourceKey = "codex",
                Windows =
                [
                    new QuotaWindow { SourceKey = "primary", UsedPercent = 10, RemainingPercent = 90, ResetsAtUtc = resetAt, HasAnyQuotaField = true },
                ],
            },
        ],
    };

    [Fact]
    public void FreshSnapshot_UnderFifteenMinutes_IsNotStale()
    {
        var s = Snapshot(Now.AddMinutes(-14), Now.AddHours(3));
        Assert.False(FreshnessEvaluator.IsStale(s, Now));
    }

    /// <summary>R05/A04: 过了 15 分钟必须明确过期 — 不能给旧数据无限续期。</summary>
    [Fact]
    public void SnapshotOlderThanFifteenMinutes_IsStale()
    {
        var s = Snapshot(Now.AddMinutes(-16), Now.AddHours(3));
        Assert.True(FreshnessEvaluator.IsStale(s, Now));
    }

    /// <summary>§5.2(4): 重置点已过而未复核 → 过期，剩余额度不得自动改满。</summary>
    [Fact]
    public void PassedResetPointWithoutRecheck_IsStale()
    {
        var s = Snapshot(Now.AddMinutes(-1), Now.AddSeconds(-30));
        Assert.True(FreshnessEvaluator.IsStale(s, Now));
    }

    [Fact]
    public void NeverSucceeded_IsStale()
    {
        var s = Snapshot(null, Now.AddHours(3));
        Assert.True(FreshnessEvaluator.IsStale(s, Now));
    }

    [Fact]
    public void MemorySample_StaleAfterThirtySeconds()
    {
        var sample = new MemorySample
        {
            SampledAtUtc = Now.AddSeconds(-29),
            PhysicalTotalBytes = 1,
            PhysicalAvailableBytes = 1,
            CommitTotalBytes = 1,
            CommitLimitBytes = 1,
        };
        Assert.False(FreshnessEvaluator.IsStale(sample, Now));

        var old = sample with { SampledAtUtc = Now.AddSeconds(-31) };
        Assert.True(FreshnessEvaluator.IsStale(old, Now));
        Assert.True(FreshnessEvaluator.IsStale((MemorySample?)null, Now));
    }
}

/// <summary>R09/A08: threshold levels, dedupe, two-level jump, reset re-arm, exclusions.</summary>
public class ReminderEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Reset = Now.AddHours(3);

    private static ProviderSnapshot Snapshot(
        double? remainingPercent, DateTimeOffset? resetAt = null,
        bool verified = true, bool fresh = true, bool omitReset = false)
    {
        resetAt ??= Reset;
        if (omitReset) resetAt = null;
        return new ProviderSnapshot
        {
            Provider = ProviderId.Codex,
            IdentityKey = "ident",
            IdentityVerified = verified,
            AttemptedAtUtc = fresh ? Now.AddMinutes(-1) : Now.AddMinutes(-30),
            SucceededAtUtc = fresh ? Now.AddMinutes(-1) : null,
            Buckets =
            [
                new QuotaBucket
                {
                    SourceKey = "codex",
                    Windows =
                    [
                        new QuotaWindow
                        {
                            SourceKey = "primary",
                            UsedPercent = remainingPercent is null ? null : 100 - remainingPercent,
                            RemainingPercent = remainingPercent,
                            ResetsAtUtc = resetAt,
                            HasAnyQuotaField = remainingPercent is not null,
                        },
                    ],
                },
            ],
        };
    }

    [Fact]
    public void FreshLowRemaining_FiresOnce_SecondEvaluationSilent()
    {
        var marks = new HashSet<string>();
        var s = Snapshot(18);

        var first = ReminderEvaluator.Evaluate(s, marks, Now);
        var second = ReminderEvaluator.Evaluate(s, marks, Now);

        var evt = Assert.Single(first);
        Assert.Equal(1, evt.Level);
        Assert.Equal(18, evt.RemainingPercent);
        Assert.Empty(second);
    }

    /// <summary>一次刷新跨过两档只发最严重的一条 (spec §7)。</summary>
    [Fact]
    public void JumpAcrossBothLevels_FiresOnlySevere()
    {
        var marks = new HashSet<string>();
        var events = ReminderEvaluator.Evaluate(Snapshot(3), marks, Now);

        var evt = Assert.Single(events);
        Assert.Equal(2, evt.Level);
        // The mild level is marked fired too, so a later 12% reading does not re-alert.
        Assert.Contains(marks, k => k.EndsWith("level1"));
        var later = ReminderEvaluator.Evaluate(Snapshot(12), marks, Now);
        Assert.Empty(later);
    }

    /// <summary>窗口重置后（新的重置点）重新计数。</summary>
    [Fact]
    public void NewResetPoint_RearsAlert()
    {
        var marks = new HashSet<string>();
        ReminderEvaluator.Evaluate(Snapshot(18, Now.AddHours(3)), marks, Now);

        var afterReset = ReminderEvaluator.Evaluate(Snapshot(18, Now.AddHours(27)), marks, Now);
        Assert.Single(afterReset);
    }

    [Fact]
    public void HealthyRemaining_FiresNothing()
    {
        var marks = new HashSet<string>();
        Assert.Empty(ReminderEvaluator.Evaluate(Snapshot(80), marks, Now));
    }

    [Fact]
    public void NoResetPoint_NeverFires_PanelOnly()
    {
        var marks = new HashSet<string>();
        Assert.Empty(ReminderEvaluator.Evaluate(Snapshot(3, omitReset: true), marks, Now));
    }

    [Fact]
    public void OutOfRangePercent_NeverFires()
    {
        var marks = new HashSet<string>();
        // Simulates parser drift: the window is flagged out-of-range AND still carries a
        // low remaining value. The evaluator must refuse flagged data no matter what the
        // remaining number claims — this is what makes the PercentOutOfRange guard a real
        // behavior gate rather than decoration (mutation-verified red in Debug+Release).
        var s = Snapshot(remainingPercent: 3);
        s = s with
        {
            Buckets =
            [
                s.Buckets[0] with
                {
                    Windows =
                    [
                        new QuotaWindow { SourceKey = "primary", UsedPercent = 140, RemainingPercent = 3, PercentOutOfRange = true, ResetsAtUtc = Reset, HasAnyQuotaField = true },
                    ],
                },
            ],
        };
        Assert.Empty(ReminderEvaluator.Evaluate(s, marks, Now));
    }

    [Fact]
    public void UnverifiedIdentity_NeverFires()
    {
        var marks = new HashSet<string>();
        Assert.Empty(ReminderEvaluator.Evaluate(Snapshot(3, verified: false), marks, Now));
    }

    [Fact]
    public void StaleSnapshot_NeverFires()
    {
        var marks = new HashSet<string>();
        Assert.Empty(ReminderEvaluator.Evaluate(Snapshot(3, fresh: false), marks, Now));
    }

    /// <summary>A09: 身份变化后旧 marks 不得延续 — 由协调器换 key 实现，这里验证 key 本身带身份维度。</summary>
    [Fact]
    public void ReminderKey_IncludesProviderBucketWindowReset()
    {
        var w = new QuotaWindow { SourceKey = "primary", ResetsAtUtc = Reset, HasAnyQuotaField = true };
        Assert.True(w.TryGetReminderKey("Codex", "codex", out var key));
        Assert.Contains("Codex", key);
        Assert.Contains("codex", key);
        Assert.Contains("primary", key);
        Assert.Contains(Reset.UtcTicks.ToString(), key);

        var noReset = new QuotaWindow { SourceKey = "primary", HasAnyQuotaField = true };
        Assert.False(noReset.TryGetReminderKey("Codex", "codex", out _));
    }
}

/// <summary>Backoff ladder, pause-on-auth, manual gating (spec §7 defaults).</summary>
public class ProviderRefreshStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    private static ProviderSnapshot Ok() => new()
    {
        Provider = ProviderId.Codex, IdentityKey = "k", IdentityVerified = true,
        AttemptedAtUtc = Now, SucceededAtUtc = Now,
        Buckets = [new QuotaBucket { SourceKey = "b" }],
    };

    private static ProviderSnapshot Fail(ProviderErrorKind kind) => new()
    {
        Provider = ProviderId.Codex, IdentityKey = "k", IdentityVerified = false,
        AttemptedAtUtc = Now, Error = kind,
    };

    [Fact]
    public void Success_StartsFiveMinuteCycle()
    {
        var state = new ProviderRefreshState();
        state.RecordAttempt(Ok(), Now);
        Assert.Equal(Now.AddMinutes(5), state.NextAutoAttemptUtc);
        Assert.True(state.IsDueForAutoRefresh(Now.AddMinutes(6)));
        Assert.False(state.IsDueForAutoRefresh(Now.AddMinutes(4)));
    }

    /// <summary>网络失败退避 5、10、20、30 分钟，上限 30；成功后恢复 5 分钟。</summary>
    [Fact]
    public void RepeatedFailures_ClimbBackoffLadder_CappedAt30()
    {
        var state = new ProviderRefreshState();

        state.RecordAttempt(Fail(ProviderErrorKind.ApiError), Now);
        Assert.Equal(Now.AddMinutes(5), state.NextAutoAttemptUtc);

        state.RecordAttempt(Fail(ProviderErrorKind.ApiError), Now);
        Assert.Equal(Now.AddMinutes(10), state.NextAutoAttemptUtc);

        state.RecordAttempt(Fail(ProviderErrorKind.Timeout), Now);
        Assert.Equal(Now.AddMinutes(20), state.NextAutoAttemptUtc);

        state.RecordAttempt(Fail(ProviderErrorKind.ExecutionFailed), Now);
        Assert.Equal(Now.AddMinutes(30), state.NextAutoAttemptUtc);

        state.RecordAttempt(Fail(ProviderErrorKind.ParseFailed), Now);
        Assert.Equal(Now.AddMinutes(30), state.NextAutoAttemptUtc);

        state.RecordAttempt(Ok(), Now);
        Assert.Equal(Now.AddMinutes(5), state.NextAutoAttemptUtc);
    }

    /// <summary>配置/认证失败暂停自动重试；其他来源不受影响（per-provider state）。</summary>
    [Fact]
    public void AuthOrConfigFailure_PausesAutoRetry()
    {
        foreach (var kind in new[] { ProviderErrorKind.NotSignedIn, ProviderErrorKind.CliNotFound, ProviderErrorKind.UnsupportedEntry })
        {
            var state = new ProviderRefreshState();
            state.RecordAttempt(Fail(kind), Now);
            Assert.True(state.PausedUntilUserRetry, kind.ToString());
            Assert.False(state.IsDueForAutoRefresh(Now.AddHours(1)));
        }
    }

    [Fact]
    public void ManualRefresh_RespectsFifteenSecondInterval()
    {
        var state = new ProviderRefreshState();
        Assert.True(state.IsManualRefreshAllowed(Now));

        state.RecordManualStart(Now);
        Assert.False(state.IsManualRefreshAllowed(Now.AddSeconds(5)), "连续点击不能叠加进程 (spec §7)");
        Assert.False(state.IsManualRefreshAllowed(Now.AddSeconds(14)));
        Assert.True(state.IsManualRefreshAllowed(Now.AddSeconds(15)));

        // Manual bypasses pause: the user's explicit retry wins.
        state.RecordAttempt(Fail(ProviderErrorKind.NotSignedIn), Now);
        Assert.True(state.IsManualRefreshAllowed(Now.AddSeconds(30)));
    }

    [Fact]
    public void ManualResetPause_ClearsPause()
    {
        var state = new ProviderRefreshState();
        state.RecordAttempt(Fail(ProviderErrorKind.NotSignedIn), Now);
        state.ResetPause();
        Assert.False(state.PausedUntilUserRetry);
    }
}

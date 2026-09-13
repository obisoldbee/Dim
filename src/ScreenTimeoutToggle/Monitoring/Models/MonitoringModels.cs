namespace OBDim.Monitoring.Models;

/// <summary>The three monitored AI subscription sources.</summary>
public enum ProviderId
{
    Codex = 0,
    MiniMax = 1,
    Ark = 2,
}

/// <summary>
/// Failure classification for a provider refresh. Kept coarse on purpose: the UI maps
/// each kind to one actionable sentence, the log records the sanitized detail. The
/// mapping must never invent a cause the CLI output does not support — when in doubt,
/// <see cref="ExecutionFailed"/>.
/// </summary>
public enum ProviderErrorKind
{
    /// <summary>No error.</summary>
    None = 0,
    /// <summary>The CLI executable could not be located (neither configured path nor PATH).</summary>
    CliNotFound = 1,
    /// <summary>The CLI entry exists but cannot be launched safely (e.g. an unparsable .cmd shim).</summary>
    UnsupportedEntry = 2,
    /// <summary>The CLI ran but the account is not signed in / the session expired.</summary>
    NotSignedIn = 3,
    /// <summary>The CLI ran and reported a provider-side API error (network, server, auth backend).</summary>
    ApiError = 4,
    /// <summary>The CLI ran but exited non-zero for a reason we cannot classify.</summary>
    ExecutionFailed = 5,
    /// <summary>The CLI did not finish within the total timeout.</summary>
    Timeout = 6,
    /// <summary>Combined stdout+stderr exceeded the output cap.</summary>
    OutputLimitExceeded = 7,
    /// <summary>Output was not parseable under the known schema.</summary>
    ParseFailed = 8,
    /// <summary>The refresh was cancelled (app exit, settings change).</summary>
    Cancelled = 9,
    /// <summary>The provider is disabled in monitoring settings (nothing was run).</summary>
    Disabled = 10,
}

/// <summary>
/// One quota window inside a bucket (e.g. the 5-hour window or the weekly window).
/// Percent direction is normalized here: <see cref="UsedPercent"/> is ALWAYS "used",
/// regardless of what direction the source reported. A window that carries no usable
/// quota field at all has <see cref="HasAnyQuotaField"/> = false and is displayed as
/// "not provided" — never as 0% or 100%.
/// </summary>
public sealed record QuotaWindow
{
    /// <summary>Stable source key within the bucket ("primary", "5h", "weekly", …).</summary>
    public required string SourceKey { get; init; }

    /// <summary>Source-provided window label, when one exists (limitName, period label).</summary>
    public string? Label { get; init; }

    public long? WindowDurationMinutes { get; init; }

    /// <summary>Used percent as reported by the source, NOT clamped. Out-of-range values are flagged, not corrected.</summary>
    public double? UsedPercent { get; init; }

    /// <summary>Derived remaining percent (100 − used). Null when used is absent or out of range.</summary>
    public double? RemainingPercent { get; init; }

    /// <summary>True when the source reported a used/remaining percent outside 0–100.</summary>
    public bool PercentOutOfRange { get; init; }

    /// <summary>UTC reset time as reported. Never guessed from the local clock.</summary>
    public DateTimeOffset? ResetsAtUtc { get; init; }

    /// <summary>Absolute used amount, only when the source provides one (rendered verbatim).</summary>
    public string? UsedText { get; init; }

    /// <summary>Absolute total, only when the source provides one.</summary>
    public string? TotalText { get; init; }

    /// <summary>False when the source returned no usable quota field for this window.</summary>
    public bool HasAnyQuotaField { get; init; }

    /// <summary>
    /// The dedupe/reset identity of this window: provider + bucket + window + reset point.
    /// Windows without a reset point return false — the reminder rules refuse them.
    /// </summary>
    public bool TryGetReminderKey(string provider, string bucketKey, out string key)
    {
        key = "";
        if (!ResetsAtUtc.HasValue) return false;
        key = $"{provider}|{bucketKey}|{SourceKey}|{ResetsAtUtc.Value.UtcTicks}";
        return true;
    }
}

/// <summary>
/// One subscription or one metered bucket within a subscription. For Codex these are the
/// rate-limit aliases (e.g. "codex", "codex_bengalfox"); for MiniMax one per model;
/// for Ark one per product subscription (agent-plan / coding-plan / team variants).
/// </summary>
public sealed record QuotaBucket
{
    public required string SourceKey { get; init; }

    /// <summary>Source-provided display name (limitName, model name, product id). Never an account identity.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Plan tier/edition tag when the source provides one ("pro", "personal", "medium", …).</summary>
    public string? Tier { get; init; }

    /// <summary>Seat identifier for team subscriptions (Ark). Never stored in logs.</summary>
    public string? SeatId { get; init; }

    /// <summary>Explicit subscription flag. Null = the source does not declare it (display "not returned").</summary>
    public bool? Subscribed { get; init; }

    /// <summary>Per-bucket failure (Ark partial results). Other buckets stay valid.</summary>
    public string? Error { get; init; }

    public IReadOnlyList<QuotaWindow> Windows { get; init; } = [];
}

/// <summary>
/// A read-only Codex rate-limit reset credit. Display only — OB Dim never consumes one.
/// </summary>
public sealed record ResetCredit
{
    public required string Title { get; init; }
    public string? Status { get; init; }
    public DateTimeOffset? ExpiresAtUtc { get; init; }
}

/// <summary>
/// Codex reset-credit summary. <see cref="Details"/> is null when only the count is known
/// (details not fetched / backend omitted them) — an EMPTY list means details were fetched
/// and none are available. The count is never inferred from the list length.
/// </summary>
public sealed record ResetCreditSummary
{
    public required int AvailableCount { get; init; }
    public IReadOnlyList<ResetCredit>? Details { get; init; }
}

/// <summary>
/// Normalized result of one provider refresh. A snapshot is only created from a real CLI
/// conversation; there is no code path that fabricates one. Failure snapshots carry
/// <see cref="Error"/> and no buckets — the coordinator keeps the previous good snapshot
/// separately, so a failed refresh can never masquerade as fresh data.
/// </summary>
public sealed record ProviderSnapshot
{
    public required ProviderId Provider { get; init; }

    /// <summary>CLI version string when it could be probed (informational only).</summary>
    public string? CliVersion { get; init; }

    /// <summary>
    /// Non-reversible cache identity key (sha256 over provider + account identifiers +
    /// execution context). Never an email or account id in the clear.
    /// </summary>
    public required string IdentityKey { get; init; }

    /// <summary>Masked, human-readable account context for the panel (e.g. "pro · o***@gmail.com").</summary>
    public string? IdentityDisplay { get; init; }

    /// <summary>
    /// False when the provider response carries no stable identity evidence (MiniMax).
    /// Unverified identities never reuse cross-session cache values.
    /// </summary>
    public required bool IdentityVerified { get; init; }

    public required DateTimeOffset AttemptedAtUtc { get; init; }

    /// <summary>Set only when the refresh produced at least one valid bucket. Failures never touch it.</summary>
    public DateTimeOffset? SucceededAtUtc { get; init; }

    public ProviderErrorKind Error { get; init; } = ProviderErrorKind.None;

    /// <summary>Sanitized, actionable error detail. Raw stderr never lands here.</summary>
    public string? ErrorMessage { get; init; }

    public IReadOnlyList<QuotaBucket> Buckets { get; init; } = [];

    /// <summary>Codex reset credits; null for providers without the concept.</summary>
    public ResetCreditSummary? ResetCredits { get; init; }

    /// <summary>
    /// True when the response is an authoritative partial result (Ark per-bucket errors):
    /// successful buckets are fresh, errored buckets are not — and keep their old timestamps.
    /// </summary>
    public bool IsPartial { get; init; }

    public bool HasError => Error != ProviderErrorKind.None;
}

/// <summary>
/// One successful Windows memory sample. Failed samples are NOT represented as fake
/// zero-filled records — the coordinator keeps the error and the last sample separately.
/// </summary>
public sealed record MemorySample
{
    public required DateTimeOffset SampledAtUtc { get; init; }

    public required ulong PhysicalTotalBytes { get; init; }

    /// <summary>Available physical memory (reclaimable pages included — not "free" memory).</summary>
    public required ulong PhysicalAvailableBytes { get; init; }

    /// <summary>Committed bytes (page-file backed). Distinct from physical RAM usage.</summary>
    public required ulong CommitTotalBytes { get; init; }

    public required ulong CommitLimitBytes { get; init; }

    /// <summary>System low-memory resource notification state. Null = unknown (query failed).</summary>
    public bool? LowMemorySignal { get; init; }

    public ulong PhysicalUsedBytes => PhysicalTotalBytes - PhysicalAvailableBytes;
}

/// <summary>Per-provider monitoring settings (the "monitoring.json" shape).</summary>
public sealed record ProviderSettings
{
    public ProviderId Id { get; init; }

    /// <summary>Upgrades default to disabled — the user opts in per provider.</summary>
    public bool Enabled { get; init; }

    /// <summary>Optional explicit CLI path. Invalid explicit paths are an error, never silently replaced.</summary>
    public string? CliPath { get; init; }
}

/// <summary>
/// Independent monitoring configuration, persisted as monitoring.json next to config.json.
/// Corrupt monitoring files are backed up and reset WITHOUT touching the original
/// screen-timeout config.
/// </summary>
public sealed record MonitoringSettings
{
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Memory monitoring defaults to on.</summary>
    public bool MemoryEnabled { get; init; } = true;

    /// <summary>Quota reminders default to off.</summary>
    public bool RemindersEnabled { get; init; } = false;

    /// <summary>
    /// Global hotkey that toggles the monitoring popover, as one string
    /// ("Ctrl+Alt+D"). Empty/invalid strings fall back to the default at registration.
    /// </summary>
    public string PopoverHotkey { get; init; } = "Ctrl+Alt+D";

    /// <summary>
    /// What a LEFT click on the tray icon does. The 2026-09-13 user feedback moved the
    /// default to opening the popover (mode switching stays on the context menu and the
    /// mode-switch hotkey); turning this off restores the original toggle behaviour.
    /// </summary>
    public bool LeftClickOpensPopover { get; init; } = true;

    public IReadOnlyList<ProviderSettings> Providers { get; init; } = CreateDefaultProviders();

    public ProviderSettings Provider(ProviderId id) =>
        Providers.FirstOrDefault(p => p.Id == id) ?? new ProviderSettings { Id = id, Enabled = false };

    public static IReadOnlyList<ProviderSettings> CreateDefaultProviders() =>
    [
        new ProviderSettings { Id = ProviderId.Codex, Enabled = false },
        new ProviderSettings { Id = ProviderId.MiniMax, Enabled = false },
        new ProviderSettings { Id = ProviderId.Ark, Enabled = false },
    ];

    public static MonitoringSettings CreateDefault() => new();
}

/// <summary>What the UI should show for one provider — snapshot + live refresh state + freshness.</summary>
public sealed record ProviderDisplayState
{
    public required ProviderId Provider { get; init; }

    public bool Enabled { get; init; }

    /// <summary>Refresh currently in flight. The previous snapshot stays visible while it runs.</summary>
    public bool Refreshing { get; init; }

    /// <summary>The last good snapshot, when one exists (may be from cache or a previous refresh).</summary>
    public ProviderSnapshot? LastGood { get; init; }

    /// <summary>The failure of the most recent attempt, when it failed (Error != None).</summary>
    public ProviderSnapshot? LastAttempt { get; init; }

    /// <summary>True when LastGood exists but is past the freshness horizon.</summary>
    public bool Stale { get; init; }

    /// <summary>Scheduled auto-refresh is paused (auth/config failure) until the user retries.</summary>
    public bool PausedUntilUserRetry { get; init; }
}

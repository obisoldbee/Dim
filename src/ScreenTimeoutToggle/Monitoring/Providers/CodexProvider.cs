using System.Text;
using System.Text.Json;
using OBDim.Monitoring.Infrastructure;
using OBDim.Monitoring.Models;

namespace OBDim.Monitoring.Providers;

/// <summary>
/// Codex provider: one short-lived `codex app-server` stdio session per refresh
/// (initialize → initialized → account/read → account/rateLimits/read), then stdin is
/// closed and the process is reaped within the kill grace (plan §2.1). No thread/turn
/// methods are ever called. Method names and payload shapes were verified against
/// codex-cli 0.154.0's generated schema (GetAccountResponse / GetAccountRateLimitsResponse).
/// </summary>
public sealed class CodexProvider : IProviderAdapter
{
    public const string ClientName = "obdim";

    private readonly IClock _clock;
    private readonly ICliProcessRunner _runner;
    private readonly TimeSpan _sessionTimeout;
    private volatile string? _cliVersion;
    private readonly SemaphoreSlim _versionProbeLock = new(1, 1);

    public CodexProvider(IClock clock, ICliProcessRunner runner, TimeSpan? sessionTimeout = null)
    {
        _clock = clock;
        _runner = runner;
        _sessionTimeout = sessionTimeout ?? TimeSpan.FromSeconds(30);
    }

    public ProviderId Id => ProviderId.Codex;

    public async Task<ProviderSnapshot> QueryAsync(ProviderSettings settings, CancellationToken cancellationToken)
    {
        var attemptedAt = _clock.UtcNow;
        var location = CliLocator.Locate(settings.CliPath, "codex");
        if (!location.Found)
        {
            return SnapshotFactory.Failure(
                ProviderId.Codex,
                location.Error == "cli.locate_configured_missing" ? ProviderErrorKind.CliNotFound : ProviderErrorKind.CliNotFound,
                location.Error == "cli.locate_configured_missing" ? "monitor.error.configured_path_missing" : "monitor.error.cli_not_installed",
                attemptedAt);
        }

        await EnsureVersionAsync(location, cancellationToken).ConfigureAwait(false);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_sessionTimeout);

        var request = new CliRequest
        {
            ExePath = location.ExePath!,
            NodeScriptPath = location.NodeScriptPath,
            Arguments = ["app-server"],
            Timeout = _sessionTimeout,
        };

        using var process = ManagedProcess.Start(request);
        if (process.LaunchError is not null)
        {
            return SnapshotFactory.Failure(
                ProviderId.Codex, ProviderErrorKind.ExecutionFailed, $"launch failed ({process.LaunchError})", attemptedAt, _cliVersion);
        }

        var channel = new RpcChannel(process, request.MaxOutputBytes);
        var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token);
        var pumpTask = process.PumpStdoutLinesAsync(line => channel.OnLineAsync(line), pumpCts.Token);

        try
        {
            var initResponse = await channel.SendAsync(
                0, "initialize",
                new { clientInfo = new { name = ClientName, title = "OB Dim", version = _cliVersion ?? "0.0.0" } },
                timeoutCts.Token).ConfigureAwait(false);
            if (initResponse.Error is not null)
            {
                return Fail(ProviderErrorKind.ExecutionFailed, "initialize rejected by app-server");
            }

            channel.NotifyAsync("initialized");

            // account/read takes an empty params OBJECT on 0.154.0 — `null` produced an
            // error frame in the real-machine trial, while {} worked in the probe.
            var accountResponse = await channel.SendAsync(1, "account/read", new { }, timeoutCts.Token).ConfigureAwait(false);
            var rateLimitsResponse = await channel.SendAsync(2, "account/rateLimits/read", new { }, timeoutCts.Token).ConfigureAwait(false);

            if (accountResponse.Error is not null || rateLimitsResponse.Error is not null)
            {
                var which = accountResponse.Error is not null ? "account/read" : "account/rateLimits/read";
                return Fail(ProviderErrorKind.ExecutionFailed, $"{which} returned an error frame");
            }

            var mapped = CodexRateLimitsMapper.Map(rateLimitsResponse.Result!.Value);
            if (!mapped.Ok)
            {
                return Fail(ProviderErrorKind.ParseFailed, mapped.Error);
            }

            var (email, planType, accountType) = CodexAccountMapper.Map(accountResponse.Result ?? default);
            if (accountResponse.Result is null)
            {
                return Fail(ProviderErrorKind.ParseFailed, "account/read result missing");
            }

            var requiresAuth = accountResponse.Result.Value.TryGetProperty("requiresOpenaiAuth").AsNullableBool();
            if (requiresAuth == true && accountType is null)
            {
                return Fail(ProviderErrorKind.NotSignedIn, null);
            }

            var identityKey = MonitoringIdentity.Hash("codex", mapped.AccountId, email);
            var verified = mapped.AccountId is not null || email is not null;
            var display = ComposeIdentityDisplay(planType, email);

            return new ProviderSnapshot
            {
                Provider = ProviderId.Codex,
                CliVersion = _cliVersion,
                IdentityKey = identityKey,
                IdentityVerified = verified,
                IdentityDisplay = display,
                AttemptedAtUtc = attemptedAt,
                SucceededAtUtc = _clock.UtcNow,
                Buckets = mapped.Buckets,
                ResetCredits = mapped.ResetCredits,
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Only our session timeout fired — the app itself is not exiting.
            return SnapshotFactory.Failure(ProviderId.Codex, ProviderErrorKind.Timeout, null, attemptedAt, _cliVersion);
        }
        catch (OperationCanceledException)
        {
            return SnapshotFactory.Failure(ProviderId.Codex, ProviderErrorKind.Cancelled, null, attemptedAt, _cliVersion);
        }
        catch (IOException)
        {
            return Fail(ProviderErrorKind.ExecutionFailed, "stdout stream broke before the conversation finished");
        }
        finally
        {
            pumpCts.Cancel();
            process.CloseStdin();
            await process.WaitForExitAfterCloseAsync(request.KillGrace, CancellationToken.None).ConfigureAwait(false);
            try { await pumpTask.ConfigureAwait(false); } catch { /* pump ends with the process */ }
        }

        ProviderSnapshot Fail(ProviderErrorKind kind, string? detail) =>
            SnapshotFactory.Failure(ProviderId.Codex, kind, detail, attemptedAt, _cliVersion);
    }

    private async Task EnsureVersionAsync(CliLocation location, CancellationToken cancellationToken)
    {
        if (_cliVersion is not null) return;
        await _versionProbeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cliVersion is not null) return;
            var result = await _runner.RunAsync(
                CliRequest.ForLocation(location, [location.VersionArgs ?? "--version"]),
                cancellationToken).ConfigureAwait(false);
            var version = result.Success ? FirstLine(result.Stdout)?.Trim() : null;
            _cliVersion = string.IsNullOrEmpty(version) ? null : version;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Version is informational; a failed probe must not block the quota query.
            _cliVersion = null;
        }
        finally
        {
            _versionProbeLock.Release();
        }
    }

    private static string? FirstLine(string text)
    {
        var idx = text.IndexOf('\n');
        return idx < 0 ? text : text[..idx];
    }

    private static string? ComposeIdentityDisplay(string? planType, string? email)
    {
        var plan = planType is null ? null : $"planType={planType}";
        var masked = email is null ? null : MonitoringIdentity.MaskEmail(email);
        return plan is null && masked is null ? null : string.Join(" · ", new[] { plan, masked }.Where(p => p is not null));
    }

    /// <summary>
    /// Minimal newline-delimited JSON-RPC routing over one app-server session. Requests are
    /// serialized (only the coordinator can run one refresh per provider), responses are
    /// matched by id, notifications are dropped. Output past the cap marks the channel dead
    /// (spec: stdout+stderr combined 1 MiB).
    /// </summary>
    internal sealed class RpcChannel
    {
        private readonly ManagedProcess _process;
        private readonly long _maxChars;
        private readonly Dictionary<int, TaskCompletionSource<JsonRpcFrame>> _pending = new();
        private readonly object _lock = new();
        private long _receivedChars;
        private bool _overLimit;

        public RpcChannel(ManagedProcess process, long maxOutputBytes)
        {
            _process = process;
            _maxChars = Math.Max(1, maxOutputBytes / sizeof(char));
        }

        public bool OverLimit => _overLimit;

        internal Task OnLineAsync(string line)
        {
            lock (_lock)
            {
                _receivedChars += line.Length + 1;
                if (_receivedChars > _maxChars)
                {
                    _overLimit = true;
                    // Fail every waiter; the session is unusable past this point.
                    foreach (var tcs in _pending.Values) tcs.TrySetException(new IOException("output limit exceeded"));
                    _pending.Clear();
                    return Task.CompletedTask;
                }
            }

            if (!JsonRpc.TryParse(line, out var frame)) return Task.CompletedTask;
            if (frame.IsResponse && frame.Id is not null)
            {
                TaskCompletionSource<JsonRpcFrame>? waiter;
                lock (_lock)
                {
                    if (!_pending.Remove(frame.Id.Value, out waiter)) return Task.CompletedTask;
                }
                waiter.TrySetResult(frame);
            }
            return Task.CompletedTask;
        }

        public Task<JsonRpcFrame> SendAsync(int id, string method, object? parameters, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<JsonRpcFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock)
            {
                if (_overLimit) throw new IOException("output limit exceeded");
                _pending.Add(id, tcs);
            }

            WriteFrame(JsonRpc.EncodeRequest(id, method, parameters));
            ct.Register(() => tcs.TrySetCanceled(ct));

            return tcs.Task;
        }

        public void NotifyAsync(string method)
        {
            WriteFrame(JsonRpc.EncodeNotification(method));
        }

        private void WriteFrame(string json)
        {
            try
            {
                _process.WriteStdinLine(json);
            }
            catch (IOException)
            {
                throw new IOException("stdin closed by app-server");
            }
        }
    }
}

/// <summary>Frame shapes of the newline-delimited JSON-RPC stream.</summary>
public readonly record struct JsonRpcFrame(int? Id, string? Method, JsonElement? Params, JsonElement? Result, JsonElement? Error)
{
    public bool IsResponse => Id is not null && (Result is not null || Error is not null);
}

public static class JsonRpc
{
    public static bool TryParse(string line, out JsonRpcFrame frame)
    {
        frame = default;
        JsonElement doc;
        try
        {
            doc = JsonDocument.Parse(line).RootElement.Clone();
        }
        catch (JsonException)
        {
            return false;
        }

        if (doc.ValueKind != JsonValueKind.Object) return false;

        int? id = null;
        if (doc.TryGetProperty("id") is { } idEl)
        {
            id = idEl.ValueKind switch
            {
                JsonValueKind.Number when idEl.TryGetInt32(out var i) => i,
                _ => null,
            };
        }

        var method = doc.TryGetProperty("method").AsNullableString();
        var hasResult = doc.TryGetProperty("result") is { } r && r.ValueKind != JsonValueKind.Undefined;
        var hasError = doc.TryGetProperty("error") is { } e && e.ValueKind != JsonValueKind.Undefined;

        frame = new JsonRpcFrame(
            id,
            method,
            doc.TryGetProperty("params"),
            hasResult ? doc.TryGetProperty("result") : null,
            hasError ? doc.TryGetProperty("error") : null);

        // A frame with no method AND no result/error is not a JSON-RPC frame at all
        // (e.g. {"id":true}) — reject it so callers do not treat junk as protocol.
        return method is not null || frame.IsResponse;
    }

    public static string EncodeRequest(int id, string method, object? parameters)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("method", method);
            if (parameters is null) writer.WriteNull("params");
            else
            {
                writer.WritePropertyName("params");
                JsonSerializer.Serialize(writer, parameters);
            }
            writer.WriteNumber("id", id);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string EncodeNotification(string method)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("method", method);
            writer.WriteStartObject("params");
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

/// <summary>
/// Maps the `account/read` result. Returns the account's plan type and email when the
/// account is a ChatGPT one; ApiKey/Bedrock accounts have neither — identity then comes
/// from the rateLimits accountId, or the snapshot is unverified.
/// </summary>
public static class CodexAccountMapper
{
    public static (string? Email, string? PlanType, string? AccountType) Map(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object) return (null, null, null);
        var account = result.TryGetProperty("account");
        if (account is null || account.Value.ValueKind != JsonValueKind.Object) return (null, null, null);

        var type = account.Value.TryGetProperty("type").AsNullableString();
        var email = account.Value.TryGetProperty("email").AsNullableString();
        var planType = account.Value.TryGetProperty("planType").AsNullableString();
        return (email, planType, type);
    }
}

/// <summary>
/// Maps the `account/rateLimits/read` result (codex 0.154.0 schema). Display order and
/// semantics per spec §5.1: prefer rateLimitsByLimitId, fall back to the legacy
/// rateLimits field; preserve limit ids, window durations, reset times; out-of-range
/// percents are flagged, not clamped.
/// </summary>
public static class CodexRateLimitsMapper
{
    public sealed record MappedResult(
        bool Ok,
        string? Error,
        IReadOnlyList<QuotaBucket> Buckets,
        ResetCreditSummary? ResetCredits,
        string? AccountId);

    public static MappedResult Map(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object)
        {
            return new MappedResult(false, "rateLimits result is not an object", [], null, null);
        }

        var accountId = result.TryGetProperty("accountId").AsNullableString();
        var byLimitId = result.TryGetProperty("rateLimitsByLimitId");
        var legacy = result.TryGetProperty("rateLimits");

        var buckets = new List<QuotaBucket>();
        if (byLimitId is { ValueKind: JsonValueKind.Object })
        {
            foreach (var prop in byLimitId.Value.EnumerateObject())
            {
                var bucket = MapSnapshot(prop.Value, sourceKey: prop.Name);
                if (bucket is null) continue;
                buckets.Add(bucket);
            }
        }

        if (legacy is { ValueKind: JsonValueKind.Object })
        {
            var legacyBucket = MapSnapshot(legacy.Value, sourceKey: null);
            if (legacyBucket is not null &&
                !buckets.Any(b => string.Equals(b.SourceKey, legacyBucket.SourceKey, StringComparison.Ordinal)))
            {
                // The legacy view mirrors one historical bucket; keep it only when the
                // multi-bucket view did not already carry the same limit id.
                buckets.Add(legacyBucket);
            }
        }

        if (buckets.Count == 0)
        {
            return new MappedResult(false, "no rate limit buckets in response", [], null, accountId);
        }

        var credits = MapResetCredits(result.TryGetProperty("rateLimitResetCredits"));
        return new MappedResult(true, null, buckets, credits, accountId);
    }

    private static QuotaBucket? MapSnapshot(JsonElement snapshot, string? sourceKey)
    {
        if (snapshot.ValueKind != JsonValueKind.Object) return null;

        var limitId = snapshot.TryGetProperty("limitId").AsNullableString();
        var limitName = snapshot.TryGetProperty("limitName").AsNullableString();
        var planType = snapshot.TryGetProperty("planType").AsNullableString();
        var key = limitId ?? sourceKey ?? "codex";

        var windows = new List<QuotaWindow>();
        AddWindow(windows, "primary", snapshot.TryGetProperty("primary"));
        AddWindow(windows, "secondary", snapshot.TryGetProperty("secondary"));
        AddSpendControlWindow(windows, snapshot.TryGetProperty("individualLimit"));

        return new QuotaBucket
        {
            SourceKey = key,
            DisplayName = limitName ?? (key == "codex" ? "Codex" : key),
            Tier = planType,
            Subscribed = null,
            Windows = windows,
        };
    }

    private static void AddWindow(List<QuotaWindow> windows, string sourceKey, JsonElement? windowEl)
    {
        if (windowEl is null || windowEl.Value.ValueKind != JsonValueKind.Object) return;
        var w = windowEl.Value;
        var used = w.TryGetProperty("usedPercent").AsNullableDouble();
        var (usedNorm, remaining, outOfRange) = PercentNormalizer.FromUsed(used);
        var duration = w.TryGetProperty("windowDurationMins").AsNullableInt64();
        var resets = EpochTime.FromSeconds(w.TryGetProperty("resetsAt").AsNullableInt64());

        windows.Add(new QuotaWindow
        {
            SourceKey = sourceKey,
            WindowDurationMinutes = duration,
            UsedPercent = usedNorm,
            RemainingPercent = remaining,
            PercentOutOfRange = outOfRange,
            ResetsAtUtc = resets,
            HasAnyQuotaField = used is not null,
        });
    }

    private static void AddSpendControlWindow(List<QuotaWindow> windows, JsonElement? limitEl)
    {
        if (limitEl is null || limitEl.Value.ValueKind != JsonValueKind.Object) return;
        var l = limitEl.Value;
        var remaining = l.TryGetProperty("remainingPercent").AsNullableDouble();
        var (used, remainingNorm, outOfRange) = PercentNormalizer.FromRemaining(remaining);

        windows.Add(new QuotaWindow
        {
            SourceKey = "individualLimit",
            UsedPercent = used,
            RemainingPercent = remainingNorm,
            PercentOutOfRange = outOfRange,
            ResetsAtUtc = EpochTime.FromSeconds(l.TryGetProperty("resetsAt").AsNullableInt64()),
            UsedText = l.TryGetProperty("used").AsNullableString(),
            TotalText = l.TryGetProperty("limit").AsNullableString(),
            HasAnyQuotaField = remaining is not null,
        });
    }

    private static ResetCreditSummary? MapResetCredits(JsonElement? creditsEl)
    {
        if (creditsEl is null || creditsEl.Value.ValueKind != JsonValueKind.Object) return null;
        var availableCount = creditsEl.Value.TryGetProperty("availableCount").AsNullableInt32();
        if (availableCount is null) return null;

        IReadOnlyList<ResetCredit>? details = null;
        if (creditsEl.Value.TryGetProperty("credits") is { } arr && arr.ValueKind == JsonValueKind.Array)
        {
            var list = new List<ResetCredit>();
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                list.Add(new ResetCredit
                {
                    Title = item.TryGetProperty("title").AsNullableString()
                            ?? item.TryGetProperty("resetType").AsNullableString()
                            ?? "reset credit",
                    Status = item.TryGetProperty("status").AsNullableString(),
                    ExpiresAtUtc = EpochTime.FromSeconds(item.TryGetProperty("expiresAt").AsNullableInt64()),
                });
            }
            details = list;
        }

        return new ResetCreditSummary { AvailableCount = availableCount.Value, Details = details };
    }
}

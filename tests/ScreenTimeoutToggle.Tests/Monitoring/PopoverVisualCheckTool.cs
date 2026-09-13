using System.Runtime.InteropServices;
using OBDim.Monitoring.Infrastructure;
using OBDim.Monitoring.Models;
using OBDim.Monitoring.Providers;
using OBDim.Monitoring.Services;
using OBDim.UI;
using Xunit;

namespace OBDim.Tests.Monitoring;

/// <summary>
/// MANUAL visual self-check (run with OBDEM_VISUAL=1): builds the REAL popover with
/// realistic fake quota data, shows each view on screen and captures a PNG per view so a
/// human/agent can eyeball the layout. Skipped silently unless OBDEM_VISUAL is set —
/// automated CI must not pop windows or require an interactive desktop.
/// Output: %TEMP%\obdim-visual\{quota,memory,settings}.png
/// </summary>
public class PopoverVisualCheckTool
{
    [Fact]
    public void Capture_AllThreeViews()
    {
        if (Environment.GetEnvironmentVariable("OBDEM_VISUAL") is null)
        {
            return; // not a visual run
        }

        RunOnSta(() =>
        {
            // Reproduce the real app's DPI context (the user's machine runs 150%) BEFORE
            // any window exists — captures at unaware DPI hid real-machine layout bugs.
            TryBecomePerMonitorDpiAware();

            var outDir = Path.Combine(Path.GetTempPath(), "obdim-visual");
            Directory.CreateDirectory(outDir);

            var dir = Path.Combine(Path.GetTempPath(), $"obdim-visual-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var now = DateTimeOffset.UtcNow;
                ProviderSnapshot Next() => new()
                {
                    Provider = ProviderId.Codex,
                    IdentityKey = "visual",
                    IdentityVerified = true,
                    AttemptedAtUtc = now,
                    SucceededAtUtc = now,
                    CliVersion = "codex-cli 0.154.0",
                    IdentityDisplay = "planType=pro · o***@gmail.com",
                    Buckets =
                    [
                        new QuotaBucket
                        {
                            SourceKey = "codex",
                            DisplayName = "Codex",
                            Tier = "pro",
                            Windows =
                            [
                                new QuotaWindow { SourceKey = "primary", WindowDurationMinutes = 10080, UsedPercent = 24, RemainingPercent = 76, ResetsAtUtc = now.AddDays(6), HasAnyQuotaField = true },
                            ],
                        },
                        new QuotaBucket
                        {
                            SourceKey = "codex_bengalfox",
                            DisplayName = "GPT-5.3-Codex-Spark",
                            Tier = "pro",
                            Windows =
                            [
                                new QuotaWindow { SourceKey = "primary", WindowDurationMinutes = 300, UsedPercent = 0, RemainingPercent = 100, ResetsAtUtc = now.AddHours(5), HasAnyQuotaField = true },
                                new QuotaWindow { SourceKey = "secondary", WindowDurationMinutes = 10080, UsedPercent = 0, RemainingPercent = 100, ResetsAtUtc = now.AddDays(7), HasAnyQuotaField = true },
                            ],
                        },
                    ],
                    ResetCredits = new ResetCreditSummary
                    {
                        AvailableCount = 3,
                        Details =
                        [
                            new ResetCredit { Title = "Full reset", Status = "available", ExpiresAtUtc = now.AddDays(8) },
                            new ResetCredit { Title = "Full reset", Status = "available", ExpiresAtUtc = now.AddDays(21) },
                            new ResetCredit { Title = "Full reset", Status = "available", ExpiresAtUtc = now.AddDays(32) },
                        ],
                    },
                };

                ProviderSnapshot snapshot = Next();
                ProviderSnapshot MiniMaxNext() => new()
                {
                    Provider = ProviderId.MiniMax,
                    IdentityKey = "minimax-visual",
                    IdentityVerified = false,
                    AttemptedAtUtc = now,
                    SucceededAtUtc = now,
                    Buckets =
                    [
                        new QuotaBucket
                        {
                            SourceKey = "general",
                            DisplayName = "general",
                            Windows =
                            [
                                new QuotaWindow { SourceKey = "interval", DisplayAsUsed = true, UsedPercent = 7, RemainingPercent = 93, ResetsAtUtc = now.AddHours(2.3), HasAnyQuotaField = true },
                                new QuotaWindow { SourceKey = "weekly", DisplayAsUsed = true, IsUnlimited = true, UsedPercent = 0, RemainingPercent = 100, ResetsAtUtc = now.AddHours(9.3), HasAnyQuotaField = true },
                            ],
                        },
                        new QuotaBucket
                        {
                            SourceKey = "video",
                            DisplayName = "video",
                            Windows =
                            [
                                new QuotaWindow { SourceKey = "interval", DisplayAsUsed = true, UsedPercent = 0, RemainingPercent = 100, ResetsAtUtc = now.AddHours(9.3), UsedText = "0", TotalText = "3", HasAnyQuotaField = true },
                            ],
                        },
                    ],
                };
                ProviderSnapshot ArkNext() => new()
                {
                    Provider = ProviderId.Ark,
                    IdentityKey = "ark-visual",
                    IdentityVerified = true,
                    AttemptedAtUtc = now,
                    SucceededAtUtc = now,
                    IdentityDisplay = "auth=sso",
                    Buckets =
                    [
                        new QuotaBucket
                        {
                            SourceKey = "coding-plan",
                            DisplayName = "coding-plan",
                            Tier = "personal",
                            Subscribed = true,
                            Windows =
                            [
                                new QuotaWindow { SourceKey = "session", DisplayAsUsed = true, UsedPercent = 0, RemainingPercent = 100, HasAnyQuotaField = true },
                                new QuotaWindow { SourceKey = "weekly", DisplayAsUsed = true, UsedPercent = 97.93, RemainingPercent = 2.07, ResetsAtUtc = now.AddHours(2.3), HasAnyQuotaField = true },
                            ],
                        },
                    ],
                };
                var adapters = new Dictionary<ProviderId, IProviderAdapter>
                {
                    [ProviderId.Codex] = new FakeAdapter(ProviderId.Codex, () => snapshot),
                    [ProviderId.MiniMax] = new FakeAdapter(ProviderId.MiniMax, MiniMaxNext),
                    [ProviderId.Ark] = new FakeAdapter(ProviderId.Ark, ArkNext),
                };

                // Enable all providers in the temp monitoring.json so the cards render
                // their data rows (upgrades default to disabled, the visual check does not).
                var settingsPath = Path.Combine(dir, "monitoring.json");
                new MonitoringSettingsService(settingsPath).Save(new MonitoringSettings
                {
                    Providers = Enum.GetValues<ProviderId>().Select(id => new ProviderSettings
                    {
                        Id = id,
                        Enabled = true,
                    }).ToList(),
                });

                using var coordinator = new MonitoringCoordinator(
                    new FakeClock(),
                    new StubMemoryReader(),                    adapters,
                    new MonitoringSettingsService(settingsPath),
                    new MonitoringCacheService(Path.Combine(dir, "cache")));
                coordinator.Start();
                // Push the fake snapshot through the real pipeline so the panel has data,
                // and give the memory sampler one tick.
                coordinator.RequestManualRefreshAll();
                System.Threading.Thread.Sleep(1500);

                using var form = new MonitorForm(coordinator);
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new System.Drawing.Point(120, 120);
                form.Show();
                Application.DoEvents();
                System.Threading.Thread.Sleep(300);

                Capture(form, Path.Combine(outDir, "quota.png"));
                form.CreditsExpanded = true;
                Application.DoEvents();
                System.Threading.Thread.Sleep(200);
                Capture(form, Path.Combine(outDir, "quota-expanded.png"));
                form.CreditsExpanded = false;
                form.SetView(MonitorForm.View.Memory);
                Application.DoEvents();
                System.Threading.Thread.Sleep(300);
                Capture(form, Path.Combine(outDir, "memory.png"));
                form.SetView(MonitorForm.View.Settings);
                Application.DoEvents();
                System.Threading.Thread.Sleep(300);
                Capture(form, Path.Combine(outDir, "settings.png"));

                form.Close();
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        });
    }

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    private static void TryBecomePerMonitorDpiAware()
    {
        try
        {
            _ = SetProcessDpiAwarenessContext(new IntPtr(-4)); // PER_MONITOR_AWARE_V2
        }
        catch
        {
            // Older OS without the export — capture proceeds at whatever context applies.
        }
    }

    private static void Capture(Form form, string path)
    {
        form.Refresh();
        // DrawToBitmap renders the control tree regardless of window position, focus or
        // occlusion — CopyFromScreen captured whatever happened to be at those pixels.
        using var bmp = new System.Drawing.Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private sealed class FakeClock : IClock
    {
        // Real time — the visual capture must not mark fresh fake data as expired.
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    private sealed class NullAdapter : IProviderAdapter
    {
        public NullAdapter(ProviderId id) { Id = id; }
        public ProviderId Id { get; }

        public Task<ProviderSnapshot> QueryAsync(ProviderSettings settings, CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderSnapshot
            {
                Provider = Id,
                IdentityKey = "none",
                IdentityVerified = false,
                AttemptedAtUtc = DateTimeOffset.UtcNow,
                Error = ProviderErrorKind.CliNotFound,
            });
    }

    private sealed class StubMemoryReader : IMemoryReader
    {
        public MemorySample? Read(out string? error)
        {
            error = null;
            return new MemorySample
            {
                SampledAtUtc = DateTimeOffset.UtcNow,
                PhysicalTotalBytes = 29_953_724_416,   // ~27.9 GB
                PhysicalAvailableBytes = 12_348_353_536,
                CommitTotalBytes = 34_272_847_872,     // ~31.9 GB
                CommitLimitBytes = 45_849_927_680,     // ~42.7 GB
                LowMemorySignal = false,
            };
        }

        public void Dispose() { }
    }

    private static void RunOnSta(Action body)
    {
        var thread = new Thread(() => body());
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "STA visual thread timed out");
    }
}

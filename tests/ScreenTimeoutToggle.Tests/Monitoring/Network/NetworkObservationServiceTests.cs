using OBDim.Monitoring.Network.Models;
using OBDim.Monitoring.Network.V2;
using OBDim.Monitoring.Network.Windows;
using Xunit;

namespace OBDim.Tests.Monitoring.Network;

public class NetworkObservationServiceTests
{
    private sealed class Time : TimeProvider
    {
        public long Tick = 1000;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Tick;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-09-22T01:00:00Z").AddMilliseconds(Tick);
    }
    private sealed class Reader : IInterfaceCounterTable
    {
        public IReadOnlyList<InterfaceCounterRow> Rows = [new("id-a", "Wi-Fi", InterfaceKind.Physical, 1000, 2000)];
        public NetworkReadStatus Status = NetworkReadStatus.Ok;
        public Action? BeforeRead;
        public int Reads;
        public NetworkReadStatus TryRead(out IReadOnlyList<InterfaceCounterRow> rows, out string? error)
        { Reads++; BeforeRead?.Invoke(); rows = Rows; error = null; return Status; }
    }
    [Fact]
    public void DefaultOffThenMonotonicRatesAndIndependentReset()
    {
        var time = new Time(); var reader = new Reader();
        using var s = new NetworkObservationService(reader, time, () => "ID-A", false);
        s.Poll(); Assert.Equal(0, reader.Reads);
        s.SetEnabled(true); s.Poll();
        Assert.Null(s.Current.Interfaces[0].Totals.Upload.Bytes);
        time.Tick += 1000; reader.Rows = [new("id-a", "Renamed Wi-Fi", InterfaceKind.Physical, 1100, 2500)];
        s.Poll();
        var i = s.Current.Interfaces[0];
        Assert.Equal(500, i.Rates.Upload); Assert.Equal(100, i.Rates.Download);
        Assert.Equal(500UL, i.Totals.Upload.Bytes); Assert.Equal("id-a", s.Current.SystemInterfaceID);
        var start = i.Totals.Upload.Since;
        time.Tick += 1000; reader.Rows = [new("id-a", "Renamed Wi-Fi", InterfaceKind.Physical, 10, 2600)];
        s.Poll(); i = s.Current.Interfaces[0];
        Assert.Equal(600UL, i.Totals.Upload.Bytes); Assert.Equal(start, i.Totals.Upload.Since);
        Assert.Null(i.Totals.Download.Bytes); Assert.Null(i.Rates.Download); Assert.Equal(100, i.Rates.Upload);
        Assert.False(s.Current.Capabilities.PerApp); Assert.False(s.Current.Capabilities.Targets);
        Assert.False(s.Current.Capabilities.BlockNewConnections); Assert.False(s.Current.IsDemo);
    }
    [Fact]
    public void LongSilenceAndFailedReadRebaselineInsteadOfInventingTraffic()
    {
        var time = new Time(); var reader = new Reader();
        using var s = new NetworkObservationService(reader, time, () => null, false);
        s.SetEnabled(true); s.Poll();
        time.Tick += 10000; s.Poll();
        Assert.Null(s.Current.Interfaces[0].Rates.Upload);
        Assert.Equal("silence", s.Current.Interfaces[0].Samples[^1].Continuity.Upload.Reason);
        reader.Status = NetworkReadStatus.Failed; time.Tick += 1000; s.Poll();
        Assert.Equal("disconnected", s.Current.State);
        reader.Status = NetworkReadStatus.Ok; time.Tick += 1000; s.Poll();
        Assert.Null(s.Current.Interfaces[0].Rates.Upload);
        Assert.Equal("source-gap", s.Current.Interfaces[0].Samples[^1].Continuity.Upload.Reason);
        Assert.Null(s.Current.SystemInterfaceID); // no implicit first-interface fallback
    }
    [Fact]
    public void RemovedInterfaceDoesNotFallBackAndRestartHasANewSession()
    {
        var time = new Time(); var reader = new Reader();
        using var s = new NetworkObservationService(reader, time, () => "id-a", false);
        s.SetEnabled(true); s.Poll(); var session = s.Current.SessionID;
        reader.Rows = [new("id-b", "Other", InterfaceKind.Tunnel, 100, 200)];
        time.Tick += 1000; s.Poll();
        Assert.Null(s.Current.SystemInterfaceID); Assert.Equal("id-b", Assert.Single(s.Current.Interfaces).Id);
        s.SetEnabled(false); Assert.Equal("stopped", s.Current.State);
        s.SetEnabled(true); s.Poll(); Assert.NotEqual(session, s.Current.SessionID);
        Assert.Null(s.Current.Interfaces[0].Totals.Upload.Bytes);
    }
    [Fact]
    public async Task InFlightOldSessionCannotPublishAcrossStopStart()
    {
        var time = new Time(); var reader = new Reader();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        reader.BeforeRead = () => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); };
        using var s = new NetworkObservationService(reader, time, () => "id-a", false);
        s.SetEnabled(true);
        var pending = Task.Run(s.Poll); Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
        s.SetEnabled(false); s.SetEnabled(true); release.Set(); await pending;
        Assert.Equal("starting", s.Current.State); Assert.Empty(s.Current.Interfaces);
        reader.BeforeRead = null; s.Poll();
        Assert.Equal("active", s.Current.State);
    }
    [Fact]
    public void HistoryIsBoundedAndDownInterfaceIsNotPresentedAsMeasuredZero()
    {
        var time = new Time(); var reader = new Reader();
        using var s = new NetworkObservationService(reader, time, () => "id-a", false);
        s.SetEnabled(true);
        for (var i = 0; i < 7205; i++) { time.Tick += 1000; s.Poll(); }
        var history = s.Current.Interfaces[0].Samples;
        Assert.True(history.Count <= NetworkObservationService.MaxPointsPerInterface);
        Assert.True(history[^1].MonotonicNs - history[0].MonotonicNs <= 7_200_000_000_000UL);
        reader.Rows = [new("id-a", "Wi-Fi", InterfaceKind.Physical, 0, 0, false)];
        time.Tick += 1000; s.Poll();
        Assert.False(s.Current.Interfaces[0].Available); Assert.Null(s.Current.Interfaces[0].Rates.Upload);
        Assert.Null(s.Current.SystemInterfaceID);
    }
}

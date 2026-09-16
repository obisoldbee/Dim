using System.Text.Json;
using OBDim.Monitoring.Infrastructure;
using OBDim.Monitoring.Models;
using OBDim.Monitoring.Providers;
using OBDim.Monitoring.Services;
using OBDim.UI;

internal static class AuthParityProbe
{
    private sealed class Clock:IClock { public DateTimeOffset UtcNow {get;set;}=DateTimeOffset.UtcNow; }
    private sealed class Memory:IMemoryReader { public MemorySample? Read(out string? error){error=null;return null;}public void Dispose(){} }
    private sealed class Adapter(ProviderSnapshot snapshot):IProviderAdapter
    {
        public ProviderSnapshot Snapshot=snapshot;
        public ProviderId Id=>Snapshot.Provider;
        public Task<ProviderSnapshot> QueryAsync(ProviderSettings settings,CancellationToken token)=>Task.FromResult(Snapshot);
    }
    public static void Run(string output)
    {
        Directory.CreateDirectory(output);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        var clock=new Clock();var runner=new CliProcessRunner();
        var settingsService=new MonitoringSettingsService(Path.Combine(output,"probe-settings.json"));
        var settings=new MonitoringSettings{MemoryEnabled=false,Providers=Enum.GetValues<ProviderId>().Select(id=>new ProviderSettings{Id=id,Enabled=true}).ToList()};
        settingsService.Save(settings);
        IProviderAdapter[] live=[new CodexProvider(clock,runner),new MiniMaxProvider(clock,runner),new ArkProvider(clock,runner)];
        var snapshots=Task.WhenAll(live.Select(a=>a.QueryAsync(settings.Provider(a.Id),CancellationToken.None))).GetAwaiter().GetResult();
        File.WriteAllText(Path.Combine(output,"live-summary.json"),JsonSerializer.Serialize(snapshots.Select(s=>new{
            Provider=s.Provider.ToString(),Error=s.Error.ToString(),s.ErrorCode,s.ExitCode,s.PlanTier,s.PlanTierIsInferred,
            Buckets=s.Buckets.Select(b=>new{Product=b.SourceKey,b.Tier,b.Subscribed,Error=b.ErrorKind.ToString(),Windows=b.Windows.Count})
        }),new JsonSerializerOptions{WriteIndented=true}));
        var adapters=snapshots.ToDictionary(s=>s.Provider,s=>new Adapter(s));
        using var coordinator=new MonitoringCoordinator(clock,new Memory(),adapters.ToDictionary(p=>p.Key,p=>(IProviderAdapter)p.Value),settingsService,new MonitoringCacheService(Path.Combine(output,"probe-cache")));
        using var panel=new MonitorForm(coordinator);
        panel.Shown+=async(_,_)=>
        {
            try
            {
                coordinator.RequestManualRefreshAll();await Settle(coordinator);Capture(panel,output,"live-quota");
                using var settingsForm=new MonitoringSettingsForm(coordinator);settingsForm.Show();typeof(MonitoringSettingsForm).GetMethod("SelectCategory",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(settingsForm,[1]);
                await Task.Delay(150);Capture(settingsForm,output,"live-settings");
                clock.UtcNow=clock.UtcNow.AddSeconds(16);
                adapters[ProviderId.Ark].Snapshot=SnapshotFactory.Failure(ProviderId.Ark,ProviderErrorKind.AuthenticationExpired,null,clock.UtcNow) with {ErrorCode="auth.session_invalid",ExitCode=1};
                coordinator.RequestManualRefresh(ProviderId.Ark);await Settle(coordinator);
                settingsForm.BringToFront();Capture(settingsForm,output,"fixture-expired-settings");
                settingsForm.Hide();panel.Show();Capture(panel,output,"fixture-expired-quota");
                File.WriteAllText(Path.Combine(output,"diagnostics.json"),coordinator.ExportDiagnostics());
            }
            catch(Exception ex){File.WriteAllText(Path.Combine(output,"probe-error.txt"),ex.ToString());Environment.ExitCode=1;}
            finally{panel.Close();}
        };
        Application.Run(panel);
    }
    private static async Task Settle(MonitoringCoordinator c)
    {
        for(var i=0;c.GetDisplayStates().Any(s=>s.Refreshing);i++)
        {if(i>1000)throw new TimeoutException();await Task.Delay(10);}
        await Task.Delay(100);
    }
    private static void Capture(Form form,string output,string name)
    {
        form.Refresh();using var image=new Bitmap(form.Width,form.Height);
        form.DrawToBitmap(image,new Rectangle(0,0,image.Width,image.Height));image.Save(Path.Combine(output,name+".png"));
    }
}

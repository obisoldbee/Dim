using System.Text.Json;
using System.Text.Json.Nodes;
using OBDim.Monitoring.Infrastructure;
using OBDim.Monitoring.Models;
using OBDim.Monitoring.Providers;
using OBDim.Monitoring.Services;
using OBDim.UI;
using Xunit;
namespace OBDim.Tests.Monitoring;

public class AuthenticationParityTests
{
    [Theory]
    [InlineData("refresh_token is invalid",ProviderErrorKind.AuthenticationExpired)]
    [InlineData("HTTP 401",ProviderErrorKind.NotSignedIn)]
    [InlineData("HTTP 403",ProviderErrorKind.PermissionDenied)]
    [InlineData("HTTP 429",ProviderErrorKind.RateLimited)]
    [InlineData("HTTP 503",ProviderErrorKind.ServiceUnavailable)]
    [InlineData("ENOTFOUND",ProviderErrorKind.NetworkError)]
    [InlineData("subscription expired",ProviderErrorKind.SubscriptionExpired)]
    [InlineData("quota exhausted",ProviderErrorKind.QuotaExhausted)]
    [InlineData("unknown flag --format",ProviderErrorKind.CliVersionUnsupported)]
    [InlineData("unexplained failure",ProviderErrorKind.ExecutionFailed)]
    public void EvidenceDeterminesFailure_NotExitCodeAlone(string message,ProviderErrorKind expected)
    {
        var snapshot=ProviderFailureClassifier.FromResult(ProviderId.Ark,new CliResult {
            ExitCode=1,Stdout=JsonSerializer.Serialize(new {error=new {message}}),StderrTail=""},DateTimeOffset.UtcNow);
        Assert.Equal(expected,snapshot.Error); Assert.Equal(1,snapshot.ExitCode);
        Assert.NotNull(snapshot.ErrorCode); Assert.Null(snapshot.ErrorMessage);
        var text=ProviderStatusPresentation.Failure(snapshot.Error,snapshot.ExitCode);
        Assert.NotEqual("查询失败",text.Title); Assert.NotEmpty(text.Detail);
    }
    [Fact]
    public void EscapedChineseAndSecrets_AreClassifiedWithoutDisclosure()
    {
        var raw=JsonSerializer.Serialize(new {error=new {message="认证已过期",token="secret-sentinel",account="private@example.invalid"}});
        var snapshot=ProviderFailureClassifier.FromResult(ProviderId.Ark,new CliResult {ExitCode=1,Stdout=raw,StderrTail=""},DateTimeOffset.UtcNow);
        Assert.Equal(ProviderErrorKind.AuthenticationExpired,snapshot.Error);
        Assert.DoesNotContain("sentinel",JsonSerializer.Serialize(snapshot));
        Assert.DoesNotContain("example.invalid",JsonSerializer.Serialize(snapshot));
    }
    [Theory]
    [InlineData("{\"logged_in\":true,\"control_plane_auth\":{\"status\":\"ok\"},\"volc_sso\":{\"expired\":true}}",null)]
    [InlineData("{\"logged_in\":true,\"control_plane_auth\":{\"status\":\"needs_login\"}}",ProviderErrorKind.NotSignedIn)]
    [InlineData("{\"logged_in\":false}",ProviderErrorKind.NotSignedIn)]
    [InlineData("{}",ProviderErrorKind.ParseFailed)]
    public void ArkChecksControlPlane_NotRenewableIdToken(string json,ProviderErrorKind? error) =>
        Assert.Equal(error,ArkAuthenticationParser.Parse(json)?.Kind);
    [Fact]
    public void MiniMaxInference_RequiresCompleteUniqueValidEntitlement()
    {
        var json=MonitoringFixtures.Load("minimax-quota-real.json");
        Assert.Equal("Max",MiniMaxQuotaParser.Parse(json).PlanTier);
        var doc=JsonNode.Parse(json)!;
        var video=doc["model_remains"]![1]!;
        video["current_interval_total_count"]=5;
        Assert.Equal("Ultra",MiniMaxQuotaParser.Parse(doc.ToJsonString()).PlanTier);
        video["current_interval_total_count"]=4;
        Assert.Null(MiniMaxQuotaParser.Parse(doc.ToJsonString()).PlanTier);
        video["current_interval_total_count"]=3;
        video["current_interval_usage_count"]=4;
        Assert.Null(MiniMaxQuotaParser.Parse(doc.ToJsonString()).PlanTier);
        video["current_interval_usage_count"]=1;
        doc["model_remains"]!.AsArray().Add(video.DeepClone());
        Assert.Null(MiniMaxQuotaParser.Parse(doc.ToJsonString()).PlanTier);
    }
    [Fact]
    public void ArkMetadata_CannotInventSubscriptionOrResolveAmbiguousTier()
    {
        var buckets=new[]{new QuotaBucket{SourceKey="coding-plan",Subscribed=true}};
        const string plan="{\"key\":\"coding-plan\",\"scope\":\"personal\",\"tier\":\"pro\"}";
        Assert.Equal("pro",ArkPlanMetadata.Apply(buckets,"{\"plans\":["+plan+"]}")[0].Tier);
        Assert.Null(ArkPlanMetadata.Apply(buckets,"{\"plans\":["+plan+","+plan+"]}")[0].Tier);
        Assert.Null(ArkPlanMetadata.Apply([buckets[0] with {Subscribed=false}],"{\"plans\":["+plan+"]}")[0].Tier);
        Assert.Null(ArkPlanParser.Parse(MonitoringFixtures.Load("ark-plan-real.json")).Buckets[0].Tier);
    }
    [Fact]
    public void ExhaustedWindowIsSuccessfulData_AndBucketErrorsKeepTheirCause()
    {
        var data=ArkPlanParser.Parse("{\"items\":[{\"product\":\"coding-plan\",\"subscribed\":true,\"periods\":[{\"label\":\"monthly\",\"percent\":100}]}]}");
        Assert.True(data.Ok);Assert.Null(data.Buckets[0].Error);Assert.Equal(0,data.Buckets[0].Windows[0].RemainingPercent);
        var failed=ArkPlanParser.Parse("{\"items\":[{\"product\":\"coding-plan\",\"error\":{\"message\":\"subscription expired\"}}]}");
        Assert.True(failed.Ok);Assert.Equal(ProviderErrorKind.SubscriptionExpired,failed.Buckets[0].ErrorKind);
    }
    private sealed class Runner : ICliProcessRunner
    {
        public List<string> Commands=[];
        public bool Expired;
        public Task<CliResult> RunAsync(CliRequest request,CancellationToken cancellationToken)
        {
            var command=string.Join(" ",request.Arguments);Commands.Add(command);
            var json=command.StartsWith("auth ") ? Expired?"{\"error\":{\"message\":\"refresh_token is invalid\"}}":"{\"logged_in\":true,\"control_plane_auth\":{\"status\":\"ok\"}}"
                :command.StartsWith("usage ")?MonitoringFixtures.Load("ark-plan-real.json")
                :"{\"plans\":[{\"key\":\"coding-plan\",\"scope\":\"personal\",\"tier\":\"pro\"}]}";
            return Task.FromResult(new CliResult{ExitCode=Expired?1:0,Stdout=json,StderrTail=""});
        }
    }
    [Fact]
    public async Task ArkAdapter_QueriesAuthQuotaAndPlanLevel_StopsOnInvalidSession()
    {
        var runner=new Runner();var adapter=new ArkProvider(new FakeClock{UtcNow=DateTimeOffset.UtcNow},runner);
        var settings=new ProviderSettings{Id=ProviderId.Ark,CliPath=Environment.ProcessPath,Enabled=true};
        var snapshot=await adapter.QueryAsync(settings,CancellationToken.None);
        Assert.False(snapshot.HasError);Assert.Equal("pro",snapshot.Buckets[0].Tier);
        Assert.Equal(new[]{"auth status --format json","usage plan --format json","plans get --format json"},runner.Commands);
        runner.Commands.Clear();runner.Expired=true;
        snapshot=await adapter.QueryAsync(settings,CancellationToken.None);
        Assert.Equal(ProviderErrorKind.AuthenticationExpired,snapshot.Error);Assert.Single(runner.Commands);
    }
    [Fact]
    public async Task Authentication_InvalidatesOldRequest_AndFreshFailureNeverBecomesConnected()
    {
        using var env=new Round2Environment();
        var release=new TaskCompletionSource<ProviderSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls=0;
        using var coordinator=env.Create(new Round2Adapter((_,_)=>Interlocked.Increment(ref calls)==1?release.Task:
            Task.FromResult(SnapshotFactory.Failure(ProviderId.Codex,ProviderErrorKind.AuthenticationExpired,null,env.Clock.UtcNow))));
        Assert.True(coordinator.RequestManualRefresh(ProviderId.Codex));
        await Round2Environment.Until(()=>calls==1);
        Assert.True(coordinator.BeginAuthentication(ProviderId.Codex));
        Assert.False(coordinator.RequestManualRefresh(ProviderId.Codex));
        release.SetResult(env.Good());
        coordinator.EndAuthentication(ProviderId.Codex,true);
        await Round2Environment.Until(()=>calls==2&&!coordinator.GetDisplayState(ProviderId.Codex).Refreshing);
        var state=coordinator.GetDisplayState(ProviderId.Codex);
        Assert.Null(state.LastGood); Assert.True(state.PausedUntilUserRetry);
        Assert.True(ProviderStatusPresentation.Connection(state).NeedsLogin);
    }
}

[Collection("NativeUi")]
public class AuthenticationParityUiTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SettingsCheck_PerformsQuery_LoginExitZeroStillRequiresFreshVerification(bool succeeds)
    {
        Round2Ui.Sta(()=>
        {
            using var env=new Round2Environment();var queries=0;
            using var coordinator=env.Create(new Round2Adapter((_,_)=> {Interlocked.Increment(ref queries);return Task.FromResult(
                succeeds && queries>1 ? env.Good() : SnapshotFactory.Failure(ProviderId.Codex,ProviderErrorKind.AuthenticationExpired,null,env.Clock.UtcNow));}));
            using var form=new MonitoringSettingsForm(coordinator);
            Exception? failure=null;
            form.Shown+=async(_,_)=>
            {
                try
                {
                    form.SelectCategory(1);form.CheckProvider(ProviderId.Codex);
                    await Round2Environment.Until(()=>queries==1&&!coordinator.GetDisplayState(ProviderId.Codex).Refreshing);
                    var loginCalls=0;form.LoginRunner=(_,_)=> {Interlocked.Increment(ref loginCalls);return Task.FromResult(0);};
                    await form.LoginProviderAsync(ProviderId.Codex);
                    Assert.Equal(1,loginCalls);
                    await Round2Environment.Until(()=>queries==2&&!coordinator.GetDisplayState(ProviderId.Codex).Refreshing);
                    await Task.Delay(30);
                    var label=Round2Ui.Descendants(form).Single(c=>c.Name=="connection_Codex");
                    if(succeeds) Assert.Contains("已连接",label.Text); else {Assert.Contains("认证已失效",label.Text);Assert.DoesNotContain("已连接",label.Text);}
                    Assert.Empty(Round2Ui.Descendants(form).Single(c=>c.Name=="action_Codex").Text);
                }
                catch(Exception ex){failure=ex;}
                finally{form.Close();}
            };
            Application.Run(form);
            if(failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        });
    }
    [Fact]
    public void MiniMaxOverview_HasInferredMax_AndOnlyDailyVideoRow()
    {
        Round2Ui.Sta(()=>
        {
            using var env=new Round2Environment();
            var parsed=MiniMaxQuotaParser.Parse(MonitoringFixtures.Load("minimax-quota-real.json"));
            env.Settings.Save(new MonitoringSettings{Providers=[new ProviderSettings{Id=ProviderId.MiniMax,Enabled=true}]});
            var adapter=new Round2Adapter((_,_)=>Task.FromResult(env.Good() with {Provider=ProviderId.MiniMax,PlanTier=parsed.PlanTier,PlanTierIsInferred=true,Buckets=parsed.Buckets}));
            using var coordinator=new MonitoringCoordinator(env.Clock,new Round2Memory(),new Dictionary<ProviderId,IProviderAdapter>{{ProviderId.MiniMax,adapter}},env.Settings,env.Cache);
            coordinator.RequestManualRefresh(ProviderId.MiniMax);
            Round2Ui.PumpUntil(()=>!coordinator.GetDisplayState(ProviderId.MiniMax).Refreshing);
            using var form=new MonitorForm(coordinator);form.Show();Application.DoEvents();
            var badge=Round2Ui.Field<Dictionary<ProviderId,MonitorForm.BadgeLabel>>(form,"_cardBadges")[ProviderId.MiniMax];
            Assert.Equal("Max",badge.Text);
            var rows=Round2Ui.Field<Dictionary<ProviderId,List<Control>>>(form,"_cardRowControls")[ProviderId.MiniMax];
            Assert.Equal(3,rows.Count);
        });
    }
}

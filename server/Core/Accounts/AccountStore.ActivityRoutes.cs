using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Brs.Local.Accounts;

public sealed partial class AccountStore
{
    // Explicit config argument keeps these routes disconnected until the integrator adds the reviewed optional CLI/constructor wiring.
    public AccountResult ActivityTop(ActivityConfig config, ActivityTopRequest request, int eventType) =>
        ReadActivity(config, request.AuthToken, (engine,c,t,id,player,now) =>
        {
            if (!config.Events.Any(e => e.EventId == request.EventId && e.EventType == eventType)) return new(400);
            var states=engine.ReadMissions(c,t,id,player,now);
            var top = engine.ReadEventTop(c,t,id,player,now,request.EventId,eventType,states);
            var action = ActivitySnapshot(engine,c,t,id,player,now,states);
            foreach(var pair in top) action[pair.Key] = pair.Value;
            return new(200,action);
        });
    public AccountResult ActivityMissionList(ActivityConfig config, ActivityListRequest request) =>
        ReadActivity(config, request.AuthToken, (engine,c,t,id,player,now) =>
        {
            var states=engine.ReadMissions(c,t,id,player,now);
            var action = ActivitySnapshot(engine,c,t,id,player,now,states);
            action["user_mission_details"] = states.Select(s=>s.Wire).ToArray();
            return new(200,action);
        });
    public AccountResult ActivityMissionReceive(ActivityConfig config, ActivityReceiveRequest request)
    {
        if (string.IsNullOrEmpty(request.AuthToken)) return new(401);
        lock(gate)
        {
            using var c=Open(); using var t=c.BeginTransaction(deferred:false);
            try
            {
                var grantContext = new EquipmentGrantContext(request.EquipmentOptions); var now=Now;
                if(!TryReadAuthenticatedAccount(c,t,request.AuthToken,now,out var id,out var player)) return new(401);
                if(!HasFinishedTutorial(player)) return new(409);
                var engine=new ActivityEngine(config,mainlineConfig,eventQuestConfig,questModesConfig);
                var result=engine.Receive(c,t,id,player,now,request.Selection,request.RetryToken,
                    (c1,t1,a,p,n,r)=>GrantActivityRewards(config,c1,t1,a,p,n,r, grantContext));
                if(result.Status!=200) return new(result.Status);
                var states=engine.ReadMissions(c,t,id,result.Player,now);
                if(engine.ObserveDailyClear(c,t,id,result.Player,now,states))states=engine.ReadMissions(c,t,id,result.Player,now);
                var action=ActivitySnapshot(engine,c,t,id,result.Player,now,states);
                action["receive_expired_missions"]=Array.Empty<object>();
                // Reward dialog is built from the selected original Master items. Internal typed_rewards/rewards are a frozen ledger format,
                // not RewardData wire; never dump the internal receipt into action.rewards with an invented RewardType/reason.
                t.Commit(); return new(200,action);
            }
            catch(InvalidDataException) { return new(503); }
            catch(GachaCapacityException) { return new(503); }
            catch(OverflowException) { return new(503); }
            catch(SqliteException) { return new(503); }
        }
    }
    private AccountResult ReadActivity(ActivityConfig config,string? auth,
        Func<ActivityEngine,SqliteConnection,SqliteTransaction,long,string,long,AccountResult> read)
    {
        if(string.IsNullOrEmpty(auth)) return new(401);
        lock(gate)
        {
            using var c=new SqliteConnection(new SqliteConnectionStringBuilder(connectionString){Mode=SqliteOpenMode.ReadOnly}.ToString());c.Open();
            using var t=c.BeginTransaction(deferred:true);
            var now=Now;
            if(!TryReadAuthenticatedAccount(c,t,auth,now,out var id,out var player))return new(401);
            if(!HasFinishedTutorial(player))return new(409);
            var result=read(new ActivityEngine(config,mainlineConfig,eventQuestConfig,questModesConfig),c,t,id,player,now);t.Commit();return result;
        }
    }
    private Dictionary<string,object?> ActivitySnapshot(ActivityEngine engine,SqliteConnection c,SqliteTransaction t,long id,string player,long now,ActivityMissionState[]? states=null)
    {
        return Snapshot(c,t,id,player,nowOverride:now,activityStates:states);
    }
    private static void ProjectActivity(ActivityEngine engine,SqliteConnection c,SqliteTransaction t,long id,string player,long now,Dictionary<string,object?> action,ActivityMissionState[]? states=null,ActivityReadSnapshot? shared=null)
    {
        var current = states ?? engine.ReadMissions(c,t,id,player,now,shared);
        action["mission_count"] = current.Count(s=>s.CanReceive);
        action["u_title"]=engine.TitleRows(c,t,id);
        
        action["u_mission_event"]=current.Where(s=>s.Definition.MissionType==4).Select(s=>s.EventWire).ToArray();
    }
}

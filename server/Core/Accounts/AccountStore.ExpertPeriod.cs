using Microsoft.Data.Sqlite;

namespace Brs.Local.Accounts;

public sealed record ExpertPeriodSelection(long Revision,int QuestTypeId,int ContentPolicyVersion=0);
public sealed record ExpertPeriodState(long Revision,int ActiveQuestTypeId,int DefaultQuestTypeId,ExpertPeriodOption[] Periods,bool HasActiveBattle);
public interface IExpertPeriodContentSnapshot
{
    long SelectionRevision { get; }
    int QuestTypeId { get; }
    int ResourceRevision { get; }
    int MasterRevision { get; }
    int ContentPolicyVersion { get; }
}

public sealed partial class AccountStore
{
    public const int CurrentContentPolicyVersion=1;
    private const string LegacyExpertPeriodSchemaSql="CREATE TABLE expert_period_settings (singleton INTEGER NOT NULL PRIMARY KEY CHECK(singleton=1), quest_type_id INTEGER NOT NULL CHECK(quest_type_id IN (300101,300201,300202,300203,300204,300205,300206)), revision INTEGER NOT NULL CHECK(revision BETWEEN 1 AND 2147483647), updated_at INTEGER NOT NULL CHECK(updated_at>0)) STRICT";
    private const string ExpertPeriodSchemaSql="CREATE TABLE expert_period_settings (singleton INTEGER NOT NULL PRIMARY KEY CHECK(singleton=1), quest_type_id INTEGER NOT NULL CHECK(quest_type_id IN (300101,300201,300202,300203,300204,300205,300206)), revision INTEGER NOT NULL CHECK(revision BETWEEN 1 AND 2147483647), updated_at INTEGER NOT NULL CHECK(updated_at>0), content_policy_version INTEGER NOT NULL DEFAULT 0 CHECK(content_policy_version>=0)) STRICT";
    private int? defaultExpertQuestTypeId;
    private Func<ExpertPeriodSelection,IExpertPeriodContentSnapshot>? prepareExpertPeriodContent;
    private IExpertPeriodContentSnapshot? expertPeriodContent;

    private static void MigrateExpertContentPolicy(SqliteConnection c,SqliteTransaction t)
    {
        Execute(c,t,"ALTER TABLE expert_period_settings RENAME TO expert_period_settings_v23;"+ExpertPeriodSchemaSql+"; INSERT INTO expert_period_settings(singleton,quest_type_id,revision,updated_at,content_policy_version) SELECT singleton,quest_type_id,revision,updated_at,0 FROM expert_period_settings_v23; DROP TABLE expert_period_settings_v23;");
    }

    // Called before listeners start. A policy upgrade changes no quest rules,
    // selection or progress; existing runs remain available after a new login.
    public void ConfigureExpertPeriodContent(Func<ExpertPeriodSelection,IExpertPeriodContentSnapshot> prepare)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        lock(gate)
        {
            if(prepareExpertPeriodContent is not null)throw new InvalidOperationException("Expert period content is already configured.");
            using var c=Open();using var t=c.BeginTransaction(deferred:false);
            var current=ReadExpertPeriod(c,t);
            var upgrade=current.ContentPolicyVersion<CurrentContentPolicyVersion;
            var next=upgrade?current with {Revision=checked(current.Revision+1),ContentPolicyVersion=CurrentContentPolicyVersion}:current;
            // Never persist the success marker before every payload is ready.
            var content=prepare(next);ValidateExpertPeriodContent(content,next);
            if(upgrade)
            {
                WriteExpertPeriod(c,t,next);
                using var q=Command(c,t,"DELETE FROM sessions");q.ExecuteNonQuery();
            }
            t.Commit();
            activeExpertQuestTypeId=next.QuestTypeId;expertPeriodContent=content;prepareExpertPeriodContent=prepare;
        }
    }

    public IExpertPeriodContentSnapshot? TryCaptureExpertPeriodContent()
    {
        lock(gate)return expertPeriodContent;
    }

    private static void ValidateExpertPeriodContent(IExpertPeriodContentSnapshot content,ExpertPeriodSelection selection)
    {
        if(content is null||content.SelectionRevision!=selection.Revision||content.QuestTypeId!=selection.QuestTypeId||content.ContentPolicyVersion!=selection.ContentPolicyVersion||content.ResourceRevision!=checked(146+selection.Revision)||content.MasterRevision!=checked(140+selection.Revision))
            throw new InvalidDataException("Expert period content identity, policy or versions differ.");
    }

    public IExpertPeriodContentSnapshot CaptureExpertPeriodContent()
    {
        lock(gate)return expertPeriodContent??throw new InvalidOperationException("Expert period content is unavailable.");
    }

    private ExpertPeriodSelection ReadExpertPeriod(SqliteConnection c,SqliteTransaction t)
    {
        using var q=Command(c,t,"SELECT revision,quest_type_id,content_policy_version FROM expert_period_settings WHERE singleton=1");
        using var r=q.ExecuteReader();
        var selection=r.Read()?new ExpertPeriodSelection(r.GetInt64(0),r.GetInt32(1),r.GetInt32(2)):new(0,defaultExpertQuestTypeId??throw new InvalidDataException("Expert default is unavailable."),0);
        if(selection.ContentPolicyVersion<0||selection.ContentPolicyVersion>CurrentContentPolicyVersion)
            throw new InvalidDataException("Unsupported content policy; downgrade refused.");
        return selection;
    }

    private void WriteExpertPeriod(SqliteConnection c,SqliteTransaction t,ExpertPeriodSelection selection)
    {
        using var q=Command(c,t,"INSERT INTO expert_period_settings(singleton,quest_type_id,revision,updated_at,content_policy_version) VALUES(1,$id,$revision,$now,$policy) ON CONFLICT(singleton) DO UPDATE SET quest_type_id=excluded.quest_type_id,revision=excluded.revision,updated_at=excluded.updated_at,content_policy_version=excluded.content_policy_version");
        q.Parameters.AddWithValue("$id",selection.QuestTypeId);q.Parameters.AddWithValue("$revision",selection.Revision);
        q.Parameters.AddWithValue("$now",Now);q.Parameters.AddWithValue("$policy",selection.ContentPolicyVersion);q.ExecuteNonQuery();
    }

    public ExpertPeriodSelection GetExpertPeriodSelection()
    {
        lock(gate){using var c=Open();using var t=c.BeginTransaction(deferred:true);return ReadExpertPeriod(c,t);}
    }

    private bool HasActiveBattle(SqliteConnection c,SqliteTransaction t)
    {
        using var q=Command(c,t,"SELECT EXISTS(SELECT 1 FROM quest_runs WHERE state IN(0,1)) OR EXISTS(SELECT 1 FROM quest_loop_sessions WHERE status IN(0,1))");
        return (long)q.ExecuteScalar()! !=0;
    }

    private ExpertPeriodState ExpertPeriodStateFor(SqliteConnection c,SqliteTransaction t,ExpertPeriodSelection selection)
    {
        if(questModesConfig is null||defaultExpertQuestTypeId is null||activeExpertQuestTypeId is null)throw Reject(503,"catalog_unavailable");
        return new(selection.Revision,selection.QuestTypeId,defaultExpertQuestTypeId.Value,ExpertPeriodOptions.ToArray(),HasActiveBattle(c,t));
    }

    public ManagementResult ManagementExpertPeriod()=>ManagementRead((c,t)=>ExpertPeriodStateFor(c,t,ReadExpertPeriod(c,t)));

    public ManagementResult ManagementSetExpertPeriod(long expectedRevision,int questTypeId)=>ManagementRead((c,t)=>
    {
        if(questModesConfig is null||defaultExpertQuestTypeId is null||activeExpertQuestTypeId is null)throw Reject(503,"catalog_unavailable");
        if(expectedRevision<0||!ExpertPeriodOptions.Any(p=>p.QuestTypeId==questTypeId)||!questModesConfig.ExpertSchedules.Any(s=>s.QuestTypeId==questTypeId))throw Reject();
        var current=ReadExpertPeriod(c,t);
        if(current.Revision!=expectedRevision)throw Reject(409,"stale_revision");
        var state=ExpertPeriodStateFor(c,t,current);
        if(current.QuestTypeId==questTypeId)return state;
        if(state.HasActiveBattle)throw Reject(409,"battle_active");
        if(prepareExpertPeriodContent is null||expertPeriodContent is null||current.ContentPolicyVersion!=CurrentContentPolicyVersion)throw Reject(503,"catalog_unavailable");
        var next=current with {Revision=checked(current.Revision+1),QuestTypeId=questTypeId};
        var content=prepareExpertPeriodContent(next);ValidateExpertPeriodContent(content,next);
        WriteExpertPeriod(c,t,next);
        var result=ExpertPeriodStateFor(c,t,next);
        using(var q=Command(c,t,"DELETE FROM sessions"))q.ExecuteNonQuery();
        t.Commit();activeExpertQuestTypeId=questTypeId;expertPeriodContent=content;
        return result;
    });
}

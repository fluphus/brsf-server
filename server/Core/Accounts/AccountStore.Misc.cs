using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Brs.Local.Accounts;

public sealed partial class AccountStore
{
    private MiscCatalog? miscCatalog;
    public void ConfigureMisc(MiscCatalog catalog)
    {
        lock(gate)
        {
            if(miscCatalog is not null)throw new InvalidOperationException("Misc catalog already configured.");
            miscCatalog=catalog;
        }
    }
    private AccountResult MutateMisc(string? auth,string retry,string route,string semantic,
        Func<SqliteConnection,SqliteTransaction,long,string,long,bool> mutate)
    {
        if(miscCatalog is null||formationConfig is null)return new(501);
        if(string.IsNullOrEmpty(auth))return new(401);
        lock(gate)
        {
            using var c=Open();using var t=c.BeginTransaction(deferred:false);var now=Now;
            if(!TryReadAuthenticatedAccount(c,t,auth,now,out var id,out var player))return new(401);
            var hash=Hash(retry);var old=FindEconomyReceipt(c,t,id,hash);
            if(old is not null)return old.Route==route&&old.Semantic==semantic?new(200,Snapshot(c,t,id,player,nowOverride:now)):new(409);
            if(!HasFinishedTutorial(player)||!mutate(c,t,id,player,now))return new(409);
            var audit=JsonSerializer.Serialize(new{source="local-misc",transaction_id=Guid.NewGuid().ToString("D"),route});
            InsertEconomyReceipt(c,t,id,hash,route,semantic,MiscCatalog.ExpectedFingerprint,audit,"{}",now);
            var action=Snapshot(c,t,id,player,nowOverride:now);t.Commit();return new(200,action);
        }
    }
    public AccountResult ReadStory(StoryReadRequest request)
    {
        if(request.StoryId<=0)return new(400);
        return MutateMisc(request.AuthToken,request.RetryToken,StoryReadRequest.Route,request.Semantic,(c,t,id,player,now)=>
        {
            if(!AccessibleStories(c,t,id,player).Contains(request.StoryId))return false;
            using var q=Command(c,t,"INSERT INTO story_reads(account_id,story_id,read_at) VALUES($id,$story,$now) ON CONFLICT(account_id,story_id) DO NOTHING");
            q.Parameters.AddWithValue("$id",id);q.Parameters.AddWithValue("$story",request.StoryId);q.Parameters.AddWithValue("$now",now);q.ExecuteNonQuery();return true;
        });
    }
    public AccountResult SetCharaVoices(CharaSetVoiceRequest request)
    {
        if(request.CharaVoices.Length is <1 or >128||request.CharaVoices.Sum(x=>x.CharaVoiceIds.Length)>2608||
            request.CharaVoices.Any(x=>x.CharaVoiceIds.Length==0||!CharaSetVoiceRequest.TryDate(x.PlayDate,out _)||x.CharaVoiceIds.Any(n=>n<=0)))return new(400);
        return MutateMisc(request.AuthToken,request.RetryToken,CharaSetVoiceRequest.Route,request.Semantic,(c,t,id,_,now)=>
        {
            var cards=FormationCards(c,t,id);var profile=ReadUserProfile(c,t,id);var current=DateTimeOffset.FromUnixTimeSeconds(now).ToOffset(TimeSpan.FromHours(9)).DateTime;
            var pending=new Dictionary<int,(MiscVoice Voice,string Date)>();
            foreach(var group in request.CharaVoices)
            {
                CharaSetVoiceRequest.TryDate(group.PlayDate,out var date);
                // Queued valid historical dates survive reconnects. No arbitrary
                // retention cutoff. A local calendar lower bound and authoritative
                // JST now reject invalid or future reports.
                if(date<new DateTime(2000,1,1)||date>current)return false;
                foreach(var voiceId in group.CharaVoiceIds)
                {
                    var voice=miscCatalog!.Voices.SingleOrDefault(v=>v.CharaVoiceId==voiceId&&!v.IsDeleted);
                    if(voice is null||!cards.Values.Any(card=>card.CharaId==voice.CharaId)||
                        voice.CardId>0&&!cards.ContainsKey(voice.CardId)||!VoiceReportEligible(voice,date,profile?.Birthday??""))return false;
                    if(!pending.TryGetValue(voiceId,out var prior)||string.CompareOrdinal(group.PlayDate,prior.Date)<0)pending[voiceId]=(voice,group.PlayDate);
                }
            }
            var existing=new HashSet<int>();
            using(var q=Command(c,t,"SELECT chara_voice_id FROM owned_chara_voices WHERE account_id=$id"))
            {q.Parameters.AddWithValue("$id",id);using var r=q.ExecuteReader();while(r.Read())existing.Add(r.GetInt32(0));}
            var additions=pending.Where(x=>!existing.Contains(x.Key)).OrderBy(x=>x.Key).ToArray();
            using(var capacity=Command(c,t,"SELECT MAX(value) FROM (SELECT COALESCE(MAX(id),0) value FROM owned_chara_voices UNION ALL SELECT COALESCE(MAX(seq),0) FROM sqlite_sequence WHERE name='owned_chara_voices')"))
                if((long)capacity.ExecuteScalar()!>int.MaxValue-additions.Length)return false;
            foreach(var item in additions)
            {
                using var q=Command(c,t,"INSERT INTO owned_chara_voices(account_id,chara_id,chara_voice_id,play_date,recorded_at) VALUES($id,$chara,$voice,$date,$now) ON CONFLICT(account_id,chara_voice_id) DO NOTHING");
                q.Parameters.AddWithValue("$id",id);q.Parameters.AddWithValue("$chara",item.Value.Voice.CharaId);q.Parameters.AddWithValue("$voice",item.Key);
                q.Parameters.AddWithValue("$date",item.Value.Date);q.Parameters.AddWithValue("$now",now);q.ExecuteNonQuery();
            }
            return true;
        });
    }
    private bool VoiceReportEligible(MiscVoice voice,DateTime date,string birthday)
    {
        if(voice.ReleaseType is 1 or 2)return true;
        if(voice.ReleaseType!=3)return false;
        if(voice.GeneralParam1=="player_birthday")return miscCatalog!.InBirthdayTerm(date,birthday);
        if(voice.GeneralParam1=="chara_birthday"&&int.TryParse(voice.GeneralParam2,NumberStyles.None,CultureInfo.InvariantCulture,out var target))
        {
            var chara=miscCatalog!.Charas.SingleOrDefault(x=>x.CharaId==target&&!x.IsDeleted);
            return chara is not null&&miscCatalog.InBirthdayTerm(date,chara.Birthday);
        }
        // The proven set_voice caller reports birthday entitlement/played voice
        // records. Awakening and battle-result unlock producers are separate;
        // do not grant those through an unproven arbitrary ID submission.
        return false;
    }
    public AccountResult UpdateSupport(UpdateSupportRequest request)
    {
        if(request.CardId<=0||request.EquipIds.Length!=3||request.EquipIds.Any(n=>n<0))return new(400);
        return MutateMisc(request.AuthToken,request.RetryToken,UpdateSupportRequest.Route,request.Semantic,(c,t,id,_,_)=>
        {
            var slots=new[]{new DeckSlot(request.CardId,request.EquipIds)}.Concat(Enumerable.Range(0,5).Select(_=>new DeckSlot(0,new int[3]))).ToArray();
            if(!FormationRules.ValidateDeck(formationConfig!,slots,FormationCards(c,t,id),FormationEquips(c,t,id)))return false;
            using var q=Command(c,t,"INSERT INTO user_support_leaders(account_id,card_id,equip_1,equip_2,equip_3) VALUES($id,$card,$a,$b,$d) ON CONFLICT(account_id) DO UPDATE SET card_id=excluded.card_id,equip_1=excluded.equip_1,equip_2=excluded.equip_2,equip_3=excluded.equip_3");
            q.Parameters.AddWithValue("$id",id);q.Parameters.AddWithValue("$card",request.CardId);
            for(var i=0;i<3;i++)q.Parameters.AddWithValue(new[]{"$a","$b","$d"}[i],request.EquipIds[i]==0?DBNull.Value:request.EquipIds[i]);
            q.ExecuteNonQuery();return true;
        });
    }
    private static HashSet<int> CompletedStoryQuests(SqliteConnection c,SqliteTransaction t,long id)
    {
        var clear=new HashSet<int>();
        using var q=Command(c,t,"SELECT quest_id FROM user_quests WHERE account_id=$id AND clear_rate>0 UNION SELECT quest_id FROM event_quests WHERE account_id=$id AND clear_rate>0");
        q.Parameters.AddWithValue("$id",id);using var r=q.ExecuteReader();while(r.Read())clear.Add(r.GetInt32(0));return clear;
    }
    private HashSet<int> AccessibleStories(SqliteConnection c,SqliteTransaction t,long id,string player,HashSet<int>? completed=null)
    {
        var clear=completed??CompletedStoryQuests(c,t,id);
        var storyIds=miscCatalog!.Quests.Where(q=>!q.IsDeleted&&(q.QuestType is 1 or 2||questModesConfig?.Data.Find(q.QuestId)!=null)&&clear.Contains(q.QuestId)).SelectMany(q=>new[]{q.BeforeStoryId,q.AfterStoryId}).ToHashSet();
        return miscCatalog.Stories.Where(s=>!s.IsDeleted&&(storyIds.Contains(s.StoryId)||s.StoryType==9&&HasFinishedTutorial(player))).Select(s=>s.StoryId).ToHashSet();
    }
    private int ProfileMainQuest(SqliteConnection c,SqliteTransaction t,long id)
    {
        if(miscCatalog is null)return 0;
        var clear=new HashSet<int>();using(var q=Command(c,t,"SELECT quest_id FROM user_quests WHERE account_id=$id"))
        {q.Parameters.AddWithValue("$id",id);using var r=q.ExecuteReader();while(r.Read())clear.Add(r.GetInt32(0));}
        return miscCatalog.Quests.Where(q=>!q.IsDeleted&&q.QuestType==1&&clear.Contains(q.QuestId)).OrderBy(q=>q.PartNo).ThenBy(q=>q.ChapterNo).ThenBy(q=>q.StageNo).ThenBy(q=>q.QuestId).LastOrDefault()?.QuestId??0;
    }
    private void ProjectMisc(SqliteConnection c,SqliteTransaction t,long id,string player,Dictionary<string,object?> action)
    {
        if(miscCatalog is null)return;
        var read=new HashSet<int>();using(var q=Command(c,t,"SELECT story_id FROM story_reads WHERE account_id=$id"))
        {q.Parameters.AddWithValue("$id",id);using var r=q.ExecuteReader();while(r.Read())read.Add(r.GetInt32(0));}
        var completed=CompletedStoryQuests(c,t,id);
        action["u_story"]=AccessibleStories(c,t,id,player,completed).Order().Select(s=>JsonSerializer.SerializeToElement(new{story_id=s,status=read.Contains(s)?1:0})).ToArray();
        action["u_still"]=StoryStillCatalog.Owned(read,completed).Select(s=>JsonSerializer.SerializeToElement(new{still_id=s})).ToArray();
        var voices=new List<JsonElement>();using(var q=Command(c,t,"SELECT id,chara_id,chara_voice_id FROM owned_chara_voices WHERE account_id=$id ORDER BY id"))
        {q.Parameters.AddWithValue("$id",id);using var r=q.ExecuteReader();while(r.Read())voices.Add(JsonSerializer.SerializeToElement(new{id=r.GetInt32(0),user_id=checked((int)id),chara_id=r.GetInt32(1),chara_voice_id=r.GetInt32(2)}));}
        action["u_chara_voice"]=voices.ToArray();
        using(var q=Command(c,t,"SELECT card_id,COALESCE(equip_1,0),COALESCE(equip_2,0),COALESCE(equip_3,0) FROM user_support_leaders WHERE account_id=$id"))
        {
            q.Parameters.AddWithValue("$id",id);using var r=q.ExecuteReader();
            if(r.Read()){action["u_user_leader"]=new[]{JsonSerializer.SerializeToElement(new{card_id=r.GetInt32(0),user_equip_ids=string.Join(',',Enumerable.Range(1,3).Select(r.GetInt32))})};return;}
        }
        // Read-only owned fallback. Editing persists explicit support state.
        using(var q=Command(c,t,"SELECT card_id FROM owned_cards WHERE account_id=$id ORDER BY user_card_id LIMIT 1"))
        {q.Parameters.AddWithValue("$id",id);action["u_user_leader"]=q.ExecuteScalar() is long card?new[]{JsonSerializer.SerializeToElement(new{card_id=checked((int)card),user_equip_ids="0,0,0"})}:Array.Empty<JsonElement>();}
    }
    private static void DetachSupportEquipReferences(SqliteConnection c,SqliteTransaction t,long account,IReadOnlyCollection<int> consumedIds)
    {
        // Called by growth's existing donor detach under its write transaction.
        // Per-ID bound statements keep SQL independent of client strings/limits.
        foreach(var id in consumedIds.Distinct())
        {
            using var q=Command(c,t,"UPDATE user_support_leaders SET equip_1=CASE WHEN equip_1=$equip THEN NULL ELSE equip_1 END,equip_2=CASE WHEN equip_2=$equip THEN NULL ELSE equip_2 END,equip_3=CASE WHEN equip_3=$equip THEN NULL ELSE equip_3 END WHERE account_id=$account AND (equip_1=$equip OR equip_2=$equip OR equip_3=$equip)");
            q.Parameters.AddWithValue("$account",account);q.Parameters.AddWithValue("$equip",id);q.ExecuteNonQuery();
        }
    }
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Brs.Local.Accounts;

public sealed partial class AccountStore
{
    private sealed record BattleVoiceSelection(int CharaVoiceId,int[] NewVoiceIds,int[] CandidateIds,int[] FrozenOwnCardIds,bool PreferredUnowned)
    {
        public string Source => "local-battle-voice-unowned-uniform-v1";
        public bool EntitlementNotPlayback => true;
    }
    private BattleVoiceSelection GrantBattleVoice(SqliteConnection c,SqliteTransaction t,long account,JsonElement payload,long now,Func<int,int>? next)
    {
        if(miscCatalog is null)return new(0,[],[],[],false);
        var slots=payload.GetProperty("support_user_id").GetInt32()==0?6:5;
        var ownIds=Enumerable.Range(1,slots).Select(slot=>payload.GetProperty("deck").GetProperty($"slot_{slot}_card_id").GetInt32()).Where(id=>id>0).ToHashSet();
        var cards=payload.GetProperty("cards").EnumerateArray().Where(card=>ownIds.Contains(card.GetProperty("card_id").GetInt32()))
            .Select(card=>(Card:card.GetProperty("card_id").GetInt32(),Chara:card.GetProperty("chara_id").GetInt32())).ToArray();
        if(cards.Length!=ownIds.Count||cards.Any(card=>formationConfig is null||!formationConfig.Cards.Any(def=>!def.IsDeleted&&def.CardId==card.Card&&def.CharaId==card.Chara)||
            !miscCatalog.Charas.Any(def=>!def.IsDeleted&&def.CharaId==card.Chara)))throw new MainlineRejectedException();
        var charas=cards.Select(card=>card.Chara).ToHashSet();
        var candidates=cards.SelectMany(card=>VoiceUnlockRules.Match(miscCatalog,card.Chara,card.Card,2101))
            .Where(voice=>voice.ReleaseType==3&&voice.GeneralParam1=="A"&&int.TryParse(voice.GeneralParam2,NumberStyles.None,CultureInfo.InvariantCulture,out var partner)&&charas.Contains(partner))
            .Select(voice=>voice.CharaVoiceId).Distinct().Order().ToArray();
        if(candidates.Length==0)return new(0,[],[],ownIds.Order().ToArray(),false);
        var owned=new HashSet<int>();
        using(var q=Command(c,t,"SELECT chara_voice_id FROM owned_chara_voices WHERE account_id=$id"))
        {q.Parameters.AddWithValue("$id",account);using var r=q.ExecuteReader();while(r.Read())owned.Add(r.GetInt32(0));}
        var missing=candidates.Where(id=>!owned.Contains(id)).ToArray();var pool=missing.Length>0?missing:candidates;
        var pick=next is null?RandomNumberGenerator.GetInt32(pool.Length):next(pool.Length);
        if(pick<0||pick>=pool.Length)throw new GachaCapacityException();
        var selected=pool[pick];var granted=SaveVoiceEntitlements(c,t,account,[selected],now);
        return new(selected,granted,candidates,ownIds.Order().ToArray(),missing.Length>0);
    }
    private int[] GrantAwakeningVoices(SqliteConnection c,SqliteTransaction t,long account,int charaId,int cardId,int before,int after,long now)
    {
        if(miscCatalog is null||after<=before)return [];
        return SaveVoiceEntitlements(c,t,account,AwakeningVoiceCandidates(charaId,cardId,before,after),now);
    }
    private int[] AwakeningVoiceCandidates(int charaId,int cardId,int before,int after)
    {
        if(miscCatalog is null||after<=before)return [];
        static int Level(MiscVoice voice)=>int.TryParse(voice.PlaceId==1703?voice.GeneralParam1:voice.GeneralParam2,
            NumberStyles.None,CultureInfo.InvariantCulture,out var n)?n:0;
        return new[]{1300,1703}.SelectMany(place=>VoiceUnlockRules.Match(miscCatalog,charaId,cardId,place))
            .Where(v=>v.ReleaseType==3&&(v.PlaceId==1703||v.GeneralParam1=="awakening"))
            .Where(v=>Level(v)>before&&Level(v)<=after).Select(v=>v.CharaVoiceId).Distinct().Order().ToArray();
    }
    private void ObserveAwakeningVoiceEntitlements(SqliteConnection c,SqliteTransaction t,long account,long now,string triggerRoute)
    {
        if(miscCatalog is null)return;
        if(triggerRoute is not("user/login"or"home"))throw new InvalidDataException("Invalid voice entitlement trigger.");
        var cards=new List<(int Card,int Chara,int Awakening)>();
        using(var q=Command(c,t,"SELECT card_id,chara_id,awakening_count FROM owned_cards WHERE account_id=$id AND awakening_count>0 ORDER BY card_id"))
        {q.Parameters.AddWithValue("$id",account);using var r=q.ExecuteReader();while(r.Read())cards.Add((r.GetInt32(0),r.GetInt32(1),r.GetInt32(2)));}
        var evidence=cards.Select(card=>new{card_id=card.Card,chara_id=card.Chara,awakening_count=card.Awakening,
            eligible_voice_ids=AwakeningVoiceCandidates(card.Chara,card.Card,0,card.Awakening)}).Where(card=>card.eligible_voice_ids.Length>0).ToArray();
        var additions=SaveVoiceEntitlements(c,t,account,evidence.SelectMany(card=>card.eligible_voice_ids).Distinct().ToArray(),now);
        if(additions.Length==0)return;
        var key=Hash($"local-voice-eligibility:{account}:{string.Join(',',additions)}");
        if(FindEconomyReceipt(c,t,account,key)is not null)throw new InvalidDataException("Voice entitlement receipt already exists without its ownership.");
        var audit=JsonSerializer.Serialize(new{source="current-awakening-entitlement",trigger_route=triggerRoute,
            entitlement_not_playback=true,granted_at=now,voice_unlock_ids=additions,current_cards=evidence});
        // A fully awakened roster can exceed the existing semantic length limit.
        // Keep the complete conditions and IDs in audit, with a bounded digest key.
        InsertEconomyReceipt(c,t,account,key,"system/voice-eligibility","voice-eligibility:"+Convert.ToHexStringLower(key),MiscCatalog.ExpectedFingerprint,audit,"{}",now);
    }
    // This table represents original u_chara_voice ownership. For automatic
    // grants its legacy play_date column records today's entitlement date;
    // the owning economy receipt identifies the grant source, not playback.
    private int[] SaveVoiceEntitlements(SqliteConnection c,SqliteTransaction t,long account,IReadOnlyCollection<int> voiceIds,long now)
    {
        if(miscCatalog is null||voiceIds.Count==0)return [];
        var existing=new HashSet<int>();
        using(var q=Command(c,t,"SELECT chara_voice_id FROM owned_chara_voices WHERE account_id=$id"))
        {q.Parameters.AddWithValue("$id",account);using var r=q.ExecuteReader();while(r.Read())existing.Add(r.GetInt32(0));}
        var additions=voiceIds.Distinct().Where(id=>!existing.Contains(id)).Order().ToArray();
        if(additions.Length==0)return [];
        using(var capacity=Command(c,t,"SELECT MAX(value) FROM (SELECT COALESCE(MAX(id),0) value FROM owned_chara_voices UNION ALL SELECT COALESCE(MAX(seq),0) FROM sqlite_sequence WHERE name='owned_chara_voices')"))
            if((long)capacity.ExecuteScalar()!>int.MaxValue-additions.Length)throw new GachaCapacityException();
        var date=DateTimeOffset.FromUnixTimeSeconds(now).ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
        foreach(var voiceId in additions)
        {
            var voice=miscCatalog.Voices.Single(v=>v.CharaVoiceId==voiceId&&!v.IsDeleted);
            using var q=Command(c,t,"INSERT INTO owned_chara_voices(account_id,chara_id,chara_voice_id,play_date,recorded_at) VALUES($id,$chara,$voice,$date,$now)");
            q.Parameters.AddWithValue("$id",account);q.Parameters.AddWithValue("$chara",voice.CharaId);q.Parameters.AddWithValue("$voice",voiceId);
            q.Parameters.AddWithValue("$date",date);q.Parameters.AddWithValue("$now",now);q.ExecuteNonQuery();
        }
        return additions;
    }
}

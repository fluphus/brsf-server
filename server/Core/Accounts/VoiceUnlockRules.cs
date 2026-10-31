namespace Brs.Local.Accounts;

// Original CharaVoiceMaster136 exclusions for the seven conditional unique
// voices. All other conditional voices have an empty IgnoreVoiceIds value.
// Generated from source SHA8082ce3e...; identity/conditions remain in misc.json.
internal static class VoiceUnlockRules
{
    private static readonly int[] UniqueAwakeningIgnore = [51010,51011,51012,51013,51014];
    private static readonly int[] UniqueHomeIgnore = [31002,31003,31004,31005,31006,31007,31008,31009,31010,31011,31012,31013,31014,31015,31016,31036,31037,31038,31039];
    internal static IReadOnlyList<int> Ignore(int voiceId) => voiceId switch
    {
        800051010 or 800051011 or 800051012 or 800051013 or 800051014 => UniqueAwakeningIgnore,
        800031010 or 800031011 => UniqueHomeIgnore,
        _ => []
    };
    internal static MiscVoice[] Match(MiscCatalog catalog,int charaId,int cardId,int place)
    {
        var voices=catalog.Voices.Where(v=>!v.IsDeleted&&v.CharaId==charaId&&v.PlaceId==place&&(v.CardId==0||v.CardId==cardId)).ToArray();
        var ignored=voices.Where(v=>v.CardId==cardId&&cardId>0).SelectMany(v=>Ignore(v.CharaVoiceId)).ToHashSet();
        return voices.Where(v=>!ignored.Contains(v.CharaVoiceId)).ToArray();
    }
}

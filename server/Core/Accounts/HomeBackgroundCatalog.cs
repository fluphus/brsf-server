using System.Collections.Frozen;

namespace Brs.Local.Accounts;

internal sealed record HomeBackgroundRule(int BackgroundId,bool IsDefault,bool IsDeleted);

// Exact original BackgroundMaster projection, independent of ownership grants.
// Default backgrounds are selectable without writing u_background entitlement.
internal static class HomeBackgroundCatalog
{
    internal const string SourceMasterSha256="8082ce3e3746a8e38d58c7e640892b80caef374f767a1d39f84db94d7108945b";
    private static readonly FrozenDictionary<int,HomeBackgroundRule> Rules=new HomeBackgroundRule[]
    {
        new(100101,true,false),
        new(100102,true,false),
        new(100103,false,false),
        new(100201,true,false),
        new(100301,true,false),
        new(100401,true,false),
        new(100501,false,false),
        new(100502,false,false),
        new(100503,false,false),
        new(100504,false,false),
        new(100601,false,false),
        new(100602,false,false),
        new(100603,false,false),
        new(100604,false,false),
        new(100701,false,false),
        new(100702,false,false),
        new(100703,false,false),
        new(100801,false,false),
        new(100802,false,false),
        new(100803,false,false),
        new(100901,false,false),
        new(100902,false,false),
        new(100903,false,false),
        new(100904,false,false),
        new(101001,false,false),
        new(101101,false,false),
        new(101201,false,false),
        new(101202,false,false),
        new(101301,false,false),
        new(101302,false,false),
        new(101401,false,false),
        new(101501,false,false),
        new(101601,false,false),
        new(101701,false,false),
        new(101702,false,false),
        new(101801,false,false),
    }.ToFrozenDictionary(r=>r.BackgroundId);
    internal static HomeBackgroundRule? Find(int backgroundId)=>Rules.GetValueOrDefault(backgroundId);
}

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Brs.Local.Accounts;
public sealed record ExpeditionDestination(int ExpeditionId,int Time,int CardExp,int ConditionQuestId,int ExpeditionDropId,int SkipPaidCoin,int EventId,bool IsDeleted);
public sealed record ExpeditionBonus(int DeckCardCount,int DropFrame,int SuccessRate,int CardExpRate,bool IsDeleted);
public sealed record ExpeditionDrop(int Id,int ExpeditionDropId,int Rarity,int Weight,int ItemType,int ItemId,int ItemQty,bool IsDeleted);
public sealed record ExpeditionProbability(int Rarity,int Weight,bool IsDeleted);
public sealed record ExpeditionUnlock(int Id,int ConditionType,int ConditionValue,int AddSlotCount,bool IsDeleted);
public sealed record ExpeditionFrozen(string PolicyVersion,ExpeditionDestination Destination,ExpeditionBonus Bonus,ExpeditionDrop[] Drops,ExpeditionProbability[] Probabilities);
public sealed record ExpeditionDropItem(int Rarity,int ItemType,int ItemId,int ItemQty);
public sealed record ExpeditionSettlement(int SuccessType,int CardExp,ExpeditionDropItem[] Drops);

public sealed record ExpeditionCatalog(string PolicyVersion,string SourceMasterSha256,string SourceExcerptSha256,
 string Rarity9Policy,int NormalFrames,string GreatFramesPolicy,string ExpPolicy,string SkipPolicy,string RestartPolicy,string ProfileCountPolicy,
 ExpeditionDestination[] Destinations,ExpeditionBonus[] Bonuses,ExpeditionDrop[] Drops,ExpeditionProbability[] Probabilities,ExpeditionUnlock[] Unlocks)
{
 public const string ExpectedFingerprint="465472319fb3a93d4c0d42496d5cb50bc0ed13ce4f49d4ca92a28bf0d2c8d595";
 internal static readonly JsonSerializerOptions Json=new(){PropertyNamingPolicy=JsonNamingPolicy.SnakeCaseLower,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
 public static ExpeditionCatalog Load(string path)
 {
  var f=new FileInfo(path);if(!f.Exists||f.Length is <1 or >262144||(f.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Invalid expedition configuration.");
  var bytes=File.ReadAllBytes(path);if(Convert.ToHexStringLower(SHA256.HashData(bytes))!=ExpectedFingerprint)throw new InvalidDataException("Expedition configuration fingerprint mismatch.");
  var c=JsonSerializer.Deserialize<ExpeditionCatalog>(bytes,Json)??throw new InvalidDataException("Missing expedition configuration.");
  if(c.PolicyVersion!="local-expedition-v1"||c.SourceMasterSha256!="8082ce3e3746a8e38d58c7e640892b80caef374f767a1d39f84db94d7108945b"||c.SourceExcerptSha256.Length!=64||
   c.Rarity9Policy!="one-original-row-guaranteed-per-run"||c.NormalFrames!=1||c.GreatFramesPolicy!="original-bonus-total"||c.ExpPolicy!="ordinary-current-cap-floor-great-rate-discard-overflow-no-mastery"||
   c.SkipPolicy!="fixed-free-first-mark-due-result-only"||c.RestartPolicy!="claimed-only-last-exact-roster"||c.ProfileCountPolicy!="claimed-runs"||
   c.Destinations.Length!=10||c.Drops.Length!=200||c.Bonuses.Length!=3||c.Probabilities.Length!=3||c.Unlocks.Length!=3||
   !c.Destinations.Select(x=>x.ExpeditionId).Order().SequenceEqual(Enumerable.Range(1000001,10))||
   c.Drops.Select(x=>x.Id).Distinct().Count()!=200||!c.Bonuses.OrderBy(x=>x.DeckCardCount).Select(x=>(x.DeckCardCount,x.DropFrame,x.SuccessRate,x.CardExpRate)).SequenceEqual(new[]{(1,2,1000,120),(2,3,2000,120),(3,4,3000,120)})||
   !c.Probabilities.OrderBy(x=>x.Rarity).Select(x=>(x.Rarity,x.Weight)).SequenceEqual(new[]{(1,80),(2,15),(3,5)})||
   c.Destinations.Any(x=>x.IsDeleted||x.EventId!=0||x.Time is not(7200 or 14400 or 28800)||x.CardExp is not(270 or 516 or 976)||x.SkipPaidCoin!=x.Time/90)||
   c.Drops.Any(x=>x.IsDeleted||x.Weight<=0||x.ItemQty<=0||x.ItemType is not(3 or 4 or 6 or 7 or 12)))throw new InvalidDataException("Unsupported expedition policy.");
  foreach(var d in c.Destinations)
  {
   var rows=c.Drops.Where(x=>x.ExpeditionDropId==d.ExpeditionDropId).ToArray();var fixedRow=rows.Where(x=>x.Rarity==9).ToArray();
   if(rows.Length==0||fixedRow.Length!=1||fixedRow[0].ItemType!=6||fixedRow[0].ItemId!=80011||fixedRow[0].ItemQty!=d.Time/360||c.Probabilities.Any(p=>!rows.Any(x=>x.Rarity==p.Rarity)))throw new InvalidDataException("Incomplete expedition rewards.");
  }
  return c;
 }
 internal ExpeditionFrozen Freeze(int destination,int members)
 {
  var d=Destinations.Single(x=>x.ExpeditionId==destination&&!x.IsDeleted);var b=Bonuses.Single(x=>x.DeckCardCount==members&&!x.IsDeleted);
  return new(PolicyVersion,d,b,Drops.Where(x=>x.ExpeditionDropId==d.ExpeditionDropId&&!x.IsDeleted).OrderBy(x=>x.Id).ToArray(),Probabilities.Where(x=>!x.IsDeleted).OrderBy(x=>x.Rarity).ToArray());
 }
}
public static class ExpeditionEngine
{
 public static ExpeditionSettlement Draw(ExpeditionFrozen f,Func<int,int>? next=null)
 {
  if(f.PolicyVersion!="local-expedition-v1")throw new InvalidDataException("Unknown frozen expedition policy.");next??=RandomNumberGenerator.GetInt32;
  var roll=next(10000);if(roll is <0 or >=10000)throw new InvalidDataException("Invalid expedition random source.");
  var success=roll<f.Bonus.SuccessRate?2:1;var count=success==2?f.Bonus.DropFrame:1;
  var rewards=new List<ExpeditionDropItem>();var fixedRow=f.Drops.Single(x=>x.Rarity==9);rewards.Add(new(9,fixedRow.ItemType,fixedRow.ItemId,fixedRow.ItemQty));
  for(var i=0;i<count;i++)
  {
   var rarity=GachaWeights.Pick(f.Probabilities,p=>p.Weight,next).Rarity;var item=GachaWeights.Pick(f.Drops.Where(x=>x.Rarity==rarity).ToArray(),x=>x.Weight,next);
   rewards.Add(new(rarity,item.ItemType,item.ItemId,item.ItemQty));
  }
  var xp=success==2?checked((int)((long)f.Destination.CardExp*f.Bonus.CardExpRate/100)):f.Destination.CardExp;
  return new(success,xp,rewards.GroupBy(x=>(x.Rarity,x.ItemType,x.ItemId)).Select(g=>new ExpeditionDropItem(g.Key.Rarity,g.Key.ItemType,g.Key.ItemId,checked(g.Sum(x=>x.ItemQty)))).OrderBy(x=>x.Rarity).ThenBy(x=>x.ItemType).ThenBy(x=>x.ItemId).ToArray());
 }
 public static GrowthExperience CardExperience(GrowthCatalog c,int card,int lv,int exp,int awakening,int xp)
 {
  var row=c.Card(card);var cap=c.CardCap(card,awakening);var group=GrowthCatalog.I(row,"LvGroupId");
  if(xp<0||lv<1||lv>cap||exp<c.Threshold("CardLvMaster",group,lv)||exp>c.Threshold("CardLvMaster",group,cap)||lv<cap&&exp>=c.Threshold("CardLvMaster",group,lv+1))throw new GrowthRejectedException();
  var after=(int)Math.Min(c.Threshold("CardLvMaster",group,cap),checked((long)exp+xp));var level=lv;while(level<cap&&after>=c.Threshold("CardLvMaster",group,level+1))level++;
  return new(level,after,xp,after-exp,0,1);
 }
}

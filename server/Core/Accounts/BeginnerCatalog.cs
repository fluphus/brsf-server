using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Brs.Local.Accounts;

public sealed record BeginnerMissionRule(int MissionId,int StepId,int ContentType,string ContentValue,int ClearScore,int TakeoverMissionId,int ItemGroupId);
public sealed record BeginnerStepRule(int StepId,int ItemGroupId);
public sealed record BeginnerRewardRule(int Id,int ItemGroupId,int ItemType,int ItemId,int ItemQty);
public sealed record LoginBonusRule(int LoginBonusId,int Type,string OpenDate,string CloseDate,bool IsDeleted);
public sealed record LoginBonusDayRule(int Id,int LoginBonusId,int DayCount,int ItemGroupId,bool IsDeleted);
public sealed record LoginBonusRewardRule(int Id,int LoginBonusId,int ItemGroupId,int ItemType,int ItemId,int ItemQty,bool IsDeleted);
public sealed record BeginnerCatalog(string PolicyVersion,string SourceMasterSha256,int CampaignId,int BeginnerLoginBonusId,
 BeginnerMissionRule[] Missions,BeginnerStepRule[] Steps,BeginnerRewardRule[] Rewards,LoginBonusRule[] LoginBonuses,LoginBonusDayRule[] LoginDays,LoginBonusRewardRule[] LoginRewards)
{
 public const string ExpectedFingerprint="366b97ec5996f0c8cb914b63b0b5bd4f8fd06920687e7b7da4fb02893b5d3acd";
 public static BeginnerCatalog Load(string path)
 {
  var f=new FileInfo(path);if(!f.Exists||f.Length is <1 or >1048576||(f.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Invalid beginner catalog.");
  var bytes=File.ReadAllBytes(path);if(Convert.ToHexStringLower(SHA256.HashData(bytes))!=ExpectedFingerprint)throw new InvalidDataException("Beginner catalog fingerprint mismatch.");
  var c=JsonSerializer.Deserialize<BeginnerCatalog>(bytes,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.SnakeCaseLower,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow})??throw new InvalidDataException("Missing beginner catalog.");
  if(c.PolicyVersion!="local-beginner-login-v1"||c.SourceMasterSha256!="8082ce3e3746a8e38d58c7e640892b80caef374f767a1d39f84db94d7108945b"||c.CampaignId!=10000101||c.BeginnerLoginBonusId!=4001||c.Missions.Length!=50||c.Steps.Length!=5||c.Rewards.Length!=63||c.LoginBonuses.Length!=98||c.LoginDays.Length!=517||c.LoginRewards.Length!=665)throw new InvalidDataException("Unsupported beginner policy.");
  if(c.Missions.Select(m=>m.MissionId).Distinct().Count()!=50||!c.Steps.Select(s=>s.StepId).Order().SequenceEqual(Enumerable.Range(1,5))||c.Missions.Any(m=>m.StepId is <1 or >5||m.ClearScore<=0)||c.Missions.GroupBy(m=>m.StepId).Any(g=>g.Count()!=10))throw new InvalidDataException("Invalid original beginner steps.");
  var groups=c.Missions.Select(m=>m.ItemGroupId).Concat(c.Steps.Select(s=>s.ItemGroupId)).ToHashSet();
  if(groups.Count!=55||!groups.SetEquals(c.Rewards.Select(r=>r.ItemGroupId))||c.Rewards.Any(r=>r.ItemQty<=0||r.ItemType is not(2 or 3 or 4 or 5 or 6 or 7 or 11 or 12))||c.Rewards.Select(r=>r.Id).Distinct().Count()!=63)throw new InvalidDataException("Invalid beginner reward closure.");
  foreach(var m in c.Missions.Where(m=>m.TakeoverMissionId!=0)){var old=c.Missions.SingleOrDefault(o=>o.MissionId==m.TakeoverMissionId);if(old is null||old.StepId>=m.StepId||old.ContentType!=m.ContentType||old.ContentValue!=m.ContentValue)throw new InvalidDataException("Invalid original takeover edge.");}
  return c;
 }
 public CommerceReward[] RewardsFor(int group)=>Rewards.Where(r=>r.ItemGroupId==group).OrderBy(r=>r.Id).Select(r=>new CommerceReward(r.ItemType,r.ItemId,r.ItemQty)).ToArray();
 public CommerceReward[] LoginRewardsFor(int bonus,int group)=>LoginRewards.Where(r=>r.LoginBonusId==bonus&&r.ItemGroupId==group&&!r.IsDeleted).OrderBy(r=>r.Id).Select(r=>new CommerceReward(r.ItemType,r.ItemId,r.ItemQty)).ToArray();
}

namespace Brs.Local.Accounts;

// Original19 nondefault StillMaster entries. Root-approved local policy: real read OR related clear,
// not proof that after-story was watched. Original two defaults remain client implicit.
internal static class StoryStillCatalog
{
 private sealed record Rule(int StillId,int StoryId,int[] QuestIds);
 private static readonly Rule[] Rules=
 [
  new(2000101,100100011,[10010001]),
  new(2000201,100100041,[10010007]),
  new(2000301,100100011,[10010001]),
  new(2000401,100101072,[10010107]),
  new(2000501,100101091,[10010109]),
  new(2000703,100102091,[10010211]),
  new(2000801,100103092,[10010311]),
  new(2000901,100104092,[10010412]),
  new(2001001,100105081,[10010511]),
  new(2001101,100106082,[10010612]),
  new(2001201,100107091,[10010714]),
  new(2001401,100108101,[10010813]),
  new(2001501,100110071,[10011009]),
  new(2001601,100110051,[10011007]),
  new(2001702,100110092,[10011012]),
  new(3000101,210101191,[21010122]),
  new(3000301,210401131,[21040117]),
  new(3000401,210601121,[21060116,21120116]),
  new(3000501,210801061,[21080107,21130107]),
 ];
 internal static int[] Owned(IReadOnlySet<int> readStories,IReadOnlySet<int> clearedQuests)=>Rules.Where(r=>readStories.Contains(r.StoryId)||r.QuestIds.Any(clearedQuests.Contains)).Select(r=>r.StillId).Distinct().Order().ToArray();
}

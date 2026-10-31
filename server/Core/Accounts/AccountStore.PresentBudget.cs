using System.Text.Json;
using Microsoft.Data.Sqlite;
namespace Brs.Local.Accounts;
public sealed partial class AccountStore
{
 private sealed class PresentBudget
 {
  private readonly AccountStore owner;private readonly SqliteConnection c;private readonly SqliteTransaction t;private readonly long account;
  internal long Free,Paid,GameCoin,FriendPoint,Ordinary;
  internal readonly Dictionary<(int Type,int Id),long> Quantities=[];
  internal readonly HashSet<int> Cards=[],Titles=[],MaterialRows=[];
  private readonly Dictionary<string,long> maxima=[];private readonly Dictionary<string,long> added=[];
  internal PresentBudget(AccountStore owner,SqliteConnection c,SqliteTransaction t,long account,string player)
  {
   this.owner=owner;this.c=c;this.t=t;this.account=account;using var d=JsonDocument.Parse(player);long W(string k)=>d.RootElement.GetProperty(k).GetInt64();Free=W("free_paid_coin");Paid=W("paid_coin");GameCoin=W("game_coin");FriendPoint=W("friend_pt");
   if(Free<0||Paid<0||GameCoin<0||FriendPoint<0)throw new InvalidDataException("Invalid existing present wallet.");
   long N(string sql){using var q=Command(c,t,sql);q.Parameters.AddWithValue("$a",account);return(long)q.ExecuteScalar()!;}
   Ordinary=owner.ActiveOrdinaryEquipmentCount(c,t,account);
   void Fill(string table,string column,HashSet<int> target){using var q=Command(c,t,"SELECT "+column+" FROM "+table+" WHERE account_id=$a");q.Parameters.AddWithValue("$a",account);using var r=q.ExecuteReader();while(r.Read())target.Add(r.GetInt32(0));}
   Fill("owned_cards","card_id",Cards);Fill("owned_titles","title_id",Titles);Fill("owned_equip_materials","equip_id",MaterialRows);
   foreach(var pair in new[]{("owned_cards","user_card_id"),("owned_equips","user_equip_id"),("owned_titles","id"),("owned_equip_materials","id")})maxima[pair.Item1]=N("SELECT MAX(v)FROM(SELECT COALESCE(MAX("+pair.Item2+"),0)v FROM "+pair.Item1+" UNION ALL SELECT COALESCE(MAX(seq),0)FROM sqlite_sequence WHERE name='"+pair.Item1+"')");
  }
  private PresentBudget(PresentBudget b)
  {owner=b.owner;c=b.c;t=b.t;account=b.account;Free=b.Free;Paid=b.Paid;GameCoin=b.GameCoin;FriendPoint=b.FriendPoint;Ordinary=b.Ordinary;foreach(var p in b.Quantities)Quantities.Add(p.Key,p.Value);Cards.UnionWith(b.Cards);Titles.UnionWith(b.Titles);MaterialRows.UnionWith(b.MaterialRows);foreach(var p in b.maxima)maxima.Add(p.Key,p.Value);foreach(var p in b.added)added.Add(p.Key,p.Value);}
  private bool Space(string table,long qty)
  {var next=added.GetValueOrDefault(table)+qty;if(next>int.MaxValue-maxima[table])return false;added[table]=next;return true;}
  private bool Quantity(int type,int id,long qty)
  {
   var key=(type,id);if(!Quantities.TryGetValue(key,out var current))current=type==7?owner.ReadEquipMaterial(c,t,account,id):CommerceQuantity(c,t,account,type,id,0);
   if(current<0||current>CommerceItemCap)throw new InvalidDataException("Invalid existing present quantity.");if(qty>CommerceItemCap-current)return false;Quantities[key]=current+qty;return true;
  }
  private bool Add(CommerceReward reward,PresentDefinition definition,List<CommerceReward> credits,List<object> conversions,List<int> duplicateTitles)
  {
   var qty=reward.ItemQty;var id=reward.ItemId;if(qty<=0)throw new InvalidDataException("Invalid frozen present quantity.");
   switch(reward.ItemType)
   {
    case 2:if(Free<0||Paid<0||qty>CommerceItemCap-Free||Free+Paid>int.MaxValue-qty)return false;Free+=qty;break;
    case 3:if(FriendPoint<0||qty>CommerceItemCap-FriendPoint)return false;FriendPoint+=qty;break;
    case 4:if(GameCoin<0||qty>999999999-GameCoin)return false;GameCoin+=qty;break;
    case 5:
     var fresh=!Cards.Contains(id);var duplicates=qty-(fresh?1:0);
     if(fresh){if(!Space("owned_cards",1))return false;Cards.Add(id);}
     if(duplicates>0)
     {
      if(!Quantity(11,id,duplicates))return false;credits.Add(new(11,id,duplicates));
      conversions.Add(new{item_type=11,item_id=id,item_qty=duplicates,before=new{item_type=5,item_id=id,item_qty=duplicates,before=(object?)null}});
     }
     if(fresh)credits.Add(new(5,id,1));return true;
    case 7:
     if(owner.inventoryMaterialConfig.Find(id)is not null)
     {if(!Quantity(7,id,qty))return false;if(!MaterialRows.Contains(id)){if(!Space("owned_equip_materials",1))return false;MaterialRows.Add(id);}}
     else {if(qty>OrdinaryEquipmentCapacity-Ordinary||!Space("owned_equips",qty))return false;Ordinary+=qty;}
     break;
    case 10:
     if(qty!=1)throw new InvalidDataException("Invalid unique present quantity.");
     if(Titles.Contains(id)){duplicateTitles.Add(id);return true;}
     if(!Space("owned_titles",1))return false;Titles.Add(id);break;
    case 6:case 11:case 12:if(!Quantity(reward.ItemType,id,qty))return false;break;
    default:throw new InvalidDataException("Unsupported frozen present type.");
   }
   credits.Add(reward);return true;
  }
  internal PresentBudget? TryAccept(CommerceReward reward,PresentDefinition definition,bool selling,out CommerceReward[] credits,out object[] conversions,out int[] duplicateTitles)
  {
   var next=new PresentBudget(this);var cr=new List<CommerceReward>();var cv=new List<object>();var du=new List<int>();bool fits;
   if(!selling)fits=next.Add(reward,definition,cr,cv,du);
   else
   {
    var gf=checked((long)definition.SaleGameCoin*reward.ItemQty);var item=checked((long)definition.SaleItemQty*reward.ItemQty);
    fits=gf<=int.MaxValue&&item<=int.MaxValue;
    if(fits&&gf>0)fits=next.Add(new(4,0,(int)gf),definition,cr,cv,du);
    if(fits&&item>0)fits=next.Add(new(6,50001,(int)item),definition,cr,cv,du);
   }
   credits=cr.ToArray();conversions=cv.ToArray();duplicateTitles=du.ToArray();return fits?next:null;
  }
 }
 private string ApplyPresentCredits(SqliteConnection c,SqliteTransaction t,long account,string player,long now,PresentBudget budget,CommerceReward[] credits, EquipmentGrantContext grantContext)
 {
  // Planning finished for all rows before the first mutation. Known row capacity
  // rejection never uses catch-and-continue after a partial inventory write.
  foreach(var group in credits.Where(x=>x.ItemType is 5 or 7).GroupBy(x=>(x.ItemType,x.ItemId)))
  {
   var qty=checked(group.Sum(x=>x.ItemQty));var d=presentCatalog!.Definition(group.Key.ItemType,group.Key.ItemId);
   if(group.Key.ItemType==5){for(var i=0;i<qty;i++)_ =GrantCard(c,t,account,d.Card!);}
   else if(inventoryMaterialConfig.Find(group.Key.ItemId)is not null)_=GrantEquip(c,t,account,now,d.Equip!, grantContext,qty);
   else for(var i=0;i<qty;i++)_ =GrantEquip(c,t,account,now,d.Equip!, grantContext);
  }
  // Converted card duplicates are explicit type11 credits, not a second card grant.
  foreach(var pair in budget.Quantities)
  {
   if(pair.Key.Type==7)continue;var table=CommerceQuantityTable(pair.Key.Type);
   using var q=Command(c,t,$"UPDATE {table} SET item_qty=$q WHERE account_id=$a AND item_id=$i");q.Parameters.AddWithValue("$a",account);q.Parameters.AddWithValue("$i",pair.Key.Id);q.Parameters.AddWithValue("$q",pair.Value);
   if(q.ExecuteNonQuery()==0){q.CommandText=$"INSERT INTO {table}(account_id,item_id,item_qty)VALUES($a,$i,$q)";q.ExecuteNonQuery();}
  }
  foreach(var title in credits.Where(x=>x.ItemType==10).Select(x=>x.ItemId).Distinct())
  {using var q=Command(c,t,"INSERT INTO owned_titles(account_id,title_id)VALUES($a,$i)ON CONFLICT(account_id,title_id)DO NOTHING");q.Parameters.AddWithValue("$a",account);q.Parameters.AddWithValue("$i",title);q.ExecuteNonQuery();}
  var wallet=System.Text.Json.Nodes.JsonNode.Parse(player)!.AsObject();wallet["free_paid_coin"]=budget.Free;wallet["paid_coin"]=budget.Paid;wallet["game_coin"]=budget.GameCoin;wallet["friend_pt"]=budget.FriendPoint;var updated=wallet.ToJsonString();if(updated!=player)SavePlayer(c,t,account,updated);return updated;
 }
}

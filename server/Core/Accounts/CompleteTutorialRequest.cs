using System.Text.Json;
namespace Brs.Local.Accounts;
public sealed record CompleteTutorialRequest(string Route,string Retry,string? Auth,string Semantic,int QuestId=0,GrowthRequest? Card=null,int Equip=0,int[]? Donors=null,DeckEditRequest? Deck=null,string? Name=null)
{
    public EquipmentGrantOptions EquipmentOptions { get; init; }

 public static readonly string[] Routes=["tutorial/name_set","tutorial/quest_result","tutorial/card_enhance","tutorial/equip_enhance","tutorial/deck_edit"];
 public static CompleteTutorialRequest Read(string route,JsonElement root)
 {
  if(!Routes.Contains(route,StringComparer.Ordinal))throw new InvalidDataException("Unknown tutorial route.");
  if(route=="tutorial/card_enhance")
  {var q=GrowthRequest.Read("card/enhance",root);return new(route,q.RetryToken,q.AuthToken,route+":"+q.Semantic,Card:q) { EquipmentOptions = EquipmentGrantOptions.Read(Brs.Local.Transport.RequestEnvelope.ReadCommon(root)) };}
  if(route=="tutorial/deck_edit")
  {var q=DeckEditRequest.Read(root);return new(route,q.RetryToken,q.AuthToken,route+":"+q.Semantic,Deck:q) { EquipmentOptions = EquipmentGrantOptions.Read(Brs.Local.Transport.RequestEnvelope.ReadCommon(root)) };}
  if(route=="tutorial/name_set")
  {var q=ProfileTextRequest.Read(root,"profile/name_change");return new(route,q.RetryToken,q.AuthToken,route+":"+q.Semantic,Name:q.Text) { EquipmentOptions = EquipmentGrantOptions.Read(Brs.Local.Transport.RequestEnvelope.ReadCommon(root)) };}
  if(route=="tutorial/equip_enhance")
  {var c=MainlineFields.Read(root,"user_equip_id","material_user_equip_ids");var target=MainlineFields.Int(root,"user_equip_id");var ids=VipRequestFields.IntArray(root,"material_user_equip_ids",20,true);if(target<=0||ids.Any(i=>i<=0)||ids.Contains(target))throw new InvalidDataException("Invalid tutorial equipment.");return new(route,MainlineFields.Retry(c),MainlineFields.Auth(c),MainlineFields.Semantic(route,new{target,ids=ids.Order().ToArray()}),Equip:target,Donors:ids) { EquipmentOptions = EquipmentGrantOptions.Read(Brs.Local.Transport.RequestEnvelope.ReadCommon(root)) };}
  var common=MainlineFields.Read(root,"quest_id","clear_rate","battle_time","kill_ability_count","main_skill_count","defeat_enemy","defeat_boss","is_all_defense_base_safety","is_loop_mode","loop_count","select_reward_id","battle_data","battle_time_ta","total_power","get_drop_place_ids");
  var quest=MainlineFields.Int(root,"quest_id");
  bool EmptyMap(string key)=>root.GetProperty(key).ValueKind==JsonValueKind.Null||root.GetProperty(key).ValueKind==JsonValueKind.Object&&!root.GetProperty(key).EnumerateObject().Any();
  if(quest<=0||MainlineFields.Int(root,"clear_rate")!=6||new[]{"battle_time","kill_ability_count","main_skill_count","loop_count","select_reward_id"}.Any(k=>MainlineFields.Int(root,k)!=0)||!EmptyMap("defeat_enemy")||!EmptyMap("defeat_boss")||MainlineFields.Bool(root,"is_all_defense_base_safety")||MainlineFields.Bool(root,"is_loop_mode")||root.GetProperty("battle_data").GetString()!=""||MainlineFields.Float(root,"battle_time_ta")!=0||MainlineFields.Float(root,"total_power")!=0||VipRequestFields.IntArray(root,"get_drop_place_ids",0).Length!=0)throw new InvalidDataException("Tutorial result differs from the original preset caller.");
  return new(route,MainlineFields.Retry(common),MainlineFields.Auth(common),MainlineFields.Semantic(route,new{quest}),QuestId:quest) { EquipmentOptions = EquipmentGrantOptions.Read(Brs.Local.Transport.RequestEnvelope.ReadCommon(root)) };
 }
}

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Brs.Local.Accounts;

public sealed partial class AccountStore
{
    private sealed record GrowthCardState(int CardId,int Level,int Exp,int Awakening,int MainSkill,int KillAbility,int KillZone);
    private sealed record GrowthEquipState(int Id,int EquipId,int Level,int Exp,int OverLimit,bool Locked,string Properties);
    // Explicit config argument keeps this batch disconnected until reviewed CLI/Program wiring is applied.
    public AccountResult MutateGrowth(GrowthCatalog config,GrowthRequest request,Func<int,int>? growthRandom=null)
    {
        if(string.IsNullOrEmpty(request.AuthToken))return new(401);
        lock(gate)
        {
            using var c=Open();using var t=c.BeginTransaction(deferred:false);var now=Now;
            if(!TryReadAuthenticatedAccount(c,t,request.AuthToken,now,out var account,out var player))return new(401);
            if(!HasFinishedTutorial(player))return new(409);
            try
            {
                var hash=Hash(request.RetryToken);var old=FindEconomyReceipt(c,t,account,hash);
                if(old is not null)
                {
                    if(old.Route!=request.Route||old.Semantic!=request.Semantic)return new(409);
                    using var frozen=JsonDocument.Parse(old.Result);
                    if(frozen.RootElement.GetProperty("source").GetString()!="local-growth-v1"||frozen.RootElement.GetProperty("route").GetString()!=request.Route)throw new InvalidDataException("Invalid frozen growth receipt.");
                    var replay=Snapshot(c,t,account,player,nowOverride:now);
                    foreach(var p in frozen.RootElement.GetProperty("action").EnumerateObject())replay[p.Name]=p.Value.Clone();
                    return new(200,replay);
                }
                var wallet=JsonNode.Parse(player)!.AsObject();var extras=new Dictionary<string,object?>();var audit=new Dictionary<string,object?>
                {
                    ["card_id"]=request.CardId,["user_equip_id"]=request.UserEquipId,["equip_id"]=0,["awakening_delta"]=0,["over_limit_delta"]=0,
                    ["awakening_before"]=0,["awakening_after"]=0,["over_limit_before"]=0,["over_limit_after"]=0,["operation_count"]=1,
                    ["selected_unit_count"]=0,["client_total_power"]=request.TotalPower
                };
                var gfSpent=0;var gfGained=0;ulong freeSpent=0,paidSpent=0;var apAdded=0;
                var consumed=new List<GrowthQuantity>();var consumedInstances=new List<GrowthEquipState>();var grants=new List<CommerceReward>();
                void Exec(string sql,params (string Key,object Value)[] args)
                {
                    using var q=Command(c,t,sql);q.Parameters.AddWithValue("$account",account);
                    foreach(var a in args)q.Parameters.AddWithValue(a.Key,a.Value);if(q.ExecuteNonQuery()<1)throw new GrowthRejectedException();
                }
                GrowthCardState Card(int card)
                {
                    using var q=Command(c,t,"SELECT card_id,lv,exp,awakening_count,main_skill_lv,kill_ability_lv,kill_zone_skill_lv FROM owned_cards WHERE account_id=$account AND card_id=$card");
                    q.Parameters.AddWithValue("$account",account);q.Parameters.AddWithValue("$card",card);using var r=q.ExecuteReader();
                    return r.Read()?new(r.GetInt32(0),r.GetInt32(1),r.GetInt32(2),r.GetInt32(3),r.GetInt32(4),r.GetInt32(5),r.GetInt32(6)):throw new GrowthRejectedException();
                }
                GrowthEquipState Equip(int id)
                {
                    using var q=Command(c,t,"SELECT user_equip_id,equip_id,lv,exp,over_limit_count,is_lock,free_property_ids FROM owned_equips WHERE account_id=$account AND user_equip_id=$equip AND is_deleted=0");
                    q.Parameters.AddWithValue("$account",account);q.Parameters.AddWithValue("$equip",id);using var r=q.ExecuteReader();
                    if(!r.Read())throw new GrowthRejectedException();var result=new GrowthEquipState(r.GetInt32(0),r.GetInt32(1),r.GetInt32(2),r.GetInt32(3),r.GetInt32(4),r.GetInt32(5)!=0,r.GetString(6));
                    if(inventoryMaterialConfig.Find(result.EquipId)is not null)throw new GrowthRejectedException();return result;
                }
                void Spend(GrowthQuantity item)
                {
                    if(item.Quantity<=0)throw new GrowthRejectedException();
                    if(item.ItemType==7)
                    {
                        if(inventoryMaterialConfig.Find(item.ItemId)is null||ReadEquipMaterial(c,t,account,item.ItemId)<item.Quantity)throw new GrowthRejectedException();
                        Exec("UPDATE owned_equip_materials SET qty=qty-$qty WHERE account_id=$account AND equip_id=$item AND qty>=$qty",("$qty",item.Quantity),("$item",item.ItemId));
                    }
                    else
                    {
                        if(item.ItemType is not(6 or 11 or 12))throw new GrowthRejectedException();var before=CommerceQuantity(c,t,account,item.ItemType,item.ItemId,0);
                        if(before<item.Quantity)throw new GrowthRejectedException();var table=CommerceQuantityTable(item.ItemType);
                        // Schema11 already permits zero in all three quantity tables. Keep the
                        // zero row so original incremental client merges clear cached quantities.
                        Exec($"UPDATE {table} SET item_qty=item_qty-$qty WHERE account_id=$account AND item_id=$item AND item_qty>=$qty",("$item",item.ItemId),("$qty",item.Quantity));
                    }
                    consumed.Add(item);
                }
                void Consume(GrowthEquipState item)
                {
                    if(item.Locked||item.Id==request.UserEquipId)throw new GrowthRejectedException();
                    DetachSupportEquipReferences(c,t,account,new[]{item.Id});
                    using(var remove=Command(c,t,"DELETE FROM deck_slot_equips WHERE account_id=$account AND user_equip_id=$equip"))
                    {remove.Parameters.AddWithValue("$account",account);remove.Parameters.AddWithValue("$equip",item.Id);remove.ExecuteNonQuery();}
                    Exec("UPDATE owned_equips SET is_deleted=1 WHERE account_id=$account AND user_equip_id=$equip AND is_deleted=0 AND is_lock=0",("$equip",item.Id));consumedInstances.Add(item);
                }
                (GrowthDonor[] Plan,GrowthEquipState[] Ordinary) Donors()
                {
                    var instances=request.EquipIds.Select(Equip).ToArray();if(instances.Any(e=>e.Locked||e.Id==request.UserEquipId))throw new GrowthRejectedException();
                    foreach(var item in request.Items)if(item.ItemType!=7||inventoryMaterialConfig.Find(item.ItemId)is null||ReadEquipMaterial(c,t,account,item.ItemId)<item.Quantity)throw new GrowthRejectedException();
                    var plan=instances.Select(e=>new GrowthDonor(e.EquipId,1,e.OverLimit,e.Id)).Concat(request.Items.Select(i=>new GrowthDonor(i.ItemId,i.Quantity))).ToArray();return(plan,instances);
                }
                int Success()=>GrowthEngine.SuccessType(growthRandom?.Invoke(10000)??RandomNumberGenerator.GetInt32(10000));
                void CreditConsume(int item,int qty)
                {
                    if(qty<=0)return;_ =config.One("ItemConsumeMaster",r=>GrowthCatalog.I(r,"ItemId")==item);var before=CommerceQuantity(c,t,account,6,item,0);
                    if(before>MaximumBusinessItemQuantity-(long)qty)throw new GachaCapacityException();
                    Exec("INSERT INTO owned_consumes(account_id,item_id,item_qty) VALUES($account,$item,$qty) ON CONFLICT(account_id,item_id) DO UPDATE SET item_qty=item_qty+excluded.item_qty",("$item",item),("$qty",qty));grants.Add(new(6,item,qty));
                }
                switch(request.Route)
                {
                    case "card/enhance":
                    {
                        var before=Card(request.CardId);var plan=GrowthEngine.EnhanceCard(config,before.CardId,before.Level,before.Exp,before.Awakening,request.Items,Success());
                        gfSpent=plan.GameCoin;foreach(var item in request.Items)Spend(item);
                        Exec("UPDATE owned_cards SET lv=$lv,exp=$exp WHERE account_id=$account AND card_id=$card",("$lv",plan.Level),("$exp",plan.Exp),("$card",before.CardId));
                        audit["before"]=before;audit["after"]=plan;audit["selected_unit_count"]=request.Items.Sum(x=>(long)x.Quantity);extras["success_type"]=plan.SuccessType;break;
                    }
                    case "card/awakening":
                    {
                        var before=Card(request.CardId);var plan=GrowthEngine.Awaken(config,before.CardId,before.Level,before.Awakening,request.IsCommonItem);gfSpent=plan.GameCoin;Spend(plan.Cost);
                        Exec("UPDATE owned_cards SET awakening_count=$count WHERE account_id=$account AND card_id=$card",("$count",plan.AwakeningCount),("$card",before.CardId));
                        if(plan.UnlockSkinId>0)
                        {
                            using var unlock=Command(c,t,"INSERT OR IGNORE INTO owned_card_skins(account_id,card_id,card_skin_id) VALUES($account,$card,$skin)");
                            unlock.Parameters.AddWithValue("$account",account);unlock.Parameters.AddWithValue("$card",before.CardId);unlock.Parameters.AddWithValue("$skin",plan.UnlockSkinId);unlock.ExecuteNonQuery();
                        }
                        audit["awakening_before"]=before.Awakening;audit["awakening_after"]=plan.AwakeningCount;audit["awakening_delta"]=plan.AwakeningCount-before.Awakening;
                        audit["voice_source"]="local-voice-awakening-v1";
                        audit["voice_unlock_ids"]=GrantAwakeningVoices(c,t,account,GrowthCatalog.I(config.Card(before.CardId),"CharaId"),before.CardId,before.Awakening,plan.AwakeningCount,now);
                        audit["before"]=before;audit["after"]=plan;audit["selected_unit_count"]=plan.Cost.Quantity;break;
                    }
                    case "card/skill/enhance":
                    {
                        var before=Card(request.CardId);var current=request.SkillType switch{1=>before.KillAbility,2=>before.MainSkill,3=>before.KillZone,_=>throw new GrowthRejectedException()};
                        var plan=GrowthEngine.Skill(config,before.CardId,before.Awakening,request.SkillType,current);gfSpent=plan.GameCoin;foreach(var item in plan.Costs)Spend(item);
                        var col=request.SkillType switch{1=>"kill_ability_lv",2=>"main_skill_lv",3=>"kill_zone_skill_lv",_=>throw new GrowthRejectedException()};
                        Exec($"UPDATE owned_cards SET {col}=$lv WHERE account_id=$account AND card_id=$card",("$lv",plan.Level),("$card",before.CardId));
                        audit["before"]=before;audit["after"]=plan;audit["selected_unit_count"]=plan.Costs.Sum(x=>(long)x.Quantity);break;
                    }
                    case "equip/enhance":
                    {
                        var before=Equip(request.UserEquipId);var donors=Donors();var plan=GrowthEngine.EnhanceEquip(config,before.EquipId,before.Level,before.Exp,before.OverLimit,donors.Plan,Success());
                        gfSpent=plan.GameCoin;foreach(var item in request.Items)Spend(item);foreach(var item in donors.Ordinary)Consume(item);
                        Exec("UPDATE owned_equips SET lv=$lv,exp=$exp,over_limit_count=$count WHERE account_id=$account AND user_equip_id=$equip AND is_deleted=0",("$lv",plan.Level),("$exp",plan.Exp),("$count",plan.OverLimitCount),("$equip",before.Id));
                        audit["equip_id"]=before.EquipId;audit["before"]=before;audit["after"]=plan;audit["over_limit_before"]=before.OverLimit;audit["over_limit_after"]=plan.OverLimitCount;
                        audit["over_limit_delta"]=plan.OverLimitCount-before.OverLimit;audit["selected_unit_count"]=donors.Plan.Sum(x=>x.Quantity);extras["success_type"]=plan.SuccessType;break;
                    }
                    case "equip/sale":
                    {
                        var donors=Donors();var plan=GrowthEngine.Sale(config,donors.Plan);gfGained=plan.GameCoin;
                        foreach(var item in request.Items)Spend(item);foreach(var item in donors.Ordinary)Consume(item);CreditConsume(plan.ConsumeItemId,plan.ConsumeItemQuantity);
                        audit["after"]=plan;audit["selected_unit_count"]=donors.Plan.Sum(x=>x.Quantity);break;
                    }
                    case "equip/lock":
                    {
                        var before=request.Changes.Select(change=>(State:Equip(change.UserEquipId),change.IsLock)).ToArray();
                        foreach(var item in before)Exec("UPDATE owned_equips SET is_lock=$lock WHERE account_id=$account AND user_equip_id=$equip AND is_deleted=0",("$lock",item.IsLock),("$equip",item.State.Id));
                        audit["before"]=before.Select(x=>x.State).ToArray();audit["after"]=request.Changes;audit["selected_unit_count"]=before.Length;break;
                    }
                    case "equip/grant_property":
                    {
                        var before=Equip(request.UserEquipId);var donor=Equip(request.MaterialUserEquipId);if(donor.Locked||donor.Id==before.Id)throw new GrowthRejectedException();
                        var plan=GrowthEngine.Property(config,before.EquipId,donor.EquipId,before.Properties,donor.Properties,request.SlotNo,request.MaterialSlotNo);gfSpent=plan.GameCoin;Consume(donor);
                        Exec("UPDATE owned_equips SET free_property_ids=$props WHERE account_id=$account AND user_equip_id=$equip AND is_deleted=0",("$props",plan.FreePropertyIds),("$equip",before.Id));
                        audit["equip_id"]=before.EquipId;audit["before"]=before;audit["after"]=plan;audit["slot_no"]=request.SlotNo;audit["material_slot_no"]=request.MaterialSlotNo;audit["selected_unit_count"]=1;break;
                    }
                    case "item/cure_stamina":
                    {
                        var before=MainlineStamina.Read(wallet,now);var plan=GrowthEngine.Cure(config,before.Current,before.Maximum,request.GemRefills,request.Items,now);apAdded=plan.StaminaAdded;
                        var debited=DebitCurrency(wallet.ToJsonString(),plan.GemCost,out freeSpent,out paidSpent)??throw new GrowthRejectedException();wallet=JsonNode.Parse(debited)!.AsObject();
                        foreach(var item in request.Items)Spend(item);var after=new MainlineStamina(before.Current+apAdded,before.Maximum,before.Current+apAdded>=before.Maximum?now:before.Anchor);after.Save(wallet);
                        audit["before"]=before;audit["after"]=after;audit["selected_unit_count"]=request.GemRefills+request.Items.Sum(x=>(long)x.Quantity);break;
                    }
                    default:throw new GrowthRejectedException();
                }
                using(var persisted=JsonDocument.Parse(wallet.ToJsonString()))
                {
                    var gf=ReadCurrency(persisted.RootElement,"game_coin");if(gf<(ulong)gfSpent)throw new GrowthRejectedException();
                    var after=checked(gf-(ulong)gfSpent+(ulong)gfGained);if(after>999999999)throw new GachaCapacityException();wallet["game_coin"]=after;
                }
                audit["consumed_items"]=consumed;audit["consumed_instances"]=consumedInstances;audit["typed_grants"]=grants;
                var result=JsonSerializer.Serialize(new{source="local-growth-v1",route=request.Route,transaction_id=Guid.NewGuid().ToString("D"),action=extras,audit});
                var cost=JsonSerializer.Serialize(new{game_coin_spent=gfSpent,game_coin_gained=gfGained,free_spent=freeSpent,paid_spent=paidSpent,stamina_added=apAdded,items=consumed});
                SavePlayer(c,t,account,wallet.ToJsonString());InsertEconomyReceipt(c,t,account,hash,request.Route,request.Semantic,config.Fingerprint,result,cost,now);
                ObserveActivityDailyClear(c,t,account,wallet.ToJsonString(),now);var action=Snapshot(c,t,account,wallet.ToJsonString(),nowOverride:now);foreach(var p in extras)action[p.Key]=p.Value;
                t.Commit();return new(200,action);
            }
            catch(GrowthRejectedException){return new(409);}
            catch(GachaCapacityException){return new(503);}
            catch(OverflowException){return new(503);}
            catch(InvalidDataException){return new(503);}
            catch(SqliteException){return new(503);}
        }
    }
}

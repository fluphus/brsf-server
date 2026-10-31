using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Brs.Local.Accounts;

public sealed partial class AccountStore
{
    private const int OrdinaryEquipmentCapacity = 999;

    private long ActiveOrdinaryEquipmentCount(SqliteConnection c, SqliteTransaction t, long account)
    {
        using var q = Command(c, t, "SELECT COUNT(*) FROM owned_equips WHERE account_id=$a AND is_deleted=0 AND equip_id NOT IN (" +
            string.Join(',', inventoryMaterialConfig.Materials.Select(m => m.EquipId)) + ")");
        q.Parameters.AddWithValue("$a", account);
        return (long)q.ExecuteScalar()!;
    }

    private static string OverflowProducerPrefix(BattleEquipmentOverflow overflow) =>
        $"quest-overflow:{overflow.RunId}:{overflow.RetryHash}:{overflow.ConfigHash}:";

    private Dictionary<string, object?> QueueBattleEquipmentOverflow(SqliteConnection c, SqliteTransaction t,
        long account, long now, GachaEquip equip, BattleEquipmentOverflow overflow)
    {
        if (presentCatalog is null) throw new GachaCapacityException();
        var prefix = OverflowProducerPrefix(overflow);
        // Ordinals include every queued copy for this exact account/run/request/config.
        // There is no mutable counter outside this serialized transaction.
        using var q = Command(c, t, "SELECT COUNT(*) FROM present_equips WHERE account_id=$a AND producer_key GLOB $prefix");
        q.Parameters.AddWithValue("$a", account); q.Parameters.AddWithValue("$prefix", prefix + "*");
        var ordinal = (long)q.ExecuteScalar()!;
        EnqueueGeneralPresents(c, t, account, now, prefix + ordinal, Convert.FromHexString(overflow.RetryHash),
            overflow.ConfigHash, 101, 0, [new(7, equip.EquipId, 1)], isOverQtyLimit: true);
        // A queued item is first acquired when actually received. No group/lock
        // state or owned instance is written before that future request.
        return Lot(7, equip.EquipId, false);
    }

    private object[] BattleEquipmentOverflowNotifications(SqliteConnection c, SqliteTransaction t,
        long account, EquipmentGrantContext grantContext)
    {
        if (grantContext.Overflow is not { } overflow || presentCatalog is null) return [];
        using var q = Command(c, t, "SELECT equip_id,qty FROM present_equips WHERE account_id=$a AND producer_key GLOB $prefix ORDER BY user_present_equip_id");
        q.Parameters.AddWithValue("$a", account); q.Parameters.AddWithValue("$prefix", OverflowProducerPrefix(overflow) + "*");
        using var r = q.ExecuteReader(); var result = new List<object>();
        while (r.Read()) result.Add(new { item_type = 7, item_id = r.GetInt32(0), item_qty = r.GetInt32(1) });
        return result.ToArray();
    }

    private bool AutomaticEquipmentLock(GachaEquip equip, bool firstGroup, EquipmentGrantOptions options)
    {
        if (options.RarityEquipAutoLock is not (0 or 4 or 5)) throw new InvalidDataException("Invalid equipment rarity threshold.");
        if (options.IsNewEquipAutoLock && firstGroup) return true;
        if (options.RarityEquipAutoLock == 0) return false;
        if (presentCatalog is null) throw new GachaCapacityException();
        return presentCatalog.Definition(7, equip.EquipId).Rarity >= options.RarityEquipAutoLock;
    }
}

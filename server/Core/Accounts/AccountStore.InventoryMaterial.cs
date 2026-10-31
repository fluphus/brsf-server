using Microsoft.Data.Sqlite;

namespace Brs.Local.Accounts;

public sealed partial class AccountStore
{
    private const string OwnedEquipMaterialSql = "CREATE TABLE owned_equip_materials (id INTEGER PRIMARY KEY AUTOINCREMENT CHECK(id BETWEEN 1 AND 2147483647), account_id INTEGER NOT NULL REFERENCES accounts(account_id), equip_id INTEGER NOT NULL CHECK(equip_id BETWEEN 1 AND 2147483647), qty INTEGER NOT NULL CHECK(qty BETWEEN 0 AND 99999999), UNIQUE(account_id,equip_id)) STRICT";
    private void EnsureNoLegacyMaterialRows(SqliteConnection c, SqliteTransaction t, int version)
    {
        if (version < 4) return; // Those historical schemas have no equipment table.
        using var q = Command(c, t, "SELECT COUNT(*) FROM owned_equips WHERE equip_id IN (" +
            string.Join(',', inventoryMaterialConfig.Materials.Select(m => m.EquipId)) + ")");
        // Includes deleted and non-initial instances. Client cache reconstruction
        // is not proven, so no automatic archive, deletion or quantity recovery.
        if ((long)q.ExecuteScalar()! != 0)
            throw new InvalidDataException("Legacy equipment material requires a separately verified recovery plan.");
    }

    private static void MigrateInventoryMaterialSchema(SqliteConnection c, SqliteTransaction t)
        => Execute(c, t, OwnedEquipMaterialSql + "; PRAGMA user_version=12;");

    private long ReadEquipMaterial(SqliteConnection c, SqliteTransaction t, long account, int equipId)
    {
        using var q = Command(c, t, "SELECT qty FROM owned_equip_materials WHERE account_id=$id AND equip_id=$equip");
        q.Parameters.AddWithValue("$id", account); q.Parameters.AddWithValue("$equip", equipId);
        var qty = q.ExecuteScalar() is long value ? value : 0;
        if (qty < 0 || qty > inventoryMaterialConfig.QuantityCap) throw new GachaCapacityException();
        return qty;
    }

    private void EnsureEquipmentCapacity(SqliteConnection c, SqliteTransaction t, long account, IEnumerable<(GachaEquip Equip, int Qty)> grants, EquipmentGrantContext grantContext)
    {
        long ordinary = 0; var materials = new Dictionary<int, long>();
        foreach (var (equip, qty) in grants)
        {
            if (qty <= 0) throw new GachaCapacityException();
            if (inventoryMaterialConfig.Find(equip.EquipId) is { } material)
            {
                if (equip != material.Equipment) throw new InvalidDataException("Material equipment definition differs from its original mapping.");
                materials[equip.EquipId] = checked(materials.GetValueOrDefault(equip.EquipId) + qty);
            }
            else ordinary = checked(ordinary + qty);
        }
        if (ordinary > int.MaxValue) throw new GachaCapacityException();
        if (ordinary > 0)
        {
            var free = Math.Max(0, OrdinaryEquipmentCapacity - ActiveOrdinaryEquipmentCount(c, t, account));
            if (grantContext.Overflow is null && ordinary > free) throw new GachaCapacityException();
            EnsureInstanceCapacity(c, t, "owned_equips", checked((int)Math.Min(ordinary, free)));
        }
        var newRows = 0;
        foreach (var (equip, qty) in materials)
        {
            if (ReadEquipMaterial(c, t, account, equip) > inventoryMaterialConfig.QuantityCap - qty) throw new GachaCapacityException();
            using var q = Command(c, t, "SELECT COUNT(*) FROM owned_equip_materials WHERE account_id=$id AND equip_id=$equip");
            q.Parameters.AddWithValue("$id", account); q.Parameters.AddWithValue("$equip", equip);
            if ((long)q.ExecuteScalar()! == 0) newRows++;
        }
        EnsureInstanceCapacity(c, t, "owned_equip_materials", newRows);
    }

    private Dictionary<string, object?> CreditEquipMaterial(SqliteConnection c, SqliteTransaction t, long account, int equipId, int qty)
    {
        if (qty <= 0) throw new GachaCapacityException();
        var definition = inventoryMaterialConfig.Find(equipId) ?? throw new GachaCapacityException();
        var before = ReadEquipMaterial(c, t, account, equipId);
        if (before > inventoryMaterialConfig.QuantityCap - (long)qty) throw new GachaCapacityException();
        int acquired;
        using (var history = Command(c, t, "INSERT OR IGNORE INTO acquired_equip_groups(account_id,equip_group_id) VALUES($id,$group)"))
        {
            history.Parameters.AddWithValue("$id", account); history.Parameters.AddWithValue("$group", definition.EquipGroupId);
            acquired = history.ExecuteNonQuery();
        }
        // UPDATE first: repeated stacks keep their ID and do not burn sequence
        // values through an AUTOINCREMENT INSERT ON CONFLICT branch.
        using var update = Command(c, t, "UPDATE owned_equip_materials SET qty=qty+$qty WHERE account_id=$id AND equip_id=$equip");
        update.Parameters.AddWithValue("$id", account); update.Parameters.AddWithValue("$equip", equipId); update.Parameters.AddWithValue("$qty", qty);
        if (update.ExecuteNonQuery() == 0)
        {
            EnsureInstanceCapacity(c, t, "owned_equip_materials", 1);
            using var insert = Command(c, t, "INSERT INTO owned_equip_materials(account_id,equip_id,qty) VALUES($id,$equip,$qty)");
            insert.Parameters.AddWithValue("$id", account); insert.Parameters.AddWithValue("$equip", equipId); insert.Parameters.AddWithValue("$qty", qty); insert.ExecuteNonQuery();
        }
        // This is a stack, not a duplicate-card conversion. before remains null;
        // no ordinary user_equip_id is fabricated in the draw summary.
        var result = Lot(7, equipId, acquired == 1); result["item_qty"] = qty; return result;
    }
}

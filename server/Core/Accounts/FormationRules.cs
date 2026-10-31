namespace Brs.Local.Accounts;

internal sealed record OwnedFormationCard(int CardId, int CharaId, int AwakeningCount);
internal sealed record OwnedFormationEquip(int UserEquipId, int EquipId, bool IsDeleted);

internal static class FormationRules
{
    internal static bool ValidateDeck(FormationConfig config, DeckSlot[] slots,
        IReadOnlyDictionary<int, OwnedFormationCard> cards, IReadOnlyDictionary<int, OwnedFormationEquip> equips)
    {
        if (slots.Length != 6 || slots[0].CardId <= 0) return false;
        var seenCards = new HashSet<int>(); var seenEquips = new HashSet<int>();
        foreach (var slot in slots)
        {
            if (slot.EquipIds.Length != 3 || slot.CardId < 0 || slot.EquipIds.Any(id => id < 0)) return false;
            if (slot.CardId == 0) { if (slot.EquipIds.Any(id => id != 0)) return false; continue; }
            if (!seenCards.Add(slot.CardId) || !cards.TryGetValue(slot.CardId, out var owned)) return false;
            var card = config.Cards.SingleOrDefault(row => row.CardId == slot.CardId && !row.IsDeleted);
            if (card is null || card.CharaId != owned.CharaId) return false;
            // SameChara/differentCard is expressly legal in decks. The support
            // slot also persists own inventory; borrowed battle support is separate.
            for (var position = 0; position < 3; position++)
            {
                var id = slot.EquipIds[position]; if (id == 0) continue;
                if (!seenEquips.Add(id) || !equips.TryGetValue(id, out var held) || held.IsDeleted) return false;
                var equip = config.Equips.SingleOrDefault(row => row.EquipId == held.EquipId && !row.IsDeleted);
                if (equip is null) return false;
                var usage = equip.EquipType switch { 201 => 2, 301 => 3, 302 => 4, _ => 1 };
                if (position == 0 ? usage != 1 || equip.EquipType != card.EquipType : usage != 2) return false;
                if (position == 2 && !config.Awakening.Any(rule => rule.Rarity == card.Rarity &&
                    rule.AwakeningCount == owned.AwakeningCount && !rule.IsDeleted && rule.Sub2Unlocked)) return false;
            }
        }
        return true;
    }

    internal static bool ValidateHome(FormationConfig config, int[] ids, IReadOnlyDictionary<int, OwnedFormationCard> cards)
    {
        if (ids.Length is < 1 or > 6 || ids.Any(id => id <= 0) || ids.Distinct().Count() != ids.Length) return false;
        var charas = new HashSet<int>();
        foreach (var id in ids)
        {
            if (!cards.TryGetValue(id, out var owned)) return false;
            var card = config.Cards.SingleOrDefault(row => row.CardId == id && !row.IsDeleted);
            if (card is null || card.CharaId != owned.CharaId || !charas.Add(owned.CharaId)) return false;
        }
        return true;
    }
}

namespace Brs.Local.Accounts;

internal static class GachaWeights
{
    internal static T Pick<T>(T[] rows, Func<T, int> weight, Func<int, int> nextBelow)
    {
        var total = rows.Sum(weight);
        var choice = nextBelow(total);
        if (choice < 0 || choice >= total) throw new InvalidDataException("RNG returned outside its integer range.");
        foreach (var row in rows)
        {
            choice -= weight(row);
            if (choice < 0) return row;
        }
        throw new InvalidDataException("Invalid integer probability range.");
    }
}

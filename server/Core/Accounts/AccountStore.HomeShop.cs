using Microsoft.Data.Sqlite;

namespace Brs.Local.Accounts;

public sealed partial class AccountStore
{
    public AccountResult ListHomeShopState(HomeShopListRequest request)
    {
        if (string.IsNullOrEmpty(request.AuthToken)) return new(401);
        lock (gate)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder(connectionString)
            { Mode = SqliteOpenMode.ReadOnly }.ToString());
            connection.Open();
            using var transaction = connection.BeginTransaction(deferred: true);
            if (!TryReadAuthenticatedAccount(connection, transaction, request.AuthToken,
                Now, out var id, out _)) return new(401);
            // Product/price metadata remains in MasterData; publish only actual
            // user purchase counts. Original ShopTriggerMaster is empty.
            var action = new Dictionary<string, object?>
            {
                ["u_shop"] = ShopRows(connection, transaction, id, Now),
                ["u_shop_trigger"] = Array.Empty<object>()
            };
            int? badge = vipPassConfig is not null ? VipPendingCount(connection, transaction, id) : null;
            transaction.Commit();
            return new(200, action, PresentItemBadge: badge);
        }
    }
}

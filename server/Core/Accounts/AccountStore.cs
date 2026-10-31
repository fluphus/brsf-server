using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Brs.Local.Accounts;

public sealed record AccountResult(int Status, Dictionary<string, object?>? Action = null, object[]? SendPresentBoxItems = null, int? PresentItemBadge = null, int? MissionBadge = null, int? PresentEquipBadge = null, int? BeginnerBadge = null, int? NewFollowerBadge = null, bool IsSendPresentBox = false, bool? BeginnerClearBadge = null,int ProtocolErrorId=0,string ProtocolErrorMessage="");

// Explicit local account policy; immutable content remains outside this database.
public sealed partial class AccountStore
{
    public const int NonceLifetimeSeconds = 600;
    public const int MaximumActiveNonces = 1024;
    private const int ApplicationId = 0x42525346; // BRSF
    public const int SessionLifetimeSeconds = 86400;
    public const int MaximumSessionsPerAccount = 8;
    public const int AccountSchemaVersion = 24;
    private const int SchemaVersion = AccountSchemaVersion;
    private const string TableSql = "CREATE TABLE nonce_issues (nonce_hash BLOB NOT NULL PRIMARY KEY CHECK(typeof(nonce_hash) = 'blob' AND length(nonce_hash) = 32), issued_at INTEGER NOT NULL CHECK(issued_at > 0), expires_at INTEGER NOT NULL CHECK(expires_at = issued_at + 600)) STRICT";
    private const string IndexSql = "CREATE INDEX ix_nonce_issues_expires_at ON nonce_issues(expires_at)";
    private static readonly Dictionary<string, string> AccountSchema = new(StringComparer.Ordinal)
    {
        ["accounts"] = "CREATE TABLE accounts (account_id INTEGER PRIMARY KEY AUTOINCREMENT CHECK(account_id BETWEEN 1 AND 2147483647), uuid TEXT NOT NULL UNIQUE CHECK(length(uuid) = 36), player_json TEXT NOT NULL CHECK(json_valid(player_json)), created_at INTEGER NOT NULL CHECK(created_at > 0)) STRICT",
        ["creation_receipts"] = "CREATE TABLE creation_receipts (nonce_hash BLOB NOT NULL PRIMARY KEY CHECK(typeof(nonce_hash) = 'blob' AND length(nonce_hash) = 32), fingerprint BLOB NOT NULL CHECK(typeof(fingerprint) = 'blob' AND length(fingerprint) = 32), account_id INTEGER NOT NULL UNIQUE REFERENCES accounts(account_id), created_at INTEGER NOT NULL CHECK(created_at > 0)) STRICT",
        ["sessions"] = "CREATE TABLE sessions (session_id INTEGER PRIMARY KEY AUTOINCREMENT, token_hash BLOB NOT NULL UNIQUE CHECK(typeof(token_hash) = 'blob' AND length(token_hash) = 32), account_id INTEGER NOT NULL REFERENCES accounts(account_id), issued_at INTEGER NOT NULL CHECK(issued_at > 0), expires_at INTEGER NOT NULL CHECK(expires_at = issued_at + 86400)) STRICT",
        ["ix_sessions_account"] = "CREATE INDEX ix_sessions_account ON sessions(account_id, session_id)"
    };
    private readonly string connectionString;
    private readonly object gate = new();
    private readonly bool compactBotPublicIds;
    public string BotPublicIdMode=>compactBotPublicIds?"compact":"legacy";
    private readonly TutorialGachaConfig? gachaConfig;
    private readonly StandardGachaConfig? standardGachaConfig;
    private readonly LocalPurchaseConfig? localPurchaseConfig;
    private readonly TicketGachaConfig? ticketGachaConfig;
    private readonly VipPassConfig? vipPassConfig;
    private readonly FormationConfig? formationConfig;
    private readonly SkinShopConfig? skinShopConfig;
    private readonly CardSkinConfig? cardSkinConfig;
    private readonly AllGachaConfig? allGachaConfig;
    private readonly CommerceConfig? commerceConfig;
    private readonly InventoryMaterialConfig inventoryMaterialConfig;
    private readonly MainlineConfig? mainlineConfig;
    private readonly ProfileConfig? profileConfig;
    private readonly ActivityConfig? activityConfig;
    private readonly EventQuestConfig? eventQuestConfig;
    private readonly TimeProvider timeProvider;
    private long Now => timeProvider.GetUtcNow().ToUnixTimeSeconds();
    private readonly Func<int, int>? nextGachaBelow;
    public string SqliteVersion { get; }

    public AccountStore(string path, bool migrateSchema1 = false, TutorialGachaConfig? tutorialGacha = null, Func<int, int>? gachaRandom = null,
        StandardGachaConfig? standardGacha = null, LocalPurchaseConfig? localPurchase = null, TicketGachaConfig? ticketGacha = null,
        VipPassConfig? vipPass = null, TimeProvider? timeProvider = null, FormationConfig? formation = null, SkinShopConfig? skinShop = null, CardSkinConfig? cardSkin = null, AllGachaConfig? allGacha = null, CommerceConfig? commerce = null, InventoryMaterialConfig? inventoryMaterial = null, MainlineConfig? mainline = null, ProfileConfig? profile = null, ActivityConfig? activity = null, EventQuestConfig? eventQuest = null, QuestModesConfig? questModes = null)
    {
        gachaConfig = tutorialGacha;
        standardGachaConfig = standardGacha;
        localPurchaseConfig = localPurchase;
        ticketGachaConfig = ticketGacha;
        vipPassConfig = vipPass;
        formationConfig = formation;
        skinShopConfig = skinShop;
        cardSkinConfig = cardSkin;
        allGachaConfig = allGacha;
        commerceConfig = commerce;
        inventoryMaterialConfig = inventoryMaterial ?? throw new InvalidDataException("Schema12 requires the original complete material inventory configuration.");
        mainlineConfig = mainline;
        profileConfig = profile;
        activityConfig = activity; eventQuestConfig = eventQuest; questModesConfig = questModes;
        if(questModes is not null && (eventQuest is null || mainline is null || activity is null || formation is null || vipPass is null)) throw new InvalidDataException("Quest modes require complete proven catalogs.");
        if (eventQuest is not null && (activity is null || mainline is null || allGacha is null || commerce is null)) throw new InvalidDataException("Event quests require complete proven catalogs.");
        this.timeProvider = timeProvider ?? TimeProvider.System;
        nextGachaBelow = gachaRandom;
        if (string.IsNullOrWhiteSpace(path) || path == ":memory:")
            throw new InvalidDataException("Explicit persistent account database path required.");
        var fullPath = Path.GetFullPath(path);
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(fullPath)!); directory is not null; directory = directory.Parent)
            if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Account database parent must exist without reparse points.");
        connectionString = ConnectionString(fullPath, SqliteOpenMode.ReadWrite);
        if (File.Exists(fullPath))
        {
            if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Account database cannot be a reparse point.");
            // Reject foreign files before invoking SQLite, including foreign WAL
            // databases whose read-open could otherwise create shared-memory files.
            Span<byte> header = stackalloc byte[100];
            int version;
            using (var source = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                source.ReadExactly(header);
                if (!header[..16].SequenceEqual("SQLite format 3\0"u8) ||
                    BinaryPrimitives.ReadInt32BigEndian(header[68..72]) != ApplicationId)
                    throw new InvalidDataException("Unsupported account database identity.");
                version = BinaryPrimitives.ReadInt32BigEndian(header[60..64]);
                if (version != SchemaVersion && version != 22 && version != 23 && !(version is >= 1 and <= 21 && migrateSchema1))
                    throw new InvalidDataException("Unsupported account database version; explicit migration required.");
            }
            using (var readOnly = new SqliteConnection(ConnectionString(fullPath, SqliteOpenMode.ReadOnly)))
            {
                readOnly.Open();
                ValidateSchema(readOnly, version);
            }
            if (version is >= 1 and <= 23)
            {
                using var migration = Open();
                using var transaction = migration.BeginTransaction(deferred: false);
                // Recheck under the write lock: never migrate an unvalidated or
                // concurrently modified schema. Caller must back up beforehand.
                ValidateSchema(migration, version, transaction);
                EnsureNoLegacyMaterialRows(migration, transaction, version);
                if (version <= 3)
                {
                    if (version == 1) Execute(migration, transaction, string.Join(';', AccountSchema.Values) + ";");
                    if (version == 3)
                        Execute(migration, transaction, "ALTER TABLE tutorial_receipts RENAME TO tutorial_receipts_v3;" + TutorialReceiptSql + "; INSERT INTO tutorial_receipts SELECT account_id,retry_hash,route,semantic,result_step,created_at FROM tutorial_receipts_v3; DROP TABLE tutorial_receipts_v3;");
                    else Execute(migration, transaction, TutorialReceiptSql + ";");
                    Execute(migration, transaction, string.Join(';', InventorySchema.Values) + ";");
                }
                if (version <= 4) MigrateEconomySchema(migration, transaction);
                if (version <= 5) MigrateConsumeSchema(migration, transaction);
                if (version <= 6) MigrateVipSchema(migration, transaction);
                if (version <= 7) MigrateFormationSchema(migration, transaction);
                if (version <= 8) MigrateSkinShopSchema(migration, transaction);
                if (version <= 9) MigrateShopCatalogSchema(migration, transaction);
                if (version <= 10) MigrateAllGachaSchema(migration, transaction);
                if (version <= 11) MigrateInventoryMaterialSchema(migration, transaction);
                if (version <= 12)
                    Execute(migration, transaction, string.Join(';', MainlineSchema.Values) + ";" + ActiveQuestRunIndexSql + "; PRAGMA user_version=13;");
                if (version <= 13) Execute(migration, transaction, UserProfileSql + "; PRAGMA user_version=14;");
                if(version<=14) ActivitySchema.CreateAndAnchor(migration, transaction, Now);
                if(version<=15)
                {
                    Execute(migration, transaction, string.Join(';',QuestModesSchema.Values)+";");
                    MiscSchema.Create(migration, transaction);
                }
                if(version<=16) ExpeditionSchema.Create(migration,transaction);
                if(version<=17)Schema18.Create(migration,transaction);
                if(version<=18)Schema19.Create(migration,transaction);
                if(version<=19)
                {
                    IdentitySchema.Create(migration,transaction,legacy:true);
                    TakeoverSchema.Create(migration,transaction);
                }
                if(version<=20)DeleteSchema.Create(migration,transaction);
                if(version<=21) ManagementSchema.Create(migration,transaction);
                if(version<=22) Execute(migration,transaction,ExpertPeriodSchemaSql+";");
                else MigrateExpertContentPolicy(migration,transaction);
                Execute(migration, transaction, "PRAGMA user_version=24;");
                transaction.Commit();
                ValidateSchema(migration, SchemaVersion);
            }
        }
        else
        {
            // Claim exactly a new file. Never replace a preexisting database/file.
            using (new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            using var created = Open();
            using var transaction = created.BeginTransaction(deferred: false);
            Execute(created, transaction, $"PRAGMA application_id = {ApplicationId}; PRAGMA user_version = {SchemaVersion}; {TableSql}; {IndexSql};" + string.Join(';', AccountSchema.Values) + ";" + TutorialReceiptSql + ";" + string.Join(';', AllGachaInventorySchema.Values) + ";" + EconomyTransactionSql + ";" + CommerceOwnedShopSql + ";" + string.Join(';', CommerceInventorySchema.Values) + ";" + GenericOwnedConsumeSql + ";" + GachaExchangeSql + ";" + VipEntitlementSql + ";" + VipPresentSql + ";" + VipDailySql + ";" + DeckHeaderSql + ";" + DeckMainIndexSql + ";" + DeckSlotSql + ";" + DeckEquipSql + ";" + AllOpenHomePreferenceSql + ";" + OwnedEquipMaterialSql + ";");
            Execute(created, transaction, string.Join(';', MainlineSchema.Values) + ";" + ActiveQuestRunIndexSql + ";");
            Execute(created, transaction, UserProfileSql + ";");
            ActivitySchema.CreateAndAnchor(created, transaction, Now);
            Execute(created, transaction, string.Join(';',QuestModesSchema.Values)+";");
            MiscSchema.Create(created, transaction);
            ExpeditionSchema.Create(created,transaction);
            Schema18.Create(created,transaction);
            Schema19.Create(created,transaction);
            IdentitySchema.Create(created,transaction,legacy:false);
            TakeoverSchema.Create(created,transaction);
            DeleteSchema.Create(created,transaction);
            ManagementSchema.Create(created,transaction);
            Execute(created,transaction,ExpertPeriodSchemaSql+";");
            transaction.Commit();
            ValidateSchema(created, SchemaVersion);
        }
        using var verified = new SqliteConnection(ConnectionString(fullPath, SqliteOpenMode.ReadOnly));
        verified.Open();
        using var engineVersion = verified.CreateCommand();
        engineVersion.CommandText = "SELECT sqlite_version()";
        SqliteVersion = (string)engineVersion.ExecuteScalar()!;
        ValidateLocalBotIdentities(verified);
        IdentitySchema.Validate(verified);
        compactBotPublicIds=IdentitySchema.Read(verified).Compact;
    }

    private static string ConnectionString(string path, SqliteOpenMode mode) => new SqliteConnectionStringBuilder
    {
        DataSource = path, Mode = mode, Pooling = false, DefaultTimeout = 5, ForeignKeys = true
    }.ToString();

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    private static void ValidateSchema(SqliteConnection connection, int version, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version";
        if ((long)command.ExecuteScalar()! != version) throw new InvalidDataException("Account database version changed.");
        command.CommandText = "SELECT name, sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY name";
        using (var reader = command.ExecuteReader())
        {
            var expected = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["nonce_issues"] = TableSql, ["ix_nonce_issues_expires_at"] = IndexSql
            };
            if (version >= 2)
                foreach (var pair in AccountSchema) expected.Add(pair.Key, pair.Value);
            if (version >= 3) expected.Add("tutorial_receipts", version == 3 ? LegacyTutorialReceiptSql : TutorialReceiptSql);
            if (version >= 4)
                foreach (var pair in version == 4 ? InventorySchema : version == 5 ? EconomyInventorySchema : version <= 8 ? LegacyCurrentInventorySchema : version <= 10 ? CurrentInventorySchema : AllGachaInventorySchema) expected.Add(pair.Key, pair.Value);
            if (version >= 5)
            {
                expected.Add("economy_transactions", EconomyTransactionSql);
                expected.Add("owned_shop_counts", version <= 6 ? LegacyOwnedShopCountSql : version <= 8 ? VipOwnedShopCountSql : version == 9 ? SingleSkinOwnedShopCountSql : version <= 10 ? OwnedShopCountSql : CommerceOwnedShopSql);
            }
            if (version >= 6) expected.Add("owned_consumes", version <= 10 ? OwnedConsumeSql : GenericOwnedConsumeSql);
            if (version >= 12) expected.Add("owned_equip_materials", OwnedEquipMaterialSql);
            if (version >= 13)
            {
                foreach (var pair in MainlineSchema) expected.Add(pair.Key, pair.Value);
                expected.Add("ix_quest_runs_active_account", ActiveQuestRunIndexSql);
            }
            if (version >= 14) expected.Add("user_profiles", UserProfileSql);
            if (version >= 15) foreach (var pair in ActivitySchema.Tables) expected.Add(pair.Key, pair.Value);
            if(version>=16) { foreach(var pair in QuestModesSchema) expected.Add(pair.Key,pair.Value); foreach(var pair in MiscSchema.Tables) expected.Add(pair.Key,pair.Value); }
            if(version>=17)foreach(var pair in ExpeditionSchema.Tables)expected.Add(pair.Key,pair.Value);
            if(version>=18)foreach(var pair in Schema18.Objects)expected.Add(pair.Key,pair.Value);
            if(version>=19)expected.Add("local_bot_peer_relations",Schema19.PeerSql);
            if(version>=20)expected.Add("account_identity_policy",IdentitySchema.Sql);
            if(version>=20)foreach(var pair in TakeoverSchema.Objects)expected.Add(pair.Key,pair.Value);
            if(version>=21)expected.Add("account_deletion_journal",DeleteSchema.Sql);
            if(version>=22)foreach(var pair in ManagementSchema.Tables)expected.Add(pair.Key,pair.Value);
            if(version>=23)expected.Add("expert_period_settings",version>=24?ExpertPeriodSchemaSql:LegacyExpertPeriodSchemaSql);
            if (version >= 11)
            {
                expected.Add("user_gacha_exchanges", GachaExchangeSql);
                foreach (var pair in CommerceInventorySchema) expected.Add(pair.Key, pair.Value);
            }
            if (version >= 7)
            {
                expected.Add("vip_entitlements", VipEntitlementSql);
                expected.Add("vip_present_items", VipPresentSql);
                expected.Add("vip_daily_deliveries", VipDailySql);
            }
            if (version >= 8)
            {
                expected.Add("user_decks", DeckHeaderSql); expected.Add("ix_user_decks_main", DeckMainIndexSql);
                expected.Add("deck_slots", DeckSlotSql); expected.Add("deck_slot_equips", DeckEquipSql);
                expected.Add("home_preferences", version <= 10 ? HomePreferenceSql : AllOpenHomePreferenceSql);
            }
            while (reader.Read())
                if (!expected.Remove(reader.GetString(0), out var sql) || reader.IsDBNull(1) || reader.GetString(1) != sql)
                    throw new InvalidDataException("Unsupported account database schema.");
            if (expected.Count != 0) throw new InvalidDataException("Incomplete account database schema.");
        }
        command.CommandText = "PRAGMA quick_check";
        if ((string?)command.ExecuteScalar() != "ok") throw new InvalidDataException("Account database integrity check failed.");
        command.CommandText = "PRAGMA foreign_key_check";
        using (var reader = command.ExecuteReader())
            if (reader.Read()) throw new InvalidDataException("Account database foreign-key check failed.");
        if (version >= 2)
        {
            command.CommandText = "SELECT account_id, uuid, player_json FROM accounts";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!Guid.TryParseExact(reader.GetString(1), "D", out _) || reader.GetInt64(0) is < 1 or > int.MaxValue)
                    throw new InvalidDataException("Invalid persisted account identity.");
                using var player = System.Text.Json.JsonDocument.Parse(reader.GetString(2));
                if (player.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object ||
                    !player.RootElement.TryGetProperty("user_id", out var id) || id.ValueKind != System.Text.Json.JsonValueKind.Number || !id.TryGetInt32(out var userId) || userId != reader.GetInt64(0) ||
                    !player.RootElement.TryGetProperty("my_game_id", out var gameId) || gameId.ValueKind != System.Text.Json.JsonValueKind.Number || !gameId.TryGetUInt64(out var myGameId) || (version<20?myGameId != (ulong)userId:myGameId is 0 or >long.MaxValue))
                    throw new InvalidDataException("Invalid persisted player identity.");
                _ = TutorialProgression.ReadStep(player.RootElement);
            }
        }
        if(version>=20)IdentitySchema.Validate(connection,transaction);
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, long? now = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        if (now.HasValue) command.Parameters.AddWithValue("$now", now.Value);
        command.ExecuteNonQuery();
    }

    public string? IssueNonce()
    {
        lock (gate)
        {
            using var connection = Open();
            // Immediate SQLite transaction also serializes another service using
            // this same explicit DB; count+insert cannot exceed the capacity cap.
            using var transaction = connection.BeginTransaction(deferred: false);
            var issued = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Execute(connection, transaction, "DELETE FROM nonce_issues WHERE expires_at <= $now", issued);
            using var count = connection.CreateCommand();
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM nonce_issues";
            if ((long)count.ExecuteScalar()! >= MaximumActiveNonces)
            {
                transaction.Commit(); // Only already-expired rows were removed.
                return null;
            }
            var random = RandomNumberGenerator.GetBytes(32);
            try
            {
                var nonce = Convert.ToBase64String(random).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO nonce_issues(nonce_hash, issued_at, expires_at) VALUES($hash, $issued, $expires)";
                insert.Parameters.Add("$hash", SqliteType.Blob).Value = SHA256.HashData(Encoding.UTF8.GetBytes(nonce));
                insert.Parameters.AddWithValue("$issued", issued);
                insert.Parameters.AddWithValue("$expires", issued + NonceLifetimeSeconds);
                insert.ExecuteNonQuery();
                transaction.Commit();
                return nonce;
            }
            finally { CryptographicOperations.ZeroMemory(random); }
        }
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    public AccountResult Create(CreateAccountRequest request)
    {
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var nonceHash = Hash(request.Nonce);
            using (var receipt = Command(connection, transaction,
                "SELECT r.fingerprint, a.uuid, a.player_json, a.account_id FROM creation_receipts r JOIN accounts a ON a.account_id = r.account_id WHERE r.nonce_hash = $hash"))
            {
                receipt.Parameters.AddWithValue("$hash", nonceHash);
                using var reader = receipt.ExecuteReader();
                if (reader.Read())
                {
                    if (!CryptographicOperations.FixedTimeEquals((byte[])reader[0], request.Fingerprint)) return new(409);
                    var oldPlayer = reader.GetString(2); var oldUuid = reader.GetString(1); var oldId = reader.GetInt64(3);
                    reader.Close();
                    if(WasTakenOver(connection,transaction,oldId))return new(409);
                    return new(200, Snapshot(connection, transaction, oldId, oldPlayer, "uuid", oldUuid));
                }
            }
            using (var challenge = Command(connection, transaction, "SELECT expires_at FROM nonce_issues WHERE nonce_hash = $hash"))
            {
                challenge.Parameters.AddWithValue("$hash", nonceHash);
                if (challenge.ExecuteScalar() is not long expires || expires <= now) return new(409);
            }
            var created=CreateAccountInTransaction(connection,transaction,nonceHash,request.Fingerprint,now,consumeNonce:true);
            if(created is null)return new(503);
            var (accountId,uuid,player)=created.Value;
            var action = Snapshot(connection, transaction, accountId, player, "uuid", uuid);
            transaction.Commit();
            return new(200, action);
        }
    }

    // Shared atomic allocator. Welcome gifts wait for a completed tutorial's Home.
    private (long Account,string Uuid,string Player)? CreateAccountInTransaction(SqliteConnection connection,SqliteTransaction transaction,byte[] nonceHash,byte[] fingerprint,long now,bool consumeNonce)
    {
            // sqlite_sequence also prevents reusing a deleted high ID. Refuse
            // Int32 overflow explicitly before INSERT, rather than wrap a wire ID.
            using (var sequence = Command(connection, transaction,
                "SELECT MAX(value) FROM (SELECT COALESCE(MAX(account_id),0) AS value FROM accounts UNION ALL SELECT COALESCE(MAX(seq),0) FROM sqlite_sequence WHERE name='accounts')"))
                if ((long)sequence.ExecuteScalar()! >= int.MaxValue) return null;
            var publicId=IdentitySchema.Allocate(connection,transaction);
            if(publicId is null)return null;
            var uuid = Guid.NewGuid().ToString("D");
            long accountId;
            using (var insert = Command(connection, transaction,
                "INSERT INTO accounts(uuid,player_json,created_at) VALUES($uuid,'{}',$now) RETURNING account_id"))
            {
                insert.Parameters.AddWithValue("$uuid", uuid);
                insert.Parameters.AddWithValue("$now", now);
                accountId = (long)insert.ExecuteScalar()!;
            }
            if(accountId is >=LocalBotCatalog.ReservedMin and <=LocalBotCatalog.ReservedMax)return null;
            var player = PlayerState.NewPlayer(checked((int)accountId), now,checked((ulong)publicId.Value));
            using (var update = Command(connection, transaction, "UPDATE accounts SET player_json = $player WHERE account_id = $id"))
            {
                update.Parameters.AddWithValue("$player", player);
                update.Parameters.AddWithValue("$id", accountId);
                update.ExecuteNonQuery();
            }
            using (var receipt = Command(connection, transaction,
                "INSERT INTO creation_receipts(nonce_hash,fingerprint,account_id,created_at) VALUES($hash,$fingerprint,$id,$now)"))
            {
                receipt.Parameters.AddWithValue("$hash", nonceHash);
                receipt.Parameters.AddWithValue("$fingerprint", fingerprint);
                receipt.Parameters.AddWithValue("$id", accountId);
                receipt.Parameters.AddWithValue("$now", now);
                receipt.ExecuteNonQuery();
            }
            if(consumeNonce)using (var consume = Command(connection, transaction, "DELETE FROM nonce_issues WHERE nonce_hash = $hash"))
            {
                consume.Parameters.AddWithValue("$hash", nonceHash);
                if (consume.ExecuteNonQuery() != 1) throw new InvalidDataException("Nonce consumption failed.");
            }
            ActivitySchema.Anchor(connection, transaction, accountId, now);
            EnrollNewAccountGifts(connection,transaction,accountId,nonceHash,now);
        return(accountId,uuid,player);
    }

    public AccountResult Login(string uuid)
    {
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            long id;
            string player;
            using (var query = Command(connection, transaction, "SELECT account_id,player_json FROM accounts WHERE uuid = $uuid"))
            {
                query.Parameters.AddWithValue("$uuid", uuid);
                using var reader = query.ExecuteReader();
                if (!reader.Read()) return new(401);
                id = reader.GetInt64(0);
                player = reader.GetString(1);
            }
            var now = Now;
            if(IsManagementLeased(id))return new(409);
            Execute(connection, transaction, "DELETE FROM sessions WHERE expires_at <= $now", now);
            var random = RandomNumberGenerator.GetBytes(32);
            try
            {
                var token = Convert.ToBase64String(random).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                using (var insert = Command(connection, transaction,
                    "INSERT INTO sessions(token_hash,account_id,issued_at,expires_at) VALUES($hash,$id,$now,$expires)"))
                {
                    insert.Parameters.AddWithValue("$hash", Hash(token));
                    insert.Parameters.AddWithValue("$id", id);
                    insert.Parameters.AddWithValue("$now", now);
                    insert.Parameters.AddWithValue("$expires", now + SessionLifetimeSeconds);
                    insert.ExecuteNonQuery();
                }
                using (var prune = Command(connection, transaction,
                    "DELETE FROM sessions WHERE account_id = $id AND session_id NOT IN (SELECT session_id FROM sessions WHERE account_id = $id ORDER BY session_id DESC LIMIT 8)"))
                {
                    prune.Parameters.AddWithValue("$id", id);
                    prune.ExecuteNonQuery();
                }
                var bonuses=ObserveBeginnerLogin(connection,transaction,id,player,now,"user/login");
                ObserveAwakeningVoiceEntitlements(connection,transaction,id,now,"user/login");
                if (activityConfig is not null) ActivitySchema.RecordSuccessfulLogin(connection, transaction, id, now);
                ObserveActivityDailyClear(connection,transaction,id,player,now);
                var action = Snapshot(connection, transaction, id, player, "auth_token", token, nowOverride: now);
                action["login_bonus"]=bonuses.Notices;
                int? presentBadge = vipPassConfig is not null ? VipPendingCount(connection, transaction, id) : null;
                transaction.Commit();
                return new(200, action, SendPresentBoxItems:bonuses.NotificationItems,IsSendPresentBox:bonuses.IsSendPresentBox,PresentItemBadge: presentBadge);
            }
            catch (GachaCapacityException) { return new(503); }
            catch(Exception ex)when((beginnerConfig is not null||miscCatalog is not null)&&ex is SqliteException or OverflowException or InvalidDataException){return new(503);}
            finally { CryptographicOperations.ZeroMemory(random); }
        }
    }
}

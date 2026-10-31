using System.Collections.ObjectModel;
using Brs.Local.Accounts;
using Brs.Local.Bootstrap;
using Brs.Local.Content;
using Brs.Local.Management;
using Brs.Local.Transport;
using Brs.Portable.Game;

namespace Brs.Portable.Runtime;

// Explicit composition only. Caller owns the injected cipher and every transport/lifetime decision.
public sealed class PortableRuntime : IAsyncDisposable
{
    public RuntimeInputs Inputs { get; }
    public AccountStore Accounts { get; }
    public GameCatalogInputs Catalogs { get; }
    public IReadOnlyDictionary<string, object> LoadedRules { get; }
    public InventoryMaterialConfig InventoryMaterial { get; }
    public BootstrapSnapshot Bootstrap { get; }
    public LocalContentOverlay Overlay { get; }
    public GameApiDispatcher GameApi { get; }
    public ManagementHost Management { get; }

    internal PortableRuntime(RuntimeInputs inputs, AccountStore accounts, GameCatalogInputs catalogs, InventoryMaterialConfig inventory,
        BootstrapSnapshot bootstrap, LocalContentOverlay overlay, GameApiDispatcher game, ManagementHost management,
        IDictionary<string, object> loadedRules)
    {
        Inputs = inputs; Accounts = accounts; Catalogs = catalogs; InventoryMaterial = inventory;
        Bootstrap = bootstrap; Overlay = overlay; GameApi = game; Management = management;
        LoadedRules = new ReadOnlyDictionary<string, object>(loadedRules);
    }
    public ValueTask DisposeAsync() => Management.DisposeAsync();
}

public static class PortableRuntimeFactory
{
    public static PortableRuntime Create(string runtimeRoot, string databasePath, PacketCipher cipher, byte[] ownerHash, int managementPort)
    {
        ArgumentNullException.ThrowIfNull(cipher); ArgumentNullException.ThrowIfNull(ownerHash);
        if (ownerHash.Length != 32 || managementPort is < 1024 or > 65535) throw new InvalidDataException("Invalid runtime management configuration.");
        var inputs = RuntimeInputs.LoadAndVerify(runtimeRoot);
        // Original Program: validate management resources, then overlay/bootstrap and rule objects before opening SQLite.
        var assets = ManagementAssets.LoadBundled();
        var labels = ManagementAssets.LoadLabels();
        string P(string name) => inputs.PathFor("--" + name + "-config");
        var originalCatalog = inputs.PathFor("--catalog");
        var localContent = LocalContentOverlay.Load(inputs.PathFor("--local-content-manifest"), originalCatalog);
        var bootstrap = BootstrapSnapshot.Load(P("bootstrap"), originalCatalog, localContent);
        var tutorialGacha = TutorialGachaConfig.Load(P("tutorial-gacha"));
        var inventoryMaterial = InventoryMaterialConfig.Load(P("inventory-material"));
        var mainline = MainlineConfig.Load(P("mainline"));
        var profile = ProfileConfig.Load(P("profile"));
        var activity = ActivityConfig.Load(P("activity"));
        var eventQuest = EventQuestConfig.Load(P("event-quest"));
        var growth = new GrowthCatalog(P("growth"));
        var questModes = QuestModesConfig.Load(P("quest-modes"));
        var completeTutorial = new CompleteTutorialCatalog(P("complete-tutorial"));
        var beginner = BeginnerCatalog.Load(P("beginner"));
        var presents = new PresentCatalog(P("present"));
        var expedition = ExpeditionCatalog.Load(P("expedition"));
        var misc = MiscCatalog.Load(P("misc"));
        var commerce = CommerceConfig.Load(P("commerce"));
        var allGacha = AllGachaConfig.Load(P("all-gacha"));
        var standardGacha = StandardGachaConfig.Load(P("standard-gacha"));
        var localPurchase = LocalPurchaseConfig.Load(P("local-purchase"));
        var ticketGacha = TicketGachaConfig.Load(P("ticket-gacha"));
        var vipPass = VipPassConfig.Load(P("vip-pass"));
        var formation = FormationConfig.Load(P("formation"));
        var skinShop = SkinShopConfig.Load(P("skin-shop"));
        var cardSkin = CardSkinConfig.Load(P("card-skin"));
        var accounts = new AccountStore(databasePath, false, tutorialGacha,
            standardGacha: standardGacha, localPurchase: localPurchase, ticketGacha: ticketGacha, vipPass: vipPass,
            formation: formation, skinShop: skinShop, cardSkin: cardSkin, allGacha: allGacha, commerce: commerce,
            inventoryMaterial: inventoryMaterial, mainline: mainline, profile: profile, activity: activity,
            eventQuest: eventQuest, questModes: questModes);
        accounts.ConfigureQuestLoops(growth);
        accounts.ConfigurePresents(presents);
        accounts.ConfigureBeginner(beginner);
        accounts.ConfigureCompleteTutorial(completeTutorial, growth);
        accounts.ConfigureMisc(misc);
        if (localContent.ActiveExpertQuestTypeId is { } activeExpert)
        {
            accounts.ConfigureExpertAvailability(activeExpert, localContent.Fingerprint);
            localContent.ConfigureDynamicExpertContent(accounts);
        }
        accounts.ConfigureExpedition(expedition, growth);
        accounts.ConfigureManagement(growth, labels);
        var catalogs = new GameCatalogInputs
        {
            TutorialGacha = tutorialGacha, StandardGacha = standardGacha, TicketGacha = ticketGacha,
            LocalPurchase = localPurchase, VipPass = vipPass, Formation = formation, SkinShop = skinShop,
            CardSkin = cardSkin, AllGacha = allGacha, Commerce = commerce, Profile = profile, Mainline = mainline,
            Activity = activity, EventQuest = eventQuest, Growth = growth, QuestModes = questModes, Misc = misc,
            Expedition = expedition, Presents = presents, Beginner = beginner, CompleteTutorial = completeTutorial
        };
        var rules = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["--tutorial-gacha-config"] = tutorialGacha, ["--inventory-material-config"] = inventoryMaterial,
            ["--mainline-config"] = mainline, ["--profile-config"] = profile, ["--activity-config"] = activity,
            ["--event-quest-config"] = eventQuest, ["--growth-config"] = growth, ["--quest-modes-config"] = questModes,
            ["--complete-tutorial-config"] = completeTutorial, ["--beginner-config"] = beginner, ["--present-config"] = presents,
            ["--expedition-config"] = expedition, ["--misc-config"] = misc, ["--commerce-config"] = commerce,
            ["--all-gacha-config"] = allGacha, ["--standard-gacha-config"] = standardGacha,
            ["--local-purchase-config"] = localPurchase, ["--ticket-gacha-config"] = ticketGacha,
            ["--vip-pass-config"] = vipPass, ["--formation-config"] = formation, ["--skin-shop-config"] = skinShop,
            ["--card-skin-config"] = cardSkin
        };
        var game = new GameApiDispatcher(accounts, cipher, bootstrap, catalogs);
        var management = new ManagementHost(new ManagementHostOptions(managementPort, "unused-not-read"), new AccountStoreManagementBackend(accounts), assets, ownerHash);
        return new PortableRuntime(inputs, accounts, catalogs, inventoryMaterial, bootstrap, localContent, game, management, rules);
    }
}

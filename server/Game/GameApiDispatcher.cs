using System.Text.Json;
using Brs.Local.Accounts;
using Brs.Local.Bootstrap;
using Brs.Local.Transport;
using Brs.Portable.Shared;

namespace Brs.Portable.Game;

public sealed partial class GameApiDispatcher : IHostDispatcher
{
    private readonly AccountStore? accounts;
    private readonly PacketCipher cipher;
    private readonly BootstrapSnapshot? bootstrap;
    private readonly TextWriter log;
    private readonly TutorialGachaConfig? tutorialGacha;
    private readonly StandardGachaConfig? standardGacha;
    private readonly TicketGachaConfig? ticketGacha;
    private readonly LocalPurchaseConfig? localPurchase;
    private readonly VipPassConfig? vipPass;
    private readonly FormationConfig? formation;
    private readonly SkinShopConfig? skinShop;
    private readonly CardSkinConfig? cardSkin;
    private readonly AllGachaConfig? allGacha;
    private readonly CommerceConfig? commerce;
    private readonly ProfileConfig? profile;
    private readonly MainlineConfig? mainline;
    private readonly ActivityConfig? activity;
    private readonly EventQuestConfig? eventQuest;
    private readonly GrowthCatalog? growth;
    private readonly QuestModesConfig? questModes;
    private readonly MiscCatalog? misc;
    private readonly ExpeditionCatalog? expedition;
    private readonly PresentCatalog? presents;
    private readonly BeginnerCatalog? beginner;
    private readonly CompleteTutorialCatalog? completeTutorial;
    public GameCatalogAvailability Availability { get; }

    public GameApiDispatcher(AccountStore? accounts, PacketCipher cipher, BootstrapSnapshot? bootstrap,
        GameCatalogInputs catalogs, GameCatalogAvailability? expectedAvailability = null, TextWriter? log = null)
    {
        ArgumentNullException.ThrowIfNull(cipher); ArgumentNullException.ThrowIfNull(catalogs);
        Availability = GameCatalogAvailability.FromInputs(catalogs);
        if (expectedAvailability is not null && expectedAvailability != Availability)
            throw new InvalidDataException("Catalog availability must derive from actual original objects.");
        if (accounts is not null && bootstrap is null) throw new InvalidDataException("Accounts require the original bootstrap.");
        this.accounts = accounts; this.cipher = cipher; this.bootstrap = bootstrap; this.log = log ?? TextWriter.Null;
        tutorialGacha = catalogs.TutorialGacha; standardGacha = catalogs.StandardGacha; ticketGacha = catalogs.TicketGacha;
        localPurchase = catalogs.LocalPurchase; vipPass = catalogs.VipPass; formation = catalogs.Formation;
        skinShop = catalogs.SkinShop; cardSkin = catalogs.CardSkin; allGacha = catalogs.AllGacha;
        commerce = catalogs.Commerce; profile = catalogs.Profile; mainline = catalogs.Mainline;
        activity = catalogs.Activity; eventQuest = catalogs.EventQuest; growth = catalogs.Growth; questModes = catalogs.QuestModes;
        misc = catalogs.Misc; expedition = catalogs.Expedition; presents = catalogs.Presents;
        beginner = catalogs.Beginner; completeTutorial = catalogs.CompleteTutorial;
    }

    public Task<HostResponse> DispatchAsync(HostRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        // ASP.NET's Map("/api/{**route}") selects the endpoint case-insensitively;
        // the extracted original path/route comparisons remain untouched.
        if (!request.Path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(request.Path, "/api", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(HostResponse.Empty(404));
        string? route = request.Path.Length > 5 ? request.Path[5..] : null;
        var diagnostic = new RequestDiagnostics("/api", log);
        diagnostic.Mark("entry");
        var status = 500;
        try
        {
            var response = DispatchCore(request, route, diagnostic);
            status = response.StatusCode;
            return Task.FromResult(response);
        }
        catch (Exception exception)
        {
            // Intentional safe logging seam: no original server_stack/raw path.
            log.WriteLine(JsonSerializer.Serialize(new { kind = "request-exception", status = 500, exception_type = exception.GetType().FullName }));
            return Task.FromResult(HostResponse.Empty(500));
        }
        finally { diagnostic.Complete(status); }
    }

    private void Trace(string kind, string ignoredPath, int status) =>
        log.WriteLine(JsonSerializer.Serialize(new { kind, status }));

    private void TraceShopSafe(int status, LocalShopFailureStage stage, LocalShopFailureReason reason) =>
        log.WriteLine(JsonSerializer.Serialize(new { kind = "local-shop-failure", status,
            stage = LocalShopDiagnostics.Stage(stage), failureReason = LocalShopDiagnostics.Reason(reason) }));

    private static T ReadContract<T>(RequestDiagnostics diagnostic, Func<T> parse)
    {
        diagnostic.Mark("strict-contract");
        var value = parse();
        diagnostic.Mark("handler");
        return value;
    }
    private static HostResponse EncryptedResponse(byte[] bytes)
    {
        var response = HostResponse.Bytes(200, bytes, "application/octet-stream");
        response.SetHeader("Cache-Control", "no-store");
        return response;
    }
}

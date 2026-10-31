using Brs.Local.Accounts;

namespace Brs.Portable.Game;

// Composition must supply the same original objects used to configure AccountStore.
// No Boolean can independently enable a catalog whose original object is absent.
public sealed record GameCatalogInputs
{
    public TutorialGachaConfig? TutorialGacha { get; init; }
    public StandardGachaConfig? StandardGacha { get; init; }
    public TicketGachaConfig? TicketGacha { get; init; }
    public LocalPurchaseConfig? LocalPurchase { get; init; }
    public VipPassConfig? VipPass { get; init; }
    public FormationConfig? Formation { get; init; }
    public SkinShopConfig? SkinShop { get; init; }
    public CardSkinConfig? CardSkin { get; init; }
    public AllGachaConfig? AllGacha { get; init; }
    public CommerceConfig? Commerce { get; init; }
    public ProfileConfig? Profile { get; init; }
    public MainlineConfig? Mainline { get; init; }
    public ActivityConfig? Activity { get; init; }
    public EventQuestConfig? EventQuest { get; init; }
    public GrowthCatalog? Growth { get; init; }
    public QuestModesConfig? QuestModes { get; init; }
    public MiscCatalog? Misc { get; init; }
    public ExpeditionCatalog? Expedition { get; init; }
    public PresentCatalog? Presents { get; init; }
    public BeginnerCatalog? Beginner { get; init; }
    public CompleteTutorialCatalog? CompleteTutorial { get; init; }
}

public sealed record GameCatalogAvailability(bool TutorialGacha, bool StandardGacha, bool TicketGacha,
    bool LocalPurchase, bool VipPass, bool Formation, bool SkinShop, bool CardSkin, bool AllGacha,
    bool Commerce, bool Profile, bool Mainline, bool Activity, bool EventQuest, bool Growth, bool QuestModes,
    bool Misc, bool Expedition, bool Presents, bool Beginner, bool CompleteTutorial)
{
    public static GameCatalogAvailability FromInputs(GameCatalogInputs inputs) => new(
        inputs.TutorialGacha is not null, inputs.StandardGacha is not null, inputs.TicketGacha is not null,
        inputs.LocalPurchase is not null, inputs.VipPass is not null, inputs.Formation is not null,
        inputs.SkinShop is not null, inputs.CardSkin is not null, inputs.AllGacha is not null,
        inputs.Commerce is not null, inputs.Profile is not null, inputs.Mainline is not null,
        inputs.Activity is not null, inputs.EventQuest is not null, inputs.Growth is not null, inputs.QuestModes is not null,
        inputs.Misc is not null, inputs.Expedition is not null, inputs.Presents is not null,
        inputs.Beginner is not null, inputs.CompleteTutorial is not null);
}

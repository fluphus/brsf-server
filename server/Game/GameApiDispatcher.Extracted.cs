using System.Text.Json;
using Brs.Local.Accounts;
using Brs.Local.Bootstrap;
using Brs.Local.Transport;
using Brs.Portable.Shared;
using Microsoft.Data.Sqlite;

namespace Brs.Portable.Game;

public sealed partial class GameApiDispatcher
{
    private HostResponse DispatchCore(HostRequest request, string? route, RequestDiagnostics diagnostic)
    {
    var path = "/api/" + route;
    var versionCheck = request.Path == "/api/version_check";
    var blank = request.Path == "/api/blank" && bootstrap is not null;
    var userBlankRequest = route==UserBlankRequest.Route && accounts is not null;
    var nonceRequest = request.Path == "/api/user/nonce" && accounts is not null;
    var createRequest = request.Path == "/api/user/create" && accounts is not null;
    var loginRequest = request.Path == "/api/user/login" && accounts is not null;
    var homeRequest = request.Path == "/api/home" && accounts is not null;
    var friendPointPickupRequest = request.Path == "/api/home/pickup_friend_pt" && accounts is not null;
    var homeUpdateStepRequest = request.Path == "/api/home/update_step" && accounts is not null;
    var homeShopListRequest = request.Path == "/api/home/shop/list" && accounts is not null;
    var gachaTopRequest = request.Path == "/api/gacha/top" && accounts is not null;
    var gachaResultRequest = request.Path == "/api/gacha/result" && accounts is not null && (allGacha is not null || standardGacha is not null || ticketGacha is not null);
    var localShopRequest = request.Path is "/api/shop/list" or "/api/shop/birthday_entry" or "/api/shop/purchase_check" or "/api/shop/purchase"
        && accounts is not null && (localPurchase is not null || vipPass is not null || skinShop is not null || commerce is not null);
    var vipResetRequest = request.Path == "/api/user/vip_reset" && accounts is not null;
    var vipDataRequest = request.Path is "/api/present/item/list" or "/api/present/item/receive" or "/api/loginbonus/list"
        && accounts is not null && vipPass is not null && !(beginner is not null&&route=="loginbonus/list");
    var formationRequest = request.Path is "/api/deck/edit" or "/api/deck/name" or "/api/deck/main" or "/api/deck/reset"
        or "/api/profile/top" or "/api/present/equip/list" or "/api/home/customize" && accounts is not null && formation is not null;
    var cardSkinRequest = request.Path == "/api/card/change_skin" && accounts is not null && cardSkin is not null;
    var profileRequest = request.Path is "/api/story/name_change" or "/api/profile/name_change" or
        "/api/profile/message_change" or "/api/profile/card_change" or "/api/profile/birthday_set" or "/api/profile/title_set"
        && accounts is not null && profile is not null;
    var mainlineRequest = request.Path is "/api/quest/story" or "/api/quest/support" or "/api/quest/active_support" or
        "/api/quest/start" or "/api/quest/check" or "/api/quest/retire" or "/api/quest/result" or "/api/quest/loop_result" or "/api/mission/check/total_power"
        && accounts is not null && mainline is not null;
    var activityRequest = request.Path is "/api/event/point/top" or "/api/event/ornament/top" or "/api/mission/list" or "/api/mission/receive" && accounts is not null && activity is not null;
    var completeTutorialRequest=accounts is not null&&completeTutorial is not null&&CompleteTutorialRequest.Routes.Contains(route??"",StringComparer.Ordinal);
    var beginnerRequest=accounts is not null&&beginner is not null&&route is BeginnerTopRequest.Route or BeginnerReceiveRequest.Route or "loginbonus/list";
    var generalPresentRequest = accounts is not null && presents is not null && PresentRequest.Routes.Contains(route??"",StringComparer.Ordinal);
    var expeditionRequest = accounts is not null && expedition is not null && ExpeditionRequest.Routes.Contains(route??"",StringComparer.Ordinal);
    var miscRequest = accounts is not null && misc is not null && route is StoryReadRequest.Route or CharaSetVoiceRequest.Route or UpdateSupportRequest.Route;
    var questRankingRequest = accounts is not null && questModes is not null && route is QuestRankingRequest.Route or QuestRankingEntryRequest.Route;
    var extraShopRequest = route == ExtraShopPurchaseRequest.Route && accounts is not null && commerce is not null && eventQuest is not null;
    var takeoverRequest=accounts is not null&&TakeoverRequest.Routes.Contains(route??"",StringComparer.Ordinal);
    var userDeleteRequest=accounts is not null&&route==UserDeleteRequest.Route;
    var botSocialRequest=accounts is not null&&LocalBotRequest.Routes.Contains(route??"",StringComparer.Ordinal);
    var growthRequest = accounts is not null && growth is not null && GrowthRequest.Routes.Contains(route ?? "", StringComparer.Ordinal);
    var tutorialRequest = (request.Path is "/api/tutorial/update_step" or "/api/tutorial/skip") && accounts is not null;
    var gachaRequest = request.Path == "/api/tutorial/gacha_result" && accounts is not null && tutorialGacha is not null;
    var shopFailureStage = LocalShopFailureStage.Decrypt;
    var shopFailureReason = LocalShopFailureReason.DecryptInvalid;
    // Request shape is intentionally not retained for logging.
    void TraceShopFailure(int status, LocalShopFailureReason? reason = null)
    {
        if (!localShopRequest) return;
        TraceShopSafe(status, shopFailureStage, reason ?? shopFailureReason);
    }
    if ((!versionCheck && !blank && !userBlankRequest && !takeoverRequest && !userDeleteRequest && !nonceRequest && !createRequest && !loginRequest && !homeRequest && !friendPointPickupRequest && !homeUpdateStepRequest && !homeShopListRequest && !gachaTopRequest && !gachaResultRequest && !localShopRequest && !vipResetRequest && !vipDataRequest && !formationRequest && !cardSkinRequest && !profileRequest && !miscRequest && !expeditionRequest && !botSocialRequest && !beginnerRequest && !completeTutorialRequest && !generalPresentRequest && !activityRequest && !mainlineRequest && !growthRequest && !extraShopRequest && !questRankingRequest && !tutorialRequest && !gachaRequest) || request.HasQueryString)
    {
        Trace("unsupported-api", path, 501);
        TraceShopFailure(501, LocalShopFailureReason.HandlerPolicyRejected);
        return HostResponse.Empty(501);
    }
    if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase))
    {
        TraceShopFailure(405, LocalShopFailureReason.HandlerPolicyRejected);
        return HostResponse.Empty(405);
    }
    try
    {
        if (request.HeaderString("X-Use-Request-Encryption") != "Yes" ||
            request.HeaderString("X-app-version") != "2.5.0" ||
            request.ContentType?.Split(';')[0] != "application/octet-stream")
        {
            Trace("api", path, 415);
            TraceShopFailure(415, LocalShopFailureReason.HandlerPolicyRejected);
            return HostResponse.Empty(415);
        }
        diagnostic.Mark("read-body");
        request.CancellationToken.ThrowIfCancellationRequested();
        if (request.Body.Length > PacketCipher.MaximumPacketBytes)
        {
            TraceShopFailure(413, LocalShopFailureReason.HandlerPolicyRejected);
            return HostResponse.Empty(413);
        }
        diagnostic.Mark("decrypt");
        var decrypted = cipher.Decrypt(request.Body.Span);
        shopFailureStage = LocalShopFailureStage.Json; shopFailureReason = LocalShopFailureReason.JsonInvalid;
        diagnostic.Mark("strict-json");
        using var json = JsonDocument.Parse(decrypted, new JsonDocumentOptions { MaxDepth = 32 });
        shopFailureStage = LocalShopFailureStage.ParseHandler; shopFailureReason = LocalShopFailureReason.CommonInvalid;
        // Request shape capture omitted by safe logging policy.
        diagnostic.Mark("strict-common");
        var common = RequestEnvelope.ReadCommon(json.RootElement);
        diagnostic.Mark("handler");
        shopFailureReason = LocalShopFailureReason.ContractRejected;
        string? nonce = null;
        object? action = null;int protocolErrorId=0;string protocolErrorMessage="";
        object[]? sendPresentBoxItems = null;
        int? presentItemBadge = null;
        int? missionBadge = null;
        int? presentEquipBadge=null,beginnerBadge=null,newFollowerBadge=null;bool isSendPresentBox=false;bool? beginnerClearBadge=null;
        void ApplyAccountResult(AccountResult result)
        {
            diagnostic.Mark("projection");
            result = accounts!.ProjectBeginnerBadge(accounts!.ProjectVipBadge(result));
            result = accounts!.ProjectLocalBotBadge(result);
            missionBadge = result.Action?.GetValueOrDefault("mission_count") is int missionCount ? missionCount : null;
            protocolErrorId=result.ProtocolErrorId;protocolErrorMessage=result.ProtocolErrorMessage;
            action = result.Action; sendPresentBoxItems = result.SendPresentBoxItems; presentItemBadge = result.PresentItemBadge;
            presentEquipBadge=result.PresentEquipBadge;beginnerBadge=result.BeginnerBadge;newFollowerBadge=result.NewFollowerBadge;isSendPresentBox=result.IsSendPresentBox;beginnerClearBadge=result.BeginnerClearBadge;
        }
        if(userDeleteRequest)
        {
            var outcome=accounts!.DeleteAccount(UserDeleteRequest.Read(json.RootElement));
            // Fixed nonsecret reason only. The deleted actor cannot be projected or logged.
            Trace("account-delete-"+outcome.Reason,path,outcome.Result.Status);
            if(outcome.Result.Status!=200)return HostResponse.Empty(outcome.Result.Status);
            action=outcome.Result.Action;
        }
        else if(takeoverRequest)
        {
            var result=accounts!.Takeover(TakeoverRequest.Read(route!,json.RootElement));
            if(result.Status!=200){Trace("takeover-policy",path,result.Status);return HostResponse.Empty(result.Status);}
            // Never run Snapshot/badge projectors for a target preview.
            action=result.Action;protocolErrorId=result.ProtocolErrorId;protocolErrorMessage=result.ProtocolErrorMessage;
        }
        else if(completeTutorialRequest)
        {
            var result=accounts!.CompleteTutorial(CompleteTutorialRequest.Read(route!,json.RootElement));
            if(result.Status!=200){Trace("complete-tutorial-policy",path,result.Status);return HostResponse.Empty(result.Status);}
            ApplyAccountResult(result);
        }
        else if(generalPresentRequest)
        {
            var result=accounts!.GeneralPresents(PresentRequest.Read(route!,json.RootElement));
            if(result.Status!=200){Trace("present-policy",path,result.Status);return HostResponse.Empty(result.Status);}
            ApplyAccountResult(result);
        }
        else if(expeditionRequest)
        {
            var result=accounts!.Expedition(ExpeditionRequest.Read(route!,json.RootElement));
            if(result.Status!=200){Trace("expedition-policy",path,result.Status);return HostResponse.Empty(result.Status);}
            ApplyAccountResult(result);
        }
        else if(userBlankRequest)
        {
            var result=accounts!.RefreshAccountSnapshot(UserBlankRequest.Read(json.RootElement));
            if(result.Status!=200){Trace("user-blank-policy",path,result.Status);return HostResponse.Empty(result.Status);}
            ApplyAccountResult(result);
        }
        else if(beginnerRequest)
        {
            var result=route switch{BeginnerTopRequest.Route=>accounts!.BeginnerTop(BeginnerTopRequest.Read(json.RootElement)),BeginnerReceiveRequest.Route=>accounts!.ReceiveBeginner(BeginnerReceiveRequest.Read(json.RootElement)),"loginbonus/list"=>accounts!.ListLoginBonuses(VipLoginBonusListRequest.Read(json.RootElement)),_=>throw new InvalidDataException("Unknown beginner route.")};
            if(result.Status!=200){Trace("beginner-policy",path,result.Status);return HostResponse.Empty(result.Status);}
            ApplyAccountResult(result);
        }
        else if(botSocialRequest)
        {
            var result=accounts!.LocalBotSocial(LocalBotRequest.Read(route!,json.RootElement));
            if(result.Status!=200){Trace("bot-social-policy",path,result.Status);return HostResponse.Empty(result.Status);}
            ApplyAccountResult(result);
        }
        else if(miscRequest)
        {
            var result=route switch
            {
                StoryReadRequest.Route=>accounts!.ReadStory(StoryReadRequest.Read(json.RootElement)),
                CharaSetVoiceRequest.Route=>accounts!.SetCharaVoices(CharaSetVoiceRequest.Read(json.RootElement)),
                UpdateSupportRequest.Route=>accounts!.UpdateSupport(UpdateSupportRequest.Read(json.RootElement)),
                _=>throw new InvalidDataException("Unknown misc route.")
            };
            if(result.Status!=200){Trace("misc-policy",path,result.Status);return HostResponse.Empty(result.Status);}
            ApplyAccountResult(result);
        }
        else if (profileRequest)
        {
            var result = route switch
            {
                "story/name_change" or "profile/name_change" or "profile/message_change" => accounts!.ChangeProfileText(ProfileTextRequest.Read(json.RootElement, route)),
                "profile/card_change" => accounts!.ChangeProfileCard(ProfileCardRequest.Read(json.RootElement)),
                "profile/birthday_set" => accounts!.SetProfileBirthday(ProfileBirthdayRequest.Read(json.RootElement)),
                "profile/title_set" => accounts!.SetProfileTitles(ProfileTitlesRequest.Read(json.RootElement)),
                _ => throw new InvalidDataException("Unsupported profile route.")
            };
            if (result.Status != 200) { Trace("profile-policy", path, result.Status); return HostResponse.Empty(result.Status); }
            ApplyAccountResult(result);
        }
        else if(questRankingRequest)
        {
            var result=route==QuestRankingRequest.Route?accounts!.ListQuestRanking(QuestRankingRequest.Read(json.RootElement)):accounts!.ChangeQuestRankingEntry(QuestRankingEntryRequest.Read(json.RootElement));
            if(result.Status!=200){Trace("quest-ranking-policy",path,result.Status);return HostResponse.Empty(result.Status);}
            ApplyAccountResult(result);
        }
        else if(extraShopRequest)
        {
            var result=accounts!.PurchaseExtraShop(ExtraShopPurchaseRequest.Read(json.RootElement));
            if(result.Status!=200){Trace("extra-shop-policy",path,result.Status);return HostResponse.Empty(result.Status);}
            ApplyAccountResult(result);
        }
        else if (growthRequest)
        {
            var result = accounts!.MutateGrowth(growth!, GrowthRequest.Read(route!, json.RootElement));
            if (result.Status != 200) { Trace("growth-policy", path, result.Status); return HostResponse.Empty(result.Status); }
            ApplyAccountResult(result);
        }
        else if (activityRequest)
        {
            var result = route switch
            {
                "event/point/top" => accounts!.ActivityTop(activity!, ActivityTopRequest.Read(json.RootElement), 1),
                "event/ornament/top" => accounts!.ActivityTop(activity!, ActivityTopRequest.Read(json.RootElement), 2),
                "mission/list" => accounts!.ActivityMissionList(activity!, ActivityListRequest.Read(json.RootElement)),
                "mission/receive" => accounts!.ActivityMissionReceive(activity!, ActivityReceiveRequest.Read(json.RootElement)),
                _ => throw new InvalidDataException("Unsupported activity route.")
            };
            if (result.Status != 200) { Trace("api",path,result.Status); return HostResponse.Empty(result.Status); }
            ApplyAccountResult(result);
        }
        else if (mainlineRequest)
        {
            var result = route switch
            {
                "quest/story" => accounts!.PrepareQuestStory(ReadContract(diagnostic, () => QuestStoryRequest.Read(json.RootElement))),
                "quest/support" => accounts!.ListQuestSupport(ReadContract(diagnostic, () => QuestSupportRequest.Read(json.RootElement))),
                "quest/active_support" => accounts!.ActiveQuestSupport(ReadContract(diagnostic, () => QuestActiveSupportRequest.Read(json.RootElement))),
                "quest/start" => accounts!.StartQuest(ReadContract(diagnostic, () => QuestStartRequest.Read(json.RootElement))),
                "quest/check" => accounts!.CheckQuest(ReadContract(diagnostic, () => QuestCheckRequest.Read(json.RootElement))),
                "quest/retire" => accounts!.RetireQuest(ReadContract(diagnostic, () => QuestRetireRequest.Read(json.RootElement))),
                "quest/loop_result" => accounts!.FinishQuestLoop(ReadContract(diagnostic,()=>QuestLoopResultRequest.Read(json.RootElement))),
                "quest/result" => accounts!.FinishQuest(ReadContract(diagnostic, () => QuestResultRequest.Read(json.RootElement))),
                "mission/check/total_power" => accounts!.ReportQuestTotalPower(ReadContract(diagnostic, () => MissionTotalPowerRequest.Read(json.RootElement))),
                _ => throw new InvalidDataException("Unsupported mainline route.")
            };
            if (result.Status != 200) { Trace("api", path, result.Status); return HostResponse.Empty(result.Status); }
            ApplyAccountResult(result);
        }
        else if (localShopRequest)
        {
            var skinPurchase = route is "shop/purchase_check" or "shop/purchase" &&
                json.RootElement.TryGetProperty("contents_type", out var contentsType) && contentsType.ValueKind == JsonValueKind.Number &&
                contentsType.TryGetInt32(out var type) && type == 3;
            var commercePurchase = commerce is not null && route is "shop/purchase_check" or "shop/purchase" &&
                json.RootElement.TryGetProperty("product_id", out var commerceProduct) && commerceProduct.ValueKind == JsonValueKind.String &&
                commerce.Find(commerceProduct.GetString()!) is not null;
            var result = commercePurchase
                ? route == "shop/purchase_check" ? accounts!.CheckCommercePurchase(CommercePurchaseRequest.Read(json.RootElement, true, commerce!))
                    : accounts!.PurchaseCommerce(CommercePurchaseRequest.Read(json.RootElement, false, commerce!))
                : skinPurchase
                ? skinShop is null ? new AccountResult(501)
                    : route == "shop/purchase_check" ? accounts!.CheckSkinPurchase(SkinPurchaseRequest.Read(json.RootElement, true))
                    : accounts!.PurchaseSkin(SkinPurchaseRequest.Read(json.RootElement, false))
                : route switch
            {
                "shop/list" => accounts!.LocalShopList(LocalShopListRequest.Read(json.RootElement)),
                "shop/birthday_entry" => accounts!.EnterBirthday(BirthdayEntryRequest.Read(json.RootElement)),
                "shop/purchase_check" => accounts!.CheckLocalPurchase(LocalPurchaseRequest.Read(json.RootElement, true)),
                "shop/purchase" => accounts!.ClaimLocalPurchase(LocalPurchaseRequest.Read(json.RootElement, false)),
                _ => throw new InvalidDataException("Unsupported local shop route.")
            };
            if (result.Status != 200)
            {
                Trace("local-shop-policy", path, result.Status);
                TraceShopFailure(result.Status, result.Status != 400 ? LocalShopFailureReason.HandlerPolicyRejected :
                    route == "shop/purchase_check" ? LocalShopFailureReason.ProductNotAllowed :
                    route == "shop/purchase" ? LocalShopFailureReason.ProductOrReceiptNotAllowed : LocalShopFailureReason.ContractRejected);
                return HostResponse.Empty(result.Status);
            }
            ApplyAccountResult(result);
        }
        if (cardSkinRequest)
        {
            var result = accounts!.ChangeCardSkin(ChangeCardSkinRequest.Read(json.RootElement));
            if (result.Status != 200) { Trace("card-skin-policy", path, result.Status); return HostResponse.Empty(result.Status); }
            ApplyAccountResult(result);
        }
        if (formationRequest && !generalPresentRequest)
        {
            var result = route switch
            {
                "deck/edit" => accounts!.EditDecks(DeckEditRequest.Read(json.RootElement)),
                "deck/name" => accounts!.NameDeck(DeckNameRequest.Read(json.RootElement)),
                "deck/main" => accounts!.SetMainDeck(DeckNumberRequest.Read(json.RootElement)),
                "deck/reset" => accounts!.ResetDeck(DeckNumberRequest.Read(json.RootElement)),
                "home/customize" => accounts!.CustomizeHome(HomeCustomizeRequest.Read(json.RootElement)),
                "profile/top" => accounts!.ProfileTop(ProfileTopRequest.Read(json.RootElement)),
                "present/equip/list" => accounts!.ListPresentEquips(PresentEquipListRequest.Read(json.RootElement)),
                _ => throw new InvalidDataException("Unsupported formation route.")
            };
            if (result.Status != 200) { Trace("formation-policy", path, result.Status); return HostResponse.Empty(result.Status); }
            ApplyAccountResult(result);
        }
        if (vipDataRequest && !generalPresentRequest)
        {
            var result = route switch
            {
                "present/item/list" => accounts!.ListVipPresents(VipPresentListRequest.Read(json.RootElement)),
                "present/item/receive" => accounts!.ReceiveVipPresents(VipPresentReceiveRequest.Read(json.RootElement)),
                "loginbonus/list" => accounts!.ListVipLoginBonus(VipLoginBonusListRequest.Read(json.RootElement)),
                _ => throw new InvalidDataException("Unsupported VIP route.")
            };
            if (result.Status != 200) { Trace("vip-data-policy", path, result.Status); return HostResponse.Empty(result.Status); }
            ApplyAccountResult(result);
        }
        if (gachaResultRequest)
        {
            var isDedicatedTicket = json.RootElement.TryGetProperty("gacha_id", out var gachaId) &&
                gachaId.ValueKind == JsonValueKind.Number && gachaId.TryGetInt32(out var poolId) && poolId is 55003 or 55004;
            if (allGacha is null && (isDedicatedTicket ? ticketGacha is null : standardGacha is null))
            {
                Trace("disabled-gacha-policy", path, 501);
                return HostResponse.Empty(501);
            }
            var result = allGacha is not null ? accounts!.AllGacha(AllGachaRequest.Read(json.RootElement)) : isDedicatedTicket
                ? accounts!.TicketGacha(TicketGachaRequest.Read(json.RootElement))
                : accounts!.StandardGacha(StandardGachaRequest.Read(json.RootElement));
            if (result.Status != 200)
            {
                Trace("gacha-result-policy", path, result.Status);
                return HostResponse.Empty(result.Status);
            }
            ApplyAccountResult(result);
        }
        if (gachaTopRequest)
        {
            var result = accounts!.GachaTop(GachaTopRequest.Read(json.RootElement));
            if (result.Status != 200)
            {
                Trace("gacha-top-policy", path, result.Status);
                return HostResponse.Empty(result.Status);
            }
            ApplyAccountResult(result);
        }
        if (homeShopListRequest)
        {
            var result = accounts!.ListHomeShopState(HomeShopListRequest.Read(json.RootElement));
            if (result.Status != 200)
            {
                Trace("home-shop-list-policy", path, result.Status);
                return HostResponse.Empty(result.Status);
            }
            ApplyAccountResult(result);
        }
        if (homeUpdateStepRequest)
        {
            var result = accounts!.UpdateHomeStep(HomeUpdateStepRequest.Read(json.RootElement));
            if (result.Status != 200)
            {
                Trace("home-update-step-policy", path, result.Status);
                return HostResponse.Empty(result.Status);
            }
            ApplyAccountResult(result);
        }
        if (friendPointPickupRequest)
        {
            var result = accounts!.PickupFriendPoints(FriendPointPickupRequest.Read(json.RootElement));
            if (result.Status != 200)
            {
                Trace("friend-point-pickup-policy", path, result.Status);
                return HostResponse.Empty(result.Status);
            }
            ApplyAccountResult(result);
        }
        if (vipResetRequest)
        {
            var result = accounts!.ResetVip(VipResetRequest.Read(json.RootElement));
            if (result.Status != 200)
            {
                Trace("vip-reset-policy", path, result.Status);
                return HostResponse.Empty(result.Status);
            }
            ApplyAccountResult(result);
        }
        if (homeRequest)
        {
            var result = accounts!.Home(HomeRequest.Read(json.RootElement));
            if (result.Status != 200)
            {
                Trace("home-policy", path, result.Status);
                return HostResponse.Empty(result.Status);
            }
            ApplyAccountResult(result);
        }
        if (gachaRequest)
        {
            var result = accounts!.TutorialGacha(TutorialGachaRequest.Read(json.RootElement, common));
            if (result.Status != 200)
            {
                Trace("tutorial-gacha", path, result.Status);
                return HostResponse.Empty(result.Status);
            }
            ApplyAccountResult(result);
        }
        if (tutorialRequest)
        {
            var result = accounts!.ProgressTutorial(TutorialProgression.ReadRequest(route!, json.RootElement, common));
            if (result.Status != 200)
            {
                Trace("tutorial", path, result.Status);
                return HostResponse.Empty(result.Status);
            }
            ApplyAccountResult(result);
        }
        if (nonceRequest || createRequest || loginRequest)
        {
            if (!RequestEnvelope.IsPreAccount(common))
            {
                Trace("pre-account-conflict", path, 409);
                return HostResponse.Empty(409);
            }
            if (nonceRequest)
            {
                nonce = accounts!.IssueNonce();
                if (nonce is null)
                {
                    Trace("nonce-capacity", path, 503);
                    return HostResponse.Empty(503);
                }
            }
            else
            {
                var result = createRequest
                    ? accounts!.Create(AccountRequests.ReadCreate(json.RootElement))
                    : accounts!.Login(AccountRequests.ReadLogin(json.RootElement));
                if (result.Status != 200)
                {
                    Trace("account-policy", path, result.Status);
                    return HostResponse.Empty(result.Status);
                }
                ApplyAccountResult(result);
            }
        }
        diagnostic.Mark("serialize");
        var response = cipher.Encrypt(versionCheck ? VersionCheck.Response() : bootstrap!.Response(nonce, action, sendPresentBoxItems, presentItemBadge, missionBadge,presentEquipBadge,beginnerBadge,newFollowerBadge,isSendPresentBox,beginnerClearBadge,protocolErrorId,protocolErrorMessage));
        // EncryptedResponse applies the original no-store header.
        Trace("api", path, 200);
        diagnostic.Mark("write-response");
        return EncryptedResponse(response);
    }
    catch (Exception ex) when (ex is InvalidDataException or JsonException or LocalShopContractException)
    {
        Trace("invalid-packet", path, 400);
        TraceShopFailure(400, ex is LocalShopContractException contract ? contract.Reason : null);
        return HostResponse.Empty(400);
    }
    // HTTP framing/body exceptions belong to the transport; explicit length guard remains above.
    catch (SqliteException)
    {
        Trace("account-store-unavailable", path, 503);
        TraceShopFailure(503, LocalShopFailureReason.HandlerPolicyRejected);
        return HostResponse.Empty(503);
    }
    }
}

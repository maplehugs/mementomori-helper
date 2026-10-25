using MementoMori.Ortega.Share.Data.ApiInterface.Equipment;
using MementoMori.Ortega.Share.Data.DtoInfo;
using MementoMori.Ortega.Share.Data.Equipment;
using MementoMori.Ortega.Share.Data.Item;
using MementoMori.Ortega.Share.Master.Data;
using MementoMori.Ortega.Custom;
using CharLevelUp = MementoMori.Ortega.Share.Data.ApiInterface.Character;
using LevelLinkApi = MementoMori.Ortega.Share.Data.ApiInterface.LevelLink;

namespace MementoMori.Funcs;

public partial class MementoMoriFuncs
{
    public async Task AutoGrow()
    {
        if (GameConfig.AutoJob.AutoEquipBest) await AutoEquipBest();
        if (GameConfig.AutoJob.AutoLevelUpCharacter) await AutoLevelUpCharacter();
        if (GameConfig.AutoJob.AutoEquipBest) await AutoEquipBest(); // 升级后可能能穿更高级的
        if (GameConfig.AutoJob.AutoReinforceTeamEquipment) await AutoReinforceTeamEquipment();
        var link = UserSyncData.UserLevelLinkDtoInfo;
        AddLog($"[Grow] Link Lv{link?.PartyLevel}.{link?.PartySubLevel} | " +
               string.Join(", ", GetMainTeam().Select(c => $"{CharacterTable.GetById(c.CharacterId).GetCombinedName()} Lv{GetEffectiveLevel(c)}")));
    }

    private bool CanAfford(IEnumerable<UserItem> items) =>
        items.All(i => i.ItemCount <= 0 || UserSyncData.GetUserItemCount(i.ItemType, i.ItemId) >= i.ItemCount);

    // 主队角色 (Auto deck), 没有就用等级最高的5个
    private List<UserCharacterDtoInfo> GetMainTeam()
    {
        var team = UserSyncData.GetDeckUserCharacterDtoInfos(DeckUseContentType.Auto);
        return team.Count > 0 ? team : UserSyncData.UserCharacterDtoInfos.OrderByDescending(d => d.Level).Take(5).ToList();
    }

    private long GetEffectiveLevel(UserCharacterDtoInfo c) =>
        UserSyncData.IsLevelLinkMember(c.Guid) ? UserSyncData.GetLevelLinkLevel(c.CharacterId) : c.Level;

    public async Task AutoLevelUpCharacter()
    {
        await ExecuteQuickAction(async (log, token) =>
        {
            var count = 0;
            var link = UserSyncData.UserLevelLinkDtoInfo;
            if (link is {IsPartyMode: true})
            {
                // 组队模式: 每个 LevelLinkMB 是一步, 同等级内 addExp, 跨等级 levelUp
                var table = LevelLinkTable.GetArray().OrderBy(d => d.Id).ToList();
                while (!token.IsCancellationRequested)
                {
                    link = UserSyncData.UserLevelLinkDtoInfo;
                    if (link.PartyLevel >= link.PartyMaxLevel && link.PartyMaxLevel > 0) break;
                    var cur = table.FindIndex(d => d.PartyLevel == link.PartyLevel && d.PartySubLevel == link.PartySubLevel);
                    if (cur < 0 || cur + 1 >= table.Count) break;
                    var next = table[cur + 1];
                    if (!CanAfford(next.RequiredLevelUpItems)) break;
                    if (next.PartyLevel == link.PartyLevel)
                        await GetResponse<LevelLinkApi.AddExpRequest, LevelLinkApi.AddExpResponse>(new LevelLinkApi.AddExpRequest());
                    else
                        await GetResponse<LevelLinkApi.LevelUpRequest, LevelLinkApi.LevelUpResponse>(new LevelLinkApi.LevelUpRequest());
                    count++;
                    await Task.Delay(GameConfig.AutoRequestDelay, token);
                }

                log($"Level Link Lv{UserSyncData.UserLevelLinkDtoInfo.PartyLevel}.{UserSyncData.UserLevelLinkDtoInfo.PartySubLevel} (+{count})");
                return;
            }

            // 普通模式: 只升不在等级链接里的前5名, 每次升最低的那个 (等级链接等级 = 前5名最低等级)
            var skip = new HashSet<string>();
            while (!token.IsCancellationRequested)
            {
                var c = UserSyncData.UserCharacterDtoInfos
                    .Where(d => !UserSyncData.IsLevelLinkMember(d.Guid))
                    .OrderByDescending(d => d.Level).Take(5)
                    .Where(d => !skip.Contains(d.Guid))
                    .Where(d => d.Level < OrtegaConst.LevelLink.MaxCharacterLevel.GetValueOrDefault(d.RarityFlags, 240))
                    .MinBy(d => d.Level);
                if (c == null) break;
                var cost = CharacterLevelTable.GetArray().FirstOrDefault(d => d.Level == c.Level + 1);
                if (cost == null) break;
                if (!CanAfford(cost.RequiredItemList)) break;
                try
                {
                    await GetResponse<CharLevelUp.LevelUpRequest, CharLevelUp.LevelUpResponse>(new CharLevelUp.LevelUpRequest {UserCharacterGuid = c.Guid});
                    count++;
                }
                catch (Exception e)
                {
                    // ponytail: 稀有度上限没精确算, 报错就跳过这个角色
                    log($"LevelUp {CharacterTable.GetById(c.CharacterId).GetCombinedName()}: {e.Message}");
                    skip.Add(c.Guid);
                }

                await Task.Delay(GameConfig.AutoRequestDelay, token);
            }

            log($"{TextResourceTable.Get("[CharacterLevelUpLabel]")} +{count} {ResourceStrings.Finished}");
        });
    }

    // 给主队每个位置换上能穿的战力最高的装备 (继承强化等级)
    public async Task AutoEquipBest()
    {
        await ExecuteQuickAction(async (log, token) =>
        {
            var count = 0;
            foreach (var c in GetMainTeam())
            {
                var job = CharacterTable.GetById(c.CharacterId).JobFlags;
                var level = GetEffectiveLevel(c);
                var equipped = UserSyncData.GetUserEquipmentDtoInfoSlotTypeDictionaryByCharacterGuid(c.Guid);
                var changes = new List<EquipmentChangeInfo>();
                foreach (var (slot, current) in equipped)
                {
                    var currentPower = current == null ? -1 : EquipmentTable.GetById(current.EquipmentId).PerformancePoint;
                    bool Fits(EquipmentMB mb) => mb.SlotType == slot && (mb.EquippedJobFlags & job) != 0 && mb.EquipmentLv <= level && mb.PerformancePoint > currentPower;

                    // 背包里的 (未强化的叠在 item 里, 强化过的是没主人的 EquipmentDtoInfo)
                    var bestItem = UserSyncData.UserItemDtoInfo
                        .Where(d => d.ItemType == ItemType.Equipment && d.ItemCount > 0)
                        .Select(d => EquipmentTable.GetById(d.ItemId)).Where(mb => mb != null && Fits(mb))
                        .MaxBy(mb => mb.PerformancePoint);
                    var bestDto = UserSyncData.UserEquipmentDtoInfos
                        .Where(d => string.IsNullOrEmpty(d.CharacterGuid))
                        .Select(d => (dto: d, mb: EquipmentTable.GetById(d.EquipmentId))).Where(x => x.mb != null && Fits(x.mb))
                        .OrderByDescending(x => x.mb.PerformancePoint).FirstOrDefault();
                    if (bestItem == null && bestDto.dto == null) continue;

                    var useDto = bestDto.dto != null && (bestItem == null || bestDto.mb.PerformancePoint >= bestItem.PerformancePoint);
                    var mb = useDto ? bestDto.mb : bestItem;
                    changes.Add(new EquipmentChangeInfo
                    {
                        EquipmentGuid = useDto ? bestDto.dto.Guid : null,
                        EquipmentId = mb.Id,
                        EquipmentSlotType = slot,
                        // 新装备没强化过才继承, 免得覆盖
                        IsInherit = current != null && (!useDto || bestDto.dto.ReinforcementLv == 0),
                        // null = 全部继承 (选择性继承要解锁后才能用, 否则 232061)
                        EquipmentInheritanceTypeList = null
                    });
                }

                if (changes.Count == 0) continue;
                try
                {
                    await GetResponse<ChangeEquipmentRequest, ChangeEquipmentResponse>(new ChangeEquipmentRequest
                    {
                        UserCharacterGuid = c.Guid, EquipmentChangeInfos = changes
                    });
                    count += changes.Count;
                    log($"{CharacterTable.GetById(c.CharacterId).GetCombinedName()}: {string.Join(", ", changes.Select(x => EquipmentTable.GetById(x.EquipmentId).Memo))}");
                }
                catch (Exception e)
                {
                    log($"Equip {CharacterTable.GetById(c.CharacterId).GetCombinedName()}: {e.Message}");
                }

                await Task.Delay(GameConfig.AutoRequestDelay, token);
            }

            log($"AutoEquip +{count} {ResourceStrings.Finished}");
        });
    }

    // 主队装备轮流 +1 直到没钱/材料 (装备等级是强化上限)
    public async Task AutoReinforceTeamEquipment()
    {
        await ExecuteQuickAction(async (log, token) =>
        {
            var count = 0;
            var guids = GetMainTeam().Select(c => c.Guid).ToHashSet();
            while (!token.IsCancellationRequested)
            {
                var e = UserSyncData.UserEquipmentDtoInfos
                    .Where(d => guids.Contains(d.CharacterGuid) && EquipmentTable.GetById(d.EquipmentId).EquipmentLv > d.ReinforcementLv)
                    .MinBy(d => d.ReinforcementLv);
                if (e == null) break;
                var mat = EquipmentReinforcementMaterialTable.GetArray().FirstOrDefault(d => d.ReinforcementLevel == e.ReinforcementLv + 1);
                if (mat == null) break;
                var cost = EquipmentTable.GetById(e.EquipmentId).SlotType == EquipmentSlotType.Weapon ? mat.WeaponRequiredItemList : mat.OthersRequiredItemList;
                if (!CanAfford(cost)) break;
                await GetResponse<ReinforcementRequest, ReinforcementResponse>(new ReinforcementRequest {EquipmentGuid = e.Guid, NumberOfTimes = 1});
                count++;
                await Task.Delay(GameConfig.AutoRequestDelay, token);
            }

            log($"{TextResourceTable.Get("[CommonReinforceLabel]")} +{count} {ResourceStrings.Finished}");
        });
    }
}

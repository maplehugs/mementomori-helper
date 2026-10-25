using MementoMori.Ortega.Custom;
using MementoMori.Ortega.Share.Data.ApiInterface.Battle;
using MementoMori.Ortega.Share.Data.ApiInterface.Quest;
using MementoMori.Ortega.Share.Data.ApiInterface.TowerBattle;
using LevelLinkApi = MementoMori.Ortega.Share.Data.ApiInterface.LevelLink;

namespace MementoMori.Funcs;

public partial class MementoMoriFuncs
{
    private const int ProgressMaxLosses = 3; // 连输3次就停, 等下次成长后再打
    private const int ProgressMaxFights = 60; // ponytail: 每轮上限, 防止再被 403

    public string LastProgressSummary { get; private set; } = "";

    // 一键: 成长 -> 主线 -> 塔 -> 再成长
    public async Task AutoProgress()
    {
        await AutoFillLevelLink();
        await AutoGrow();
        if (GameConfig.AutoJob.AutoProgressQuest) await AutoProgressQuest();
        if (GameConfig.AutoJob.AutoProgressTower)
            foreach (var t in GetAvailableTower())
                await AutoProgressTower(t);
        await AutoGrow();
    }

    // 把空的等级链接格子填满 (主队优先, 然后稀有度高的), 不动前5名基础角色
    public async Task AutoFillLevelLink()
    {
        await ExecuteQuickAction(async (log, token) =>
        {
            var link = UserSyncData.UserLevelLinkDtoInfo;
            if (link == null) return;
            var members = UserSyncData.UserLevelLinkMemberDtoInfos ?? new();
            var baseFive = UserSyncData.UserCharacterDtoInfos.Where(d => !UserSyncData.IsLevelLinkMember(d.Guid))
                .OrderByDescending(d => d.Level).Take(5).Select(d => d.Guid).ToHashSet();
            var team = GetMainTeam().Select(d => d.Guid).ToHashSet();
            var candidates = UserSyncData.UserCharacterDtoInfos
                .Where(d => !baseFive.Contains(d.Guid) && !UserSyncData.IsLevelLinkMember(d.Guid))
                .OrderByDescending(d => team.Contains(d.Guid)).ThenByDescending(d => d.RarityFlags).ThenByDescending(d => d.Level)
                .Select(d => d.CharacterId).Distinct().ToList(); // 同角色只放一个
            var freeCells = Enumerable.Range(1, link.MemberMaxCount)
                .Where(n => !members.Any(m => m.CellNo == n && !string.IsNullOrEmpty(m.UserCharacterGuid))).ToList();
            foreach (var (cell, charId) in freeCells.Zip(candidates))
            {
                if (token.IsCancellationRequested) break;
                if (members.Any(m => m.CharacterId == charId)) continue;
                var c = UserSyncData.UserCharacterDtoInfos.Where(d => d.CharacterId == charId && !baseFive.Contains(d.Guid)).MaxBy(d => d.RarityFlags);
                if (c == null) continue;
                try
                {
                    await GetResponse<LevelLinkApi.SetMemberRequest, LevelLinkApi.SetMemberResponse>(new LevelLinkApi.SetMemberRequest {LevelLinkCellNo = cell, UserCharacterGuid = c.Guid});
                    log($"Level Link +{CharacterTable.GetById(c.CharacterId).GetCombinedName()} (slot {cell})");
                }
                catch (Exception e)
                {
                    log($"Level Link slot {cell}: {e.Message}"); // 冷却中的格子会报错, 跳过
                }

                await Task.Delay(GameConfig.AutoRequestDelay, token);
            }
        });
    }

    public async Task AutoProgressQuest()
    {
        await ExecuteQuickAction(async (log, token) =>
        {
            int fights = 0, losses = 0;
            await GetResponse<MapInfoRequest, MapInfoResponse>(new MapInfoRequest {IsUpdateOtherPlayerInfo = true});
            while (!token.IsCancellationRequested && fights < ProgressMaxFights && losses < ProgressMaxLosses)
            {
                var questId = UserSyncData.UserBattleBossDtoInfo.BossClearMaxQuestId + 1;
                if (QuestTable.GetById(questId) == null) break; // 通关了
                try
                {
                    await GetResponse<GetQuestInfoRequest, GetQuestInfoResponse>(new GetQuestInfoRequest {TargetQuestId = questId});
                    var resp = await GetResponse<BossRequest, BossResponse>(new BossRequest {QuestId = questId});
                    fights++;
                    var win = resp.BattleResult.SimulationResult.BattleEndInfo.IsWinAttacker();
                    log($"Quest {QuestTable.GetById(questId).Memo}: {(win ? "WIN" : "LOSE")}");
                    if (win)
                    {
                        losses = 0;
                        await GetResponse<MapInfoRequest, MapInfoResponse>(new MapInfoRequest {IsUpdateOtherPlayerInfo = true});
                        try { await GetResponse<NextQuestRequest, NextQuestResponse>(new NextQuestRequest()); }
                        catch (Exception) { /* 没有下一关可移动时会报错 */ }
                    }
                    else losses++;
                }
                catch (Exception e)
                {
                    log($"Quest {questId}: {e.Message}");
                    break;
                }

                await Task.Delay(Math.Max(GameConfig.AutoRequestDelay, 1500), token);
            }

            LastProgressSummary = $"Quest {QuestTable.GetById(UserSyncData.UserBattleBossDtoInfo.BossClearMaxQuestId)?.Memo}";
            log($"[Progress] {LastProgressSummary} ({fights} fights)");
        });
    }

    public async Task AutoProgressTower(TowerType type)
    {
        await ExecuteQuickAction(async (log, token) =>
        {
            int fights = 0, losses = 0;
            while (!token.IsCancellationRequested && fights < ProgressMaxFights && losses < ProgressMaxLosses)
            {
                var tower = UserSyncData.UserTowerBattleDtoInfos?.FirstOrDefault(d => d.TowerType == type);
                if (tower == null) break;
                if (type != TowerType.Infinite && tower.TodayClearNewFloorCount >= 10) break;
                var questId = tower.MaxTowerBattleId + 1;
                try
                {
                    var resp = await GetResponse<StartRequest, StartResponse>(new StartRequest {TargetTowerType = type, TowerBattleQuestId = questId});
                    fights++;
                    var win = resp.BattleResult.SimulationResult.BattleEndInfo.IsWinAttacker();
                    log($"{TextResourceTable.Get(type)} {questId}: {(win ? "WIN" : "LOSE")}");
                    losses = win ? 0 : losses + 1;
                }
                catch (Exception e)
                {
                    log($"{TextResourceTable.Get(type)} {questId}: {e.Message}");
                    break;
                }

                await Task.Delay(Math.Max(GameConfig.AutoRequestDelay, 1500), token);
            }
        });
    }
}

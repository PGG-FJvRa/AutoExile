using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using AutoExile.Systems;
using System.Numerics;

namespace AutoExile.Modes.BossEncounters
{
    /// <summary>
    /// Normal Searing Exarch encounter opened with an Incandescent Invitation.
    /// The encounter waits through Exarch's invulnerable transitions, lets the
    /// normal combat system fight whenever he is targetable, then sweeps the
    /// final boss position before completing.
    /// </summary>
    public sealed class SearingExarchEncounter : IBossEncounter
    {
        public string Name => "Searing Exarch";
        public string Status { get; private set; } = "";

        // Incandescent Invitation. The normal key uses the Eldritch metadata path;
        // CurrencyUberBossKeyRed is the separate Uber invitation.
        private const string FragmentPath = "CurrencyEldritchBossKey";
        private const string LegacyFragmentPath = "CurrencyBossKeyRed";
        private const string BossPath = "CleansingFireBoss";
        private const string DescensionAltarPath = "CleansingFireDescensionObject";
        private const float LootScanIntervalMs = 500f;

        public Func<Element, bool> MapFilter => el =>
        {
            var path = el.Entity?.Path;
            return path?.Contains(FragmentPath, StringComparison.OrdinalIgnoreCase) == true
                || path?.Contains(LegacyFragmentPath, StringComparison.OrdinalIgnoreCase) == true;
        };

        public string? InventoryFragmentPath => FragmentPath;
        public int FragmentCost => 1;

        public IReadOnlyList<string> MustLootItems { get; } = new[] { "Forbidden Flame" };

        // Do not allow combat positioning to pull the character away while loot is dropping.
        public bool SuppressCombatPositioning => _phase == ExarchPhase.WaitingForLoot;

        private ExarchPhase _phase = ExarchPhase.Idle;
        private DateTime _phaseStartTime;
        private Entity? _bossEntity;
        private bool _bossWasAlive;
        private Vector2? _bossDeathPos;
        private DateTime _bossLastSeenAt;
        private DateTime _lastLootScan;

        private enum ExarchPhase
        {
            Idle,
            WaitingForBoss,
            Fighting,
            WaitingForLoot,
        }

        public void OnEnterZone(BotContext ctx)
        {
            var gc = ctx.Game;
            var pfGrid = gc.IngameState?.Data?.RawPathfindingData;
            var tgtGrid = gc.IngameState?.Data?.RawTerrainTargetingData;
            if (pfGrid != null && gc.Player != null)
            {
                var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                ctx.Exploration.Initialize(pfGrid, tgtGrid, playerGrid, ctx.Settings.Build.BlinkRange.Value);
            }

            _phase = ExarchPhase.WaitingForBoss;
            _phaseStartTime = DateTime.Now;
            _bossEntity = null;
            _bossWasAlive = false;
            _bossDeathPos = null;
            _bossLastSeenAt = DateTime.MinValue;
            _lastLootScan = DateTime.MinValue;
            Status = "Entered arena — waiting for Searing Exarch";
            ctx.Log("[Exarch] Zone entered");
        }

        public BossEncounterResult Tick(BotContext ctx)
        {
            var gc = ctx.Game;
            if (gc?.Player == null) return BossEncounterResult.InProgress;

            var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
            ctx.Exploration.Update(playerGrid);

            _bossEntity = FindBoss(gc);
            if (_bossEntity != null)
            {
                _bossLastSeenAt = DateTime.Now;
                _bossDeathPos = _bossEntity.GridPosNum;
                if (_bossEntity.IsAlive)
                    _bossWasAlive = true;
            }

            ctx.Combat.BossInvulnerable = _bossEntity != null
                && _bossEntity.IsAlive
                && !_bossEntity.IsTargetable;

            if (_phase != ExarchPhase.WaitingForLoot && _bossWasAlive &&
                ((_bossEntity != null && !_bossEntity.IsAlive) || IsDescensionAltarVisible(gc)))
            {
                StartLootSweep(ctx, "Kill detected");
            }

            switch (_phase)
            {
                case ExarchPhase.WaitingForBoss:
                    return TickWaitingForBoss(ctx);
                case ExarchPhase.Fighting:
                    return TickFighting(ctx);
                case ExarchPhase.WaitingForLoot:
                    return TickWaitingForLoot(ctx, gc, playerGrid);
                default:
                    return BossEncounterResult.InProgress;
            }
        }

        private BossEncounterResult TickWaitingForBoss(BotContext ctx)
        {
            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 90)
            {
                Status = "Timeout waiting for Searing Exarch";
                return BossEncounterResult.Failed;
            }

            if (_bossEntity != null && _bossEntity.IsAlive)
            {
                _phase = ExarchPhase.Fighting;
                _phaseStartTime = DateTime.Now;
                ctx.Log("[Exarch] Boss found — fighting");
            }

            Status = _bossEntity?.IsTargetable == false
                ? "Searing Exarch emerging — waiting for vulnerability"
                : "Waiting for Searing Exarch";
            return BossEncounterResult.InProgress;
        }

        private BossEncounterResult TickFighting(BotContext ctx)
        {
            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 600)
            {
                Status = "Fight timeout (10min)";
                return BossEncounterResult.Failed;
            }

            // A completed encounter can remove the boss entity before the altar appears.
            // Require a long absence so normal intermission mechanics do not end the run.
            if (_bossWasAlive && _bossEntity == null &&
                (DateTime.Now - _bossLastSeenAt).TotalSeconds > 30)
            {
                StartLootSweep(ctx, "Boss absent after fight");
                return BossEncounterResult.InProgress;
            }

            if (_bossEntity == null)
            {
                Status = "Searing Exarch transitioning";
                return BossEncounterResult.InProgress;
            }

            var life = _bossEntity.GetComponent<ExileCore.PoEMemory.Components.Life>();
            var hpPct = life != null ? life.CurHP * 100 / Math.Max(1, life.MaxHP) : 0;
            Status = _bossEntity.IsTargetable
                ? $"Fighting Searing Exarch — HP:{hpPct}%"
                : "Searing Exarch invulnerable — waiting";
            return BossEncounterResult.InProgress;
        }

        private BossEncounterResult TickWaitingForLoot(BotContext ctx, GameController gc, Vector2 playerGrid)
        {
            var timeout = ctx.Settings.Run.LootSweepTimeoutSeconds.Value;
            var elapsed = (DateTime.Now - _phaseStartTime).TotalSeconds;
            if (elapsed > timeout)
            {
                Status = "Loot sweep done";
                ctx.Log("[Exarch] Loot sweep complete");
                return BossEncounterResult.Complete;
            }

            var countdown = $"({timeout - elapsed:F0}s left)";
            var lootPos = _bossDeathPos ?? playerGrid;
            if (Vector2.Distance(playerGrid, lootPos) > 15 && !ctx.Navigation.IsNavigating)
                ctx.Navigation.NavigateTo(gc, lootPos);

            if ((DateTime.Now - _lastLootScan).TotalMilliseconds >= LootScanIntervalMs)
            {
                ctx.Loot.Scan(gc);
                _lastLootScan = DateTime.Now;
            }

            if (ctx.Interaction.IsBusy)
            {
                Status = $"Picking up loot {countdown}";
                return BossEncounterResult.InProgress;
            }

            if (ctx.Loot.HasLootNearby)
            {
                var (_, candidate) = ctx.Loot.PickupNext(ctx.Interaction, ctx.Navigation);
                if (candidate != null)
                {
                    Status = $"Looting: {candidate.ItemName} {countdown}";
                    return BossEncounterResult.InProgress;
                }
            }

            if (ctx.Loot.TogglePhase != LootSystem.LabelTogglePhase.Idle)
            {
                ctx.Loot.TickLabelToggle(gc);
                Status = $"Label toggle {countdown}";
                return BossEncounterResult.InProgress;
            }
            if (ctx.Loot.ShouldToggleLabels(gc))
            {
                ctx.Loot.StartLabelToggle(gc);
                return BossEncounterResult.InProgress;
            }

            Status = $"Waiting for loot {countdown}";
            return BossEncounterResult.InProgress;
        }

        private void StartLootSweep(BotContext ctx, string reason)
        {
            _phase = ExarchPhase.WaitingForLoot;
            _phaseStartTime = DateTime.Now;
            _lastLootScan = DateTime.MinValue;
            Status = $"{reason} — looting";
            ctx.Log($"[Exarch] {reason} — looting");
        }

        private static Entity? FindBoss(GameController gc)
        {
            try
            {
                foreach (var entity in gc.EntityListWrapper.ValidEntitiesByType[EntityType.Monster])
                {
                    if (entity.IsHostile && entity.Rarity == MonsterRarity.Unique &&
                        entity.Path?.Contains(BossPath, StringComparison.OrdinalIgnoreCase) == true)
                        return entity;
                }
            }
            catch (IndexOutOfRangeException) { }
            return null;
        }

        private static bool IsDescensionAltarVisible(GameController gc)
        {
            try
            {
                return gc.EntityListWrapper.ValidEntitiesByType[EntityType.MiscellaneousObjects]
                    .Any(entity => entity.Path?.Contains(DescensionAltarPath, StringComparison.OrdinalIgnoreCase) == true);
            }
            catch (IndexOutOfRangeException) { return false; }
        }

        public void Reset()
        {
            _phase = ExarchPhase.Idle;
            _bossEntity = null;
            _bossWasAlive = false;
            _bossDeathPos = null;
            _bossLastSeenAt = DateTime.MinValue;
            _lastLootScan = DateTime.MinValue;
            Status = "";
        }
    }
}
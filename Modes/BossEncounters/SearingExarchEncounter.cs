using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.PoEMemory.Components;
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
        private const float PositionLockDistance = 12f;

        // The active load flow prefers the player inventory by base name; retain a
        // permissive stash filter only as a compatibility fallback.
        public Func<Element, bool> MapFilter => _ => true;

        public string? InventoryFragmentPath => FragmentPath;
        public int FragmentCost => 1;

        public bool UsesPreloadedMapDevice => true;
        public bool UsesAutoMatchCtrlClick => true;

        // Exarch's Forbidden Flame drops unidentified, so its ground label is the
        // base item name rather than the unique name. Keep both labels whitelisted.
        public IReadOnlyList<string> MustLootItems { get; } = new[]
        {
            "Forbidden Flame",
            "Crimson Jewel",
            "Exceptional Eldritch Ember",
        };

        // Walk to the arena center/explore while the boss is not yet visible instead
        // of allowing normal combat to idle at the entry portal.
        public bool SuppressCombat => _phase == ExarchPhase.WaitingForBoss;

        // During Exarch's ball/invulnerability transitions the entity remains present
        // but is not targetable. Hold position rather than chasing its transition path.
        // Also do not allow combat positioning to pull the character during loot.
        public bool SuppressCombatPositioning => _phase == ExarchPhase.WaitingForLoot ||
            _phase == ExarchPhase.Fighting;

        public bool SuppressDodge => _phase == ExarchPhase.Fighting;

        private ExarchPhase _phase = ExarchPhase.Idle;
        private DateTime _phaseStartTime;
        private Entity? _bossEntity;
        private bool _bossWasAlive;
        private Vector2? _bossDeathPos;
        private DateTime _bossLastSeenAt;
        private DateTime _lastLootScan;
        private bool _isInvulnerablePhase;
        private bool _combatPositionLocked;

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
            _isInvulnerablePhase = false;
            _combatPositionLocked = false;
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

            _isInvulnerablePhase = IsInvulnerablePhase(_bossEntity);

            ctx.Combat.BossInvulnerable = _isInvulnerablePhase;

            if (_phase != ExarchPhase.WaitingForLoot &&
                ((_bossWasAlive && _bossEntity != null && !_bossEntity.IsAlive) ||
                IsDescensionAltarVisible(gc)))
            {
                StartLootSweep(ctx, "Kill detected");
            }

            // Fallback for game versions where Exarch's monster metadata differs
            // from the expected path: after the arena has been searched, boss loot
            // is still an authoritative indication that the fight is over.
            if (_phase == ExarchPhase.WaitingForBoss &&
                (DateTime.Now - _phaseStartTime).TotalSeconds > 10)
            {
                ctx.Loot.Scan(gc);
                if (ctx.Loot.HasLootNearby)
                    StartLootSweep(ctx, "Post-fight loot detected");
            }

            switch (_phase)
            {
                case ExarchPhase.WaitingForBoss:
                    return TickWaitingForBoss(ctx, gc, playerGrid);
                case ExarchPhase.Fighting:
                    return TickFighting(ctx, gc, playerGrid);
                case ExarchPhase.WaitingForLoot:
                    return TickWaitingForLoot(ctx, gc, playerGrid);
                default:
                    return BossEncounterResult.InProgress;
            }
        }

        private BossEncounterResult TickWaitingForBoss(BotContext ctx, GameController gc, Vector2 playerGrid)
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

            // The arena initially has no targetable boss near the portal. Sweep the
            // navigation grid toward its unexplored center until Exarch streams in.
            if (_phase == ExarchPhase.WaitingForBoss && !ctx.Navigation.IsNavigating)
            {
                var target = ctx.Exploration.GetNextExplorationTarget(playerGrid);
                if (target.HasValue)
                {
                    ctx.Navigation.NavigateTo(gc, target.Value);
                    Status = $"Searching arena for Searing Exarch ({Vector2.Distance(playerGrid, target.Value):F0}g)";
                    return BossEncounterResult.InProgress;
                }
            }

            Status = _bossEntity?.IsTargetable == false
                ? "Searing Exarch emerging — waiting for vulnerability"
                : "Waiting for Searing Exarch";
            return BossEncounterResult.InProgress;
        }

        private BossEncounterResult TickFighting(BotContext ctx, GameController gc, Vector2 playerGrid)
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

            // The combat system only repositions after it has an in-range target.
            // Exarch can stream in outside that range, so explicitly close the gap
            // first and then allow normal combat positioning to take over.
            var bossGrid = _bossEntity.GridPosNum;
            var distance = Vector2.Distance(playerGrid, bossGrid);

            // The boss is intentionally invulnerable immediately after entry. Do
            // not lock at that point — keep following the real boss position until
            // the entry animation completes and he becomes vulnerable.
            if (!_combatPositionLocked && _isInvulnerablePhase)
            {
                if (distance > 5 && !ctx.Navigation.IsNavigating)
                    ctx.Navigation.NavigateTo(gc, bossGrid);
                Status = $"Searing Exarch emerging — moving to boss ({distance:F0}g)";
                return BossEncounterResult.InProgress;
            }

            if (!_combatPositionLocked && distance > PositionLockDistance && !ctx.Navigation.IsNavigating)
            {
                ctx.Navigation.NavigateTo(gc, bossGrid);
                Status = $"Approaching Searing Exarch ({distance:F0}g)";
                return BossEncounterResult.InProgress;
            }

            // The entrance has the boss present but initially invulnerable. Reach
            // directly on the boss first; only then do later invulnerability signals mean
            // the ball phase and require us to hold position.
            if (!_combatPositionLocked)
            {
                _combatPositionLocked = true;
                if (ctx.Navigation.IsNavigating)
                    ctx.Navigation.Stop(gc);
                ctx.Log($"[Exarch] At boss ({distance:F0}g) — holding position until kill");
            }

            if (_isInvulnerablePhase)
            {
                Status = "Searing Exarch ball/invulnerability phase — holding position";
                return BossEncounterResult.InProgress;
            }

            var life = _bossEntity.GetComponent<ExileCore.PoEMemory.Components.Life>();
            var hpPct = life != null ? life.CurHP * 100 / Math.Max(1, life.MaxHP) : 0;
            Status = _bossEntity.IsTargetable
                ? $"Fighting Searing Exarch — HP:{hpPct}% dist={distance:F0}g"
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
                Entity? soleUnique = null;
                foreach (var entity in gc.EntityListWrapper.ValidEntitiesByType[EntityType.Monster])
                {
                    if (!entity.IsHostile || entity.Rarity != MonsterRarity.Unique)
                        continue;

                    var path = entity.Path ?? "";
                    var name = entity.RenderName ?? "";
                    if (path.Contains(BossPath, StringComparison.OrdinalIgnoreCase) ||
                        path.Contains("CleansingFire", StringComparison.OrdinalIgnoreCase) ||
                        path.Contains("Exarch", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Exarch", StringComparison.OrdinalIgnoreCase))
                        return entity;

                    // The boss arena normally exposes just one hostile Unique. Keep
                    // it as a final compatibility fallback if the metadata changes.
                    soleUnique ??= entity;
                }
                return soleUnique;
            }
            catch (IndexOutOfRangeException) { }
            return null;
        }

        private static bool IsInvulnerablePhase(Entity? boss)
        {
            if (boss == null || !boss.IsAlive)
                return false;
            if (!boss.IsTargetable)
                return true;

            // Targetability remains true for some Exarch ball transitions. The
            // state machine's life-bar flag is the reliable combat-side signal:
            // normal DPS is allowed only while it is positive. Also accept any
            // explicitly named ball/invulnerability state for metadata variants.
            if (boss.TryGetComponent<StateMachine>(out var stateMachine) && stateMachine?.States != null)
            {
                foreach (var state in stateMachine.States)
                {
                    var name = state.Name ?? string.Empty;
                    if (name.Equals("boss_life_bar", StringComparison.OrdinalIgnoreCase) && state.Value <= 0)
                        return true;
                    if (state.Value > 0 &&
                        (name.Contains("ball", StringComparison.OrdinalIgnoreCase) ||
                         name.Contains("invulner", StringComparison.OrdinalIgnoreCase)))
                        return true;
                }
            }

            return false;
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
            _isInvulnerablePhase = false;
            _combatPositionLocked = false;
            Status = "";
        }
    }
}
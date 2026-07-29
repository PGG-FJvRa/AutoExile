using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using System.Collections.Generic;
using System.Numerics;

namespace AutoExile.Systems
{
    /// <summary>
    /// Sells surplus currency for Chaos on the Faustus Currency Exchange.
    ///
    /// Flow: walk to Faustus → open Currency Exchange → scan the "I Have" picker to build a queue
    /// of currencies whose stack value (owned qty × ninja unit chaos) exceeds the threshold and
    /// aren't excluded → for each (up to the per-run cap): I-Have = that currency, I-Want = Chaos,
    /// place order at the exchange's default quantity/market rate.
    ///
    /// v1: places at the exchange's auto-filled quantity/rate (does NOT yet force the full stack).
    /// Reuses the interaction pattern proven in FaustusSystem.
    /// </summary>
    public class SellExchangeSystem
    {
        private const string FaustusPath = "Metadata/NPC/League/Kalguur/VillageFaustusHideout";
        private const int ClickCooldownMs = 400;
        private const float StateTimeoutSeconds = 12f;
        private const string WantCurrencyBaseName = "Chaos Orb";

        private static readonly NinjaPriceCategory[] EligibleCats =
        {
            NinjaPriceCategory.Currency, NinjaPriceCategory.Fragment, NinjaPriceCategory.Scarab,
            NinjaPriceCategory.Essence, NinjaPriceCategory.Oil, NinjaPriceCategory.Fossil,
            NinjaPriceCategory.Resonator, NinjaPriceCategory.DeliriumOrb, NinjaPriceCategory.Artifact,
            NinjaPriceCategory.Omen, NinjaPriceCategory.KalguuranRune, NinjaPriceCategory.AllflameEmber,
            NinjaPriceCategory.DjinnCoin, NinjaPriceCategory.Astrolabe,
        };

        private SellState _state = SellState.Idle;
        private DateTime _stateEnteredAt = DateTime.MinValue;
        private DateTime _lastClickAt = DateTime.MinValue;

        private readonly Queue<string> _queue = new(); // base names still to sell
        private string _current = "";
        private int _ordersPlaced;
        private int _maxOrders = 3;
        private double _thresholdChaos = 50.0;
        private HashSet<string> _exclusions = new(StringComparer.OrdinalIgnoreCase);
        private bool _havePicked;
        private bool _wantPicked;

        public bool IsBusy => _state != SellState.Idle;
        public string Status { get; private set; } = "";
        public int OrdersPlaced => _ordersPlaced;

        /// <summary>Begin a sell run. Candidates are computed from the exchange once the panel opens.</summary>
        public void Start(int maxOrders, double thresholdChaos, HashSet<string> exclusions)
        {
            _queue.Clear();
            _current = "";
            _ordersPlaced = 0;
            _maxOrders = System.Math.Max(1, maxOrders);
            _thresholdChaos = thresholdChaos;
            _exclusions = exclusions ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _havePicked = false;
            _wantPicked = false;
            SetState(SellState.WalkingToFaustus);
        }

        public void Cancel(GameController gc = null, NavigationSystem nav = null)
        {
            if (gc != null && nav != null) nav.Stop(gc);
            _queue.Clear();
            SetState(SellState.Idle);
        }

        public void Tick(BotContext ctx)
        {
            if (_state == SellState.Idle) return;

            if ((DateTime.Now - _stateEnteredAt).TotalSeconds > StateTimeoutSeconds)
            {
                Status = $"Sell: timeout in {_state}";
                Cancel(ctx.Game, ctx.Navigation);
                return;
            }

            switch (_state)
            {
                case SellState.WalkingToFaustus: TickWalk(ctx); break;
                case SellState.WaitingForDialog: TickWaitDialog(ctx); break;
                case SellState.ClickingExchange: TickClickExchange(ctx); break;
                case SellState.WaitingForPanel: TickWaitPanel(ctx); break;
                case SellState.ScanCandidates: TickScan(ctx); break;
                case SellState.PickingHave: TickPickHave(ctx); break;
                case SellState.PickingWant: TickPickWant(ctx); break;
                case SellState.PlacingOrder: TickPlaceOrder(ctx); break;
            }
        }

        // ── Navigation (mirrors FaustusSystem) ──

        private void TickWalk(BotContext ctx)
        {
            var gc = ctx.Game;
            var dialog = gc.IngameState.IngameUi.NpcDialog;
            if (dialog != null && dialog.IsVisible) { SetState(SellState.ClickingExchange); return; }

            var faustus = FindFaustus(gc);
            if (faustus == null) { Status = "Sell: Faustus not found"; return; }

            if (ctx.Interaction.IsBusy)
            {
                var r = ctx.Interaction.Tick(gc);
                Status = $"Sell: walking to Faustus ({ctx.Interaction.Status})";
                if (r == InteractionResult.Succeeded || r == InteractionResult.Failed)
                    SetState(SellState.WaitingForDialog);
                return;
            }
            ctx.Interaction.InteractWithEntity(faustus, ctx.Navigation, requireProximity: true);
            Status = "Sell: interacting with Faustus";
        }

        private void TickWaitDialog(BotContext ctx)
        {
            var gc = ctx.Game;
            if (ctx.Interaction.IsBusy)
            {
                var r = ctx.Interaction.Tick(gc);
                if (r == InteractionResult.Failed) { Status = "Sell: interaction failed"; Cancel(gc, ctx.Navigation); return; }
            }
            var dialog = gc.IngameState.IngameUi.NpcDialog;
            if (dialog == null || !dialog.IsVisible) { Status = "Sell: waiting for dialog"; return; }
            SetState(SellState.ClickingExchange);
        }

        private void TickClickExchange(BotContext ctx)
        {
            var gc = ctx.Game;
            var dialog = gc.IngameState.IngameUi.NpcDialog;
            if (dialog == null || !dialog.IsVisible) { Status = "Sell: dialog closed"; Cancel(gc, ctx.Navigation); return; }
            if (!CanClick()) return;

            var lines = dialog.NpcLines;
            if (lines == null || lines.Count == 0) { Status = "Sell: no dialog lines"; return; }

            foreach (var line in lines)
            {
                if (line?.Text?.Contains("Continue", StringComparison.OrdinalIgnoreCase) == true)
                { Status = "Sell: clicking Continue"; ClickElement(gc, line.Element); return; }
            }
            foreach (var line in lines)
            {
                if (line?.Text?.Contains("Exchange", StringComparison.OrdinalIgnoreCase) == true)
                { Status = "Sell: clicking Currency Exchange"; ClickElement(gc, line.Element); SetState(SellState.WaitingForPanel); return; }
            }
            Status = "Sell: looking for Currency Exchange option";
        }

        private void TickWaitPanel(BotContext ctx)
        {
            var gc = ctx.Game;
            var panel = gc.IngameState.IngameUi.CurrencyExchangePanel;
            if (panel != null && panel.IsVisible) { SetState(SellState.ScanCandidates); return; }
            if ((DateTime.Now - _stateEnteredAt).TotalSeconds < 2.0) { Status = "Sell: waiting for exchange panel"; return; }
            var dialog = gc.IngameState.IngameUi.NpcDialog;
            if (dialog != null && dialog.IsVisible) { SetState(SellState.ClickingExchange); return; }
            Status = "Sell: waiting for exchange panel";
        }

        // ── Candidate scan: open the I-Have picker, read owned qty × ninja price ──

        private void TickScan(BotContext ctx)
        {
            var gc = ctx.Game;
            var panel = gc.IngameState.IngameUi.CurrencyExchangePanel;
            if (panel == null || !panel.IsVisible) { Status = "Sell: panel closed"; Cancel(gc, ctx.Navigation); return; }

            var picker = panel.CurrencyPicker;
            // Need the I-Have picker open to read owned quantities.
            if (picker == null || !picker.IsVisible)
            {
                if (!CanClick()) return;
                ClickChild(gc, panel, 10, 0); // I-Have button
                Status = "Sell: opening I Have picker to scan";
                return;
            }
            if (picker.IsPickingWantedCurrency) { Status = "Sell: wrong picker open"; return; }

            // Build the queue from the picker options.
            var rows = new List<(string name, double total)>();
            try
            {
                foreach (var opt in picker.Options)
                {
                    string name = null;
                    try { name = (string)opt.ItemType.BaseName; } catch { }
                    if (string.IsNullOrEmpty(name) || _exclusions.Contains(name)) continue;
                    int qty = ReadPickerQty((ExileCore.PoEMemory.Element)opt);
                    if (qty <= 0) continue;
                    double unit = UnitChaos(ctx, name);
                    if (unit <= 0.0) continue;
                    double total = qty * unit;
                    if (total >= _thresholdChaos) rows.Add((name, total));
                }
            }
            catch { }

            rows.Sort((a, b) => b.total.CompareTo(a.total));
            _queue.Clear();
            int added = 0;
            foreach (var r in rows)
            {
                if (added >= _maxOrders) break;
                _queue.Enqueue(r.name);
                added++;
            }

            if (_queue.Count == 0) { Status = "Sell: no candidates over threshold"; SetState(SellState.Idle); return; }

            _current = _queue.Dequeue();
            _havePicked = false;
            _wantPicked = false;
            Status = $"Sell: queued {added} orders; first = {_current}";
            SetState(SellState.PickingHave);
        }

        // ── Per-candidate: pick I-Have currency, pick I-Want Chaos, place order ──

        private void TickPickHave(BotContext ctx)
        {
            var gc = ctx.Game;
            var panel = gc.IngameState.IngameUi.CurrencyExchangePanel;
            if (panel == null || !panel.IsVisible) { Status = "Sell: panel closed"; Cancel(gc, ctx.Navigation); return; }
            var picker = panel.CurrencyPicker;

            if (_havePicked)
            {
                if (picker != null && picker.IsVisible) { Status = "Sell: waiting I Have picker close"; return; }
                SetState(SellState.PickingWant); return;
            }

            if (picker != null && picker.IsVisible && !picker.IsPickingWantedCurrency)
            {
                var option = FindPickerOption(picker, null, _current);
                if (option == null) { Status = $"Sell: {_current} not in I Have picker"; return; }
                if (!CanClick()) return;
                ClickRect(gc, option);
                _havePicked = true;
                Status = $"Sell: selected I Have = {_current}";
                return;
            }

            if (!CanClick()) return;
            ClickChild(gc, panel, 10, 0); // I-Have button
            Status = "Sell: opening I Have picker";
        }

        private void TickPickWant(BotContext ctx)
        {
            var gc = ctx.Game;
            var panel = gc.IngameState.IngameUi.CurrencyExchangePanel;
            if (panel == null || !panel.IsVisible) { Status = "Sell: panel closed"; Cancel(gc, ctx.Navigation); return; }
            var picker = panel.CurrencyPicker;

            if (_wantPicked)
            {
                if (picker != null && picker.IsVisible) { Status = "Sell: waiting I Want picker close"; return; }
                SetState(SellState.PlacingOrder); return;
            }

            if (picker != null && picker.IsVisible && picker.IsPickingWantedCurrency)
            {
                var option = FindPickerOption(picker, null, WantCurrencyBaseName);
                if (option == null) { Status = "Sell: Chaos not in I Want picker"; return; }
                if (!CanClick()) return;
                ClickRect(gc, option);
                _wantPicked = true;
                Status = "Sell: selected I Want = Chaos";
                return;
            }

            if (!CanClick()) return;
            ClickChild(gc, panel, 7, 0); // I-Want button
            Status = "Sell: opening I Want picker";
        }

        private void TickPlaceOrder(BotContext ctx)
        {
            var gc = ctx.Game;
            var panel = gc.IngameState.IngameUi.CurrencyExchangePanel;
            if (panel == null || !panel.IsVisible) { Status = "Sell: panel closed"; Cancel(gc, ctx.Navigation); return; }
            if (!CanClick()) return;

            // NOTE v1: quantity/rate left at the exchange's auto-filled defaults. Full-stack quantity
            // control is a follow-up once its element is mapped.
            ClickChild(gc, panel, 16, 0); // place order
            _ordersPlaced++;
            Status = $"Sell: placed order {_ordersPlaced} ({_current})";

            if (_ordersPlaced >= _maxOrders || _queue.Count == 0)
            { SetState(SellState.Idle); Status = $"Sell: done, {_ordersPlaced} orders placed"; return; }

            _current = _queue.Dequeue();
            _havePicked = false;
            _wantPicked = false;
            SetState(SellState.PickingHave);
        }

        // ── Helpers (mirror FaustusSystem) ──

        private void SetState(SellState s) { _state = s; _stateEnteredAt = DateTime.Now; }
        private bool CanClick() => (DateTime.Now - _lastClickAt).TotalMilliseconds >= ClickCooldownMs && BotInput.CanAct;

        private void ClickChild(GameController gc, dynamic panel, int i, int j)
        {
            try
            {
                var el = panel.GetChildAtIndex(i)?.GetChildAtIndex(j);
                if (el != null && el.IsVisible) { ClickElement(gc, (ExileCore.PoEMemory.Element)el); }
            }
            catch { }
        }

        private void ClickRect(GameController gc, ExileCore.PoEMemory.Element el)
        {
            try
            {
                var rect = el.GetClientRect();
                var center = new Vector2(rect.X + rect.Width / 2f, rect.Y + rect.Height / 2f);
                BotInput.Click(ToAbsolutePos(gc, center));
                _lastClickAt = DateTime.Now;
            }
            catch { }
        }

        private void ClickElement(GameController gc, ExileCore.PoEMemory.Element element)
        {
            try
            {
                var rect = element.GetClientRect();
                var center = new Vector2(rect.X + rect.Width / 2f, rect.Y + rect.Height / 2f);
                BotInput.Click(ToAbsolutePos(gc, center));
                _lastClickAt = DateTime.Now;
            }
            catch { }
        }

        private static Vector2 ToAbsolutePos(GameController gc, Vector2 clientPos)
        {
            var wr = gc.Window.GetWindowRectangle();
            return new Vector2(wr.X + clientPos.X, wr.Y + clientPos.Y);
        }

        private double UnitChaos(BotContext ctx, string name)
        {
            foreach (var cat in EligibleCats)
            {
                var pr = ctx.NinjaPrice.GetPrice(name, cat);
                if (pr.MaxChaosValue > 0.0) return pr.MaxChaosValue;
            }
            return 0.0;
        }

        private static int ReadPickerQty(ExileCore.PoEMemory.Element opt)
        {
            try
            {
                var t = opt.GetChildAtIndex(1)?.GetChildAtIndex(0)?.Text;
                if (string.IsNullOrWhiteSpace(t)) return 0;
                t = t.Trim().Replace(",", "");
                double mult = 1;
                if (t.EndsWith("K", StringComparison.OrdinalIgnoreCase)) { mult = 1000; t = t[..^1]; }
                else if (t.EndsWith("M", StringComparison.OrdinalIgnoreCase)) { mult = 1_000_000; t = t[..^1]; }
                return double.TryParse(t, out var v) ? (int)(v * mult) : 0;
            }
            catch { return 0; }
        }

        private static Entity FindFaustus(GameController gc)
        {
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
                if (entity.Path?.Contains(FaustusPath, StringComparison.OrdinalIgnoreCase) == true)
                    return entity;
            return null;
        }

        private static ExileCore.PoEMemory.Element FindPickerOption(dynamic picker, string metaSubstring, string baseName)
        {
            try
            {
                foreach (var option in picker.Options)
                {
                    if (option == null) continue;
                    var itemType = option.ItemType;
                    if (itemType == null) continue;
                    string meta = itemType.Metadata;
                    string bname = itemType.BaseName;
                    if (metaSubstring != null)
                    {
                        if (meta?.Contains(metaSubstring, StringComparison.OrdinalIgnoreCase) == true)
                            return (ExileCore.PoEMemory.Element)option;
                    }
                    else if (baseName != null)
                    {
                        if (bname?.Equals(baseName, StringComparison.OrdinalIgnoreCase) == true)
                            return (ExileCore.PoEMemory.Element)option;
                    }
                }
            }
            catch { }
            return null;
        }
    }

    internal enum SellState
    {
        Idle,
        WalkingToFaustus,
        WaitingForDialog,
        ClickingExchange,
        WaitingForPanel,
        ScanCandidates,
        PickingHave,
        PickingWant,
        PlacingOrder,
    }
}

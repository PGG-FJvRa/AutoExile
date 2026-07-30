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

        private readonly Queue<(string name, int qty)> _queue = new(); // still to sell
        private string _current = "";
        private int _currentQty; // full owned stack of _current, to list for sale
        private int _ordersPlaced;
        private int _maxOrders = 3;
        private double _thresholdChaos = 50.0;
        private HashSet<string> _exclusions = new(StringComparer.OrdinalIgnoreCase);
        private bool _havePicked;
        private bool _wantPicked;
        private bool _ownedFilter; // = finished typing the search filter for this candidate
        private bool _lockedWant;
        private bool _lockedHave;
        private string _searchText = "";
        private int _searchIndex;
        private bool _ownedClicked;
        private bool _searchFocused;
        private DateTime _searchFocusedAt;
        private bool _searchCleared;
        private DateTime _lastTypeAt = DateTime.MinValue;
        private const int TypeIntervalMs = 280;
        private bool _amountFocused;
        private bool _amountCleared;
        private string _amountText = "";
        private int _amountIndex;
        private bool _wantFocused;
        private bool _wantCleared;
        private bool _wantTyped;
        private bool _wantFinalized;

        public bool IsBusy => _state != SellState.Idle;
        public string Status { get; private set; } = "";
        public int OrdersPlaced => _ordersPlaced;

        private readonly List<string> _log = new();
        public IReadOnlyList<string> Log => _log;
        private void AddLog(string m)
        {
            _log.Add($"{DateTime.Now:HH:mm:ss} {m}");
            if (_log.Count > 12) _log.RemoveAt(0);
        }

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
            _log.Clear();
            AddLog($"start (max={_maxOrders}, thr={_thresholdChaos:F0}c)");
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
                AddLog($"TIMEOUT in {_state}");
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
                case SellState.LockingAmounts: TickLockAmounts(ctx); break;
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

            // Wait for the option list to actually populate before concluding anything —
            // reading on the first visible frame yields 0 and would wrongly report "no candidates".
            int optCount = 0;
            try { foreach (var _ in picker.Options) optCount++; } catch { }
            if (optCount == 0) { Status = "Sell: waiting for I Have options to load…"; return; }

            // Build the queue from the picker options.
            var rows = new List<(string name, int qty, double total)>();
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
                    if (total >= _thresholdChaos) rows.Add((name, qty, total));
                }
            }
            catch { }

            rows.Sort((a, b) => b.total.CompareTo(a.total));
            _queue.Clear();
            int added = 0;
            foreach (var r in rows)
            {
                if (added >= _maxOrders) break;
                _queue.Enqueue((r.name, r.qty));
                added++;
            }

            AddLog($"scan opts={optCount} cands={rows.Count} queued={added}");
            if (_queue.Count == 0) { Status = $"Sell: no candidates (opts={optCount})"; SetState(SellState.Idle); return; }

            var (cn, cq) = _queue.Dequeue();
            _current = cn;
            _currentQty = cq;
            _havePicked = false;
            _wantPicked = false;
            _ownedFilter = false;
            _lockedWant = false;
            _lockedHave = false;
            _searchText = SearchPrefix(_current);
            _searchIndex = 0;
            _ownedClicked = false;
            _searchFocused = false;
            _searchCleared = false;
            _amountFocused = false;
            _amountCleared = false;
            _amountText = _currentQty.ToString();
            _amountIndex = 0;
            _wantFocused = false;
            _wantCleared = false;
            _wantTyped = false;
            _wantFinalized = false;
            Status = $"Sell: queued {added}/{optCount}; first = {_current}";
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
                // Proceed after a short wait even if the picker lingers open (don't hang).
                if (picker != null && picker.IsVisible && (DateTime.Now - _lastClickAt).TotalMilliseconds < 2000)
                { Status = "Sell: waiting I Have picker close"; return; }
                SetState(SellState.PickingWant); return;
            }

            if (picker != null && picker.IsVisible && !picker.IsPickingWantedCurrency)
            {
                // Type the currency name into the picker's (auto-focused) search box to filter the
                // 1000+ item list down to the match, which then appears at the top / on-screen.
                // 1) Switch to the "Owned" category first.
                if (!_ownedClicked)
                {
                    if (!CanClick()) return;
                    var ownedTab = FindElementByText((ExileCore.PoEMemory.Element)panel, "Owned", 0);
                    _ownedClicked = true;
                    if (ownedTab != null) { ClickRect(gc, ownedTab); AddLog("clicked Owned"); }
                    else AddLog("Owned tab not found");
                    return;
                }

                // 2) Focus the search box (its "Select currency" placeholder). SAFETY: never type
                // unless it was found+clicked — otherwise keys leak to the game (movement/skills).
                if (!_searchFocused)
                {
                    if (!CanClick()) return;
                    var box = FindElementContaining((ExileCore.PoEMemory.Element)panel, "select currency", 0);
                    if (box != null)
                    {
                        ClickRect(gc, box);
                        AddLog("focus search (placeholder)");
                    }
                    else
                    {
                        // Leftover text hides the "Select currency" placeholder — focus the search
                        // box by position (bottom-centre of the picker) so it works regardless.
                        var pr = ((ExileCore.PoEMemory.Element)picker).GetClientRect();
                        var pos = new Vector2(pr.X + pr.Width * 0.5f, pr.Y + pr.Height - 18f);
                        BotInput.Click(ToAbsolutePos(gc, pos));
                        _lastClickAt = DateTime.Now;
                        AddLog($"focus search (pos {pos.X:F0},{pos.Y:F0})");
                    }
                    _searchFocused = true;
                    _searchFocusedAt = DateTime.Now;
                    return;
                }

                // 3) Select-all to clear any leftover text, then type the name one key at a time.
                if (!_ownedFilter)
                {
                    if (!_searchCleared)
                    {
                        // Wait a full second after focusing so the search box keeps focus.
                        if ((DateTime.Now - _searchFocusedAt).TotalMilliseconds < 1000)
                        { Status = "Sell: settling on search box"; return; }
                        BotInput.SelectAll();
                        _searchCleared = true;
                        _lastTypeAt = DateTime.Now;
                        return;
                    }
                    if (_searchIndex < _searchText.Length)
                    {
                        if ((DateTime.Now - _lastTypeAt).TotalMilliseconds < TypeIntervalMs) return;
                        if (!BotInput.CanAct) return;
                        var k = CharToKey(_searchText[_searchIndex]);
                        if (k != System.Windows.Forms.Keys.None) BotInput.PressKey(k);
                        _lastTypeAt = DateTime.Now;
                        if (_searchIndex == 0) AddLog($"typing '{_searchText}'");
                        _searchIndex++;
                        return;
                    }
                    _ownedFilter = true;         // filter typed
                    _lastClickAt = DateTime.Now; // settle for the list to filter
                    return;
                }

                // 4) Click the filtered result — topmost on-screen match in the picker grid.
                var cell = FindTopmostVisible((ExileCore.PoEMemory.Element)picker, _current, 100f, 2000f, 0);
                if (cell == null) { Status = $"Sell: {_current} not visible after filter"; return; }
                if (!CanClick()) return;
                AddLog($"have {_current} y={cell.GetClientRect().Y:F0}");
                ClickRect(gc, cell);
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
                if (picker != null && picker.IsVisible && (DateTime.Now - _lastClickAt).TotalMilliseconds < 2000)
                { Status = "Sell: waiting I Want picker close"; return; }
                SetState(SellState.LockingAmounts); return;
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

        private void TickLockAmounts(BotContext ctx)
        {
            var gc = ctx.Game;
            var panel = gc.IngameState.IngameUi.CurrencyExchangePanel;
            if (panel == null || !panel.IsVisible) { Status = "Sell: panel closed"; Cancel(gc, ctx.Navigation); return; }
            // Set the I-Have amount to the full owned stack: focus the amount box (panel child 8),
            // select-all, then type the quantity. The I-Want (Chaos) amount auto-computes from the
            // market ratio.
            if (!_amountFocused)
            {
                if (!CanClick()) return;
                // Confirm child 8 really is the (numeric) amount box before clicking/typing, so we
                // never type digits into the game (number keys are flask slots!).
                var c8 = SafeChildText(panel, 8);
                if (!IsNumericAmount(c8))
                {
                    AddLog($"abort: amount box c8='{c8}' not numeric");
                    Status = "Sell: amount box not found — aborting (not typing)";
                    Cancel(gc, ctx.Navigation);
                    return;
                }
                AddLog($"amt box c8='{c8}' -> {_amountText}");
                ClickChildSingle(gc, panel, 8);
                _amountFocused = true;
                _lastTypeAt = DateTime.Now;
                Status = "Sell: focusing amount box";
                return;
            }
            if (!_amountCleared)
            {
                if ((DateTime.Now - _lastTypeAt).TotalMilliseconds < TypeIntervalMs) return;
                BotInput.SelectAll();
                _amountCleared = true;
                _lastTypeAt = DateTime.Now;
                return;
            }
            if (_amountIndex < _amountText.Length)
            {
                if ((DateTime.Now - _lastTypeAt).TotalMilliseconds < TypeIntervalMs) return;
                if (!BotInput.CanAct) return;
                var k = CharToKey(_amountText[_amountIndex]);
                if (k != System.Windows.Forms.Keys.None) BotInput.PressKey(k);
                _lastTypeAt = DateTime.Now;
                if (_amountIndex == 0) AddLog($"typing amount {_amountText}");
                _amountIndex++;
                return;
            }

            // I-Have stack is set. Now type 0 into the I-Want (Chaos) amount box (child 5): the
            // exchange auto-computes a valid ratio + amount, which un-greys Place Order (this is the
            // manual flow — typing an amount then 0 on the other side).
            if (!_wantFocused)
            {
                if (!CanClick()) return;
                var c5 = SafeChildText(panel, 5);
                if (!IsNumericAmount(c5))
                {
                    AddLog($"abort: I-Want box c5='{c5}' not numeric");
                    Status = "Sell: I-Want box not found — aborting";
                    Cancel(gc, ctx.Navigation);
                    return;
                }
                AddLog($"want box c5='{c5}' -> 0");
                ClickChildSingle(gc, panel, 5);
                _wantFocused = true;
                _lastTypeAt = DateTime.Now;
                return;
            }
            if (!_wantCleared)
            {
                if ((DateTime.Now - _lastTypeAt).TotalMilliseconds < TypeIntervalMs) return;
                BotInput.SelectAll();
                _wantCleared = true;
                _lastTypeAt = DateTime.Now;
                return;
            }
            if (!_wantTyped)
            {
                if ((DateTime.Now - _lastTypeAt).TotalMilliseconds < TypeIntervalMs) return;
                if (!BotInput.CanAct) return;
                BotInput.PressKey(System.Windows.Forms.Keys.D0); // type "0"
                _wantTyped = true;
                _lastTypeAt = DateTime.Now;
                AddLog("typed 0 into I-Want");
                return;
            }
            // Give the exchange a moment to recompute the ratio.
            if ((DateTime.Now - _lastTypeAt).TotalMilliseconds < 500) return;

            // Click the Chaos amount box once more to finalize the sell amount before placing.
            if (!_wantFinalized)
            {
                if (!CanClick()) return;
                ClickChildSingle(gc, panel, 5);
                _wantFinalized = true;
                AddLog("finalized Chaos amount");
                return;
            }
            SetState(SellState.PlacingOrder);
        }

        private void TickPlaceOrder(BotContext ctx)
        {
            var gc = ctx.Game;
            var panel = gc.IngameState.IngameUi.CurrencyExchangePanel;
            if (panel == null || !panel.IsVisible) { Status = "Sell: panel closed"; Cancel(gc, ctx.Navigation); return; }
            if (!CanClick()) return;

            // Amount is now set to the full stack; rate is the exchange's market fill. Place it.
            ClickChild(gc, panel, 16, 0); // place order
            _ordersPlaced++;
            AddLog($"placed #{_ordersPlaced} {_current}");
            Status = $"Sell: placed order {_ordersPlaced} ({_current})";

            if (_ordersPlaced >= _maxOrders || _queue.Count == 0)
            { SetState(SellState.Idle); Status = $"Sell: done, {_ordersPlaced} orders placed"; return; }

            var (cn, cq) = _queue.Dequeue();
            _current = cn;
            _currentQty = cq;
            _havePicked = false;
            _wantPicked = false;
            _ownedFilter = false;
            _lockedWant = false;
            _lockedHave = false;
            _searchText = SearchPrefix(_current);
            _searchIndex = 0;
            _ownedClicked = false;
            _searchFocused = false;
            _searchCleared = false;
            _amountFocused = false;
            _amountCleared = false;
            _amountText = _currentQty.ToString();
            _amountIndex = 0;
            _wantFocused = false;
            _wantCleared = false;
            _wantTyped = false;
            _wantFinalized = false;
            SetState(SellState.PickingHave);
        }

        // ── Helpers (mirror FaustusSystem) ──

        private void SetState(SellState s) { _state = s; _stateEnteredAt = DateTime.Now; AddLog($"-> {s}"); }
        private bool CanClick() => (DateTime.Now - _lastClickAt).TotalMilliseconds >= ClickCooldownMs && BotInput.CanAct;

        private void ClickChildSingle(GameController gc, dynamic panel, int i)
        {
            try { var el = panel.GetChildAtIndex(i); if (el != null && el.IsVisible) ClickElement(gc, (ExileCore.PoEMemory.Element)el); }
            catch { }
        }

        // A plausible amount field: has at least one digit and only digit/separator characters.
        private static bool IsNumericAmount(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            bool hasDigit = false;
            foreach (var c in s)
            {
                if (char.IsDigit(c)) hasDigit = true;
                else if (c == ',' || c == '.' || c == ' ' || c == 'K' || c == 'k' || c == 'M' || c == 'm') continue;
                else return false;
            }
            return hasDigit;
        }

        private static string SafeChildText(dynamic panel, int i)
        {
            try { var e = panel.GetChildAtIndex(i); return e == null ? "" : (string)e.Text ?? ""; }
            catch { return "?"; }
        }

        // Find the first element whose (normalized) text contains 'name' and whose rect is within
        // the visible [minY, maxY] band — i.e. the on-screen picker cell, not an off-screen data row.
        private static ExileCore.PoEMemory.Element FindVisibleByText(ExileCore.PoEMemory.Element root, string name, float minY, float maxY, int depth)
        {
            if (root == null || depth > 16) return null;
            try
            {
                var t = root.Text;
                if (!string.IsNullOrEmpty(t) && Norm(t).Contains(Norm(name)))
                {
                    var r = root.GetClientRect();
                    if (r.Y >= minY && r.Y <= maxY) return root;
                }
                var kids = root.Children;
                if (kids != null)
                    for (int i = 0; i < kids.Count; i++)
                    {
                        var f = FindVisibleByText(kids[i], name, minY, maxY, depth + 1);
                        if (f != null) return f;
                    }
            }
            catch { }
            return null;
        }

        // Typeable prefix for the search box: letters/digits/spaces up to the first punctuation
        // (e.g. an apostrophe), so it stays a valid substring of the display name.
        // "Dead Man's Sulphur" -> "dead man"; "Orb of Annulment" -> "orb of annulment".
        private static System.Windows.Forms.Keys CharToKey(char ch)
        {
            char c = char.ToLowerInvariant(ch);
            if (c >= 'a' && c <= 'z') return (System.Windows.Forms.Keys)((int)System.Windows.Forms.Keys.A + (c - 'a'));
            if (c >= '0' && c <= '9') return (System.Windows.Forms.Keys)((int)System.Windows.Forms.Keys.D0 + (c - '0'));
            if (c == ' ') return System.Windows.Forms.Keys.Space;
            return System.Windows.Forms.Keys.None;
        }

        private static string SearchPrefix(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var sb = new System.Text.StringBuilder();
            foreach (var c in name)
            {
                if (char.IsLetterOrDigit(c) || c == ' ') sb.Append(char.ToLowerInvariant(c));
                else break;
            }
            var s = sb.ToString().Trim();
            return s.Length > 0 ? s : name;
        }

        // Among all descendants whose normalized text contains 'name' with rect.Y in [minY, maxY],
        // return the topmost (smallest Y) — the on-screen grid cell, not off-screen data rows.
        private static ExileCore.PoEMemory.Element FindTopmostVisible(ExileCore.PoEMemory.Element root, string name, float minY, float maxY, int depth)
        {
            if (root == null || depth > 18) return null;
            ExileCore.PoEMemory.Element best = null;
            float bestY = float.MaxValue;
            try
            {
                var t = root.Text;
                if (!string.IsNullOrEmpty(t) && Norm(t).Contains(Norm(name)))
                {
                    var r = root.GetClientRect();
                    if (r.Y >= minY && r.Y <= maxY) { best = root; bestY = r.Y; }
                }
                var kids = root.Children;
                if (kids != null)
                    for (int i = 0; i < kids.Count; i++)
                    {
                        var f = FindTopmostVisible(kids[i], name, minY, maxY, depth + 1);
                        if (f != null) { var fy = f.GetClientRect().Y; if (fy < bestY) { best = f; bestY = fy; } }
                    }
            }
            catch { }
            return best;
        }

        // Lowercase, keep letters/digits, collapse whitespace, drop punctuation (apostrophes etc.)
        // so "Dead Man's\nSulphur" and "Dead Man's Sulphur" compare equal.
        private static string Norm(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder();
            bool lastSpace = true;
            foreach (var c in s)
            {
                if (char.IsLetterOrDigit(c)) { sb.Append(char.ToLowerInvariant(c)); lastSpace = false; }
                else if (char.IsWhiteSpace(c)) { if (!lastSpace) { sb.Append(' '); lastSpace = true; } }
            }
            return sb.ToString().Trim();
        }

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

        // Recursively find the first element whose normalized text contains 'needle'.
        private static ExileCore.PoEMemory.Element FindElementContaining(ExileCore.PoEMemory.Element root, string needle, int depth)
        {
            if (root == null || depth > 16) return null;
            try
            {
                var t = root.Text;
                if (!string.IsNullOrEmpty(t) && Norm(t).Contains(Norm(needle))) return root;
                var kids = root.Children;
                if (kids != null)
                    for (int i = 0; i < kids.Count; i++)
                    {
                        var f = FindElementContaining(kids[i], needle, depth + 1);
                        if (f != null) return f;
                    }
            }
            catch { }
            return null;
        }

        // Recursively find the first element whose trimmed text matches (case-insensitive).
        private static ExileCore.PoEMemory.Element FindElementByText(ExileCore.PoEMemory.Element root, string text, int depth)
        {
            if (root == null || depth > 12) return null;
            try
            {
                var t = root.Text;
                if (!string.IsNullOrEmpty(t) && t.Trim().Equals(text, StringComparison.OrdinalIgnoreCase))
                    return root;
                var kids = root.Children;
                if (kids != null)
                    for (int i = 0; i < kids.Count; i++)
                    {
                        var f = FindElementByText(kids[i], text, depth + 1);
                        if (f != null) return f;
                    }
            }
            catch { }
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
        LockingAmounts,
        PlacingOrder,
    }
}

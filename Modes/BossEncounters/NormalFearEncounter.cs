namespace AutoExile.Modes.BossEncounters
{
    /// <summary>
    /// Standard Incarnation of Fear encounter. It uses the same Moment of Trauma
    /// arena and strategy as the Uber encounter, but opens with the normal key and
    /// targets the non-Uber boss entity.
    /// </summary>
    public sealed class NormalFearEncounter : FearEncounter
    {
        public override string Name => "Incarnation of Fear (Normal)";
        protected override string FragmentPath => "CurrencyBossKeyAnger";
        protected override string BossPath => "AngerBoss@";
        public override int FragmentCost => 1;
    }
}
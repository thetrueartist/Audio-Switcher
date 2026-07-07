namespace AudioSwitcher
{
    public enum SilenceAction
    {
        Bump,                    // higher-safety tiers remain -> drop one and try again
        GiveUpNotRateRelated,    // silent even at the lowest rate set BEFORE audio init -> rate isn't the cause
        StayPinned,              // at the floor but we couldn't pre-set cleanly -> can't conclude; leave as-is
    }

    // Decides what to do when a game comes up silent, from where it sits on the tier ladder and
    // whether we set its format BEFORE it opened its audio device (a "clean pre-set" - only
    // guaranteed when we froze it at kernel-speed detection). The key insight: if the LOWEST rate
    // was applied before the game touched audio and it's STILL silent, lowering can't be the fix,
    // so we should stop dragging it to the worst quality and flag it instead. Pure + unit-tested.
    public static class SilencePolicy
    {
        public static SilenceAction Decide(int currentTier, int lowestTier, bool cleanPreset)
        {
            if (currentTier < lowestTier) return SilenceAction.Bump;        // still lower rates to try
            return cleanPreset ? SilenceAction.GiveUpNotRateRelated         // floor + clean pre-set + silent
                               : SilenceAction.StayPinned;                  // floor but no clean pre-set
        }
    }
}

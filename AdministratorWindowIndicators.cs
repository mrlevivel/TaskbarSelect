using System;

namespace TaskbarSelect
{
    // The authenticated helper owns both surfaces for its confirmed HIGH pins.
    // A pin ledger is necessary but does not prove that a corner was published.
    internal sealed class AdministratorWindowIndicators : IDisposable
    {
        readonly FocusIndicator focus;
        readonly PinnedWindowIndicator pins;
        readonly ReservedPinFocus confirmed;
        bool disposed;

        internal AdministratorWindowIndicators(FocusIndicator focus, PinnedWindowIndicator pins, ReservedPinFocus confirmed)
        {
            if (focus == null || pins == null || confirmed == null) throw new ArgumentNullException("indicators");
            this.focus = focus; this.pins = pins; this.confirmed = confirmed;
        }
        internal void Update(int[] choices)
        {
            if (disposed) return;
            if (choices == null || !AdministratorSupport.ValidFocus(Array.ConvertAll(choices, value => (long)value))) {
                Hide(); return;
            }
            string accent = FocusAppearance.Presets[choices[1]];
            // Publish the corner first. Failed, hidden or settling corners
            // cannot suppress the focus frame's lighting at this target.
            pins.Update(confirmed.Observations, choices[6] != 0, null, accent);
            focus.Update(choices[0], accent, choices[2] != 0, choices[3] != 0, choices[4], choices[5],
                delegate(FocusedWindowSnapshot target) { return confirmed.Matches(target) && pins.ReservesFocusCorner(target); });
        }
        internal void Hide()
        { if (!disposed) { pins.Update(new PinnedWindowObservation[0], true); focus.Hide(); } }
        public void Dispose()
        { if (disposed) return; disposed = true; pins.Dispose(); focus.Dispose(); }
    }
}

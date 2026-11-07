using System;
using System.Collections.Generic;
using System.Drawing;

namespace TaskbarSelect
{
    internal sealed class OwnedSnapDisplay
    {
        internal string Device, Identity;
        internal Rectangle Bounds, Work;
        internal bool Hidden;
    }

    internal sealed class OwnedSnapCycle
    {
        internal string DisplayIdentity;
        internal bool RightHalf;
        internal Rectangle ExpectedVisible;
    }

    internal sealed class OwnedSnapPlan
    {
        internal OwnedSnapDisplay Display;
        internal Rectangle VisibleTarget;
        internal bool RightHalf;
    }

    // Pure geometry only. Runtime ownership, opt-in, window identity and movement
    // authorization remain the controller's responsibility.
    internal static class OwnedSnapPolicy
    {
        static bool ValidRectangle(Rectangle value)
        {
            return value.Width > 0 && value.Height > 0 &&
                (long)value.X + value.Width <= Int32.MaxValue &&
                (long)value.Y + value.Height <= Int32.MaxValue;
        }

        static bool Contains(Rectangle bounds, Rectangle work)
        {
            return work.X >= bounds.X && work.Y >= bounds.Y &&
                (long)work.X + work.Width <= (long)bounds.X + bounds.Width &&
                (long)work.Y + work.Height <= (long)bounds.Y + bounds.Height;
        }

        static bool Reject(string message, out OwnedSnapPlan plan, out string reason)
        { plan = null; reason = message; return false; }

        internal static bool TryPlan(IList<OwnedSnapDisplay> displays, string currentIdentity,
            Rectangle currentVisible, bool left, OwnedSnapCycle previous,
            out OwnedSnapPlan plan, out string reason)
        {
            plan = null; reason = null;
            if (displays == null || displays.Count == 0)
                return Reject("No connected displays are available", out plan, out reason);
            if (String.IsNullOrWhiteSpace(currentIdentity) || !ValidRectangle(currentVisible))
                return Reject("The current window or display is unavailable", out plan, out reason);

            var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var devices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ordered = new List<OwnedSnapDisplay>();
            foreach (var display in displays) {
                if (display == null || String.IsNullOrWhiteSpace(display.Identity) || String.IsNullOrWhiteSpace(display.Device) ||
                    !ValidRectangle(display.Bounds) || !ValidRectangle(display.Work) || !Contains(display.Bounds, display.Work))
                    return Reject("A connected display has invalid bounds or work area", out plan, out reason);
                if (!identities.Add(display.Identity) || !devices.Add(display.Device))
                    return Reject("Connected display identities are ambiguous", out plan, out reason);
                ordered.Add(display);
            }
            ordered.Sort(delegate(OwnedSnapDisplay a, OwnedSnapDisplay b) {
                int order = a.Bounds.Left.CompareTo(b.Bounds.Left);
                if (order == 0) order = a.Bounds.Top.CompareTo(b.Bounds.Top);
                return order == 0 ? StringComparer.Ordinal.Compare(a.Device, b.Device) : order;
            });
            int current = ordered.FindIndex(delegate(OwnedSnapDisplay display) {
                return String.Equals(display.Identity, currentIdentity, StringComparison.OrdinalIgnoreCase);
            });
            if (current < 0) return Reject("The current display is no longer connected", out plan, out reason);

            bool continuing = previous != null &&
                String.Equals(previous.DisplayIdentity, currentIdentity, StringComparison.OrdinalIgnoreCase) &&
                previous.ExpectedVisible == currentVisible;
            int destination = current;
            bool rightHalf = !left;
            if (continuing) {
                if (left && !previous.RightHalf) {
                    destination = current == 0 ? ordered.Count - 1 : current - 1;
                    rightHalf = true;
                } else if (!left && previous.RightHalf) {
                    destination = current == ordered.Count - 1 ? 0 : current + 1;
                    rightHalf = false;
                }
            }

            var selected = ordered[destination];
            Rectangle area = selected.Hidden ? selected.Bounds : selected.Work;
            if (area.Width < 2)
                return Reject("This display is too narrow for two window halves", out plan, out reason);
            // Endpoint validation above makes these operations safe even on
            // negative-coordinate displays and at either Int32 screen edge.
            int leftWidth = area.Width / 2;
            int x = rightHalf ? checked(area.X + leftWidth) : area.X;
            int width = rightHalf ? area.Width - leftWidth : leftWidth;
            plan = new OwnedSnapPlan { Display = selected, RightHalf = rightHalf,
                VisibleTarget = new Rectangle(x, area.Y, width, area.Height) };
            return true;
        }
    }
}

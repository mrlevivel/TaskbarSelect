using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TaskbarSelect
{
    internal enum AppearanceChoiceKind { Finish, Lighting, Accent }

    // The checked native radio button is the selection. There is no hidden
    // combo box, separate input proxy, or settings persistence in this page.
    internal sealed class AppearanceChoiceGrid : Panel
    {
        readonly string[] items;
        readonly AppearanceChoiceCard[] cards;
        readonly Label[] familyHeadings;
        readonly int[][] familyCards;
        readonly int[] displayOrder;
        readonly int columns;
        int gap = 8, familyGap = 12, headingGap = 6;
        bool changing, ready, familyFontsReleased;
        internal event EventHandler SelectedIndexChanged;
        internal string[] Items { get { return (string[])items.Clone(); } }
        internal AppearanceChoiceCard[] Cards { get { return (AppearanceChoiceCard[])cards.Clone(); } }
        internal Label[] FamilyHeadings { get { return (Label[])familyHeadings.Clone(); } }
        internal AppearanceChoiceCard[] DisplayOrder
        {
            get {
                var ordered = new AppearanceChoiceCard[displayOrder.Length];
                for (int i = 0; i < ordered.Length; i++) ordered[i] = cards[displayOrder[i]];
                return ordered;
            }
        }
        internal int SelectedIndex
        {
            get { for (int i = 0; i < cards.Length; i++) if (cards[i].Checked) return i; return -1; }
            set {
                if (value < -1 || value >= cards.Length) throw new ArgumentOutOfRangeException("value");
                if (value == SelectedIndex) return;
                changing = true;
                try { for (int i = 0; i < cards.Length; i++) { cards[i].Checked = i == value; cards[i].TabStop = i == value || (value == -1 && i == 0); } }
                finally { changing = false; }
                RaiseSelection();
            }
        }
        internal string SelectedItem { get { int index = SelectedIndex; return index < 0 ? null : items[index]; } }

        internal AppearanceChoiceGrid(string[] labels, string[] descriptions, AppearanceChoiceKind kind, int preferredColumns)
        {
            if (labels == null || descriptions == null || labels.Length != descriptions.Length || labels.Length == 0)
                throw new ArgumentException("Appearance choices need matching labels and descriptions.");
            items = (string[])labels.Clone(); columns = Math.Max(1, preferredColumns);
            cards = new AppearanceChoiceCard[items.Length];
            Margin = Padding.Empty; TabStop = false; AccessibleRole = AccessibleRole.Grouping;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            for (int i = 0; i < cards.Length; i++) {
                var card = new AppearanceChoiceCard(items[i], descriptions[i], kind, i);
                card.TabIndex = i; card.TabStop = i == 0;
                card.CheckedChanged += delegate(object sender, EventArgs args) {
                    if (!changing && ((RadioButton)sender).Checked) RaiseSelection();
                };
                cards[i] = card; Controls.Add(card);
            }
            var groups = new List<int[]>(); var headings = new List<Label>(); var order = new List<int>();
            if (kind == AppearanceChoiceKind.Accent) {
                bool[] placed = new bool[cards.Length];
                foreach (string family in FocusAppearance.AccentFamilyLabels) {
                    var members = new List<int>();
                    foreach (string preset in FocusAppearance.AccentFamilyPresets(family))
                        for (int i = 0; i < items.Length; i++)
                            if (!placed[i] && String.Equals(items[i], preset, StringComparison.OrdinalIgnoreCase)) { members.Add(i); placed[i] = true; }
                    // Keep reusable subset grids complete even when a caller
                    // supplies a noncanonical label that normalizes to Original.
                    for (int i = 0; i < items.Length; i++)
                        if (!placed[i] && FocusAppearance.AccentFamily(items[i]) == family) { members.Add(i); placed[i] = true; }
                    if (members.Count == 0) continue;
                    var heading = new SectionHeader(family) { Dock = DockStyle.None, TabStop = false, AutoEllipsis = false,
                        AccessibleName = family, AccessibleRole = AccessibleRole.StaticText, Margin = Padding.Empty };
                    heading.TabIndex = order.Count;
                    headings.Add(heading); Controls.Add(heading); groups.Add(members.ToArray()); order.AddRange(members);
                }
            } else {
                var members = new int[cards.Length];
                for (int i = 0; i < cards.Length; i++) members[i] = i;
                groups.Add(members); order.AddRange(members);
            }
            familyHeadings = headings.ToArray(); familyCards = groups.ToArray(); displayOrder = order.ToArray();
            // The persisted index stays canonical. Native keyboard traversal
            // follows the grouped reading order, with all radios still siblings.
            for (int i = 0; i < displayOrder.Length; i++) cards[displayOrder[i]].TabIndex = i;
            if (kind == AppearanceChoiceKind.Accent) {
                var readingOrder = new List<Control>();
                for (int group = 0; group < familyCards.Length; group++) {
                    readingOrder.Add(familyHeadings[group]);
                    foreach (int index in familyCards[group]) {
                        cards[index].AccessibleDescription += " Color family: " + familyHeadings[group].Text + ".";
                        readingOrder.Add(cards[index]);
                    }
                }
                // Expose the same headings and choices in the native child
                // order for screen readers, without changing canonical IDs.
                for (int i = readingOrder.Count - 1; i >= 0; i--) Controls.SetChildIndex(readingOrder[i], 0);
            }
            ready = true; PerformLayout();
        }
        void RaiseSelection()
        {
            EventHandler handler = SelectedIndexChanged; if (handler != null) handler(this, EventArgs.Empty);
        }
        int ColumnCount(int width)
        {
            if (!ready || cards == null || cards.Length == 0) return 1;
            int minimum = cards[0].Kind == AppearanceChoiceKind.Accent ? cards[0].MinimumAccentWidth : cards[0].MinimumCardWidth;
            int count = Math.Max(1, Math.Min(columns, (Math.Max(1, width) + gap) / Math.Max(1, minimum + gap)));
            if (cards[0].Kind != AppearanceChoiceKind.Accent && cards[0].MaximumCardHeight > 0)
                while (count > 1) {
                    int cell = Math.Max(1, (width - (count - 1) * gap) / count); bool fits = true;
                    foreach (var card in cards)
                        if (card.GetPreferredSize(new Size(cell, 0)).Height > card.MaximumCardHeight) { fits = false; break; }
                    if (fits) break;
                    count--;
                }
            return count;
        }
        internal int LayoutColumns { get { return ColumnCount(ClientSize.Width); } }
        public override Size GetPreferredSize(Size proposedSize)
        {
            int width = proposedSize.Width > 0 ? proposedSize.Width : Math.Max(1, Width);
            if (!ready) return new Size(width, 1);
            return new Size(width, Arrange(width, false));
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e); if (!ready || cards == null || cards.Length == 0) return;
            Arrange(ClientSize.Width, true);
        }
        int Arrange(int width, bool apply)
        {
            int count = ColumnCount(width), y = 0;
            int cell = Math.Max(1, (width - (count - 1) * gap) / count);
            for (int group = 0; group < familyCards.Length; group++) {
                if (group > 0) y += familyGap;
                if (familyHeadings.Length != 0) {
                    Label heading = familyHeadings[group];
                    int height = TextRenderer.MeasureText(heading.Text, heading.Font, new Size(Math.Max(1, width), Int32.MaxValue),
                        TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
                    if (apply) heading.SetBounds(0, y, Math.Max(1, width), height);
                    y += height + headingGap;
                }
                int[] members = familyCards[group];
                if (cards[0].Kind == AppearanceChoiceKind.Accent) {
                    y = ArrangeAccent(members, width, count, y, apply);
                    continue;
                }
                for (int row = 0; row * count < members.Length; row++) {
                    if (row > 0) y += gap;
                    int rowHeight = 0;
                    for (int col = 0; col < count && row * count + col < members.Length; col++)
                        rowHeight = Math.Max(rowHeight, cards[members[row * count + col]].GetPreferredSize(new Size(cell, 0)).Height);
                    if (apply) for (int col = 0; col < count && row * count + col < members.Length; col++) {
                        int right = col == count - 1 ? width : (col + 1) * cell + col * gap;
                        cards[members[row * count + col]].SetBounds(col * (cell + gap), y, Math.Max(1, right - col * (cell + gap)), rowHeight);
                    }
                    y += rowHeight;
                }
            }
            return y;
        }
        int ArrangeAccent(int[] members, int width, int maximumColumns, int y, bool apply)
        {
            int next = 0;
            while (next < members.Length) {
                int start = next, rowWidth = 0, rowHeight = 0;
                while (next < members.Length && next - start < maximumColumns) {
                    AppearanceChoiceCard card = cards[members[next]];
                    int chipWidth = Math.Max(1, Math.Min(width, card.GetPreferredSize(Size.Empty).Width));
                    int added = (next == start ? 0 : gap) + chipWidth;
                    if (next > start && rowWidth + added > width) break;
                    rowWidth += added; rowHeight = Math.Max(rowHeight, card.GetPreferredSize(new Size(chipWidth, 0)).Height); next++;
                }
                int x = 0;
                if (apply) for (int i = start; i < next; i++) {
                    AppearanceChoiceCard card = cards[members[i]];
                    int chipWidth = Math.Max(1, Math.Min(width, card.GetPreferredSize(Size.Empty).Width));
                    card.SetBounds(x, y, chipWidth, rowHeight); x += chipWidth + gap;
                }
                y += rowHeight;
                if (next < members.Length) y += gap;
            }
            return y;
        }
        protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
        {
            gap = Math.Max(1, (int)Math.Round(gap * factor.Width));
            familyGap = Math.Max(1, (int)Math.Round(familyGap * factor.Height)); headingGap = Math.Max(1, (int)Math.Round(headingGap * factor.Height));
            base.ScaleControl(factor, specified);
        }
        protected override void OnFontChanged(EventArgs e)
        { base.OnFontChanged(e); PerformLayout(); if (Parent != null) Parent.PerformLayout(); }
        internal void SetPreview(FocusIndicatorMode frame, FocusLightingMode lighting, string accent, int finish = 0)
        { foreach (var card in cards) card.SetPreview(frame, lighting, accent, finish); }
        internal void SetViewportHeight(int height)
        { foreach (var card in cards) card.MaximumCardHeight = Math.Max(0, height); }
        protected override void Dispose(bool disposing)
        {
            Font[] ownedFonts = null;
            if (disposing && !familyFontsReleased && familyHeadings != null) {
                familyFontsReleased = true; ownedFonts = new Font[familyHeadings.Length];
                for (int i = 0; i < ownedFonts.Length; i++) ownedFonts[i] = familyHeadings[i].Font;
            }
            base.Dispose(disposing);
            if (ownedFonts != null) foreach (Font owned in ownedFonts) owned.Dispose();
        }
    }

    internal sealed class AppearanceChoiceCard : RadioButton
    {
        readonly AppearancePreviewScene scene = new AppearancePreviewScene();
        readonly string description;
        readonly string presetName;
        readonly int index;
        internal readonly AppearanceChoiceKind Kind;
        FocusIndicatorMode frame = FocusIndicatorMode.InsetEdge;
        FocusLightingMode lighting;
        int finish;
        string accent = "Original";
        bool hover;
        Font titleFont;
        int inset = 12, gap = 6, previewHeight = 72, indicator = 14, minimumCard = 155, minimumAccent = 112;
        int accentInset = 10, accentSwatch = 20, maximumAccent = 170, minimumAccentHeight = 44;
        int minimumPreviewHeight = 24;
        internal string Description { get { return description; } }
        internal string PresetName { get { return presetName; } }
        internal int ChoiceIndex { get { return index; } }
        internal int MinimumCardWidth { get { return minimumCard; } }
        internal int MinimumAccentWidth { get { return minimumAccent; } }
        internal int MaximumAccentWidth { get { return maximumAccent; } }
        internal int MinimumAccentHeight { get { return minimumAccentHeight; } }
        internal int MaximumCardHeight { get; set; }
        internal Rectangle PreviewBounds { get; private set; }
        internal Rectangle TitleBounds { get; private set; }
        internal Rectangle DescriptionBounds { get; private set; }
        internal Rectangle RadioBounds { get; private set; }

        internal AppearanceChoiceCard(string title, string details, AppearanceChoiceKind kind, int choiceIndex)
        {
            presetName = kind == AppearanceChoiceKind.Accent ? FocusAppearance.AccentStorageName(title) : null;
            Text = kind == AppearanceChoiceKind.Accent ? FocusAppearance.AccentLabel(presetName) : title;
            description = details; Kind = kind; index = choiceIndex;
            AutoCheck = true; AutoSize = false; UseMnemonic = false;
            AccessibleName = Text; AccessibleDescription = details; AccessibleRole = AccessibleRole.RadioButton;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            if (titleFont != null) titleFont.Dispose(); titleFont = new Font(Font, FontStyle.Bold);
        }
        internal void SetPreview(FocusIndicatorMode style, FocusLightingMode light, string color, int frameFinish = 0)
        {
            int normalizedFinish = (int)FocusAppearance.NormalizeFinish(frameFinish);
            string storedAccent = FocusAppearance.AccentStorageName(color);
            // Frame-style samples explain the stroke itself. Lighting belongs
            // to its own cards and the combined preview, so choosing a light
            // must not change these examples or invalidate their cached scene.
            FocusLightingMode sampleLight = Kind == AppearanceChoiceKind.Finish ? FocusLightingMode.None : light;
            if (frame == style && lighting == sampleLight && accent == storedAccent && finish == normalizedFinish) return;
            frame = style; lighting = sampleLight; accent = storedAccent; finish = normalizedFinish; scene.Clear(); Invalidate();
        }
        int TextHeight(string value, Font textFont, int width)
        { return TextRenderer.MeasureText(String.IsNullOrEmpty(value) ? " " : value, textFont, new Size(Math.Max(1, width), Int32.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height; }
        int PreviewHeight(int width)
        {
            if (MaximumCardHeight <= 0) return previewHeight;
            int title = TextHeight(Text, titleFont ?? Font, width - inset * 2 - indicator - gap);
            int details = TextHeight(description, Font, width - inset * 2);
            int textAndPadding = inset * 2 + gap + title + (details == 0 ? 0 : gap + details);
            return Math.Min(previewHeight, Math.Max(minimumPreviewHeight, MaximumCardHeight - textAndPadding));
        }
        public override Size GetPreferredSize(Size proposedSize)
        {
            if (Kind == AppearanceChoiceKind.Accent) {
                int natural = TextRenderer.MeasureText(Text, titleFont ?? Font, new Size(Int32.MaxValue, Int32.MaxValue),
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width + accentInset * 2 + accentSwatch + indicator + gap * 2;
                int preferred = Math.Max(minimumAccent, Math.Min(maximumAccent, natural));
                int chipWidth = proposedSize.Width > 0 ? Math.Max(1, Math.Min(maximumAccent, proposedSize.Width)) : preferred;
                int titleHeight = TextHeight(Text, titleFont ?? Font, chipWidth - accentInset * 2 - accentSwatch - indicator - gap * 2);
                return new Size(chipWidth, Math.Max(minimumAccentHeight, accentInset * 2 + Math.Max(titleHeight, Math.Max(accentSwatch, indicator))));
            }
            int width = proposedSize.Width > 0 ? proposedSize.Width : Math.Max(1, Width);
            int title = TextHeight(Text, titleFont ?? Font, width - inset * 2 - indicator - gap);
            int details = TextHeight(description, Font, width - inset * 2);
            int preview = PreviewHeight(width);
            return new Size(width, inset * 2 + preview + gap + title + (details == 0 ? 0 : gap + details));
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (Kind == AppearanceChoiceKind.Accent) {
                PreviewBounds = new Rectangle(accentInset, Math.Max(0, (Height - accentSwatch) / 2), accentSwatch, accentSwatch);
                RadioBounds = new Rectangle(Width - accentInset - indicator, Math.Max(0, (Height - indicator) / 2), indicator, indicator);
                int left = PreviewBounds.Right + gap, titleWidth = Math.Max(1, RadioBounds.Left - gap - left);
                int textHeight = TextHeight(Text, titleFont ?? Font, titleWidth);
                TitleBounds = new Rectangle(left, Math.Max(0, (Height - textHeight) / 2), titleWidth, textHeight);
                DescriptionBounds = Rectangle.Empty;
                return;
            }
            int width = Math.Max(1, ClientSize.Width - inset * 2), preview = PreviewHeight(ClientSize.Width);
            PreviewBounds = new Rectangle(inset, inset, width, preview);
            int titleHeight = TextHeight(Text, titleFont ?? Font, width - indicator - gap);
            TitleBounds = new Rectangle(inset, PreviewBounds.Bottom + gap, Math.Max(1, width - indicator - gap), titleHeight);
            DescriptionBounds = new Rectangle(inset, TitleBounds.Bottom + gap, width, TextHeight(description, Font, width));
            RadioBounds = new Rectangle(Width - inset - indicator, TitleBounds.Top + Math.Max(0, (Math.Min(TitleBounds.Height, Font.Height) - indicator) / 2), indicator, indicator);
        }
        protected override void OnFontChanged(EventArgs e)
        {
            if (titleFont != null) titleFont.Dispose(); titleFont = new Font(Font, FontStyle.Bold);
            base.OnFontChanged(e); PerformLayout(); if (Parent != null) Parent.PerformLayout();
        }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? Ui.Background : Parent.BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            bool highContrast = SystemInformation.HighContrast;
            Color face = Enabled && hover ? Ui.Hover : Ui.Surface;
            Color foreground = Enabled ? (highContrast && hover ? SystemColors.HighlightText : Ui.Text) : (highContrast ? SystemColors.GrayText : Ui.Muted);
            Color selection = highContrast ? foreground : Ui.Accent;
            Color edge = Checked ? selection : Ui.Line;
            using (var path = NativeChrome.Rounded(new RectangleF(.5f, .5f, Math.Max(1, Width - 1), Math.Max(1, Height - 1)), highContrast ? 0 : 3 * DeviceDpi / 96f)) {
                using (var fill = new SolidBrush(face)) e.Graphics.FillPath(fill, path);
                using (var line = new Pen(edge, Checked ? Math.Max(1, DeviceDpi / 96f) : 1)) e.Graphics.DrawPath(line, path);
            }
            if (Kind == AppearanceChoiceKind.Accent) {
                Color swatch = FocusAppearance.Accent(presetName, Ui.Dark);
                using (var path = NativeChrome.Rounded(PreviewBounds, highContrast ? 0 : 2 * DeviceDpi / 96f))
                using (var fill = new SolidBrush(swatch)) e.Graphics.FillPath(fill, path);
            } else {
                FocusIndicatorMode style = frame == FocusIndicatorMode.Off ? FocusIndicatorMode.InsetEdge : frame;
                FocusLightingMode previewLight = Kind == AppearanceChoiceKind.Finish ? FocusLightingMode.None :
                    Kind == AppearanceChoiceKind.Lighting ? (FocusLightingMode)index : lighting;
                int previewFinish = Kind == AppearanceChoiceKind.Finish ? index : finish;
                scene.Draw(e.Graphics, PreviewBounds, style, previewLight, accent, DeviceDpi, false, previewFinish);
            }
            TextRenderer.DrawText(e.Graphics, Text, titleFont ?? Font, TitleBounds, foreground, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            if (!DescriptionBounds.IsEmpty) TextRenderer.DrawText(e.Graphics, description, Font, DescriptionBounds,
                Enabled ? (highContrast && hover ? SystemColors.HighlightText : Ui.Muted) : foreground, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            Rectangle radio = RadioBounds;
            using (var pen = new Pen(Checked ? selection : foreground)) e.Graphics.DrawEllipse(pen, radio);
            if (Checked) using (var fill = new SolidBrush(selection)) e.Graphics.FillEllipse(fill, Rectangle.Inflate(radio, -Math.Max(3, indicator / 4), -Math.Max(3, indicator / 4)));
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4), foreground, face);
        }
        protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
        {
            inset = Math.Max(1, (int)Math.Round(inset * factor.Width)); gap = Math.Max(1, (int)Math.Round(gap * factor.Height));
            indicator = Math.Max(1, (int)Math.Round(indicator * factor.Width)); previewHeight = Math.Max(1, (int)Math.Round(previewHeight * factor.Height));
            minimumCard = Math.Max(1, (int)Math.Round(minimumCard * factor.Width)); minimumAccent = Math.Max(1, (int)Math.Round(minimumAccent * factor.Width));
            accentInset = Math.Max(1, (int)Math.Round(accentInset * factor.Width)); accentSwatch = Math.Max(1, (int)Math.Round(accentSwatch * factor.Width));
            maximumAccent = Math.Max(1, (int)Math.Round(maximumAccent * factor.Width)); minimumAccentHeight = Math.Max(1, (int)Math.Round(minimumAccentHeight * factor.Height));
            minimumPreviewHeight = Math.Max(1, (int)Math.Round(minimumPreviewHeight * factor.Height));
            base.ScaleControl(factor, specified);
        }
        protected override void Dispose(bool disposing)
        { if (disposing) { scene.Dispose(); if (titleFont != null) { titleFont.Dispose(); titleFont = null; } } base.Dispose(disposing); }
    }

    // Only generated pixels of an illustrative window. This never reads a
    // desktop, creates an overlay, observes foreground focus, or writes settings.
    internal sealed class AppearancePreviewScene : IDisposable
    {
        Bitmap image;
        Rectangle frame, imageBounds;
        string key;
        internal void Clear() { if (image != null) image.Dispose(); image = null; key = null; }
        internal void Draw(Graphics graphics, Rectangle area, FocusIndicatorMode style, FocusLightingMode lighting, string accent, int dpi, bool detailed, int finish = 0)
        {
            if (area.Width < 20 || area.Height < 20) return;
            bool highContrast = SystemInformation.HighContrast;
            int margin = Math.Max(10, (int)Math.Round(12 * dpi / 96.0));
            int windowWidth = Math.Max(12, detailed ? Math.Min(area.Width - margin * 2, (int)Math.Round(400 * dpi / 96.0)) : area.Width - margin * 2);
            // Even a compressed decorative sample needs a complete frame in
            // device pixels. A fixed 12px floor disappears beneath a 2 DIP
            // stroke at high DPI; normal-size samples keep their exact geometry.
            int windowHeight = Math.Max(Math.Max(12, (int)Math.Round(8 * dpi / 96.0)), area.Height - margin * 2);
            Rectangle nextFrame = new Rectangle((area.Width - windowWidth) / 2, margin, windowWidth, windowHeight);
            FocusLightingMode effectiveLight = highContrast ? FocusLightingMode.None : lighting;
            int normalizedFinish = (int)FocusAppearance.NormalizeFinish(finish);
            string nextKey = area.Size + ":" + nextFrame + ":" + dpi + ":" + style + ":" + effectiveLight + ":" + accent + ":" + normalizedFinish + ":" + Ui.Dark + ":" + highContrast;
            if (key != nextKey) {
                Clear(); frame = nextFrame; key = nextKey;
                var target = new FocusIndicatorTarget { Frame = frame, Monitors = new[] { new Rectangle(Point.Empty, area.Size) }, Dpi = dpi, Corner = Math.Max(2, (int)Math.Round(4 * dpi / 96.0)) };
                var visual = FocusIndicatorVisual.Create(target, style, accent, Ui.Dark, false, (int)effectiveLight, normalizedFinish);
                if (visual != null) { image = FocusIndicatorPaint.Render(visual, 1, .18); imageBounds = visual.Bounds; }
            }
            Rectangle window = frame; window.Offset(area.Location);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = NativeChrome.Rounded(window, highContrast ? 0 : Math.Max(2, 4 * dpi / 96f))) {
                using (var fill = new SolidBrush(Ui.Card)) graphics.FillPath(fill, path);
                using (var pen = new Pen(Ui.Line)) graphics.DrawPath(pen, path);
            }
            int small = Math.Max(2, (int)Math.Round(4 * dpi / 96.0));
            int toolbar = Math.Min(window.Height / 3, Math.Max(10, (int)Math.Round((detailed ? 26 : 14) * dpi / 96.0)));
            using (var line = new Pen(Ui.Line)) graphics.DrawLine(line, window.Left + small, window.Top + toolbar, window.Right - small, window.Top + toolbar);
            using (var brush = new SolidBrush(Ui.Muted)) {
                for (int i = 0; i < 3; i++) graphics.FillEllipse(brush, window.Right - small * (i + 2), window.Top + toolbar / 2 - small / 3, Math.Max(1, small / 2), Math.Max(1, small / 2));
            }
            int bodyLeft = window.Left + small * 3, bodyTop = window.Top + toolbar + small * 3;
            using (var bar = new SolidBrush(Ui.Line)) {
                int available = Math.Max(1, window.Width - small * 6);
                for (int i = 0; i < (detailed ? 3 : 2); i++) {
                    int top = bodyTop + i * small * 3;
                    if (top + small >= window.Bottom - small * 2) break;
                    graphics.FillRectangle(bar, bodyLeft, top, Math.Max(1, available * (i == 0 ? 65 : 45) / 100), Math.Max(1, small / 2));
                }
            }
            if (image != null) graphics.DrawImageUnscaled(image, area.X + imageBounds.X, area.Y + imageBounds.Y);
        }
        public void Dispose() { Clear(); }
    }

    internal sealed class AppearancePreview : Control
    {
        readonly AppearancePreviewScene scene = new AppearancePreviewScene();
        FocusIndicatorMode frame;
        FocusLightingMode lighting;
        FocusFrameFinish finish;
        string accent = "Original";
        internal FocusIndicatorMode FrameStyle { get { return frame; } }
        internal FocusLightingMode Lighting { get { return lighting; } }
        internal FocusFrameFinish Finish { get { return finish; } }
        internal string AccentPreset { get { return accent; } }
        internal bool AnimationRunning { get { return false; } }
        internal AppearancePreview()
        {
            TabStop = false; AccessibleRole = AccessibleRole.Graphic;
            AccessibleName = "Appearance preview";
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        internal void SetPreview(FocusIndicatorMode style, FocusLightingMode light, string color, int frameFinish = 0)
        {
            frame = style; lighting = light; accent = FocusAppearance.AccentStorageName(color); finish = FocusAppearance.NormalizeFinish(frameFinish);
            AccessibleDescription = FocusAppearance.FrameLabel((int)style) + ", " + FocusAppearance.FinishLabel((int)finish) + ", " + FocusAppearance.LightingLabels[(int)light] + ", " + FocusAppearance.AccentLabel(accent) + ". Illustrative focused window.";
            scene.Clear(); Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Ui.Surface); scene.Draw(e.Graphics, ClientRectangle, frame, lighting, accent, DeviceDpi, true, (int)finish);
        }
        protected override void Dispose(bool disposing) { if (disposing) scene.Dispose(); base.Dispose(disposing); }
    }

    internal sealed class AppearancePage : Panel
    {
        internal readonly ToggleSwitch FocusFrame = new ToggleSwitch("Focus frame");
        internal readonly AppearanceChoiceGrid FinishChoices;
        internal readonly AppearanceChoiceGrid LightingChoices;
        internal readonly AppearanceChoiceGrid AccentChoices;
        internal readonly AppearancePreview CombinedPreview = new AppearancePreview();
        readonly SettingsCard[] cards = new SettingsCard[4];
        readonly Label[] headings = new Label[4], descriptions = new Label[4];
        readonly Label caption = Ui.Label("");
        readonly Label finishHeading = new SectionHeader("Frame style");
        Control viewport;
        int inset = 16, gap = 8, cardGap = 12, previewHeight = 156, toggleHeight = 40;
        bool layingOut, ready;
        int EffectivePreviewHeight
        { get { return viewport == null || viewport.ClientSize.Height <= 0 ? previewHeight : Math.Min(previewHeight, viewport.ClientSize.Height); } }

        internal AppearancePage()
        {
            // Dock owns the width; OnLayout owns the measured height. AutoSize
            // would negotiate width again while a hidden native page is settling.
            Dock = DockStyle.Top; AutoSize = false; TabStop = false;
            Margin = Padding.Empty; AccessibleRole = AccessibleRole.Grouping; AccessibleName = "Appearance";
            FocusFrame.Dock = DockStyle.None; FocusFrame.Margin = Padding.Empty;
            FocusFrame.AccessibleRole = AccessibleRole.CheckButton;
            FocusFrame.TabIndex = 0;
            FocusFrame.AccessibleDescription = "Show a focus frame around the active window. Turning it off keeps your frame style and lighting choices.";
            FinishChoices = new AppearanceChoiceGrid(FocusAppearance.FinishLabels, new[] {
                "A restrained two-pixel frame.", "A crisp, one-pixel line in your accent color." }, AppearanceChoiceKind.Finish, 2);
            FinishChoices.AccessibleName = "Frame style"; FinishChoices.TabIndex = 1;
            FinishChoices.SelectedIndex = 0;
            LightingChoices = new AppearanceChoiceGrid(FocusAppearance.LightingLabels, new[] {
                "A clean, unlit frame.", "A gentle halo around the frame.", "A brighter frame with a stronger, steady glow.", "A traveling highlight with a flowing glow." }, AppearanceChoiceKind.Lighting, 2);
            string[] colors = FocusAppearance.Presets, colorDescriptions = new string[colors.Length];
            for (int i = 0; i < colors.Length; i++) colorDescriptions[i] = FocusAppearance.AccentLabel(colors[i]) + " accent for Taskbar Select controls, Ghost Bar and focus frame.";
            AccentChoices = new AppearanceChoiceGrid(colors, colorDescriptions, AppearanceChoiceKind.Accent, 6);
            LightingChoices.AccessibleName = "Frame lighting"; AccentChoices.AccessibleName = "Accent color";
            string[] titles = { "Preview", "Focus frame", "Frame lighting", "Accent color" };
            string[] details = { "See your choices together on a focused window.", "Choose the frame itself. Samples are shown without lighting.", "Choose the light around your focus frame.", "Applies to Taskbar Select, Ghost Bar, search and the focus frame." };
            for (int i = 0; i < cards.Length; i++) {
                cards[i] = new SettingsCard { AccessibleName = titles[i], Dock = DockStyle.None, Padding = Padding.Empty, Margin = Padding.Empty };
                headings[i] = new SectionHeader(titles[i]) { Dock = DockStyle.None, AutoEllipsis = false };
                descriptions[i] = Ui.Label(details[i]); descriptions[i].Dock = DockStyle.None; descriptions[i].AutoEllipsis = false;
                cards[i].Controls.Add(headings[i]); cards[i].Controls.Add(descriptions[i]); Controls.Add(cards[i]);
            }
            caption.Dock = DockStyle.None; caption.AutoEllipsis = false; caption.AccessibleName = "Selected appearance";
            cards[0].Controls.Add(CombinedPreview); cards[0].Controls.Add(caption);
            finishHeading.Dock = DockStyle.None; finishHeading.AutoEllipsis = false;
            cards[1].Controls.Add(FocusFrame); cards[1].Controls.Add(finishHeading); cards[1].Controls.Add(FinishChoices);
            cards[2].Controls.Add(LightingChoices); cards[3].Controls.Add(AccentChoices);
            FocusFrame.CheckedChanged += delegate { SynchronizePreview(); };
            FinishChoices.SelectedIndexChanged += delegate { SynchronizePreview(); };
            LightingChoices.SelectedIndexChanged += delegate { SynchronizePreview(); };
            AccentChoices.SelectedIndexChanged += delegate { SynchronizePreview(); };
            ready = true;
            SynchronizePreview();
        }
        internal void SynchronizePreview()
        {
            FocusIndicatorMode style = FocusFrame.Checked ? FocusIndicatorMode.InsetEdge : FocusIndicatorMode.Off;
            FocusLightingMode light = (FocusLightingMode)Math.Max(0, LightingChoices.SelectedIndex);
            string color = AccentChoices.SelectedItem ?? "Original";
            int finish = (int)FocusAppearance.NormalizeFinish(FinishChoices.SelectedIndex);
            FinishChoices.Enabled = style != FocusIndicatorMode.Off;
            LightingChoices.Enabled = style != FocusIndicatorMode.Off;
            descriptions[1].Text = style == FocusIndicatorMode.Off ? "Samples are shown without lighting. Your frame style is kept while off." : "Choose the frame itself. Samples are shown without lighting.";
            descriptions[2].Text = style == FocusIndicatorMode.Off ? "Turn on Focus frame to use lighting. Your lighting choice is kept." : "Choose the light around your focus frame.";
            FinishChoices.SetPreview(style, light, color, finish);
            LightingChoices.SetPreview(style, light, color, finish); AccentChoices.SetPreview(style, light, color, finish);
            CombinedPreview.SetPreview(style, light, color, finish);
            string colorLabel = FocusAppearance.AccentLabel(color);
            caption.Text = style == FocusIndicatorMode.Off ? "Focus frame off  ·  " + colorLabel : FocusAppearance.FrameLabel((int)style) + "  ·  " + FocusAppearance.FinishLabel(finish) + "  ·  " + FocusAppearance.LightingLabels[(int)light] + "  ·  " + colorLabel;
            PerformLayout(); Invalidate(true);
        }
        int TextHeight(Label label, int width)
        { return TextRenderer.MeasureText(String.IsNullOrEmpty(label.Text) ? " " : label.Text, label.Font, new Size(Math.Max(1, width), Int32.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height; }
        int CardHeight(int index, int width)
        {
            int inner = Math.Max(1, width - inset * 2);
            int height = inset * 2 + TextHeight(headings[index], inner) + gap + TextHeight(descriptions[index], inner) + gap;
            if (index == 0) return height + EffectivePreviewHeight + gap + TextHeight(caption, inner);
            if (index == 1) return height + Math.Max(toggleHeight, FocusFrame.Font.Height + gap * 2) + gap + TextHeight(finishHeading, inner) + gap +
                FinishChoices.GetPreferredSize(new Size(inner, 0)).Height;
            var grid = index == 2 ? LightingChoices : AccentChoices;
            return height + grid.GetPreferredSize(new Size(inner, 0)).Height;
        }
        public override Size GetPreferredSize(Size proposedSize)
        {
            int width = proposedSize.Width > 0 ? proposedSize.Width : Math.Max(1, Width), height = 0;
            if (!ready || cards == null || cards[0] == null) return new Size(width, 1);
            for (int i = 0; i < cards.Length; i++) height += CardHeight(i, width) + (i == 0 ? 0 : cardGap);
            return new Size(width, height);
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e); if (!ready || layingOut || cards == null || cards[0] == null) return;
            layingOut = true;
            try {
                int viewportHeight = viewport == null ? 0 : viewport.ClientSize.Height;
                FinishChoices.SetViewportHeight(viewportHeight); LightingChoices.SetViewportHeight(viewportHeight);
                int width = ClientSize.Width, inner = Math.Max(1, width - inset * 2), y = 0;
                for (int i = 0; i < cards.Length; i++) {
                    int cardHeight = CardHeight(i, width); cards[i].SetBounds(0, y, width, cardHeight);
                    int top = inset, headingHeight = TextHeight(headings[i], inner), detailHeight = TextHeight(descriptions[i], inner);
                    headings[i].SetBounds(inset, top, inner, headingHeight); top += headingHeight + gap;
                    descriptions[i].SetBounds(inset, top, inner, detailHeight); top += detailHeight + gap;
                    if (i == 0) {
                        int preview = EffectivePreviewHeight;
                        CombinedPreview.SetBounds(inset, top, inner, preview); top += preview + gap;
                        caption.SetBounds(inset, top, inner, TextHeight(caption, inner));
                    } else if (i == 1) {
                        int switchHeight = Math.Max(toggleHeight, FocusFrame.Font.Height + gap * 2);
                        FocusFrame.SetBounds(inset, top, inner, switchHeight); top += switchHeight + gap;
                        int finishHeadingHeight = TextHeight(finishHeading, inner);
                        finishHeading.SetBounds(inset, top, inner, finishHeadingHeight); top += finishHeadingHeight + gap;
                        FinishChoices.SetBounds(inset, top, inner, FinishChoices.GetPreferredSize(new Size(inner, 0)).Height);
                    } else {
                        var grid = i == 2 ? LightingChoices : AccentChoices;
                        grid.SetBounds(inset, top, inner, grid.GetPreferredSize(new Size(inner, 0)).Height);
                    }
                    y += cardHeight + cardGap;
                }
                int wanted = Math.Max(1, y - cardGap); if (Height != wanted) Height = wanted;
            } finally { layingOut = false; }
        }
        protected override void OnFontChanged(EventArgs e)
        { base.OnFontChanged(e); PerformLayout(); if (Parent != null) Parent.PerformLayout(); }
        protected override void OnParentChanged(EventArgs e)
        {
            if (viewport != null) viewport.ClientSizeChanged -= ViewportSizeChanged;
            base.OnParentChanged(e); viewport = Parent;
            if (viewport != null) viewport.ClientSizeChanged += ViewportSizeChanged;
            PerformLayout();
        }
        void ViewportSizeChanged(object sender, EventArgs e) { PerformLayout(); }
        protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
        {
            inset = Math.Max(1, (int)Math.Round(inset * factor.Width)); gap = Math.Max(1, (int)Math.Round(gap * factor.Height));
            cardGap = Math.Max(1, (int)Math.Round(cardGap * factor.Height)); previewHeight = Math.Max(1, (int)Math.Round(previewHeight * factor.Height));
            toggleHeight = Math.Max(1, (int)Math.Round(toggleHeight * factor.Height));
            base.ScaleControl(factor, specified);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && viewport != null) { viewport.ClientSizeChanged -= ViewportSizeChanged; viewport = null; }
            base.Dispose(disposing);
        }
    }
}

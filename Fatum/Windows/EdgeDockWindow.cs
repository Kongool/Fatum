using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Fatum.Services;
using Fatum.Windows.Dock;

namespace Fatum.Windows;

/// <summary>
/// The edge dock: a tab pinned to a screen edge that pops its panel out beside it while pointed at (click to
/// pin). Ported from Daedalus's <c>EdgeDockWindow</c> (which ported Charon's). The tab has its own edge and its
/// own spot along it; dragging it and letting go puts it on the nearest edge, where it was let go.
/// <para>
/// This window itself is an invisible 1×1 host. Each tab is its own small ImGui window, so it can sit anywhere
/// on any edge, and <see cref="DockLayoutShare"/> keeps it from covering Daedalus's or Charon's tabs.
/// </para>
/// <para>
/// The panel is the BODY of the settings window — its own <c>Draw()</c>, called inside the pop-out — so the
/// dock and <c>/fatum</c> cannot drift apart.
/// </para>
/// <para>
/// On the left and right edges the label is ordinary text whose vertices are turned after it is drawn
/// (<see cref="DockMath"/>), under a full-screen clip rect: ImGui culls glyphs against the clip rect BEFORE
/// they are turned. On the top and bottom edges the label reads normally.
/// </para>
/// </summary>
public sealed unsafe class EdgeDockWindow : Window, IDisposable
{
    /// <param name="Id">Stable id; also the ImGui id suffix and the layout key.</param>
    /// <param name="Label">What the tab reads.</param>
    /// <param name="Applies">Whether the tab is shown right now.</param>
    /// <param name="DrawPanel">The panel body — normally the target window's own <c>Draw()</c>.</param>
    /// <param name="PanelWidth">A fixed panel width (height left to the content); null sizes both ways.</param>
    public sealed record Tab(string Id, string Label, Func<bool> Applies, Action DrawPanel, float? PanelWidth = null);

    private const float TabThickness = 34f;
    private const float TabPadding = 14f;
    private const float PanelGap = 6f;
    private const float LabelScale = 1.25f;
    private const float DragThreshold = 6f;
    private static readonly TimeSpan CloseDelay = TimeSpan.FromMilliseconds(450);

    // After Daedalus (10) when tabs want the same spot.
    private const string Owner = "Fatum";
    private const int Order = 40;

    // Fatum violet where Daedalus uses gold.
    private static readonly Vector4 Accent = new(0.61f, 0.36f, 0.96f, 1f);
    private static readonly Vector4 AccentText = new(0.80f, 0.70f, 1.00f, 1f);
    private static readonly Vector4 TextSecondary = new(0.70f, 0.71f, 0.75f, 1f);
    private static readonly Vector4 Rose = new(0.90f, 0.25f, 0.30f, 1f);

    private const ImGuiWindowFlags TabFlags =
        ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoMove
        | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav
        | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;

    private readonly Configuration config;
    private readonly ConfigWindow configWindow;
    private readonly VoiceController controller;
    private readonly IReadOnlyList<Tab> tabs;
    private readonly DockLayoutShare layout;
    private readonly DockHover hover = new();
    private List<Tab> visible = new();
    private Vector2 panelSize = new(420f, 480f);
    private string? dragging;

    public EdgeDockWindow(Configuration config, ConfigWindow configWindow, VoiceController controller)
        : base("##FatumEdgeDock")
    {
        this.config = config;
        this.configWindow = configWindow;
        this.controller = controller;
        tabs = new[] { new Tab("main", "Fatum", () => true, configWindow.DrawEmbedded) };
        layout = new DockLayoutShare(Plugin.PluginInterface, Owner, Order);

        Flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoInputs
                | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing
                | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoScrollbar;
        RespectCloseHotkey = false;
        PositionCondition = ImGuiCond.Always;
        SizeCondition = ImGuiCond.Always;
        Size = Vector2.One;
        IsOpen = true;
    }

    public void Dispose() => layout.Dispose();

    // Not drawn (so not published; the shared layout drops it once it goes stale) while off or nothing applies.
    public override bool DrawConditions()
    {
        visible = config.EdgeDockEnabled ? tabs.Where(t => t.Applies()).ToList() : new List<Tab>();
        if (visible.Count > 0) return true;
        configWindow.ReleaseEmbedded();
        return false;
    }

    public override void PreDraw() => Position = ImGui.GetMainViewport().WorkPos;

    public override void Draw()
    {
        var viewport = ImGui.GetMainViewport();
        var workPos = viewport.WorkPos;
        var workSize = viewport.WorkSize;

        var requests = visible.Select(t =>
        {
            var (edge, along) = Placement(t.Id);
            return new DockRequest(t.Id, edge, along, TabLength(t.Label));
        }).ToList();
        var starts = layout.Place(requests, workPos, workSize);

        string? hovered = null;
        Tab? openTab = null;
        var openMin = Vector2.Zero;
        var openSize = Vector2.Zero;
        var openEdge = DockEdge.Right;

        for (var i = 0; i < visible.Count; i++)
        {
            var tab = visible[i];
            var edge = requests[i].Edge;
            if (!starts.TryGetValue(tab.Id, out var start))
                continue;

            var (min, size) = DockMath.TabRect(edge, start, requests[i].Length, TabThickness, workPos, workSize);
            if (DrawTabWindow(tab, edge, min, size, ref hovered))
            {
                openTab = tab;
                openMin = min;
                openSize = size;
                openEdge = edge;
            }
        }

        if (dragging != null)
            DrawDrag(workPos, workSize);

        var panelBusy = openTab != null && DrawPanel(openTab, openEdge, openMin, openSize);
        if (openTab == null && !configWindow.IsOpen)
            configWindow.ReleaseEmbedded();

        hover.Update(hovered, panelBusy, DateTime.UtcNow, CloseDelay, visible.Select(t => t.Id).ToList());
    }

    private (DockEdge Edge, float Along) Placement(string id) => (config.EdgeDockEdge, config.EdgeDockAlong);

    private void MoveTab(string id, DockEdge edge, float along)
    {
        config.EdgeDockEdge = edge;
        config.EdgeDockAlong = along;
        config.Save();
    }

    /// <summary>One tab, in its own little window. True when its panel is out.</summary>
    private bool DrawTabWindow(Tab tab, DockEdge edge, Vector2 min, Vector2 size, ref string? hovered)
    {
        ImGui.SetNextWindowPos(min, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);

        var isOpen = false;
        if (ImGui.Begin($"##FatumEdgeDockTab{tab.Id}", TabFlags))
        {
            // A press that moves becomes a drag; only a press that does not is a click (pin / unpin).
            var clicked = ImGui.InvisibleButton($"##dock{tab.Id}", size);
            if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left, DragThreshold))
                dragging = tab.Id;
            if (clicked && dragging != tab.Id)
                hover.Click(tab.Id);
            if (ImGui.IsItemHovered() && dragging == null)
                hovered = tab.Id;

            isOpen = hover.Open == tab.Id && dragging == null;
            var lit = isOpen || hovered == tab.Id || dragging == tab.Id;
            DrawTab(ImGui.GetWindowDrawList(), min, size, tab.Label, lit, hover.Pinned == tab.Id, edge, controller.State);
        }

        ImGui.End();
        ImGui.PopStyleVar(3);
        return isOpen;
    }

    /// <summary>
    /// A tab being dragged: the edge it would land on lit violet, an outline where it would sit, and on release
    /// the move.
    /// </summary>
    private void DrawDrag(Vector2 workPos, Vector2 workSize)
    {
        var mouse = ImGui.GetMousePos();
        var target = DockMath.NearestEdge(mouse, workPos, workSize);
        var along = DockMath.AlongFraction(target, mouse, workPos, workSize);

        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            var id = dragging!;
            dragging = null;
            MoveTab(id, target, along);
            return;
        }

        var fg = ImGui.GetForegroundDrawList();
        var color = ImGui.GetColorU32(Accent);
        var a = workPos;
        var b = workPos + workSize;
        const float bar = 3f;
        switch (target)
        {
            case DockEdge.Left: fg.AddRectFilled(a, new Vector2(a.X + bar, b.Y), color); break;
            case DockEdge.Right: fg.AddRectFilled(new Vector2(b.X - bar, a.Y), b, color); break;
            case DockEdge.Top: fg.AddRectFilled(a, new Vector2(b.X, a.Y + bar), color); break;
            case DockEdge.Bottom: fg.AddRectFilled(new Vector2(a.X, b.Y - bar), b, color); break;
        }

        // The outline of where it would land (before any nudge from its neighbours).
        var label = visible.FirstOrDefault(t => t.Id == dragging)?.Label ?? dragging!;
        var length = TabLength(label);
        var horizontal = DockMath.IsHorizontal(target);
        var edgeStart = horizontal ? workPos.X : workPos.Y;
        var edgeLength = horizontal ? workSize.X : workSize.Y;
        var start = Math.Clamp(edgeStart + along * edgeLength - length / 2f, edgeStart,
            MathF.Max(edgeStart, edgeStart + edgeLength - length));
        var (min, size) = DockMath.TabRect(target, start, length, TabThickness, workPos, workSize);
        fg.AddRect(min, min + size, color, 6f, ImDrawFlags.None, 2f);
    }

    private static float TabLength(string label) =>
        MathF.Max(90f, ImGui.CalcTextSize(label).X * LabelScale + TabPadding * 2f);

    private static void DrawTab(
        ImDrawListPtr drawList, Vector2 min, Vector2 size, string label, bool lit, bool pinned, DockEdge edge,
        RecordingState state)
    {
        var max = min + size;
        var fill = lit ? new Vector4(0.16f, 0.15f, 0.20f, 0.96f) : new Vector4(0.10f, 0.10f, 0.12f, 0.88f);
        // Rounded on the side facing the screen, square against the edge it is pinned to.
        var corners = edge switch
        {
            DockEdge.Right => ImDrawFlags.RoundCornersLeft,
            DockEdge.Top => ImDrawFlags.RoundCornersBottom,
            DockEdge.Bottom => ImDrawFlags.RoundCornersTop,
            _ => ImDrawFlags.RoundCornersRight,
        };
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(fill), 6f, corners);

        if (pinned)
        {
            // Violet on the tab's screen-facing side marks a pinned panel.
            var (barMin, barSize) = edge switch
            {
                DockEdge.Right => (min, new Vector2(2f, size.Y)),
                DockEdge.Top => (new Vector2(min.X, max.Y - 2f), new Vector2(size.X, 2f)),
                DockEdge.Bottom => (min, new Vector2(size.X, 2f)),
                _ => (new Vector2(max.X - 2f, min.Y), new Vector2(2f, size.Y)),
            };
            drawList.AddRectFilled(barMin, barMin + barSize, ImGui.GetColorU32(Accent));
        }

        // A small dot at the tab's leading end while Fatum is hearing you (red) or transcribing (violet).
        if (state != RecordingState.Idle)
        {
            var dot = DockMath.IsHorizontal(edge)
                ? new Vector2(min.X + 9f, min.Y + size.Y / 2f)
                : new Vector2(min.X + size.X / 2f, min.Y + 9f);
            drawList.AddCircleFilled(dot, 4f, ImGui.GetColorU32(state == RecordingState.Recording ? Rose : Accent));
        }

        var font = ImGui.GetFont();
        var fontSize = ImGui.GetFontSize() * LabelScale;
        var textSize = ImGui.CalcTextSize(label) * LabelScale;
        var color = ImGui.GetColorU32(lit ? AccentText : TextSecondary);

        if (DockMath.IsHorizontal(edge))
        {
            drawList.AddText(font, fontSize, min + (size - textSize) / 2f, color, label);
            return;
        }

        // Turned, the label is textSize.Y wide and textSize.X tall — centred in the tab.
        var topLeft = min + new Vector2((size.X - textSize.Y) / 2f, (size.Y - textSize.X) / 2f);
        var pivot = DockMath.PivotFor(topLeft, textSize);

        // Drawn first near the top-left of the screen, where the whole unturned line fits, then turned and
        // moved into the tab.
        var drawnAt = ImGui.GetMainViewport().WorkPos + new Vector2(8f, 8f);
        drawList.PushClipRectFullScreen();
        var first = drawList.VtxBuffer.Size;
        drawList.AddText(font, fontSize, drawnAt, color, label);
        var last = drawList.VtxBuffer.Size;
        var vertices = drawList.VtxBuffer.Data;
        for (var i = first; i < last; i++)
            vertices[i].Pos = DockMath.TurnUpAndPlace(vertices[i].Pos, drawnAt, pivot);
        drawList.PopClipRect();
    }

    /// <summary>Draws the open tab's panel beside it. True while the pointer is on it or it is mid-interaction.</summary>
    private bool DrawPanel(Tab tab, DockEdge edge, Vector2 tabMin, Vector2 tabSize)
    {
        var viewport = ImGui.GetMainViewport();
        var (pos, pivot) = DockMath.PanelPlacement(
            edge, tabMin, tabSize, panelSize, viewport.WorkPos, viewport.WorkSize, PanelGap);

        ImGui.SetNextWindowPos(pos, ImGuiCond.Always, pivot);
        if (tab.PanelWidth is { } width)
            ImGui.SetNextWindowSizeConstraints(new Vector2(width, 0f), new Vector2(width, float.MaxValue));

        var flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings
                    | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.AlwaysAutoResize;

        var busy = false;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10f, 8f));
        if (ImGui.Begin($"##FatumEdgeDockPanel{tab.Id}", flags))
        {
            try
            {
                tab.DrawPanel();
            }
            catch (Exception ex)
            {
                // Caught HERE: an exception leaving between Begin and End would skip the End and corrupt
                // ImGui's window stack for every plugin drawing after this one.
                ImGui.TextColored(Rose, $"This panel could not be drawn: {ex.Message}");
            }

            panelSize = ImGui.GetWindowSize();

            // The panel also counts as "in use" while one of its own pop-ups is open or a control is held:
            // a combo's list is a separate window, and losing the panel under it closes the combo.
            busy = ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows
                                         | ImGuiHoveredFlags.AllowWhenBlockedByPopup
                                         | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem)
                   || ImGui.IsPopupOpen(string.Empty, ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel)
                   || ImGui.IsAnyItemActive();
        }

        ImGui.End();
        ImGui.PopStyleVar();
        return busy;
    }
}

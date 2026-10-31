using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The BUILD window, one tab per question the player is asking: INCOME (how do I earn more),
/// PRODUCTION (how do I get more units), DEFENCE (how do I hold what I have) and MANAGE (what do I
/// own, and what needs a repair crew). The catalogue is split between PRODUCTION and DEFENCE by the
/// game's own building category, so a building a patch adds still files itself.
/// </summary>
internal sealed class CommanderEconomyUi
{
    private const int WindowId = 0x434F4D42;
    private const float RowHeight = 50f;
    private const float FooterHeight = 62f;
    private const float CardHeight = 138f;
    private const float CardGap = 6f;
    private const string IncomeNote = "Each income building pays for as long as it stands, so each is worth defending. "
        + "Upgrade the ones you own from MANAGE, or by clicking them.";

    private const float LedgerLineHeight = 22f;

    private static readonly string[] ProductCategories = { "GROUND VEHICLES", "AIRCRAFT", "NAVAL" };

    /// <summary>
    /// The economy sheet's height: its section header, the income and upkeep lines with a caption
    /// and a total each, the net line and three notes, plus the gaps between them.
    /// </summary>
    private static readonly float EconomySheetHeight = 40f
        + ((CommanderEconomyService.IncomeKinds.Count + CommanderEconomyService.UpkeepLineNames.Length + 8) * LedgerLineHeight)
        + 10f;

    private readonly System.Collections.Generic.List<GUIStyle> rightAlignedSources = new();
    private readonly System.Collections.Generic.List<GUIStyle> rightAligned = new();

    private readonly CommanderEconomyService service;
    private readonly CommanderRepairService repairService;

    /// <summary>The catalogue rows the current tab shows, refilled each draw.</summary>
    private readonly System.Collections.Generic.List<BuildingDefinition> shownCatalog = new();
    private bool helpVisible;
    private bool positionInitialized;
    private Tab tab;
    private Rect windowRect;
    private Vector2 scroll;

    /// <summary>The PRODUCES list is open in place of the catalogue on the PRODUCTION tab.</summary>
    private bool productionListOpen;

    /// <summary>The blueprint whose CANCEL is armed: two clicks, like DESTROY, since it throws the progress away.</summary>
    private CommanderEconomyService.ConstructionSite? cancelArmedSite;

    /// <summary>The category the PRODUCES list is showing, one of <see cref="ProductCategories"/>.</summary>
    private string productionFilter = ProductCategories[0];

    internal CommanderEconomyUi(CommanderEconomyService service, CommanderRepairService repairService)
    {
        this.service = service;
        this.repairService = repairService;
    }

    private enum Tab
    {
        Income,
        Production,
        Defence,
        Manage,
    }

    internal bool Visible { get; private set; }

    internal void Toggle()
    {
        Visible = !Visible;
        helpVisible = false;
        EnsurePosition();
    }

    internal void Hide()
    {
        Visible = false;
        helpVisible = false;
        service.CancelBuild();
    }

    internal void Draw()
    {
        if (!Visible)
        {
            return;
        }

        EnsurePosition();
        windowRect = CommanderWindowScale.Clamp(WindowId, windowRect);
        windowRect = CommanderWindowScale.Window(WindowId, windowRect, DrawWindow, "BUILD");
    }

    internal bool ContainsScreenPoint(Vector2 screenPoint)
    {
        return Visible && CommanderWindowScale.Visual(WindowId, windowRect).Contains(CommanderUiScale.ScreenToGui(screenPoint));
    }

    internal void ResetPosition()
    {
        positionInitialized = false;
        EnsurePosition();
    }

    private static float DrawSection(Rect viewRect, float rowY, string title, int count, string emptyText)
    {
        rowY = CommanderUiTheme.DrawSection(new Rect(8f, rowY + 8f, viewRect.width - 16f, 20f), title);
        if (count == 0)
        {
            // Measured, not a fixed row: the naval dock's note wraps to two lines and was cut in half.
            float height = Mathf.Max(20f, CommanderUiTheme.MutedLabel.CalcHeight(new GUIContent(emptyText), viewRect.width - 24f));
            GUI.Label(new Rect(10f, rowY, viewRect.width - 24f, height), emptyText, CommanderUiTheme.MutedLabel);
            rowY += height + 4f;
        }

        return rowY;
    }

    private static float DrawLedgerCaption(Rect viewRect, float rowY, string caption)
    {
        GUI.Label(new Rect(10f, rowY + 2f, viewRect.width - 20f, LedgerLineHeight), caption, CommanderUiTheme.Caption);
        return rowY + LedgerLineHeight;
    }

    private static void DrawLedgerNote(Rect viewRect, ref float rowY, string text, GUIStyle style)
    {
        GUI.Label(new Rect(10f, rowY, viewRect.width - 20f, LedgerLineHeight), text, style);
        rowY += LedgerLineHeight;
    }

    private static void DrawUpgradeRow(
        Rect rect,
        string label,
        int level,
        float cost,
        float funds,
        System.Action upgrade,
        int maxLevel = CommanderEconomyService.MaxLevel)
    {
        float buttonWidth = 118f;
        GUI.Label(new Rect(rect.x + 6f, rect.y + 8f, rect.width - buttonWidth - 16f, 22f), label, CommanderUiTheme.Label);
        Rect buttonRect = new(rect.xMax - buttonWidth, rect.y + 3f, buttonWidth - 4f, rect.height - 6f);
        if (level >= maxLevel)
        {
            GUI.Label(buttonRect, "MAX", CommanderUiTheme.MutedLabel);
            return;
        }

        bool oldEnabled = GUI.enabled;
        GUI.enabled = oldEnabled && funds >= cost;
        if (GUI.Button(buttonRect, $"UPGRADE {CommanderEconomyService.FundsLabel(cost)}", CommanderUiTheme.Button))
        {
            upgrade();
        }

        GUI.enabled = oldEnabled;
    }

    /// <summary>The game files factories, depots and hangars here: all of them put units on the field.</summary>
    private static bool IsProductionCategory(BuildingType type)
    {
        return type == BuildingType.FAC || type == BuildingType.DEP || type == BuildingType.HGR;
    }

    private static bool HasCategory(System.Collections.Generic.IReadOnlyList<UnitDefinition> options, string category)
    {
        for (int i = 0; i < options.Count; i++)
        {
            if (CommanderEconomyService.GetProductCategory(options[i]) == category)
            {
                return true;
            }
        }

        return false;
    }

    private void DrawWindow(int windowId)
    {
        float y = 38f;
        if (CommanderUiTheme.DrawHelpButton(windowRect.width, ref helpVisible))
        {
            string help = "Buying pays for a blueprint: your repair vehicles (Jacknifes, bought at a depot) "
                + "drive out and build it, and more of them build faster. With none nearby the "
                + "blueprint waits. INCOME builds the fuel exchange, gas export terminal and gold mine. PRODUCTION builds factories "
                + "(a ground vehicle, any aircraft, modded ones too, or a ship, which waits in reserve and launches free from the NAVAL window), "
                + "naval docks, depots, hangars and the enrichment plant. DEFENCE sells radars, air defence, supply stores and the rest. "
                + "MANAGE lists what you own with its UPGRADE, and hires a repair crew for a damaged building. "
                + "The enemy commander builds under the same rules, so all of it "
                + "is worth bombing. While siting, the preview is green where the ground is clear and "
                + "red on a road or on top of something; hold the repeat key to place several, "
                + "right-click backs out. Click anything you built for its UPGRADE and DESTROY. "
                + $"A mis-placed blueprint: {CommanderSettings.CancelBlueprint} over it, or CANCEL under MANAGE, refunds it in full.";
            y = 34f + CommanderUiTheme.DrawHelpOverlay(
                new Rect(12f, 34f, windowRect.width - 24f, 150f),
                help) + 10f;
        }

        if (GUI.Button(new Rect(windowRect.width - 34f, 3f, 26f, 22f), "X", CommanderUiTheme.ChromeButton))
        {
            Hide();
            return;
        }

        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        float funds = hq != null ? hq.factionFunds : 0f;
        string fundsLine = $"FUNDS {CommanderEconomyService.FundsLabel(funds)}"
            + $"   |   INCOME {CommanderEconomyService.SignedFundsLabel(service.FriendlyNetIncomePerMinute)}/MIN";

        // Enriched uranium only once there is some: a nuclear store is paid for out of this, and the
        // game shows the count on its own map panels, not here where the plant is bought.
        int warheads = CommanderEconomyService.GetFriendlyWarheads();
        if (warheads > 0)
        {
            fundsLine += $"   |   ENRICHED URANIUM {warheads}";
        }

        GUI.Label(
            new Rect(12f, y, windowRect.width - 24f, 22f),
            fundsLine,
            CommanderUiTheme.Caption);
        y += 26f;

        // A blueprint that silently never starts reads as a broken build button.
        if (hq != null && service.HasBlueprintWithoutBuilders(hq))
        {
            GUI.Label(
                new Rect(12f, y, windowRect.width - 24f, 36f),
                "! A blueprint is waiting for a builder. Buy a repair vehicle (Jacknife) at a depot, or select one and right-click the blueprint.",
                CommanderUiTheme.WarningLabel);
            y += 40f;
        }

        y = DrawTabs(y);

        switch (tab)
        {
            case Tab.Production:
                DrawProduction(y, funds);
                break;
            case Tab.Defence:
                DrawCatalog(y, funds, production: false);
                break;
            case Tab.Manage:
                DrawManage(y, funds);
                break;
            default:
                DrawIncome(y, funds);
                break;
        }

        string status = service.PlacementStatus;
        if (tab == Tab.Manage && !string.IsNullOrWhiteSpace(repairService.StatusText))
        {
            status = repairService.StatusText;
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            GUI.Label(
                new Rect(14f, windowRect.height - 56f, windowRect.width - 28f, 40f),
                status,
                CommanderUiTheme.MutedLabel);
        }

        GUI.DragWindow(new Rect(0f, 0f, windowRect.width - 72f, 28f));
    }

    private float DrawTabs(float y)
    {
        float width = (windowRect.width - 24f) / 4f;
        DrawTab(new Rect(12f, y, width - 4f, 26f), Tab.Income, "INCOME");
        DrawTab(new Rect(12f + width, y, width - 4f, 26f), Tab.Production, "PRODUCTION");
        DrawTab(new Rect(12f + (width * 2f), y, width - 4f, 26f), Tab.Defence, "DEFENCE");

        // The count is the damaged buildings, the one thing on MANAGE that asks for the player.
        DrawTab(
            new Rect(12f + (width * 3f), y, width - 4f, 26f),
            Tab.Manage,
            repairService.DamagedBuildings.Count > 0 ? $"MANAGE ({repairService.DamagedBuildings.Count})" : "MANAGE");
        return y + 34f;
    }

    private void DrawTab(Rect rect, Tab value, string label)
    {
        if (GUI.Button(rect, label, tab == value ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button)
            && tab != value)
        {
            tab = value;
            scroll = Vector2.zero;
            productionListOpen = false;
            service.CancelBuild();
        }
    }

    private void DrawIncome(float y, float funds)
    {
        // One card each, in the order the service lists them: the cheapest starter up to the gold mine.
        int incomeCount = CommanderEconomyService.IncomeKinds.Count;
        float width = windowRect.width - 24f;
        float incomeWidth = (width - (CardGap * (incomeCount - 1))) / incomeCount;
        for (int i = 0; i < incomeCount; i++)
        {
            CommanderBuildKind kind = CommanderEconomyService.IncomeKinds[i];
            float cost = CommanderEconomyService.GetBuildCost(kind);
            DrawBuildCard(
                new Rect(12f + (i * (incomeWidth + CardGap)), y, incomeWidth, CardHeight),
                kind,
                CommanderEconomyService.GetIncomeName(kind),
                CommanderEconomyService.FundsLabel(cost),
                $"+{CommanderEconomyService.FundsLabel(CommanderEconomyService.GetIncomePerMinute(kind, 1))}/MIN",
                funds,
                cost);
        }

        y += CardHeight + CardGap;
        GUI.Label(new Rect(12f, y, width, 60f), IncomeNote, CommanderUiTheme.MutedLabel);
    }

    private void DrawProduction(float y, float funds)
    {
        float width = windowRect.width - 24f;
        UnitDefinition? production = service.SelectedProduction;
        float half = (width - CardGap) * 0.5f;

        // The price follows what the plant will build: a tank line costs more than a light one.
        float factoryCost = CommanderEconomyService.GetFactoryBuildCost(production);
        DrawBuildCard(
            new Rect(12f, y, half, CardHeight),
            CommanderBuildKind.Factory,
            production != null ? $"{CommanderGameAccess.GetVehicleLabel(production)} factory" : "Factory",
            CommanderEconomyService.FundsLabel(factoryCost),
            $"1 EVERY {CommanderEconomyService.FactoryProductionSeconds / 60f:0.#} MIN",
            funds,
            factoryCost);
        DrawBuildCard(
            new Rect(12f + half + CardGap, y, half, CardHeight),
            CommanderBuildKind.NavalDock,
            "Naval dock",
            CommanderEconomyService.FundsLabel(CommanderEconomyService.NavalDockBuildCost),
            "ON THE SHORE",
            funds,
            CommanderEconomyService.NavalDockBuildCost);
        y += CardHeight + CardGap;

        bool oldEnabled = GUI.enabled;
        GUI.Label(new Rect(12f, y + 4f, 150f, 20f), "FACTORY MAKES", CommanderUiTheme.Caption);
        GUI.enabled = oldEnabled && production != null;
        string productLabel = production != null
            ? $"{CommanderGameAccess.GetVehicleLabel(production)}   ({CommanderEconomyService.GetProductCategory(production)})"
            : "Nothing available";
        if (GUI.Button(
            new Rect(130f, y, windowRect.width - 142f, 28f),
            new GUIContent(
                productLabel + (productionListOpen ? "   ^" : "   v"),
                "What the next factory you build will make: a ground vehicle, any aircraft or a ship. Fixed once it is built."),
            productionListOpen ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            productionListOpen = !productionListOpen;
            productionFilter = CommanderEconomyService.GetProductCategory(production);
            scroll = Vector2.zero;
        }

        GUI.enabled = oldEnabled;
        y += 36f;

        // The list takes the rest of the tab while it is open, so nothing underneath it can take the click.
        if (productionListOpen)
        {
            DrawProductionList(y);
            return;
        }

        DrawCatalog(y, funds, production: true);
    }

    /// <summary>Everything the player owns: damaged buildings first, they are the rows that want a click.</summary>
    private void DrawManage(float y, float funds)
    {
        System.Collections.Generic.IReadOnlyList<Unit> damaged = repairService.DamagedBuildings;
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        int blueprints = hq != null ? CommanderEconomyService.CountBlueprints(hq) : 0;
        int rows = blueprints + (blueprints > 0 ? 1 : 0) + damaged.Count + service.Mines.Count + service.Factories.Count + service.NavalDocks.Count + 4;
        Rect viewRect = BeginList(y, (rows * RowHeight) + EconomySheetHeight + 8f);
        float rowY = 4f;

        // Only while there is one: it is the way back from a mis-placed blueprint, not a standing list.
        if (blueprints > 0)
        {
            rowY = DrawSection(viewRect, rowY, "UNDER CONSTRUCTION   CANCEL REFUNDS IN FULL", blueprints, string.Empty);
            System.Collections.Generic.IReadOnlyList<CommanderEconomyService.ConstructionSite> sites = CommanderEconomyService.Blueprints!;
            for (int i = 0; i < sites.Count; i++)
            {
                if (ReferenceEquals(sites[i].Hq, hq))
                {
                    DrawBlueprintRow(new Rect(4f, rowY, viewRect.width - 8f, RowHeight - 6f), sites[i]);
                    rowY += RowHeight;
                }
            }
        }

        rowY = DrawSection(
            viewRect,
            rowY,
            $"NEEDS REPAIR   CREW {CommanderEconomyService.FundsLabel(CommanderRepairService.CrewCost)} EACH",
            damaged.Count,
            "Nothing of yours needs repair.");
        for (int i = 0; i < damaged.Count; i++)
        {
            DrawRepairRow(new Rect(4f, rowY, viewRect.width - 8f, RowHeight - 6f), damaged[i], funds);
            rowY += RowHeight;
        }

        rowY = DrawSection(viewRect, rowY, "INCOME", service.Mines.Count, "None built.");
        for (int i = 0; i < service.Mines.Count; i++)
        {
            Unit building = service.Mines[i];
            DrawUpgradeRow(
                new Rect(4f, rowY, viewRect.width - 8f, RowHeight - 6f),
                service.DescribeIncome(building),
                service.GetIncomeLevel(building),
                service.GetIncomeUpgradeCost(building),
                funds,
                () => service.UpgradeIncome(building));
            rowY += RowHeight;
        }

        rowY = DrawSection(viewRect, rowY, "FACTORIES", service.Factories.Count, "No friendly factories.");
        for (int i = 0; i < service.Factories.Count; i++)
        {
            Factory factory = service.Factories[i];
            int level = service.GetFactoryLevel(factory);
            string produces = factory.ProductionUnit != null ? factory.ProductionUnit.code : "?";
            DrawUpgradeRow(
                new Rect(4f, rowY, viewRect.width - 8f, RowHeight - 6f),
                $"{produces} FACTORY   LVL {level}/{CommanderEconomyService.GetFactoryMaxLevel(factory)}   {level}/cycle",
                level,
                CommanderEconomyService.GetFactoryUpgradeCost(factory, level),
                funds,
                () => service.UpgradeFactory(factory),
                CommanderEconomyService.GetFactoryMaxLevel(factory));
            rowY += RowHeight;
        }

        rowY = DrawSection(
            viewRect,
            rowY,
            "NAVAL DOCKS",
            service.NavalDocks.Count,
            "None built. A dock has to stand on the shore; without one no ships can be bought.");
        for (int i = 0; i < service.NavalDocks.Count; i++)
        {
            Unit dock = service.NavalDocks[i];
            int level = service.GetNavalDockLevel(dock);
            string dockLabel = $"NAVAL DOCK   LVL {level}/{CommanderEconomyService.MaxLevel}   "
                + CommanderNavalPurchaseService.GetLevelUnlockLabel(level);
            DrawUpgradeRow(
                new Rect(4f, rowY, viewRect.width - 8f, RowHeight - 6f),
                dockLabel,
                level,
                CommanderEconomyService.GetNavalDockUpgradeCost(level),
                funds,
                () => service.UpgradeNavalDock(dock));
            rowY += RowHeight;
        }

        DrawEconomySheet(viewRect, rowY, funds);
        GUI.EndScrollView();
    }

    /// <summary>
    /// The ledger under MANAGE: every income line, every upkeep line, and what is left of it a
    /// minute, which is the figure the command bar shows.
    /// </summary>
    private void DrawEconomySheet(Rect viewRect, float rowY, float funds)
    {
        float net = service.FriendlyNetIncomePerMinute;
        float gross = service.FriendlyGrossIncomePerMinute;
        float upkeep = service.FriendlyUpkeepPerMinute;
        rowY = CommanderUiTheme.DrawSection(
            new Rect(8f, rowY + 8f, viewRect.width - 16f, 20f),
            $"ECONOMY   NET {CommanderEconomyService.SignedFundsLabel(net)}/MIN");

        rowY = DrawLedgerCaption(viewRect, rowY, "INCOME");
        for (int i = 0; i < CommanderEconomyService.IncomeKinds.Count; i++)
        {
            int count = service.GetLedgerIncomeCount(i);
            DrawLedgerRow(
                viewRect,
                rowY,
                CommanderEconomyService.GetIncomeName(CommanderEconomyService.IncomeKinds[i]),
                count > 0 ? $"{count} built   avg level {(float)service.GetLedgerIncomeLevels(i) / count:0.#}" : "none built",
                count > 0 ? $"+{CommanderEconomyService.FundsLabel(service.GetLedgerIncome(i))}/min" : "-",
                CommanderUiTheme.Label,
                CommanderUiTheme.Label);
            rowY += LedgerLineHeight;
        }

        DrawLedgerRow(viewRect, rowY, "Gross income", string.Empty, $"+{CommanderEconomyService.FundsLabel(gross)}/min", CommanderUiTheme.Header, CommanderUiTheme.Header);
        rowY += LedgerLineHeight;

        rowY = DrawLedgerCaption(
            viewRect,
            rowY,
            $"UPKEEP   {Mathf.Max(0f, CommanderSettings.DefenceUpkeepPercentPerMinute):0.##}% OF PRICE A MINUTE");
        for (int i = 0; i < CommanderEconomyService.UpkeepLineNames.Length; i++)
        {
            int count = service.GetLedgerUpkeepCount(i);
            DrawLedgerRow(
                viewRect,
                rowY,
                CommanderEconomyService.UpkeepLineNames[i],
                count > 0 ? $"{count} held   worth {CommanderEconomyService.FundsLabel(service.GetLedgerUpkeepValue(i))}" : "none",
                count > 0 ? $"-{CommanderEconomyService.FundsLabel(service.GetLedgerUpkeepCost(i))}/min" : "-",
                CommanderUiTheme.Label,
                CommanderUiTheme.Label);
            rowY += LedgerLineHeight;
        }

        DrawLedgerRow(viewRect, rowY, "Total upkeep", string.Empty, $"-{CommanderEconomyService.FundsLabel(upkeep)}/min", CommanderUiTheme.Header, CommanderUiTheme.Header);
        rowY += LedgerLineHeight + 6f;

        DrawLedgerRow(
            viewRect,
            rowY,
            "NET INCOME",
            string.Empty,
            $"{CommanderEconomyService.SignedFundsLabel(net)}/min",
            CommanderUiTheme.Header,
            net < 0f ? CommanderUiTheme.WarningLabel : CommanderUiTheme.Figure);
        rowY += LedgerLineHeight + 4f;

        string share = gross > 0f
            ? $"Upkeep takes {upkeep / gross * 100f:0}% of what you earn."
            : upkeep > 0f ? "No income: upkeep comes straight out of your funds." : "Build income buildings from the INCOME tab.";
        DrawLedgerNote(viewRect, ref rowY, share, CommanderUiTheme.MutedLabel);

        string dearest = service.LedgerDearestLabel.Length > 0
            ? $"Costliest to hold: {service.LedgerDearestLabel}, -{CommanderEconomyService.FundsLabel(service.LedgerDearestUpkeep)}/min."
            : "Nothing you own costs upkeep. Armed buildings, air defence and artillery do.";
        DrawLedgerNote(viewRect, ref rowY, dearest, CommanderUiTheme.MutedLabel);

        // Upkeep is clamped to the funds on hand, so "runs out" means "defences stop being paid
        // for out of savings", never a negative balance.
        if (net < 0f)
        {
            string outlook = funds > 0f ? $"At this rate your funds run out in about {funds / -net:0} min." : "Your funds are empty: upkeep takes everything you earn.";
            DrawLedgerNote(viewRect, ref rowY, outlook, CommanderUiTheme.WarningLabel);
        }
    }

    /// <summary>One ledger line: what it is, a muted detail beside it, and the amount at the right edge.</summary>
    private void DrawLedgerRow(Rect viewRect, float rowY, string label, string detail, string amount, GUIStyle labelStyle, GUIStyle amountStyle)
    {
        const float amountWidth = 120f;
        const float labelWidth = 170f;
        float x = 14f;
        GUI.Label(new Rect(x, rowY, labelWidth, LedgerLineHeight), label, labelStyle);
        GUI.Label(
            new Rect(x + labelWidth, rowY, viewRect.width - labelWidth - amountWidth - 34f, LedgerLineHeight),
            detail,
            CommanderUiTheme.MutedLabel);
        GUI.Label(new Rect(viewRect.width - amountWidth - 14f, rowY, amountWidth, LedgerLineHeight), amount, RightAligned(amountStyle));
    }

    /// <summary>
    /// A right-aligned copy of a theme style, rebuilt when the theme hands out a new one (a colour
    /// change rebuilds every style), so it never keeps stale colours.
    /// </summary>
    private GUIStyle RightAligned(GUIStyle source)
    {
        for (int i = 0; i < rightAlignedSources.Count; i++)
        {
            if (ReferenceEquals(rightAlignedSources[i], source))
            {
                return rightAligned[i];
            }
        }

        if (rightAlignedSources.Count > 8)
        {
            rightAlignedSources.Clear();
            rightAligned.Clear();
        }

        GUIStyle copy = new(source) { alignment = TextAnchor.MiddleRight, wordWrap = false };
        rightAlignedSources.Add(source);
        rightAligned.Add(copy);
        return copy;
    }

    /// <summary>
    /// The PRODUCES list, one category at a time: with every aircraft in the game (modded ones too)
    /// beside the ground roster, a single list was a long scroll to find anything in.
    /// </summary>
    private void DrawProductionList(float y)
    {
        System.Collections.Generic.IReadOnlyList<UnitDefinition> options = service.ProductionOptions;
        y = DrawProductionFilters(y, options);

        int rows = 0;
        for (int i = 0; i < options.Count; i++)
        {
            if (CommanderEconomyService.GetProductCategory(options[i]) == productionFilter)
            {
                rows++;
            }
        }

        Rect viewRect = BeginList(y, Mathf.Max(1, rows));
        float rowY = 4f;
        for (int i = 0; i < options.Count; i++)
        {
            UnitDefinition option = options[i];
            if (CommanderEconomyService.GetProductCategory(option) != productionFilter)
            {
                continue;
            }

            if (DrawProductRow(new Rect(4f, rowY, viewRect.width - 8f, RowHeight - 6f), option))
            {
                service.SelectProduction(i);
                productionListOpen = false;
            }

            rowY += RowHeight;
        }

        GUI.EndScrollView();
    }

    /// <summary>One tab per category the roster actually has, so a mission with no aircraft shows none.</summary>
    private float DrawProductionFilters(float y, System.Collections.Generic.IReadOnlyList<UnitDefinition> options)
    {
        int present = 0;
        for (int c = 0; c < ProductCategories.Length; c++)
        {
            if (HasCategory(options, ProductCategories[c]))
            {
                present++;
            }
        }

        if (present < 2)
        {
            return y;
        }

        float width = (windowRect.width - 24f) / present;
        float x = 12f;
        for (int c = 0; c < ProductCategories.Length; c++)
        {
            string category = ProductCategories[c];
            if (!HasCategory(options, category))
            {
                continue;
            }

            if (GUI.Button(
                new Rect(x, y, width - 4f, 24f),
                category,
                category == productionFilter ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button)
                && category != productionFilter)
            {
                productionFilter = category;
                scroll = Vector2.zero;
            }

            x += width;
        }

        return y + 30f;
    }

    /// <summary>A product row: its picture, its name, and what a plant for it costs.</summary>
    private bool DrawProductRow(Rect rect, UnitDefinition option)
    {
        bool selected = ReferenceEquals(option, service.SelectedProduction);
        bool clicked = GUI.Button(rect, GUIContent.none, selected ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button);

        Rect picture = new(rect.x + 2f, rect.y + 1f, (rect.height - 2f) * 1.45f, rect.height - 2f);
        GUI.Box(picture, string.Empty, CommanderUiTheme.Inset);
        CommanderUnitPreview.DrawStill(picture, option);

        const float priceWidth = 130f;
        GUI.Label(
            new Rect(picture.xMax + 10f, rect.y, rect.width - picture.width - priceWidth - 20f, rect.height),
            CommanderGameAccess.GetVehicleLabel(option),
            CommanderUiTheme.Label);
        GUI.Label(
            new Rect(rect.xMax - priceWidth - 6f, rect.y, priceWidth, rect.height),
            $"PLANT {CommanderEconomyService.FundsLabel(CommanderEconomyService.GetFactoryBuildCost(option))}",
            CommanderUiTheme.MutedLabel);
        return clicked;
    }

    /// <summary>
    /// The catalogue rows for PRODUCTION or DEFENCE, grouped by the category the game itself files
    /// each building under. Rows are drawn straight from the definition list, so a game patch that
    /// adds a building adds a row. DEFENCE puts the civilian buildings last: they are scenery.
    /// </summary>
    private void DrawCatalog(float y, float funds, bool production)
    {
        System.Collections.Generic.IReadOnlyList<BuildingDefinition> catalog = service.Catalog;
        shownCatalog.Clear();
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < catalog.Count; i++)
            {
                BuildingType type = catalog[i].buildingType;
                if (IsProductionCategory(type) == production && (type == BuildingType.CIV) == (pass == 1))
                {
                    shownCatalog.Add(catalog[i]);
                }
            }
        }

        if (shownCatalog.Count == 0)
        {
            GUI.Label(
                new Rect(12f, y + 6f, windowRect.width - 24f, 40f),
                catalog.Count == 0 ? "No buildings available. The encyclopedia has not loaded yet." : "Nothing else to build here.",
                CommanderUiTheme.MutedLabel);
            return;
        }

        // Two cards a row, the factory and dock cards' size, under a header per category. The
        // height walk is the drawing walk without the drawing.
        const float headerHeight = 30f;
        float contentHeight = 4f;
        int column = 0;
        for (int i = 0; i < shownCatalog.Count; i++)
        {
            if (i == 0 || shownCatalog[i].buildingType != shownCatalog[i - 1].buildingType)
            {
                contentHeight += (column > 0 ? CardHeight + CardGap : 0f) + headerHeight;
                column = 0;
            }

            column = (column + 1) % 2;
            if (column == 0)
            {
                contentHeight += CardHeight + CardGap;
            }
        }

        contentHeight += column > 0 ? CardHeight + CardGap : 0f;

        Rect viewRect = BeginList(y, contentHeight);
        float cardWidth = (viewRect.width - 8f - CardGap) * 0.5f;
        float rowY = 4f;
        column = 0;
        for (int i = 0; i < shownCatalog.Count; i++)
        {
            BuildingDefinition definition = shownCatalog[i];
            if (i == 0 || definition.buildingType != shownCatalog[i - 1].buildingType)
            {
                rowY += column > 0 ? CardHeight + CardGap : 0f;
                column = 0;
                CommanderUiTheme.DrawSection(
                    new Rect(8f, rowY + 6f, viewRect.width - 16f, 20f),
                    CommanderEconomyService.GetCategoryLabel(definition.buildingType).ToUpperInvariant());
                rowY += headerHeight;
            }

            DrawStructureCard(new Rect(4f + (column * (cardWidth + CardGap)), rowY, cardWidth, CardHeight), definition, funds);
            column = (column + 1) % 2;
            if (column == 0)
            {
                rowY += CardHeight + CardGap;
            }
        }

        GUI.EndScrollView();
    }

    /// <summary>A catalogue building as a card, the same as the factory and dock cards above it.</summary>
    private void DrawStructureCard(Rect rect, BuildingDefinition definition, float funds)
    {
        float cost = CommanderEconomyService.GetStructureCost(definition);
        bool pending = service.PendingBuild == CommanderBuildKind.Structure
            && ReferenceEquals(service.PendingStructure, definition);
        bool oldEnabled = GUI.enabled;
        GUI.enabled = oldEnabled && (pending || funds >= cost);
        if (CommanderUiTheme.DrawCard(
            rect,
            definition,
            pending ? "Placing: click again to cancel" : CommanderEconomyService.GetStructureLabel(definition),
            CommanderEconomyService.FundsLabel(cost),
            string.Empty,
            pending))
        {
            if (pending)
            {
                service.CancelBuild();
            }
            else
            {
                service.BeginStructureBuild(definition);
            }
        }

        GUI.enabled = oldEnabled;
    }

    private void DrawBlueprintRow(Rect rect, CommanderEconomyService.ConstructionSite site)
    {
        const float buttonWidth = 128f;
        bool armed = ReferenceEquals(cancelArmedSite, site);
        GUI.Label(
            new Rect(rect.x + 6f, rect.y + 8f, rect.width - buttonWidth - 16f, 22f),
            armed ? "CLICK AGAIN: THE PROGRESS IS LOST." : $"{site.Label.ToUpperInvariant()}   {site.Progress * 100f:0}% BUILT",
            armed ? CommanderUiTheme.Header : CommanderUiTheme.Label);
        if (GUI.Button(
            new Rect(rect.xMax - buttonWidth, rect.y + 3f, buttonWidth - 4f, rect.height - 6f),
            armed ? "CONFIRM" : "CANCEL",
            armed ? CommanderUiTheme.DangerButton : CommanderUiTheme.Button))
        {
            if (armed)
            {
                service.CancelBlueprint(site);
                cancelArmedSite = null;
            }
            else
            {
                cancelArmedSite = site;
            }
        }
    }

    private void DrawRepairRow(Rect rect, Unit building, float funds)
    {
        const float buttonWidth = 128f;
        float condition = CommanderGameAccess.GetUnitCondition(building) * 100f;
        GUI.Label(
            new Rect(rect.x + 6f, rect.y + 8f, rect.width - buttonWidth - 16f, 22f),
            $"{CommanderGameAccess.GetUnitLabel(building)}   {condition:0}%",
            CommanderUiTheme.Label);

        Rect buttonRect = new(rect.xMax - buttonWidth, rect.y + 3f, buttonWidth - 4f, rect.height - 6f);
        if (repairService.HasCrewAssigned(building))
        {
            GUI.Label(buttonRect, "CREW EN ROUTE", CommanderUiTheme.MutedLabel);
            return;
        }

        bool oldEnabled = GUI.enabled;
        GUI.enabled = oldEnabled && funds >= CommanderRepairService.CrewCost;
        if (GUI.Button(buttonRect, "SEND CREW", CommanderUiTheme.Button))
        {
            repairService.SendRepairCrew(building);
        }

        GUI.enabled = oldEnabled;
    }

    private Rect BeginList(float y, int rows)
    {
        return BeginList(y, (rows * RowHeight) + 8f);
    }

    private Rect BeginList(float y, float contentHeight)
    {
        Rect listRect = new(12f, y, windowRect.width - 24f, windowRect.height - y - FooterHeight);
        GUI.Box(listRect, string.Empty, CommanderUiTheme.Inset);
        contentHeight = Mathf.Max(listRect.height - 8f, contentHeight);
        Rect viewRect = new(0f, 0f, listRect.width - 22f, contentHeight);
        scroll = GUI.BeginScrollView(listRect, scroll, viewRect);
        return viewRect;
    }

    /// <summary>A build card: the building it puts up, lit while that placement is armed.</summary>
    private void DrawBuildCard(Rect rect, CommanderBuildKind kind, string title, string price, string corner, float funds, float cost)
    {
        bool pending = service.PendingBuild == kind;
        bool oldEnabled = GUI.enabled;
        GUI.enabled = oldEnabled && (pending || funds >= cost);
        if (CommanderUiTheme.DrawCard(rect, service.GetPreviewDefinition(kind), pending ? "Placing: click again to cancel" : title, price, corner, pending))
        {
            if (pending)
            {
                service.CancelBuild();
            }
            else
            {
                service.BeginBuild(kind);
            }
        }

        GUI.enabled = oldEnabled;
    }

    private void EnsurePosition()
    {
        if (positionInitialized)
        {
            return;
        }

        float width = Mathf.Min(560f, CommanderUiScale.Width - 24f);
        float height = Mathf.Min(760f, CommanderUiScale.Height - 64f);
        windowRect = new Rect(12f, 52f, width, height);
        positionInitialized = true;
    }
}

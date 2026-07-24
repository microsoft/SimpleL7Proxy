namespace chat_tester.Components.Shared;

public static class PanelDisplayStateCoordinator
{
    public const string RequestPanel = "request";
    public const string ResultPanel = "result";
    public const string RawPanel = "raw";

    public static PanelDisplayStateGroup SelectPanel(string panel)
    {
        var selectedPanel = NormalizePanel(panel, RequestPanel);

        return new PanelDisplayStateGroup(
            selectedPanel,
            selectedPanel == RequestPanel ? PanelDisplayState.Expanded : PanelDisplayState.Minimized,
            selectedPanel == ResultPanel ? PanelDisplayState.Expanded : PanelDisplayState.Minimized,
            selectedPanel == RawPanel ? PanelDisplayState.Expanded : PanelDisplayState.Minimized);
    }

    public static PanelDisplayStateGroup Normalize(
        PanelDisplayState requestState,
        PanelDisplayState resultState,
        PanelDisplayState rawState,
        string activePanel,
        string fallbackPanel = ResultPanel)
    {
        if (requestState == PanelDisplayState.Minimized &&
            resultState == PanelDisplayState.Minimized &&
            rawState == PanelDisplayState.Minimized)
        {
            return SelectPanel(fallbackPanel);
        }

        return new PanelDisplayStateGroup(
            ResolveActivePanel(requestState, resultState, rawState, activePanel),
            requestState,
            resultState,
            rawState);
    }

    private static string ResolveActivePanel(
        PanelDisplayState requestState,
        PanelDisplayState resultState,
        PanelDisplayState rawState,
        string activePanel)
    {
        var normalizedActivePanel = NormalizePanel(activePanel, string.Empty);
        if (IsPanelVisible(normalizedActivePanel, requestState, resultState, rawState))
        {
            return normalizedActivePanel;
        }

        if (requestState != PanelDisplayState.Minimized)
        {
            return RequestPanel;
        }

        if (resultState != PanelDisplayState.Minimized)
        {
            return ResultPanel;
        }

        if (rawState != PanelDisplayState.Minimized)
        {
            return RawPanel;
        }

        return ResultPanel;
    }

    private static bool IsPanelVisible(
        string panel,
        PanelDisplayState requestState,
        PanelDisplayState resultState,
        PanelDisplayState rawState)
        => panel switch
        {
            RequestPanel => requestState != PanelDisplayState.Minimized,
            ResultPanel => resultState != PanelDisplayState.Minimized,
            RawPanel => rawState != PanelDisplayState.Minimized,
            _ => false
        };

    private static string NormalizePanel(string panel, string fallbackPanel)
        => panel switch
        {
            RequestPanel => RequestPanel,
            ResultPanel => ResultPanel,
            RawPanel => RawPanel,
            _ => fallbackPanel
        };
}

public readonly record struct PanelDisplayStateGroup(
    string ActivePanel,
    PanelDisplayState Request,
    PanelDisplayState Result,
    PanelDisplayState Raw);
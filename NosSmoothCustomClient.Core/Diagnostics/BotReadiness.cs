using NosSmoothCustomClient.Configuration;
using NosSmoothCustomClient.Input;
using NosSmoothCustomClient.State;

namespace NosSmoothCustomClient.Diagnostics;

/// <summary>
/// One thing the bot can or cannot do, and why.
/// </summary>
/// <param name="Name">What it is, in the operator's words.</param>
/// <param name="Ready">Whether it will actually happen.</param>
/// <param name="Detail">The setting behind it, or the reason it is unavailable.</param>
public readonly record struct ReadinessItem(string Name, bool Ready, string Detail);

/// <summary>
/// Answers "why is the bot not doing that?" before it is asked.
/// </summary>
/// <remarks>
/// Every capability here has already failed silently at least once: a potion key that was never
/// pressed because another code path held its cooldown, a route of waypoints with no minimap point,
/// a window that was never bound. The pattern is always the same - the bot is working exactly as
/// configured, the configuration is incomplete, and nothing says which part. A decision not to act
/// is as worth reporting as an action, so this is rendered in the window and logged at startup
/// rather than left for the operator to deduce.
/// </remarks>
public static class BotReadiness
{
    /// <summary>
    /// Describes what the bot can currently do.
    /// </summary>
    /// <param name="options">The bot options.</param>
    /// <param name="state">The state manager.</param>
    /// <param name="input">The input switch, when the bot drives the client by keyboard.</param>
    /// <returns>One line per capability.</returns>
    public static IReadOnlyList<ReadinessItem> Describe(BotOptions options, ProtocolStateManager state, SwitchableGameInput? input)
    {
        var items = new List<ReadinessItem>();

        if (input is not null)
        {
            items.Add(input.CanGoLive
                ? new ReadinessItem("Fenêtre du jeu", true, input.IsLive ? "liée, les touches partent" : "liée, en simulation - clique sur JOUE")
                : new ReadinessItem("Fenêtre du jeu", false, "non liée : aucune touche ne peut partir"));
        }

        items.Add(Key("Cibler et attaquer", options.Keys.TargetAndAttack));

        var skills = options.Skills.Count(s => s.Enabled && GameKey.TryParse(s.Key, out _));
        items.Add(new ReadinessItem
        (
            "Sorts",
            skills > 0,
            skills > 0
                ? $"{skills} sort(s) actifs avec une touche"
                : "aucun sort actif avec une touche : coche-les et donne-leur une touche"
        ));

        var buffs = options.Buffs.Count(b => b.Enabled && GameKey.TryParse(b.Key, out _));
        items.Add(new ReadinessItem
        (
            "Buffs",
            buffs > 0,
            buffs > 0 ? $"{buffs} buff(s) actifs avec une touche" : "aucun buff actif avec une touche"
        ));

        items.Add(Key($"Potion de vie (sous {options.HpPotionThreshold:P0})", options.Keys.HpPotion));

        items.Add(options.Keys.MpPotion is null
            ? new ReadinessItem("Potion de mana", false, "aucune touche - désactivé volontairement")
            : Key($"Potion de mana (sous {options.MpPotionThreshold:P0})", options.Keys.MpPotion));

        items.Add(Key("Ramasser", options.Keys.Loot));
        items.Add(Route(options, state));

        if (input is not null)
        {
            items.Add(ClickMode(options, input));
        }

        return items;
    }

    private static ReadinessItem Route(BotOptions options, ProtocolStateManager state)
    {
        if (options.Waypoints.Count == 0)
        {
            return new ReadinessItem("Déplacement", false, "aucun waypoint : enregistre une route avec F9 / F10");
        }

        var clickable = options.Waypoints.Count(w => w.IsClickable);
        if (clickable == 0)
        {
            return new ReadinessItem
            (
                "Déplacement",
                false,
                $"{options.Waypoints.Count} waypoint(s) sans point de clic sur la minimap : "
                + "le bot ne peut aller nulle part. Enregistre la route avec F9 / F10"
            );
        }

        if (clickable < options.Waypoints.Count)
        {
            return new ReadinessItem
            (
                "Déplacement",
                false,
                $"seulement {clickable} waypoint(s) sur {options.Waypoints.Count} ont un point de clic"
            );
        }

        if (options.RouteMapId is { } routeMap && state.CurrentMapId >= 0 && state.CurrentMapId != routeMap)
        {
            return new ReadinessItem
            (
                "Déplacement",
                false,
                $"la route appartient à la carte {routeMap}, tu es sur la {state.CurrentMapId}"
            );
        }

        return new ReadinessItem("Déplacement", true, $"{clickable} waypoint(s) prêts");
    }

    private static ReadinessItem ClickMode(BotOptions options, SwitchableGameInput input)
    {
        var mode = input.ClickMode switch
        {
            MinimapClickMode.RealCursor => "vrai curseur : la souris bouge, la fenêtre doit être visible",
            _ => "messages postés : la souris est libre, le jeu peut tourner en arrière-plan"
        };

        return new ReadinessItem("Clic minimap", true, options.MinimapClickMode == MinimapClickMode.Auto
            ? mode + " (bascule seul si rien ne bouge)"
            : mode + " (fixé par la configuration)");
    }

    private static ReadinessItem Key(string name, string? binding)
        => GameKey.TryParse(binding, out var key)
            ? new ReadinessItem(name, true, $"touche {key.Label}")
            : new ReadinessItem(name, false, "aucune touche configurée");
}

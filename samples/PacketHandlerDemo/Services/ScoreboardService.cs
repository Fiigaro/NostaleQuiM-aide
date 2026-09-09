namespace PacketHandlerDemo.Services;

/// <summary>
/// Etat partage entre tous les appels de responders.
/// </summary>
/// <remarks>
/// Point important pour l'injection de dependances : NosSmooth enregistre les
/// responders en <c>Scoped</c> et ouvre un scope NEUF pour chaque paquet
/// (<c>ManagedPacketHandler.DispatchResponder</c>). Une instance de responder ne
/// survit donc pas d'un paquet a l'autre : tout etat qui doit persister vit dans
/// un service <c>Singleton</c> comme celui-ci, injecte dans le constructeur.
/// <para>
/// Les responders d'un meme paquet sont invoques en parallele via
/// <c>Task.WhenAll</c> : l'etat partage doit etre protege, d'ou le semaphore.
/// </para>
/// </remarks>
public class ScoreboardService
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly Dictionary<long, int> _totals = new();

    /// <summary>
    /// Ajoute un score au total du joueur et renvoie le nouveau total.
    /// </summary>
    /// <param name="playerId">L'identifiant du joueur.</param>
    /// <param name="score">Le score a ajouter.</param>
    /// <param name="ct">Le jeton d'annulation.</param>
    /// <returns>Le total cumule du joueur.</returns>
    public async Task<int> AddScoreAsync(long playerId, int score, CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            var total = _totals.TryGetValue(playerId, out var current) ? current + score : score;
            _totals[playerId] = total;
            return total;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Renvoie le classement courant, du meilleur au moins bon.
    /// </summary>
    /// <param name="ct">Le jeton d'annulation.</param>
    /// <returns>Le classement.</returns>
    public async Task<IReadOnlyList<(long PlayerId, int Total)>> GetRankingAsync(CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            return _totals
                .Select(x => (PlayerId: x.Key, Total: x.Value))
                .OrderByDescending(x => x.Total)
                .ToList();
        }
        finally
        {
            _semaphore.Release();
        }
    }
}

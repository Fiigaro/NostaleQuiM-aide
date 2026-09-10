using Remora.Results;

namespace NosSmoothCustomClient.Responders;

/// <summary>
/// Collapses several results into one.
/// </summary>
internal static class ResultAggregate
{
    /// <summary>
    /// Returns success when every result succeeded, otherwise the first failure.
    /// </summary>
    /// <param name="results">The results.</param>
    /// <returns>The combined result.</returns>
    public static Result Combine(IReadOnlyList<Result> results)
    {
        foreach (var result in results)
        {
            if (!result.IsSuccess)
            {
                return result;
            }
        }

        return Result.FromSuccess();
    }
}

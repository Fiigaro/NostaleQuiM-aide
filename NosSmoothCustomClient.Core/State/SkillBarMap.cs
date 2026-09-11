using System.Collections.Concurrent;

namespace NosSmoothCustomClient.State;

/// <summary>
/// The current skill bar: which cast id each skill VNum sits at.
/// </summary>
/// <remarks>
/// A singleton for the same reason every other piece of state here is one - responders are resolved
/// per packet, so an instance field on a responder is empty again by the time the next packet
/// arrives. The bar is rebuilt wholesale on each <c>ski</c>, because equipping a Specialist replaces
/// the whole set rather than adding to it.
/// </remarks>
public sealed class SkillBarMap
{
    private readonly ConcurrentDictionary<int, int> _castIdByVNum = new();
    private volatile int[] _vnumsByCastId = Array.Empty<int>();

    /// <summary>Gets the VNums in cast id order.</summary>
    public IReadOnlyList<int> VNums => _vnumsByCastId;

    /// <summary>Gets how many skills the bar holds.</summary>
    public int Count => _vnumsByCastId.Length;

    /// <summary>
    /// Replaces the bar.
    /// </summary>
    /// <param name="vnums">The skill VNums, in bar order.</param>
    public void Replace(IReadOnlyList<int> vnums)
    {
        _castIdByVNum.Clear();

        for (var castId = 0; castId < vnums.Count; castId++)
        {
            _castIdByVNum[vnums[castId]] = castId;
        }

        _vnumsByCastId = vnums.ToArray();
    }

    /// <summary>
    /// Finds the cast id of a skill.
    /// </summary>
    /// <param name="vnum">The skill VNum.</param>
    /// <param name="castId">The cast id, when known.</param>
    /// <returns>True when the skill is on the bar.</returns>
    public bool TryGetCastId(int vnum, out int castId)
        => _castIdByVNum.TryGetValue(vnum, out castId);
}

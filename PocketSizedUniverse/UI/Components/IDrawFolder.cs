
using System.Collections.Immutable;

namespace PocketSizedUniverse.UI.Components;

public interface IDrawFolder
{
    int TotalPairs { get; }
    int OnlinePairs { get; }
    IImmutableList<DrawUserPair> DrawPairs { get; }
    void Draw();
}

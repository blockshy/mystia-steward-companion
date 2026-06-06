namespace MystiaSteward.Core;

public interface IRecommendationStateProvider
{
    string Description { get; }
    RecommendationState LoadState();
}

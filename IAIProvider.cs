namespace AIWatcher;

public interface IAIProvider
{
    string ProviderName { get; }
    Task<IReadOnlyList<AIInstance>> GetInstancesAsync();
}

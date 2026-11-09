using System.Threading;
using System.Threading.Tasks;

namespace Ergonomy.Configuration
{
    public enum AlarmImageAssetState
    {
        Pending = 0,
        Ready = 1,
        Failed = 2
    }

    /// <summary>
    /// Self-healing alarm-image cache. Settings refresh must call
    /// <see cref="EnsureLoadedAsync"/> even when the JSON payload did not change.
    /// </summary>
    public interface IAlarmImageLoader
    {
        AlarmImageAssetState AssetState { get; }
        int CachedImageCount { get; }
        Task EnsureLoadedAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Used by Ergonomy.Service, which never renders alarm UI.
    /// </summary>
    public sealed class NoOpAlarmImageLoader : IAlarmImageLoader
    {
        public static readonly NoOpAlarmImageLoader Instance = new();
        public AlarmImageAssetState AssetState => AlarmImageAssetState.Ready;
        public int CachedImageCount => 0;
        public Task EnsureLoadedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

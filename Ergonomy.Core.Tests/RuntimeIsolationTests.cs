using Ergonomy.Core.Hosting;
using Xunit;

namespace Ergonomy.Core.Tests
{
    public sealed class RuntimeIsolationTests
    {
        [Fact]
        public void Mutex_names_are_the_v1_global_contract()
        {
            Assert.Equal(@"Global\Ergonomy_Service_Running_v1", RuntimeIsolation.ServiceRunningMutexName);
            Assert.Equal(@"Global\Ergonomy_Sqlite_Owner_v1", RuntimeIsolation.SqliteOwnerMutexName);
        }

        [Fact]
        public void TryAcquire_is_exclusive_and_abandoned_owner_can_reclaim_after_dispose()
        {
            string name = @"Local\Ergonomy.Tests.Mutex." + Guid.NewGuid().ToString("N");

            using IsolationClaim? first = RuntimeIsolation.TryAcquire(name);
            Assert.NotNull(first);

            IsolationClaim? second = RuntimeIsolation.TryAcquire(name);
            Assert.Null(second);

            first!.Dispose();

            using IsolationClaim? third = RuntimeIsolation.TryAcquire(name);
            Assert.NotNull(third);
        }
    }
}

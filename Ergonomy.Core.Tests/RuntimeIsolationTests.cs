using Ergonomy.Core.Hosting;
using Xunit;

namespace Ergonomy.Core.Tests
{
    public sealed class RuntimeIsolationTests
    {
        [Fact]
        public void Mutex_names_are_stable_contracts()
        {
            Assert.Equal(@"Global\Ergonomy_Service_Running_v1", RuntimeIsolation.ServiceRunningMutexName);
            Assert.Equal(@"Global\Ergonomy_Sqlite_Owner_v1", RuntimeIsolation.SqliteOwnerMutexName);
            Assert.Equal(@"Global\Ergonomy_Agent_SingleInstance_Mutex", RuntimeIsolation.TraySingleInstanceMutexName);
        }

        [Fact]
        public void TryAcquireMutex_second_waiter_does_not_own()
        {
            string name = @"Local\Ergonomy.Test.SqliteOwner." + Guid.NewGuid().ToString("N");
            Assert.True(RuntimeIsolation.TryAcquireMutex(name, out Mutex? first));
            Assert.NotNull(first);
            try
            {
                Assert.False(RuntimeIsolation.TryAcquireMutex(name, out Mutex? second));
                Assert.Null(second);
            }
            finally
            {
                RuntimeIsolation.Release(ref first);
            }
        }

        [Fact]
        public void TryAcquireMutex_rejects_empty_name()
        {
            Assert.False(RuntimeIsolation.TryAcquireMutex(" ", out Mutex? mutex));
            Assert.Null(mutex);
        }
    }
}

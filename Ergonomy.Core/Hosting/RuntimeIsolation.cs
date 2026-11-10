using System;
using System.Threading;
using Ergonomy.Core.Diagnostics;

namespace Ergonomy.Core.Hosting
{
    /// <summary>
    /// Cross-process mutexes that keep a single SQLite writer.
    /// <list type="bullet">
    /// <item><see cref="ServiceRunningMutexName"/> — held for the lifetime of Ergonomy.Service.</item>
    /// <item><see cref="SqliteOwnerMutexName"/> — held by Service, or by legacy Ergonomy.exe when Service is down.</item>
    /// </list>
    /// Ergonomy.Task never claims either mutex and never opens the database.
    /// AbandonedMutexException is treated as ownership (previous owner crashed).
    /// </summary>
    public static class RuntimeIsolation
    {
        public const string ServiceRunningMutexName = @"Global\Ergonomy_Service_Running_v1";
        public const string SqliteOwnerMutexName = @"Global\Ergonomy_Sqlite_Owner_v1";

        private static int _ownsSqlite;

        /// <summary>
        /// True when this process successfully claimed <see cref="SqliteOwnerMutexName"/>.
        /// </summary>
        public static bool ThisProcessOwnsSqlite => Volatile.Read(ref _ownsSqlite) != 0;

        /// <summary>
        /// True when this process may open/write the outbox. False when Ergonomy.Service
        /// is alive and this process is not the owner (legacy tray must not write).
        /// Tests and the single-process tray (Service down) remain allowed.
        /// </summary>
        public static bool MayWriteSqlite
            => ThisProcessOwnsSqlite || !IsServiceRunning();

        /// <summary>
        /// Service entry: claim the running mutex then the SQLite owner mutex.
        /// Both or neither. Abandoned mutex = this process owns it.
        /// </summary>
        public static IsolationClaim? TryClaimService()
        {
            Mutex? running = TryAcquireMutex(ServiceRunningMutexName);
            if (running == null)
                return null;

            Mutex? owner = TryAcquireMutex(SqliteOwnerMutexName);
            if (owner == null)
            {
                ReleaseAndDispose(running);
                return null;
            }

            MarkSqliteOwned();
            return new IsolationClaim(running, owner);
        }

        /// <summary>
        /// Legacy tray entry when Service is not running. Task must never call this.
        /// </summary>
        public static IsolationClaim? TryClaimSqliteOwner()
        {
            Mutex? owner = TryAcquireMutex(SqliteOwnerMutexName);
            if (owner == null)
                return null;
            MarkSqliteOwned();
            return new IsolationClaim(owner);
        }

        /// <summary>
        /// True when the Service running mutex already exists (does not take ownership).
        /// </summary>
        public static bool IsServiceRunning()
        {
            try
            {
                using var existing = Mutex.OpenExisting(ServiceRunningMutexName);
                return true;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                return false;
            }
            catch (AbandonedMutexException)
            {
                return false;
            }
            catch (Exception ex)
            {
                ExceptionPolicy.IgnoreBestEffortDispose(ex, "service-running-probe");
                return false;
            }
        }

        /// <summary>Test helper: acquire an arbitrary named mutex. Does not mark SQLite ownership.</summary>
        internal static IsolationClaim? TryAcquire(string name)
        {
            Mutex? mutex = TryAcquireMutex(name);
            return mutex == null ? null : new IsolationClaim(mutex);
        }

        private static void MarkSqliteOwned()
            => Interlocked.Exchange(ref _ownsSqlite, 1);

        private static Mutex? TryAcquireMutex(string name)
        {
            Mutex? mutex = null;
            try
            {
                mutex = new Mutex(initiallyOwned: true, name, out bool createdNew);
                if (createdNew)
                    return mutex;

                mutex.Dispose();
                return null;
            }
            catch (AbandonedMutexException ex)
            {
                return ex.Mutex ?? mutex;
            }
            catch (Exception ex)
            {
                ExceptionPolicy.IgnoreBestEffortDispose(ex, "mutex-acquire");
                try { mutex?.Dispose(); }
                catch (Exception disposeEx) { ExceptionPolicy.IgnoreBestEffortDispose(disposeEx, "mutex-acquire-dispose"); }
                return null;
            }
        }

        internal static void ReleaseAndDispose(Mutex? mutex)
        {
            if (mutex == null)
                return;
            try { mutex.ReleaseMutex(); }
            catch (Exception ex) { ExceptionPolicy.IgnoreBestEffortDispose(ex, "mutex-release"); }
            try { mutex.Dispose(); }
            catch (Exception ex) { ExceptionPolicy.IgnoreBestEffortDispose(ex, "mutex-dispose"); }
        }
    }

    /// <summary>Holds one or two named mutexes for the process lifetime.</summary>
    public sealed class IsolationClaim : IDisposable
    {
        private Mutex? _first;
        private Mutex? _second;
        private int _disposed;

        internal IsolationClaim(Mutex first, Mutex? second = null)
        {
            _first = first ?? throw new ArgumentNullException(nameof(first));
            _second = second;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            RuntimeIsolation.ReleaseAndDispose(_first);
            RuntimeIsolation.ReleaseAndDispose(_second);
            _first = null;
            _second = null;
        }
    }
}

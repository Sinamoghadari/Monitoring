using System;
using System.Threading;
using Ergonomy.Diagnostics;

namespace Ergonomy.Core.Hosting
{
    /// <summary>
    /// Dual-runtime isolation: exactly one process may own the SQLCipher outbox.
    /// <see cref="Ergonomy.Service"/> is the production owner. The legacy tray
    /// (<c>Ergonomy.exe</c>) yields when the Service is running and must not open SQLite.
    /// <see cref="Ergonomy.Task"/> never takes these mutexes (it does not write SQLite).
    /// </summary>
    public static class RuntimeIsolation
    {
        /// <summary>Held for the lifetime of Ergonomy.Service so the tray can detect it.</summary>
        public const string ServiceRunningMutexName = @"Global\Ergonomy_Service_Running_v1";

        /// <summary>Exclusive owner of <c>C:\ProgramData\Ergonomy\ergonomy_local.db</c>.</summary>
        public const string SqliteOwnerMutexName = @"Global\Ergonomy_Sqlite_Owner_v1";

        /// <summary>Existing tray single-instance mutex. Service must never take this name.</summary>
        public const string TraySingleInstanceMutexName = @"Global\Ergonomy_Agent_SingleInstance_Mutex";

        /// <summary>
        /// True when a Service process still holds <see cref="ServiceRunningMutexName"/>.
        /// <see cref="UnauthorizedAccessException"/> is treated as present: a LocalSystem-created
        /// mutex is invisible to a standard user via OpenExisting, which still means Service is up.
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
            catch (UnauthorizedAccessException)
            {
                return true;
            }
            catch (Exception ex)
            {
                ExceptionPolicy.IgnoreBestEffortDispose(ex, nameof(RuntimeIsolation));
                return false;
            }
        }

        /// <summary>
        /// Service entry: claim "service is running" then exclusive SQLite ownership.
        /// If the legacy tray already owns the outbox, both claims are released and the
        /// caller must exit without opening SQLCipher.
        /// </summary>
        public static bool TryClaimService(
            out Mutex? serviceRunning,
            out Mutex? sqliteOwner,
            out string? reason)
        {
            serviceRunning = null;
            sqliteOwner = null;
            reason = null;

            if (!TryAcquireMutex(ServiceRunningMutexName, out serviceRunning) || serviceRunning == null)
            {
                reason = "Another Ergonomy.Service instance is already running.";
                return false;
            }

            if (!TryAcquireMutex(SqliteOwnerMutexName, out sqliteOwner) || sqliteOwner == null)
            {
                Release(ref serviceRunning);
                reason = "SQLite outbox is owned by another process (legacy Ergonomy.exe). Stop the tray before starting Ergonomy.Service.";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Legacy tray: take exclusive SQLite ownership. Returns false when the Service
        /// (or another tray) already owns the database — caller must not open SQLCipher.
        /// </summary>
        public static bool TryClaimSqliteOwner(out Mutex? sqliteOwner, out string? reason)
        {
            sqliteOwner = null;
            reason = null;
            if (!TryAcquireMutex(SqliteOwnerMutexName, out sqliteOwner) || sqliteOwner == null)
            {
                reason = "SQLite outbox is already owned (Ergonomy.Service is the production writer).";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Creates or opens a named mutex and tries to own it without blocking.
        /// An abandoned mutex (previous crash) is treated as ownership.
        /// </summary>
        public static bool TryAcquireMutex(string name, out Mutex? mutex)
        {
            mutex = null;
            if (string.IsNullOrWhiteSpace(name))
                return false;

            Mutex? created = null;
            try
            {
                created = new Mutex(initiallyOwned: true, name, out bool createdNew);
                if (createdNew)
                {
                    mutex = created;
                    return true;
                }

                if (created.WaitOne(TimeSpan.Zero))
                {
                    mutex = created;
                    return true;
                }

                created.Dispose();
                return false;
            }
            catch (AbandonedMutexException ex)
            {
                mutex = ex.Mutex ?? created;
                return mutex != null;
            }
            catch (UnauthorizedAccessException)
            {
                created?.Dispose();
                mutex = null;
                return false;
            }
            catch (Exception ex)
            {
                created?.Dispose();
                ExceptionPolicy.Report(
                    ExceptionSeverity.Operational,
                    ex,
                    new ExceptionReportContext
                    {
                        Module = nameof(RuntimeIsolation),
                        Message = "Failed to acquire mutex " + name
                    });
                mutex = null;
                return false;
            }
        }

        public static void Release(ref Mutex? mutex)
        {
            Mutex? local = mutex;
            mutex = null;
            if (local == null)
                return;

            try
            {
                local.ReleaseMutex();
            }
            catch (ApplicationException ex)
            {
                ExceptionPolicy.IgnoreBestEffortDispose(ex, nameof(RuntimeIsolation));
            }
            catch (ObjectDisposedException ex)
            {
                ExceptionPolicy.IgnoreIfShuttingDown(ex, nameof(RuntimeIsolation));
            }
            catch (Exception ex)
            {
                ExceptionPolicy.IgnoreBestEffortDispose(ex, nameof(RuntimeIsolation));
            }

            try
            {
                local.Dispose();
            }
            catch (Exception ex)
            {
                ExceptionPolicy.IgnoreBestEffortDispose(ex, nameof(RuntimeIsolation));
            }
        }
    }
}

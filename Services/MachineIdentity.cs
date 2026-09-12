using System;
using Ergonomy.Logging;

namespace Ergonomy.Services
{
    /// <summary>
    /// Immutable stable identity of the device/agent used for logging and payload labels.
    /// Avoids retrieving WindowsIdentity multiple times and keeps usernames out of
    /// high-cardinality observability labels.
    /// </summary>
    public sealed class MachineIdentity
    {
        public string WindowsSid { get; }
        public string WindowsUsername { get; }
        public string WindowsUsernameRunAdmin { get; }
        public string MachineName { get; }
        public string SessionGuid { get; }

        /// <summary>
        /// هویت پایدار عامل را با SID، نام کاربری، نام ماشین و یک شناسه نشست تازه می‌سازد.
        /// </summary>
        public MachineIdentity(
            string windowsSid,
            string windowsUsername,
            string machineName,
            string? windowsUsernameRunAdmin = null)
        {
            WindowsSid = windowsSid;
            WindowsUsername = windowsUsername;
            WindowsUsernameRunAdmin = AppLogNormalizer.NormalizeWindowsUsernameRunAdmin(
                windowsUsernameRunAdmin,
                windowsUsername);
            MachineName = machineName;
            SessionGuid = Guid.NewGuid().ToString();
        }
    }
}

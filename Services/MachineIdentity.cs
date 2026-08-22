using System;

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
        public string MachineName { get; }
        public string SessionGuid { get; }

        public MachineIdentity(string windowsSid, string windowsUsername, string machineName)
        {
            WindowsSid = windowsSid;
            WindowsUsername = windowsUsername;
            MachineName = machineName;
            SessionGuid = Guid.NewGuid().ToString();
        }
    }
}

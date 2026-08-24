using System;

namespace ReportGenerator.Repositories
{
    /// <summary>The instance is not registered in the report generator database yet.</summary>
    public sealed class InstanceNotFoundException : Exception
    {
        public InstanceNotFoundException(string instanceName)
            : base("Instance " + instanceName + " not found in database")
        {
            InstanceName = instanceName;
        }

        public string InstanceName { get; }
    }

    /// <summary>The requested instance differs from the one this deployment is pinned to.</summary>
    public sealed class InstanceForcedException : Exception
    {
        public InstanceForcedException(string instanceName, string forcedInstanceName)
            : base("This deployment only serves instance '" + forcedInstanceName + "', not '" + instanceName + "'")
        {
            InstanceName = instanceName;
            ForcedInstanceName = forcedInstanceName;
        }

        public string InstanceName { get; }
        public string ForcedInstanceName { get; }
    }
}

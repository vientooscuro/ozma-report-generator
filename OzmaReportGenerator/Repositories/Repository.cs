using System;
using System.IO;
using System.Linq;
using System.Data.Common;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ReportGenerator.Models;

namespace ReportGenerator.Repositories
{
    public abstract class Repository : IDisposable
    {
        protected ReportGeneratorContext dbContext;
        protected Instance instance { get; private set; } = null!;

        public void Dispose()
        {
            dbContext.Dispose();
        }

        public Repository(IConfiguration configuration, string instanceName, bool createInstanceIfNotExists = false)
        {
            if (string.IsNullOrEmpty(instanceName))
                throw new Exception("Instance name cannot be empty");
            var forceInstance = configuration.GetValue<string>("OzmaDBSettings:ForceInstance");
            if (forceInstance != null && forceInstance != instanceName)
                throw new InstanceForcedException(instanceName, forceInstance);
            dbContext = new ReportGeneratorContext(configuration);
            Instance? instance;
            try
            {
                instance = dbContext.Instances.FirstOrDefault(p => p.Name == instanceName);
            }
            catch (DbException e)
            {
                // Table doesn't exist
                if (e.SqlState == "42P01")
                {
                    DatabaseBootstrap.ApplySchemaScript(dbContext);
                    instance = dbContext.Instances.FirstOrDefault(p => p.Name == instanceName);
                }
                else
                {
                    throw;
                }
            }
            if (instance == null)
            {
                if (createInstanceIfNotExists)
                {
                    var newInstance = new Instance { Name = instanceName };
                    dbContext.Instances.Add(newInstance);
                    dbContext.SaveChanges();
                    instance = newInstance;
                }
                else
                    throw new InstanceNotFoundException(instanceName);
            }
            this.instance = instance;
        }
    }
}

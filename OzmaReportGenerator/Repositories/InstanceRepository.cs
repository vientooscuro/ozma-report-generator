using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ReportGenerator.Models;

namespace ReportGenerator.Repositories
{
    /// <summary>Reads the instance list without binding to a particular instance.</summary>
    public sealed class InstanceRepository : IDisposable
    {
        private readonly ReportGeneratorContext dbContext;

        public InstanceRepository(IConfiguration configuration)
        {
            dbContext = new ReportGeneratorContext(configuration);
        }

        public async Task<List<string>> LoadAllInstanceNames()
        {
            try
            {
                return await dbContext.Instances.AsNoTracking().Select(p => p.Name).ToListAsync();
            }
            catch (DbException e) when (e.SqlState == "42P01")
            {
                // Table doesn't exist yet.
                DatabaseBootstrap.ApplySchemaScript(dbContext);
                return await dbContext.Instances.AsNoTracking().Select(p => p.Name).ToListAsync();
            }
        }

        public void Dispose()
        {
            dbContext.Dispose();
        }
    }
}

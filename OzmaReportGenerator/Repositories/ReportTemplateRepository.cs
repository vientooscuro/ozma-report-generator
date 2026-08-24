using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ReportGenerator.Models;

namespace ReportGenerator.Repositories
{
    public class ReportTemplateRepository : Repository
    {
        public ReportTemplateRepository(IConfiguration configuration, string instanceName, bool createInstanceIfNotExists = false) : base(configuration,
            instanceName, createInstanceIfNotExists)
        {

        }

        public async Task AddTemplate(ReportTemplate template)
        {
            var vTemplate = new VReportTemplate
            {
                Schema = template.Schema,
                SchemaId = template.SchemaId,
                Name = template.Name,
                ReportTemplate = template,
            };
            await dbContext.VReportTemplates.AddAsync(vTemplate);
            await dbContext.SaveChangesAsync();
        }

        public async Task UpdateTemplate(ReportTemplate template)
        {
            dbContext.Entry(template).State = EntityState.Modified;
            await dbContext.SaveChangesAsync();
        }

        public async Task<List<VReportTemplate>> LoadAllTemplates()
        {
            return await dbContext.VReportTemplates.Include(p => p.Schema).Where(p => p.Schema.InstanceId == instance.Id).AsNoTracking()
                .ToListAsync();
        }

        public async Task DeleteTemplate(int id)
        {
            var item = dbContext.VReportTemplates.First(p =>
                (p.Schema.InstanceId == instance.Id) && (p.Id == id));
            dbContext.Remove(item);
            await dbContext.SaveChangesAsync();
        }

        public async Task<Dictionary<int, int>> LoadQueryCounts()
        {
            return await dbContext.ReportTemplateQueries
                .Where(q => q.Template.Schema.InstanceId == instance.Id)
                .GroupBy(q => q.TemplateId)
                .Select(g => new { TemplateId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.TemplateId, x => x.Count);
        }

        public async Task<ReportTemplate?> LoadTemplate(string schemaName, string templateName)
        {
            return await dbContext.ReportTemplates.AsNoTracking().Include(p => p.ReportTemplateQueries)
                .FirstOrDefaultAsync(p =>
                    (p.Schema.InstanceId == instance.Id) && (p.Schema.Name == schemaName) && (p.Name == templateName));
        }

        public async Task<ReportTemplate?> LoadTemplate(int templateId)
        {
            return await dbContext.ReportTemplates.Include(p => p.ReportTemplateQueries)
                .FirstOrDefaultAsync(p =>
                    (p.Schema.InstanceId == instance.Id) && (p.Id == templateId));
        }
    }
}

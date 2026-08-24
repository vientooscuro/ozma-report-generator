using System;
using System.IO;
using Microsoft.EntityFrameworkCore;
using ReportGenerator.Models;

namespace ReportGenerator.Repositories
{
    public static class DatabaseBootstrap
    {
        /// <summary>Applies db/db.sql when the tables are missing (PostgreSQL error 42P01).</summary>
        public static void ApplySchemaScript(ReportGeneratorContext dbContext)
        {
            var dbScript = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "db", "db.sql");
            var dbScriptContent = File.ReadAllText(dbScript);
            dbContext.Database.ExecuteSqlRaw(dbScriptContent);
        }
    }
}

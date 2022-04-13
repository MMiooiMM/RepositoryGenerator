using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Duotify.EFCore.RepositoryGenerator.Properties;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Duotify.EFCore.RepositoryGenerator
{
    public class App : IHostedService
    {
        private int? _exitCode;

        private readonly ILogger<App> logger;
        private readonly IHostApplicationLifetime appLifetime;

        public App(ILogger<App> logger, IHostApplicationLifetime appLifetime)
        {
            this.logger = logger;
            this.appLifetime = appLifetime;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                logger.LogTrace(Resources.WorkerStarted(DateTimeOffset.Now));

                var runner = new CommandRunner("efr", "\nUsage: efr generate -c ContosoUniversityContext -v -o Models\n");

                runner.SubCommand("list", "Lists available DbContext types.", c =>
                {
                    c.Option("project", "project", "p", Resources.ProjectOptionDescription);
                    c.OnRun((namedArgs) =>
                    {
                        var assembly = GetAssembly(namedArgs.GetValueOrDefault("project"));

                        var dbContextNames = GetDbContextTypesFromAssembly(assembly).Select(type => GetFullName(type));

                        var sb = new StringBuilder();

                        foreach (var dbContextName in dbContextNames)
                        {
                            sb.AppendLine(dbContextName);
                        }

                        Reporter.WriteData(sb.ToString());

                        return 1;
                    });
                });

                runner.SubCommand("generate", "說明待補", c =>
                {
                    c.Option("output", "output-dir", "o", Resources.OutputOptionDescription);
                    c.Option("project", "project", "p", Resources.ProjectOptionDescription);
                    c.Option("context", "context", "c", Resources.ContextOptionDescription);
                    c.Option("verbose", "verbose", "v", Resources.VerboseOptionDescription, true);
                    c.OnRun((namedArgs) =>
                    {
                        Reporter.IsVerbose = namedArgs.ContainsKey("verbose");

                        var assembly = GetAssembly(namedArgs.GetValueOrDefault("project"));

                        var dbContextTypes = GetDbContextTypesFromAssembly(assembly);                        

                        if (!string.IsNullOrWhiteSpace(namedArgs.GetValueOrDefault("context")))
                        {
                            dbContextTypes = dbContextTypes
                            .Where(t => t.Name.Equals(namedArgs.GetValueOrDefault("context")));
                        }

                        CreateFiles(dbContextTypes.FirstOrDefault(), namedArgs.GetValueOrDefault("output"));

                        return 1;
                    });
                });

                _exitCode = runner.Run(Environment.GetCommandLineArgs().Skip(1));
            }
            catch (CommandException ex)
            {
                Reporter.WriteError(ex.Message);
                _exitCode = 1;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, Resources.UnhandledException);
                _exitCode = 1;
            }
            finally
            {
                appLifetime.StopApplication();
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            logger.LogTrace(Resources.WorkerStopped(DateTimeOffset.Now));

            Environment.ExitCode = _exitCode.GetValueOrDefault(-1);

            return Task.CompletedTask;
        }

        /// <summary>
        /// 取得 Assembly
        /// </summary>
        /// <param name="projectPath"></param>
        /// <returns></returns>
        private Assembly GetAssembly(string projectPath)
        {
            var project = GetAndBuildProject(projectPath);

            return GetAssemblyFromProject(project);
        }

        /// <summary>
        /// 取得專案資訊並建置專案
        /// </summary>
        /// <param name="projectPath"></param>
        /// <returns></returns>
        private Project GetAndBuildProject(string projectPath)
        {
            var projectFile = ResolveProject(projectPath);

            var project = Project.FromFile(projectFile, null);

            Reporter.WriteInformation(Resources.BuildStarted);
            project.Build();
            Reporter.WriteInformation(Resources.BuildSucceeded);

            return project;
        }

        /// <summary>
        /// 取得專案檔，若路徑中有零筆或多筆專案檔，則拋出例外
        /// </summary>
        /// <param name="projectPath"></param>
        /// <returns></returns>
        private static string ResolveProject(string projectPath)
        {
            var projects = GetProjectFiles(projectPath);

            return projects.Count switch
            {
                0 => throw new CommandException(projectPath != null
                    ? Resources.NoProjectInDirectory(projectPath)
                    : Resources.NoProject),
                > 1 => throw new CommandException(projectPath != null
                    ? Resources.MultipleProjectsInDirectory(projectPath)
                    : Resources.MultipleProjects),
                _ => projects[0],
            };
        }

        /// <summary>
        /// 取得 proj 檔案列表
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        private static List<string> GetProjectFiles(string path)
        {
            if (path == null)
            {
                path = Directory.GetCurrentDirectory();
            }
            else if (!Path.IsPathRooted(path))
            {
                path = Path.Combine(Directory.GetCurrentDirectory(), path);
            }

            if (!Directory.Exists(path))
            {
                return new List<string> { path };
            }

            var projectFiles = Directory.EnumerateFiles(path, "*.*proj", SearchOption.TopDirectoryOnly)
                .Where(f => !string.Equals(Path.GetExtension(f), ".xproj", StringComparison.OrdinalIgnoreCase))
                .Take(2).ToList();

            return projectFiles;
        }

        /// <summary>
        /// 透過 project 取得 Assembly
        /// </summary>
        /// <param name="path"></param>
        /// <param name="name"></param>
        /// <returns></returns>
        private static Assembly GetAssemblyFromProject(Project project)
        {
            var targetDir = Path.GetFullPath(Path.Combine(project.ProjectDir, project.OutputPath));

            string localPath = string.IsNullOrEmpty(targetDir)
                ? Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
                : targetDir;

            string assemblyFilePath = Path.Combine(localPath, project.TargetFileName);

            if (!File.Exists(assemblyFilePath))
            {
                throw new Exception(Resources.AssemblyFileNotFound(assemblyFilePath));
            }

            return Assembly.LoadFrom(assemblyFilePath);
        }

        /// <summary>
        /// 從 Assembly 取得 DbContext 的 Type
        /// </summary>
        /// <param name="assembly"></param>
        /// <returns></returns>
        private static IEnumerable<Type> GetDbContextTypesFromAssembly(Assembly assembly)
        {
            return GetTypesFromAssembly(assembly)
                .Where(t => t != null && CheckIfTypeInheritanceDbContext(t));
        }

        /// <summary>
        /// 從 Assembly 取得 DbContext 的 Type
        /// </summary>
        /// <param name="assembly"></param>
        /// <returns></returns>
        private static IEnumerable<Type> GetTypesFromAssembly(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                return e.Types;
            }
        }

        /// <summary>
        /// 檢查是否為 DbSet 的泛型類別
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        private bool CheckIfDbSetGenericType(Type type)
        {
            return type.IsGenericType && GetFullName(type).Contains("DbSet");
        }

        /// <summary>
        /// 檢查 Type 是否繼承於 DbContext
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        private static bool CheckIfTypeInheritanceDbContext(Type type)
        {
            return type.IsSubclassOf(typeof(DbContext));
        }

        /// <summary>
        /// 產生檔案
        /// </summary>
        /// <param name="type"></param>
        /// <param name="output"></param>
        private void CreateFiles(Type type, string output)
        {
            var outputDir = string.IsNullOrWhiteSpace(output)
                ? Directory.GetCurrentDirectory()
                : Path.Combine(Directory.GetCurrentDirectory(), output);

            if (!Directory.Exists(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            var entityTypes = type.GetProperties()
               .Where(prop => CheckIfDbSetGenericType(prop.PropertyType))
               .Select(type => type.PropertyType.GetGenericArguments()[0]);

            CreateFile("EFRepository.cs", GenerateEFRepositoryTemplate(type), outputDir);
            CreateFile("EFUnitOfWork.cs", GenerateEFUnitOfWorkTemplate(type), outputDir);
            CreateFile("IRepository.cs", GenerateIRepositoryTemplate(type), outputDir);
            CreateFile("IUnitOfWork.cs", GenerateIUnitOfWorkTemplate(type), outputDir);
            CreateFile("RepositoryHelper.cs", GenerateRepositoryHelperTemplate(type, entityTypes), outputDir);

            foreach (var entityType in entityTypes) 
            {
                CreateFile($"{entityType.Name}Repository.cs", GenerateRepositoryTemplate(entityType), outputDir);
            }           
        }

        /// <summary>
        /// 產生檔案
        /// </summary>
        /// <param name="fileName"></param>
        /// <param name="fileContent"></param>
        /// <param name="outputDir"></param>
        private static void CreateFile(string fileName, string fileContent, string outputDir)
        {
            var filePath = Path.Combine(outputDir, fileName);
            using StreamWriter sw = new StreamWriter(filePath);
            sw.Write(fileContent);
            Reporter.WriteVerbose($"Creating {filePath}");
        }

        /// <summary>
        /// 取得 Type 的完整名稱
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        private string GetFullName(Type type)
        {
            if (!type.IsGenericType) return type.Name;

            StringBuilder sb = new StringBuilder();

            sb.Append(type.Name.Substring(0, type.Name.LastIndexOf("`")));
            sb.Append(type.GetGenericArguments().Aggregate("<",
                delegate (string aggregate, Type type)
                {
                    return aggregate + (aggregate == "<" ? "" : ",") + GetFullName(type);
                }));
            sb.Append('>');

            return sb.ToString();
        }

        /// <summary>
        /// 產生 Repository 範本
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        private static string GenerateRepositoryTemplate(Type type)
        {
            StringBuilder sb = new StringBuilder();

            sb.AppendLine("using System;");
            sb.AppendLine("using System.Linq;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine();
            sb.AppendLine($"namespace {type.Namespace}");
            sb.AppendLine("{");
            sb.AppendLine($"    public class {type.Name}Repository : EFRepository<{type.Name}>, I{type.Name}Repository");
            sb.AppendLine("    {");
            sb.AppendLine();
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine($"    public interface I{type.Name}Repository : IRepository<{type.Name}>");
            sb.AppendLine("    {");
            sb.AppendLine();
            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        /// <summary>
        /// 產生 RepositoryHelper 範本
        /// </summary>
        /// <param name="type"></param>
        /// <param name="entityTypes"></param>
        /// <returns></returns>
        private static string GenerateRepositoryHelperTemplate(Type type, IEnumerable<Type> entityTypes)
        {
            if (!CheckIfTypeInheritanceDbContext(type)) 
            { 
                return string.Empty;
            }

            StringBuilder sb = new StringBuilder();

            sb.AppendLine($"namespace {type.Namespace}");
            sb.AppendLine("{");
            sb.AppendLine("    public static class RepositoryHelper");
            sb.AppendLine("    {");
            sb.AppendLine("        public static IUnitOfWork GetUnitOfWork()");
            sb.AppendLine("        {");
            sb.AppendLine("            return new EFUnitOfWork();");
            sb.AppendLine("        }");

            foreach (var entityType in entityTypes) {
                sb.AppendLine();
                sb.AppendLine($"        public static {entityType.Name}Repository Get{entityType.Name}Repository()");
                sb.AppendLine("        {");
                sb.AppendLine($"            var repository = new {entityType.Name}Repository();");
                sb.AppendLine("            repository.UnitOfWork = GetUnitOfWork();");
                sb.AppendLine("            return repository;");
                sb.AppendLine("        }");
                sb.AppendLine();
                sb.AppendLine($"        public static {entityType.Name}Repository Get{entityType.Name}Repository(IUnitOfWork unitOfWork)");
                sb.AppendLine("        {");
                sb.AppendLine($"            var repository = new {entityType.Name}Repository();");
                sb.AppendLine("            repository.UnitOfWork = unitOfWork;");
                sb.AppendLine("            return repository;");
                sb.AppendLine("        }");
            }
        
            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        /// <summary>
        /// 產生 EFRepository 範本
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        private static string GenerateEFRepositoryTemplate(Type type)
        {
            StringBuilder sb = new StringBuilder();

            sb.AppendLine("using System;");
            sb.AppendLine("using System.Data.Entity;");
            sb.AppendLine("using System.Linq;");
            sb.AppendLine("using System.Linq.Expressions;");
            sb.AppendLine();
            sb.AppendLine($"namespace {type.Namespace}");
            sb.AppendLine("{");
            sb.AppendLine("    public class EFRepository<T> : IRepository<T> where T : class");
            sb.AppendLine("    {");
            sb.AppendLine("        public IUnitOfWork UnitOfWork { get; set; }");
            sb.AppendLine("        ");
            sb.AppendLine("        private IDbSet<T> _objectset;");
            sb.AppendLine("        private IDbSet<T> ObjectSet");
            sb.AppendLine("        {");
            sb.AppendLine("            get");
            sb.AppendLine("            {");
            sb.AppendLine("                if (_objectset == null)");
            sb.AppendLine("                {");
            sb.AppendLine("                    _objectset = UnitOfWork.Context.Set<T>();");
            sb.AppendLine("                }");
            sb.AppendLine("                return _objectset;");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        public virtual IQueryable<T> All()");
            sb.AppendLine("        {");
            sb.AppendLine("            return ObjectSet.AsQueryable();");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        public IQueryable<T> Where(Expression<Func<T, bool>> expression)");
            sb.AppendLine("        {");
            sb.AppendLine("            return ObjectSet.Where(expression);");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        public virtual void Add(T entity)");
            sb.AppendLine("        {");
            sb.AppendLine("            ObjectSet.Add(entity);");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        public virtual void Delete(T entity)");
            sb.AppendLine("        {");
            sb.AppendLine("            ObjectSet.Remove(entity);");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        /// <summary>
        /// 產生 IUnitOfWork 範本
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        private static string GenerateIUnitOfWorkTemplate(Type type) 
        {
            StringBuilder sb = new StringBuilder();

            sb.AppendLine("using System.Data.Entity;");
            sb.AppendLine();
            sb.AppendLine($"namespace {type.Namespace}");
            sb.AppendLine("{");
            sb.AppendLine("    public interface IUnitOfWork");
            sb.AppendLine("    {");
            sb.AppendLine("        DbContext Context { get; set; }");
            sb.AppendLine("        void Commit();");
            sb.AppendLine("        bool LazyLoadingEnabled { get; set; }");
            sb.AppendLine("        bool ProxyCreationEnabled { get; set; }");
            sb.AppendLine("        string ConnectionString { get; set; }");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        /// <summary>
        /// 產生 EFUnitOfWork 範本
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        private static string GenerateEFUnitOfWorkTemplate(Type type) 
        {
            StringBuilder sb = new StringBuilder();

            sb.AppendLine("using System.Data.Entity;");
            sb.AppendLine();
            sb.AppendLine($"namespace {type.Namespace}");
            sb.AppendLine("{");
            sb.AppendLine("    public class EFUnitOfWork : IUnitOfWork");
            sb.AppendLine("    {");
            sb.AppendLine("        public DbContext Context { get; set; }");
            sb.AppendLine();
            sb.AppendLine("        public EFUnitOfWork()");
            sb.AppendLine("        {");
            sb.AppendLine("            Context = new ContosoUniversityEntities();");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        public void Commit()");
            sb.AppendLine("        {");
            sb.AppendLine("            Context.SaveChanges();");
            sb.AppendLine("        }");
            sb.AppendLine("        ");
            sb.AppendLine("        public bool LazyLoadingEnabled");
            sb.AppendLine("        {");
            sb.AppendLine("            get { return Context.Configuration.LazyLoadingEnabled; }");
            sb.AppendLine("            set { Context.Configuration.LazyLoadingEnabled = value; }");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        public bool ProxyCreationEnabled");
            sb.AppendLine("        {");
            sb.AppendLine("            get { return Context.Configuration.ProxyCreationEnabled; }");
            sb.AppendLine("            set { Context.Configuration.ProxyCreationEnabled = value; }");
            sb.AppendLine("        }");
            sb.AppendLine("        ");
            sb.AppendLine("        public string ConnectionString");
            sb.AppendLine("        {");
            sb.AppendLine("            get { return Context.Database.Connection.ConnectionString; }");
            sb.AppendLine("            set { Context.Database.Connection.ConnectionString = value; }");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        /// <summary>
        /// 產生 IRepository 範本
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        private static string GenerateIRepositoryTemplate(Type type) 
        {
            StringBuilder sb = new StringBuilder();

            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using System.Linq;");
            sb.AppendLine("using System.Linq.Expressions;");
            sb.AppendLine("using System.Text;");
            sb.AppendLine();
            sb.AppendLine($"namespace {type.Namespace}");
            sb.AppendLine("{");
            sb.AppendLine("    public interface IRepository<T>");
            sb.AppendLine("    {");
            sb.AppendLine("        IUnitOfWork UnitOfWork { get; set; }");
            sb.AppendLine("        IQueryable<T> All();");
            sb.AppendLine("        IQueryable<T> Where(Expression<Func<T, bool>> expression);");
            sb.AppendLine("        void Add(T entity);");
            sb.AppendLine("        void Delete(T entity);");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }
    }
}
// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.Design.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Internal;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Design;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Microsoft.EntityFrameworkCore.Design
{
    public class MigrationsOperations
    {
        private readonly ILoggerProvider _loggerProvider;
        private readonly LazyRef<ILogger> _logger;
        private readonly Assembly _assembly;
        private readonly string _projectDir;
        private readonly string _rootNamespace;
        private readonly DesignTimeServicesBuilder _servicesBuilder;
        private readonly DbContextOperations _contextOperations;

        public MigrationsOperations(
            [NotNull] AssemblyLoader assemblyLoader,
            [NotNull] ILoggerProvider loggerProvider,
            [NotNull] Assembly assembly,
            [NotNull] Assembly startupAssembly,
            [CanBeNull] string environment,
            [NotNull] string projectDir,
            [NotNull] string startupProjectDir,
            [NotNull] string rootNamespace)
        {
            Check.NotNull(assemblyLoader, nameof(assemblyLoader));
            Check.NotNull(loggerProvider, nameof(loggerProvider));
            Check.NotNull(assembly, nameof(assembly));
            Check.NotNull(startupAssembly, nameof(startupAssembly));
            Check.NotNull(projectDir, nameof(projectDir));
            Check.NotEmpty(startupProjectDir, nameof(startupProjectDir));
            Check.NotNull(rootNamespace, nameof(rootNamespace));

            var loggerFactory = new LoggerFactory();
            loggerFactory.AddProvider(loggerProvider);

            _loggerProvider = loggerProvider;
            _logger = new LazyRef<ILogger>(() => loggerFactory.CreateCommandsLogger());
            _assembly = assembly;
            _projectDir = projectDir;
            _rootNamespace = rootNamespace;
            _contextOperations = new DbContextOperations(
                loggerProvider,
                assembly,
                startupAssembly,
                environment,
                startupProjectDir);

            var startup = new StartupInvoker(startupAssembly, environment, startupProjectDir);
            _servicesBuilder = new DesignTimeServicesBuilder(assemblyLoader, startup);
        }

        public virtual MigrationFiles AddMigration(
            [NotNull] string name,
            [CanBeNull] string outputDir,
            [CanBeNull] string contextType)
        {
            Check.NotEmpty(name, nameof(name));

            var subNamespace = outputDir != null
                ? string.Join(".", outputDir.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                : null;

            using (var context = _contextOperations.CreateContext(contextType))
            {
                var services = _servicesBuilder.Build(context);
                EnsureServices(services);

                var scaffolder = services.GetRequiredService<MigrationsScaffolder>();
                var migration = scaffolder.ScaffoldMigration(name, _rootNamespace, subNamespace);
                var files = scaffolder.Save(_projectDir, migration);

                return files;
            }
        }

        public virtual IEnumerable<MigrationInfo> GetMigrations(
            [CanBeNull] string contextType)
        {
            using (var context = _contextOperations.CreateContext(contextType))
            {
                var services = _servicesBuilder.Build(context);
                EnsureServices(services);

                var migrationsAssembly = services.GetRequiredService<IMigrationsAssembly>();
                var idGenerator = services.GetRequiredService<IMigrationsIdGenerator>();

                return from id in migrationsAssembly.Migrations.Keys
                       select new MigrationInfo { Id = id, Name = idGenerator.GetName(id) };
            }
        }

        public virtual string ScriptMigration(
            [CanBeNull] string fromMigration,
            [CanBeNull] string toMigration,
            bool idempotent,
            [CanBeNull] string contextType)
        {
            using (var context = _contextOperations.CreateContext(contextType))
            {
                var services = _servicesBuilder.Build(context);
                EnsureServices(services);

                var migrator = services.GetRequiredService<IMigrator>();

                return migrator.GenerateScript(fromMigration, toMigration, idempotent);
            }
        }

        public virtual void UpdateDatabase(
            [CanBeNull] string targetMigration,
            [CanBeNull] string contextType)
        {
            using (var context = _contextOperations.CreateContext(contextType))
            {
                var services = _servicesBuilder.Build(context);
                EnsureServices(services);

                var migrator = services.GetRequiredService<IMigrator>();

                migrator.Migrate(targetMigration);
            }

            _logger.Value.LogInformation(CommandsStrings.Done);
        }

        public virtual MigrationFiles RemoveMigration(
            [CanBeNull] string contextType, bool force)
        {
            using (var context = _contextOperations.CreateContext(contextType))
            {
                var services = _servicesBuilder.Build(context);
                EnsureServices(services);

                var scaffolder = services.GetRequiredService<MigrationsScaffolder>();

                var files = scaffolder.RemoveMigration(_projectDir, _rootNamespace, force);

                _logger.Value.LogInformation(CommandsStrings.Done);

                return files;
            }
        }

        private void EnsureServices(IServiceProvider services)
        {
            var providerServices = services.GetRequiredService<IDatabaseProviderServices>();
            if (!(providerServices is IRelationalDatabaseProviderServices))
            {
                throw new OperationException(CommandsStrings.NonRelationalProvider(providerServices.InvariantName));
            }

            var assemblyName = _assembly.GetName();
            var options = services.GetRequiredService<IDbContextOptions>();
            var contextType = services.GetRequiredService<ICurrentDbContext>().Context.GetType();
            var migrationsAssemblyName = RelationalOptionsExtension.Extract(options).MigrationsAssembly
                ?? contextType.GetTypeInfo().Assembly.GetName().Name;
            if (assemblyName.Name != migrationsAssemblyName
                && assemblyName.FullName != migrationsAssemblyName)
            {
                throw new OperationException(
                    CommandsStrings.MigrationsAssemblyMismatch(assemblyName.Name, migrationsAssemblyName));
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using IngosAbpTemplate.API.Infrastructure;
using IngosAbpTemplate.Application;
using IngosAbpTemplate.Application.Contracts;
using IngosAbpTemplate.Domain;
using IngosAbpTemplate.Domain.Shared;
using IngosAbpTemplate.Domain.Shared.Localization;
using IngosAbpTemplate.Infrastructure;
using Localization.Resources.AbpUi;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Versioning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi.Models;
using StackExchange.Redis;
using Volo.Abp;
using Volo.Abp.AspNetCore.Mvc;
using Volo.Abp.AspNetCore.Serilog;
using Volo.Abp.Auditing;
using Volo.Abp.Autofac;
using Volo.Abp.Caching;
using Volo.Abp.Caching.StackExchangeRedis;
using Volo.Abp.Localization;
using Volo.Abp.Modularity;
using Volo.Abp.PermissionManagement.HttpApi;
using Volo.Abp.Swashbuckle;
using Volo.Abp.VirtualFileSystem;

namespace IngosAbpTemplate.API
{
    [DependsOn(typeof(AbpAutofacModule),
        typeof(AbpCachingStackExchangeRedisModule),
        typeof(IngosAbpTemplateApplicationModule),
        typeof(IngosAbpTemplateInfrastructureModule),
        typeof(AbpPermissionManagementHttpApiModule),
        typeof(AbpAspNetCoreSerilogModule),
        typeof(AbpSwashbuckleModule)
    )]
    public class IngosAbpTemplateApiModule : AbpModule
    {
        private const string CorsPolicyName = "IngosAbpTemplate";

        #region Services

        public override void PreConfigureServices(ServiceConfigurationContext context)
        {
            PreConfigure<AbpAspNetCoreMvcOptions>(options =>
            {
                // Set dynamic api router with api version info
                options.ConventionalControllers.Create(typeof(IngosAbpTemplateApplicationModule).Assembly,
                    opts => { opts.RootPath = "v{version:apiVersion}"; });

                // Specify version info for framework built-in api
                options.ConventionalControllers.Create(typeof(AbpPermissionManagementHttpApiModule).Assembly,
                    opts => { opts.ApiVersions.Add(new ApiVersion(1, 0)); });
            });
        }

        public override void ConfigureServices(ServiceConfigurationContext context)
        {
            var configuration = context.Services.GetConfiguration();
            var hostingEnvironment = context.Services.GetHostingEnvironment();

            context.Services.AddHttpClient();

            ConfigureHealthChecks(context);
            ConfigureAuditing(context);
            ConfigureConventionalControllers(context);
            ConfigureAuthentication(context, configuration);
            ConfigureLocalization();
            ConfigureCache(configuration);
            ConfigureVirtualFileSystem(context);
            ConfigureRedis(context, configuration, hostingEnvironment);
            ConfigureCors(context, configuration);
            ConfigureSwaggerServices(context, configuration);
        }


        public override void OnApplicationInitialization(ApplicationInitializationContext context)
        {
            var app = context.GetApplicationBuilder();
            var env = context.GetEnvironment();

            if (env.IsDevelopment()) app.UseDeveloperExceptionPage();

            app.UseAbpRequestLocalization();

            app.UseCorrelationId();
            app.UseVirtualFiles();
            app.UseRouting();
            app.UseCors(CorsPolicyName);
            app.UseAuthentication();

            app.UseAuthorization();

            app.UseHealthChecks("/health");

            app.UseSwagger();
            app.UseAbpSwaggerUI(options =>
            {
                options.DocumentTitle = "IngosAbpTemplate API";

                // Display latest api version by default
                //
                var provider = context.ServiceProvider.GetRequiredService<IApiVersionDescriptionProvider>();
                var apiVersionList = provider.ApiVersionDescriptions
                    .Select(i => $"v{i.ApiVersion.MajorVersion}")
                    .Distinct().Reverse();
                foreach (var apiVersion in apiVersionList)
                    options.SwaggerEndpoint($"/swagger/{apiVersion}/swagger.json",
                        $"IngosAbpTemplate API {apiVersion?.ToUpperInvariant()}");
                
                var configuration = context.GetConfiguration();
                options.OAuthClientId(configuration["AuthServer:SwaggerClientId"]);
                options.OAuthClientSecret(configuration["AuthServer:SwaggerClientSecret"]);
            });

            app.UseAuditing();
            app.UseAbpSerilogEnrichers();
            app.UseUnitOfWork();
            app.UseConfiguredEndpoints();
        }

        #endregion Services

        #region Methods

        private static void ConfigureHealthChecks(ServiceConfigurationContext context)
        {
            context.Services.AddHealthChecks()
                .AddDbContextCheck<IngosAbpTemplateDbContext>();

            // Todo: adapt k8s probes
        }

        private void ConfigureAuditing(ServiceConfigurationContext context)
        {
            Configure<AbpAuditingOptions>(options =>
            {
                options.ApplicationName = "IngosAbpTemplate"; // Set the application name
                options.EntityHistorySelectors.AddAllEntities(); // Default saving all changes of entities
            });
        }

        private void ConfigureCache(IConfiguration configuration)
        {
            Configure<AbpDistributedCacheOptions>(options => { options.KeyPrefix = "IngosAbpTemplate:"; });
        }

        private void ConfigureVirtualFileSystem(ServiceConfigurationContext context)
        {
            var hostingEnvironment = context.Services.GetHostingEnvironment();

            if (hostingEnvironment.IsDevelopment())
                Configure<AbpVirtualFileSystemOptions>(options =>
                {
                    options.FileSets.ReplaceEmbeddedByPhysical<IngosAbpTemplateDomainSharedModule>(
                        Path.Combine(hostingEnvironment.ContentRootPath,
                            $@"..{Path.DirectorySeparatorChar}\IngosAbpTemplate.Domain.Shared"));
                    options.FileSets.ReplaceEmbeddedByPhysical<IngosAbpTemplateDomainModule>(
                        Path.Combine(hostingEnvironment.ContentRootPath,
                            $@"..{Path.DirectorySeparatorChar}\IngosAbpTemplate.Domain"));
                    options.FileSets.ReplaceEmbeddedByPhysical<IngosAbpTemplateApplicationContractsModule>(
                        Path.Combine(hostingEnvironment.ContentRootPath,
                            $@"..{Path.DirectorySeparatorChar}\IngosAbpTemplate.Application.Contracts"));
                    options.FileSets.ReplaceEmbeddedByPhysical<IngosAbpTemplateApplicationModule>(
                        Path.Combine(hostingEnvironment.ContentRootPath,
                            $@"..{Path.DirectorySeparatorChar}\IngosAbpTemplate.Application"));
                });
        }

        private void ConfigureConventionalControllers(ServiceConfigurationContext context)
        {
            Configure<AbpAspNetCoreMvcOptions>(options => { context.Services.ExecutePreConfiguredActions(options); });

            // Use lowercase routing and lowercase query string
            context.Services.AddRouting(options =>
            {
                options.LowercaseUrls = true;
                options.LowercaseQueryStrings = true;
            });

            context.Services.AddAbpApiVersioning(options =>
            {
                options.ReportApiVersions = true;

                options.AssumeDefaultVersionWhenUnspecified = true;

                options.DefaultApiVersion = new ApiVersion(1, 0);

                options.ApiVersionReader = new UrlSegmentApiVersionReader();

                var mvcOptions = context.Services.ExecutePreConfiguredActions<AbpAspNetCoreMvcOptions>();
                options.ConfigureAbp(mvcOptions);
            });

            context.Services.AddVersionedApiExplorer(option =>
            {
                option.GroupNameFormat = "'v'VVV";

                option.AssumeDefaultVersionWhenUnspecified = true;
            });
        }

        private void ConfigureAuthentication(ServiceConfigurationContext context, IConfiguration configuration)
        {
            context.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options =>
                {
                    options.Authority = configuration["AuthServer:Authority"];
                    options.RequireHttpsMetadata = Convert.ToBoolean(configuration["AuthServer:RequireHttpsMetadata"]);
                    options.Audience = "IngosAbpTemplate";
                });
        }

        private static void ConfigureSwaggerServices(ServiceConfigurationContext context, IConfiguration configuration)
        {
            context.Services.AddAbpSwaggerGenWithOAuth(
                configuration["AuthServer:Authority"],
                new Dictionary<string, string>
                {
                    {"IngosAbpTemplate", "IngosAbpTemplate API"}
                },
                options =>
                {
                    // Get application api version info
                    var provider = context.Services.BuildServiceProvider()
                        .GetRequiredService<IApiVersionDescriptionProvider>();

                    // Generate swagger by api major version
                    foreach (var description in provider.ApiVersionDescriptions)
                        options.SwaggerDoc(description.GroupName, new OpenApiInfo
                        {
                            Contact = new OpenApiContact
                            {
                                Name = "Danvic Wang",
                                Email = "danvic.wang@outlook.com",
                                Url = new Uri("https://yuiter.com")
                            },
                            Description = "IngosAbpTemplate API",
                            Title = "IngosAbpTemplate API",
                            Version = $"v{description.ApiVersion.MajorVersion}"
                        });

                    options.DocInclusionPredicate((docName, description) =>
                    {
                        // Get api major version
                        var apiVersion = $"v{description.GetApiVersion().MajorVersion}";

                        if (!docName.Equals(apiVersion))
                            return false;

                        // Replace router parameter
                        var values = description.RelativePath
                            .Split('/')
                            .Select(v => v.Replace("v{version}", apiVersion));

                        description.RelativePath = string.Join("/", values);

                        return true;
                    });

                    // Let params use the camel naming method
                    options.DescribeAllParametersInCamelCase();

                    // 取消 API 文档需要输入版本信息
                    options.OperationFilter<RemoveVersionFromParameter>();

                    // Inject api and dto comments
                    //
                    var paths = new List<string>
                    {
                        @"wwwroot\api-doc\IngosAbpTemplate.API.xml",
                        @"wwwroot\api-doc\IngosAbpTemplate.Application.xml",
                        @"wwwroot\api-doc\IngosAbpTemplate.Application.Contracts.xml"
                    };
                    GetApiDocPaths(paths, Path.GetDirectoryName(AppContext.BaseDirectory))
                        .ForEach(x => options.IncludeXmlComments(x, true));
                });
        }

        private void ConfigureLocalization()
        {
            Configure<AbpLocalizationOptions>(options =>
            {
                options.Resources
                    .Get<IngosAbpTemplateResource>()
                    .AddBaseTypes(
                        typeof(AbpUiResource)
                    );

                options.Languages.Add(new LanguageInfo("zh-Hans", "zh-Hans", "简体中文"));
                options.Languages.Add(new LanguageInfo("zh-Hant", "zh-Hant", "繁體中文"));
                options.Languages.Add(new LanguageInfo("en", "en", "English"));
            });
        }

        private static void ConfigureRedis(ServiceConfigurationContext context, IConfiguration configuration,
            IHostEnvironment hostingEnvironment)
        {
            if (hostingEnvironment.IsDevelopment()) 
                return;
            
            var redis = ConnectionMultiplexer.Connect(configuration["Redis:Configuration"]);
            context.Services
                .AddDataProtection()
                .PersistKeysToStackExchangeRedis(redis, "IngosAbpTemplate-Protection-Keys");
        }

        private static void ConfigureCors(ServiceConfigurationContext context, IConfiguration configuration)
        {
            context.Services.AddCors(options =>
            {
                options.AddPolicy(CorsPolicyName, builder =>
                {
                    builder
                        .WithOrigins(
                            configuration["App:CorsOrigins"]
                                .Split(",", StringSplitOptions.RemoveEmptyEntries)
                                .Select(o => o.RemovePostFix("/"))
                                .ToArray()
                        )
                        .WithAbpExposedHeaders()
                        .SetIsOriginAllowedToAllowWildcardSubdomains()
                        .AllowAnyHeader()
                        .AllowAnyMethod()
                        .AllowCredentials();
                });
            });
        }

        /// <summary>
        /// Get the api description doc path
        /// </summary>
        /// <param name="paths">The xml file path</param>
        /// <param name="basePath">The site's base running files path</param>
        /// <returns></returns>
        private static List<string> GetApiDocPaths(IEnumerable<string> paths, string basePath)
        {
            var files = from path in paths
                let xml = Path.Combine(basePath, path)
                select xml;

            return files.ToList();
        }

        #endregion Methods
    }
}
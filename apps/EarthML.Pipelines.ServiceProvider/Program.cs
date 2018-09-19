using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;
using SInnovations.ServiceFabric.RegistrationMiddleware.AspNetCore;
using SInnovations.ServiceFabric.RegistrationMiddleware.AspNetCore.Extensions;
using SInnovations.Unity.AspNetCore;
using Unity.Builder;
using Unity.Extension;
using Unity.Policy;
using Unity;
using SInnovations.ServiceFabric.Storage.Extensions;
using Microsoft.Extensions.DependencyInjection;
using SInnovations.ServiceFabric.Unity;
using EarthML.Pipelines.ServiceProvider.Actors;
using SInnovations.ServiceFabric.RegistrationMiddleware.AspNetCore.Model;
using SInnovations.ServiceFabric.Gateway.Model;
using System.Threading;
using EarthML.Pipelines.ServiceProvider.Services;
using Unity.Injection;
using Unity.Lifetime;
using Microsoft.ServiceFabric.Services.Remoting.Client;
using Microsoft.ServiceFabric.Services.Remoting.FabricTransport;
using Microsoft.ServiceFabric.Services.Remoting;
using System.Fabric;

[assembly: FabricTransportServiceRemotingProvider(RemotingClientVersion = RemotingClientVersion.V2_1, RemotingListenerVersion = RemotingListenerVersion.V2_1)]

namespace EarthML.Pipelines.ServiceProvider
{

    public class LoggingExtension : UnityContainerExtension,
                                   IBuildPlanCreatorPolicy,
                                   IBuildPlanPolicy
    {
        #region Fields

        private readonly MethodInfo _createLoggerMethod = typeof(LoggingExtension).GetTypeInfo()
                                                                                  .GetDeclaredMethod(nameof(CreateLogger));

        #endregion


        #region Constructors

        //[InjectionConstructor]
        //public LoggingExtension()
        //{
        //    LoggerFactory = new LoggerFactory();
        //}

        //public LoggingExtension(ILoggerFactory factory)
        //{
        //    LoggerFactory = factory ?? new LoggerFactory();
        //}


        #endregion


        #region Public Members

        //public ILoggerFactory LoggerFactory { get; }

        #endregion


        #region IBuildPlanPolicy


        public void BuildUp(IBuilderContext context)
        {
            context.Existing = null == context.ParentContext
                             ? context.ParentContext.Container.Resolve<ILoggerFactory>().CreateLogger(context.OriginalBuildKey.Name ?? string.Empty)
                             : context.Container.Resolve<ILoggerFactory>().CreateLogger(context.ParentContext.BuildKey.Type);
            context.BuildComplete = true;
        }

        #endregion


        #region IBuildPlanCreatorPolicy

        IBuildPlanPolicy IBuildPlanCreatorPolicy.CreatePlan(IBuilderContext context, INamedType buildKey)
        {
            var info = (context ?? throw new ArgumentNullException(nameof(context))).BuildKey
                                                                                    .Type
                                                                                    .GetTypeInfo();
            if (!info.IsGenericType) return this;

            var buildMethod = _createLoggerMethod.MakeGenericMethod(info.GenericTypeArguments.First())
                                                 .CreateDelegate(typeof(DynamicBuildPlanMethod));

            return new DynamicMethodBuildPlan((DynamicBuildPlanMethod)buildMethod, context.Container.Resolve<ILoggerFactory>());
        }

        #endregion


        #region Implementation

        private static void CreateLogger<T>(IBuilderContext context, ILoggerFactory loggerFactory)
        {
            context.Existing = loggerFactory.CreateLogger<T>();
            context.BuildComplete = true;
        }

        protected override void Initialize()
        {
            Context.Policies.Set(typeof(Microsoft.Extensions.Logging.ILogger), string.Empty, typeof(IBuildPlanPolicy), this);
            Context.Policies.Set<IBuildPlanCreatorPolicy>(this, typeof(Microsoft.Extensions.Logging.ILogger));
            Context.Policies.Set<IBuildPlanCreatorPolicy>(this, typeof(ILogger<>));
        }

        private delegate void DynamicBuildPlanMethod(IBuilderContext context, ILoggerFactory loggerFactory);

        private class DynamicMethodBuildPlan : IBuildPlanPolicy
        {
            private readonly DynamicBuildPlanMethod _buildMethod;
            private readonly ILoggerFactory _loggerFactory;

            /// <summary>
            /// 
            /// </summary>
            /// <param name="buildMethod"></param>
            /// <param name="loggerFactory"></param>
            public DynamicMethodBuildPlan(DynamicBuildPlanMethod buildMethod,
                                          ILoggerFactory loggerFactory)
            {
                _buildMethod = buildMethod;
                _loggerFactory = loggerFactory;
            }

            /// <summary>
            /// 
            /// </summary>
            /// <param name="context"></param>
            public void BuildUp(IBuilderContext context)
            {
                _buildMethod(context, _loggerFactory);
            }
        }

        #endregion
    }
    public class Program
    {
        private const string LiterateLogTemplate = "[{Timestamp:HH:mm:ss} {Level}] {SourceContext}{NewLine}{Message}{NewLine}{Exception}{NewLine}";



        public static void Main(string[] args)
        {

            var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

            var config = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddJsonFile($"appsettings.{environmentName}.json", optional: true)
                .AddEnvironmentVariables();

            using (var container = new FabricContainer()
                       .AddOptions()
                       .UseConfiguration(config) //willl also be set on hostbuilder
                       .ConfigureSerilogging(logConfiguration =>
                           logConfiguration.MinimumLevel.Information()
                           .Enrich.FromLogContext()
                           .WriteTo.LiterateConsole(outputTemplate: LiterateLogTemplate))
                       .ConfigureApplicationInsights())
            {
                container.AddNewExtension<LoggingExtension>();



                if (args.Contains("--serviceFabric"))
                {
                    container.ConfigureApplicationStorage();

                    RunInServiceFabric(container);
                }
                else
                {
                    RunOnIIS(container);
                }
            }


        }

        private static void RunOnIIS(IUnityContainer container)
        {

            var host = new WebHostBuilder()
                 .UseKestrel()
                 .UseContentRoot(Directory.GetCurrentDirectory())
                 .UseIISIntegration()
                   .ConfigureServices(services =>
                    services.AddSingleton(container)
                    .AddSingleton<IServiceProviderFactory<IServiceCollection>, UnityServiceProviderFactory>())
                 .UseStartup<Startup>()
                 //  .UseApplicationInsights()
                 .Build();

            host.Run();
        }

        private static void RunInServiceFabric(IUnityContainer container)
        {

            container.WithActor<PipelineRunnerActor, ActorBaseService<PipelineRunnerActorDocument>>(
                (sp, context, actorType, factory) => new ActorBaseService<PipelineRunnerActorDocument>(context, actorType, factory));

            container.WithStatefullService<PipelineRunnerService>("PipelineRunnerServiceType");
          
           

            container.WithKestrelHosting<Startup>("EarthML.Pipelines.ServiceProviderType",
                new KestrelHostingServiceOptions
                {
                    GatewayApplicationName ="EarthML.Gateway",
                    GatewayOptions = new GatewayOptions
                    {
                        Key = "EarthML.Pipelines.ServiceProviderType",
                        ServerName = "local.earthml.com api.earthml.com",
                        ReverseProxyLocation = new[] { "EarthML.Pipelines" }.BuildResourceProviderLocation(),
                        Ssl = new SslOptions
                        {
                            Enabled = true,
                            SignerEmail = "info@earthml.com"
                        },
                    }


                });

            Thread.Sleep(Timeout.Infinite);
        }
    }
    }

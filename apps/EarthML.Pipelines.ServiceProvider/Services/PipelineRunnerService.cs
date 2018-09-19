using Docker.DotNet;
using EarthML.Pipelines;
using EarthML.Pipelines.Document;
using EarthML.Pipelines.Parameters;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.ServiceFabric.Data;
using Microsoft.ServiceFabric.Data.Collections;
using Microsoft.ServiceFabric.Services.Client;
using Microsoft.ServiceFabric.Services.Communication.Runtime;
using Microsoft.ServiceFabric.Services.Remoting;
using Microsoft.ServiceFabric.Services.Remoting.Client;
using Microsoft.ServiceFabric.Services.Remoting.Runtime;
using Microsoft.ServiceFabric.Services.Remoting.V2.FabricTransport.Runtime;
using Microsoft.ServiceFabric.Services.Runtime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using System;
using System.Collections.Generic;
using System.Fabric;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace EarthML.Pipelines.ServiceProvider.Services
{
    [Serializable]
    [DataContract]
    public class PipelineData
    {
        [IgnoreDataMember]
        [JsonProperty("pipeline")]
        public JToken pipeline { get; set; }

        [JsonIgnore]
        [DataMember(Name = "pipeline")]
        public byte[] pipelineData
        {
            get
            {
                if (pipeline == null)
                    return null;
                return Encoding.UTF8.GetBytes(pipeline.ToString(Newtonsoft.Json.Formatting.None));
            }
            set
            {
                if (value==null || value.Length==0)
                    pipeline = null;
                else
                    pipeline = JToken.Parse(Encoding.UTF8.GetString(value));
            }
        }

        [DataMember]
        public string[] arguments { get; set; }

    }

    [Serializable]
    public class DocumentRequest
    {
        public string id { get; set; }
        public PipelineData data { get; set; }

    }

    public interface IPipelineRunnerService : IService {

        Task SendAsync(DocumentRequest data);
        Task<DocumentRequest[]> GetBatchAsync(int batchSize, CancellationToken cancellationToken);

    }

    public class PipelineExecutorService : StatelessService
    {
        public PipelineExecutorService(StatelessServiceContext serviceContext) : base(serviceContext)
        {
        }

        protected override async Task RunAsync(CancellationToken cancellationToken)
        {
            int batchSize = 5;
            long delayMs = 100;


            DockerPipelineRunnerOptions options = new DockerPipelineRunnerOptions();

            var block = new ActionBlock<DocumentRequest>(async (@event) =>
            {

                var services = new ServiceCollection();
              //  using (var fa =)
                {

                    services.AddLogging(builder =>
                    {

                        builder.AddSerilog(new LoggerConfiguration()
                           .Enrich.WithProperty("ApiVersion", "1.2.5000")
                           .WriteTo.File($"c:/logs/{@event.id}/default.log",buffered:true)
                           .CreateLogger());

                        //builder.AddFile(o =>
                        //{
                        //    o.FileAppender = new PhysicalFileAppender(new PhysicalFileProvider("c:/logs"));
                        //    o.BasePath = @event?.id;
                        //    o.EnsureBasePath = true;
                        //   // o.FallbackFileName = $"{@event?.id}.log";
                        //});
                    });

                    // create logger factory
                    using (var sp = services.BuildServiceProvider())
                    {
                        var loggerFactory = sp.GetService<ILoggerFactory>();

                        var logger = loggerFactory.CreateLogger<PipelineExecutorService>();

                        try
                        {

                            logger.LogInformation("Received<{eventId}>: {@event}", @event.id, @event.data);



                            var args = @event.data.arguments;


                            var ci = new DockerClientExecutor(
                                new DockerClientConfiguration(new Uri("npipe://./pipe/docker_engine"))
                                 .CreateClient(), loggerFactory.CreateLogger< DockerClientExecutor>());


                            var runner = new DockerPipelineRunner(
                               loggerFactory.CreateLogger< DockerPipelineRunner>(),
                                 new ExpressionParser(@event.data.pipeline
                                .UpdateParametersFromConsoleArguments(args), loggerFactory.CreateLogger< ExpressionParser>())
                                .AddRegex()
                                .AddSplit()
                                .AddConcat()
                                .AddAll()
                               , ci, options
                              );




                            await runner.RunPipelineAsync(CancellationToken.None);

                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(ex);
                            logger.LogError(ex, "Failed to run pipeline");


                        }

                    }
                }
               
                //  await connection.InvokeAsync("pipeline_recieved", arg1: @event.id);

            }, new ExecutionDataflowBlockOptions
            {
                MaxDegreeOfParallelism = batchSize,
                BoundedCapacity = batchSize
            });


            while (!cancellationToken.IsCancellationRequested)
            {

                var md5 = MD5.Create();
                var value = md5.ComputeHash(Encoding.ASCII.GetBytes(Guid.NewGuid().ToString()));
                var key = BitConverter.ToInt64(value, 0);

                var pipelineRunnerService = ServiceProxy.Create<IPipelineRunnerService>(
                     new Uri($"fabric:/EarthML.PipelinesApplication/PipelineRunnerService"),
                     partitionKey: new ServicePartitionKey(key), listenerName: "V2_1Listener");

                var items = await pipelineRunnerService.GetBatchAsync(batchSize,cancellationToken);

                foreach(var item in items)
                {
                    await block.SendAsync(item);
                }

                int delayFactor = batchSize - items.Length;
                await Task.Delay(TimeSpan.FromMilliseconds(delayMs * delayFactor), cancellationToken);
            }

            block.Complete();
            await block.Completion;



        }

    }
    public class PipelineRunnerService : StatefulService, IPipelineRunnerService
    {
        private readonly ILogger logger;

        public PipelineRunnerService(StatefulServiceContext serviceContext, ILogger logger) : base(serviceContext)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task SendAsync(DocumentRequest data)
        {
            var myQueue = await StateManager.GetOrAddAsync<IReliableConcurrentQueue<DocumentRequest>>("myQueue");

            using (var tx = StateManager.CreateTransaction())
            {
                await myQueue.EnqueueAsync(tx, data);
                await tx.CommitAsync();
            }
        }
        public async Task<DocumentRequest[]> GetBatchAsync( int batchSize, CancellationToken cancellationToken)
        {
            var myQueue = await StateManager.GetOrAddAsync<IReliableConcurrentQueue<DocumentRequest>>("myQueue");
            var processItems = new List<DocumentRequest>();
            using (var txn = this.StateManager.CreateTransaction())
            {
                ConditionalValue<DocumentRequest> ret;

                for (int i = 0; i < batchSize; ++i)
                {
                    ret = await myQueue.TryDequeueAsync(txn, cancellationToken);

                    if (ret.HasValue)
                    {
                        // If an item was dequeued, add to the buffer for processing
                            processItems.Add(ret.Value);
                        //await block.SendAsync(ret.Value);
                    }
                    else
                    {
                        // else break the for loop
                        break;
                    }
                }

                await txn.CommitAsync();
            }
            return processItems.ToArray();
        }
        /// <summary>
        /// Optional override to create listeners (e.g., HTTP, Service Remoting, WCF, etc.) for this service replica to handle client or user requests.
        /// </summary>
        /// <remarks>
        /// For more information on service communication, see https://aka.ms/servicefabricservicecommunication
        /// </remarks>
        /// <returns>A collection of listeners.</returns>
        protected override IEnumerable<ServiceReplicaListener> CreateServiceReplicaListeners()
        {
            return this.CreateServiceRemotingReplicaListeners();
            //  return new[]
            //{
            //    new ServiceReplicaListener((ctx)=> new FabricTransportServiceRemotingListener(ctx,this),"V2Listener")
            //};
        }
    }
}

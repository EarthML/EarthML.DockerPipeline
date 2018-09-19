using Docker.DotNet;
using EarthML.Pipelines;
using EarthML.Pipelines.Document;
using EarthML.Pipelines.Parameters;
using Microsoft.Extensions.Logging;
using Microsoft.ServiceFabric.Data;
using Microsoft.ServiceFabric.Data.Collections;
using Microsoft.ServiceFabric.Services.Communication.Runtime;
using Microsoft.ServiceFabric.Services.Remoting;
using Microsoft.ServiceFabric.Services.Remoting.Runtime;
using Microsoft.ServiceFabric.Services.Remoting.V2.FabricTransport.Runtime;
using Microsoft.ServiceFabric.Services.Runtime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Fabric;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

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

        protected override async Task RunAsync(CancellationToken cancellationToken)
        {
            int batchSize = 5;
            long delayMs = 100;


            DockerPipelineRunnerOptions options = new DockerPipelineRunnerOptions();

            var block = new ActionBlock<DocumentRequest>(async (@event) =>
            {
                try
                {
                  
                    Console.WriteLine($"Received<{@event?.id}>: \n{@event?.data?.pipeline.ToString(Newtonsoft.Json.Formatting.Indented)}");

                    var args = @event.data.arguments;


                    var ci = new DockerClientExecutor(new DockerClientConfiguration(new Uri("npipe://./pipe/docker_engine"))
                         .CreateClient(), "c:/logs");


                    var runner = new DockerPipelineRunner(
                         new ExpressionParser(@event.data.pipeline
                        .UpdateParametersFromConsoleArguments(args))
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
                }

                //  await connection.InvokeAsync("pipeline_recieved", arg1: @event.id);

            }, new ExecutionDataflowBlockOptions
            {
                MaxDegreeOfParallelism = batchSize, BoundedCapacity = batchSize
            });


            var myQueue = await StateManager.GetOrAddAsync<IReliableConcurrentQueue<DocumentRequest>>("myQueue");

          
            while (!cancellationToken.IsCancellationRequested)
            {
                //// Buffer for dequeued items
                //List<DocumentRequest> processItems = new List<DocumentRequest>();

                using (var txn = this.StateManager.CreateTransaction())
                {
                    ConditionalValue<DocumentRequest> ret;

                    for (int i = 0; i < batchSize; ++i)
                    {
                        ret = await myQueue.TryDequeueAsync(txn, cancellationToken);

                        if (ret.HasValue)
                        {
                            // If an item was dequeued, add to the buffer for processing
                            //    processItems.Add(ret.Value);
                            await block.SendAsync(ret.Value);
                        }
                        else
                        {
                            // else break the for loop
                            break;
                        }
                    }

                    await txn.CommitAsync();
                }

                

                int delayFactor = batchSize - block.InputCount;
                await Task.Delay(TimeSpan.FromMilliseconds(delayMs * delayFactor), cancellationToken);
            }

            block.Complete();
            await block.Completion;




        }
    }
}

using Docker.DotNet;
using EarthML.DockerPipeline;
using EarthML.DockerPipeline.Document;
using EarthML.DockerPipeline.Parameters;
using McMaster.Extensions.CommandLineUtils;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Auth;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace EarthML.DockerPipelineCli
{
    public class PipelineData
    {
        public JToken pipeline { get; set; }
        public string[] arguments { get; set; }

    }
    public class DocumentRequest
    {
        public string id { get; set; }
        public PipelineData data { get; set; }

    }


    [Command(ThrowOnUnexpectedArgument = false)]
    [HelpOption]
    class Program
    {
        static Task<int> Main(string[] args) => CommandLineApplication.ExecuteAsync<Program>(args);

        [Option(LongName = "daemon", ShortName = "d", Description = "Run in daemon mode, working as a pipeline executor for local docker socket. \n Remember to run with '-v /var/run/docker.sock:/var/run/docker.sock earthml/docker-pipeline' ")]
        private bool Daemon { get; }

        [Option(LongName = "pipeline", ShortName = "p", Description = "The path to the pipeline document")]
        private string pipeline { get; }

        [Option(LongName = "logpath", ShortName = "log", Description = "The path to dump logs")]
        private string logpath { get; }


        [Option(LongName = "parallel", ShortName ="pa" ,Description = "Parallel execution count")]
        private int parallel { get; } = 2;

        [Option(LongName = "docker-sock", ShortName = "ds", Description = "The docker sock location")]
        private string dockerSock { get; } = "unix://var/run/docker.sock";

        private async Task<int> OnExecuteAsync(CommandLineApplication app)
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("en-US");

            if (!string.IsNullOrEmpty(logpath))
            {
                Directory.CreateDirectory(logpath);
            }
            //var cc = new DockerClientConfiguration(new Uri("npipe://./pipe/docker_engine"))
            //         .CreateClient();

            //var list = await cc.Volumes.ListAsync();
            //var t = await cc.Volumes.InspectAsync(list.Volumes.First().Name);

            //return 0;

            //Console.WriteLine(new Uri("unix://var/run/docker.sock"));
            if (string.IsNullOrEmpty(pipeline) && !Daemon)
            {
                //--docker-sock npipe://./pipe/docker_engine -p examples/sen2cor_daemon.json --pid 3e5b3ad3-a174-453a-bc32-e568bc09be14 --id S2B_MSIL1C_20180107T102359_N0206_R065_T33UUB_20180107T121759 --username pksorensen --password 123456

                var storage = new CloudStorageAccount(new StorageCredentials("eodata", "z6GbmMTOfzK3w4Z6Cwd5acts1n327qbjWSLAviOhWMzSChA+Q9R/l/Sf5FgjvxhMBnaW4BMTby5Vwk+slmElGQ=="), true);
                var testcontainer = storage.CreateCloudBlobClient().GetContainerReference("test");
                await testcontainer.CreateIfNotExistsAsync();
 
                var client = new HttpClient();
                var sw = Stopwatch.StartNew();
                var post = await client.PostAsync("https://dockerpipeline.azurewebsites.net/subscriptions/test/providers/EarthML.DockerPipeline/PipelineRunners/test/pipelines",
                    new StringContent(JToken.FromObject(new
                    {
                        pipeline = JToken.Parse(File.ReadAllText("examples/sen2cor_daemon.json")),
                        arguments = new[] {
                            "--pid", "3e5b3ad3-a174-453a-bc32-e568bc09be14",
                            "--outputcontainer", testcontainer.Uri.AbsoluteUri ,
                            "--outputkey", "z6GbmMTOfzK3w4Z6Cwd5acts1n327qbjWSLAviOhWMzSChA+Q9R/l/Sf5FgjvxhMBnaW4BMTby5Vwk+slmElGQ==",
                            "--id", "S2B_MSIL1C_20180107T102359_N0206_R065_T33UUB_20180107T121759",
                            "--username", "pksorensen",
                            "--password", "123456" }
                    }).ToString(), Encoding.UTF8, "application/json"));

                Console.WriteLine(await post.Content.ReadAsStringAsync());
                Console.WriteLine(sw.ElapsedMilliseconds);
                return 0;
            }

            if (Daemon)
            {
                DockerPipelineRunnerOptions options = new DockerPipelineRunnerOptions();

                var test = new DockerClientConfiguration(new Uri(dockerSock))
                     .CreateClient();


                var images = await test.Containers.ListContainersAsync(new Docker.DotNet.Models.ContainersListParameters());

                foreach (var image in images)
                    Console.WriteLine(image.Image);


                var connected = true;
                var connection = new HubConnectionBuilder()
                    .WithUrl("https://dockerpipeline.azurewebsites.net/pipelines")
                    .ConfigureLogging(c =>
                    {
                       
                    })
                  //  .WithConsoleLogger()
                    .Build();

                var block = new ActionBlock<DocumentRequest>(async (@event) =>
                {
                    try
                    {
                        Console.Write(JsonConvert.SerializeObject(@event));
                        Console.WriteLine($"Received<{@event?.id}>: \n{@event?.data?.pipeline.ToString(Newtonsoft.Json.Formatting.Indented)}");

                        var args = @event.data.arguments;

                        
                        var ci = new DockerClientExecutor(new DockerClientConfiguration(new Uri(dockerSock))
                             .CreateClient(),logpath);


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


                    }catch(Exception ex)
                    {
                        Console.WriteLine(ex);
                    }

                    await connection.InvokeAsync("pipeline_recieved", arg1: @event.id);

                }, new ExecutionDataflowBlockOptions
                {
                     MaxDegreeOfParallelism = parallel
                });


                connection.On<DocumentRequest>("run_pipeline", async @event =>
                 {

                  
                    await block.SendAsync(@event);

                 });

                connection.Closed += (ex) => { connected = false; block.Complete(); return Task.CompletedTask;  };

                await connection.StartAsync();

                await connection.InvokeAsync("Connected", arg1: images.Select(c => c.ID).ToArray());

                await block.Completion;

                return 0;
            }



            //var args = new[] {
            //    "--pipeline", "examples/sen2cor.json",
            //    "--id", "S2B_MSIL1C_20180130T071129_N0206_R106_T39RXL_20180130T082434",
            //    "--storageAccountKey", "z6GbmMTOfzK3w4Z6Cwd5acts1n327qbjWSLAviOhWMzSChA+Q9R/l/Sf5FgjvxhMBnaW4BMTby5Vwk+slmElGQ==",
            //    "--storageAccountName", "eodata",
            //    "--shareName", "uzjcx01tsuwxq18ymde4mdezmfqwnzexmjlftjaymdzfujewnl9umzlswexf",
            //    "--shareName1", "test2",
            //    "--registryServer","eodata.azurecr.io",
            //    "--registryPassword", "NvmS6ajuc8Htw3Sg",
            //    "--registryUsername" , "30beed1d-f53a-4fb0-a626-730c035a56be"};

            //var ci = new DockerContainerInstanceExecutor(new DockerContainerInstanceExecutorOptions
            //{
            //    SubscriptionId= "4c6c1ea4-3605-4411-a44e-b65382627f5b",
            //    ClientCredentials = new ClientCredential("http://AzureContainerInstanceRunner", "NvmS6ajuc8Htw3Sg"),
            //    ResourceGroup= "sentinel-playground",
            //    ContainerGroupName = "sen2cor8gb-test7",
            //    Cpu=1,
            //    MemoryInGB=8,
            //    OsType= "Linux",
            //    Location= "WestEurope",TenantId= "0840c760-6f7b-4556-b337-8c090e2d458d"
            //});
            //  Console.WriteLine(await ci.ExecuteStepAsync(null, null, null, null, default));
            // return;
            {
                var args =
                  app.Arguments.Select(c => new[] { $"--{c.Name}", c.Value })
                      .Concat(app.Options.Where(o => o.HasValue()).Select(o => new[] { $"--{o.LongName}", o.Value() }))
                      .SelectMany(m => m).Concat(app.RemainingArguments).ToArray();



                var ci = new DockerClientExecutor(new DockerClientConfiguration(new Uri(dockerSock))
                     .CreateClient(),logpath);


                var runner = new DockerPipelineRunner(
                     new ExpressionParser(args.ReadAsDocument()
                    .UpdateParametersFromConsoleArguments(args))
                    .AddRegex()
                    .AddSplit()
                    .AddConcat()
                    .AddAll()
                   , ci
                  );



                await runner.RunPipelineAsync(CancellationToken.None);
            }

            return 0;
        }

        private static Task Connection_Closed(Exception arg)
        {
            throw new NotImplementedException();
        }
    }
}

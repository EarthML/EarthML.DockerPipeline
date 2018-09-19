
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace EarthML.Pipelines.Document
{
    public class SemaphoreWrapper
    {
        private int counter = 0;
        private readonly Semaphore semaphore;

        public int RunningCounter => counter;

        public SemaphoreWrapper(int initialCount, int maximumCount)
        {
            
            this.semaphore = new Semaphore(initialCount, maximumCount);


        }

        internal void WaitOne()
        {

            this.semaphore.WaitOne();
            Interlocked.Increment(ref counter);
        }

        internal void Release()
        {
            Interlocked.Decrement(ref counter);
            this.semaphore.Release();
        }
    }
    public class DockerPipelineRunnerOptions
    {
        private ConcurrentDictionary<string, SemaphoreWrapper> _semaphores = new ConcurrentDictionary<string, SemaphoreWrapper>();

        public SemaphoreWrapper GetSemaphore(string parallelKey, int parallelBlocker)
        {
            return _semaphores.GetOrAdd(parallelKey, (key) => new SemaphoreWrapper(initialCount: parallelBlocker, maximumCount: parallelBlocker));
        }
    }

    public class ParallelContext
    {

    }
    public class DockerPipelineRunner
    {
        private readonly ILogger logger;
        private ExpressionParser parser;
        private DockerPipelineExecutor executor;
        private DockerPipelineRunnerOptions options;

        public DockerPipelineRunner(
            ILogger logger,
            ExpressionParser parser,
            DockerPipelineExecutor executor, 
            DockerPipelineRunnerOptions options =null)  
        {
          
           // var services = new ServiceCollection();
            


            this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.parser = parser ?? throw new ArgumentNullException(nameof(parser));
            this.options = options ?? new DockerPipelineRunnerOptions();
            parser.Functions["stdout"] = (document, arguments) => "hello world";
            parser.Functions["numbers"] = (document, arguments) => new JArray(arguments.SelectMany(c => c).Select(c => double.Parse(c.ToString())));
            parser.Functions["number"] = (document, arguments) => arguments.Select(c => double.Parse(c.ToString())).FirstOrDefault();
            parser.Functions["int"] = (document, arguments) => arguments.Select(c => (int)double.Parse(c.ToString())).FirstOrDefault();
            parser.Functions["output"] = (document, arguments) => document.SelectToken($"$.steps[?(@.name == '{arguments[0].ToString()}')].outputs").SelectToken(arguments[1].ToString());
            parser.Functions["exists"] = (document, arguments) => Directory.Exists(arguments[0].ToString());
            parser.Functions["folders"] = (document, arguments) => new JArray(Directory.GetDirectories(arguments[0].ToString()));
            parser.Functions["join"] = (document, arguments) => string.Join(arguments.First().ToString(), arguments.Skip(1).SelectMany(c => c).Select(c => c.ToString()));
        }

        public static string CreateMD5(string input)
        {
            // Use input string to calculate MD5 hash
            using (System.Security.Cryptography.MD5 md5 = System.Security.Cryptography.MD5.Create())
            {
                byte[] inputBytes = System.Text.Encoding.ASCII.GetBytes(input);
                byte[] hashBytes = md5.ComputeHash(inputBytes);

                // Convert the byte array to hexadecimal string
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < hashBytes.Length; i++)
                {
                    sb.Append(hashBytes[i].ToString("X2"));
                }
                return sb.ToString();
            }
        }

        public async Task RunPipelineAsync(CancellationToken cancellationToken)
        {

            if(parser.Document.SelectToken("$.imageRegistryCredentials") is JArray imageRegistryCredentials)
            {
                foreach(JObject imageRegistryCredential in imageRegistryCredentials)
                {
                    HandleObjReplacement(imageRegistryCredential);
                    logger.LogInformation("using {@imageRegistryCredential}", imageRegistryCredential);
                }
            }

            foreach (var part in parser.Document.SelectToken("$.pipe"))
            {
                var command = part.SelectToken("$.command");

                var dependsOn = part.SelectToken("$.dependsOn")?.Select(c => c.ToString()).ToArray();




                var parallelBlocker = int.MaxValue;
                var parallelKey = part.SelectToken("$.name").ToString();
                if (part.SelectToken("$.parallelContext") is JObject parallelContext)
                {

                    HandleObjReplacement(parallelContext);

                    parallelBlocker = parallelContext.SelectToken("$.max")?.ToObject<int>() ?? parallelBlocker;
                    parallelKey = parallelContext.SelectToken("$.contextKey")?.ToString() ?? parallelKey;



                    logger.LogInformation("using {@parallelContext}: {parallelKey} {parallelBlocker}", parallelContext, parallelKey, parallelBlocker);
                }

                 

                var semaphoreObject = options.GetSemaphore(parallelKey, parallelBlocker); //
          

                parser.Functions["parallelContext"] = (document, arguments) => JToken.FromObject(new { counter = semaphoreObject.RunningCounter });

                var volumnes = new Dictionary<string,JObject>();

                var dependenciesResolved = true;
                if (dependsOn != null)
                {
                    foreach (var dependency in dependsOn)
                    {
                        if (dependency.StartsWith("/volumes/"))
                        {
                            var volumeName = dependency.Substring("/volumes/".Length);

                            var volume = parser.Document.SelectToken($"$.volumes.{volumeName}")?.DeepClone();
                            


                            if (volume is JObject volumeObj)
                            {
                                HandleObjReplacement(volumeObj);



                                if (volume.SelectToken("$.azureFileShare") is JObject azureFileShare)
                                {
                                    var storageAccount = new CloudStorageAccount(new Microsoft.WindowsAzure.Storage.Auth.StorageCredentials(azureFileShare.SelectToken("$.storageAccountName").ToString(), azureFileShare.SelectToken("$.storageAccountKey").ToString()), true);
                                    await storageAccount.CreateCloudFileClient().GetShareReference(azureFileShare.SelectToken("$.shareName").ToString()).CreateIfNotExistsAsync();
                                }

                                if(volume.SelectToken("$.local") is JObject local)
                                {
                                    var uniqueId = local.SelectToken("$.uniqueId")?.ToString();
                                    if (!string.IsNullOrEmpty(uniqueId))
                                    {
                                        local.Replace(CreateMD5(uniqueId));
                                    }
                                }

                                //if (volume.SelectToken("$.mountedFrom").ToString().StartsWith("["))
                                //{
                                //    volume.SelectToken("$.mountedFrom").Replace(parser.Evaluate(volume.SelectToken("$.mountedFrom").ToString()));
                                //}
                                // volumnes.Add(volume.SelectToken("$.mountedAt").ToString(),new EmptyStruct { });
                                volumnes.Add(volumeName,volumeObj);
                            }
                            else
                            {
                                dependenciesResolved = false;
                                break;
                            }
                        }
                        else
                        {
                            var completed = parser.Document.SelectToken($"$.pipe[?(@.name == '{dependency}')].outputs.completed")?.ToObject<bool>() ?? false;
                            if (!completed)
                            {
                                dependenciesResolved = false;
                                break;
                            }
                        }

                    }

                    if (!dependenciesResolved)
                    {
                        continue;
                    }
                }

                parser.Functions["mountedAt"] = (d, a) => volumnes[a.First().ToString()]["mountedAt"].ToString();
                parser.Functions["mountedFrom"] = (d, a) => volumnes[a.First().ToString()]["mountedFrom"].ToString();


                var commands = new List<string[]>() { };
                var loop = part.SelectToken("loop");
                if (loop != null)
                {
                   

                    if (loop.Type == JTokenType.String && loop.ToString().StartsWith("["))
                    {
                        loop = parser.Evaluate(loop.ToString());
                    }

                    logger.LogInformation("using {@loop}", loop);

                    foreach (var loopVar in loop)
                    {
                        parser.Functions["loop"] = (document, arguments) => arguments[0].ToString() == "var" ? loopVar: Path.GetFileName(loopVar.ToString());

                       
                        var skip = part.SelectToken("$.skip");
                       
                        if (skip == null || !skip.ToString().StartsWith("[") || !parser.Evaluate(skip.ToString()).ToObject<bool>())
                        {
                            commands.Add(command.Select(c => c.ToString().StartsWith("[") ? parser.Evaluate(c.ToString())?.ToString() : c.ToString()).ToArray());
                        }


                       
                    }
                }
                else
                {
                    var skip = part.SelectToken("$.skip");
                    if (skip == null || !skip.ToString().StartsWith("[") || !parser.Evaluate(skip.ToString()).ToObject<bool>())
                    {
                        commands.Add(command.Select(c => c.ToString().StartsWith("[") ? parser.Evaluate(c.ToString())?.ToString() : c.ToString()).ToArray());
                    } 
                }
                
                if (!command.Any())
                {
                    var outputs = part["outputs"] as JObject ?? new JObject();
                    outputs["completed"] = true;
                    part["outputs"] = outputs;
                    Console.WriteLine(part.ToString());
                    continue;
                }


                var image = part.SelectToken("$.image").ToString();

                if (image.StartsWith("["))
                {
                    part["image"] = parser.Evaluate(image).ToString();
                }


                var block = new ActionBlock<string[]>(async arguments =>
                {
                    try
                    {
                        semaphoreObject.WaitOne();





                        var binds = volumnes.Select((v, i) => $"{GetMountedFrom(v)}:{v.Value.SelectToken("$.mountedAt").ToString()}").ToList();

                        var runCommand = $"docker run {string.Join(" ", binds.Select(b => $"-v {b}"))} {part.SelectToken("$.image").ToString()} {string.Join(" ", arguments)}";

                        logger.LogInformation(runCommand);
                        

                        string stdOut = await executor.ExecuteStepAsync(parser, arguments, part, volumnes, cancellationToken);

                        parser.Functions["stdout"] = (d, o) => stdOut;

                        var outputs = part["outputs"] as JObject ?? new JObject();

                        foreach (var outputProperty in outputs.Properties())
                        {
                            if (outputProperty.Value.ToString().StartsWith("["))
                            {
                                outputProperty.Value.Replace(parser.Evaluate(outputProperty.Value.ToString()));
                            }
                        }

                        //Console.WriteLine("stdout:");
                       // Console.WriteLine(stdOut.ToString());
                         



                        //  await client.Containers.RemoveContainerAsync(container.ID, new ContainerRemoveParameters { RemoveVolumes = true, RemoveLinks = true, Force = true });

                       // Console.WriteLine(part.ToString());
                    }
                    finally
                    {
                        semaphoreObject.Release();
                    }

                }, new ExecutionDataflowBlockOptions
                {
                    MaxDegreeOfParallelism = parser.Evaluate("[parameters('numberOfProcesses')]").ToObject<int>()
                });

                //  var prop = command.Parent;
                // Console.WriteLine(command.ToString());
                foreach (var arguments in commands)
                {
                    await block.SendAsync(arguments);  
                }

                block.Complete();
                await block.Completion;
              
                {
                    var outputs = part["outputs"] as JObject ?? new JObject();
                    outputs["completed"] = true;
                    part["outputs"] = outputs;
                    logger.LogInformation("pipeline completed with {output}", outputs);
                }



            }


            await executor.PipelineFinishedAsync(parser);

        }

        private static string GetMountedFrom(KeyValuePair<string,JObject> v)
        {

            return v.Value.SelectToken("$.mountedFrom")?.ToString()
                ?? v.Value.SelectToken("$.local")?.ToStringOrNull()
                ?? v.Key;
        }

        private void HandleObjReplacement(JObject volumeObj)
        {
            foreach (var prop in volumeObj.Properties())
            {
                if (prop.Value.Type == JTokenType.String && prop.Value.ToString().StartsWith("["))
                {
                    prop.Value.Replace(parser.Evaluate(prop.Value.ToString()));
                }else if(prop.Value is JObject nestedObj)
                {
                    HandleObjReplacement(nestedObj);
                }
            }
        }



    }
}

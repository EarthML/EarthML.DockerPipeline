using Docker.DotNet;
using Docker.DotNet.Models;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace EarthML.DockerPipeline.Document
{
    public class DockerPipelineRunner
    {
        private ExpressionParser parser;
        private DockerClient client;


        public DockerPipelineRunner(ExpressionParser parser, DockerClient client)  
        {
            this.client = client;
            this.parser = parser;

            parser.Functions["stdout"] = (document, arguments) => "hello world";
            parser.Functions["numbers"] = (document, arguments) => new JArray(arguments.SelectMany(c => c).Select(c => double.Parse(c.ToString())));
            parser.Functions["number"] = (document, arguments) => arguments.Select(c => double.Parse(c.ToString())).FirstOrDefault();
            parser.Functions["int"] = (document, arguments) => arguments.Select(c => (int)double.Parse(c.ToString())).FirstOrDefault();
            parser.Functions["output"] = (document, arguments) => document.SelectToken($"$.steps[?(@.name == '{arguments[0].ToString()}')].outputs").SelectToken(arguments[1].ToString());
            parser.Functions["exists"] = (document, arguments) => Directory.Exists(arguments[0].ToString());
            parser.Functions["folders"] = (document, arguments) => new JArray(Directory.GetDirectories(arguments[0].ToString()));
            parser.Functions["join"] = (document, arguments) => string.Join(arguments.First().ToString(), arguments.Skip(1).SelectMany(c => c).Select(c => c.ToString()));
        }

        public async Task RunPipelineAsync(CancellationToken cancellationToken)
        {
            foreach (var part in parser.Document.SelectToken("$.pipe"))
            {
                var command = part.SelectToken("$.command");

                var dependsOn = part.SelectToken("$.dependsOn")?.Select(c => c.ToString()).ToArray();

                var volumnes = new List<JToken>();

                var dependenciesResolved = true;
                if (dependsOn != null)
                {
                    foreach (var dependency in dependsOn)
                    {
                        if (dependency.StartsWith("/volumes/"))
                        {
                            var volumeName = dependency.Substring("/volumes/".Length);
                            var volume = parser.Document.SelectToken($"$.volumes.{volumeName}");

                            if (volume != null)
                            {
                                if (volume.SelectToken("$.mountedFrom").ToString().StartsWith("["))
                                {
                                    volume.SelectToken("$.mountedFrom").Replace(parser.Evaluate(volume.SelectToken("$.mountedFrom").ToString()));
                                }
                                // volumnes.Add(volume.SelectToken("$.mountedAt").ToString(),new EmptyStruct { });
                                volumnes.Add(volume);
                            }
                            else
                            {
                                dependenciesResolved = false;
                                break;
                            }
                        }
                        else
                        {
                            var completed = parser.Document.SelectToken($"$.steps[?(@.name == '{dependency}')].outputs.completed")?.ToObject<bool>() ?? false;
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

              

                var commands = new List<string[]>() { };
                var loop = part.SelectToken("loop");
                if (loop != null)
                {
                    foreach (var loopVar in parser.Evaluate(loop.ToString()))
                    {
                        parser.Functions["loop"] = (document, arguments) => Path.GetFileName(loopVar.ToString());

                       
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

                if(!command.Any())
                {
                    var outputs = part["outputs"] as JObject ?? new JObject();
                    outputs["completed"] = true;
                    part["outputs"] = outputs;
                    Console.WriteLine(part.ToString());
                    continue;
                }


                var block = new ActionBlock<string[]>(async arguments =>
                {



                    var binds = volumnes.Select((v, i) => $"{v.SelectToken("$.mountedFrom").ToString()}:{v.SelectToken("$.mountedAt").ToString()}").ToList();
                    var runCommand = $"docker run {string.Join(" ", binds.Select(b => $"-v {b}"))} {part.SelectToken("$.image").ToString()} {string.Join(" ", arguments)}";

                    Console.WriteLine(runCommand);

                    var container = await client.Containers.CreateContainerAsync(new CreateContainerParameters
                    {
                        Hostname = "",
                        Domainname = "",
                        User = "",
                        AttachStdin = false,
                        AttachStderr = true,
                        AttachStdout = true,
                        Tty = true,
                        Volumes = volumnes.ToDictionary(volume => volume.SelectToken("$.mountedAt").ToString(), v => new EmptyStruct()),
                        Image = part.SelectToken("$.image").ToString(),
                        Cmd = arguments,
                        //Cmd=new[] { "gdalinfo","eudem_dem_3035_europe.tif"  }.ToList(),
                        HostConfig = new HostConfig
                        {
                            LogConfig = new LogConfig { Type = "json-file" },
                            AutoRemove = false,
                            Binds = binds
                        }

                    });
                    await client.Containers.StartContainerAsync(container.ID, new ContainerStartParameters { }, cancellationToken);

                    // Console.WriteLine(string.Join("\n", container.Warnings));
                    var wait = await client.Containers.WaitContainerAsync(container.ID, CancellationToken.None);

                    await WriteLog(client, container, new ContainerLogsParameters { ShowStderr = true }, cancellationToken);
                    var stdOut = await WriteLog(client, container, new ContainerLogsParameters { ShowStdout = true }, cancellationToken);



                    parser.Functions["stdout"] = (d, o) => stdOut;

                    var outputs = part["outputs"] as JObject ?? new JObject();

                    foreach (var outputProperty in outputs.Properties())
                    {
                        if (outputProperty.Value.ToString().StartsWith("["))
                        {
                            outputProperty.Value.Replace(parser.Evaluate(outputProperty.Value.ToString()));
                        }
                    }

                    Console.WriteLine("stdout:");
                    Console.WriteLine(stdOut.ToString());


                  

                    //  await client.Containers.RemoveContainerAsync(container.ID, new ContainerRemoveParameters { RemoveVolumes = true, RemoveLinks = true, Force = true });

                    Console.WriteLine(part.ToString());

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
                }



            }
        }
        private static async Task<string> WriteLog(DockerClient client, CreateContainerResponse container, ContainerLogsParameters op, CancellationToken token)
        {
            var log = await client.Containers.GetContainerLogsAsync(container.ID, op, token);
            var sb = new StringBuilder();
            using (var reader = new StreamReader(log))
            {
                while (!reader.EndOfStream)
                {
                    var line = await reader.ReadLineAsync();
                    if (op.ShowStderr.HasValue && op.ShowStderr.Value)
                    {
                        Console.WriteLine(line);
                    }
                    sb.AppendLine(line.TrimEnd(Environment.NewLine.ToCharArray()));
                }
            }
            return sb.ToString();
        }
    }
}

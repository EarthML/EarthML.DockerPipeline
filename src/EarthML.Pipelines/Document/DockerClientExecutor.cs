using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EarthML.Pipelines.Document
{
    public class DockerClientExecutor : DockerPipelineExecutor
    {
        private readonly DockerClient client;
        private readonly ILogger logger;

        public DockerClientExecutor(DockerClient dockerClient, ILogger logger)
        {
            this.client = dockerClient ?? throw new ArgumentNullException(nameof(dockerClient));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }
        public async Task<string> ExecuteStepAsync(ExpressionParser parser, string[] arguments, JToken part,  IDictionary<string,JObject> volumnes, CancellationToken cancellationToken)
        {
         
            var volumneMounts = volumnes.Select(volume => volume.Value.SelectToken("$.mountedAt").ToString()).ToArray();

            foreach(var local in volumnes.Where(v => v.Value.SelectToken("$.local") !=null))
            {

                var localName = local.Value.SelectToken("$.local");
                var volumeName = localName.Type == JTokenType.String ? localName.ToString() : $"{parser.Id}-{local.Key}";
                local.Value["localName"] = volumeName;


                var list = await client.Volumes.ListAsync();

                if (list.Volumes == null || !list.Volumes.Any(v => v.Name == volumeName))
                {

                    var retry = false;
                    do
                    {
                        retry = false;

                        try
                        {

                            var vd = await client.Volumes.CreateAsync(new VolumesCreateParameters
                                {
                                    Labels = new Dictionary<string, string>() {
                                    { "pipelineid",parser.Id},
                                    { "createdat", DateTimeOffset.UtcNow.UtcTicks.ToString()},
                                    { "cache",local.Value.SelectToken("$.cache")?.ToString() ?? "false"}
                                },
                                Name = volumeName
                            }, cancellationToken);
                            local.Value["localName"] = vd.Name;
                        }
                        catch (Docker.DotNet.DockerApiException ex)
                        {
                            var response = JToken.Parse(ex.ResponseBody);
                            var msg = response["message"]?.ToString();
                            Console.WriteLine(msg);

                            if (msg.IndexOf("no space left on device") != -1)
                            {
                                Console.WriteLine("Removing half of the volumes");

                                var half = list.Volumes.Where(v =>v.Labels !=null && v.Labels.ContainsKey("createdat")).OrderBy(v => long.Parse(v.Labels["createdat"])).ToArray();

                                foreach (var volume in half.Take(half.Length / 2).ToArray())
                                {
                                    try
                                    {
                                        await client.Volumes.RemoveAsync(volume.Name);
                                    }
                                    catch (Exception removeex)
                                    {
                                        Console.WriteLine(removeex);
                                    }
                                }

                                retry = true;

                            }
                        }
                    } while (retry);
                }
               


            }

            logger.LogInformation("Using {@volumnes}",volumnes);

         //   Console.WriteLine(JToken.FromObject(volumnes).ToString(Newtonsoft.Json.Formatting.Indented));

            var binds = volumnes.Select((v, i) => $"{v.Value.SelectToken("$.mountedFrom")?.ToString() ?? v.Value.SelectToken("$.localName")?.ToString() ?? v.Key}:{v.Value.SelectToken("$.mountedAt").ToString()}").ToList();

            logger.LogInformation("Using {@volumnes}, {@binds} and {@volumneMounts}", volumnes,binds, volumneMounts);
          
            var image = part.SelectToken("$.image").ToString();
            var tag = "latest";

            var imgParts = image.Split(":");
            if (imgParts.Length > 1)
            {
                image = imgParts.First();
                tag = imgParts.Last();
            }
            logger.LogInformation("Using {image}:{tag}", image, tag);
            if (!(part.SelectToken("$.skipImageDownload")?.ToObject<bool>() ?? false))
            {
                try
                {
                    using (var s = logger.BeginScope("creating {image}:{tag}", image, tag))
                    {

                        await client.Images.CreateImageAsync(new ImagesCreateParameters { FromImage = image, Tag = tag }, new AuthConfig
                        {

                        }, new Progress<JSONMessage>((c) => { logger.LogInformation("{@progress}", c); }));//if (!string.IsNullOrEmpty(c.ProgressMessage)){ logger.LogInformation(c.ProgressMessage); } }));
                    }
                }
                catch (Docker.DotNet.DockerApiException ex)
                {

                    logger.LogError(ex, "Failed to create image {image}:{tag}, {error}", image, tag, ex.ResponseBody);
                     

                }
            }




            var co = new CreateContainerParameters
            {
                Hostname = "",
                Domainname = "",
                User = "",
                AttachStdin = false,
                AttachStderr = part.SelectToken("$.attachStderr")?.ToObject<bool>() ?? true,
                AttachStdout = part.SelectToken("$.attachStdout")?.ToObject<bool>() ?? true,
                Tty = part.SelectToken("$.tty")?.ToObject<bool>() ?? true,
                Volumes = volumneMounts.ToDictionary(volume => volume, v => new EmptyStruct()),
                Image = part.SelectToken("$.image").ToString(),

                Cmd = arguments,

                HostConfig = new HostConfig
                {
                    LogConfig = new LogConfig { Type = "json-file" },
                    AutoRemove = false,
                    Binds = binds,
                }

            };
            logger.LogInformation("Creating {@container}",co);

            var container = await client.Containers.CreateContainerAsync(co);

            logger.LogInformation("Starting {@container}",container);

            await client.Containers.StartContainerAsync(container.ID, new ContainerStartParameters {  }, cancellationToken);

            // Console.WriteLine(string.Join("\n", container.Warnings));
            var wait = await client.Containers.WaitContainerAsync(container.ID, CancellationToken.None);

            await WriteLog(client, container, new ContainerLogsParameters { ShowStderr = true }, cancellationToken);
            var stdOut = await WriteLog(client, container, new ContainerLogsParameters { ShowStdout = true }, cancellationToken);
            using (var scope = logger.BeginScope($"{parser.Id}-{part["name"]}.log"))
            {
                logger.LogInformation(stdOut);
            }
           
                

            await client.Containers.RemoveContainerAsync(container.ID, new ContainerRemoveParameters { Force = true }, cancellationToken);
            return stdOut;
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

        public async Task PipelineFinishedAsync(ExpressionParser parser)
        {

            var list = await client.Volumes.ListAsync();
            if (list.Volumes != null)
            {
                foreach (var volume in list.Volumes.Where(v => v.Labels != null && v.Labels.ContainsKey("pipelineid") && v.Labels["pipelineid"] == parser.Id))
                {
                    if (!bool.Parse(volume.Labels["cache"]))
                    {
                        await client.Volumes.RemoveAsync(volume.Name);
                    }
                }
            }

        }
    }
}

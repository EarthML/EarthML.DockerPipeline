using EarthML.Pipelines;
using EarthML.Pipelines.Document;
using Microsoft.Azure.Management.ContainerInstance.Fluent;
using Microsoft.Azure.Management.ContainerInstance.Fluent.Models;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Microsoft.Rest;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace EarthML.DockerScheduler.AzureContainerInstance
{
    public class DockerContainerInstanceExecutorOptions
    {
        public string SubscriptionId { get; set; }
        public ClientCredential ClientCredentials { get; set; }
        public string ResourceGroup { get; set; }
        public string ContainerGroupName { get; set; }
        public int Cpu { get; set; }
        public int MemoryInGB { get; set; }
        public string OsType { get; set; }
        public string Location { get; set; }
        public string TenantId { get;  set; }
    }
    public class DockerContainerInstanceExecutor : DockerPipelineExecutor
    {
        protected DockerContainerInstanceExecutorOptions Options { get; set; }
        public DockerContainerInstanceExecutor(DockerContainerInstanceExecutorOptions executorOptions)
        {
            Options = executorOptions;
            adal = new AuthenticationContext($"https://login.windows.net/{Options.TenantId}/oauth2/authorize");
        }
        private class Test : DelegatingHandler
        {

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {

                request.RequestUri = new Uri(request.RequestUri.AbsoluteUri.Replace("2017-08-01-preview", "2017-12-01-preview"));
                return base.SendAsync(request, cancellationToken);
            }

        }

        AuthenticationContext adal;

        public async Task<string> ExecuteStepAsync(ExpressionParser parser, string[] arguments, JToken part, IDictionary<string, JObject> volumes, CancellationToken cancellationToken)
        {

            var token = await adal.AcquireTokenAsync("https://management.azure.com/", Options.ClientCredentials);
            var client = new ContainerInstanceManagementClient(new TokenCredentials(token.AccessToken), new Test());
            client.SubscriptionId = Options.SubscriptionId;
            

            var cn = await client.ContainerGroups.CreateOrUpdateAsync(Options.ResourceGroup,Options.ContainerGroupName, new ContainerGroupInner
            {
                Containers = new[]
                {
                    new Container
                    {
                        Name = part.SelectToken("$.name").ToString(),
                        Image = part.SelectToken("$.image").ToString(),
                        Resources = new ResourceRequirements{ Requests = new ResourceRequests{ Cpu = Options.Cpu, MemoryInGB = Options.MemoryInGB } },
                            VolumeMounts = volumes.Select(v=>
                            new VolumeMount{
                                Name = v.Key,
                                MountPath = v.Value.SelectToken("$.mountedAt").ToString()
                            }).ToArray() ,
                        Command = arguments
                    }
                },
                ImageRegistryCredentials =
                 parser.Document.SelectToken("$.imageRegistryCredentials").ToObject<ImageRegistryCredential[]>(),
                 Location = Options.Location,
                OsType = Options.OsType,
                RestartPolicy = "Never",
                Volumes =
                    volumes.Select(v => new Volume(v.Key, v.Value.SelectToken("$.azureFileShare").ToObject<AzureFileVolume>())).ToArray()
                
            

            });

            while (cn.ProvisioningState == "Creating" || !cn.Containers[0].InstanceView.CurrentState.State.Equals("Terminated", StringComparison.OrdinalIgnoreCase))
            {

                await Task.Delay(30000);
                cn = await client.ContainerGroups.GetAsync(Options.ResourceGroup, Options.ContainerGroupName);
            }

        //    var log = await client.ContainerLogs.ListAsync(Options.ResourceGroup, part.SelectToken("$.name").ToString(), Options.ContainerGroupName);

            await client.ContainerGroups.DeleteAsync(Options.ResourceGroup, Options.ContainerGroupName, cancellationToken);

            return "";// log.Content;
        }

        public Task PipelineFinishedAsync(ExpressionParser parser)
        {
            return Task.CompletedTask;
        }
    }
}

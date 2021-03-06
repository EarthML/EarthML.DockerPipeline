﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using EarthML.Pipelines.ServiceProvider.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.ServiceFabric.Services.Client;
using Microsoft.ServiceFabric.Services.Remoting.Client;
using Newtonsoft.Json.Linq;
using Unity;

namespace EarthML.Pipelines.ServiceProvider
{

    public class PipelineRunnerHub : Hub
    {
        private readonly PipelineRunnerTaskManager taskManager;

      
        public PipelineRunnerHub(PipelineRunnerTaskManager taskManager)
        {
           this.taskManager = taskManager;
        }

        public override async Task OnConnectedAsync()
        {
          //  await Clients.Client(Context.ConnectionId).SendAsync("SetUsersOnline", await GetUsersOnline());
            await base.OnConnectedAsync();
        }

     
        public async Task Connected(string[] containers)
        {

            await Clients.Client(Context.ConnectionId).SendAsync("msg","Welcome :" + Context.ConnectionId);
        }

        public async Task pipeline_recieved(string id)
        {
         await   this.taskManager.CompleteAsync( id);
           // await Clients.All.SendAsync("Send", Context.User.Identity.Name, message);
        }
    }

    public class PipelineRunnerTaskManager
    {
        ConcurrentDictionary<string, TaskCompletionSource<byte[]>> _tasks = new ConcurrentDictionary<string, TaskCompletionSource<byte[]>>();

        public async Task<string> SendAsync(IHubContext<PipelineRunnerHub> hub, JToken data)
        {
            var id = Guid.NewGuid().ToString("N");

            await hub.Clients.All.SendAsync("run_pipeline", arg1: new { id, data }).ConfigureAwait(false);

             var task = new TaskCompletionSource<byte[]>();
            _tasks[id] = task;

            return id;
        }

        public Task CompleteAsync(string id)
        {
            if (_tasks.ContainsKey(id))
            { 
                _tasks.Remove(id, out TaskCompletionSource<byte[]> promise);
                promise.SetResult(new byte[0]);
            }
           
            return Task.CompletedTask;
        }

        public string GetStatus(string pipelineId)
        {
            return _tasks.ContainsKey(pipelineId) ? "running":"completed";
        }
    }

    public class PipelineRunnerController : Controller {


        [HttpPost]
        [Route("subscriptions/{subscriptionid}/providers/EarthML.Pipelines/PipelineRunners/{id}/pipelines")]
        public async Task<IActionResult> CreateDocumentOnPipeline(string id, [FromBody] JToken body
           // [FromServices] IPipelineRunnerService pipelineRunnerService 
            //[FromServices] PipelineRunnerTaskManager pipelineRunnerTaskManager, 
            //[FromServices] IHubContext<PipelineRunnerHub> hubContext
            )
        {
            var documentId = Guid.NewGuid().ToString();

            var md5 = MD5.Create();
            var value = md5.ComputeHash(Encoding.ASCII.GetBytes(documentId));
            var key = BitConverter.ToInt64(value, 0);


            var pipelineRunnerService = ServiceProxy.Create<IPipelineRunnerService>(
                        new Uri($"fabric:/EarthML.PipelinesApplication/PipelineRunnerService"),new ServicePartitionKey(key), listenerName: "V2_1Listener");
            var document = new DocumentRequest { id = Guid.NewGuid().ToString(), data = body.ToObject<PipelineData>() };
            await pipelineRunnerService.SendAsync(document);
           
            //var reply = await pipelineRunnerTaskManager.SendAsync(hubContext, body);

            //body["id"] = reply;
            //body["status"] = "created";

            return Ok(body);

        }
        [HttpGet]
        [Route("subscriptions/{subscriptionid}/providers/EarthML.Pipelines/PipelineRunners/{id}/pipelines/{pipelineId}")]
        public async Task<IActionResult> GetPipeline(string id, string pipelineId, [FromServices] PipelineRunnerTaskManager pipelineRunnerTaskManager, [FromServices] IHubContext<PipelineRunnerHub> hubContext)
        {


             

            return Ok(new {
                id=pipelineId,
                status = pipelineRunnerTaskManager.GetStatus(pipelineId)
            });

        }

        //[HttpPost]
        //[Route("subscriptions/{subscriptionid}/providers/EarthML.Pipelines/PipelineRunners")]
        //public async Task<IActionResult> CreatePipelineRunner()
        //{
        //    var obj = new { id = Guid.NewGuid().ToString() };
        //    return CreatedAtAction(nameof(GetPipelineRunner),obj,obj);
        //}
    }

    public class Startup
    {
        // This method gets called by the runtime. Use this method to add services to the container.
        // For more information on how to configure your application, visit https://go.microsoft.com/fwlink/?LinkID=398940
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddMvc();
            services.AddSignalR();
            services.AddSingleton(new PipelineRunnerTaskManager()); 
        }

        public void ConfigureContainer(IUnityContainer container)
        {
            container.RegisterInstance("This string is displayed if container configured correctly",
                                       "This string is displayed if container configured correctly");
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IHostingEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            
            app.UseSignalR(routes =>
            {
                routes.MapHub<PipelineRunnerHub>("/pipelines");
            });
            app.UseMvc();
        }
    }
}

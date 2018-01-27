using Docker.DotNet;
using EarthML.DockerPipeline;
using EarthML.DockerPipeline.Document;
using EarthML.DockerPipeline.Parameters;
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace EarthML.DockerPipelineCli
{
    class Program
    {
         
        static async Task Main(string[] args)
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
             
            var runner = new DockerPipelineRunner(
                 new ExpressionParser(args.ReadAsDocument()
                .UpdateParametersFromConsoleArguments(args))
                .AddRegex()
                .AddSplit()
                .AddConcat()
                .AddAll()
               ,
                  new DockerClientConfiguration(new Uri("npipe://./pipe/docker_engine"))
                .CreateClient());



            await runner.RunPipelineAsync(CancellationToken.None);
           

          
        }
      
       
    }
}

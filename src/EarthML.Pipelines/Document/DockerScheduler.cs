using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace EarthML.Pipelines.Document
{
    public interface DockerPipelineExecutor
    {
        Task<string> ExecuteStepAsync(ExpressionParser parser, string[] arguments, JToken part, IDictionary<string, JObject> volumnes, CancellationToken cancellationToken);
        Task PipelineFinishedAsync(ExpressionParser parser);
    }
    public static class JTokenExtensions
    {
        public static string ToStringOrNull(this JToken token)
        {
            if (token.Type == JTokenType.String)
                return token.ToString();
            return null;
        }
    }
}

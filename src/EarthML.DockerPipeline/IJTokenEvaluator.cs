using Newtonsoft.Json.Linq;

namespace EarthML.DockerPipeline
{
    public interface IJTokenEvaluator
    {
        JToken Evaluate();
    }
}

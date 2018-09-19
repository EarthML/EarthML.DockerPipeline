using Newtonsoft.Json.Linq;

namespace EarthML.Pipelines
{
    public interface IJTokenEvaluator
    {
        JToken Evaluate();
    }
}

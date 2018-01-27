using Newtonsoft.Json.Linq;

namespace EarthML.DockerPipeline
{
    public class ConstantEvaluator : IJTokenEvaluator
    {
        private string k;

        public ConstantEvaluator(string k)
        {
            this.k = k;
        }

        public JToken Evaluate()
        {
            return JToken.Parse(k);
        }
    }
}

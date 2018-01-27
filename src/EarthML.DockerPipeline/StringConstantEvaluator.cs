using Newtonsoft.Json.Linq;

namespace EarthML.DockerPipeline
{
    public class StringConstantEvaluator : IJTokenEvaluator
    {
        private string text;

        public StringConstantEvaluator(string text)
        {
            this.text = text;
        }

        public JToken Evaluate()
        {
            return text;
        }
    }
}

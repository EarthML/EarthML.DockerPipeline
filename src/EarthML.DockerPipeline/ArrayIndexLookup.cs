using Newtonsoft.Json.Linq;

namespace EarthML.DockerPipeline
{
    public class ArrayIndexLookup : IJTokenEvaluator
    {
        public string parsedText;

        public ArrayIndexLookup(string parsedText)
        {
            this.parsedText = parsedText;
        }

        public IJTokenEvaluator ArrayEvaluator { get; set; }

        public JToken Evaluate()
        {
            return ArrayEvaluator.Evaluate()[int.Parse(parsedText)];
        }
    }
}

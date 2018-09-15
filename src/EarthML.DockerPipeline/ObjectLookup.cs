using Newtonsoft.Json.Linq;

namespace EarthML.DockerPipeline
{
    internal class ObjectLookup : IJTokenEvaluator
    {
        public string text;

        public ObjectLookup(string text)
        {
            this.text = text;
        }

        public IJTokenEvaluator Object { get; internal set; }

        public JToken Evaluate()
        {
            return Object.Evaluate()[text];
        }
    }
}
using Newtonsoft.Json.Linq;
using System.Linq;

namespace EarthML.Pipelines
{
    public class FunctionEvaluator : IJTokenEvaluator
    {
        private string name;
        private IJTokenEvaluator[] parameters;
        private ExpressionParser evaluator;
        public FunctionEvaluator(ExpressionParser evaluator, string name, IJTokenEvaluator[] parameters)
        {
            this.name = name;
            this.parameters = parameters;
            this.evaluator = evaluator;
        }

        public JToken Evaluate()
        {
            return evaluator.Evaluate(name, parameters.Select(p => p.Evaluate()).ToArray());
        }


    }
}

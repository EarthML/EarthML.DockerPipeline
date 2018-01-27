using Newtonsoft.Json.Linq;
using Sprache;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace EarthML.DockerPipeline
{
    public static class RegexExpressionParser
    {
        private static JToken ParametersFunc(JToken document, JToken[] arg)
        {
            return document.SelectToken($"$.parameters.{arg[0].ToString()}");
        }

        private static JToken MountedFromFunc(JToken document, JToken[] arg)
        {
            return document.SelectToken($"$.volumes.{arg[0].ToString()}.mountedFrom").ToString();
        }

        private static JToken MountedAtFunc(JToken document, JToken[] arg)
        {
            var volumne = document.SelectToken($"$.volumes.{arg[0].ToString()}");
            if (volumne == null)
            {
                throw new Exception($"The volume with name {arg[0].ToString()} was not found");
            }
            return volumne.SelectToken("$.mountedAt").ToString();
        }

        public static ExpressionParser AddConcat(this ExpressionParser parser)
        {
            parser.Functions["concat"] = (document,arguments) => string.Join("", arguments.Select(k => k.ToString()));
            return parser;
        }

        public static ExpressionParser AddSplit(this ExpressionParser parser)
        {
            parser.Functions["split"] = (document,arguments) => JArray.FromObject(arguments.Single().ToString().Split(","));
            return parser;
        }
        public static ExpressionParser AddRegex(this ExpressionParser parser)
        {
            parser.Functions["regex"] = RegexFunc;
            return parser;
        }
        public static ExpressionParser AddAll(this ExpressionParser parser)
        {
            parser.Functions["mountedAt"] = MountedAtFunc;
            parser.Functions["mountedFrom"] = MountedFromFunc;
            parser.Functions["parameters"] = ParametersFunc;


            return parser;
        }

        private static JToken RegexFunc(JToken document, JToken[] arguments)
        {
            var group = int.Parse(arguments[2].ToString().Trim('$'));
            var input = arguments[0].ToString();
            var pattern = arguments[1].ToString();

            return SelectRegexGroup(group,
                Regex.Match(input, pattern));
        }
        private static JToken SelectRegexGroup(int v, Match match)
        {
            return JToken.FromObject(match.Groups[v].Value);
        }
    }
    public class ExpressionParser
    {
        public readonly Parser<IJTokenEvaluator> Function;
        public readonly Parser<IJTokenEvaluator> Constant;

        private static readonly Parser<char> DoubleQuote = Parse.Char('"');
        private static readonly Parser<char> SingleQuote = Parse.Char('\'');
        private static readonly Parser<char> Backslash = Parse.Char('\\');

        private static readonly Parser<char> QdText =
            Parse.AnyChar.Except(DoubleQuote);
        private static readonly Parser<char> QdText1 =
    Parse.AnyChar.Except(SingleQuote);

        //private static readonly Parser<char> QuotedPair =
        //    from _ in Backslash
        //    from c in Parse.AnyChar
        //    select c;

        private static readonly Parser<StringConstantEvaluator> QuotedString =
            from open in DoubleQuote
            from text in QdText.Many().Text()
            from close in DoubleQuote
            select new StringConstantEvaluator(text);

        private static readonly Parser<StringConstantEvaluator> QuotedSingleString =
           from open in SingleQuote
           from text in QdText1.Many().Text()
           from close in SingleQuote
           select new StringConstantEvaluator(text);

        public Dictionary<string, Func<JToken,JToken[], JToken>> Functions { get; set; } = new Dictionary<string, Func<JToken,JToken[], JToken>>();

        private readonly Parser<IJTokenEvaluator> Number = from op in Parse.Optional(Parse.Char('-').Token())
                                                           from num in Parse.Decimal
                                                           from trailingSpaces in Parse.Char(' ').Many()
                                                           select new DecimalConstantEvaluator(decimal.Parse(num) * (op.IsDefined ? -1 : 1));

        public JToken Document { get; set; }
        public ExpressionParser(JToken document)
        {
            Function = from name in Parse.Letter.AtLeastOnce().Text()
                       from lparen in Parse.Char('(')
                       from expr in Parse.Ref(() => Function.Or(Number).Or(QuotedString).Or(QuotedSingleString).Or(Constant)).DelimitedBy(Parse.Char(',').Token())
                       from rparen in Parse.Char(')')
                       select CallFunction(name, expr.ToArray());

            Constant = Parse.LetterOrDigit.AtLeastOnce().Text().Select(k => new ConstantEvaluator(k));
            Document = document;
        }


        
        public JToken Evaluate(string name, params JToken[] arguments)
        {
            return Functions[name](Document,arguments);

        }

        IJTokenEvaluator CallFunction(string name, IJTokenEvaluator[] parameters)
        {
            return new FunctionEvaluator(this, name, parameters);
        }

        public JToken Evaluate(string str)
        {
            var stringParser = //Apparently the name 'string' was taken...
                 from first in Parse.Char('[')
                 from text in this.Function
                 from last in Parse.Char(']')
                 select text;



            var func = stringParser.Parse(str);

            return func.Evaluate();
        }



    }
}

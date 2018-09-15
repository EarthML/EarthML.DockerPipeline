using Newtonsoft.Json.Linq;
using Sprache;
using System;
using System.Collections.Generic;
using System.Linq;

namespace EarthML.DockerPipeline
{
    public class ExpressionParser
    {
        public readonly Parser<IJTokenEvaluator> Function;
        public readonly Parser<IJTokenEvaluator> Constant;
        public readonly Parser<IJTokenEvaluator> ArrayIndexer;
        public readonly Parser<IJTokenEvaluator> PropertyAccess;
        public readonly Parser<IJTokenEvaluator[]> Tokenizer;

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
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
       
        public ExpressionParser(JToken document)
        {
            Constant = Parse.LetterOrDigit.AtLeastOnce().Text().Select(k => new ConstantEvaluator(k));

            Tokenizer = from expr in Parse.Ref(() => Parse.Ref(() => (Function.Or(Number).Or(QuotedString).Or(QuotedSingleString).Or(Constant)).Or(ArrayIndexer).Or(PropertyAccess)).AtLeastOnce()).Optional().DelimitedBy(Parse.Char(',').Token())
                        select FixArrayIndexers(expr.Select(c => (c.GetOrDefault() ?? Enumerable.Empty<IJTokenEvaluator>()).ToArray()).ToArray());

            Function = from name in Parse.Letter.AtLeastOnce().Text()
                       from lparen in Parse.Char('(')
                       from expr in Tokenizer
                       from rparen in Parse.Char(')')
                           select CallFunction(name, expr);

            PropertyAccess = from first in Parse.Char('.')
                             from text in Parse.LetterOrDigit.AtLeastOnce().Text()
                             select new ObjectLookup(text);

            ArrayIndexer = from first in Parse.Char('[')
                           from text in Parse.Number
                           from last in Parse.Char(']')
                           select new ArrayIndexLookup(text); ;

            Document = document;
        }

        private IJTokenEvaluator[] FixArrayIndexers(IJTokenEvaluator[][] enumerable)
        {
            return enumerable.Where(c=>c.Any()).Select(c => ArrayLookup(c)).ToArray();
        }

        private IJTokenEvaluator ArrayLookup(IJTokenEvaluator[] c)
        {
            if(c.Length == 1)
                return c.First();

            if (c.Length == 2 && c[1] is ArrayIndexLookup looup)
            {
                looup.ArrayEvaluator = c[0];
                return looup;
            }
            
            if(c.Length == 2 && c[1] is ObjectLookup lookup)
            {
                lookup.Object = c[0];
                return lookup;
            }

                return null;

        }

        public JToken Evaluate(string name, params JToken[] arguments)
        {
            if (!Functions.ContainsKey(name))
                throw new Exception($"{name} not found in functions");
            return Functions[name](Document,arguments);

        }

        IJTokenEvaluator CallFunction(string name, IJTokenEvaluator[] parameters)
        {
            return new FunctionEvaluator(this, name, parameters);
        }

        public JToken Evaluate(string str)
        {  

            Parser<IJTokenEvaluator[]> stringParser =  
                 from first in Parse.Char('[')
                 from evaluator in Tokenizer  
                 from last in Parse.Char(']')
                 select evaluator;



            var func = stringParser.Parse(str).ToArray();
            if(func.Length == 1)
             return func.First().Evaluate();

            for(var i = 0; i < func.Length; i++)
            {
                if (func[i] is ArrayIndexLookup array)
                {
                    var arrayToken = func[i-1].Evaluate();
                    if (arrayToken.Type != JTokenType.Array)
                        throw new Exception("not an array");

                    return arrayToken[int.Parse(array.parsedText)];

                }else if( func[i] is ObjectLookup objectLookup)
                {
                    var arrayToken = func[i - 1].Evaluate();
                    if (arrayToken.Type != JTokenType.Object)
                        throw new Exception("not an object");

                    return arrayToken[objectLookup.text];

                }
            }

            return null;
           


        }



    }
}

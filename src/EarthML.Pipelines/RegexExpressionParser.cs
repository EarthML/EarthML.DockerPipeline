using Newtonsoft.Json.Linq;
using Sprache;
using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace EarthML.Pipelines
{
    public static class RegexExpressionParser
    {
        private static JToken ParametersFunc(JToken document, JToken[] arg)
        {
            return document.SelectToken($"$.parameters.{arg[0].ToString()}");
        }

        //private static JToken MountedFromFunc(JToken document, JToken[] arg)
        //{
        //    return document.SelectToken($"$.volumes.{arg[0].ToString()}.mountedFrom").ToString();
        //}

        //private static JToken MountedAtFunc(JToken document, JToken[] arg)
        //{
        //    var volumne = document.SelectToken($"$.volumes.{arg[0].ToString()}");
        //    if (volumne == null)
        //    {
        //        throw new Exception($"The volume with name {arg[0].ToString()} was not found");
        //    }
        //    return volumne.SelectToken("$.mountedAt").ToString();
        //}

        public static ExpressionParser AddConcat(this ExpressionParser parser)
        {
            parser.Functions["concat"] = (document,arguments) => string.Join("", arguments.Select(k => k.ToString()));
            return parser;
        }

        public static ExpressionParser AddSplit(this ExpressionParser parser)
        {
 
            parser.Functions["split"] = (document,arguments) => JArray.FromObject(arguments.First().ToString().Split(arguments.Skip(1).FirstOrDefault()?.ToString()??","));
            return parser;
        }
        public static ExpressionParser AddRegex(this ExpressionParser parser)
        {
            parser.Functions["regex"] = RegexFunc;
            return parser;
        }
        public static ExpressionParser AddAll(this ExpressionParser parser)
        {
           // parser.Functions["mountedAt"] = MountedAtFunc;
           // parser.Functions["mountedFrom"] = MountedFromFunc;
            parser.Functions["parameters"] = ParametersFunc;
            parser.Functions["replace"] = ReplaceFunc;

            //   parser.Functions["date"]= (document, arguments) =>

            parser.Functions["date"] = DateFunc;
            return parser;
        }


        static System.Globalization.CultureInfo cul = System.Globalization.CultureInfo.CurrentCulture;
        private static JToken DateFunc(JToken document, JToken[] arguments)
        {
            var date = arguments.First().ToObject<DateTime>();
            var format = arguments.Skip(1).First().ToString();
            format = format.Replace("Q",$"Q{GetQuarter(date)}");
            format = format.Replace("W", $"W{cul.Calendar.GetWeekOfYear(date, CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday)}");

            var formatted = date.ToString(format);

            return formatted;
        }
        public static int GetQuarter(DateTime date)
        {
            if (date.Month >= 4 && date.Month <= 6)
                return 2;
            else if (date.Month >= 7 && date.Month <= 9)
                return 3;
            else if (date.Month >= 10 && date.Month <= 12)
                return 4;
            else
                return 1;
        }

        private static JToken ReplaceFunc(JToken document, JToken[] arguments)
        {
          return  arguments.First().ToString().Replace(arguments[1].ToString(), arguments[2].ToString());
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
}

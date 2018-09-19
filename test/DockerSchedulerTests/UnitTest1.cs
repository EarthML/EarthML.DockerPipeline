using EarthML.Pipelines;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using Sprache;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.RegularExpressions;

namespace DockerPipelineTests
{
   
   
    [TestClass]
    public class UnitTest1
    {
        [TestMethod]
        public void canStringHaveProps()
        {
            var parser = new ExpressionParser(new JObject())
                .AddRegex().AddSplit().AddAll().AddConcat();
            parser.Functions["test"] = 
                (d,a)=> new JObject(new JProperty("a",1));

            Assert.AreEqual(1, parser.Evaluate("[test().a]"));
        }

        [TestMethod]
        public void TestSplitArrayIndex()
        {
            var parser = new ExpressionParser(new JObject())
              .AddRegex().AddSplit().AddAll().AddConcat();

            Assert.AreEqual("test", parser.Evaluate("[concat(split('test','es')[0],'est')]"));
        }

        [TestMethod]
        public void TestDateWeek()
        {
            var parser = new ExpressionParser(new JObject())
              .AddRegex().AddSplit().AddAll().AddConcat();
            Assert.AreEqual("W4", new DateTime(2017, 2, 1).ToString("W4"));
            Assert.AreEqual("2017W5", parser.Evaluate("[date('2017-02-01','yyyyW')]"));
        }

        [TestMethod]
        public void DateTest()
        {
            var parser = new ExpressionParser(new JObject())
              .AddRegex().AddSplit().AddAll();

            Assert.AreEqual("2017Feb", parser.Evaluate("[date('2017-02-15','yyyyMMM')]"));
        }

        [TestMethod]
        public void TestMethod1()
        {
             
            var parser = new ExpressionParser(new JObject())
                .AddRegex().AddSplit();
            parser.Functions["add"] = (document,arguments) => 
                arguments.Aggregate(0.0, (acc, argument) => acc + argument.ToObject<double>());
 
            Assert.AreEqual(4.0, parser.Evaluate("[add(2,2)]"));
            Assert.AreEqual(7.0, parser.Evaluate("[add(2,2,3)]"));
            Assert.AreEqual(3.0, parser.Evaluate("[add(2,2,-1)]"));
            Assert.AreEqual(4.0, parser.Evaluate("[add(2,2,0,0)]"));

            var stdout = File.ReadAllText("stdout.txt");

            var test = parser.Evaluate("[split(regex(\"testfoo\",\"test(.*)\",\"$1\"))]");

            Assert.AreEqual("[\"foo\"]",test.ToString( Newtonsoft.Json.Formatting.None));


            parser.Functions["stdout"] = (document,arguments) => stdout;
            parser.Functions["numbers"] = (document,arguments) => new JArray(arguments.SelectMany(c => c).Select(c => double.Parse(c.ToString())));

            var AABB = parser.Evaluate("[numbers(split(regex(stdout(2),\"\\('AABB: ', (.*?)\\)\",\"$1\")))]");
           
            CollectionAssert.AreEqual(new[] { 480000, 6150000, -1580, 530000, 6200000, 755 }, AABB.ToObject<int[]>());


            parser.AddConcat();

            Assert.AreEqual("foobar", parser.Evaluate("[concat(\"foo\",\"bar\")]"));
            Assert.AreEqual("foobar", parser.Evaluate("[concat('foo',\"bar\")]"));
            parser.Functions["number"] = (document,arguments) => arguments.Select(c => double.Parse(c.ToString())).FirstOrDefault();

            Assert.AreEqual(16.0, parser.Evaluate("[number(regex(stdout(0),\"\\('Suggested number of tiles: ', (.*?)\\)\",\"$1\"))]"));

            Assert.AreEqual(11.0, parser.Evaluate("[number(regex(stdout(0),\"\\('Suggested Potree-OctTree number of levels: ', (.*?)\\)\",\"$1\"))]"));

            Assert.AreEqual(205.0, parser.Evaluate("[number(regex(stdout(0),\"\\('Suggested Potree-OctTree spacing: ', (.*?)\\)\",\"$1\"))]"));

 
        }

    

        private JToken RegexFunc(JToken[] arguments)
        { var group = int.Parse(arguments[2].ToString().Trim('$'));
            var input = arguments[0].ToString();
            var pattern = arguments[1].ToString();

            return SelectRegexGroup(group,
                Regex.Match(input, pattern));
        }

        private JToken SelectRegexGroup(int v, Match match)
        {
            return JToken.FromObject(match.Groups[v].Value);
        }
    }
}

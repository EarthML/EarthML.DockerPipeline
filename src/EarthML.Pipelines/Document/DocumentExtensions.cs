using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EarthML.Pipelines.Document
{
    public static class DocumentExtensions
    {
        public static JToken ReadAsDocument(this string path)
        {
            return JToken.Parse(File.ReadAllText(path));
        }
        public static JToken ReadAsDocument(this string[] arguments)
        {
            return JToken.Parse(File.ReadAllText(arguments[Array.IndexOf(arguments, $"--pipeline")+1]));
        }
    }
}

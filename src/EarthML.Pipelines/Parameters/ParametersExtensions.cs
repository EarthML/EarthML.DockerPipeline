﻿using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EarthML.Pipelines.Parameters
{
    public static class ParametersExtensions
    {
        public static JToken UpdateParametersFromConsoleArguments(this JToken document, params string[] arguments)
        {
            foreach (var parameter in (document.SelectToken("$.parameters") as JObject).Properties())
            {
                var idx = Array.IndexOf(arguments, $"--{parameter.Name}");
                if (idx != -1)
                {
                    parameter.Value.Replace(arguments[idx + 1]);
                }
                else
                {
                    parameter.Value.Replace(parameter.Value.SelectToken("$.defaultValue"));
                }
            }

            return document;
        }
        
    }
}

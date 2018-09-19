using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EarthML.Pipelines.Model
{
    public class Pipe
    {
        public string name { get; set; }
        public string image { get; set; }
        public List<string> command { get; set; }
        public List<string> dependsOn { get; set; }
    }
    public class AzureFileShare : Volume
    {
        public string shareName { get; set; }
        public string storageAccountKey { get; set; }
        public string storageAccountName { get; set; }
    }
    public class ImageRegistryCredential
    {
        public string server { get; set; }
        public string password { get; set; }
        public string username { get; set; }
    }
    public class Volume
    {

    }
    public class Pipeline
    {
        [JsonProperty("parameters")]
        public Object Parameters { get; set; }
        public List<ImageRegistryCredential> imageRegistryCredentials { get; set; } = new List<ImageRegistryCredential>();
        public Dictionary<string, Volume> volumes { get; set; }
        public List<Pipe> pipe { get; set; }
    }
}

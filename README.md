
# Docker Pipeline


Docker Pipeline is a json format to describe a pipeline of docker commands that should be run.

Docker Pipeline CLI will take the pipeline document and execute each part of the pipe until its completed. 

Docker Pipeline have a rich and easily extentable framework for expressions for handing input and output from each step in the pipeline. 


A pipeline example could look like the following, which will allow converting lidar data into a weboptimized format for visualization using Potree. Using a map reduce idea of first splitting, then mapping in parellel and at the end combining everything. This approach significant speeds things up compared to running it all in one command.
```
  "pipe": [
    {
      "name": "mpc-info",
      "image": "oscar/mpc:v1",
      "command": [ "mpc-info", "-i", "[mountedAt('mpc-info-data')]", "-c", "[parameters('numberOfProcesses')]" ],
      "dependsOn": [ "/volumes/mpc-info-data" ],
      "outputs": {
        "AABB": "[numbers(split(regex(stdout(0),\"\\('AABB: ', (.*?)\\)\",\"$1\")))]",
        "numberOfTiles": "[int(regex(stdout(0),\"\\('Suggested number of tiles: ', (.*?)\\)\",\"$1\"))]",
        "levels": "[int(regex(stdout(0),\"\\('Suggested Potree-OctTree number of levels: ', (.*?)\\)\",\"$1\"))]",
        "spacing": "[number(regex(stdout(0),\"\\('Suggested Potree-OctTree spacing: ', (.*?)\\)\",\"$1\"))]"
      }
    },
    {
      "name": "mpc-tiling",
      "image": "oscar/mpc:v1",
      "dependsOn": [ "mpc-info", "/volumes/mpc-info-data", "/volumes/mpc-tiling-data", "/volumes/mpc-tiling-temp" ],
      "skip": "[exists(mountedFrom('mpc-tiling-data'))]",
      "command": [
        "mpc-tiling",
        "-i", "[mountedAt('mpc-info-data')]",
        "-o", "[mountedAt('mpc-tiling-data')]",
        "-t", "[mountedAt('mpc-tiling-temp')]",
        "-e", "[concat(output('mpc-info','$.AABB[0]') ,' ', output('mpc-info','$.AABB[1]'),' ', output('mpc-info','$.AABB[3]'),' ', output('mpc-info','$.AABB[4]'))]",
        "-n", "[output('mpc-info','$.numberOfTiles')]",
        "-p", "[parameters('numberOfProcesses')]"
      ]
    },
    {
      "name": "mpc-map",
      "image": "oscar/mpc:v1",
      "dependsOn": [ "mpc-tiling", "/volumes/mpc-tiling-dist", "/volumes/mpc-tiling-data" ],
      "loop": "[folders(mountedFrom('mpc-tiling-data'))]",
      "skip": "[exists(concat(mountedFrom('mpc-tiling-dist'), '/poctrees/' ,loop('folderName'), '_potree'))]",
      "command": [
        "PotreeConverter",
        "--outdir",
        "[concat(mountedAt('mpc-tiling-dist'), '/poctrees/' ,loop('folderName'), '_potree')]",
        "--levels",
        "[output('mpc-info','$.levels')]",
        "--output-format",
        "LAZ",
        "--source",
        "[concat(mountedAt('mpc-tiling-data'),'/', loop('folderName'))]",
        "--spacing",
        "[output('mpc-info','$.spacing')]",
        "--aabb",
        "[join(' ', output('mpc-info','$.AABB'))]",
        "--projection",
        "+proj=utm +zone=32 +ellps=GRS80 +towgs84=0,0,0,0,0,0,0 +units=m +no_defs"
      ]
    },
    {
      "name": "mpc-reduce",
      "image": "oscar/mpc:v1",
      "dependsOn": [ "mpc-copy", "/volumes/mpc-tiling-dist" ],
      "command": [ "mpc-merge-all", "-i", "[concat(mountedAt('mpc-tiling-dist'), '/poctrees')]", "-o", "[concat(mountedAt('mpc-tiling-dist'), '/poctrees_merge')]", "-m" ],
      "outputs": {
        "data": "[regex(stdout(0),\"\\('Final merged Potree-OctTree is in: ', '(.*?)'\\)\",\"$1\")]"        
      }
    }
  ]
```

## Expressions

Within the document expressions can be formed to alter and customize the pipeline at runtime. To create a expresssion one would use a string json property formatted to start with a square bracket like so `"[...]"`.

The engine then exposes functions that can accesss parameters, variables, outputs from previosly steps, volumnes and a bunch of other functions.

In the example above the regex expression is heavily used to get outputs from pipeline steps, as docker pipeline is made to build ontop of all the engineering hours already spend out there such one can use tools already build like we did with the [Massive PotreeConverterProject](https://github.com/NLeSC/Massive-PotreeConverter), infact it was only an hour of work to read, learn and be able to write the docker pipeline for their hard work. All credits should go to them if you end up using this for converting lidar data.


## Features


### Parameters
Docker Pipeline also have a parameters section
```
  "parameters": {
    "numberOfProcesses": {
      "type": "number",
      "metadata": "Number of processes [default is 1]",
      "defaultValue": 1
    },
    "lidarInputFolder": {
      "type": "path",
      "metadata": "The location of lidar files"
    },
    "lidarTilesFolder": {
      "type": "path",
      "metadata": "The location of lidar tiles"
    },
    "lidarTempFolder": {
      "type": "path",
      "metadata": "The location of lidar tmp files"
    },
    "potreeOutputFolder": {
      "type": "path",
      "metadata": "The location of lidar tmp files"
    }
  }
```

This allows the pipeline steps to ask for parameters that should change the execution of the pipeline. Together with the CLI, one can now simply change those by --ParameterName \<value>.

### Outputs

Each step of the docker pipeline can provide outputs to the next steps in the pipeline, as example one can look at the first step of above defined pipeline.
```
    {
      "name": "mpc-info",
      "image": "oscar/mpc:v1",
      "command": [ "mpc-info", "-i", "[mountedAt('mpc-info-data')]", "-c", "[parameters('numberOfProcesses')]" ],
      "dependsOn": [ "/volumes/mpc-info-data" ],
      "outputs": {
        "AABB": "[numbers(split(regex(stdout(0),\"\\('AABB: ', (.*?)\\)\",\"$1\")))]",
        "numberOfTiles": "[int(regex(stdout(0),\"\\('Suggested number of tiles: ', (.*?)\\)\",\"$1\"))]",
        "levels": "[int(regex(stdout(0),\"\\('Suggested Potree-OctTree number of levels: ', (.*?)\\)\",\"$1\"))]",
        "spacing": "[number(regex(stdout(0),\"\\('Suggested Potree-OctTree spacing: ', (.*?)\\)\",\"$1\"))]"
      }
    },
```

Expressions in outputs are first evaluated when the step have been executed and is ready for being used in the next steps. 


### DependsOn
Each step can have dependsOn properties that delays the execution until the dependency is ready. A dependency on another step would mean that the other step needs to run first. Expressions of the step are first evaluated after dependencies are ready.

### Skip

It is possible to skip a step. Using expressions like in the example above we used it to skip those mapping steps that already executed if running the same pipeline again by checking if a folder is existing.

### Loop

It is possible to execute the same step/command multiple times in a loop, where expressions are evaluated per itteration in the loop. To access the loop variable in expresions the `loop()` expression can be used. The loop will loop over each element defined in the loop property of the step.



## CLI

To run a pipeline using the CLI, one may run 
```
EarthML.Pipelines.Cli --pipeline exampleJob.json --numberOfProcesses 4 --lidarInputFolder "D:/PKS/LiDAR" --lidarTilesFolder "D:/PKS/potree/tiles" --lidarTempFolder "D:/PKS/potree/tmp" --potreeOutputFolder "D:/PKS/potree/dist"
```
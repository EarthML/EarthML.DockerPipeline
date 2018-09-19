FROM microsoft/aspnetcore-build:2.0 AS build-env
WORKDIR /solution

COPY  . ./
WORKDIR /solution/src/EarthML.Pipelines.Cli
# Copy csproj and restore as distinct layers
#COPY *.csproj ./
RUN dotnet restore

# Copy everything else and build
#COPY . ./
RUN dotnet publish -c Release -o out

# Build runtime image
FROM microsoft/aspnetcore:2.0
WORKDIR /app
COPY --from=build-env /solution/src/EarthML.Pipelines.Cli/out .
ENTRYPOINT ["dotnet", "EarthML.Pipelines.Cli.dll"]
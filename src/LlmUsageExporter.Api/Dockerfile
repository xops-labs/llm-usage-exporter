FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/LlmUsageExporter.Api/LlmUsageExporter.Api.csproj src/LlmUsageExporter.Api/
RUN dotnet restore src/LlmUsageExporter.Api/LlmUsageExporter.Api.csproj

COPY . .
RUN dotnet publish src/LlmUsageExporter.Api/LlmUsageExporter.Api.csproj \
    --configuration Release \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "LlmUsageExporter.Api.dll"]

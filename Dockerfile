# StockWise MCP: container image for Render (or any Docker host).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY StockWiseMcp.csproj ./
RUN dotnet restore StockWiseMcp.csproj
COPY . .
RUN dotnet publish StockWiseMcp.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
# Render sends traffic to $PORT (set to 8080 in render.yaml); TLS is handled by Render's proxy.
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
ENTRYPOINT ["dotnet", "StockWiseMcp.dll"]

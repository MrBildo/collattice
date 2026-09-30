# Container image for the API alone (no bundled frontend). Build from the
# repository root, which is the build context this file expects:
#
#   docker build -f backend/Collabot.Collattice.Api/Dockerfile -t collattice-api .
#
# The API refuses to start without an absolute database path, so pass one at run:
#
#   docker run -p 8080:8080 -e "ConnectionStrings__Board=Data Source=/data/collattice.db" \
#     -v collattice-data:/data collattice-api
#
# The base images track the stable .NET 10 line (the 10.0 tags move with each
# servicing release), never a preview.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY nuget.config ./
COPY backend/ backend/
RUN dotnet publish backend/Collabot.Collattice.Api/Collabot.Collattice.Api.csproj -c Release -o /app/out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/out .
EXPOSE 8080
ENTRYPOINT ["dotnet", "Collabot.Collattice.Api.dll"]

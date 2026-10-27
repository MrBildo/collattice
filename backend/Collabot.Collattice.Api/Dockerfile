# Container image for the API alone (no bundled frontend). Build from the
# repository root, which is the build context this file expects:
#
#   docker build -f backend/Collabot.Collattice.Api/Dockerfile -t collattice-api .
#
# The image keeps the database in /data, a volume, so the board outlives the container. Without
# this the app would fall back to its per-user default, which in this image is root's home folder
# inside the container's own layer, lost when the container is removed. Mount a named volume there:
#
#   docker run -p 8080:8080 -v collattice-data:/data collattice-api
#
# To keep the database elsewhere, set ConnectionStrings__Board at run (-e), which replaces the value
# below.
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
ENV ConnectionStrings__Board="Data Source=/data/collattice.db"
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["dotnet", "Collabot.Collattice.Api.dll"]

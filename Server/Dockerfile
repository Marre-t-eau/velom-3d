# Image du serveur de synchronisation. À construire depuis la racine du dépôt :
#   docker build -f Server/Dockerfile -t velom3d-server .
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Sync/Core/SyncProtocol.cs Sync/Core/
COPY Server/ Server/
RUN dotnet publish Server/Velom3d.Server.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
RUN mkdir /data && chown app /data
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Velom3d__Database=/data/velom3d.db
VOLUME /data
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "Velom3d.Server.dll"]

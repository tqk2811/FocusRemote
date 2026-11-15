# Build from repo root: docker build -f src/FocusRemote.Relay/Dockerfile -t focusremote-relay .
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /repo
COPY src/Directory.Build.props src/Directory.Build.targets src/
COPY src/FocusRemote.Relay/ src/FocusRemote.Relay/
RUN dotnet publish src/FocusRemote.Relay/FocusRemote.Relay.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "FocusRemote.Relay.dll"]

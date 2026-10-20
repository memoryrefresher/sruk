# docker build -t searaider-relay -f src/SeaRaider.Relay/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/SeaRaider.Relay -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
VOLUME /data
ENV Relay__UsersFile=/data/relay-users.json
EXPOSE 5280
ENTRYPOINT ["dotnet", "SeaRaider.Relay.dll"]

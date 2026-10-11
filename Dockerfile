FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ChartifyWebhook.csproj ./
RUN dotnet restore ChartifyWebhook.csproj --source https://api.nuget.org/v3/index.json
COPY *.cs ./
RUN dotnet publish ChartifyWebhook.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./
USER root
RUN mkdir /data && chown $APP_UID:$APP_UID /data
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Webhook__DatabasePath=/data/events.json \
    Bybit__Enabled=false \
    Bybit__AllowTestOrders=false
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "ChartifyWebhook.dll"]
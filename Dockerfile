# ── NitroGateway 后端 ──
# 构建参数：默认构建 Webapi；中心 Ingest 由 compose 传 APP_PROJECT 切换发布目标（ADR-025 D1 独立容器）
ARG APP_PROJECT=src/NitroGateway.Webapi/NitroGateway.Webapi.csproj

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 复制所有项目文件
COPY *.slnx ./
COPY src/NitroGateway.Domain/NitroGateway.Domain.csproj        src/NitroGateway.Domain/
COPY src/NitroGateway.Shared/NitroGateway.Shared.csproj        src/NitroGateway.Shared/
COPY src/NitroGateway.Device/NitroGateway.Device.csproj        src/NitroGateway.Device/
COPY src/NitroGateway.Alarm/NitroGateway.Alarm.csproj          src/NitroGateway.Alarm/
COPY src/NitroGateway.Collection/NitroGateway.Collection.csproj src/NitroGateway.Collection/
COPY src/NitroGateway.Forwarder/NitroGateway.Forwarder.csproj  src/NitroGateway.Forwarder/
COPY src/NitroGateway.Host/NitroGateway.Host.csproj            src/NitroGateway.Host/
COPY src/NitroGateway.Ingest/NitroGateway.Ingest.csproj        src/NitroGateway.Ingest/
COPY src/NitroGateway.Persistence/NitroGateway.Persistence.csproj src/NitroGateway.Persistence/
COPY src/NitroGateway.Security/NitroGateway.Security.csproj    src/NitroGateway.Security/
COPY src/NitroGateway.Telemetry/NitroGateway.Telemetry.csproj  src/NitroGateway.Telemetry/
COPY src/NitroGateway.Webapi/NitroGateway.Webapi.csproj        src/NitroGateway.Webapi/
COPY src/NitroGateway.Protocol/Abstraction/*.csproj            src/NitroGateway.Protocol/Abstraction/
COPY src/NitroGateway.Protocol/Modbus/*.csproj                 src/NitroGateway.Protocol/Modbus/
COPY src/NitroGateway.Protocol/S7/*.csproj                     src/NitroGateway.Protocol/S7/
COPY src/NitroGateway.Protocol/NitroGateway.Protocols/*.csproj src/NitroGateway.Protocol/NitroGateway.Protocols/
COPY src/NitroGateway.Storage/Buffer/*.csproj                  src/NitroGateway.Storage/Buffer/
COPY src/NitroGateway.Storage/Configuration/*.csproj           src/NitroGateway.Storage/Configuration/
COPY src/NitroGateway.Storage/TimeSeries/*.csproj              src/NitroGateway.Storage/TimeSeries/
COPY src/NitroGateway.Transport/MQTT/*.csproj                  src/NitroGateway.Transport/MQTT/
COPY src/NitroGateway.Transport/HTTP/*.csproj                  src/NitroGateway.Transport/HTTP/

RUN dotnet restore "$APP_PROJECT"

# 复制源码并发布
COPY src/ src/
RUN dotnet publish "$APP_PROJECT" -c Release -o /app --no-restore

# ── 运行镜像 ──
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

# 创建数据目录
RUN mkdir -p /app/data /app/logs
ENV DOTNET_ENVIRONMENT=Production
# ADR-022 P1-4：容器内必须监听 0.0.0.0，否则 appsettings 的 Urls=localhost 使宿主机/前端容器无法访问 API
#（默认 Webapi 5100；ingest 服务在 docker-compose.yml 用 environment 覆盖为 5200）
ENV ASPNETCORE_URLS=http://0.0.0.0:5100
EXPOSE 5100

ENTRYPOINT ["dotnet", "NitroGateway.Webapi.dll"]

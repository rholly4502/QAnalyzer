# QAnalyzer Web — multi-stage build
# Stage 1: build frontend (Vite) -> /build/src/QAnalyzer.Web/wwwroot
FROM node:20-alpine AS web
WORKDIR /build/web
COPY web/package*.json ./
RUN npm ci
COPY web/ .
# vite outDir = ../src/QAnalyzer.Web/wwwroot (relatif ke /build/web)
RUN npm run build

# Stage 2: build backend .NET
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["src/QAnalyzer.Core/QAnalyzer.Core.csproj", "QAnalyzer.Core/"]
COPY ["src/QAnalyzer.Web/QAnalyzer.Web.csproj", "QAnalyzer.Web/"]
RUN dotnet restore "QAnalyzer.Web/QAnalyzer.Web.csproj"
COPY src/QAnalyzer.Core/ QAnalyzer.Core/
COPY src/QAnalyzer.Web/ QAnalyzer.Web/
# bersihkan wwwroot lama lalu salin hasil web build (sebelum publish)
RUN rm -rf /src/QAnalyzer.Web/wwwroot
COPY --from=web /build/src/QAnalyzer.Web/wwwroot /src/QAnalyzer.Web/wwwroot
WORKDIR /src/QAnalyzer.Web
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "QAnalyzer.Web.dll"]

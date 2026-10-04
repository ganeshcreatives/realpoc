FROM node:24-bookworm-slim AS web
WORKDIR /src
COPY package.json package-lock.json ./
COPY packages/sdk/package.json packages/sdk/package.json
COPY apps/web/package.json apps/web/package.json
RUN npm ci --ignore-scripts
COPY packages packages
COPY apps apps
COPY scripts/copy-sdk.mjs scripts/copy-sdk.mjs
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props global.json ./
COPY server server
RUN dotnet publish server/School.Api/School.Api.csproj -c Release -o /out/api
RUN dotnet publish server/School.Bff/School.Bff.csproj -c Release -o /out/bff

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out/api /app/api
COPY --from=build /out/bff /app/bff
COPY --from=web /src/server/School.Bff/wwwroot /app/bff/wwwroot
COPY deploy/start.sh /app/start.sh
RUN chmod 755 /app/start.sh
USER $APP_UID
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
ENTRYPOINT ["/bin/bash", "/app/start.sh"]

# One container: the .NET server serves the API, the SignalR hub and the built web front end.
#   docker build -t matchlens .
#   docker run -p 8080:8080 -e MATCHLENS_DECISION_ENDPOINT=... -e MATCHLENS_DECISION_KEY=... \
#     -e MATCHLENS_DECISION_DEPLOYMENT=Decision-1 -e MATCHLENS_CHAT_DEPLOYMENT=gpt-5.4-mini matchlens

FROM node:22-alpine AS web
WORKDIR /src/web
COPY web/package.json web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY web/ ./
RUN npx vite build --outDir /out/wwwroot --emptyOutDir

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS api
WORKDIR /src
COPY Directory.Build.props ./
COPY src/ src/
RUN dotnet publish src/MatchLens.Server/MatchLens.Server.csproj -c Release -o /out/app
COPY --from=web /out/wwwroot /out/app/wwwroot

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=api /out/app ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "MatchLens.Server.dll"]

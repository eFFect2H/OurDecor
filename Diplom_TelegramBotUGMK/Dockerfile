# ---------- BUILD ----------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY . .
RUN dotnet publish -c Release -o /app/publish

# ---------- RUNTIME ----------
FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app

COPY --from=build /app/publish .

# директории, которые будут монтироваться
RUN mkdir -p /app/config /app/token /app/data /app/data/logs

ENV DOTNET_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "Telegrame_Test.dll"]

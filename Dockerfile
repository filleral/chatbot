# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# restore (capa cacheable)
COPY src/WhatsappBot.Functions/WhatsappBot.Functions.csproj src/WhatsappBot.Functions/
RUN dotnet restore src/WhatsappBot.Functions/WhatsappBot.Functions.csproj

# build + publish
COPY src/ src/
RUN dotnet publish src/WhatsappBot.Functions/WhatsappBot.Functions.csproj -c Release -o /app --no-restore

# ---------- runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app ./

# Render sobrescribe PORT en tiempo de ejecución; 8080 es el valor por defecto local.
ENV PORT=8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "WhatsappBot.dll"]

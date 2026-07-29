# ---- build stage ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/AegisErp.Web/AegisErp.Web.csproj -c Release -o /app/publish /p:UseAppHost=false

# ---- runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .
RUN mkdir -p /data
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    Database__Provider=Sqlite \
    ConnectionStrings__Sqlite="Data Source=/data/aegis_erp.db" \
    DOTNET_hostBuilder__reloadConfigOnChange=false
EXPOSE 8080
ENTRYPOINT ["dotnet", "AegisErp.Web.dll"]

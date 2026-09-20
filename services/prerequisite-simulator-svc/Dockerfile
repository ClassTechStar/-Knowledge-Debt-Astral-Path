# prerequisite-simulator-svc —— 先修补全模拟器（§19.8）
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY . .
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "prerequisite-simulator-svc.dll"]

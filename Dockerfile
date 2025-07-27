#See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build
WORKDIR /src
COPY ["ArashiDNS.Nous.csproj", "."]
RUN dotnet restore "./ArashiDNS.Nous.csproj"
COPY . .
WORKDIR "/src/."
RUN dotnet build "ArashiDNS.Nous.csproj" -c Release -o /app/build /p:UseAppHost=true /p:PublishAot=false /p:PublishSingleFile=false

FROM build AS publish
RUN dotnet publish "ArashiDNS.Nous.csproj" -c Release -o /app/publish /p:UseAppHost=true /p:PublishAot=false /p:PublishSingleFile=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENV ARASHI_ANY=1
ENV ARASHI_RUNNING_IN_CONTAINER=1
EXPOSE 16883
ENTRYPOINT ["dotnet","ArashiDNS.Nous.dll"]

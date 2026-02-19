FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY fileshare-scoob-dog.sln ./
COPY Fileshare/Fileshare.csproj Fileshare/
RUN dotnet restore fileshare-scoob-dog.sln

COPY . .
RUN dotnet publish Fileshare/Fileshare.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app/publish ./
ENTRYPOINT ["dotnet", "Fileshare.dll"]

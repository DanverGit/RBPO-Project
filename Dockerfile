FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/CinemaBooking.Api/CinemaBooking.Api.csproj src/CinemaBooking.Api/
RUN dotnet restore src/CinemaBooking.Api/CinemaBooking.Api.csproj
COPY src/ src/
RUN dotnet publish src/CinemaBooking.Api/CinemaBooking.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
ENTRYPOINT ["dotnet", "CinemaBooking.Api.dll"]

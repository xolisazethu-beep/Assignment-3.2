# ── Build stage ──────────────────────────────────────────────────────────────
# Restore + publish on the full SDK image, then copy only the published output
# into a small runtime image so the final container ships no compilers/sources.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first (cached) using just the csproj, so code-only changes don't
# re-download every NuGet package.
COPY CareerHub.Api.csproj ./
RUN dotnet restore CareerHub.Api.csproj

# Copy the rest of the source and publish a Release build. The test project is
# excluded by .dockerignore so it never enters the image.
COPY . .
RUN dotnet publish CareerHub.Api.csproj -c Release -o /app/publish --no-restore

# ── Runtime stage ────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./

# Listen on 8080 inside the container (the non-root default for aspnet images).
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "CareerHub.Api.dll"]

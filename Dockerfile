# Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/FrigateLabeler/FrigateLabeler.csproj src/FrigateLabeler/
RUN dotnet restore src/FrigateLabeler/FrigateLabeler.csproj
COPY src/ src/
RUN dotnet publish src/FrigateLabeler/FrigateLabeler.csproj -c Release -o /app --no-restore

# Run
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

# Config (sign-in token, API key, camera notes) and results live on volumes, never in the image.
ENV FRIGATE_LABELER_CONFIG=/config \
    FRIGATE_LABELER_DATA=/data \
    FRIGATE_LABELER_HOST=0.0.0.0 \
    FRIGATE_LABELER_PORT=5178
RUN mkdir -p /config /data && chown app:app /config /data
VOLUME ["/config", "/data"]
EXPOSE 5178

USER app
HEALTHCHECK --interval=60s --timeout=5s CMD ["dotnet", "frigate-labeler.dll", "healthcheck"]
ENTRYPOINT ["dotnet", "frigate-labeler.dll"]
CMD ["serve"]
